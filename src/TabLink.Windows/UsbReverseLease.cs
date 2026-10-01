using System.Diagnostics;
using System.Security.Principal;
using TabLink.Core;

namespace TabLink.Windows;

// A receipt, never an instruction to reserve or replace a port. It is first
// persisted as a non-removal intent before the bind and becomes cleanup
// authority only after the exact --no-rebind operation succeeds.
internal sealed record UsbReverseLease(Guid Id, string Serial, string Vid, string Pid,
    AdbReverseEndpoint Endpoint, int OwnerPid, long OwnerStartUtcTicks, string OwnerUserSid,
    int SchemaVersion)
{
    internal const int CurrentSchemaVersion = 3;

    internal static UsbReverseLease Created(ApprovedUsbDevice approved, AdbReverseEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (!endpoint.IsValid) throw new ArgumentException("USB 通道端点无效。", nameof(endpoint));
        using var owner = Process.GetCurrentProcess();
        return new(Guid.NewGuid(), approved.Serial, approved.UsbIdentity.Vid, approved.UsbIdentity.Pid,
            endpoint, owner.Id, owner.StartTime.ToUniversalTime().Ticks, CurrentUserSid(),
            CurrentSchemaVersion);
    }

    internal static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");

    /// <summary>
    /// Processes a durable queue entry. The owner check may be bypassed only
    /// for a receipt that this same process has explicitly retired in memory
    /// after its display session stopped; disk data alone never grants that
    /// exception.
    /// </summary>
    internal static async Task<UsbReverseCleanupResult> CleanupPendingAsync(
        UsbReverseLease lease, bool ownerExplicitlyRetired, bool removalAuthorized,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!removalAuthorized)
                return new(UsbReverseCleanupStatus.PreparedAbandoned,
                    "绑定准备记录没有获得删除权限，已永久封存且未执行 ADB 删除。");
            if (!string.Equals(lease.OwnerUserSid, CurrentUserSid(), StringComparison.Ordinal))
                return new(UsbReverseCleanupStatus.OwnerUserMismatch,
                    "USB 通道属于另一个 Windows 用户，当前会话不处理它。");
            if (!ownerExplicitlyRetired && OwnerIsRunning(lease.OwnerPid, lease.OwnerStartUtcTicks))
                return new(UsbReverseCleanupStatus.OwnerRunning, "原连接进程仍在运行，保留 USB 通道。");
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TabLink", "settings.json");
            var settings = new SettingsStore(settingsPath).Load();
            var adbPath = TrustedBundledAdb.StageAndGetVerifiedPath();
            if (adbPath is null)
                return new(UsbReverseCleanupStatus.Failed,
                    "完整交付目录没有固定校验的随包 ADB，已保留原 USB 通道记录。");
            return await CleanupOwnedAsync(lease, settings, adbPath, UsbInventory.ReadAsync,
                new AdbProcessRunner(), static (_, _) => false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or System.Security.SecurityException)
        { return new(UsbReverseCleanupStatus.Failed, ex.Message); }
    }

    // Dependency injection keeps all tests away from the user's tablet and ADB.
    internal static async Task<UsbReverseCleanupResult> CleanupOwnedAsync(UsbReverseLease lease,
        DevicePolicySettings settings, string adbPath,
        Func<CancellationToken, Task<IReadOnlyList<UsbDeviceIdentity>>> inventory,
        IAdbProcessRunner runner, Func<int, long, bool> ownerIsRunning,
        CancellationToken cancellationToken = default)
    {
        if (lease.SchemaVersion != CurrentSchemaVersion || lease.Id == Guid.Empty ||
            lease.OwnerPid <= 0 || lease.OwnerStartUtcTicks <= 0 ||
            !string.Equals(lease.OwnerUserSid, CurrentUserSid(), StringComparison.Ordinal))
            return new(UsbReverseCleanupStatus.Failed, "USB 通道所有权记录无效。");
        if (!lease.Endpoint.IsValid)
            return new(UsbReverseCleanupStatus.Failed, "USB 通道端点记录无效。");
        if (ownerIsRunning(lease.OwnerPid, lease.OwnerStartUtcTicks))
            return new(UsbReverseCleanupStatus.OwnerRunning, "原连接进程仍在运行，保留 USB 通道。");
        try
        {
            var adb = new AdbClient(adbPath, new DevicePolicy(settings), inventory, runner);
            var devices = await adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            var matches = devices.Where(x => string.Equals(x.Serial, lease.Serial, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                return new(UsbReverseCleanupStatus.DeviceUnavailable, "原平板未唯一出现，暂不清理 USB 通道。");
            // The saved receipt authorizes cleanup of this same selected device,
            // but it does not bypass its current state or the current exclusions.
            var approved = await adb.ApproveAsync(matches[0], cancellationToken).ConfigureAwait(false);
            if (!string.Equals(lease.Vid, approved.UsbIdentity.Vid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(lease.Pid, approved.UsbIdentity.Pid, StringComparison.OrdinalIgnoreCase))
                return new(UsbReverseCleanupStatus.IdentityChanged, "USB 硬件身份变化，保留原通道。");

            var mapping = await adb.InspectReversePortAsync(approved, lease.Endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (mapping.Status == AdbReversePortStatus.Missing)
                return new(UsbReverseCleanupStatus.AlreadyAbsent, "原 USB 通道已经移除。");
            if (mapping.Status != AdbReversePortStatus.Existing)
                return new(UsbReverseCleanupStatus.MappingChanged, "端口已指向其他映射，未修改它。");
            // PID reuse and owner-state changes remain fail-closed. Re-read the
            // exact mapping immediately before the mutating command as a
            // best-effort defence against another ADB client replacing it
            // after the first inspection. ADB does not expose an atomic
            // compare-and-delete primitive, so callers must still hold the
            // TabLink machine mutation gate and treat external-client ABA as a
            // documented platform boundary.
            if (ownerIsRunning(lease.OwnerPid, lease.OwnerStartUtcTicks))
                return new(UsbReverseCleanupStatus.OwnerRunning, "原连接进程仍在运行，保留 USB 通道。");
            var finalMapping = await adb.InspectReversePortAsync(approved, lease.Endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (finalMapping.Status == AdbReversePortStatus.Missing)
                return new(UsbReverseCleanupStatus.AlreadyAbsent, "原 USB 通道已经移除。");
            if (finalMapping.Status != AdbReversePortStatus.Existing)
                return new(UsbReverseCleanupStatus.MappingChanged, "删除前端口已经变化，未修改它。");
            if (ownerIsRunning(lease.OwnerPid, lease.OwnerStartUtcTicks))
                return new(UsbReverseCleanupStatus.OwnerRunning, "原连接进程仍在运行，保留 USB 通道。");
            await adb.RemoveReverseAsync(approved, lease.Endpoint, cancellationToken).ConfigureAwait(false);
            return new(UsbReverseCleanupStatus.Removed, "已清理本次连接创建的 USB 通道。");
        }
        catch (OperationCanceledException) { throw; }
        catch (DevicePolicyException ex) { return new(UsbReverseCleanupStatus.PolicyBlocked, ex.Message); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or TimeoutException)
        { return new(UsbReverseCleanupStatus.Failed, ex.Message); }
    }

    private static bool OwnerIsRunning(int pid, long startTicks)
    {
        try
        {
            using var owner = Process.GetProcessById(pid);
            return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}

internal enum UsbReverseCleanupStatus
{
    Removed, AlreadyAbsent, PreparedAbandoned, OwnerRunning, DeviceUnavailable, IdentityChanged, PolicyBlocked,
    OwnerUserMismatch, MappingChanged, Failed
}

internal sealed record UsbReverseCleanupResult(UsbReverseCleanupStatus Status, string Message)
{
    internal bool Complete => Status is UsbReverseCleanupStatus.Removed or UsbReverseCleanupStatus.AlreadyAbsent
        or UsbReverseCleanupStatus.PreparedAbandoned or UsbReverseCleanupStatus.MappingChanged;
}
