using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using TabLink.Core;

namespace TabLink.Windows;

// Explicit developer integration test. Runs only when requested by its CLI,
// on previously configured empty targets; draws only on those owned screens.
internal sealed class MultiDisplayVerification : Form
{
    readonly string directory;
    readonly List<DisplaySessionReservation> owned=[];
    readonly List<PatternWindow> patterns=[];
    readonly Dictionary<DisplaySessionReservation,PatternWindow> patternOwners=[];
    readonly List<object> evidence=[];
    readonly CancellationTokenSource timeout=new(TimeSpan.FromSeconds(55));
    readonly System.Windows.Forms.Timer renewal=new(){Interval=1000};
    bool ended;
    internal MultiDisplayVerification(string directory)
    {
        this.directory=Path.GetFullPath(directory);Directory.CreateDirectory(this.directory);
        ShowInTaskbar=false;Opacity=0;Size=new Size(1,1);
        renewal.Tick+=(_,_)=>{foreach(var reservation in owned)reservation.Guard.Renew(DateTime.UtcNow.AddSeconds(20));};
        Shown+=async(_,_)=>await RunAsync();
        FormClosing+=(_,e)=>{if(!ended)e.Cancel=true;};
    }
    async Task RunAsync()
    {
        var primaryBefore=VirtualDisplayManager.GetDisplays().Where(x=>x.IsPrimary).ToArray();
        string? failure=null;
        try
        {
            if(VirtualDisplayManager.GetDisplays().Any(x=>x.IsTabLinkCompatible))throw new IOException("请先停止全部设备；测试不会接管已有活动虚拟屏。");
            var profile=new TabletDisplayProfile(1280,720,0,1,60,1280,720,[new(1280,720,60,1)]);
            renewal.Start();
            for(var i=0;i<3;i++)
            {
                var reservation=await DisplaySessionAllocator.Shared.AcquireAsync(Guid.NewGuid(),profile,timeout.Token);
                owned.Add(reservation);
                // Do not show windows while later targets are still being added:
                // CCD placement/DPI changes can relocate already visible forms.
                var window=new PatternWindow(i);patterns.Add(window);patternOwners.Add(reservation,window);
                evidence.Add(new{step="allocate",device=i,target=reservation.TargetKey,display=reservation.CurrentDisplay});
            }
            if(owned.Select(x=>x.TargetKey).Distinct().Count()!=3||owned.Select(x=>x.Lease.DeviceName).Distinct().Count()!=3)
                throw new IOException("显示目标或源并不独立。");
            AlignPatterns("all-targets-ready");
            await Task.Delay(700,timeout.Token);
            AlignPatterns("before-parallel-capture");
            // Concurrent encoders, not three sequential captures of one source.
            var results=await Task.WhenAll(owned.Select((r,i)=>CaptureFramesAsync(r,profile,i,timeout.Token)));
            evidence.Add(new{step="parallel-video",results});
            var keep=owned[2];var first=owned[0];patterns[0].Close();
            await first.DisposeAsync();owned.Remove(first);
            AlignPatterns("after-release-one");
            await Task.Delay(250,timeout.Token);
            var remaining=VirtualDisplayManager.ResolveCurrent(keep.Lease);
            evidence.Add(new{step="release-one",remaining});
            await CaptureFramesAsync(keep,profile,4,timeout.Token);
            var primaryAfter=VirtualDisplayManager.GetDisplays().Where(x=>x.IsPrimary).ToArray();
            if(!primaryBefore.SequenceEqual(primaryAfter))throw new IOException("物理主屏布局发生变化。");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            renewal.Stop();foreach(var pattern in patterns)pattern.Close();
            foreach(var reservation in owned.ToArray())try{await reservation.DisposeAsync();owned.Remove(reservation);}catch(Exception ex){failure=(failure??"")+"\nCleanup: "+ex.Message;}
            var displays=VirtualDisplayManager.GetDisplays();
            if(displays.Any(x=>x.IsTabLinkCompatible))failure=(failure??"")+"\n仍有活动虚拟屏。";
            File.WriteAllText(Path.Combine(directory,"multi-display-result.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.Now,success=failure is null,failure,evidence,primaryBefore,displays},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=failure is null?0:1;ended=true;Close();timeout.Dispose();renewal.Dispose();
        }
    }
    void AlignPatterns(string stage)
    {
        foreach(var reservation in owned)
        {
            var current=VirtualDisplayManager.ResolveCurrent(reservation.Lease);
            var window=patternOwners[reservation];
            var previous=window.Bounds;
            window.Bounds=current.Bounds;
            if(!window.Visible)window.Show();
            // Reapply after Show/WM_DPICHANGED and verify the physical HWND rect.
            current=VirtualDisplayManager.ResolveCurrent(reservation.Lease);
            window.Bounds=current.Bounds;window.Invalidate();window.Update();
            if(!GetWindowRect(window.Handle,out var rect))throw new IOException("无法读取测试窗口的实际位置。");
            var actual=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
            evidence.Add(new{step="pattern-bounds",stage,target=reservation.TargetKey,initial=reservation.CurrentDisplay.Bounds,
                previous,expected=current.Bounds,actual,window.DeviceDpi});
            if(actual!=current.Bounds)throw new IOException("测试窗口没有准确覆盖其自有虚拟目标，不能将编码哈希作为显示成功证据。");
        }
    }
    async Task<object> CaptureFramesAsync(DisplaySessionReservation reservation,TabletDisplayProfile profile,int index,CancellationToken ct)
    {
        using var output=File.Create(Path.Combine(directory,$"screen-{index}.h264"));
        var count=0;var hashes=new HashSet<string>();
        var current=VirtualDisplayManager.ResolveCurrent(reservation.Lease);
        await foreach(var packet in VideoPipeline.StreamAsync(current,profile,ct,null,reservation.Lease,browserCompatible:true))
        {
            if(packet.Type==0x20)
            {
                using var config=JsonDocument.Parse(packet.Payload);
                await output.WriteAsync(Convert.FromBase64String(config.RootElement.GetProperty("csd0").GetString()!),ct);
                await output.WriteAsync(Convert.FromBase64String(config.RootElement.GetProperty("csd1").GetString()!),ct);
            }
            if(packet.Type==0x21&&packet.IsFrame)
            {
                await output.WriteAsync(packet.Payload.AsMemory(8),ct);
                hashes.Add(Convert.ToHexString(SHA256.HashData(packet.Payload.AsSpan(8))));
                if(++count>=90)break;
            }
        }
        if(count<90||hashes.Count<45)throw new IOException($"第 {index} 个目标没有提供持续变化的视频。");
        return new{index,count,distinctEncodedFrames=hashes.Count,target=reservation.TargetKey};
    }
    sealed class PatternWindow : Form
    {
        readonly int index;readonly System.Windows.Forms.Timer animation=new(){Interval=16};int frame;
        internal PatternWindow(int index)
        {
            this.index=index;AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;DoubleBuffered=true;
            animation.Tick+=(_,_)=>{frame++;Invalidate();};animation.Start();FormClosed+=(_,_)=>animation.Dispose();
        }
        protected override bool ShowWithoutActivation=>true;
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(index switch{0=>Color.DarkRed,1=>Color.DarkGreen,_=>Color.Navy});
            using var font=new Font("Segoe UI",48,FontStyle.Bold);
            e.Graphics.DrawString($"TabLink test screen {index+1}\nFrame {frame}",font,Brushes.White,80,100);
            e.Graphics.FillRectangle(Brushes.Yellow,(frame*11)%Math.Max(1,Width-130),Height-140,120,80);
        }
    }
    [StructLayout(LayoutKind.Sequential)]struct RECT{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",SetLastError=true)]static extern bool GetWindowRect(IntPtr handle,out RECT rect);
}
