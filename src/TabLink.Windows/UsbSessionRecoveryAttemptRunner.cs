using TabLink.Core;

namespace TabLink.Windows;

internal enum UsbRecoveryExecutionStatus
{
    Connected,
    RetryableFailure,
    TerminalFailure,
    Cancelled
}

internal sealed record UsbRecoveryExecution(
    UsbRecoveryExecutionStatus Status,
    AdbReversePortStatus? RouteStatus,
    bool ClientLaunchCompleted,
    string Detail);

/// <summary>
/// Executes one bounded repair attempt against the original approved USB
/// device and original FrameServer token. Display allocation and lifetime stay
/// with MainForm; callbacks only rotate the exact reverse-ownership receipt.
/// </summary>
internal sealed class UsbSessionRecoveryAttemptRunner
{
    static readonly TimeSpan NativeReconnectWindow = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan LaunchReconnectWindow = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    readonly AdbClient adb;
    readonly ApprovedAndroidUser targetUser;
    readonly ApprovedUsbDevice target;
    readonly AdbReverseEndpoint endpoint;
    readonly string token;
    readonly Func<bool> isCurrent;
    readonly Func<bool> isConnected;
    readonly Func<bool> hasOwnedMapping;
    readonly Action retireOwnedMapping;
    readonly Action prepareCreatedMapping;
    readonly Action publishCreatedMapping;
    readonly Action abandonPreparedMapping;
    readonly Func<TimeSpan, CancellationToken, Task> delay;

