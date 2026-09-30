using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace TabLink.Windows;

public sealed record VirtualDisplayInfo(string DeviceName, string FriendlyName, bool IsPrimary, Rectangle Bounds, bool IsTabLinkCompatible)
{
    public override string ToString() => $"{FriendlyName} · {Bounds.Width} × {Bounds.Height}" + (IsPrimary ? " · 主屏" : IsTabLinkCompatible ? " · 可用虚拟副屏" : "");
}

public sealed record VirtualDisplaySource(string SourceKey, string DeviceName, uint AdapterLowPart,
    int AdapterHighPart, uint SourceId, bool IsActive, bool IsCloned, bool IsInUse);

public sealed record VirtualDisplayTarget(string TargetKey, string AdapterInstanceId, string MonitorPath,
    uint AdapterLowPart, int AdapterHighPart, uint TargetId, string FriendlyName,
    bool IsAvailable, bool IsActive, bool IsPrimary, IReadOnlyList<VirtualDisplaySource> Sources);

public static partial class VirtualDisplayManager
{
    // Without awareness, Windows can project IddCx paths onto the physical
    // adapter as compatibility clones and mark the real paths unavailable.
    private const uint QdcVirtualModeAware = 0x10;
    private const uint SdcVirtualModeAware = 0x8000;
    private const uint PathSupportVirtualMode = 0x8;
    public static string InstallerPath => Path.Combine(AppContext.BaseDirectory, "TabLink.DriverSetup.exe");

    public static IReadOnlyList<VirtualDisplayTarget> GetTargets() => BuildTargets(ReadDisplayPaths(1), ReadDesktopLayout());

