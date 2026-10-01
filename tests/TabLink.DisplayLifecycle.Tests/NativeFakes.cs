namespace TabLink.Windows;

// This test executable links the real watcher, but never links the real display adapter,
// diagnostics writer, or USB cleanup. An accidental mutation remains entirely in memory.
public sealed record DisplayLease(Guid LeaseId, string DeviceName);
public sealed record DisplayChangeResult(bool Success, bool Changed, string Message);
public sealed record VirtualDisplayInfo(string DeviceName);
internal sealed record DisplayRelocation(VirtualDisplayInfo CurrentDisplay, DisplayLease RememberedLease);
static class VirtualDisplayManager
{
    public static Func<DisplayLease, DisplayChangeResult> OnDetach = _ => throw new Exception("Unexpected fake detach");
    public static DisplayChangeResult Detach(DisplayLease lease) => OnDetach(lease);
    public static DisplayChangeResult Restore(DisplayLease lease) => throw new Exception("Tests must not restore displays");
    internal static DisplayRelocation ResolveLayout(DisplayLease lease) => throw new IOException("No native desktop in pure lifecycle tests");
    internal static bool RememberedLayoutEquals(DisplayLease left, DisplayLease right) => left == right;
    public static DisplayLease CaptureDetachedLease(int width, int height, int refreshRate) => throw new Exception("Tests must not enumerate native displays");
    internal static DisplayLease ReuseRememberedPosition(DisplayLease fresh, DisplayLease remembered) => fresh;
    internal static string GetTargetStorageKey(DisplayLease lease) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.DeviceName)));
}
internal readonly record struct AdbReverseEndpoint(int DevicePort)
{
    internal bool IsValid => DevicePort is >= 49152 and <= 65535;
}
internal sealed record UsbReverseLease(Guid Id, int OwnerPid, long OwnerStartUtcTicks,
    AdbReverseEndpoint Endpoint, int SchemaVersion = 2)
{
    internal const int CurrentSchemaVersion = 2;
    internal static Func<UsbReverseLease,CancellationToken,Task<UsbReverseCleanupResult>> OnCleanup =
        (_,_) => throw new Exception("Tests must not use ADB");
    internal static Task<UsbReverseCleanupResult> CleanupAfterOwnerExitAsync(UsbReverseLease receipt, CancellationToken cancellationToken) =>
        OnCleanup(receipt,cancellationToken);
}
internal enum UsbReverseCleanupStatus { Removed, AlreadyAbsent, DeviceUnavailable, MappingChanged, Failed }
internal sealed record UsbReverseCleanupResult(UsbReverseCleanupStatus Status, string Message)
{
    internal bool Complete => Status is UsbReverseCleanupStatus.Removed or UsbReverseCleanupStatus.AlreadyAbsent;
}
static class Diagnostics
{
    internal static Action<string, object>? OnSave;
    public static void Save(string name, object value) => OnSave?.Invoke(name, value);
}
