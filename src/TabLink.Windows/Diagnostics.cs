using System.Drawing.Imaging;
using TabLink.Core;

namespace TabLink.Windows;

internal static class Diagnostics
{
    internal static async Task<int> ProbeAsync()
    {
        var usb=await UsbInventory.ReadAsync(CancellationToken.None);
        var settings=new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","settings.json")).Load();
        var path=AdbLocator.FindAdbPath(settings.AdbPath);
        var adb=path is null?null:new AdbClient(path,new DevicePolicy(settings),UsbInventory.ReadAsync);
        var devices=adb is null?Array.Empty<AdbDevice>():await adb.ListDevicesAsync();
        return Save("device-probe.json",()=>new{timestamp=DateTimeOffset.Now,adbPath=path,usb,adbDevices=devices,displays=VirtualDisplayManager.GetDisplays()})?0:1;
    }

    internal static async Task<int> SmokeAsync(string serial,int seconds)
    {
        seconds=Math.Clamp(seconds,10,120);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(seconds+35));
        var settings=new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","settings.json")).Load();
        var adb=new AdbClient(AdbLocator.FindAdbPath(settings.AdbPath)??throw new IOException("找不到adb"),new DevicePolicy(settings),UsbInventory.ReadAsync);
        var candidate=(await adb.ListDevicesAsync(timeout.Token)).Single(d=>d.Serial==serial);
        var approved=await adb.ApproveAsync(candidate,timeout.Token);
        // A diagnostic image proves Android receives and renders frames before
        // any Windows display is created or enabled. It contains no desktop data.
        var images=new[]{CreateTestFrame(false),CreateTestFrame(true)};
        var tick=0;
        await using var server=new FrameServer(()=>images[(Interlocked.Increment(ref tick)/10)%2],null,()=>{},10);
        var reversed=false;
        AdbReverseEndpoint? reverseEndpoint=null;
        var began=DateTimeOffset.Now;
        try
        {
            server.Start();
            reverseEndpoint=await adb.ReserveRandomReverseEndpointAsync(approved,timeout.Token);reversed=true;
            await adb.LaunchAsync(approved,server.Token,reverseEndpoint.Value,timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(seconds),timeout.Token);
            var passed=server.PresentedFrames>=Math.Max(5,seconds*3)&&server.LastPresentedUtc>DateTime.UtcNow.AddSeconds(-5);
            var saved=Save("usb-smoke-result.json",()=>new{passed,began,ended=DateTimeOffset.Now,serial,server.FramesSent,server.PresentedFrames,server.PresentedWidth,server.PresentedHeight,server.LastPresentedUtc,desktopCaptured=false,virtualDisplayChanged=false});
            return passed&&saved?0:1;
        }
        catch(Exception e){Save("usb-smoke-result.json",()=>new{passed=false,serial,error=e.Message,began});throw;}
        finally{if(reversed&&reverseEndpoint is { } endpoint){try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(8));var mapping=await adb.InspectReversePortAsync(approved,endpoint,cleanup.Token);if(mapping.Status==AdbReversePortStatus.Existing)await adb.RemoveReverseAsync(approved,endpoint,cleanup.Token);else Save("usb-smoke-cleanup.json",()=>new{error="本次诊断的 USB 端点映射已经变化，未修改它。"});}catch(Exception e){Save("usb-smoke-cleanup.json",()=>new{error=e.Message});}}}
    }

    static byte[] CreateTestFrame(bool alternate)
    {
        using var image=new Bitmap(1280,800);
        using var graphics=Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(14,24,41));
        using var heading=new Font("Segoe UI",64,FontStyle.Bold);
        using var description=new Font("Microsoft YaHei UI",24);
        graphics.DrawString("TabLink",heading,Brushes.White,80,100);
        graphics.DrawString("USB 连接测试",description,Brushes.LightGreen,84,235);
        graphics.DrawString("平板正在接收电脑传来的测试画面",description,Brushes.White,84,305);
        graphics.DrawString("尚未启用虚拟副屏，不影响电脑桌面",description,Brushes.LightSteelBlue,84,365);
        using var bar=new SolidBrush(alternate?Color.FromArgb(51,200,160):Color.FromArgb(42,123,226));
        graphics.FillRectangle(bar,84,480,alternate?950:450,48);
        using var output=new MemoryStream();image.Save(output,ImageFormat.Jpeg);return output.ToArray();
    }
    static readonly DiagnosticStore Store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink", "diagnostics"));
    internal static string Folder => Store.Folder;
    internal static bool Save(string name, object? result) => Store.TrySave(name, () => result);
    internal static bool Save(string name, Func<object?> snapshot, Action<string>? reportFailure = null) => Store.TrySave(name, snapshot, reportFailure);
}
