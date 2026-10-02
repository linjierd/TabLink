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
    readonly Button exportSupportBundle=new(){Text="导出脱敏支持包"};
    readonly ComboBox requestedModes=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=310};
    readonly Button refreshModes=new(){Text="读取设备请求模式"};
    readonly Button repairMode=new(){Text="配置选中显示模式"};
    readonly Label modeHint=new(){AutoSize=true,ForeColor=Color.FromArgb(90,107,128),Margin=new Padding(0,7,0,0),Text="显示模式修复只处理设备上报的模式。"};
    readonly ConnectionHealthTracker connectionHealth=new();
    ConnectionHealthAttempt healthAttempt;
    Guid healthConnectionId;
    long healthSentFrames,healthSubmittedFrames,healthPresentedFrames;
    bool healthCapturePaused,healthSubmissionFresh,healthPresentationFresh;
    bool diagnosing,exportingSupportBundle;
    ProductLanguage? diagnosticReportLanguage;
    bool diagnosticReportNeedsRerun;

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
        var diagnosticTools=Flow(repairSuggested,diagnose,repairConnection,openDiagnosticFolder,exportSupportBundle);
        layout.Controls.Add(diagnosticTools,0,2);
        layout.Controls.Add(healthStages,0,3);layout.Controls.Add(healthDetail,0,4);
        var modeTools=Flow(refreshModes,requestedModes,repairMode,modeHint);layout.Controls.Add(modeTools,0,5);
        var adbTools=Flow(repairAdb);layout.Controls.Add(adbTools,0,6);layout.Controls.Add(diagnosticReport,0,7);page.Controls.Add(layout);
        diagnose.Click+=async(_,_)=>await DiagnoseAsync();
        repairAdb.Click+=async(_,_)=>await GuardAsync(RepairAdbAsync,adbOperation:true);
        repairConnection.Click+=async(_,_)=>await GuardAsync(RepairConnectionAsync);
        repairSuggested.Click+=async(_,_)=>await GuardAsync(RepairSelectedHealthStageAsync);
        refreshModes.Click+=(_,_)=>RefreshRequestedModes();
        repairMode.Click+=async(_,_)=>await GuardAsync(ConfigureDisplayModeFromUiAsync);
        requestedModes.SelectedIndexChanged+=(_,_)=>UpdateHealthRepairButton();
        openDiagnosticFolder.Click+=(_,_)=>OpenDiagnosticFolder();
        exportSupportBundle.Click+=async(_,_)=>await ExportSupportBundleFromUiAsync();
        healthStages.SelectedIndexChanged+=(_,_)=>{UpdateHealthRepairButton();UpdateHealthDetail();};
        healthStages.SizeChanged+=(_,_)=>ResizeHealthColumns();
        page.SizeChanged+=(_,_)=>
        {
            var contentWidth=Math.Max(260,page.ClientSize.Width-page.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-8);
            healthIntro.MaximumSize=healthSummary.MaximumSize=new Size(contentWidth,0);
            modeHint.MaximumSize=new Size(Math.Max(220,contentWidth),0);
            diagnosticTools.MaximumSize=modeTools.MaximumSize=adbTools.MaximumSize=new Size(contentWidth,0);
        };
        RefreshConnectionHealthUi();
        return page;
    }

    void ResizeHealthColumns()
    {
        if(healthStages.IsDisposed||healthStages.Disposing||healthStages.Columns.Count<3)return;
        healthStages.Columns[2].Width=Math.Max(220,
            healthStages.ClientSize.Width-healthStages.Columns[0].Width-healthStages.Columns[1].Width-8);
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
                var detail=step.Detail is null?HealthReasonText(step.Reason):WindowsUiText.TranslateHealthDetail(step.Detail,uiLanguage);
                if(step.State==ConnectionHealthState.Attention&&step.Recovery!=ConnectionHealthRecovery.None)detail+=Ui(" · 建议："," · Suggested action: ")+HealthRecoveryText(step.Recovery);
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
            ?Ui($"当前路径：{HealthPathText(snapshot.Path)} · 解码提交 {snapshot.SubmittedFrames:N0} · 呈现回调 {snapshot.PresentedFrames:N0}",$"Current path: {HealthPathText(snapshot.Path)} · decode submissions {snapshot.SubmittedFrames:N0} · presentation callbacks {snapshot.PresentedFrames:N0}")
            :Ui("当前没有活动连接；开始连接后将显示六阶段进度。","There is no active connection. Six-stage progress appears after a connection starts.");
        UpdateHealthRepairButton();
        UpdateHealthDetail();
    }

    void UpdateHealthRepairButton()
    {
        var step=healthStages.SelectedItems.Count==1?healthStages.SelectedItems[0].Tag as ConnectionHealthStep:null;
        var canRepair=step is {State:ConnectionHealthState.Attention,Recovery:not ConnectionHealthRecovery.None};
        repairSuggested.Enabled=!busy&&!stopping&&!closing&&!exitStarting&&!updateExitStarted&&canRepair;
        repairSuggested.Text=canRepair?Ui("修复：","Repair: ")+HealthRecoveryText(step!.Recovery):Ui("修复所选问题","Repair selected issue");
        repairMode.Enabled=!busy&&!stopping&&!closing&&!exitStarting&&!updateExitStarted&&requestedModes.SelectedItem is RequestedMode;
        repairMode.Text=HasAnySessions?Ui("停止当前连接并配置模式","Stop current connection and configure mode"):Ui("配置选中显示模式","Configure selected display mode");
    }

    void UpdateHealthDetail()
    {
        var step=healthStages.SelectedItems.Count==1?healthStages.SelectedItems[0].Tag as ConnectionHealthStep:null;
        healthDetail.Text=step is null?Ui("选择一个连接阶段可查看完整证据与修复建议。","Select a connection stage to view its evidence and suggested repair."):
            $"{HealthStageText(step.Stage)} · {HealthStateText(step.State)}\r\n{(step.Detail is null?HealthReasonText(step.Reason):WindowsUiText.TranslateHealthDetail(step.Detail,uiLanguage))}"+
            (step.State==ConnectionHealthState.Attention&&step.Recovery!=ConnectionHealthRecovery.None?Ui("\r\n建议操作：","\r\nSuggested action: ")+HealthRecoveryText(step.Recovery):"");
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
        Log(Ui("显示模式修复将停止当前连接，只配置设备上报的模式；完成后需要重新连接。",
            "Display-mode repair will stop the current connection and configure only a device-reported mode. Reconnect when it finishes."));
            await StopAllAsync();
        }
        if(HasPendingOwnedDisplayCleanup||connectionHealth.Snapshot().Steps.Any(step=>step.Recovery==ConnectionHealthRecovery.ReclaimOwnedDisplay))
            throw new IOException("本次拥有的副屏尚未完成精确回收；已中止显示模式配置，请先执行“回收本次拥有的副屏”。");
        RefreshRequestedModes();
        await RepairDisplayModeAsync();
    }

    string HealthStageText(ConnectionHealthStage value)=>value switch
    {
        ConnectionHealthStage.RouteAndListener=>Ui("1. 线路与监听","1. Route and listener"),
        ConnectionHealthStage.AuthenticationAndDisplayProfile=>Ui("2. 认证与屏幕参数","2. Authentication and display profile"),
        ConnectionHealthStage.SingleVirtualDisplay=>Ui("3. 唯一虚拟副屏","3. Single virtual display"),
        ConnectionHealthStage.CaptureEncodeSend=>Ui("4. 捕获、编码与发送","4. Capture, encode and send"),
        ConnectionHealthStage.AndroidDecodeSubmission=>Ui("5. 客户端解码提交","5. Client decode submission"),
        ConnectionHealthStage.PhysicalPresentation=>Ui("6. 客户端呈现回调","6. Client presentation callback"),
        _=>value.ToString()
    };
    string HealthStateText(ConnectionHealthState value)=>value switch
    {ConnectionHealthState.Waiting=>Ui("等待","Waiting"),ConnectionHealthState.Working=>Ui("进行中","Working"),ConnectionHealthState.Healthy=>Ui("正常","Healthy"),ConnectionHealthState.Paused=>Ui("已暂停","Paused"),ConnectionHealthState.Attention=>Ui("需处理","Needs attention"),_=>value.ToString()};
    string HealthPathText(ConnectionHealthPath value)=>value switch
    {ConnectionHealthPath.NativeNetwork=>Ui("Wi-Fi / USB 网络","Wi-Fi / USB network"),ConnectionHealthPath.AdbCompatibility=>Ui("USB 调试兼容","USB debugging compatibility"),ConnectionHealthPath.Browser=>Ui("浏览器","Browser"),_=>Ui("未选择","Not selected")};
    string HealthReasonText(ConnectionHealthReason value)=>value switch
    {ConnectionHealthReason.Idle=>Ui("等待上一步","Waiting for previous stage"),ConnectionHealthReason.Starting=>Ui("正在启动","Starting"),ConnectionHealthReason.Ready=>Ui("已就绪","Ready"),ConnectionHealthReason.Authenticating=>Ui("等待客户端认证","Waiting for client authentication"),ConnectionHealthReason.DisplayProfileReceived=>Ui("已收到屏幕参数","Display profile received"),ConnectionHealthReason.DisplayPreparing=>Ui("正在准备副屏","Preparing display"),ConnectionHealthReason.DisplayReady=>Ui("副屏已就绪","Display ready"),ConnectionHealthReason.PipelineStarting=>Ui("正在启动视频流水线","Starting video pipeline"),ConnectionHealthReason.FrameSent=>Ui("电脑已发送画面","Computer sent video"),ConnectionHealthReason.CapturePaused=>Ui("画面采集暂停","Capture paused"),ConnectionHealthReason.DecodeSubmitted=>Ui("已提交当前解码器","Submitted to decoder"),ConnectionHealthReason.FramePresented=>Ui("收到 Surface 呈现回调","Presentation callback received"),ConnectionHealthReason.Reconnecting=>Ui("正在重连","Reconnecting"),ConnectionHealthReason.NeedsAttention=>Ui("需要处理","Needs attention"),ConnectionHealthReason.Stopped=>Ui("连接已停止","Connection stopped"),_=>value.ToString()};
    string HealthRecoveryText(ConnectionHealthRecovery value)=>value switch
    {ConnectionHealthRecovery.RefreshRoute=>Ui("刷新线路","Refresh route"),ConnectionHealthRecovery.RecreatePairing=>Ui("重建当前连接","Rebuild current connection"),ConnectionHealthRecovery.ConfigureDisplayMode=>Ui("配置设备请求模式","Configure device-requested mode"),ConnectionHealthRecovery.ReclaimOwnedDisplay=>Ui("回收本次拥有的副屏","Reclaim this session's display"),ConnectionHealthRecovery.RestartVideo=>Ui("重启视频连接","Restart video connection"),ConnectionHealthRecovery.OpenLogs=>Ui("打开日志","Open logs"),_=>Ui("无需操作","No action")};

    static void OpenDiagnosticFolder()
    {
        Directory.CreateDirectory(Diagnostics.Folder);
        var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(Diagnostics.Folder);Process.Start(start);
    }

    async Task ExportSupportBundleFromUiAsync()
    {
        if(exportingSupportBundle||closing)return;
        exportingSupportBundle=true;exportSupportBundle.Enabled=false;
        try{await ExportSupportBundleAsync();}
        catch(Exception error)
        {
            var summary=SupportBundleExporter.FailureSummary(error);
            Log(Ui("脱敏支持包导出失败。","Redacted support-bundle export failed. ")+summary);
            if(!IsDisposed&&!Disposing&&!closing)MessageBox.Show(this,Ui("脱敏支持包导出失败：\r\n","Redacted support-bundle export failed:\r\n")+summary,"TabLink",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        finally
        {
            exportingSupportBundle=false;
            if(!IsDisposed)exportSupportBundle.Enabled=!busy&&!stopping&&!closing&&!exitStarting&&!updateExitStarted;
        }
    }

    async Task ExportSupportBundleAsync()
    {
        var prepared=SupportBundleExporter.Prepare(CreateSupportBundleSnapshot());
        if(ShowSupportBundlePreview(prepared.PreviewText)!=DialogResult.OK)return;
        using var dialog=new SaveFileDialog
        {
            Title=Ui("保存 TabLink 脱敏支持包","Save TabLink redacted support bundle"),
            Filter=Ui("ZIP 支持包 (*.zip)|*.zip","ZIP support bundle (*.zip)|*.zip"),
            DefaultExt="zip",
            AddExtension=true,
            OverwritePrompt=true,
            FileName="TabLink-Support-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".zip"
        };
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        await Task.Run(()=>SupportBundleExporter.WriteAtomic(dialog.FileName,prepared));
            Log(Ui("已导出脱敏支持包。没有复制原始日志、身份、路径、地址、配对材料或桌面内容。",
                "The redacted support bundle was exported without raw logs, identities, paths, addresses, pairing material or desktop content."));
        if(!IsDisposed&&!Disposing&&!closing)
            MessageBox.Show(this,Ui("脱敏支持包已保存到：\r\n","Redacted support bundle saved to:\r\n")+dialog.FileName+Ui("\r\n\r\n上传到公开 Issue 前仍建议再打开检查一次。","\r\n\r\nReview it once more before attaching it to a public issue."),
                "TabLink",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }

    SupportBundleSnapshot CreateSupportBundleSnapshot()
    {
        var health=connectionHealth.Snapshot();
        var nativeSources=new List<(FrameServer Source,SupportNativeCandidate Candidate)>();
        if(server is { } primary)
        {
            var connected=primary.ClientConnected;
            nativeSources.Add((primary,SupportBundleExporter.CreateNativeCandidate(connected,
                primary.ClientDisplayProfile is { } reported?ToSupportDisplay(reported):null,
                tabletProfile is { } prepared?ToSupportDisplay(prepared):null)));
        }
        foreach(var session in additionalSessions.Where(session=>!session.IsStopped))
        {
            if(session.Server is not { } additional)continue;
            var connected=additional.ClientConnected;
            nativeSources.Add((additional,SupportBundleExporter.CreateNativeCandidate(connected,
                additional.ClientDisplayProfile is { } reported?ToSupportDisplay(reported):null,
                session.Profile is { } prepared?ToSupportDisplay(prepared):null)));
        }
        var browserState=browserStates.Values.FirstOrDefault(state=>
            (state.State is "streaming" or "paused")&&browserDisplays.ContainsKey(state.Id));
        SupportDisplayProfile? browserDisplay=browserState is not null&&browserDisplays.TryGetValue(browserState.Id,out var ownedBrowser)
            ? ToSupportDisplay(ownedBrowser.Profile):null;
        var current=SupportBundleExporter.SelectCurrentConnection(nativeSources.Select(item=>item.Candidate).ToArray(),
            browserState is not null,browserDisplay);
        var source=current.NativeSourceIndex>=0?nativeSources[current.NativeSourceIndex].Source:null;
        SupportVideoStatus? video=null;
        if(source is not null&&current.Display is not null&&encoderRuntime is {} runtime)
        {
            video=new(SupportBundleExporter.MapEncoderBackend(runtime.Backend),runtime.Hardware,runtime.Width,runtime.Height,
                runtime.RequestedFps,runtime.EffectiveFps,
                source.CapturePaused,Math.Max(0,source.FramesSent),Math.Max(0,source.SubmittedFrames),
                Math.Max(0,source.PresentedFrames),SupportBundleExporter.NormalizeRate(source.ClientSubmittedFps),
                SupportBundleExporter.NormalizeRate(source.ClientPresentedFps));
        }
        return new(DateTimeOffset.UtcNow,
            typeof(MainForm).Assembly.GetName().Version??new Version(0,0,0),
            Environment.OSVersion.Version,Environment.Version,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
            MapSupportQuality(selectedQuality),MapSupportEncoderPreference(selectedEncoder),allowSoftwareFallback,
            current.ClientConnected,MapSupportPath(health.Path),current.Display,video,
            health.Steps.Select(step=>new SupportHealthStep(MapSupportStage(step.Stage),MapSupportState(step.State),
                MapSupportReason(step.Reason),MapSupportRecovery(step.Recovery))).ToArray());
    }

    DialogResult ShowSupportBundlePreview(string preview)
    {
        using var previewFont=new Font(FontFamily.GenericMonospace,9);
        using var window=new Form
        {
            Text=Ui("TabLink · 脱敏支持包预览","TabLink · Redacted support-bundle preview"),StartPosition=FormStartPosition.CenterParent,
            Size=new Size(820,640),MinimumSize=new Size(620,460),ShowInTaskbar=false,
            Font=Font,BackColor=Color.White,ForeColor=ink
        };
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=1,RowCount=3};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var explanation=new Label
        {
            AutoSize=true,Dock=DockStyle.Fill,MaximumSize=new Size(760,0),Margin=new Padding(0,0,0,10),
            Text=Ui("下面是 ZIP 中将保存的全部文本内容。程序不会上传文件，也不会读取原始日志、配置、信任库、设备身份或桌面画面。请检查后再保存。","Below is all text that will be saved in the ZIP. TabLink does not upload files or read raw logs, settings, trust stores, device identity or desktop video. Review it before saving.")
        };
        var content=new TextBox
        {
            Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,
            Text=preview,Font=previewFont,BackColor=Color.White
        };
        var save=new Button{Text=Ui("保存本地 ZIP","Save local ZIP"),AutoSize=true,DialogResult=DialogResult.OK};
        var cancel=new Button{Text=Ui("取消","Cancel"),AutoSize=true,DialogResult=DialogResult.Cancel};
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=new Padding(0,10,0,0)};
        actions.Controls.Add(save);actions.Controls.Add(cancel);
        layout.Controls.Add(explanation,0,0);layout.Controls.Add(content,0,1);layout.Controls.Add(actions,0,2);
        window.Controls.Add(layout);window.AcceptButton=save;window.CancelButton=cancel;
        return window.ShowDialog(this);
    }

    static SupportDisplayProfile ToSupportDisplay(TabletDisplayProfile value)=>new(value.Width,value.Height,value.NativeWidth,
        value.NativeHeight,value.Rotation,value.RequestedRefreshRate);
    static SupportConnectionPath MapSupportPath(ConnectionHealthPath value)=>value switch
    {
        ConnectionHealthPath.None=>SupportConnectionPath.None,
        ConnectionHealthPath.NativeNetwork=>SupportConnectionPath.NativeNetwork,
        ConnectionHealthPath.AdbCompatibility=>SupportConnectionPath.AdbCompatibility,
        ConnectionHealthPath.Browser=>SupportConnectionPath.Browser,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };
    static SupportQualityPreset MapSupportQuality(VideoQualityPreset value)=>value switch
    {
        VideoQualityPreset.Automatic=>SupportQualityPreset.Automatic,
        VideoQualityPreset.LowLatency=>SupportQualityPreset.LowLatency,
        VideoQualityPreset.Balanced=>SupportQualityPreset.Balanced,
        VideoQualityPreset.HighQuality=>SupportQualityPreset.HighQuality,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };
    static SupportEncoderPreference MapSupportEncoderPreference(VideoEncoderPreference value)=>value switch
    {
        VideoEncoderPreference.Automatic=>SupportEncoderPreference.Automatic,
        VideoEncoderPreference.Nvenc=>SupportEncoderPreference.Nvenc,
        VideoEncoderPreference.Qsv=>SupportEncoderPreference.Qsv,
        VideoEncoderPreference.Amf=>SupportEncoderPreference.Amf,
        VideoEncoderPreference.LibX264=>SupportEncoderPreference.LibX264,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };
    static SupportHealthStage MapSupportStage(ConnectionHealthStage value)=>value switch
    {
        ConnectionHealthStage.RouteAndListener=>SupportHealthStage.RouteAndListener,
        ConnectionHealthStage.AuthenticationAndDisplayProfile=>SupportHealthStage.AuthenticationAndDisplayProfile,
        ConnectionHealthStage.SingleVirtualDisplay=>SupportHealthStage.SingleVirtualDisplay,
        ConnectionHealthStage.CaptureEncodeSend=>SupportHealthStage.CaptureEncodeSend,
        ConnectionHealthStage.AndroidDecodeSubmission=>SupportHealthStage.ClientDecodeSubmission,
        ConnectionHealthStage.PhysicalPresentation=>SupportHealthStage.ClientPresentationCallback,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };
    static SupportHealthState MapSupportState(ConnectionHealthState value)=>value switch
    {
        ConnectionHealthState.Waiting=>SupportHealthState.Waiting,
        ConnectionHealthState.Working=>SupportHealthState.Working,
        ConnectionHealthState.Healthy=>SupportHealthState.Healthy,
        ConnectionHealthState.Paused=>SupportHealthState.Paused,
        ConnectionHealthState.Attention=>SupportHealthState.Attention,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };
    static SupportHealthReason MapSupportReason(ConnectionHealthReason value)=>value switch
    {
        ConnectionHealthReason.Idle=>SupportHealthReason.Idle,
        ConnectionHealthReason.Starting=>SupportHealthReason.Starting,
        ConnectionHealthReason.Ready=>SupportHealthReason.Ready,
        ConnectionHealthReason.Authenticating=>SupportHealthReason.Authenticating,
        ConnectionHealthReason.DisplayProfileReceived=>SupportHealthReason.DisplayProfileReceived,
        ConnectionHealthReason.DisplayPreparing=>SupportHealthReason.DisplayPreparing,
        ConnectionHealthReason.DisplayReady=>SupportHealthReason.DisplayReady,
        ConnectionHealthReason.PipelineStarting=>SupportHealthReason.PipelineStarting,
        ConnectionHealthReason.FrameSent=>SupportHealthReason.FrameSent,
        ConnectionHealthReason.CapturePaused=>SupportHealthReason.CapturePaused,
        ConnectionHealthReason.DecodeSubmitted=>SupportHealthReason.DecodeSubmitted,
        ConnectionHealthReason.FramePresented=>SupportHealthReason.FramePresented,
        ConnectionHealthReason.Reconnecting=>SupportHealthReason.Reconnecting,
        ConnectionHealthReason.NeedsAttention=>SupportHealthReason.NeedsAttention,
        ConnectionHealthReason.Stopped=>SupportHealthReason.Stopped,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };
    static SupportHealthRecovery MapSupportRecovery(ConnectionHealthRecovery value)=>value switch
    {
        ConnectionHealthRecovery.None=>SupportHealthRecovery.None,
        ConnectionHealthRecovery.RefreshRoute=>SupportHealthRecovery.RefreshRoute,
        ConnectionHealthRecovery.RecreatePairing=>SupportHealthRecovery.RecreatePairing,
        ConnectionHealthRecovery.ConfigureDisplayMode=>SupportHealthRecovery.ConfigureDisplayMode,
        ConnectionHealthRecovery.ReclaimOwnedDisplay=>SupportHealthRecovery.ReclaimOwnedDisplay,
        ConnectionHealthRecovery.RestartVideo=>SupportHealthRecovery.RestartVideo,
        ConnectionHealthRecovery.OpenLogs=>SupportHealthRecovery.OpenLogs,
        _=>throw new ArgumentOutOfRangeException(nameof(value))
    };

    void RefreshRequestedModes(bool reportEmpty=true)
    {
        var selected=(requestedModes.SelectedItem as RequestedMode)?.Profile;
        requestedModes.Items.Clear();
        var profiles=additionalSessions.Select(x=>x.RequestedProfile).Append(lastRequestedProfile).Where(x=>x is not null).Cast<TabletDisplayProfile>()
            .DistinctBy(p=>(p.Width,p.Height,p.RequestedRefreshRate)).ToArray();
        foreach(var profile in profiles)requestedModes.Items.Add(new RequestedMode(profile,uiLanguage));
        if(requestedModes.Items.Count>0)
        {
            requestedModes.SelectedItem=requestedModes.Items.OfType<RequestedMode>().FirstOrDefault(item=>selected is not null&&
                item.Profile.Width==selected.Width&&item.Profile.Height==selected.Height&&
                item.Profile.RequestedRefreshRate==selected.RequestedRefreshRate);
            if(requestedModes.SelectedIndex<0)requestedModes.SelectedIndex=0;
            modeHint.Text=Ui("已读取设备上报的请求模式；只会配置选中模式。","Device-requested modes loaded. Only the selected mode will be configured.");
        }
        else
        {
            modeHint.Text=Ui("尚未收到设备屏幕参数；请先完成一次认证或连接。","No display profile has been received. Authenticate or connect a device first.");
            if(reportEmpty)Log(modeHint.Text);
        }
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
        primaryTargetKey=null;Log(Ui("已添加设备所需显示模式，请重新扫码连接。","The device-requested display mode was added. Scan again to reconnect."));await RefreshAsync();
    }

    async Task DiagnoseAsync()
    {
        if(diagnosing||closing)return;diagnosing=true;diagnose.Enabled=false;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(22));
        var results=new List<ConnectionFinding>();
        var diagnosisLanguage=uiLanguage;
        string D(string simplifiedChinese,string english)=>
            diagnosisLanguage==ProductLanguage.SimplifiedChinese?simplifiedChinese:english;
        try
        {
            if(!settingsValid)
            {
                results.Add(new(D("配置","Configuration"),D("需要处理","Needs attention"),
                    D("配置文件读取失败；必须先恢复原文件，检测和 ADB 路径修复不会覆盖排除规则：",
                        "The settings file could not be read. Restore it first; diagnostics and ADB repair will not overwrite exclusion rules: ")+store.Path));
                return;
            }
            var snapshot=new DevicePolicySettings{SchemaVersion=settings.SchemaVersion,AdbPath=settings.AdbPath,ExcludedDevices=[..settings.ExcludedDevices]};
            results=await Task.Run(async()=>
            {
                var found=results;
                try{DevicePolicy.ValidateSettings(snapshot);found.Add(new(D("配置","Configuration"),D("通过","Passed"),
                    D($"已加载 {snapshot.ExcludedDevices.Count} 条排除规则。",$"Loaded {snapshot.ExcludedDevices.Count} exclusion rules.")));}
                catch(Exception ex){found.Add(new(D("配置","Configuration"),D("需要处理","Needs attention"),
                    SafeErrorForLanguage(ex,diagnosisLanguage)+D("；请修正配置文件，检测不会覆盖损坏的配置。"," Correct the settings file; diagnostics will not overwrite damaged settings.")));return found;}
                var bundled=Path.Combine(AppContext.BaseDirectory,"tools","platform-tools","adb.exe");
                var bundleDirectory=Path.GetDirectoryName(bundled)!;
                var bundleProvided=Directory.Exists(bundleDirectory);
                if(bundleProvided)
                {
                    try
                    {
                        _=TrustedBundledAdb.StageAndGetVerifiedPath(bundled);
                        found.Add(new(D("内置 ADB","Bundled ADB"),D("通过","Passed"),
                            D("三件套哈希与固定 Google r37 版本一致，并已复制到受保护目录。",
                                "All three files match the pinned Google r37 hashes and were copied to protected storage.")));
                    }
                    catch(Exception ex) when(IsTrustedAdbFailure(ex))
                    {found.Add(new(D("内置 ADB","Bundled ADB"),D("需要处理","Needs attention"),
                        D("随包三件套不完整或固定 SHA-256 不匹配；未执行这些文件。",
                            "The bundled file set is incomplete or its pinned SHA-256 does not match. These files were not run.")));}
                }
                else found.Add(new(D("ADB 组件","ADB component"),D("信息","Information"),
                    D("公开发行包不分发 Google Platform-Tools。免调试网络连接不需要 ADB；兼容模式可选择你从 Android 官方安装的 r37 platform-tools/adb.exe。",
                        "Public releases do not redistribute Google Platform-Tools. Network connections do not need ADB; for compatibility mode, select r37 platform-tools/adb.exe installed from Android's official site.")));
                string? adbLocation=null;
                try{adbLocation=TrustedBundledAdb.LocateStageAndGetVerifiedPath(snapshot.AdbPath);}
                catch(Exception ex) when(IsTrustedAdbFailure(ex))
                {found.Add(new(D("ADB 选择","ADB selection"),D("需要处理","Needs attention"),
                    D("找到的 Android Platform-Tools 未通过固定 SHA-256 校验或无法写入受保护目录；未执行源文件。",
                        "The discovered Android Platform-Tools failed pinned SHA-256 verification or could not be written to protected storage. The source files were not run.")));}
                if(adbLocation is null)found.Add(new(D("ADB 选择","ADB selection"),D("需要处理","Needs attention"),bundleProvided
                    ?D("当前没有可执行的受保护 ADB 副本。内置组件完整时，可点击“修复：使用内置 ADB”。",
                        "No executable protected ADB copy is available. If the bundled component is complete, choose Repair: use bundled ADB.")
                    :D("没有可信 ADB 副本。ADB 兼容模式请从 Android 官方页面安装 r37 后选择 adb.exe；Wi-Fi / USB 网络共享模式不需要 ADB。",
                        "No trusted ADB copy is available. For ADB compatibility mode, install r37 from Android's official site and select adb.exe; Wi-Fi and USB tethering modes do not need ADB.")));
                else
                {
                    var version=await new AdbProcessRunner().RunAsync(adbLocation,["version"],TimeSpan.FromSeconds(4),deadline.Token);
                    found.Add(new(D("ADB 执行","ADB execution"),version.ExitCode==0?D("通过","Passed"):D("需要处理","Needs attention"),version.ExitCode==0
                        ?SafeAdbVersionSummary(version.StandardOutput,diagnosisLanguage)
                        :SafeErrorForLanguage(new AdbCommandException(version.ExitCode,version.StandardError,version.StandardOutput),diagnosisLanguage,adbOperation:true)));
                }
                var usb=await UsbInventory.ReadAsync(deadline.Token);
                found.Add(new("Windows USB",D("信息","Information"),
                    D($"当前存在 {usb.Count} 个具有序列号的 USB 身份。",$"Found {usb.Count} USB identities with serial numbers.")));
                if(adbLocation is not null)
                {
                    var diagnosticPolicy=new DevicePolicy(snapshot);
                    var client=new AdbClient(adbLocation,diagnosticPolicy,UsbInventory.ReadAsync);
                    var devices=await client.ListDevicesAsync(deadline.Token);
                    if(devices.Count==0)found.Add(new(D("安卓调试设备","Android debugging devices"),D("信息","Information"),
                        D("未发现 ADB 设备。免调试网络模式无需 ADB；调试模式需在安卓开启 USB 调试并允许此电脑。",
                            "No ADB device was found. Network mode does not require ADB; debugging mode requires USB debugging and approval for this computer.")));
                    for(var index=0;index<devices.Count;index++)
                    {
                        var device=devices[index];
                        var verdict=diagnosticPolicy.Evaluate(device,usb);
                        var detail=device.State switch
                        {
                            "unauthorized"=>D("请在此设备解锁后允许电脑的 USB 调试授权。","Unlock this device and approve USB debugging for this computer."),
                            "offline"=>D("设备 ADB 离线，请重插该设备数据线并重新授权。","The device is offline in ADB. Reconnect its cable and approve it again."),
                            _=>verdict.Allowed?D("可在 USB 调试页选择并连接。","The device can be selected and connected on the USB debugging page."):
                                (diagnosisLanguage==ProductLanguage.English?D("设备被保护规则排除。","The device is excluded by a protection rule."):verdict.Reason)
                        };
                        found.Add(new(D($"安卓设备 {index+1}",$"Android device {index+1}"),
                            verdict.Allowed?D("通过","Passed"):D("需要处理","Needs attention"),detail));
                    }
                }
                var networks=NetworkInterfaceCatalog.GetChoices(snapshot);
                found.Add(new(D("网络线路","Network routes"),networks.Count>0?D("通过","Passed"):D("需要处理","Needs attention"),
                    networks.Count>0?string.Join("\r\n",networks.Select(x=>$"{x.InterfaceAlias} · {x.LocalAddress} · {x.Kind}")):
                    D("没有可用线路。请连接同一 Wi-Fi，或在设备开启 USB 网络共享；检查设备是否被排除。",
                        "No route is available. Join the same Wi-Fi network or enable USB tethering on the device, and check whether the device is excluded.")));
                var targets=VirtualDisplayManager.GetTargets();
                found.Add(new(D("虚拟副屏","Virtual display"),targets.Count<=1?D("通过","Passed"):D("需要处理","Needs attention"),targets.Count switch
                {
                    0 => D("当前没有虚拟显示设备，这是断开状态的预期结果；设备认证后会按需安装。",
                        "No virtual display device is present. This is expected while disconnected; one is installed on demand after authentication."),
                    1 => D($"检测到唯一虚拟显示目标，活动={targets[0].IsActive}。停止连接后应自动卸载。",
                        $"One virtual display target was found; active={targets[0].IsActive}. It should be removed automatically after disconnection."),
                    _ => D($"检测到 {targets.Count} 个 TabLink 兼容目标；单屏策略要求最多一个，请停止连接并运行清理。",
                        $"Found {targets.Count} TabLink-compatible targets. The single-display policy allows at most one; stop the connection and run cleanup.")
                }));
                try{found.Add(new(D("H.264 编码器","H.264 encoder"),D("通过","Passed"),VideoPipeline.FindFfmpeg()));}
                catch(Exception ex){found.Add(new(D("H.264 编码器","H.264 encoder"),D("需要处理","Needs attention"),
                    SafeErrorForLanguage(ex,diagnosisLanguage)+D("；请使用完整交付目录。"," Use the complete distribution directory.")));}
                var apk=Path.Combine(AppContext.BaseDirectory,"android","TabLink.apk");
                found.Add(new(D("安卓客户端","Android client"),File.Exists(apk)?D("通过","Passed"):D("需要处理","Needs attention"),File.Exists(apk)
                    ?D("客户端随包提供；在 USB 调试页可安装到明确选中的安卓设备。","The client is included and can be installed to an explicitly selected Android device from the USB debugging page.")
                    :D("缺少 android/TabLink.apk，请恢复完整交付目录。","android/TabLink.apk is missing. Restore the complete distribution directory.")));
                var conflicts=Process.GetProcessesByName("ExtensoDeskServer");
                found.Add(new(D("USB 冲突","USB conflict"),conflicts.Length==0?D("通过","Passed"):D("需要处理","Needs attention"),conflicts.Length==0
                    ?D("未发现 ExtensoDeskServer 进程。","The ExtensoDeskServer process was not found.")
                    :D("ExtensoDeskServer 正在运行，可能接管 USB 设备。请先退出它的服务再连接。",
                        "ExtensoDeskServer is running and may claim USB devices. Stop its service before connecting.")));foreach(var p in conflicts)p.Dispose();
                return found;
            },deadline.Token);
            if(server is {} primary)results.Add(new(D("主连接","Primary connection"),D("信息","Information"),
                D($"客户端连接={primary.ClientConnected}，电脑发送={primary.FramesSent} 帧，解码提交={primary.SubmittedFrames} 帧 / {primary.ClientSubmittedFps:F1} fps，呈现回调={primary.PresentedFrames} 帧 / {primary.ClientPresentedFps:F1} fps，采集暂停={primary.CapturePaused}。",
                    $"Client connected={primary.ClientConnected}; host sent={primary.FramesSent} frames; decode submitted={primary.SubmittedFrames} frames / {primary.ClientSubmittedFps:F1} fps; presentation callbacks={primary.PresentedFrames} frames / {primary.ClientPresentedFps:F1} fps; capture paused={primary.CapturePaused}.")));
            foreach(var s in additionalSessions.Where(x=>!x.IsStopped))results.Add(new(D("独立设备 ","Independent device ")+s.Port,D("信息","Information"),WindowsUiText.TranslateNativeSessionState(s.State,diagnosisLanguage)));
            foreach(var s in browserStates.Values)results.Add(new(D("浏览器 ","Browser ")+s.Id.ToString()[..8],D("信息","Information"),BrowserStatusText(s,diagnosisLanguage)));
            Diagnostics.Save("connection-diagnosis.json",()=>new{timestamp=DateTimeOffset.Now,findings=results},Log);
        }
        catch(OperationCanceledException){results.Add(new(D("检测","Diagnostics"),D("未完成","Incomplete"),
            D("检测已超时或取消。已有连接继续运行，可重试检测。","Diagnostics timed out or were cancelled. Existing connections remain active; you can run diagnostics again.")));}
        catch(Exception ex){results.Add(new(D("检测","Diagnostics"),D("需要处理","Needs attention"),SafeErrorForLanguage(ex,diagnosisLanguage)));}
        finally
        {
            diagnosticReport.Text=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"\r\n\r\n"+string.Join("\r\n\r\n",results.Select(x=>$"[{WindowsUiText.TranslateRuntime(x.State,diagnosisLanguage)}] {WindowsUiText.TranslateRuntime(x.Check,diagnosisLanguage)}\r\n{WindowsUiText.TranslateHealthDetail(x.Detail,diagnosisLanguage)}"));
            diagnosticReportLanguage=diagnosisLanguage;diagnosticReportNeedsRerun=false;
            if(diagnosisLanguage!=uiLanguage)RefreshDiagnosticReportLanguage();
            diagnosing=false;diagnose.Enabled=!closing;
        }
    }

    void RefreshDiagnosticReportLanguage()
    {
        if(diagnosticReport.TextLength==0)return;
        if(diagnosticReportNeedsRerun||diagnosticReportLanguage!=uiLanguage)
        {
            diagnosticReportNeedsRerun=true;diagnosticReportLanguage=uiLanguage;
            diagnosticReport.Text=Ui(
                "界面语言已切换。请重新运行检测，以简体中文生成当前检测报告。",
                "The interface language changed. Run diagnostics again to generate a current report in English.");
        }
    }

    static bool IsTrustedAdbFailure(Exception ex)=>TrustedBundledAdb.IsTrustStorageFailure(ex);
    static string SafeAdbVersionSummary(string output,ProductLanguage language)
    {
        var lines=output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(line=>line.StartsWith("Android Debug Bridge version ",StringComparison.Ordinal)||
                line.StartsWith("Version ",StringComparison.Ordinal)).Take(2).ToArray();
        return lines.Length==0
            ?language==ProductLanguage.SimplifiedChinese
                ?"受保护 ADB 的 version 命令执行成功；诊断未记录原始路径。"
                :"The protected ADB version command succeeded; diagnostics did not record the original path."
            :string.Join("\r\n",lines);
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
        Log(Ui("已修复 ADB 路径，使用校验通过的内置组件；未重启共享 ADB 服务。","The ADB path was repaired with the verified bundled component; the shared ADB service was not restarted."));await RefreshAsync();
    }
    async Task RepairConnectionAsync()
    {
        if(sessionList.SelectedItem is NativeSessionRow row&&!row.Session.IsStopped)
        {row.Session.Reconnect();Log(Ui("正在重建选中设备的视频连接，其他设备继续运行。","Rebuilding the selected device's video connection; other devices continue running."));return;}
        if(browserHost is {} host)
        {
            if(browserSessions.SelectedItem is BrowserSessionRow browser)await host.StopSessionAsync(browser.Status.Id);
            CreateBrowserPairing();Log(Ui("已为浏览器接入生成新的单次配对二维码。","A new single-use browser pairing QR code was generated."));return;
        }
        if(networkChoice is not null&&server is {} running){running.RequestReconnect();Log(Ui("已请求主网络会话重新协商屏幕和编码器。","The primary network session was asked to renegotiate its display and encoder."));return;}
        if(approved is {} target)
        {
            var serial=target.Serial;await StopAsync();await RefreshAsync();
            devices.SelectedItem=devices.Items.OfType<DeviceChoice>().SingleOrDefault(x=>x.Device.Serial==serial)??throw new IOException("原设备未就绪；请查看检测结果并重新授权。");
            await ConnectAsync();return;
        }
        await RefreshNetworksAsync();await RefreshAsync();
        Log(Ui("已刷新设备与线路。请回到连接页选择连接方式和目标设备；浏览器接入可重新生成配对二维码。",
            "Devices and routes were refreshed. Return to Connect display to select a method and target; browser access can generate a new pairing QR code."));
    }
    sealed record ConnectionFinding(string Check,string State,string Detail);
    sealed record RequestedMode(TabletDisplayProfile Profile,ProductLanguage Language)
    {public override string ToString()=>$"{Profile.Width} × {Profile.Height} · {Profile.RequestedRefreshRate} Hz"+
        (Language==ProductLanguage.SimplifiedChinese?"（设备报告）":" (reported by device)");}
}
