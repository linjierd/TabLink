using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TabLink.Core;

namespace TabLink.Windows;

internal static partial class UsbInventory
{
    // Enumerate Windows' existing PnP records only: no USB handle, control request,
    // accessory handshake, device reset, driver binding, or mode switch.
    public static Task<IReadOnlyList<UsbDeviceIdentity>> ReadAsync(CancellationToken ct) => Task.Run<IReadOnlyList<UsbDeviceIdentity>>(() =>
    {
        var devices = new List<UsbDeviceIdentity>();
        var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint i=0;;i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set,i,ref info))
                {
                    var error=Marshal.GetLastWin32Error(); if(error==259) break;
                    throw new Win32Exception(error);
                }
                var buffer = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set,ref info,buffer,buffer.Capacity,out _)) continue;
                var match = UsbParent().Match(buffer.ToString());
                if(match.Success) devices.Add(new UsbDeviceIdentity(match.Groups[3].Value,match.Groups[1].Value,match.Groups[2].Value));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    },ct);

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\([^\\]+)$",RegexOptions.IgnoreCase)]
    private static partial Regex UsbParent();
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(IntPtr guid,string enumerator,IntPtr hwnd,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set,ref SP_DEVINFO_DATA info,StringBuilder id,int size,out int required);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
