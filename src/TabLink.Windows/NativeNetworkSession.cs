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
    internal VirtualDisplayInfo? CurrentDisplay=>display?.CurrentDisplay;
    internal bool HasPendingDisplayCleanup=>stopped&&display is not null;
    internal FrameServer? Server=>server;
    internal event Action<TabletDisplayProfile>? DisplayPreparationStarted;
    internal event Action<VirtualDisplayInfo>? DisplayPrepared;
    internal event Action<Exception>? DisplayPreparationFailed;
    internal event Action? Stopped;
    readonly bool allowTouch;
    readonly Func<Func<Task>,Task> onUi;
    readonly Action<string> log;
    readonly VideoEncoderSelectionOptions encoderOptions;
    readonly Action<VideoEncoderRuntimeSnapshot>? encoderSelected;
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

    internal NativeNetworkSession(NetworkInterfaceChoice network,int port,bool allowTouch,Func<Func<Task>,Task> onUi,
        Action<string> log,VideoEncoderSelectionOptions? encoderOptions=null,
        Action<VideoEncoderRuntimeSnapshot>? encoderSelected=null)
    {Network=network;Port=port;this.allowTouch=allowTouch;this.onUi=onUi;this.log=log;
        this.encoderOptions=encoderOptions??new();this.encoderSelected=encoderSelected;}

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
        DisplayPreparationStarted?.Invoke(next);
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
            DisplayPrepared?.Invoke(display!.CurrentDisplay);
        }
        catch(Exception ex){log($"设备 {Port} 副屏准备失败：{ex.Message}");DisplayPreparationFailed?.Invoke(ex);await ReleaseDisplayAsync();throw;}
        finally{preparation.Release();}
    }

    static bool SameMode(TabletDisplayProfile a,TabletDisplayProfile b)=>a.Width==b.Width&&a.Height==b.Height&&a.RequestedRefreshRate==b.RequestedRefreshRate;
    IAsyncEnumerable<VideoPacket> Video(CancellationToken ct)
    {
        var owned=display??throw new IOException("设备尚未分配到独立副屏。");
        return VideoPipeline.StreamAsync(owned.CurrentDisplay,profile??throw new IOException("缺少屏幕参数。"),ct,log,
            owned.Lease,encoderOptions:encoderOptions,encoderSelected:encoderSelected);
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
        if(now>state.DeadlineUtc){State="设备超过首帧或后续呈现期限";await DisposeAsync();return;}
        var progress=server.HasRecentPresentation?$"呈现回调 {server.ClientPresentedFps:F1} 帧/秒":server.HasRecentSubmission?$"解码提交 {server.ClientSubmittedFps:F1} 帧/秒（呈现待验证）":"等待画面";
        State=state.CapturePaused?"画面暂停，连接保留":server.ClientConnected?$"{profile!.Width} × {profile.Height} · {progress}":"等待设备重连";
        if(desktop.IsAvailable&&server.LastPresentedUtc>now.AddSeconds(-5))
            try{display.Guard.RefreshRememberedLayout();}catch(IOException){}
    }

    internal static bool Matches(NetworkInterfaceChoice a,NetworkInterfaceChoice b)=>a.InterfaceId==b.InterfaceId&&a.LocalAddress.Equals(b.LocalAddress)&&a.UsbSerial==b.UsbSerial&&a.PrefixLength==b.PrefixLength;
    internal void Reconnect()=>server?.RequestReconnect();
    async Task ReleaseDisplayAsync()
    {
        try
        {
            if(input is {} ownedInput)
            {
                try{ownedInput.Dispose();}
                catch(Exception ex){log($"设备 {Port} 画面输入清理需要检查：{ex.Message}");}
                input=null;
            }
            if(display is {} owned){await owned.DisposeAsync();display=null;}
        }
        finally
        {
            if(power is {} ownedPower)
            {
                try{ownedPower.Dispose();}
                catch(Exception ex){log($"设备 {Port} 电源请求清理需要检查：{ex.Message}");}
                power=null;
            }
        }
    }
    internal async Task RetryDisplayCleanupAsync()
    {
        if(!HasPendingDisplayCleanup)throw new IOException("此原生会话没有可安全重试的副屏所有权记录。");
        await preparation.WaitAsync();
        try{await ReleaseDisplayAsync();State="本次拥有的副屏已精确回收";}
        finally{preparation.Release();}
    }
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    async Task DisposeCoreAsync()
    {
        stopped=true;Stopped?.Invoke();lifetime.Cancel();
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
