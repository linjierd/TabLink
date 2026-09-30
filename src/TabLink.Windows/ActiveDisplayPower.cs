using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TabLink.Windows;

// Per-device kernel requests remain valid across async thread changes.
internal sealed class ActiveDisplayPower : IDisposable
{
    readonly SafeFileHandle request;
    internal ActiveDisplayPower()
    {
        var reason=Marshal.StringToHGlobalUni("TabLink 正在向设备传输独立副屏");
        try
        {
            var context=new ReasonContext { Version=0, Flags=1, SimpleReason=reason };
            request=PowerCreateRequest(ref context);
        }
        finally { Marshal.FreeHGlobal(reason); }
        if(request.IsInvalid){request.Dispose();throw new IOException("无法创建副屏电源请求。");}
        if(!PowerSetRequest(request,0)||!PowerSetRequest(request,1))
        {request.Dispose();throw new IOException("无法保持副屏会话唤醒状态。");}
    }
    public void Dispose()=>request.Dispose();
    [StructLayout(LayoutKind.Explicit,Size=32)]
    struct ReasonContext
    {
        [FieldOffset(0)]public uint Version;
        [FieldOffset(4)]public uint Flags;
        [FieldOffset(8)]public nint SimpleReason;
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool PowerSetRequest(SafeFileHandle request,int type);
}
