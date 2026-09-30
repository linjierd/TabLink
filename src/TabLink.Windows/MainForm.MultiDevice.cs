using System.Diagnostics;
using QRCoder;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly List<NativeNetworkSession> additionalSessions=[];
    readonly ListBox sessionList=new(){Dock=DockStyle.Fill,IntegralHeight=false};
    readonly Button addSession=new(){Text="添加原生客户端连接"};
    readonly Button stopSession=new(){Text="停止选中设备"};
    readonly Button retrySession=new(){Text="重连选中设备"};
    readonly Button stopAll=new(){Text="停止当前设备"};
    readonly PictureBox deviceQr=new(){Size=new Size(250,250),SizeMode=PictureBoxSizeMode.Zoom};
    readonly Label deviceHint=new(){AutoSize=true,MaximumSize=new Size(480,0)};
    string? deviceQrUri;
    bool monitoringAdditional;
    Task? additionalStartTask;
    DateTime lastAdditionalNetworkUtc;
    bool HasAdditionalSessions=>additionalSessions.Any(x=>!x.IsStopped)||browserHost is not null;
    bool HasAnySessions=>server is not null||HasAdditionalSessions;

    TabPage BuildMultiDeviceTab()
    {
        var page=new TabPage("原生客户端"){BackColor=Color.White,Padding=new Padding(18),AutoScroll=true};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,265));
        layout.Controls.Add(new Label{Text="只允许一台原生客户端使用唯一的扩展屏。先在 Wi-Fi / USB 免调试页选择线路，再添加设备。\n设备认证并上报屏幕参数后才安装虚拟屏；停止连接后自动卸载。",AutoSize=true,MaximumSize=new Size(880,0),ForeColor=muted},0,0);
        layout.Controls.Add(sessionList,0,1);
        layout.Controls.Add(Flow(addSession,retrySession,stopSession,stopAll),0,2);
        var pair=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};pair.Controls.Add(deviceQr);pair.Controls.Add(deviceHint);layout.Controls.Add(pair,0,3);
        page.Controls.Add(layout);
        addSession.Click+=async(_,_)=>await GuardAsync(AddNativeSessionAsync);
        stopSession.Click+=async(_,_)=>await GuardAsync(async()=>{if(sessionList.SelectedItem is NativeSessionRow r){await r.Session.DisposeAsync();RefreshSessionList();}});
        retrySession.Click+=(_,_)=>{if(sessionList.SelectedItem is NativeSessionRow r)r.Session.Reconnect();};
        stopAll.Click+=async(_,_)=>await GuardAsync(StopAllAsync);
        sessionList.SelectedIndexChanged+=(_,_)=>ShowSessionPairing();
        return page;
    }

    Task AddNativeSessionAsync()=>additionalStartTask=AddNativeSessionCoreAsync();
    async Task AddNativeSessionCoreAsync()
    {
        if(HasAnySessions)throw new InvalidOperationException("TabLink 只允许一个副屏连接。请先停止当前设备。");
        var selected=networks.SelectedItem as NetworkInterfaceChoice??throw new IOException("请先在 Wi-Fi / USB 免调试页选择可用线路。");
        var fresh=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        var choice=fresh.SingleOrDefault(x=>SameInterface(x,selected))??throw new IOException("所选线路已经变化或被排除，请刷新线路。");
        var used=additionalSessions.Where(x=>!x.IsStopped).Select(x=>x.Port).ToHashSet();
        // The original connection page retains its own fixed listener port.
        used.Add(27184);
        var port=NativeNetworkSession.Ports.FirstOrDefault(x=>!used.Contains(x));
        if(port==0)throw new IOException("其他原生配对端口已用完，请使用首页主连接或停止一个设备。");
        _=VideoPipeline.FindFfmpeg();
        var session=new NativeNetworkSession(choice,port,touch.Checked,OnUiAsync,Log);
        additionalSessions.Add(session);
        try{await session.StartAsync(lifetime.Token);}
        catch{additionalSessions.Remove(session);throw;}
        RefreshSessionList(session.Id);
        Log($"已创建独立设备配对：{choice.InterfaceAlias}，端口 {port}。尚未收到设备前不启用副屏。");
    }

    Task OnUiAsync(Func<Task> action)
    {
        if(IsDisposed||Disposing||closing)return Task.FromCanceled(new CancellationToken(true));
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(async()=>
            {
                try{if(closing)throw new OperationCanceledException();await action();done.TrySetResult();}
                catch(OperationCanceledException){done.TrySetCanceled();}
                catch(Exception ex){done.TrySetException(ex);}
            });
        }
        catch(InvalidOperationException){done.TrySetCanceled();}
        return done.Task;
    }

    void RefreshSessionList(Guid? select=null)
    {
        var selected=select??(sessionList.SelectedItem as NativeSessionRow)?.Session.Id;
        sessionList.BeginUpdate();sessionList.Items.Clear();
        if(server is not null)sessionList.Items.Add("主连接：请在 Wi-Fi / USB 或 USB 调试页管理");
        foreach(var session in additionalSessions.Where(x=>!x.IsStopped))sessionList.Items.Add(new NativeSessionRow(session));
        foreach(var row in sessionList.Items.OfType<NativeSessionRow>())if(row.Session.Id==selected){sessionList.SelectedItem=row;break;}
        if(sessionList.SelectedIndex<0&&sessionList.Items.Count>0)sessionList.SelectedIndex=0;
        sessionList.EndUpdate();ShowSessionPairing();
    }

    void ShowSessionPairing()
    {
        var session=(sessionList.SelectedItem as NativeSessionRow)?.Session;
        var uri=session?.PairingUri;
        if(deviceQrUri!=uri){deviceQr.Image?.Dispose();deviceQr.Image=uri is null?null:MakeQr(uri);deviceQrUri=uri;}
        deviceHint.Text=session is null?"选择设备查看专属二维码。\n浏览器连接在“浏览器”页管理。":$"{session.Network.InterfaceAlias} · {session.Network.LocalAddress}:{session.Port}\n\n{session.State}\n\n在另一台设备的 TabLink 原生客户端扫码。每个二维码只用于一台设备。\n停止当前设备后才能连接另一台设备。";
        stopSession.Enabled=retrySession.Enabled=session is not null&&!busy&&!closing;
    }
    static Bitmap MakeQr(string value)
    {
        using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(value,QRCodeGenerator.ECCLevel.M);
        using var code=new PngByteQRCode(data);using var stream=new MemoryStream(code.GetGraphic(6));using var decoded=Image.FromStream(stream);return new Bitmap(decoded);
    }

    async Task MonitorAdditionalAsync()
    {
        if(monitoringAdditional||closing||!HasAdditionalSessions)return;
        monitoringAdditional=true;
        try
        {
            IReadOnlyList<NetworkInterfaceChoice>? choices=null;
            if(DateTime.UtcNow-lastAdditionalNetworkUtc>TimeSpan.FromSeconds(6))
            {choices=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);lastAdditionalNetworkUtc=DateTime.UtcNow;}
            var desktop=InputDesktopAvailability.Query();
            foreach(var session in additionalSessions.ToArray())
                try{await session.CheckAsync(choices,desktop);}
                catch(Exception ex)
                {
                    Log("独立设备检查失败："+ex.Message);
                    try{await session.DisposeAsync();}catch(Exception cleanup){Log("该设备回收需要检查："+cleanup.Message);}
                }
            await MonitorBrowserAsync(choices,desktop);
            RefreshSessionList();
            UpdateAdditionalButtons();
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception ex){Log("原生客户端监测："+ex.Message);}
        finally{monitoringAdditional=false;}
    }

    async Task StopAllAsync()
    {
        await StopAsync();
        foreach(var session in additionalSessions.ToArray())try{await session.DisposeAsync();}catch(Exception ex){Log("设备回收失败："+ex.Message);}
        await StopBrowserAsync();
        RefreshSessionList();UpdateButtons();
    }

    async Task ApplyAdditionalPolicyAsync(DevicePolicySettings next)
    {
        var choices=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(next),lifetime.Token);
        foreach(var session in additionalSessions.Where(x=>!x.IsStopped&&x.Network.Kind==NetworkInterfaceKind.Usb).ToArray())
            if(!choices.Any(x=>SameInterface(x,session.Network)))await session.DisposeAsync();
        if(browserChoice is {Kind:NetworkInterfaceKind.Usb} selected&&!choices.Any(x=>SameInterface(x,selected)))await StopBrowserAsync();
        RefreshSessionList();
    }

    void UpdateAdditionalButtons()
    {
        var ready=!busy&&!closing&&!stopping&&settingsValid;
        addSession.Enabled=ready&&!HasAnySessions&&networks.SelectedItem is NetworkInterfaceChoice;
        stopAll.Enabled=ready&&HasAnySessions;
        if(server is null&&browserHost is not null)
        {
            var active=browserDisplays.Count;
            status.Text=active>0?"浏览器副屏正在传输":"浏览器接入已开启，等待设备扫码";
            metrics.Text=active>0?$"浏览器活动副屏 {active} · 全局最多一块虚拟屏":"本地 HTTPS + WebRTC · 尚未分配虚拟副屏";
        }
        else if(server is null&&additionalSessions.Any(x=>!x.IsStopped))
        {
            status.Text="副屏服务运行中";
            metrics.Text=$"原生连接 {additionalSessions.Count(x=>!x.IsStopped)} · 全局最多一块虚拟屏";
        }
        UpdateBrowserButtons(ready);
        EvaluateAutomaticUpdateApplication();
    }
    sealed record NativeSessionRow(NativeNetworkSession Session)
    {public override string ToString()=>$"原生设备 · {Session.Network.InterfaceAlias}:{Session.Port} · {Session.State}";}
}
