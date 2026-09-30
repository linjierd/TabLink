using System.Collections.Concurrent;
using System.Diagnostics;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    BrowserHost? browserHost;
    NetworkInterfaceChoice? browserChoice;
    readonly List<NetworkFirewall> browserRules=[];
    readonly ConcurrentDictionary<Guid,BrowserOwnedDisplay> browserDisplays=new();
    readonly ConcurrentDictionary<Guid,BrowserSessionStatus> browserStates=new();
    readonly ListBox browserSessions=new(){Dock=DockStyle.Top,Height=140,IntegralHeight=false,Visible=false};
    readonly ComboBox browserNetworks=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Button refreshBrowserNetworks=new(){Text="刷新线路"};
    readonly Button startBrowser=new(){Text="开启浏览器接入"};
    readonly Button newBrowserPair=new(){Text="重新生成配对二维码"};
    readonly Button stopBrowser=new(){Text="关闭浏览器接入"};
    readonly Button stopBrowserDevice=new(){Text="停止选中浏览器"};
    readonly Button exportCa=new(){Text="导出本机证书"};
    readonly Button copyBrowserUri=new(){Text="复制接入链接"};
    readonly PictureBox browserQr=new(){Size=new Size(200,200),SizeMode=PictureBoxSizeMode.Zoom,Visible=false};
    readonly Label browserHint=new(){AutoSize=true,MaximumSize=new Size(560,0),Text="选择当前可用线路，再开启浏览器接入。"};
    string? browserUri;
    Task? browserStartTask;

    Control BuildBrowserPanel()
    {
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(18),AutoScroll=true};
        var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=6};
        for(var i=0;i<layout.RowCount;i++)layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var help=new Label{AutoSize=true,MaximumSize=new Size(820,0),ForeColor=muted,Text="本地离线 HTTPS + WebRTC。首次使用需信任本机证书；当前只允许一台设备作为副屏。"};
        layout.Controls.Add(help,0,0);
        var route=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=3,RowCount=1,Margin=new Padding(0,10,0,10)};
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));route.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        route.Controls.Add(new Label{Text="网络线路",AutoSize=true,Margin=new Padding(0,7,8,0)},0,0);route.Controls.Add(browserNetworks,1,0);route.Controls.Add(refreshBrowserNetworks,2,0);layout.Controls.Add(route,0,1);
        layout.Controls.Add(Flow(startBrowser,exportCa,newBrowserPair,copyBrowserUri),0,2);
        var pair=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=new Padding(0,8,0,8)};pair.Controls.Add(browserQr);pair.Controls.Add(browserHint);layout.Controls.Add(pair,0,3);
        layout.Controls.Add(browserSessions,0,4);layout.Controls.Add(Flow(stopBrowserDevice,stopBrowser),0,5);page.Controls.Add(layout);
        page.SizeChanged+=(_,_)=>
        {
            var width=Math.Max(280,page.ClientSize.Width-page.Padding.Horizontal-28);
            help.MaximumSize=new Size(width,0);browserHint.MaximumSize=new Size(Math.Max(240,width-browserQr.Width-28),0);
        };
        startBrowser.Click+=async(_,_)=>await GuardAsync(StartBrowserAsync);
        refreshBrowserNetworks.Click+=async(_,_)=>await GuardAsync(RefreshNetworksAsync);
        browserNetworks.SelectedIndexChanged+=(_,_)=>UpdateBrowserButtons(!busy&&!closing&&!stopping);
        newBrowserPair.Click+=(_,_)=>{try{CreateBrowserPairing();}catch(Exception ex){ShowError(ex);}};
        stopBrowser.Click+=async(_,_)=>await GuardAsync(StopBrowserAsync);
        stopBrowserDevice.Click+=async(_,_)=>await GuardAsync(async()=>{if(browserSessions.SelectedItem is BrowserSessionRow row&&browserHost is {} host)await host.StopSessionAsync(row.Status.Id);});
        copyBrowserUri.Click+=(_,_)=>{if(browserUri is not null)try{Clipboard.SetText(browserUri);}catch(Exception ex){ShowError(ex);}};
        exportCa.Click+=(_,_)=>
        {
            if(browserHost is not {} host)return;
            using var save=new SaveFileDialog{FileName="TabLink-本机证书.cer",Filter="公开 CA 证书|*.cer",OverwritePrompt=true};
            if(save.ShowDialog(this)==DialogResult.OK)try{File.Copy(host.CaCertificatePath,save.FileName,true);Log("已导出本机公开证书。请传到接收设备安装，并核对指纹。");}catch(Exception ex){ShowError(ex);}
        };
        return page;
    }

    Task StartBrowserAsync()=>browserStartTask=StartBrowserCoreAsync();
    async Task StartBrowserCoreAsync()
    {
        if(browserHost is not null)return;
        if(HasAnySessions)throw new InvalidOperationException("TabLink 只允许一个副屏连接。请先停止当前原生连接。");
        var selected=browserNetworks.SelectedItem as NetworkInterfaceChoice??throw new IOException("请先选择可用的 Wi-Fi 或 USB 网络共享线路。");
        var available=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        var choice=available.SingleOrDefault(x=>SameInterface(x,selected))??throw new IOException("所选线路已变化或被排除。");
        _=VideoPipeline.FindFfmpeg();
        browserChoice=choice;var allowTouch=touch.Checked;
        try
        {
            browserRules.Add(await NetworkFirewall.OpenAsync(choice,lifetime.Token,27185));
            foreach(var port in BrowserHost.MediaPorts)browserRules.Add(await NetworkFirewall.OpenAsync(choice,lifetime.Token,port,"UDP"));
            lifetime.Token.ThrowIfCancellationRequested();
            browserHost=new BrowserHost(choice.LocalAddress,(id,p,ct)=>PrepareBrowserAsync(id,p,allowTouch,ct),1);
            browserHost.Diagnostic+=(id,message)=>Log($"浏览器 {id.ToString()[..8]}：{message}");
            browserHost.SessionChanged+=state=>
            {
                if(state.State=="closed")
                {
                    if(!browserStates.TryRemove(state.Id,out _))InvalidateBrowserPairing();
                }
                else
                {
                    var first=browserStates.TryAdd(state.Id,state);
                    if(!first)browserStates[state.Id]=state;
                    else InvalidateBrowserPairing();
                }
            };
            await browserHost.StartAsync(lifetime.Token);
            CreateBrowserPairing();SetStatus("浏览器接入已开启，等待设备扫码");metrics.Text=$"{choice.InterfaceAlias} · 本地 HTTPS + WebRTC · 单设备";
        }
        catch{await StopBrowserAsync();throw;}
    }

    async Task<BrowserDisplaySession> PrepareBrowserAsync(Guid id,TabletDisplayProfile profile,bool allowTouch,CancellationToken ct)
    {
        DisplaySessionReservation? reservation=null;DesktopCapture? captureInput=null;ActiveDisplayPower? powerRequest=null;
        try
        {
            reservation=await DisplaySessionAllocator.Shared.AcquireAsync(id,profile,ct);
            captureInput=new DesktopCapture(reservation.CurrentDisplay,identity:reservation.Lease);powerRequest=new ActiveDisplayPower();
            var owned=new BrowserOwnedDisplay(reservation,captureInput,powerRequest,profile);browserDisplays[id]=owned;
            var token=ct;token.ThrowIfCancellationRequested();
            return new BrowserDisplaySession(c=>VideoPipeline.StreamAsync(owned.Reservation.CurrentDisplay,profile,c,Log,owned.Reservation.Lease,browserCompatible:true),
                allowTouch?owned.Input.Input:null,owned.Input.ReleaseInput,async()=>
                {
                    browserDisplays.TryRemove(id,out _);
                    try{owned.Input.Dispose();await owned.Reservation.DisposeAsync();}
                    finally{owned.Power.Dispose();}
                });
        }
        catch(Exception ex)
        {
            Log("浏览器独立副屏准备失败："+ex.Message);
            browserDisplays.TryRemove(id,out _);captureInput?.Dispose();powerRequest?.Dispose();
            if(reservation is not null)await reservation.DisposeAsync();throw;
        }
    }

    void CreateBrowserPairing()
    {
        var host=browserHost??throw new IOException("请先开启浏览器接入。");
        var offer=host.CreatePairing();browserUri=offer.Uri;
        browserQr.Image?.Dispose();browserQr.Image=MakeQr(offer.Uri);browserQr.Visible=true;
        browserHint.Text=$"1. 首次使用：导出并在设备信任本机证书。\n2. 扫码，在页面点击开始连接。\n\n二维码单次使用，到期 {offer.ExpiresUtc.LocalDateTime:HH:mm:ss}。\n需要重新配对时，请生成新的二维码。\n\n证书 SHA-256 指纹：\n{host.CaFingerprint}";
        UpdateBrowserButtons(!busy&&!closing);
    }

    void InvalidateBrowserPairing()
    {
        if(IsDisposed||closing)return;
        if(InvokeRequired)
        {
            if(IsHandleCreated)try{BeginInvoke(InvalidateBrowserPairing);}catch(InvalidOperationException)when(IsDisposed||Disposing||closing){}
            return;
        }
        if(browserUri is null&&!browserQr.Visible)return;
        browserUri=null;var image=browserQr.Image;browserQr.Image=null;browserQr.Visible=false;image?.Dispose();
        browserHint.Text="本次二维码已使用。设备断开后，请点击“重新生成配对二维码”获取新的单次链接。";
        UpdateBrowserButtons(!busy&&!closing&&!stopping);
    }

    async Task MonitorBrowserAsync(IReadOnlyList<NetworkInterfaceChoice>? choices,InputDesktopStatus desktop)
    {
        var host=browserHost;if(host is null)return;
        if(choices is not null&&browserChoice is {} selected&&!choices.Any(x=>SameInterface(x,selected)))
        {Log("浏览器线路已断开或被排除，回收浏览器副屏。");await StopBrowserAsync();return;}
        foreach(var item in browserDisplays.ToArray())
        {
            var current=item.Value;
            browserStates.TryGetValue(item.Key,out var state);
            var now=DateTime.UtcNow;var evaluation=current.Deadline.Evaluate(now,state?.LastPresentedUtc,state?.CapturePaused??false,desktop);
            try
            {
                current.Reservation.Guard.Renew(evaluation.DeadlineUtc);
                if(now>evaluation.DeadlineUtc){Log("浏览器超过 20 秒没有显示进展，停止该设备。");await host.StopSessionAsync(item.Key);}
            }
            catch(ObjectDisposedException){}
            catch(Exception ex)
            {
                Log("此浏览器副屏检查失败："+ex.Message);
                try{await host.StopSessionAsync(item.Key);}catch(Exception cleanup){Log("该浏览器回收需要检查："+cleanup.Message);}
            }
        }
        var previous=(browserSessions.SelectedItem as BrowserSessionRow)?.Status.Id;
        browserSessions.BeginUpdate();browserSessions.Items.Clear();
        foreach(var state in browserStates.Values.OrderBy(x=>x.Id))browserSessions.Items.Add(new BrowserSessionRow(state));
        foreach(var row in browserSessions.Items.OfType<BrowserSessionRow>())if(row.Status.Id==previous){browserSessions.SelectedItem=row;break;}
        if(browserSessions.SelectedIndex<0&&browserSessions.Items.Count>0)browserSessions.SelectedIndex=0;
        browserSessions.EndUpdate();
        UpdateBrowserButtons(!busy&&!closing&&!stopping);
    }

    async Task StopBrowserAsync()
    {
        var host=browserHost;browserHost=null;
        try{if(host is not null)await host.DisposeAsync();}
        finally
        {
            foreach(var rule in browserRules.ToArray())try{await rule.DisposeAsync();browserRules.Remove(rule);}catch(Exception ex){Log("浏览器防火墙规则清理失败："+ex.Message);}
            browserChoice=null;browserUri=null;browserStates.Clear();browserSessions.Items.Clear();browserQr.Image?.Dispose();browserQr.Image=null;browserQr.Visible=false;
            browserHint.Text="浏览器接入已关闭，副屏已回收。";
            if(!HasAnySessions&&connectionMode.SelectedIndex==1){status.Text="浏览器接入尚未开启";metrics.Text="本地 HTTPS + WebRTC · 单设备";}
            UpdateBrowserButtons(!busy&&!closing);
            UpdateButtons();
        }
    }
    void UpdateBrowserButtons(bool ready)
    {
        browserNetworks.Enabled=refreshBrowserNetworks.Enabled=ready&&browserHost is null;
        startBrowser.Enabled=ready&&!HasAnySessions&&browserHost is null&&browserNetworks.SelectedItem is NetworkInterfaceChoice;
        newBrowserPair.Enabled=exportCa.Enabled=ready&&browserHost is not null;
        copyBrowserUri.Enabled=ready&&browserHost is not null&&browserUri is not null;
        stopBrowser.Enabled=stopBrowserDevice.Enabled=ready&&browserHost is not null;
        browserSessions.Visible=browserSessions.Items.Count>0;
        stopBrowserDevice.Visible=browserSessions.Visible;stopBrowser.Visible=browserHost is not null;
    }
    sealed class BrowserOwnedDisplay(DisplaySessionReservation reservation,DesktopCapture input,ActiveDisplayPower power,TabletDisplayProfile profile)
    {
        internal DisplaySessionReservation Reservation {get;}=reservation;
        internal DesktopCapture Input {get;}=input;
        internal ActiveDisplayPower Power {get;}=power;
        internal TabletDisplayProfile Profile {get;}=profile;
        internal SessionPresentationDeadline Deadline {get;}=CreateDeadline();
        static SessionPresentationDeadline CreateDeadline(){var value=new SessionPresentationDeadline();value.Reset(DateTime.UtcNow);return value;}
    }
    sealed record BrowserSessionRow(BrowserSessionStatus Status)
    {public override string ToString()=>$"浏览器 {Status.Id.ToString()[..8]} · {Status.Message} · 已显示 {Status.PresentedFrames} 帧";}
}
