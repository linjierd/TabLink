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
    readonly ConcurrentDictionary<Guid,EncoderSelectionGeneration> browserEncoderGenerations=new();
    readonly ConcurrentDictionary<Guid,DisplaySessionReservation> browserCleanupReservations=new();
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
        var help=new Label{AutoSize=true,MaximumSize=new Size(820,0),ForeColor=muted,Text=BrowserRtcSession.IsSupported
            ?Ui("本地离线 HTTPS + WebRTC。首次使用需信任本机证书；当前只允许一台设备作为副屏。","Offline local HTTPS + WebRTC. Trust this computer's certificate on first use. Only one device can be the second screen.")
            :Ui("此公开发行包未包含浏览器 WebRTC 接收组件。Android 手机和平板请使用原生 APK，通过 Wi-Fi、USB 网络共享或 ADB 兼容模式连接。","This public build does not include the browser WebRTC receiver. On Android, use the native APK over Wi-Fi, USB tethering or ADB compatibility mode.")};
        var route=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=3,RowCount=1,Margin=new Padding(0,10,0,10)};
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));route.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        route.Controls.Add(new Label{Text="网络线路",AutoSize=true,Margin=new Padding(0,7,8,0)},0,0);route.Controls.Add(browserNetworks,1,0);route.Controls.Add(refreshBrowserNetworks,2,0);layout.Controls.Add(route,0,0);
        layout.Controls.Add(Flow(startBrowser,exportCa,newBrowserPair,copyBrowserUri),0,1);
        layout.Controls.Add(help,0,2);
        var pair=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=new Padding(0,8,0,8)};pair.Controls.Add(browserQr);pair.Controls.Add(browserHint);layout.Controls.Add(pair,0,3);
        layout.Controls.Add(browserSessions,0,4);layout.Controls.Add(Flow(stopBrowserDevice,stopBrowser),0,5);page.Controls.Add(layout);
        page.SizeChanged+=(_,_)=>
        {
            var width=Math.Max(280,page.ClientSize.Width-page.Padding.Horizontal-28);
            help.MaximumSize=new Size(width,0);browserHint.MaximumSize=new Size(Math.Max(240,width-browserQr.Width-28),0);
        };
        startBrowser.Click+=async(_,_)=>await GuardAsync(StartBrowserAsync);
        refreshBrowserNetworks.Click+=async(_,_)=>await GuardAsync(RefreshNetworksAsync);
        browserNetworks.SelectedIndexChanged+=(_,_)=>UpdateBrowserButtons(!busy&&!closing&&!exitStarting&&!updateExitStarted&&!stopping&&!connectionStarts.IsStarting);
        newBrowserPair.Click+=(_,_)=>{try{CreateBrowserPairing();}catch(Exception ex){ShowError(ex);}};
        stopBrowser.Click+=async(_,_)=>{SuppressTrustedNetworkAutoStart();await GuardAsync(StopBrowserAsync);};
        stopBrowserDevice.Click+=async(_,_)=>await GuardAsync(async()=>{if(browserSessions.SelectedItem is BrowserSessionRow row&&browserHost is {} host)await host.StopSessionAsync(row.Status.Id);});
        copyBrowserUri.Click+=(_,_)=>{if(browserUri is not null)try{Clipboard.SetText(browserUri);}catch(Exception ex){ShowError(ex);}};
        exportCa.Click+=(_,_)=>
        {
            if(browserHost is not {} host)return;
            using var save=new SaveFileDialog{FileName=Ui("TabLink-本机证书.cer","TabLink-computer-certificate.cer"),Filter=Ui("公开 CA 证书|*.cer","Public CA certificate|*.cer"),OverwritePrompt=true};
            if(save.ShowDialog(this)==DialogResult.OK)try{File.Copy(host.CaCertificatePath,save.FileName,true);Log(Ui("已导出本机公开证书。请传到接收设备安装，并核对指纹。","The computer's public certificate was exported. Install it on the receiving device and verify the fingerprint."));}catch(Exception ex){ShowError(ex);}
        };
        return page;
    }

    Task StartBrowserAsync()
    {
        SuppressTrustedNetworkAutoStart();
        return browserStartTask=RunConnectionStartAsync(ConnectionStartKind.Browser,StartBrowserCoreAsync);
    }
    async Task StartBrowserCoreAsync(ConnectionStartLease startLease)
    {
        startLease.ThrowIfNotCurrent();
        if(!BrowserRtcSession.IsSupported)throw new NotSupportedException("此公开发行包未包含浏览器 WebRTC 接收组件；请使用 Android 原生客户端。");
        if(browserHost is not null)return;
        if(HasAnySessions)throw new InvalidOperationException("TabLink 只允许一个副屏连接。请先停止当前原生连接。");
        var selected=browserNetworks.SelectedItem as NetworkInterfaceChoice??throw new IOException("请先选择可用的 Wi-Fi 或 USB 网络共享线路。");
        var available=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        startLease.ThrowIfNotCurrent();
        var choice=available.SingleOrDefault(x=>SameNetworkBinding(x,selected))??throw new IOException("所选线路已变化或被排除。");
        await EnsureOwnedDisplayCleanupBeforeNewConnectionAsync();
        startLease.ThrowIfNotCurrent();
        _=VideoPipeline.FindFfmpeg();
        browserChoice=choice;var allowTouch=touch.Checked;
        try
        {
            BeginConnectionHealth(ConnectionHealthPath.Browser,$"选择浏览器线路 {choice.InterfaceAlias} · {choice.LocalAddress}");
            browserRules.Add(await NetworkFirewall.OpenAsync(choice,lifetime.Token,27185));
            startLease.ThrowIfNotCurrent();
            foreach(var port in BrowserHost.MediaPorts)
            {
                browserRules.Add(await NetworkFirewall.OpenAsync(choice,lifetime.Token,port,"UDP"));
                startLease.ThrowIfNotCurrent();
            }
            lifetime.Token.ThrowIfCancellationRequested();
            startLease.ThrowIfNotCurrent();
            browserHost=new BrowserHost(choice.LocalAddress,(id,p,ct)=>PrepareBrowserAsync(id,p,allowTouch,ct),1);
            browserHost.Diagnostic+=(id,message)=>Log(Ui($"浏览器 {id.ToString()[..8]}：{message}",$"Browser {id.ToString()[..8]}: {RuntimeUi(message)}"));
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
                UpdateBrowserConnectionHealth(state);
            };
            await browserHost.StartAsync(lifetime.Token);
            startLease.ThrowIfNotCurrent();
            MarkHealthRouteReady($"{choice.InterfaceAlias} · {choice.LocalAddress}:27185 · 本地 HTTPS/WebRTC 已启动");
            MarkHealthAuthenticationStarted("等待浏览器使用当前单次二维码完成认证与屏幕参数上报");
            CreateBrowserPairing();SetStatus("浏览器接入已开启，等待设备扫码","Browser connection is ready; waiting for a device to scan");SetMetrics(
                $"{choice.InterfaceAlias} · 本地 HTTPS + WebRTC · 单设备",
                $"{choice.InterfaceAlias} · local HTTPS + WebRTC · one device");
        }
        catch(Exception ex){MarkConnectionHealthAttention(SafeError(ex));await StopBrowserAsync();throw;}
    }

    async Task<BrowserDisplaySession> PrepareBrowserAsync(Guid id,TabletDisplayProfile profile,bool allowTouch,CancellationToken ct)
    {
        DisplaySessionReservation? reservation=null;DesktopCapture? captureInput=null;ActiveDisplayPower? powerRequest=null;
        var encoderGeneration=BeginEncoderSelectionConnection();
        browserEncoderGenerations[id]=encoderGeneration;
        try
        {
            if(!browserCleanupReservations.IsEmpty)
                throw new IOException("上一浏览器副屏仍在等待精确回收；未准备新的浏览器副屏。");
            MarkHealthDisplayProfile(profile);
            MarkHealthDisplayPreparing("正在按浏览器请求模式准备唯一虚拟副屏");
            reservation=await DisplaySessionAllocator.Shared.AcquireAsync(id,profile,ct);
            captureInput=new DesktopCapture(reservation.CurrentDisplay,identity:reservation.Lease);powerRequest=new ActiveDisplayPower();
            MarkHealthDisplayReady(reservation.CurrentDisplay);
            MarkHealthPipelineStarting("正在启动浏览器兼容的视频流水线");
            var owned=new BrowserOwnedDisplay(reservation,captureInput,powerRequest,profile,encoderGeneration);browserDisplays[id]=owned;
            var token=ct;token.ThrowIfCancellationRequested();
            return new BrowserDisplaySession(c=>VideoPipeline.StreamAsync(owned.Reservation.CurrentDisplay,profile,c,Log,
                    owned.Reservation.Lease,browserCompatible:true,encoderOptions:CurrentEncoderOptions(),
                    encoderSelected:snapshot=>ReportEncoderSelection(encoderGeneration,snapshot)),
                allowTouch?owned.Input.Input:null,owned.Input.ReleaseInput,async()=>
                {
                    if(browserEncoderGenerations.TryRemove(id,out var currentGeneration))InvalidateEncoderSelection(currentGeneration);
                    else InvalidateEncoderSelection(owned.EncoderGeneration);
                    browserDisplays.TryRemove(id,out _);
                    try
                    {
                        try{owned.Input.Dispose();}catch(Exception inputError){Log(Ui("浏览器画面输入清理需要检查：","Browser video-input cleanup needs attention: ")+SafeError(inputError));}
                        await owned.Reservation.DisposeAsync();
                        browserCleanupReservations.TryRemove(id,out _);
                    }
                    catch(Exception ex)
                    {
                        browserCleanupReservations[id]=owned.Reservation;
                        var detail=OwnedDisplayCleanupFailureDetail(ex);MarkOwnedDisplayCleanupAttention(detail);Log(detail);
                        throw;
                    }
                    finally{try{owned.Power.Dispose();}catch(Exception powerError){Log(Ui("浏览器电源请求清理需要检查：","Browser power-request cleanup needs attention: ")+SafeError(powerError));}}
                });
        }
        catch(Exception ex)
        {
            if(browserEncoderGenerations.TryRemove(id,out var currentGeneration))InvalidateEncoderSelection(currentGeneration);
            else InvalidateEncoderSelection(encoderGeneration);
            MarkConnectionHealthAttention(SafeError(ex));
            Log(Ui("浏览器独立副屏准备失败：","Browser independent-display preparation failed: ")+SafeError(ex));
            browserDisplays.TryRemove(id,out _);
            try{captureInput?.Dispose();}catch(Exception inputError){Log(Ui("浏览器准备失败后的画面输入清理需要检查：","Video-input cleanup after browser preparation failure needs attention: ")+SafeError(inputError));}
            try{powerRequest?.Dispose();}catch(Exception powerError){Log(Ui("浏览器准备失败后的电源请求清理需要检查：","Power-request cleanup after browser preparation failure needs attention: ")+SafeError(powerError));}
            if(reservation is not null)
            {
                try{await reservation.DisposeAsync();browserCleanupReservations.TryRemove(id,out _);}
                catch(Exception cleanup)
                {
                    browserCleanupReservations[id]=reservation;
                    var detail=OwnedDisplayCleanupFailureDetail(cleanup);MarkOwnedDisplayCleanupAttention(detail);Log(detail);
                    throw new AggregateException("浏览器副屏准备失败，且本次拥有的副屏仍待精确回收。",ex,cleanup);
                }
            }
            throw;
        }
    }

    void UpdateBrowserConnectionHealth(BrowserSessionStatus state)
    {
        if(IsDisposed||Disposing)return;
        if(InvokeRequired)
        {
            if(IsHandleCreated)try{BeginInvoke(()=>UpdateBrowserConnectionHealth(state));}catch(InvalidOperationException)when(IsDisposed||Disposing){}
            return;
        }
        if(state.State=="closed")
        {
            ResetConnectionHealthScope();
            if(!browserCleanupReservations.IsEmpty)
            {
                MarkOwnedDisplayCleanupAttention("浏览器已断开；本次拥有的副屏仍在等待精确回收");
                RefreshConnectionHealthUi();return;
            }
            if(!healthAttempt.IsEmpty)connectionHealth.RestartFrom(healthAttempt,ConnectionHealthStage.AuthenticationAndDisplayProfile,"浏览器已断开，等待重新配对");
            RefreshConnectionHealthUi();return;
        }
        if(state.CapturePaused&&!healthCapturePaused)
        {
            healthCapturePaused=true;connectionHealth.PauseFrom(healthAttempt,ConnectionHealthStage.CaptureEncodeSend,state.Message);
        }
        else if(!state.CapturePaused&&healthCapturePaused)
        {
            healthCapturePaused=false;connectionHealth.ResumeFrom(healthAttempt,ConnectionHealthStage.CaptureEncodeSend,"浏览器画面正在恢复");
            healthPresentedFrames=state.PresentedFrames;
            healthSubmissionFresh=healthPresentationFresh=false;
        }
        if(state.State=="streaming"&&state.PresentedFrames==0)
            connectionHealth.FrameSent(healthAttempt,"浏览器媒体通道已建立，等待页面呈现确认");
        if(state.PresentedFrames>healthPresentedFrames)
        {
            healthPresentedFrames=state.PresentedFrames;
            connectionHealth.FramePresented(healthAttempt,state.PresentedFrames,$"浏览器已确认呈现 {state.PresentedFrames:N0} 帧");
            healthPresentationFresh=true;
        }
        RefreshConnectionHealthUi();
    }

    void CreateBrowserPairing()
    {
        var host=browserHost??throw new IOException("请先开启浏览器接入。");
        if(!browserCleanupReservations.IsEmpty)
            throw new IOException("上一浏览器副屏仍在等待精确回收；请先在连接记录中执行“回收本次拥有的副屏”。");
        if(healthAttempt.IsEmpty&&browserChoice is {} choice)
        {
            BeginConnectionHealth(ConnectionHealthPath.Browser,$"选择浏览器线路 {choice.InterfaceAlias} · {choice.LocalAddress}");
            MarkHealthRouteReady($"{choice.InterfaceAlias} · {choice.LocalAddress}:27185 · 本地 HTTPS/WebRTC 已启动");
            MarkHealthAuthenticationStarted("等待浏览器使用当前单次二维码完成认证与屏幕参数上报");
        }
        var offer=host.CreatePairing();browserUri=offer.Uri;
        browserQr.Image?.Dispose();browserQr.Image=MakeQr(offer.Uri);browserQr.Visible=true;
        SetBrowserHint($"1. 首次使用：导出并在设备信任本机证书。\n2. 扫码，在页面点击开始连接。\n\n二维码单次使用，到期 {offer.ExpiresUtc.LocalDateTime:HH:mm:ss}。\n需要重新配对时，请生成新的二维码。\n\n证书 SHA-256 指纹：\n{host.CaFingerprint}",$"1. First use: export and trust this computer's certificate on the device.\n2. Scan the QR code and select Connect display on the page.\n\nThe QR code is single-use and expires at {offer.ExpiresUtc.LocalDateTime:HH:mm:ss}.\nGenerate a new code to pair again.\n\nCertificate SHA-256 fingerprint:\n{host.CaFingerprint}");
        UpdateBrowserButtons(!busy&&!closing&&!exitStarting&&!updateExitStarted&&!connectionStarts.IsStarting);
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
        SetBrowserHint("本次二维码已使用。设备断开后，请点击“重新生成配对二维码”获取新的单次链接。","This QR code has been used. After the device disconnects, choose Generate a new pairing QR code for another single-use link.");
        UpdateBrowserButtons(!busy&&!closing&&!exitStarting&&!updateExitStarted&&!stopping&&!connectionStarts.IsStarting);
    }

    async Task MonitorBrowserAsync(IReadOnlyList<NetworkInterfaceChoice>? choices,InputDesktopStatus desktop)
    {
        var host=browserHost;if(host is null)return;
        if(choices is not null&&browserChoice is {} selected&&!choices.Any(x=>SameNetworkBinding(x,selected)))
        {Log(Ui("浏览器线路已断开或被排除，回收浏览器副屏。","The browser route disconnected or was excluded; reclaiming the browser display."));await StopBrowserAsync();return;}
        foreach(var item in browserDisplays.ToArray())
        {
            var current=item.Value;
            browserStates.TryGetValue(item.Key,out var state);
            var now=DateTime.UtcNow;var evaluation=current.Deadline.Evaluate(now,state?.LastPresentedUtc,state?.CapturePaused??false,desktop);
            if(state is {CapturePaused:false}&&healthPresentationFresh&&
                (state.LastPresentedUtc is null||state.LastPresentedUtc<=now-FrameServer.TelemetryFreshnessWindow))
            {
                connectionHealth.RestartFrom(healthAttempt,ConnectionHealthStage.PhysicalPresentation,"最近 5 秒没有新的浏览器呈现回调证据");
                healthPresentedFrames=state.PresentedFrames;healthPresentationFresh=false;RefreshConnectionHealthUi();
            }
            try
            {
                current.Reservation.Guard.Renew(evaluation.DeadlineUtc);
                // Cross-process renewal can block; enforce against a fresh
                // post-renew clock just like the USB and native paths.
                now=DateTime.UtcNow;
                if(now>evaluation.DeadlineUtc){Log(Ui("浏览器超过首帧或后续呈现期限，停止该设备。","The browser exceeded its first-frame or presentation deadline; stopping that device."));await host.StopSessionAsync(item.Key);}
            }
            catch(ObjectDisposedException){}
            catch(Exception ex)
            {
                Log(Ui("此浏览器副屏检查失败：","This browser-display check failed: ")+SafeError(ex));
                try{await host.StopSessionAsync(item.Key);}catch(Exception cleanup){Log(Ui("该浏览器回收需要检查：","This browser-display reclamation needs attention: ")+SafeError(cleanup));}
            }
        }
        var previous=(browserSessions.SelectedItem as BrowserSessionRow)?.Status.Id;
        browserSessions.BeginUpdate();browserSessions.Items.Clear();
        foreach(var state in browserStates.Values.OrderBy(x=>x.Id))browserSessions.Items.Add(new BrowserSessionRow(state,uiLanguage));
        foreach(var row in browserSessions.Items.OfType<BrowserSessionRow>())if(row.Status.Id==previous){browserSessions.SelectedItem=row;break;}
        if(browserSessions.SelectedIndex<0&&browserSessions.Items.Count>0)browserSessions.SelectedIndex=0;
        browserSessions.EndUpdate();
        UpdateBrowserButtons(!busy&&!closing&&!exitStarting&&!updateExitStarted&&!stopping&&!connectionStarts.IsStarting);
    }

    async Task StopBrowserAsync()
    {
        var host=browserHost;browserHost=null;
        foreach(var generation in browserEncoderGenerations.Values)InvalidateEncoderSelection(generation);
        try{if(host is not null)await host.DisposeAsync();}
        finally
        {
            foreach(var rule in browserRules.ToArray())try{await rule.DisposeAsync();browserRules.Remove(rule);}catch(Exception ex){Log(Ui("浏览器防火墙规则清理失败：","Browser firewall-rule cleanup failed: ")+SafeError(ex));}
            browserChoice=null;browserUri=null;browserStates.Clear();browserSessions.Items.Clear();browserQr.Image?.Dispose();browserQr.Image=null;browserQr.Visible=false;
            if(browserCleanupReservations.IsEmpty)SetBrowserHint("浏览器接入已关闭，副屏已回收。","Browser connection is disabled and its display was reclaimed.");
            else SetBrowserHint("浏览器接入已关闭，副屏精确回收待重试。","Browser connection is disabled; exact display reclamation will be retried.");
            if(!connectionHealth.Snapshot().Steps.Any(step=>step.State==ConnectionHealthState.Attention))StopConnectionHealth("浏览器接入已关闭并回收本次副屏");
            if(!HasAnySessions){ClearEncoderSelectionIfIdle();if(connectionMode.SelectedIndex==1){ShowStatus("浏览器接入尚未开启","Browser connection is not enabled");SetMetrics("本地 HTTPS + WebRTC · 单设备","Local HTTPS + WebRTC · one device");}}
            UpdateBrowserButtons(!busy&&!closing&&!exitStarting&&!updateExitStarted&&!connectionStarts.IsStarting);
            UpdateButtons();
        }
    }
    void UpdateBrowserButtons(bool ready)
    {
        browserNetworks.Enabled=refreshBrowserNetworks.Enabled=ready&&browserHost is null;
        startBrowser.Enabled=BrowserRtcSession.IsSupported&&ready&&!HasAnySessions&&browserHost is null&&browserNetworks.SelectedItem is NetworkInterfaceChoice;
        newBrowserPair.Enabled=ready&&browserHost is not null&&browserCleanupReservations.IsEmpty;
        exportCa.Enabled=ready&&browserHost is not null;
        copyBrowserUri.Enabled=ready&&browserHost is not null&&browserUri is not null&&browserCleanupReservations.IsEmpty;
        stopBrowser.Enabled=stopBrowserDevice.Enabled=ready&&browserHost is not null;
        browserSessions.Visible=browserSessions.Items.Count>0;
        stopBrowserDevice.Visible=browserSessions.Visible;stopBrowser.Visible=browserHost is not null;
    }
    sealed class BrowserOwnedDisplay(DisplaySessionReservation reservation,DesktopCapture input,ActiveDisplayPower power,
        TabletDisplayProfile profile,EncoderSelectionGeneration encoderGeneration)
    {
        internal DisplaySessionReservation Reservation {get;}=reservation;
        internal DesktopCapture Input {get;}=input;
        internal ActiveDisplayPower Power {get;}=power;
        internal TabletDisplayProfile Profile {get;}=profile;
        internal EncoderSelectionGeneration EncoderGeneration {get;}=encoderGeneration;
        internal SessionPresentationDeadline Deadline {get;}=CreateDeadline();
        static SessionPresentationDeadline CreateDeadline(){var value=new SessionPresentationDeadline();value.Reset(DateTime.UtcNow);return value;}
    }
    sealed record BrowserSessionRow(BrowserSessionStatus Status,ProductLanguage Language)
    {public override string ToString()=>Language==ProductLanguage.SimplifiedChinese
        ?$"浏览器 {Status.Id.ToString()[..8]} · {Status.Message} · 已显示 {Status.PresentedFrames} 帧"
        :$"Browser {Status.Id.ToString()[..8]} · {BrowserStatusText(Status,Language)} · presented {Status.PresentedFrames} frames";}

    internal static string BrowserStatusText(BrowserSessionStatus status,ProductLanguage language)
    {
        if(language==ProductLanguage.SimplifiedChinese)return status.Message;
        if(status.CapturePaused)return "Video paused; the connection is retained";
        return status.State switch
        {
            "connecting"=>"Establishing the encrypted connection",
            "preparing"=>"Preparing the independent display",
            "streaming" when status.PresentedFrames>0=>"Presenting the independent display",
            "streaming"=>"Connected; waiting for presentation confirmation",
            "paused"=>"Video paused; the connection is retained",
            "closed"=>"Disconnected",
            _=>"Connection state updated"
        };
    }
}