    // QDC_ALL_PATHS is a source/target graph, not one row per monitor.
    internal static IReadOnlyList<VirtualDisplayTarget> BuildTargets(IReadOnlyList<ActiveDisplay> paths,
        IReadOnlyList<DisplayDesktopState> layout)
    {
        var usedSources = paths.Where(p => p.IsActive).Select(p => SourceKey(p.AdapterId.LowPart, p.AdapterId.HighPart, p.SourceId)).ToHashSet(StringComparer.Ordinal);
        return paths.Where(p => p.IsMttDriver && p.IsMttMonitor && !string.IsNullOrWhiteSpace(p.MonitorPath))
            .GroupBy(p => TargetKey(p.AdapterInstanceId, p.MonitorPath, p.TargetAdapterId.LowPart, p.TargetAdapterId.HighPart, p.TargetId), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var sources = group.GroupBy(p => SourceKey(p.AdapterId.LowPart, p.AdapterId.HighPart, p.SourceId), StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var p = g.First();
                        return new VirtualDisplaySource(g.Key, p.SourceName, p.AdapterId.LowPart, p.AdapterId.HighPart,
                            p.SourceId, g.Any(x => x.IsActive), g.Any(x => x.IsCloned), usedSources.Contains(g.Key));
                    }).OrderBy(p => p.SourceId).ToArray();
                return new VirtualDisplayTarget(group.Key, first.AdapterInstanceId, first.MonitorPath,
                    first.TargetAdapterId.LowPart, first.TargetAdapterId.HighPart, first.TargetId, first.FriendlyName,
                    group.Any(p => p.TargetAvailable), group.Any(p => p.IsActive),
                    group.Any(p => p.IsActive && layout.Any(d => d.IsPrimary && Same(d.DeviceName, p.SourceName))), sources);
            }).OrderBy(t => t.TargetKey, StringComparer.Ordinal).ToArray();
    }

    internal static string SourceKey(uint low, int high, uint id) => $"{high:X8}:{low:X8}:{id:X8}";
    internal static string TargetKey(string instance, string monitor, uint low, int high, uint id) =>
        $"{instance.ToUpperInvariant()}|{monitor.ToUpperInvariant()}|{high:X8}:{low:X8}:{id:X8}";
    internal static string TargetKey(DisplayLease lease) => TargetKey(lease.AdapterInstanceId, lease.MonitorPath,
        lease.TargetAdapterLowPart, lease.TargetAdapterHighPart, lease.TargetId);
    internal static string SourceKey(DisplayLease lease) => SourceKey(lease.AdapterLowPart, lease.AdapterHighPart, lease.SourceId);
    // Storage ownership follows the monitor, even when Windows assigns another source.
    internal static string GetTargetStorageKey(DisplayLease lease) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(lease.AdapterInstanceId.ToUpperInvariant() + "\0" + lease.MonitorPath.ToUpperInvariant() + "\0" + lease.TargetId.ToString("X8"))));

    // Discovery is read-only: never set global topology, create displays, disable
    // other adapters, or substitute the primary display if the VDD is missing.
    public static IReadOnlyList<VirtualDisplayInfo> GetDisplays()
    {
        var active = ReadActivePaths();
        return ReadDesktopLayout().Select(screen =>
        {
            var paths = active.Where(p => string.Equals(p.SourceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            // More than one target from a source is a clone, not an extended screen.
            var match = paths.Count == 1 ? paths[0] : null;
            var friendly = match?.FriendlyName;
            var compatible = match is not null && match.IsMttDriver && match.IsMttMonitor
                && !match.IsCloned && !screen.IsPrimary && screen.Width > 0 && screen.Height > 0;
            return new VirtualDisplayInfo(screen.DeviceName, string.IsNullOrWhiteSpace(friendly) ? screen.DeviceName : friendly,
                screen.IsPrimary, new Rectangle(screen.X, screen.Y, screen.Width, screen.Height), compatible);
        }).ToArray();
    }

    internal sealed record ActiveDisplay(string SourceName, string FriendlyName, bool IsMttDriver, bool IsMttMonitor, bool IsCloned,
        string AdapterInstanceId, string MonitorPath, LUID AdapterId, uint SourceId, LUID TargetAdapterId, uint TargetId, bool IsActive, bool TargetAvailable = true);

    private static List<ActiveDisplay> ReadActivePaths() => ReadDisplayPaths(2);

    private static List<ActiveDisplay> ReadDisplayPaths(uint queryFlags)
    {
        queryFlags |= QdcVirtualModeAware;
        // CCD can change between buffer sizing and the query. Retry only the
        // documented insufficient-buffer race, with a bounded attempt count.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (GetDisplayConfigBufferSizes(queryFlags, out var pathCount, out var modeCount) != 0) return [];
            if (pathCount > 1024 || modeCount > 4096) return [];
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            var result = QueryDisplayConfig(queryFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) continue;
            if (result != 0) return [];
            var entries = new List<ActiveDisplay>();
            var driverCache = new Dictionary<LUID, string?>();
            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = Header<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(1, path.sourceInfo.adapterId, path.sourceInfo.id) };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME { header = Header<DISPLAYCONFIG_TARGET_DEVICE_NAME>(2, path.targetInfo.adapterId, path.targetInfo.id) };
                if (GetSourceName(ref source) != 0 || GetTargetName(ref target) != 0) continue;
                if (!driverCache.TryGetValue(path.sourceInfo.adapterId, out var driverInstance))
                {
                    var adapter = new DISPLAYCONFIG_ADAPTER_NAME { header = Header<DISPLAYCONFIG_ADAPTER_NAME>(4, path.sourceInfo.adapterId, 0) };
                    driverInstance = GetAdapterName(ref adapter) == 0 ? GetMttAdapterInstance(adapter.adapterDevicePath) : null;
                    driverCache[path.sourceInfo.adapterId] = driverInstance;
                }
                var cloned = paths.Take((int)pathCount).Count(other => (other.flags & 1) != 0 && other.sourceInfo.adapterId.Equals(path.sourceInfo.adapterId) && other.sourceInfo.id == path.sourceInfo.id) > 1;
                var monitorMatches = target.monitorDevicePath?.Contains(@"DISPLAY#MTT1337#", StringComparison.OrdinalIgnoreCase) == true;
                entries.Add(new(source.viewGdiDeviceName ?? "", target.monitorFriendlyDeviceName ?? "", driverInstance is not null, monitorMatches, cloned,
                    driverInstance ?? "", target.monitorDevicePath ?? "", path.sourceInfo.adapterId, path.sourceInfo.id,
                    path.targetInfo.adapterId, path.targetInfo.id, (path.flags & 1) != 0, path.targetInfo.targetAvailable));
            }
            return entries;
        }
        return [];
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header<T>(uint type, LUID adapter, uint id) where T : struct =>
        new() { type = type, size = (uint)Marshal.SizeOf<T>(), adapterId = adapter, id = id };

    private static string? GetMttAdapterInstance(string? deviceInterfacePath)
    {
        if (string.IsNullOrWhiteSpace(deviceInterfacePath)) return null;
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return null;
        var detail = IntPtr.Zero;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiOpenDeviceInterface(set, deviceInterfacePath, 0, ref iface)) return null;
            SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required < 8 || required > 65536) return null;
            detail = Marshal.AllocHGlobal((int)required);
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            var device = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiGetDeviceInterfaceDetailWithInfo(set, ref iface, detail, required, out _, ref device)) return null;
            var bytes = new byte[8192];
            if (!SetupDiGetDeviceRegistryProperty(set, ref device, 1, out _, bytes, (uint)bytes.Length, out required)) return null;
            var ids = Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(required, bytes.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (!ids.Any(id => id.Equals(@"Root\MttVDD", StringComparison.OrdinalIgnoreCase) || id.Equals("MttVDD", StringComparison.OrdinalIgnoreCase))) return null;
            var instance = new StringBuilder(1024);
            return SetupDiGetDeviceInstanceId(set, ref device, instance, instance.Capacity, out _) ? instance.ToString() : null;
        }
        finally
        {
            if (detail != IntPtr.Zero) Marshal.FreeHGlobal(detail);
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    [StructLayout(LayoutKind.Sequential)] internal readonly record struct LUID(uint LowPart, int HighPart);
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    // The largest member of this native union is DISPLAYCONFIG_TARGET_MODE (48 B).
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType; [FieldOffset(4)] public uint id; [FieldOffset(8)] public LUID adapterId;
        // Source-mode view of the union. Target modes read from Windows retain
        // all their original 48 bytes when this blittable struct is copied.
        [FieldOffset(16)] public uint sourceWidth; [FieldOffset(20)] public uint sourceHeight;
        [FieldOffset(24)] public uint sourcePixelFormat; [FieldOffset(28)] public int sourceX; [FieldOffset(32)] public int sourceY;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology;
        public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdapterName(ref DISPLAYCONFIG_ADAPTER_NAME request);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classId, IntPtr parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref SP_DEVICE_INTERFACE_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder instance, int length, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
