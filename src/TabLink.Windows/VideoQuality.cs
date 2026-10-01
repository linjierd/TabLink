using System.Text.Json;
using System.Text.Json.Serialization;
using TabLink.Core;

namespace TabLink.Windows;

internal enum VideoQualityPreset
{
    Automatic,
    LowLatency,
    Balanced,
    HighQuality
}

internal enum VideoTransportKind
{
    Unknown,
    Usb,
    WiFi,
    Ethernet
}

internal sealed record VideoEncodingPlan(
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    int BufferKbits,
    int GopFrames,
    long Generation,
    string Reason);

internal sealed record VideoQualityObservation(
    Guid ConnectionId,
    DateTime ObservedUtc,
    bool CapturePaused,
    long FramesSent,
    long PayloadBytesWritten,
    long PacketWriteCount,
    double PacketWriteTotalMs,
    long PacketWriteSlowCount,
    bool ReceiverFeedbackFresh,
    long FeedbackSequence,
    long ReceivedVideoFrames,
    long SubmittedFrames,
    long PresentedFrames,
    long DecoderEpoch,
    int QueueDepth,
    int QueueCapacity,
    int QueueHighWaterMark,
    long InputDroppedFrames,
    long BackpressureTimeouts,
    long OverflowDrops,
    long ExpiredDrops,
    long AwaitingKeyFrameDrops,
    long RenderDrops,
    bool AwaitingKeyFrame);

internal sealed record VideoQualitySnapshot(
    VideoQualityPreset Preset,
    VideoEncodingPlan Plan,
    bool RuntimeChangesEnabled,
    int AdaptiveLevel,
    int BadWindows,
    int StableWindows,
    double LastSentFps,
    double LastMegabitsPerSecond,
    string State,
    DateTime? LastChangedUtc);

/// <summary>Produces one strictly increasing media timeline across encoder restarts.</summary>
internal sealed class MediaTimestampClock
{
    double next;
    long last = -1;

    internal long Next(int fps)
    {
        if (fps is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(fps));
        var value = Math.Max(last + 1, checked((long)Math.Round(next, MidpointRounding.AwayFromZero)));
        last = value;
        next += 1_000_000d / fps;
        return value;
    }
}

/// <summary>
/// Connection-local, conservative quality policy. It never changes the virtual
/// display mode: only the owned FFmpeg encoder process may be restarted.
/// </summary>
internal sealed class AdaptiveVideoSession
{
    const int MaximumAdaptiveLevel = 3;
    static readonly TimeSpan ChangeCooldown = TimeSpan.FromSeconds(10);
    readonly object gate = new();
    readonly int width;
    readonly int height;
    readonly int requestedFps;
    readonly VideoTransportKind transport;
    VideoQualityPreset preset;
    VideoEncodingPlan plan;
    long nextGeneration = 1;
    int adaptiveLevel;
    int badWindows;
    int stableWindows;
    bool runtimeChangesEnabled;
    bool connected;
    string state;
    DateTime? lastChangedUtc;
    VideoQualityObservation? previous;
    double lastSentFps;
    double lastMbps;

