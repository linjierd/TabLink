using System.Runtime.InteropServices;
using System.Text;

namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    private static void AssertNoActiveOutput(string exactInstance)
    {
        if (HasActiveOutput(exactInstance))
            throw new InvalidOperationException("TabLink 虚拟屏仍在活动桌面中，请先停止连接并断开该副屏，再添加显示模式。");
    }

    private static bool HasActiveOutput(string exactInstance)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // The legacy view can hide an active IddCx adapter behind a
            // physical-adapter clone. Require the real virtual-aware paths.
            const uint queryFlags = 2 | 0x10;
            var error = GetDisplayConfigBufferSizes(queryFlags, out var count, out var modeCount);
            if (error != 0 || count > 1024 || modeCount > 4096)
                throw new InvalidOperationException("无法确认当前显示拓扑，未重启驱动。");
            var paths = new CONFIG_PATH[count];
            var modes = new CONFIG_MODE[modeCount];
            error = QueryDisplayConfig(queryFlags, ref count, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == 122) continue;
            if (error != 0) throw new InvalidOperationException("无法读取当前显示拓扑，未重启驱动（" + error + "）。");
            var adapters = new HashSet<DISPLAY_LUID>();
            for (var i = 0; i < count; i++)
            {
                foreach (var luid in new[] { paths[i].source.adapterId, paths[i].target.adapterId })
                {
                    if (!adapters.Add(luid)) continue;
                    var request = new CONFIG_ADAPTER_NAME { header = new() { type = 4, size = (uint)Marshal.SizeOf<CONFIG_ADAPTER_NAME>(), adapterId = luid } };
                    if (DisplayConfigGetDeviceInfo(ref request) != 0)
                        throw new InvalidOperationException("无法确认活动显卡身份，未重启驱动。");
                    if (ResolveAdapterInstance(request.adapterDevicePath).Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
        throw new InvalidOperationException("显示器拓扑持续变化，未重启驱动。");
    }

    private static string ResolveAdapterInstance(string interfacePath)
    {
        var classId = DisplayClass;
        var set = SetupDiCreateDeviceInfoList(ref classId, IntPtr.Zero);
        if (set == new IntPtr(-1)) ThrowLastError("无法查询活动显卡设备");
        var detail = IntPtr.Zero;
        try
        {
            var iface = new DEVICE_INTERFACE { cbSize = (uint)Marshal.SizeOf<DEVICE_INTERFACE>() };
            if (!SetupDiOpenDeviceInterface(set, interfacePath, 0, ref iface)) ThrowLastError("无法解析活动显卡接口");
            SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required < 8 || required > 65536) throw new InvalidOperationException("活动显卡接口信息长度无效。");
            detail = Marshal.AllocHGlobal((int)required);
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            var device = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiGetDeviceInterfaceDetailWithInfo(set, ref iface, detail, required, out _, ref device)) ThrowLastError("无法读取活动显卡实例");
            var instance = new StringBuilder(1024);
            if (!SetupDiGetDeviceInstanceId(set, ref device, instance, instance.Capacity, out _)) ThrowLastError("无法读取活动显卡标识");
            return instance.ToString();
        }
        finally { if (detail != IntPtr.Zero) Marshal.FreeHGlobal(detail); SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct DISPLAY_LUID(uint LowPart, int HighPart);
    [StructLayout(LayoutKind.Sequential)] private struct CONFIG_SOURCE { public DISPLAY_LUID adapterId; public uint id, modeIndex, status; }
    [StructLayout(LayoutKind.Sequential)] private struct CONFIG_TARGET
    {
        public DISPLAY_LUID adapterId; public uint id, modeIndex, technology, rotation, scaling, numerator, denominator, scanline;
        [MarshalAs(UnmanagedType.Bool)] public bool available; public uint status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CONFIG_PATH { public CONFIG_SOURCE source; public CONFIG_TARGET target; public uint flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct CONFIG_MODE { [FieldOffset(0)] public uint type; }
    [StructLayout(LayoutKind.Sequential)] private struct CONFIG_HEADER { public uint type, size; public DISPLAY_LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct CONFIG_ADAPTER_NAME
    { public CONFIG_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath; }
    [StructLayout(LayoutKind.Sequential)] private struct DEVICE_INTERFACE { public uint cbSize; public Guid classGuid; public uint flags; public UIntPtr reserved; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint count, [Out] CONFIG_PATH[] paths, ref uint modeCount, [Out] CONFIG_MODE[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref CONFIG_ADAPTER_NAME request);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref DEVICE_INTERFACE iface);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DEVICE_INTERFACE iface, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(IntPtr set, ref DEVICE_INTERFACE iface, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA device);
}
