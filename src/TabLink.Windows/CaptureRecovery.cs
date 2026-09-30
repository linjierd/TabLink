using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TabLink.Windows;

internal sealed class CaptureUnavailableException(string message) : IOException(message);

internal sealed record CaptureRecoveryOptions(TimeSpan PollInterval, TimeSpan HeartbeatInterval,
    TimeSpan AvailableRecoveryLimit, TimeSpan InitialBackoff, TimeSpan MaximumBackoff)
{
    internal static CaptureRecoveryOptions Default { get; } = new(TimeSpan.FromMilliseconds(150),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
}

// The probe and identity validator are local host capabilities. No wire message
// or client acknowledgement can claim that a secure desktop is active.
internal static class CaptureRecovery
{
    internal static async IAsyncEnumerable<VideoPacket> StreamAsync(
        Func<CancellationToken,IAsyncEnumerable<VideoPacket>> createSource,
        Action validateDisplay, Func<InputDesktopStatus> queryDesktop,
        [EnumeratorCancellation] CancellationToken ct, CaptureRecoveryOptions? options = null)
    {
        var policy = options ?? CaptureRecoveryOptions.Default;
        IAsyncEnumerator<VideoPacket>? reader = null;
        CancellationTokenSource? attempt = null;
        Task<MoveResult>? pending = null;
        bool paused = false;
        long? availableRecoveryStarted = null;
        long nextAttempt = 0, lastHeartbeat = long.MinValue / 2, lastSourceProgress = Environment.TickCount64;
        long lastIdentityCheck = long.MinValue / 2;
        double backoff = policy.InitialBackoff.TotalMilliseconds;

        async Task StopAttempt()
        {
            attempt?.Cancel();
            if (pending is not null)
                await pending.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (reader is not null)
                await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            reader = null; pending = null;
            attempt?.Dispose(); attempt = null;
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var desktop = queryDesktop();
                var now = Environment.TickCount64;
                if (!desktop.IsAvailable)
                {
                    // Do not enumerate protected display topology, open another
                    // desktop or attempt another capture while unavailable.
                    await StopAttempt().ConfigureAwait(false);
                    paused = true;
                    if (desktop.IsUnavailable) availableRecoveryStarted = null;
                    else availableRecoveryStarted ??= now;
                    CheckDeadline(availableRecoveryStarted, now, policy);
                    yield return PausePacket(true, "Windows 桌面暂不可采集，等待返回普通桌面。");
                    lastHeartbeat = now;
                    await Task.Delay(policy.HeartbeatInterval, ct).ConfigureAwait(false);
                    continue;
                }

                // A real identity/bounds change is fatal once the normal desktop
                // is available. It must never become an infinite retry/fallback.
                if(reader is null||now-lastIdentityCheck>=250)
                {
                    CaptureUnavailableException? changed=null;
                    try { validateDisplay(); }
                    catch(CaptureUnavailableException ex) { changed=ex; }
                    catch(DisplayLayoutChangingException ex) { changed=new CaptureUnavailableException(ex.Message); }
                    catch (IOException) when (!queryDesktop().IsAvailable) { continue; }
                    lastIdentityCheck=now;
                    if(changed is not null)
                    {
                        await StopAttempt().ConfigureAwait(false);
                        paused=true;availableRecoveryStarted??=now;
                        CheckDeadline(availableRecoveryStarted,now,policy);
                        yield return PausePacket(true,changed.Message);
                        lastHeartbeat=now;nextAttempt=now+(long)policy.InitialBackoff.TotalMilliseconds;
                        await Task.Delay(policy.PollInterval,ct).ConfigureAwait(false);
                        continue;
                    }
                }

                if (paused) availableRecoveryStarted ??= now;
                CheckDeadline(availableRecoveryStarted, now, policy);
                if (paused && now - lastHeartbeat >= policy.HeartbeatInterval.TotalMilliseconds)
                {
                    yield return PausePacket(true, "正在恢复同一虚拟副屏画面。");
                    lastHeartbeat = now;
                }
                if (now < nextAttempt)
                {
                    await Task.Delay(policy.PollInterval, ct).ConfigureAwait(false);
                    continue;
                }
                if (reader is null)
                {
                    attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reader = createSource(attempt.Token).GetAsyncEnumerator(attempt.Token);
                    lastSourceProgress = now;
                }
                pending ??= MoveNextAsync(reader);
                await Task.WhenAny(pending, Task.Delay(policy.PollInterval, ct)).ConfigureAwait(false);
                if (!pending.IsCompleted)
                {
                    if (Environment.TickCount64 - lastSourceProgress > policy.AvailableRecoveryLimit.TotalMilliseconds)
                        throw new IOException("普通桌面上的画面采集持续无响应，已停止恢复。");
                    continue;
                }
                var moved = await pending.ConfigureAwait(false);
                pending = null;
                ct.ThrowIfCancellationRequested();
                if (moved.Error is not null || !moved.HasPacket)
                {
                    var localDesktop = queryDesktop();
                    if (moved.Error is not CaptureUnavailableException && moved.Error is not DisplayLayoutChangingException && localDesktop.IsAvailable)
                        throw moved.Error ?? new IOException("画面编码流意外结束。");
                    await StopAttempt().ConfigureAwait(false);
                    paused = true;
                    availableRecoveryStarted ??= Environment.TickCount64;
                    yield return PausePacket(true, "画面采集暂时不可用，正在等待恢复。");
                    lastHeartbeat = Environment.TickCount64;
                    nextAttempt = lastHeartbeat + (long)backoff;
                    backoff = Math.Min(backoff * 2, policy.MaximumBackoff.TotalMilliseconds);
                    continue;
                }
                // Recheck immediately before forwarding pixels. A transition
                // after MoveNext completed must discard that queued frame.
                if (!queryDesktop().IsAvailable) continue;
                var packet = reader.Current;
                if (packet.IsFrame)
                {
                    lastSourceProgress = Environment.TickCount64;
                    if (paused) yield return PausePacket(false, "USB 副屏画面已恢复。");
                    paused = false; availableRecoveryStarted = null;
                    backoff = policy.InitialBackoff.TotalMilliseconds;
                }
                yield return packet;
            }
        }
        finally { await StopAttempt().ConfigureAwait(false); }
    }

    static void CheckDeadline(long? since, long now, CaptureRecoveryOptions options)
    {
        if (since.HasValue && now - since.Value > options.AvailableRecoveryLimit.TotalMilliseconds)
            throw new IOException("普通桌面恢复画面超时，请重新连接平板。");
    }

    internal static VideoPacket PausePacket(bool paused, string message) =>
        new(0x02, JsonSerializer.SerializeToUtf8Bytes(new { capturePaused = paused, message }), false, paused);

    static async Task<MoveResult> MoveNextAsync(IAsyncEnumerator<VideoPacket> reader)
    {
        try { return new(await reader.MoveNextAsync().ConfigureAwait(false), null); }
        catch (Exception ex) { return new(false, ex); }
    }
    readonly record struct MoveResult(bool HasPacket, Exception? Error);
}