    internal AdaptiveVideoSession(TabletDisplayProfile profile, VideoQualityPreset preset,
        VideoTransportKind transport)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        width = profile.Width;
        height = profile.Height;
        requestedFps = Math.Clamp(profile.RequestedRefreshRate, 1, 144);
        this.transport = transport;
        this.preset = preset;
        adaptiveLevel = InitialAdaptiveLevel(transport);
        state = "等待客户端能力协商";
        plan = BuildPlan(preset, adaptiveLevel, nextGeneration, "连接初始画质");
    }

    internal VideoEncodingPlan CurrentPlan { get { lock (gate) return plan; } }

    internal VideoQualitySnapshot Snapshot()
    {
        lock (gate)
            return new(preset, plan, runtimeChangesEnabled, adaptiveLevel, badWindows, stableWindows,
                lastSentFps, lastMbps, state, lastChangedUtc);
    }

    internal void BeginConnection(bool allowRuntimeChanges)
    {
        lock (gate)
        {
            connected = true;
            runtimeChangesEnabled = allowRuntimeChanges;
            previous = null;
            badWindows = stableWindows = 0;
            lastSentFps = lastMbps = 0;
            adaptiveLevel = InitialAdaptiveLevel(transport);
            var reason = allowRuntimeChanges
                ? "接收端反馈已协商，等待稳定采样"
                : "客户端未协商自适应反馈，本次保持固定画质";
            ReplacePlanUnsafe(BuildPlan(preset, adaptiveLevel, nextGeneration, reason), forceGeneration: false);
            state = reason;
        }
    }

    internal void EndConnection()
    {
        lock (gate)
        {
            connected = runtimeChangesEnabled = false;
            previous = null;
            badWindows = stableWindows = 0;
            state = "连接已结束";
        }
    }

    /// <returns>true when the active encoder plan changed immediately.</returns>
    internal bool SetPreset(VideoQualityPreset value, DateTime nowUtc)
    {
        lock (gate)
        {
            if (preset == value) return false;
            preset = value;
            adaptiveLevel = InitialAdaptiveLevel(transport);
            badWindows = stableWindows = 0;
            previous = null;
            if (connected && !runtimeChangesEnabled)
            {
                state = "当前客户端不支持同会话画质切换；新设置将在下次连接生效";
                return false;
            }
            var next = BuildPlan(preset, adaptiveLevel, nextGeneration + 1,
                "用户切换到" + DisplayName(preset));
            var changed = HasDifferentEncoderSettings(plan, next);
            if (changed)
            {
                nextGeneration++;
                next = next with { Generation = nextGeneration };
                plan = next;
                lastChangedUtc = nowUtc;
            }
            else plan = next with { Generation = plan.Generation };
            state = next.Reason;
            return changed;
        }
    }

    internal VideoQualitySnapshot Observe(VideoQualityObservation sample)
    {
        lock (gate)
        {
            if (!connected || preset != VideoQualityPreset.Automatic || !runtimeChangesEnabled)
            {
                previous = sample;
                return SnapshotUnsafe();
            }
            if (sample.CapturePaused)
            {
                previous = null;
                badWindows = stableWindows = 0;
                state = "Windows 采集暂停，自适应判断已冻结";
                return SnapshotUnsafe();
            }
            if (!sample.ReceiverFeedbackFresh || sample.FeedbackSequence < 1)
            {
                previous = null;
                badWindows = stableWindows = 0;
                state = "等待新的接收端反馈，自适应判断已冻结";
                return SnapshotUnsafe();
            }
            if (previous is null || previous.ConnectionId != sample.ConnectionId ||
                sample.ObservedUtc <= previous.ObservedUtc || sample.FeedbackSequence <= previous.FeedbackSequence)
            {
                previous = sample;
                badWindows = stableWindows = 0;
                state = "正在建立本次连接的自适应基线";
                return SnapshotUnsafe();
            }

            var elapsed = (sample.ObservedUtc - previous.ObservedUtc).TotalSeconds;
            if (elapsed < 0.5 || sample.FramesSent < previous.FramesSent ||
                sample.PayloadBytesWritten < previous.PayloadBytesWritten ||
                sample.PacketWriteCount < previous.PacketWriteCount ||
                sample.PacketWriteTotalMs < previous.PacketWriteTotalMs ||
                sample.PacketWriteSlowCount < previous.PacketWriteSlowCount ||
                sample.ReceivedVideoFrames < previous.ReceivedVideoFrames ||
                sample.SubmittedFrames < previous.SubmittedFrames ||
                sample.PresentedFrames < previous.PresentedFrames ||
                sample.QueueHighWaterMark < previous.QueueHighWaterMark ||
                sample.InputDroppedFrames < previous.InputDroppedFrames ||
                sample.BackpressureTimeouts < previous.BackpressureTimeouts)
            {
                previous = sample;
                badWindows = stableWindows = 0;
                state = "遥测基线已重置，等待下一观察窗口";
                return SnapshotUnsafe();
            }

            var sent = sample.FramesSent - previous.FramesSent;
            var bytes = sample.PayloadBytesWritten - previous.PayloadBytesWritten;
            var received = sample.ReceivedVideoFrames - previous.ReceivedVideoFrames;
            var submitted = sample.SubmittedFrames - previous.SubmittedFrames;
            var presented = sample.PresentedFrames - previous.PresentedFrames;
            var writes = sample.PacketWriteCount - previous.PacketWriteCount;
            var slowWrites = sample.PacketWriteSlowCount - previous.PacketWriteSlowCount;
            var writeMs = sample.PacketWriteTotalMs - previous.PacketWriteTotalMs;
            var generationChanged = sample.DecoderEpoch != previous.DecoderEpoch;
            var overflow = generationChanged ? 0 : NonNegativeDelta(sample.OverflowDrops, previous.OverflowDrops);
            var expired = generationChanged ? 0 : NonNegativeDelta(sample.ExpiredDrops, previous.ExpiredDrops);
            var awaitingDrops = generationChanged ? 0 : NonNegativeDelta(sample.AwaitingKeyFrameDrops, previous.AwaitingKeyFrameDrops);
            var renderDrops = generationChanged ? 0 : NonNegativeDelta(sample.RenderDrops, previous.RenderDrops);
            var inputDrops = NonNegativeDelta(sample.InputDroppedFrames, previous.InputDroppedFrames);
            var backpressureTimeouts = NonNegativeDelta(sample.BackpressureTimeouts, previous.BackpressureTimeouts);
            previous = sample;

            lastSentFps = sent / elapsed;
            lastMbps = bytes * 8d / elapsed / 1_000_000d;
            var target = Math.Max(1, plan.Fps);
            var averageWriteMs = writes == 0 ? 0 : writeMs / writes;
            var slowWriteRatio = writes == 0 ? 0 : (double)slowWrites / writes;
            var queueCrowded = sample.QueueCapacity > 0 &&
                sample.QueueDepth >= Math.Max(2, sample.QueueCapacity - 2);
            var dependencyLoss = overflow + expired + awaitingDrops > 0 || sample.AwaitingKeyFrame;
            var receiverPressure = inputDrops > 0 || backpressureTimeouts > 0;
            var transportSlow = averageWriteMs >= Math.Max(8, 750d / target) || slowWriteRatio >= 0.15;
            var deliverySlow = sent >= Math.Max(4, target * elapsed * 0.45) &&
                (received < sent * 0.72 || submitted < received * 0.72);
            var severeRenderLoss = presented > 0 && renderDrops > Math.Max(3, presented / 10);
            var unhealthy = dependencyLoss || receiverPressure || queueCrowded || transportSlow || deliverySlow || severeRenderLoss;
            var healthy = !unhealthy && !generationChanged && sample.QueueDepth <= 1 &&
                overflow + expired + awaitingDrops + renderDrops == 0 &&
                sent >= target * elapsed * 0.88 && received >= sent * 0.90 &&
                submitted >= received * 0.90 && presented >= submitted * 0.82 &&
                averageWriteMs < Math.Max(4, 350d / target);

            if (unhealthy)
            {
                badWindows++;
                stableWindows = 0;
                state = dependencyLoss ? "接收端参考链中断，观察关键帧恢复"
                    : receiverPressure ? "接收端丢帧或背压超时，等待连续观察"
                    : queueCrowded ? "接收端队列偏高，等待连续观察"
                    : transportSlow ? "网络写入变慢，等待连续观察"
                    : "发送、接收或解码速度下降，等待连续观察";
            }
            else if (healthy)
            {
                stableWindows++;
                badWindows = 0;
                state = "链路稳定，保持当前画质";
            }
            else
            {
                badWindows = stableWindows = 0;
                state = "指标未形成连续趋势，保持当前画质";
            }

            var cooldownReady = lastChangedUtc is null || sample.ObservedUtc - lastChangedUtc >= ChangeCooldown;
            if (badWindows >= 3 && adaptiveLevel < MaximumAdaptiveLevel && cooldownReady)
            {
                adaptiveLevel++;
                ApplyAdaptiveLevelUnsafe(sample.ObservedUtc, "连续 3 个窗口出现传输或接收压力，降低一档码率");
            }
            else if (stableWindows >= 15 && adaptiveLevel > 0 && cooldownReady)
            {
                adaptiveLevel--;
                ApplyAdaptiveLevelUnsafe(sample.ObservedUtc, "连续约 30 秒稳定，尝试恢复一档码率");
            }
            return SnapshotUnsafe();
        }
    }

    void ApplyAdaptiveLevelUnsafe(DateTime nowUtc, string reason)
    {
        nextGeneration++;
        plan = BuildPlan(VideoQualityPreset.Automatic, adaptiveLevel, nextGeneration, reason);
        badWindows = stableWindows = 0;
        lastChangedUtc = nowUtc;
        state = reason;
    }

    VideoEncodingPlan BuildPlan(VideoQualityPreset value, int level, long generation, string reason)
    {
        var baseRate = Math.Clamp((int)Math.Round((double)width * height * requestedFps * 0.145 / 1000), 6_000, 40_000);
        double multiplier;
        int gop;
        switch (value)
        {
            case VideoQualityPreset.LowLatency:
                multiplier = 0.60;
                gop = Math.Max(15, requestedFps / 2);
                break;
            case VideoQualityPreset.Balanced:
                multiplier = 0.80;
                gop = requestedFps;
                break;
            case VideoQualityPreset.HighQuality:
                multiplier = 1.20;
                gop = requestedFps;
                break;
            default:
                multiplier = level switch { 0 => 1.00, 1 => 0.80, 2 => 0.60, _ => 0.40 };
                gop = requestedFps;
                break;
        }
        var bitrate = Math.Clamp((int)Math.Round(baseRate * multiplier / 250) * 250, 3_000, 48_000);
        var buffer = Math.Clamp(bitrate / 20, 500, 2_000);
        return new(width, height, requestedFps, bitrate, buffer, gop, generation, reason);
    }

    void ReplacePlanUnsafe(VideoEncodingPlan candidate, bool forceGeneration)
    {
        if (forceGeneration || HasDifferentEncoderSettings(plan, candidate))
        {
            nextGeneration++;
            plan = candidate with { Generation = nextGeneration };
        }
        else plan = candidate with { Generation = plan.Generation };
    }

    VideoQualitySnapshot SnapshotUnsafe() => new(preset, plan, runtimeChangesEnabled, adaptiveLevel,
        badWindows, stableWindows, lastSentFps, lastMbps, state, lastChangedUtc);

    static bool HasDifferentEncoderSettings(VideoEncodingPlan left, VideoEncodingPlan right) =>
        left.Width != right.Width || left.Height != right.Height || left.Fps != right.Fps ||
        left.BitrateKbps != right.BitrateKbps || left.BufferKbits != right.BufferKbits ||
        left.GopFrames != right.GopFrames;

    static long NonNegativeDelta(long current, long prior) => current >= prior ? current - prior : 0;
    static int InitialAdaptiveLevel(VideoTransportKind transport) => transport == VideoTransportKind.WiFi ? 1 : 0;

    internal static string DisplayName(VideoQualityPreset value) => value switch
    {
        VideoQualityPreset.Automatic => "自动",
        VideoQualityPreset.LowLatency => "低延迟",
        VideoQualityPreset.Balanced => "均衡",
        VideoQualityPreset.HighQuality => "高清晰",
        _ => value.ToString()
    };
}

internal sealed class VideoQualityPreferences
{
    const int CurrentSchema = 1;
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    readonly string path;

    internal VideoQualityPreferences(string path) => this.path = Path.GetFullPath(path);

    internal VideoQualityPreset Load()
    {
        try
        {
            if (!File.Exists(path)) return VideoQualityPreset.Automatic;
            var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options);
            return data is { Schema: CurrentSchema } && Enum.TryParse<VideoQualityPreset>(data.Preset, true, out var parsed) &&
                Enum.IsDefined(parsed)
                ? parsed : VideoQualityPreset.Automatic;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or JsonException)
        {
            return VideoQualityPreset.Automatic;
        }
    }

    internal void Save(VideoQualityPreset preset)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Stored(CurrentSchema, preset.ToString()), Options);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    sealed record Stored(int Schema, string Preset);
}