    internal UsbSessionRecoveryAttemptRunner(
        AdbClient adb,
        ApprovedAndroidUser targetUser,
        AdbReverseEndpoint endpoint,
        string token,
        Func<bool> isCurrent,
        Func<bool> isConnected,
        Func<bool> hasOwnedMapping,
        Action retireOwnedMapping,
        Action publishCreatedMapping,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this(adb, targetUser, endpoint, token, isCurrent, isConnected, hasOwnedMapping,
            retireOwnedMapping, static () => { }, publishCreatedMapping, static () => { }, delay)
    { }

    internal UsbSessionRecoveryAttemptRunner(
        AdbClient adb,
        ApprovedAndroidUser targetUser,
        AdbReverseEndpoint endpoint,
        string token,
        Func<bool> isCurrent,
        Func<bool> isConnected,
        Func<bool> hasOwnedMapping,
        Action retireOwnedMapping,
        Action prepareCreatedMapping,
        Action publishCreatedMapping,
        Action abandonPreparedMapping,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.adb = adb ?? throw new ArgumentNullException(nameof(adb));
        this.targetUser = targetUser ?? throw new ArgumentNullException(nameof(targetUser));
        target = targetUser.Device;
        if (!endpoint.IsValid) throw new ArgumentException("The USB endpoint is invalid.", nameof(endpoint));
        this.endpoint = endpoint;
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A session token is required.", nameof(token));
        this.token = token;
        this.isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        this.isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        this.hasOwnedMapping = hasOwnedMapping ?? throw new ArgumentNullException(nameof(hasOwnedMapping));
        this.retireOwnedMapping = retireOwnedMapping ?? throw new ArgumentNullException(nameof(retireOwnedMapping));
        this.prepareCreatedMapping = prepareCreatedMapping ?? throw new ArgumentNullException(nameof(prepareCreatedMapping));
        this.publishCreatedMapping = publishCreatedMapping ?? throw new ArgumentNullException(nameof(publishCreatedMapping));
        this.abandonPreparedMapping = abandonPreparedMapping ?? throw new ArgumentNullException(nameof(abandonPreparedMapping));
        this.delay = delay ?? Task.Delay;
    }

    internal async Task<UsbRecoveryExecution> RunAsync(
        TimeSpan budget,
        Func<bool> tryReserveClientLaunch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tryReserveClientLaunch);
        if (budget <= TimeSpan.Zero)
            return Retry("恢复预算已经用完。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        var ct = deadline.Token;
        AdbReversePortStatus? route = null;
        var launched = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return Cancelled("连接代次已经变化。");
            if (isConnected()) return Connected(null, false, "平板已经重新连接。");

            // A recovery episode belongs to the Android user that was bound
            // when this display session started. Check it before inspecting or
            // mutating the reverse route; malformed user output must not retire
            // an otherwise valid ownership receipt.
            try { await adb.ValidateCurrentAndroidUserAsync(targetUser, ct); }
            catch (AdbResponseException ex)
            { return Terminal(null, false, SafeErrorSummary.ForUser(ex, adbOperation: true)); }

            var inspection = await adb.InspectReversePortAsync(target, endpoint, ct);
            route = inspection.Status;
            if (!isCurrent()) return Cancelled("检查完成时连接代次已经变化。");

            if (inspection.Status == AdbReversePortStatus.Conflicting)
            {
                try { retireOwnedMapping(); }
                catch (Exception ex) when (IsOwnershipPersistenceFailure(ex))
                {
                    return Terminal(route, false,
                        "本会话 USB 端点已指向其他映射；已撤销直接删除权限，但无法持久注销旧记录："+SafeErrorSummary.ForUser(ex));
                }
                return Terminal(route, false, "本会话 USB 端点已指向其他映射，未替换或删除它。");
            }
            if (inspection.Status == AdbReversePortStatus.Existing && !hasOwnedMapping())
            {
                try { retireOwnedMapping(); }
                catch (Exception ex) when (IsOwnershipPersistenceFailure(ex))
                {
                    return Terminal(route, false,
                        "发现未获删除权限的 USB 映射；未修改它，且无法封存准备记录："+SafeErrorSummary.ForUser(ex));
                }
                return Terminal(route, false,
                    "发现本会话端点映射，但准备记录未获得删除权限；已封存记录，未修改映射。");
            }
            if (inspection.Status == AdbReversePortStatus.Missing)
            {
                // The old receipt cannot authorize cleanup of a future mapping.
                retireOwnedMapping();
                ct.ThrowIfCancellationRequested();
                if (!isCurrent()) return Cancelled("重建前连接代次已经变化。");
                prepareCreatedMapping();
                try { await adb.ReversePortAsync(target, endpoint, ct); }
                catch (AdbCommandException)
                {
                    abandonPreparedMapping();
                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    abandonPreparedMapping();
                    throw;
                }
                route = AdbReversePortStatus.Created;
                // Publish immediately after a definite --no-rebind success. If
                // Stop raced us, it waits this attempt and then removes exactly
                // the mapping represented by this new receipt.
                try { publishCreatedMapping(); }
                catch (Exception ex) when (IsOwnershipPersistenceFailure(ex))
                {
                    return Terminal(route, false,
                        "USB 映射已创建，但无法发布新的所有权记录；将停止会话并执行精确清理："+SafeErrorSummary.ForUser(ex));
                }
            }

            if (await WaitForConnectionAsync(NativeReconnectWindow, ct))
                return Connected(route, false, "USB 通道恢复后，平板已自动重连。");
            if (!isCurrent()) return Cancelled("等待重连时连接代次已经变化。");

            if (tryReserveClientLaunch())
            {
                try { await adb.LaunchAsync(targetUser, token, endpoint, ct); }
                catch (AdbResponseException ex)
                {
                    // The reverse route is still exactly owned by this session.
                    // A protected-provider response failure or malformed user
                    // response must stop the session, while preserving that
                    // ownership so Stop can remove the exact mapping.
                    return Terminal(route, false,
                        "Android 客户端未接受受保护的 USB 会话配置；将停止会话并精确回收通道："
                        + SafeErrorSummary.ForUser(ex, adbOperation: true));
                }
                launched = true;
                if (await WaitForConnectionAsync(LaunchReconnectWindow, ct))
                    return Connected(route, true, "已用原会话令牌启动客户端并恢复连接。");
            }
            return Retry("USB 通道已检查，但平板尚未重新完成认证连接。", route, launched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("USB 恢复已随当前会话取消。", route, launched);
        }
        catch (OperationCanceledException)
        {
            return Retry("本次 USB 恢复达到时间预算。", route, launched);
        }
        catch (AdbDeviceTemporarilyUnavailableException ex)
        {
            return Retry(SafeErrorSummary.ForUser(ex), route, launched);
        }
        catch (Exception ex) when (ex is InvalidDataException or AdbResponseException)
        {
            // Unknown list output means the old ownership proof is no longer
            // sufficient to authorize crash cleanup of whatever occupies the
            // per-session device endpoint.
            try { retireOwnedMapping(); }
            catch (Exception retireError) when (IsOwnershipPersistenceFailure(retireError))
            { return Terminal(route, launched, SafeErrorSummary.ForUser(ex,adbOperation:true) + "；注销旧 USB 所有权记录也失败：" + SafeErrorSummary.ForUser(retireError)); }
            return Terminal(route, launched, SafeErrorSummary.ForUser(ex,adbOperation:true));
        }
        catch (DevicePolicyException ex)
        {
            return Terminal(route, launched, SafeErrorSummary.ForUser(ex,adbOperation:true));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return Retry(SafeErrorSummary.ForUser(ex,adbOperation:true), route, launched);
        }
        catch (ArgumentException ex)
        {
            return Terminal(route, launched, SafeErrorSummary.ForUser(ex,adbOperation:true));
        }
    }

    async Task<bool> WaitForConnectionAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        if (isConnected()) return true;
        var remaining = window;
        while (remaining > TimeSpan.Zero)
        {
            var pause = remaining < PollInterval ? remaining : PollInterval;
            await delay(pause, cancellationToken);
            if (isConnected()) return true;
            if (!isCurrent()) return false;
            remaining -= pause;
        }
        return isConnected();
    }

    static UsbRecoveryExecution Connected(AdbReversePortStatus? route, bool launched, string detail) =>
        new(UsbRecoveryExecutionStatus.Connected, route, launched, detail);
    static UsbRecoveryExecution Retry(string detail, AdbReversePortStatus? route = null, bool launched = false) =>
        new(UsbRecoveryExecutionStatus.RetryableFailure, route, launched, detail);
    static UsbRecoveryExecution Terminal(AdbReversePortStatus? route, bool launched, string detail) =>
        new(UsbRecoveryExecutionStatus.TerminalFailure, route, launched, detail);
    static UsbRecoveryExecution Cancelled(string detail, AdbReversePortStatus? route = null, bool launched = false) =>
        new(UsbRecoveryExecutionStatus.Cancelled, route, launched, detail);
    static bool IsOwnershipPersistenceFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or TimeoutException or System.Text.Json.JsonException;
}
