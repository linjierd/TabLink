using System.Drawing;
using System.Runtime.InteropServices;

namespace TabLink.Windows;

internal sealed record DxgiOutputIdentity(int AdapterIndex, int OutputIndex, string AdapterName,
    uint AdapterLuidLow, int AdapterLuidHigh, string DeviceName, Rectangle Bounds, bool Attached, int Rotation);

// Read-only DXGI discovery, using the same EnumAdapters / EnumOutputs ordering
// as FFmpeg 7.0.2. A Windows DISPLAY number is never treated as a DXGI index.
internal static class DxgiCaptureTarget
{
    const int NotFound = unchecked((int)0x887a0002);

    internal static IReadOnlyList<DxgiOutputIdentity> ReadOutputs()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<OutputDescription>() != 96 || Marshal.SizeOf<AdapterDescription>() != 304)
            throw new IOException("DXGI 结构大小不匹配，未选择 GPU 捕获输出");
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Check(CreateDXGIFactory1(ref iid, out var factory), "创建 DXGI 枚举器");
        var outputs = new List<DxgiOutputIdentity>();
        try
        {
            for (uint adapterIndex = 0; adapterIndex < 64; adapterIndex++)
            {
                var result = Method<EnumerateObject>(factory, 7)(factory, adapterIndex, out var adapter);
                if (result == NotFound) return outputs;
                Check(result, "枚举 DXGI 显卡");
                try
                {
                    Check(Method<GetAdapterDescription>(adapter, 8)(adapter, out var description), "读取 DXGI 显卡身份");
                    for (uint outputIndex = 0; outputIndex < 128; outputIndex++)
                    {
                        result = Method<EnumerateObject>(adapter, 7)(adapter, outputIndex, out var output);
                        if (result == NotFound) break;
                        Check(result, "枚举 DXGI 输出");
                        try
                        {
                            Check(Method<GetOutputDescription>(output, 7)(output, out var item), "读取 DXGI 输出身份");
                            outputs.Add(new((int)adapterIndex, (int)outputIndex, description.Description,
                                description.LuidLow, description.LuidHigh, item.DeviceName,
                                Rectangle.FromLTRB(item.Left, item.Top, item.Right, item.Bottom), item.Attached, item.Rotation));
                        }
                        finally { Marshal.Release(output); }
                        if (outputIndex == 127) throw new IOException("DXGI 输出数量超过安全枚举上限");
                    }
                }
                finally { Marshal.Release(adapter); }
            }
            throw new IOException("DXGI 显卡数量超过安全枚举上限");
        }
        finally { Marshal.Release(factory); }
    }

    internal static DxgiOutputIdentity? SelectUnique(VirtualDisplayInfo display,
        IReadOnlyList<DxgiOutputIdentity> outputs, out string reason)
    {
        if (!display.IsTabLinkCompatible || display.IsPrimary || display.Bounds.Width < 1 || display.Bounds.Height < 1)
        { reason = "所选显示器不是已验证的独立虚拟副屏"; return null; }
        var matches = outputs.Where(output => output.Attached && output.Bounds == display.Bounds &&
            output.DeviceName.Equals(display.DeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            reason = matches.Length == 0 ? "DXGI 中没有对应的活动虚拟屏输出" : "DXGI 输出身份存在歧义";
            return null;
        }
        reason = $"DXGI adapter {matches[0].AdapterIndex}, output {matches[0].OutputIndex}, {matches[0].DeviceName}";
        return matches[0];
    }

    internal static bool IsCurrent(VirtualDisplayInfo display, DxgiOutputIdentity target)
    {
        var current = SelectUnique(display, ReadOutputs(), out _);
        return current is not null && current.AdapterIndex == target.AdapterIndex && current.OutputIndex == target.OutputIndex &&
            current.AdapterLuidLow == target.AdapterLuidLow && current.AdapterLuidHigh == target.AdapterLuidHigh &&
            current.Rotation == target.Rotation;
    }

    static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    static void Check(int result, string action)
    {
        if (result < 0) throw new IOException(action + "失败（0x" + result.ToString("X8") + "）", Marshal.GetExceptionForHR(result));
    }

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumerateObject(IntPtr self, uint index, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetOutputDescription(IntPtr self, out OutputDescription result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetAdapterDescription(IntPtr self, out AdapterDescription result);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        [MarshalAs(UnmanagedType.Bool)] public bool Attached;
        public int Rotation;
        public IntPtr Monitor;
    }
}
