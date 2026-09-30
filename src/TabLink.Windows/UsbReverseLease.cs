using System.Diagnostics;
using TabLink.Core;

namespace TabLink.Windows;

// A receipt, never an instruction to reserve or replace a port. Create this only
// after AdbClient.ReversePortAsync completed successfully for the selected USB.
internal sealed record UsbReverseLease(Guid Id, string Serial, string Vid, string Pid,
    int OwnerPid, long OwnerStartUtcTicks)
{
    internal static UsbReverseLease Created(ApprovedUsbDevice approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        using var owner = Process.GetCurrentProcess();
        return new(Guid.NewGuid(), approved.Serial, approved.UsbIdentity.Vid, approved.UsbIdentity.Pid,
            owner.Id, owner.StartTime.ToUniversalTime().Ticks);
    }

    // Caller MUST retain the session ownership lock from checking the receipt
    // through this operation. Call synchronously with GetAwaiter().GetResult()
    // inside SessionGuard's existing cross-process mutex; do not await while
    // owning a thread-affine Mutex. This prevents another TabLink session from
    // acquiring the port between the inspection and removal.
    internal static async Task<UsbReverseCleanupResult> CleanupAfterOwnerExitAsync(
        UsbReverseLease lease, CancellationToken cancellationToken = default)
    {
        try
        {
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TabLink", "settings.json");
            var settings = new SettingsStore(settingsPath).Load();
            var adbPath = AdbLocator.FindAdbPath(settings.AdbPath);
            if (adbPath is null) return new(UsbReverseCleanupStatus.Failed, "找不到 adb，保留原 USB 通道记录。");
            return await CleanupOwnedAsync(lease, settings, adbPath, UsbInventory.ReadAsync,
                new AdbProcessRunner(), OwnerIsRunning, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(UsbReverseCleanupStatus.Failed, ex.Message); }
    }

    // Dependency injection keeps all tests away from the user's tablet and ADB.
    internal static async Task<UsbReverseCleanupResult> CleanupOwnedAsync(UsbReverseLease lease,
        DevicePolicySettings settings, string adbPath,
        Func<CancellationToken, Task<IReadOnlyList<UsbDeviceIdentity>>> inventory,
        IAdbProcessRunner runner, Func<int, long, bool> ownerIsRunning,
        CancellationToken cancellationToken = default)
    {
        if (lease.Id == Guid.Empty || lease.OwnerPid <= 0 || lease.OwnerStartUtcTicks <= 0)
            return new(UsbReverseCleanupStatus.Failed, "USB 通道所有权记录无效。");
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

            var listed = await runner.RunAsync(adbPath, ["-s", approved.Serial, "reverse", "--list"],
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (listed.ExitCode != 0)
                return new(UsbReverseCleanupStatus.Failed, "无法读取原平板的 USB 通道映射。");
            var lines = listed.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
            // adb reverse --list emits: transport-name device-endpoint host-endpoint.
            // Reject unknown formats rather than inferring ownership from a substring.
            if (lines.Any(parts => parts.Length != 3))
                return new(UsbReverseCleanupStatus.Failed, "USB 通道列表格式无法确认，保留原映射。");
            var owned = lines.Where(parts => parts[1] == "tcp:27183").ToArray();
            if (owned.Length == 0)
                return new(UsbReverseCleanupStatus.AlreadyAbsent, "原 USB 通道已经移除。");
            if (owned.Length != 1 || owned[0][2] != "tcp:27183")
                return new(UsbReverseCleanupStatus.MappingChanged, "端口已指向其他映射，未修改它。");
            // PID reuse and owner-state changes remain fail-closed. TargetAsync
            // then rechecks fresh ADB, USB identity, and exclusions once again.
            if (ownerIsRunning(lease.OwnerPid, lease.OwnerStartUtcTicks))
                return new(UsbReverseCleanupStatus.OwnerRunning, "原连接进程仍在运行，保留 USB 通道。");
            await adb.RemoveReverseAsync(approved, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(UsbReverseCleanupStatus.Removed, "已清理本次连接创建的 USB 通道。");
        }
        catch (OperationCanceledException) { throw; }
        catch (DevicePolicyException ex) { return new(UsbReverseCleanupStatus.PolicyBlocked, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or TimeoutException)
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
    Removed, AlreadyAbsent, OwnerRunning, DeviceUnavailable, IdentityChanged, PolicyBlocked, MappingChanged, Failed
}

internal sealed record UsbReverseCleanupResult(UsbReverseCleanupStatus Status, string Message)
{
    internal bool Complete => Status is UsbReverseCleanupStatus.Removed or UsbReverseCleanupStatus.AlreadyAbsent;
}
