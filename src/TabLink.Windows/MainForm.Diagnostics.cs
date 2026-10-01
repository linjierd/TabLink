using System.Diagnostics;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly TextBox diagnosticReport=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Top,Height=180};
    readonly ListView healthStages=new(){View=View.Details,FullRowSelect=true,GridLines=true,HideSelection=false,ShowItemToolTips=true,Dock=DockStyle.Top,Height=205};
    readonly Label healthSummary=new(){AutoSize=true,ForeColor=Color.FromArgb(90,107,128),Margin=new Padding(0,6,0,6)};
    readonly TextBox healthDetail=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Top,Height=62,Text="选择一个连接阶段可查看完整证据与修复建议。"};
    readonly Button diagnose=new(){Text="检测连接"};
    readonly Button repairAdb=new(){Text="修复：使用内置 ADB"};
    readonly Button repairConnection=new(){Text="重建连接"};
    readonly Button repairSuggested=new(){Text="修复所选问题"};
    readonly Button openDiagnosticFolder=new(){Text="日志目录"};
    readonly ComboBox requestedModes=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=310};
    readonly Button refreshModes=new(){Text="读取设备请求模式"};
    readonly Button repairMode=new(){Text="配置选中显示模式"};
    readonly Label modeHint=new(){AutoSize=true,ForeColor=Color.FromArgb(90,107,128),Margin=new Padding(0,7,0,0),Text="显示模式修复只处理设备上报的模式。"};
    readonly ConnectionHealthTracker connectionHealth=new();
    ConnectionHealthAttempt healthAttempt;
    Guid healthConnectionId;
    long healthSentFrames,healthSubmittedFrames,healthPresentedFrames;
    bool healthCapturePaused,healthSubmissionFresh,healthPresentationFresh;
    bool diagnosing;

    Control BuildDiagnosticsPanel()
    {
        repairAdb.Visible=Directory.Exists(Path.Combine(AppContext.BaseDirectory,"tools","platform-tools"));
        healthStages.Columns.Add("连接阶段",210);healthStages.Columns.Add("状态",90);healthStages.Columns.Add("当前证据与建议",520);
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(18),AutoScroll=true};
        var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=false,Height=730,ColumnCount=1,RowCount=8};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,205));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,62));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,180));
        var healthIntro=new Label{Dock=DockStyle.Top,AutoSize=true,ForeColor=muted,Text="按真实事件检查线路、认证、副屏、发送、客户端解码提交和呈现回调；呈现回调仍不等于物理面板测量。"};
        layout.Controls.Add(healthIntro,0,0);layout.Controls.Add(healthSummary,0,1);
        layout.Controls.Add(Flow(repairSuggested,diagnose,repairConnection,openDiagnosticFolder),0,2);
        layout.Controls.Add(healthStages,0,3);layout.Controls.Add(healthDetail,0,4);
        var modeTools=Flow(refreshModes,requestedModes,repairMode,modeHint);layout.Controls.Add(modeTools,0,5);
        layout.Controls.Add(Flow(repairAdb),0,6);layout.Controls.Add(diagnosticReport,0,7);page.Controls.Add(layout);
        diagnose.Click+=async(_,_)=>await DiagnoseAsync();
        repairAdb.Click+=async(_,_)=>await GuardAsync(RepairAdbAsync,adbOperation:true);
        repairConnection.Click+=async(_,_)=>await GuardAsync(RepairConnectionAsync);
        repairSuggested.Click+=async(_,_)=>await GuardAsync(RepairSelectedHealthStageAsync);
        refreshModes.Click+=(_,_)=>RefreshRequestedModes();
        repairMode.Click+=async(_,_)=>await GuardAsync(ConfigureDisplayModeFromUiAsync);
        requestedModes.SelectedIndexChanged+=(_,_)=>UpdateHealthRepairButton();
        openDiagnosticFolder.Click+=(_,_)=>OpenDiagnosticFolder();
        healthStages.SelectedIndexChanged+=(_,_)=>{UpdateHealthRepairButton();UpdateHealthDetail();};
        healthStages.SizeChanged+=(_,_)=>healthStages.Columns[2].Width=Math.Max(220,healthStages.ClientSize.Width-healthStages.Columns[0].Width-healthStages.Columns[1].Width-8);
        page.SizeChanged+=(_,_)=>(healthIntro.MaximumSize,healthSummary.MaximumSize,modeHint.MaximumSize)=(
            new Size(Math.Max(260,page.ClientSize.Width-page.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-8),0),
            new Size(Math.Max(260,page.ClientSize.Width-page.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-8),0),
            new Size(Math.Max(220,page.ClientSize.Width-page.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-8),0));
        RefreshConnectionHealthUi();
        return page;
    }

    void BeginConnectionHealth(ConnectionHealthPath path,string detail)
    {
        healthAttempt=connectionHealth.BeginAttempt(path,detail);
        ResetConnectionHealthScope();
        RefreshConnectionHealthUi();
    }

    void ResetConnectionHealthScope()
    {
        healthConnectionId=Guid.Empty;
        healthSentFrames=healthSubmittedFrames=healthPresentedFrames=0;
        healthCapturePaused=healthSubmissionFresh=healthPresentationFresh=false;
    }

    void MarkHealthRouteReady(string detail)
    {if(!healthAttempt.IsEmpty)connectionHealth.RouteAndListenerReady(healthAttempt,detail);RefreshConnectionHealthUi();}
    void MarkHealthAuthenticationStarted(string detail)
    {if(!healthAttempt.IsEmpty)connectionHealth.AuthenticationStarted(healthAttempt,detail);RefreshConnectionHealthUi();}
    void MarkHealthDisplayProfile(TabletDisplayProfile profile)
    {if(!healthAttempt.IsEmpty)connectionHealth.DisplayProfileReceived(healthAttempt,$"{profile.Width} × {profile.Height} @ {profile.RequestedRefreshRate} Hz");RefreshConnectionHealthUi();}
    void MarkHealthDisplayPreparing(string detail)
    {if(!healthAttempt.IsEmpty)connectionHealth.DisplayPreparing(healthAttempt,detail);RefreshConnectionHealthUi();}
    void MarkHealthDisplayReady(VirtualDisplayInfo display)
    {if(!healthAttempt.IsEmpty)connectionHealth.DisplayReady(healthAttempt,$"{display.Bounds.Width} × {display.Bounds.Height} · {display.DeviceName}");RefreshConnectionHealthUi();}
    void MarkHealthPipelineStarting(string detail)
    {if(!healthAttempt.IsEmpty)connectionHealth.PipelineStarting(healthAttempt,detail);RefreshConnectionHealthUi();}

    void StopConnectionHealth(string detail)
    {
        if(!healthAttempt.IsEmpty)connectionHealth.Stop(healthAttempt,detail);
        healthAttempt=default;ResetConnectionHealthScope();
        RefreshConnectionHealthUi();
    }

    void MarkConnectionHealthAttention(string detail)
    {
        if(healthAttempt.IsEmpty)return;
        var snapshot=connectionHealth.Snapshot();
        // Preserve strict owned-display cleanup when generic UI exception
        // handling runs afterward; its exact retry must not be replaced by a
        // mode change or broad connection repair.
        var displayCleanup=snapshot[ConnectionHealthStage.SingleVirtualDisplay];
        if(displayCleanup is {State:ConnectionHealthState.Attention,Recovery:ConnectionHealthRecovery.ReclaimOwnedDisplay})return;
        var step=snapshot.Steps.FirstOrDefault(item=>item.State!=ConnectionHealthState.Healthy)??snapshot.Steps[^1];
        var recovery=step.Stage switch
        {
            ConnectionHealthStage.RouteAndListener=>ConnectionHealthRecovery.RefreshRoute,
            ConnectionHealthStage.AuthenticationAndDisplayProfile=>ConnectionHealthRecovery.RecreatePairing,
            ConnectionHealthStage.SingleVirtualDisplay=>ConnectionHealthRecovery.ConfigureDisplayMode,
            _=>ConnectionHealthRecovery.RestartVideo
        };
        connectionHealth.NeedsAttention(healthAttempt,step.Stage,recovery,detail);
        RefreshConnectionHealthUi();
    }

    void MarkOwnedDisplayCleanupAttention(string detail)
    {
        if(healthAttempt.IsEmpty)return;
        connectionHealth.OwnedDisplayCleanupFailed(healthAttempt,detail);
        RefreshConnectionHealthUi();
    }

    void RefreshPrimaryConnectionHealth(FrameServer source)
    {
        if(healthAttempt.IsEmpty)return;
        var performance=source.SendPerformance;
        if(!source.ClientConnected)
        {
            // The server resets its connection-scoped counters on disconnect.
            // Clear our identity as the transition is observed so the 2-second
            // monitor cannot repeatedly restart the same health attempt.
            if(healthConnectionId!=Guid.Empty)
            {
                connectionHealth.RestartFrom(healthAttempt,ConnectionHealthStage.AuthenticationAndDisplayProfile,"客户端已断开，监听仍在等待重新认证");
                ResetConnectionHealthScope();
                RefreshConnectionHealthUi();
            }
            return;
        }
        if(source.ClientConnected&&performance.ConnectionId!=Guid.Empty&&performance.ConnectionId!=healthConnectionId)
        {
            if(healthConnectionId!=Guid.Empty)connectionHealth.RestartFrom(healthAttempt,ConnectionHealthStage.AuthenticationAndDisplayProfile,"客户端已重新连接，等待本次连接的新证据");
            healthConnectionId=performance.ConnectionId;
            healthSentFrames=healthSubmittedFrames=healthPresentedFrames=0;
            healthCapturePaused=healthSubmissionFresh=healthPresentationFresh=false;
        }
        if(source.ClientDisplayProfile is { } profile)
        {
            var state=connectionHealth.Snapshot()[ConnectionHealthStage.AuthenticationAndDisplayProfile];
            if(state.Reason!=ConnectionHealthReason.DisplayProfileReceived)MarkHealthDisplayProfile(profile);
        }
        if(source.CapturePaused&&!healthCapturePaused)
        {
            healthCapturePaused=true;
            connectionHealth.PauseFrom(healthAttempt,ConnectionHealthStage.CaptureEncodeSend,"安全桌面或采集恢复期间保留连接");
        }
        else if(!source.CapturePaused&&healthCapturePaused)
        {
            healthCapturePaused=false;
            connectionHealth.ResumeFrom(healthAttempt,ConnectionHealthStage.CaptureEncodeSend,"采集已恢复，等待新的发送、解码与呈现证据");
            healthSentFrames=performance.CompletedFrames;healthSubmittedFrames=source.SubmittedFrames;healthPresentedFrames=source.PresentedFrames;
            healthSubmissionFresh=healthPresentationFresh=false;
        }
        if(!source.CapturePaused&&performance.CompletedFrames>healthSentFrames)
        {
            healthSentFrames=performance.CompletedFrames;
            connectionHealth.FrameSent(healthAttempt,$"本次连接已发送 {performance.CompletedFrames:N0} 帧");
        }
        if(source.SubmittedFrames>healthSubmittedFrames)
        {
            healthSubmittedFrames=source.SubmittedFrames;
            connectionHealth.DecoderSubmitted(healthAttempt,source.SubmittedFrames,$"客户端已提交 {source.SubmittedFrames:N0} 帧到解码器");
            healthSubmissionFresh=true;
        }
        if(source.PresentedFrames>healthPresentedFrames)
        {
            healthPresentedFrames=source.PresentedFrames;
            connectionHealth.FramePresented(healthAttempt,source.PresentedFrames,$"收到 Surface 呈现回调 {source.PresentedFrames:N0} 帧");
            // A physical presentation is also current proof that decode completed,
            // while decoder submission alone can never prove presentation.
            healthSubmissionFresh=healthPresentationFresh=true;
        }
        if(!source.CapturePaused)
        {
            var presentationFresh=source.HasRecentPresentation;
            var submissionFresh=source.HasRecentSubmission||presentationFresh;
            if(healthSubmissionFresh&&!submissionFresh)
            {
                connectionHealth.RestartFrom(healthAttempt,ConnectionHealthStage.AndroidDecodeSubmission,"最近 5 秒没有新的解码提交或呈现回调证据");
                healthSubmittedFrames=source.SubmittedFrames;healthPresentedFrames=source.PresentedFrames;
                healthSubmissionFresh=healthPresentationFresh=false;
            }
            else if(healthPresentationFresh&&!presentationFresh)
            {
                connectionHealth.RestartFrom(healthAttempt,ConnectionHealthStage.PhysicalPresentation,"最近 5 秒没有新的客户端呈现回调证据");
                healthPresentedFrames=source.PresentedFrames;
                healthPresentationFresh=false;
            }
        }
        RefreshConnectionHealthUi();
    }

    void RefreshNativeSessionConnectionHealth(NativeNetworkSession session)
    {
        if(healthAttempt.IsEmpty||session.IsStopped||session.Server is not {} source)return;
        if(source.ClientConnected)
        {
            var snapshot=connectionHealth.Snapshot();
            if(session.Profile is { } profile&&snapshot[ConnectionHealthStage.AuthenticationAndDisplayProfile].Reason!=ConnectionHealthReason.DisplayProfileReceived)
                MarkHealthDisplayProfile(profile);
            snapshot=connectionHealth.Snapshot();
            if(session.CurrentDisplay is { } display&&snapshot[ConnectionHealthStage.SingleVirtualDisplay].State!=ConnectionHealthState.Healthy)
            {
                MarkHealthDisplayPreparing("正在按原生客户端报告的模式准备唯一虚拟副屏");
                MarkHealthDisplayReady(display);
            }
            snapshot=connectionHealth.Snapshot();
            if(session.CurrentDisplay is not null&&snapshot[ConnectionHealthStage.CaptureEncodeSend].State==ConnectionHealthState.Waiting)
                MarkHealthPipelineStarting("正在启动原生客户端的视频流水线");
        }
        RefreshPrimaryConnectionHealth(source);
    }

    void RefreshConnectionHealthUi()
    {
        if(IsDisposed||healthStages.IsDisposed)return;
        if(InvokeRequired)
        {
            if(IsHandleCreated)try{BeginInvoke(RefreshConnectionHealthUi);}catch(InvalidOperationException)when(IsDisposed||Disposing){}
            return;
        }
        var snapshot=connectionHealth.Snapshot();
        var selectedStage=healthStages.SelectedItems.Count==1&&healthStages.SelectedItems[0].Tag is ConnectionHealthStep selected?selected.Stage:(ConnectionHealthStage?)null;
        var topStage=healthStages.TopItem?.Tag is ConnectionHealthStep top?top.Stage:(ConnectionHealthStage?)null;
        var attentionStage=snapshot.Steps.FirstOrDefault(step=>step.State==ConnectionHealthState.Attention)?.Stage;
        var desiredSelection=attentionStage??selectedStage;
        ListViewItem? restoredTop=null;
        healthStages.BeginUpdate();
        try
        {
            healthStages.Items.Clear();
            foreach(var step in snapshot.Steps)
            {
                var detail=step.Detail??HealthReasonText(step.Reason);
                if(step.State==ConnectionHealthState.Attention&&step.Recovery!=ConnectionHealthRecovery.None)detail+=" · 建议："+HealthRecoveryText(step.Recovery);
                var item=new ListViewItem(HealthStageText(step.Stage)){Tag=step,ToolTipText=detail};
                item.SubItems.Add(HealthStateText(step.State));item.SubItems.Add(detail);
                item.ForeColor=step.State switch
                {
                    ConnectionHealthState.Healthy=>Color.FromArgb(20,120,75),
                    ConnectionHealthState.Attention=>Color.FromArgb(190,70,35),
                    ConnectionHealthState.Paused=>Color.FromArgb(155,105,20),
                    ConnectionHealthState.Working=>accent,
                    _=>muted
                };
                healthStages.Items.Add(item);
                if(desiredSelection==step.Stage){item.Selected=true;item.Focused=true;}
                if(topStage==step.Stage)restoredTop=item;
            }
        }
        finally{healthStages.EndUpdate();}
        if(restoredTop is not null)healthStages.TopItem=restoredTop;
        healthSummary.Text=snapshot.IsActive
            ?$"当前路径：{HealthPathText(snapshot.Path)} · 解码提交 {snapshot.SubmittedFrames:N0} · 呈现回调 {snapshot.PresentedFrames:N0}"
            :"当前没有活动连接；开始连接后将显示六阶段进度。";
        UpdateHealthRepairButton();
        UpdateHealthDetail();
    }

    void UpdateHealthRepairButton()
    {
        var step=healthStages.SelectedItems.Count==1?healthStages.SelectedItems[0].Tag as ConnectionHealthStep:null;
        var canRepair=step is {State:ConnectionHealthState.Attention,Recovery:not ConnectionHealthRecovery.None};
        repairSuggested.Enabled=!busy&&!stopping&&!closing&&canRepair;
        repairSuggested.Text=canRepair?"修复："+HealthRecoveryText(step!.Recovery):"修复所选问题";
        repairMode.Enabled=!busy&&!stopping&&!closing&&requestedModes.SelectedItem is RequestedMode;
        repairMode.Text=HasAnySessions?"停止当前连接并配置模式":"配置选中显示模式";
    }

    void UpdateHealthDetail()
    {
        var step=healthStages.SelectedItems.Count==1?healthStages.SelectedItems[0].Tag as ConnectionHealthStep:null;
        healthDetail.Text=step is null?"选择一个连接阶段可查看完整证据与修复建议。":
            $"{HealthStageText(step.Stage)} · {HealthStateText(step.State)}\r\n{step.Detail??HealthReasonText(step.Reason)}"+
            (step.State==ConnectionHealthState.Attention&&step.Recovery!=ConnectionHealthRecovery.None?"\r\n建议操作："+HealthRecoveryText(step.Recovery):"");
    }

    async Task RepairSelectedHealthStageAsync()
    {
        var step=healthStages.SelectedItems.Count==1?healthStages.SelectedItems[0].Tag as ConnectionHealthStep:null;
        switch(step?.Recovery??ConnectionHealthRecovery.None)
        {
            case ConnectionHealthRecovery.RefreshRoute:
                await RefreshNetworksAsync();await RefreshAsync();break;
            case ConnectionHealthRecovery.RecreatePairing:
            case ConnectionHealthRecovery.RestartVideo:
                await RepairConnectionAsync();break;
            case ConnectionHealthRecovery.ConfigureDisplayMode:
                await ConfigureDisplayModeFromUiAsync();break;
            case ConnectionHealthRecovery.ReclaimOwnedDisplay:
                await RetryOwnedDisplayCleanupAsync();break;
            case ConnectionHealthRecovery.OpenLogs:
                OpenDiagnosticFolder();break;
        }
    }

    async Task ConfigureDisplayModeFromUiAsync()
    {
        if(HasAnySessions)
        {
            Log("显示模式修复将停止当前连接，只配置设备上报的模式；完成后需要重新连接。");
            await StopAllAsync();
        }
        if(HasPendingOwnedDisplayCleanup||connectionHealth.Snapshot().Steps.Any(step=>step.Recovery==ConnectionHealthRecovery.ReclaimOwnedDisplay))
            throw new IOException("本次拥有的副屏尚未完成精确回收；已中止显示模式配置，请先执行“回收本次拥有的副屏”。");
        RefreshRequestedModes();
        await RepairDisplayModeAsync();
    }

    static string HealthStageText(ConnectionHealthStage value)=>value switch
    {
        ConnectionHealthStage.RouteAndListener=>"1. 线路与监听",
        ConnectionHealthStage.AuthenticationAndDisplayProfile=>"2. 认证与屏幕参数",
        ConnectionHealthStage.SingleVirtualDisplay=>"3. 唯一虚拟副屏",
        ConnectionHealthStage.CaptureEncodeSend=>"4. 捕获、编码与发送",
        ConnectionHealthStage.AndroidDecodeSubmission=>"5. 客户端解码提交",
        ConnectionHealthStage.PhysicalPresentation=>"6. 客户端呈现回调",
        _=>value.ToString()
    };
    static string HealthStateText(ConnectionHealthState value)=>value switch
    {ConnectionHealthState.Waiting=>"等待",ConnectionHealthState.Working=>"进行中",ConnectionHealthState.Healthy=>"正常",ConnectionHealthState.Paused=>"已暂停",ConnectionHealthState.Attention=>"需处理",_=>value.ToString()};
    static string HealthPathText(ConnectionHealthPath value)=>value switch
    {ConnectionHealthPath.NativeNetwork=>"Wi-Fi / USB 网络",ConnectionHealthPath.AdbCompatibility=>"USB 调试兼容",ConnectionHealthPath.Browser=>"浏览器",_=>"未选择"};
    static string HealthReasonText(ConnectionHealthReason value)=>value switch
    {ConnectionHealthReason.Idle=>"等待上一步",ConnectionHealthReason.Starting=>"正在启动",ConnectionHealthReason.Ready=>"已就绪",ConnectionHealthReason.Authenticating=>"等待客户端认证",ConnectionHealthReason.DisplayProfileReceived=>"已收到屏幕参数",ConnectionHealthReason.DisplayPreparing=>"正在准备副屏",ConnectionHealthReason.DisplayReady=>"副屏已就绪",ConnectionHealthReason.PipelineStarting=>"正在启动视频流水线",ConnectionHealthReason.FrameSent=>"电脑已发送画面",ConnectionHealthReason.CapturePaused=>"画面采集暂停",ConnectionHealthReason.DecodeSubmitted=>"已提交当前解码器",ConnectionHealthReason.FramePresented=>"收到 Surface 呈现回调",ConnectionHealthReason.Reconnecting=>"正在重连",ConnectionHealthReason.NeedsAttention=>"需要处理",ConnectionHealthReason.Stopped=>"连接已停止",_=>value.ToString()};
    static string HealthRecoveryText(ConnectionHealthRecovery value)=>value switch
    {ConnectionHealthRecovery.RefreshRoute=>"刷新线路",ConnectionHealthRecovery.RecreatePairing=>"重建当前连接",ConnectionHealthRecovery.ConfigureDisplayMode=>"配置设备请求模式",ConnectionHealthRecovery.ReclaimOwnedDisplay=>"回收本次拥有的副屏",ConnectionHealthRecovery.RestartVideo=>"重启视频连接",ConnectionHealthRecovery.OpenLogs=>"打开日志",_=>"无需操作"};

    static void OpenDiagnosticFolder()
    {
        Directory.CreateDirectory(Diagnostics.Folder);
        var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(Diagnostics.Folder);Process.Start(start);
    }

    void RefreshRequestedModes()
    {
        requestedModes.Items.Clear();
        var profiles=additionalSessions.Select(x=>x.RequestedProfile).Append(lastRequestedProfile).Where(x=>x is not null).Cast<TabletDisplayProfile>()
            .DistinctBy(p=>(p.Width,p.Height,p.RequestedRefreshRate));
        foreach(var profile in profiles)requestedModes.Items.Add(new RequestedMode(profile));
        if(requestedModes.Items.Count>0){requestedModes.SelectedIndex=0;modeHint.Text="已读取设备上报的请求模式；只会配置选中模式。";}
        else {modeHint.Text="尚未收到设备屏幕参数；请先完成一次认证或连接。";Log(modeHint.Text);}
        UpdateHealthRepairButton();
    }
    async Task RepairDisplayModeAsync()
    {
        if(HasAnySessions)throw new IOException("显示模式变更需要重新加载虚拟驱动。请先停止当前设备。");
        var profile=(requestedModes.SelectedItem as RequestedMode)?.Profile??throw new IOException("请读取并选择设备请求的显示模式。");
        var helper=VirtualDisplayManager.InstallerPath;
        var start=new ProcessStartInfo(helper){UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
        foreach(var arg in new[]{"--configure-display",profile.Width.ToString(),profile.Height.ToString(),profile.RequestedRefreshRate.ToString(),"--quiet"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("无法启动显示模式配置组件。");await process.WaitForExitAsync(lifetime.Token);
        if(process.ExitCode!=0)throw new IOException("显示模式配置失败，请查看 %LOCALAPPDATA%\\TabLink\\display-configure-result.json。");
        primaryTargetKey=null;Log("已添加设备所需显示模式，请重新扫码连接。");await RefreshAsync();
    }

    async Task DiagnoseAsync()
    {
        if(diagnosing||closing)return;diagnosing=true;diagnose.Enabled=false;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(22));
        var results=new List<ConnectionFinding>();
        try
        {
            if(!settingsValid)
            {
                results.Add(new("配置","需要处理","配置文件读取失败；必须先恢复原文件，检测和 ADB 路径修复不会覆盖排除规则："+store.Path));
                return;
            }
            var snapshot=new DevicePolicySettings{SchemaVersion=settings.SchemaVersion,AdbPath=settings.AdbPath,ExcludedDevices=[..settings.ExcludedDevices]};
            results=await Task.Run(async()=>
            {
                var found=results;
                try{DevicePolicy.ValidateSettings(snapshot);found.Add(new("配置","通过",$"已加载 {snapshot.ExcludedDevices.Count} 条排除规则。"));}
                catch(Exception ex){found.Add(new("配置","需要处理",SafeError(ex)+"；请修正配置文件，检测不会覆盖损坏的配置。"));return found;}
                var bundled=Path.Combine(AppContext.BaseDirectory,"tools","platform-tools","adb.exe");
                var bundleDirectory=Path.GetDirectoryName(bundled)!;
                var bundleProvided=Directory.Exists(bundleDirectory);
                if(bundleProvided)
                {
                    try
                    {
                        _=TrustedBundledAdb.StageAndGetVerifiedPath(bundled);
                        found.Add(new("内置 ADB","通过","三件套哈希与固定 Google r37 版本一致，并已复制到受保护目录。"));
                    }
                    catch(Exception ex) when(IsTrustedAdbFailure(ex))
                    {found.Add(new("内置 ADB","需要处理","随包三件套不完整或固定 SHA-256 不匹配；未执行这些文件。"));}
                }
                else found.Add(new("ADB 组件","信息","公开发行包不分发 Google Platform-Tools。免调试网络连接不需要 ADB；兼容模式可选择你从 Android 官方安装的 r37 platform-tools/adb.exe。"));
                string? adbLocation=null;
                try{adbLocation=TrustedBundledAdb.LocateStageAndGetVerifiedPath(snapshot.AdbPath);}
                catch(Exception ex) when(IsTrustedAdbFailure(ex))
                {found.Add(new("ADB 选择","需要处理","找到的 Android Platform-Tools 未通过固定 SHA-256 校验或无法写入受保护目录；未执行源文件。"));}
                if(adbLocation is null)found.Add(new("ADB 选择","需要处理",bundleProvided
                    ?"当前没有可执行的受保护 ADB 副本。内置组件完整时，可点击“修复：使用内置 ADB”。"
                    :"没有可信 ADB 副本。ADB 兼容模式请从 Android 官方页面安装 r37 后选择 adb.exe；Wi-Fi / USB 网络共享模式不需要 ADB。"));
                else
                {
                    var version=await new AdbProcessRunner().RunAsync(adbLocation,["version"],TimeSpan.FromSeconds(4),deadline.Token);
                    found.Add(new("ADB 执行",version.ExitCode==0?"通过":"需要处理",version.ExitCode==0
                        ?SafeAdbVersionSummary(version.StandardOutput)
                        :SafeErrorSummary.ForUser(new AdbCommandException(version.ExitCode,version.StandardError,version.StandardOutput))));
                }
                var usb=await UsbInventory.ReadAsync(deadline.Token);
                found.Add(new("Windows USB","信息",$"当前存在 {usb.Count} 个具有序列号的 USB 身份。"));
                if(adbLocation is not null)
                {
                    var diagnosticPolicy=new DevicePolicy(snapshot);
                    var client=new AdbClient(adbLocation,diagnosticPolicy,UsbInventory.ReadAsync);
                    var devices=await client.ListDevicesAsync(deadline.Token);
                    if(devices.Count==0)found.Add(new("安卓调试设备","信息","未发现 ADB 设备。免调试网络模式无需 ADB；调试模式需在安卓开启 USB 调试并允许此电脑。"));
                    for(var index=0;index<devices.Count;index++)
                    {
                        var device=devices[index];
                        var verdict=diagnosticPolicy.Evaluate(device,usb);
                        var detail=device.State switch{"unauthorized"=>"请在此设备解锁后允许电脑的 USB 调试授权。","offline"=>"设备 ADB 离线，请重插该设备数据线并重新授权。",_=>verdict.Allowed?"可在 USB 调试页选择并连接。":verdict.Reason};
                        found.Add(new($"安卓设备 {index+1}",verdict.Allowed?"通过":"需要处理",detail));
                    }
                }
                var networks=NetworkInterfaceCatalog.GetChoices(snapshot);
                found.Add(new("网络线路",networks.Count>0?"通过":"需要处理",networks.Count>0?string.Join("\r\n",networks.Select(x=>$"{x.InterfaceAlias} · {x.LocalAddress} · {x.Kind}")):"没有可用线路。请连接同一 Wi-Fi，或在设备开启 USB 网络共享；检查设备是否被排除。"));
                var targets=VirtualDisplayManager.GetTargets();
                found.Add(new("虚拟副屏",targets.Count<=1?"通过":"需要处理",targets.Count switch
                {
                    0 => "当前没有虚拟显示设备，这是断开状态的预期结果；设备认证后会按需安装。",
                    1 => $"检测到唯一虚拟显示目标，活动={targets[0].IsActive}。停止连接后应自动卸载。",
                    _ => $"检测到 {targets.Count} 个 TabLink 兼容目标；单屏策略要求最多一个，请停止连接并运行清理。"
                }));
                try{found.Add(new("H.264 编码器","通过",VideoPipeline.FindFfmpeg()));}catch(Exception ex){found.Add(new("H.264 编码器","需要处理",SafeError(ex)+"；请使用完整交付目录。"));}
                var apk=Path.Combine(AppContext.BaseDirectory,"android","TabLink.apk");
                found.Add(new("安卓客户端",File.Exists(apk)?"通过":"需要处理",File.Exists(apk)?"客户端随包提供；在 USB 调试页可安装到明确选中的安卓设备。":"缺少 android/TabLink.apk，请恢复完整交付目录。"));
                var conflicts=Process.GetProcessesByName("ExtensoDeskServer");
                found.Add(new("USB 冲突",conflicts.Length==0?"通过":"需要处理",conflicts.Length==0?"未发现 ExtensoDeskServer 进程。":"ExtensoDeskServer 正在运行，可能接管 USB 设备。请先退出它的服务再连接。"));foreach(var p in conflicts)p.Dispose();
                return found;
            },deadline.Token);
            if(server is {} primary)results.Add(new("主连接","信息",$"客户端连接={primary.ClientConnected}，电脑发送={primary.FramesSent} 帧，解码提交={primary.SubmittedFrames} 帧 / {primary.ClientSubmittedFps:F1} fps，呈现回调={primary.PresentedFrames} 帧 / {primary.ClientPresentedFps:F1} fps，采集暂停={primary.CapturePaused}。"));
            foreach(var s in additionalSessions.Where(x=>!x.IsStopped))results.Add(new("独立设备 "+s.Port,"信息",s.State));
            foreach(var s in browserStates.Values)results.Add(new("浏览器 "+s.Id.ToString()[..8],"信息",s.Message));
            Diagnostics.Save("connection-diagnosis.json",()=>new{timestamp=DateTimeOffset.Now,findings=results},Log);
        }
        catch(OperationCanceledException){results.Add(new("检测","未完成","检测已超时或取消。已有连接继续运行，可重试检测。"));}
        catch(Exception ex){results.Add(new("检测","需要处理",SafeError(ex)));}
        finally
        {
            diagnosticReport.Text=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"\r\n\r\n"+string.Join("\r\n\r\n",results.Select(x=>$"[{x.State}] {x.Check}\r\n{x.Detail}"));
            diagnosing=false;diagnose.Enabled=!closing;
        }
    }

    static bool IsTrustedAdbFailure(Exception ex)=>TrustedBundledAdb.IsTrustStorageFailure(ex);
    static string SafeAdbVersionSummary(string output)
    {
        var lines=output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(line=>line.StartsWith("Android Debug Bridge version ",StringComparison.Ordinal)||
                line.StartsWith("Version ",StringComparison.Ordinal)).Take(2).ToArray();
        return lines.Length==0?"受保护 ADB 的 version 命令执行成功；诊断未记录原始路径。":string.Join("\r\n",lines);
    }
    async Task RepairAdbAsync()
    {
        if(!settingsValid)throw new IOException("配置文件尚未成功读取。请先恢复原配置，不能用 ADB 路径修复覆盖排除规则："+store.Path);
        var bundled=Path.Combine(AppContext.BaseDirectory,"tools","platform-tools","adb.exe");
        if(!File.Exists(bundled))throw new IOException("完整交付目录中没有内置 Android Platform-Tools；Wi-Fi 模式仍可使用。");
        _=TrustedBundledAdb.StageAndGetVerifiedPath(bundled);
        // ADB approval fingerprints include settings. Clean our own reverse
        // before changing the path; unrelated network sessions continue.
        if(approved is not null)await StopAsync();
        var next=new DevicePolicySettings{SchemaVersion=settings.SchemaVersion,AdbPath=bundled,ExcludedDevices=[..settings.ExcludedDevices]};
        store.Save(next);settings.AdbPath=bundled;LocateAdb();
        Log("已修复 ADB 路径，使用校验通过的内置组件；未重启共享 ADB 服务。");await RefreshAsync();
    }
    async Task RepairConnectionAsync()
    {
        if(sessionList.SelectedItem is NativeSessionRow row&&!row.Session.IsStopped)
        {row.Session.Reconnect();Log("正在重建选中设备的视频连接，其他设备继续运行。");return;}
        if(browserHost is {} host)
        {
            if(browserSessions.SelectedItem is BrowserSessionRow browser)await host.StopSessionAsync(browser.Status.Id);
            CreateBrowserPairing();Log("已为浏览器接入生成新的单次配对二维码。");return;
        }
        if(networkChoice is not null&&server is {} running){running.RequestReconnect();Log("已请求主网络会话重新协商屏幕和编码器。");return;}
        if(approved is {} target)
        {
            var serial=target.Serial;await StopAsync();await RefreshAsync();
            devices.SelectedItem=devices.Items.OfType<DeviceChoice>().SingleOrDefault(x=>x.Device.Serial==serial)??throw new IOException("原设备未就绪；请查看检测结果并重新授权。");
            await ConnectAsync();return;
        }
        await RefreshNetworksAsync();await RefreshAsync();
        Log("已刷新设备与线路。请回到连接页选择连接方式和目标设备；浏览器接入可重新生成配对二维码。");
    }
    sealed record ConnectionFinding(string Check,string State,string Detail);
    sealed record RequestedMode(TabletDisplayProfile Profile)
    {public override string ToString()=>$"{Profile.Width} × {Profile.Height} · {Profile.RequestedRefreshRate} Hz（设备报告）";}
}
