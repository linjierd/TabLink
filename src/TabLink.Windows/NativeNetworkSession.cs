using TabLink.Core;

namespace TabLink.Windows;

// A native receiver owns its listener, display reservation, capture, firewall
// and deadlines. Closing it cannot dispose another receiver's resources.
internal sealed class NativeNetworkSession : IAsyncDisposable
{
    internal static readonly int[] Ports=[27184,27186,27187,27188,27189,27190,27191,27192];
    internal Guid Id { get; }=Guid.NewGuid();
    internal NetworkInterfaceChoice Network { get; }
    internal int Port { get; }
    internal string? PairingUri=>server?.NetworkConnectionUri;
    internal string State { get; private set; }="正在启动";
    internal bool IsStopped=>stopped;
    internal TabletDisplayProfile? Profile=>profile;
    internal TabletDisplayProfile? RequestedProfile {get;private set;}
    internal FrameServer? Server=>server;
    readonly bool allowTouch;
    readonly Func<Func<Task>,Task> onUi;
    readonly Action<string> log;
    readonly CancellationTokenSource lifetime=new();
    readonly SemaphoreSlim preparation=new(1,1);
    readonly SessionPresentationDeadline deadline=new();
    readonly DateTime createdUtc=DateTime.UtcNow;
    FrameServer? server;
    NetworkFirewall? firewall;
    DisplaySessionReservation? display;
    DesktopCapture? input;
    ActiveDisplayPower? power;
    TabletDisplayProfile? profile;
    string? targetKey;
    bool stopped;
    Task? disposal;

    internal NativeNetworkSession(NetworkInterfaceChoice network,int port,bool allowTouch,Func<Func<Task>,Task> onUi,Action<string> log)
    {Network=network;Port=port;this.allowTouch=allowTouch;this.onUi=onUi;this.log=log;}

    internal async Task StartAsync(CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        try
        {
            firewall=await NetworkFirewall.OpenAsync(Network,linked.Token,Port);
            linked.Token.ThrowIfCancellationRequested();
            var options=new NetworkSessionOptions(Network.LocalAddress,Port);
            try
            {
                server=new FrameServer(options,(p,c)=>onUi(()=>PrepareAsync(p,c)),Video,
                    allowTouch?m=>input?.Input(m):null,()=>input?.ReleaseInput());
            }
            catch{options.Dispose();throw;}
            server.Status+=message=>{State=message;log($"设备端口 {Port}：{message}");};
            server.DisplayProfileChanged+=p=>
            {
                var current=profile;
                if(current is not null&&!SameMode(current,p))server?.RequestReconnect();
            };
            server.Start();State="等待扫码配对（5 分钟有效）";
        }
        catch{await DisposeAsync();throw;}
    }

    async Task PrepareAsync(TabletDisplayProfile next,CancellationToken ct)
    {
        RequestedProfile=next;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        await preparation.WaitAsync(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if(display is null||profile is null||!SameMode(profile,next))
            {
                await ReleaseDisplayAsync();
                display=await DisplaySessionAllocator.Shared.AcquireAsync(Id,next,linked.Token,targetKey);
                targetKey=display.TargetKey;
                input=new DesktopCapture(display.CurrentDisplay,identity:display.Lease);
                power=new ActiveDisplayPower();
            }
            linked.Token.ThrowIfCancellationRequested();
            profile=next;deadline.Reset(DateTime.UtcNow);State="已配对，等待显示首帧";
        }
        catch(Exception ex){log($"设备 {Port} 副屏准备失败：{ex.Message}");await ReleaseDisplayAsync();throw;}
        finally{preparation.Release();}
    }

    static bool SameMode(TabletDisplayProfile a,TabletDisplayProfile b)=>a.Width==b.Width&&a.Height==b.Height&&a.RequestedRefreshRate==b.RequestedRefreshRate;
    IAsyncEnumerable<VideoPacket> Video(CancellationToken ct)
    {
        var owned=display??throw new IOException("设备尚未分配到独立副屏。");
        return VideoPipeline.StreamAsync(owned.CurrentDisplay,profile??throw new IOException("缺少屏幕参数。"),ct,log,owned.Lease);
    }

    internal async Task CheckAsync(IReadOnlyList<NetworkInterfaceChoice>? available,InputDesktopStatus desktop)
    {
        if(stopped||server is null)return;
        if(available is not null&&!available.Any(x=>Matches(x,Network)))
        {State="线路已断开或被排除";await DisposeAsync();return;}
        if(display is null)
        {
            if(DateTime.UtcNow-createdUtc>TimeSpan.FromMinutes(5)){State="配对已超时";await DisposeAsync();}
            return;
        }
        if(preparation.CurrentCount==0)return;
        var now=DateTime.UtcNow;
        var state=deadline.Evaluate(now,server.LastClientProgressUtc,server.CapturePaused,desktop);
        display.Guard.Renew(state.DeadlineUtc);
        if(now>state.DeadlineUtc){State="设备超过 20 秒未确认显示画面";await DisposeAsync();return;}
        State=state.CapturePaused?"画面暂停，连接保留":server.ClientConnected?$"{profile!.Width} × {profile.Height} · {(server.SubmissionEvidenceOnly?"解码提交（呈现待验证）":"显示")} {server.ClientReportedFps:F1} 帧/秒":"等待设备重连";
        if(desktop.IsAvailable&&server.LastPresentedUtc>now.AddSeconds(-5))
            try{display.Guard.RefreshRememberedLayout();}catch(IOException){}
    }

    internal static bool Matches(NetworkInterfaceChoice a,NetworkInterfaceChoice b)=>a.InterfaceId==b.InterfaceId&&a.LocalAddress.Equals(b.LocalAddress)&&a.UsbSerial==b.UsbSerial&&a.PrefixLength==b.PrefixLength;
    internal void Reconnect()=>server?.RequestReconnect();
    async Task ReleaseDisplayAsync()
    {
        try
        {
            input?.Dispose();input=null;
            if(display is {} owned){await owned.DisposeAsync();display=null;}
        }
        finally{power?.Dispose();power=null;}
    }
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    async Task DisposeCoreAsync()
    {
        stopped=true;lifetime.Cancel();
        try{if(server is {} running){await running.DisposeAsync();server=null;}}
        finally
        {
            await preparation.WaitAsync();
            try{await ReleaseDisplayAsync();}
            finally
            {
                preparation.Release();
                if(firewall is {} rule){await rule.DisposeAsync();firewall=null;}
            }
        }
    }
}
