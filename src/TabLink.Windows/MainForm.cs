using System.Diagnostics;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm : Form
{
    readonly Color ink=Color.FromArgb(30,43,65), muted=Color.FromArgb(90,107,128), accent=Color.FromArgb(31,105,210);
    readonly SettingsStore store=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","settings.json"));
    DevicePolicySettings settings = new();
    DevicePolicy policy=null!;
    AdbClient? adb;
    ApprovedUsbDevice? approved;
    DesktopCapture? capture;
    FrameServer? server;
    readonly string? requestedSerial;
    DateTime sessionStartedUtc;
    readonly SessionPresentationDeadline presentationDeadline=new();
    SecondScreenWelcome? welcome;
    SessionGuard? displayGuard;
    DisplaySessionReservation? primaryReservation;
    DisplaySessionReservation? pendingPrimaryDisplayCleanup;
    readonly Guid primarySessionId=Guid.NewGuid();
    string? primaryTargetKey;
    TabletDisplayProfile? lastRequestedProfile;
    ActiveDisplayPower? activePower;
    TabletDisplayProfile? tabletProfile;
    bool adaptingDisplay;
    long previousPresented;
    DateTime previousSampleUtc;
    readonly CancellationTokenSource lifetime=new();
    bool busy,closing,settingsValid=true,monitoring,reverseCreated,stopping;
    Task? stopTask;
    readonly ComboBox devices=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly ComboBox displays=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Button refresh=new(){Text="刷新设备"};
    readonly Button connect=new(){Text="连接选中的平板"};
    readonly Button stop=new(){Text="停止连接",Enabled=false};
    readonly Button installApk=new(){Text="安装安卓客户端"};
    readonly Button chooseAdb=new(){Text="选择 adb.exe"};
    readonly CheckBox touch=new(){Text="允许平板触控操作副屏",Checked=true,AutoSize=true};
    readonly Label status=new(){Text="尚未连接",AutoSize=true,Font=new Font("Microsoft YaHei UI",13,FontStyle.Bold)};
    readonly Label metrics=new(){Text="按需启用唯一一块虚拟副屏",AutoSize=true};
    readonly Label adbPath=new(){Text="正在定位 Android 平台工具…",AutoSize=false,AutoEllipsis=true,MinimumSize=new Size(0,36),TextAlign=ContentAlignment.MiddleLeft};
    readonly ListBox rules=new(){Dock=DockStyle.Top,Height=96,IntegralHeight=false,MinimumSize=new Size(0,90)};
    readonly TextBox serial=new(){PlaceholderText="设备序列号（可单独填写）",Width=240};
    readonly TextBox vid=new(){PlaceholderText="VID，如 19D2",Width=130,MaxLength=4};
    readonly TextBox pid=new(){PlaceholderText="PID，如 0246",Width=130,MaxLength=4};
    readonly TextBox label=new(){PlaceholderText="备注，如 随身 Wi-Fi",Width=220};
    readonly Button addRule=new(){Text="加入排除列表"};
    readonly Button removeRule=new(){Text="移除选中规则"};
    readonly TextBox log=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None};
    readonly TabControl mainTabs=new(){Dock=DockStyle.Fill,Multiline=false,Padding=new Point(16,7),Margin=new Padding(0,8,0,6)};
    readonly ComboBox connectionMode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=280};
    readonly Label connectionModeHint=new(){AutoSize=true,ForeColor=Color.FromArgb(90,107,128),MaximumSize=new Size(820,0)};
    readonly Panel connectionHost=new(){Dock=DockStyle.Fill,BackColor=Color.White};
    Control? clientConnectionPanel,browserConnectionPanel,usbDebugConnectionPanel;
    readonly System.Windows.Forms.Timer monitor=new(){Interval=2000};
    readonly System.Windows.Forms.Timer activationTimer=new(){Interval=350};
    readonly NotifyIcon tray=new(){Icon=SystemIcons.Application,Text="TabLink · USB 平板副屏",Visible=true};
    readonly ContextMenuStrip trayMenu=new();
    readonly ToolStripMenuItem trayStop=new("停止连接");
    bool trayHintShown;

    public MainForm(string? requestedSerial=null,EventWaitHandle? activationRequest=null,string? requestedNetwork=null,bool diagnosticPairing=false,EventWaitHandle? exitRequest=null,bool verification=false)
    {
        this.requestedSerial=requestedSerial;
        this.diagnosticPairing=diagnosticPairing;
        Text="TabLink · 平板副屏"; AutoScaleMode=AutoScaleMode.Dpi; Size=new Size(960,680); MinimumSize=new Size(760,640);
        StartPosition=FormStartPosition.CenterScreen; Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(244,247,251); ForeColor=ink;
        BuildUi();
        var showWindow=new ToolStripMenuItem("打开主窗口");
        var exit=new ToolStripMenuItem("退出 TabLink");
        showWindow.Click+=(_,_)=>RestoreFromTray();
        tray.DoubleClick+=(_,_)=>RestoreFromTray();
        trayStop.Click+=async(_,_)=>await GuardAsync(StopAllAsync);
        exit.Click+=async(_,_)=>await ExitAsync();
        trayMenu.Items.AddRange([showWindow,trayStop,new ToolStripSeparator(),exit]);
        tray.ContextMenuStrip=trayMenu;
        if(!verification)ConfigureAutomaticUpdates();
        activationTimer.Tick+=async(_,_)=>{if(exitRequest?.WaitOne(0)==true&&!closing){await ExitAsync();return;}if(activationRequest?.WaitOne(0)==true&&!closing)RestoreFromTray();};
        activationTimer.Start();
        if(!verification)
        {
            try {settings=store.Load();if(!File.Exists(store.Path))store.Save(settings);} catch(Exception ex) when(ex is SettingsLoadException or IOException or UnauthorizedAccessException) {settingsValid=false; Log("配置读取失败，连接已禁用。请保留配置文件并修复："+store.Path);Log(ex.InnerException?.Message??ex.Message);}
            policy=new DevicePolicy(settings);LoadRules();LocateAdb();
        }
        else
        {
            settings=new DevicePolicySettings();policy=new DevicePolicy(settings);LoadRules();adbPath.Text="界面验证模式：未读取本机 ADB 配置。";
        }
        refresh.Click+=async(_,_)=>await GuardAsync(RefreshAsync);
        chooseAdb.Click+=(_,_)=>BrowseAdb();
        connect.Click+=async(_,_)=>await GuardAsync(ConnectAsync);
        stop.Click+=async(_,_)=>await GuardAsync(StopAsync);
        installApk.Click+=async(_,_)=>await GuardAsync(InstallApkAsync);
        addRule.Click+=async(_,_)=>await GuardAsync(AddRuleAsync); removeRule.Click+=async(_,_)=>await GuardAsync(RemoveRuleAsync);
        rules.SelectedIndexChanged+=(_,_)=>UpdateButtons();
        monitor.Tick+=async(_,_)=>await MonitorAsync();
        Shown+=async(_,_)=>
        {
            if(verification)return;
            BeginAutomaticUpdateChecks();
            await GuardAsync(RefreshNetworksAsync);monitor.Start();
            if(requestedNetwork is not null)
            {
                await GuardAsync(async()=>
                {
                    var address=System.Net.IPAddress.Parse(requestedNetwork);
                    var choice=networks.Items.OfType<NetworkInterfaceChoice>().SingleOrDefault(x=>x.LocalAddress.Equals(address))??throw new IOException("命令指定的本地线路不可用或已被排除。");
                    networks.SelectedItem=choice;await StartNetworkAsync();
                });
            }
            if(this.requestedSerial is not null)
            {
                connectionMode.SelectedIndex=2;
                await GuardAsync(RefreshAsync);
                var selected=devices.Items.OfType<DeviceChoice>().SingleOrDefault(d=>d.Device.Serial==this.requestedSerial);
                if(selected is null){SetStatus("指定的平板尚未连接或尚未授权，请查看连接记录。");return;}
                devices.SelectedItem=selected;await GuardAsync(ConnectAsync);
            }
        };
        FormClosing+=async(_,e)=>
        {
            if(closing)return;
            if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;HideToTray();return;}
            if(e.CloseReason==CloseReason.WindowsShutDown)
            {
                // The independent guard collects our display when this owner exits.
                closing=true;monitor.Stop();lifetime.Cancel();tray.Visible=false;return;
            }
            e.Cancel=true;await ExitAsync();
        };
        FormClosed+=(_,_)=>{idleUpdateDelay?.Cancel();idleUpdateDelay?.Dispose();activationTimer.Dispose();monitor.Dispose();tray.Visible=false;tray.Dispose();trayMenu.Dispose();updateCoordinator?.Dispose();lifetime.Dispose();};
        UpdateButtons();
    }

    void HideToTray()
    {
        Hide();ShowInTaskbar=false;
        if(!trayHintShown)
        {
            trayHintShown=true;
            tray.ShowBalloonTip(3000,"TabLink 已在后台运行","副屏连接继续运行。双击托盘图标或再次打开快捷方式可回到主窗口；右键菜单可停止或退出。",ToolTipIcon.Info);
        }
    }
    void RestoreFromTray()
    {
        if(closing)return;
        ShowInTaskbar=true;Show();
        if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;
        if(!Screen.AllScreens.Any(screen=>screen.WorkingArea.IntersectsWith(Bounds)))CenterToScreen();
        Activate();BringToFront();
    }
    async Task ExitAsync(bool requireReadyUpdater = false)
    {
        if(closing)return;
        if(requireReadyUpdater)
        {
            var launched=updateCoordinator is not null&&updateCoordinator.Ready is not null&&await updateCoordinator.TryLaunchReadyUpdaterAsync();
            if(!launched)
            {
                updateExitStarted=false;
                SetStatus("自动更新未能安全启动，TabLink 保持运行");
                return;
            }
        }
        closing=true;monitor.Stop();activationTimer.Stop();lifetime.Cancel();UpdateButtons();
        if(networkStartTask is {} opening)try{await opening;}catch(Exception ex){Log("网络启动已结束："+ex.Message);}
        if(additionalStartTask is {} extraOpening)try{await extraOpening;}catch(Exception ex){Log("设备启动已结束："+ex.Message);}
        if(browserStartTask is {} browserOpening)try{await browserOpening;}catch(Exception ex){Log("浏览器启动已结束："+ex.Message);}
        try{await StopAllAsync();if(!requireReadyUpdater&&updateCoordinator is not null)await updateCoordinator.TryLaunchReadyUpdaterAsync();}
        finally{tray.Visible=false;Close();}
    }

    void BuildUi()
    {
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=4};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,96));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,48));Controls.Add(root);
        var header=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,MinimumSize=new Size(0,88),FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(0,0,0,8)};
        header.Controls.Add(new Label{Text="TabLink",Font=new Font("Segoe UI",22,FontStyle.Bold),AutoSize=true,ForeColor=accent});
        header.Controls.Add(new Label{Text="让手机、平板成为电脑的独立扩展桌面",AutoSize=true,ForeColor=muted});root.Controls.Add(header,0,0);
        var state=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,MinimumSize=new Size(0,64),BackColor=Color.White,Padding=new Padding(14,7,10,6),FlowDirection=FlowDirection.TopDown,WrapContents=false};
        metrics.ForeColor=muted;state.Controls.Add(status);state.Controls.Add(metrics);root.Controls.Add(state,0,1);
        state.SizeChanged+=(_,_)=>{var width=Math.Max(240,state.ClientSize.Width-state.Padding.Horizontal-12);status.MaximumSize=metrics.MaximumSize=new Size(width,0);};
        root.Controls.Add(mainTabs,0,2);
        var connectionPage=new TabPage("连接副屏"){BackColor=Color.White,Padding=new Padding(12)};
        var exclusions=new TabPage("设备保护"){BackColor=Color.White,Padding=new Padding(12),AutoScroll=true};
        var support=new TabPage("检测与日志"){BackColor=Color.White,Padding=new Padding(10)};
        mainTabs.TabPages.AddRange([connectionPage,exclusions,support]);

        var connectionLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));connectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var modeHeader=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(4,0,4,6)};
        var modeRow=new FlowLayoutPanel{AutoSize=true,WrapContents=true,Margin=Padding.Empty};
        modeRow.Controls.Add(new Label{Text="连接方式",AutoSize=true,Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold),Margin=new Padding(0,7,14,0)});
        connectionMode.Items.AddRange(["TabLink 客户端（推荐）","浏览器接入","USB 调试（兼容）"]);modeRow.Controls.Add(connectionMode);
        touch.Margin=new Padding(0,8,0,8);
        modeHeader.Controls.Add(modeRow);modeHeader.Controls.Add(connectionModeHint);modeHeader.Controls.Add(touch);
        connectionLayout.Controls.Add(modeHeader,0,0);connectionLayout.Controls.Add(connectionHost,0,1);connectionPage.Controls.Add(connectionLayout);

        clientConnectionPanel=BuildNetworkPanel();browserConnectionPanel=BuildBrowserPanel();
        var connection=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(20),AutoScroll=true};usbDebugConnectionPanel=connection;
        connectionHost.Controls.Add(clientConnectionPanel);connectionHost.Controls.Add(browserConnectionPanel);connectionHost.Controls.Add(usbDebugConnectionPanel);
        connectionMode.SelectedIndexChanged+=(_,_)=>ShowConnectionMode();connectionMode.SelectedIndex=0;

        log.BackColor=Color.White;log.ForeColor=muted;
        var form=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,RowCount=5};
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(var i=0;i<form.RowCount;i++)form.RowStyles.Add(new RowStyle(SizeType.AutoSize));connection.Controls.Add(form);
        form.Controls.Add(new Label{Text="USB 平板",AutoSize=true,Margin=new Padding(0,6,0,0)},0,0);form.Controls.Add(devices,1,0);
        var commands=Flow(refresh,installApk);form.Controls.Add(commands,1,1);
        var startCommands=Flow(connect,stop);connect.BackColor=accent;connect.ForeColor=Color.White;connect.FlatStyle=FlatStyle.Flat;form.Controls.Add(startCommands,1,2);
        var help=new Label{AutoSize=true,MaximumSize=new Size(820,0),ForeColor=muted,Margin=new Padding(0,8,0,6),Text="连接时自动安装唯一副屏，断开时自动卸载。APK 会读取设备方向、原生分辨率和支持的刷新率，并显示 H.264 实际接收帧率。"};form.Controls.Add(help,0,3);form.SetColumnSpan(help,2);
        var tools=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,AutoSize=true,Margin=Padding.Empty};
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        chooseAdb.AutoSize=true;chooseAdb.Padding=new Padding(10,5,10,5);tools.Controls.Add(chooseAdb,0,0);tools.Controls.Add(adbPath,1,0);
        form.Controls.Add(tools,0,4);form.SetColumnSpan(tools,2);adbPath.ForeColor=muted;adbPath.Dock=DockStyle.Fill;adbPath.MaximumSize=Size.Empty;adbPath.Margin=new Padding(0,2,0,0);
        connectionPage.SizeChanged+=(_,_)=>
        {
            var width=Math.Max(280,connectionPage.ClientSize.Width-connectionPage.Padding.Horizontal-28);
            connectionModeHint.MaximumSize=help.MaximumSize=new Size(width,0);
        };
        var exclusionLayout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=4};
        for(var i=0;i<exclusionLayout.RowCount;i++)exclusionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));exclusions.Controls.Add(exclusionLayout);
        var exclusionHelp=new Label{Text="序列号或 VID/PID 任一匹配都会阻止 USB 连接与安装 APK。已默认保护你的 F50 Pro。\n新增规则命中正在使用的 USB 设备时，会先停止该连接。",AutoSize=true,MaximumSize=new Size(820,0),ForeColor=muted,Margin=new Padding(0,0,0,12)};
        exclusionLayout.Controls.Add(exclusionHelp,0,0);
        exclusionLayout.Controls.Add(rules,0,1);exclusionLayout.Controls.Add(Flow(LabeledField("设备序列号",serial),LabeledField("USB VID",vid),LabeledField("USB PID",pid)),0,2);exclusionLayout.Controls.Add(Flow(LabeledField("备注名称",label),addRule,removeRule),0,3);
        exclusions.SizeChanged+=(_,_)=>exclusionHelp.MaximumSize=new Size(Math.Max(280,exclusions.ClientSize.Width-exclusions.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-20),0);

        var supportLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
        supportLayout.RowStyles.Add(new RowStyle(SizeType.Percent,78));supportLayout.RowStyles.Add(new RowStyle(SizeType.Percent,22));
        supportLayout.Controls.Add(BuildDiagnosticsPanel(),0,0);
        var logBox=new GroupBox{Text="连接记录",Dock=DockStyle.Fill,Padding=new Padding(10)};logBox.Controls.Add(log);supportLayout.Controls.Add(logBox,0,1);support.Controls.Add(supportLayout);

        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,Padding=Padding.Empty};
        footer.RowStyles.Add(new RowStyle(SizeType.Percent,50));footer.RowStyles.Add(new RowStyle(SizeType.Percent,50));
        footer.Controls.Add(new Label{Text="只启用一块副屏  ·  点 × 后在托盘继续运行",Dock=DockStyle.Fill,ForeColor=muted,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty},0,0);
        var authorRow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=Padding.Empty,Padding=Padding.Empty};
        authorRow.Controls.Add(new Label{Text="作者：张林杰（Jey / @linjierd）",AutoSize=true,ForeColor=muted,Margin=new Padding(0,2,12,0)});
        var github=new LinkLabel{Text="GitHub",AutoSize=true,LinkColor=accent,ActiveLinkColor=accent,VisitedLinkColor=accent,Margin=new Padding(0,2,12,0)};
        var blog=new LinkLabel{Text="博客：linjie.space",AutoSize=true,LinkColor=accent,ActiveLinkColor=accent,VisitedLinkColor=accent,Margin=new Padding(0,2,0,0)};
        github.LinkClicked+=(_,_)=>OpenAuthorLink("https://github.com/linjierd");blog.LinkClicked+=(_,_)=>OpenAuthorLink("https://linjie.space/");
        authorRow.Controls.Add(github);authorRow.Controls.Add(blog);footer.Controls.Add(authorRow,0,1);root.Controls.Add(footer,0,3);
    }
    void OpenAuthorLink(string address)
    {
        try{Process.Start(new ProcessStartInfo(address){UseShellExecute=true});}
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {Log("无法打开链接："+ex.Message);SetStatus("无法打开链接，请手动访问 "+address);}
    }
    void ShowConnectionMode()
    {
        if(connectionMode.SelectedIndex==1)SyncNetworkSelection(networks,browserNetworks);
        else if(connectionMode.SelectedIndex==0)SyncNetworkSelection(browserNetworks,networks);
        var panels=new[]{clientConnectionPanel,browserConnectionPanel,usbDebugConnectionPanel};
        foreach(var panel in panels)if(panel is not null)panel.Visible=false;
        var selected=connectionMode.SelectedIndex switch{1=>browserConnectionPanel,2=>usbDebugConnectionPanel,_=>clientConnectionPanel};
        connectionModeHint.Text=connectionMode.SelectedIndex switch
        {
            1=>"适用于 iPhone、iPad、HarmonyOS 及不安装客户端的设备；首次使用需要信任本机证书。",
            2=>"仅用于 Android 兼容连接；需要打开 USB 调试并授权这台电脑。",
            _=>"推荐方式：手机或平板使用 TabLink 客户端扫码，支持同一 Wi-Fi 或 USB 网络共享。"
        };
        if(!HasAnySessions)
        {
            status.Text=connectionMode.SelectedIndex switch{1=>"浏览器接入尚未开启",2=>"等待安卓 USB 调试设备",_=>"尚未连接"};
            metrics.Text=connectionMode.SelectedIndex switch{1=>"本地 HTTPS + WebRTC · 单设备",2=>"兼容连接 · 按需启用唯一副屏",_=>"本地加密连接 · 无需 USB 调试"};
        }
        if(selected is not null){selected.Visible=true;selected.BringToFront();}
    }
    static void SyncNetworkSelection(ComboBox source,ComboBox destination)
    {
        if(source.SelectedItem is not NetworkInterfaceChoice selected)return;
        var match=destination.Items.OfType<NetworkInterfaceChoice>().FirstOrDefault(candidate=>SameInterface(candidate,selected));
        if(match is not null)destination.SelectedItem=match;
    }
    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=Padding.Empty};
        foreach(var c in controls){if(c is Button){c.AutoSize=true;c.Padding=new Padding(10,5,10,5);c.Margin=new Padding(0,0,10,0);}panel.Controls.Add(c);}return panel;
    }
    static Control LabeledField(string caption,Control editor)
    {
        var panel=new TableLayoutPanel{AutoSize=true,ColumnCount=1,RowCount=2,Margin=new Padding(0,4,12,4)};
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label{Text=caption,AutoSize=true,ForeColor=Color.FromArgb(90,107,128),Margin=new Padding(0,0,0,3)},0,0);
        panel.Controls.Add(editor,0,1);return panel;
    }
    void Log(string message)
    {
        if(IsDisposed)return;if(InvokeRequired){BeginInvoke(()=>Log(message));return;}
        var line=$"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";log.AppendText(line);
        try{var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","logs");Directory.CreateDirectory(folder);File.AppendAllText(Path.Combine(folder,$"{DateTime.Today:yyyy-MM-dd}.log"),line);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    void SetStatus(string message){if(IsDisposed)return;if(InvokeRequired){BeginInvoke(()=>SetStatus(message));return;}status.Text=message;Log(message);}
    void LocateAdb()
    {
        var path=AdbLocator.FindAdbPath(settings.AdbPath);
        adb=path is null?null:new AdbClient(path,policy,UsbInventory.ReadAsync);
        adbPath.Text=path is null?"未找到 adb.exe。请选择已安装的 Android SDK platform-tools 中的 adb.exe。":path;
    }
    void BrowseAdb()
    {
        using var dialog=new OpenFileDialog{Filter="Android Debug Bridge|adb.exe",Title="选择 Android 官方平台工具 adb.exe"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{settings.AdbPath=dialog.FileName;store.Save(settings);LocateAdb();Log("已更新 Android 平台工具位置。");UpdateButtons();}catch(Exception ex){ShowError(ex);}
    }
    async Task RefreshAsync()
    {
        LocateAdb();devices.Items.Clear();displays.Items.Clear();
        foreach(var display in VirtualDisplayManager.GetDisplays().Where(d=>d.IsTabLinkCompatible&&!d.IsPrimary))displays.Items.Add(new DisplayChoice(display));
        if(displays.Items.Count>0)displays.SelectedIndex=0;
        if(adb is not null && settingsValid)
        {
            var inventory=await UsbInventory.ReadAsync(lifetime.Token);
            foreach(var device in await adb.ListDevicesAsync(lifetime.Token))devices.Items.Add(new DeviceChoice(device,policy.Evaluate(device,inventory)));
            if(devices.Items.Count>0)devices.SelectedIndex=0;
            Log($"刷新完成：{devices.Items.Count} 台 USB 调试候选设备（含待授权/离线），{displays.Items.Count} 块可用虚拟副屏。");
        }
        else Log("尚未配置 Android 平台工具，请先选择 adb.exe。");
        if(devices.Items.Count==0)devices.Items.Add("未发现 ADB 平板：请确认 USB 调试已开启并在平板允许此电脑");
        if(displays.Items.Count==0)displays.Items.Add(File.Exists(SessionGuard.LastDisplayPath)?"当前没有虚拟副屏 · 连接时自动安装":"尚无活动虚拟副屏");
        if(devices.SelectedIndex<0)devices.SelectedIndex=0;
        if(displays.SelectedIndex<0)displays.SelectedIndex=0;
        if(server is null)status.Text=devices.Items.OfType<DeviceChoice>().Any(d=>d.Decision.Allowed)?"平板已识别，请连接副屏":"等待平板 USB 调试连接";
    }
    DeviceChoice SelectedDevice()=>devices.SelectedItem as DeviceChoice??throw new InvalidOperationException("请先接入平板，开启 USB 调试并授权，然后刷新设备。");
    async Task InstallApkAsync()
    {
        var choice=SelectedDevice();if(adb is null)throw new InvalidOperationException("请先选择 adb.exe");
        var target=await adb.ApproveAsync(choice.Device,lifetime.Token);
        var apk=Path.Combine(AppContext.BaseDirectory,"android","TabLink.apk");
        if(!File.Exists(apk))throw new FileNotFoundException("交付目录中缺少 android/TabLink.apk。请运行完整构建脚本。",apk);
        SetStatus("正在将 TabLink 客户端安装到选中的平板…");
        await adb.InstallApkAsync(target,apk,lifetime.Token);SetStatus("安卓客户端已安装，可以连接副屏。");
    }
    async Task ConnectAsync()
    {
        if(server is not null)return;if(HasAdditionalSessions)throw new InvalidOperationException("TabLink 只允许一个副屏连接。请先停止当前原生或浏览器连接。");if(adb is null)throw new InvalidOperationException("请先选择 adb.exe");
        if(Process.GetProcessesByName("ExtensoDeskServer").Length>0)throw new InvalidOperationException("ExtensoDesk 后台仍在运行。请先退出它的 USB 服务，避免两个程序同时连接平板。");
        var device=SelectedDevice();
        await EnsureOwnedDisplayCleanupBeforeNewConnectionAsync();
        BeginConnectionHealth(ConnectionHealthPath.AdbCompatibility,$"手动选择设备 {device.Device.Serial}");
        // Establish tablet authorization before bringing back a previously
        // detached virtual screen. A missing tablet must not leave a phantom.
        approved=await adb.ApproveAsync(device.Device,lifetime.Token);
        try
        {
            MarkHealthRouteReady("USB 身份与 ADB 授权已核验，准备本机反向通道");
            MarkHealthAuthenticationStarted("正在从明确选中的客户端读取屏幕参数");
            SetStatus("正在从 APK 读取平板屏幕参数…");
            tabletProfile=await adb.ReadDisplayProfileAsync(approved,lifetime.Token);
            MarkHealthDisplayProfile(tabletProfile);
            Diagnostics.Save("tablet-display-profile.json",()=>tabletProfile,Log);
            Log($"平板报告：{tabletProfile.Width} × {tabletProfile.Height}，当前 {tabletProfile.RefreshRate:F1} Hz，支持最高 {tabletProfile.RequestedRefreshRate} Hz，方向 {tabletProfile.Rotation}。");
            _=VideoPipeline.FindFfmpeg();
            MarkHealthDisplayPreparing("正在按设备报告的模式准备唯一虚拟副屏");
            var current=await PrepareDisplayAsync(tabletProfile);
            MarkHealthDisplayReady(current);
            await Task.Delay(200,lifetime.Token);
            activePower=new ActiveDisplayPower();
            var captureIdentity=(displayGuard??throw new IOException("副屏保护组件未启动")).Lease;
            capture=new DesktopCapture(current,identity:captureIdentity);
            MarkHealthPipelineStarting("正在启动桌面捕获与 H.264 编码器");
            var profile=tabletProfile;
            server=new FrameServer(ct=>VideoPipeline.StreamAsync(current,profile,ct,Log,captureIdentity),touch.Checked?capture.Input:null,capture.ReleaseInput);
            var newServer=server;
            server.DisplayProfileChanged+=p=>{if(!IsDisposed)BeginInvoke(async()=>await AdaptDisplayAsync(newServer,p));};
            sessionStartedUtc=DateTime.UtcNow;
            presentationDeadline.Reset(sessionStartedUtc);
            previousPresented=0;previousSampleUtc=sessionStartedUtc;
            server.Status+=SetStatus;server.Start();
            await adb.ReversePortAsync(approved,cancellationToken:lifetime.Token);reverseCreated=true;
            (displayGuard??throw new IOException("副屏保护组件未启动")).AttachReverse(UsbReverseLease.Created(approved));
            SetStatus("USB 通道已建立，等待平板接收画面…");
            await adb.LaunchAsync(approved,server.Token,cancellationToken:lifetime.Token);
            UpdateButtons();
        }
        catch(Exception ex){MarkConnectionHealthAttention(ex.Message);await StopAsync();throw;}
    }

    async Task<VirtualDisplayInfo> PrepareDisplayAsync(TabletDisplayProfile profile,CancellationToken cancellationToken=default)
    {
        lastRequestedProfile=profile;
        using var preparation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellationToken);
        var ct=preparation.Token;ct.ThrowIfCancellationRequested();
        primaryReservation=await DisplaySessionAllocator.Shared.AcquireAsync(primarySessionId,profile,ct,primaryTargetKey);
        // Acquire retries allocator-owned pending cleanup before creating a new
        // reservation. A successful acquire therefore retires any stale UI
        // retry handle from the preceding stopped session.
        pendingPrimaryDisplayCleanup=null;
        primaryTargetKey=primaryReservation.TargetKey;
        displayGuard=primaryReservation.Guard;
        var current=primaryReservation.CurrentDisplay;
        displays.Items.Clear();displays.Items.Add(new DisplayChoice(current));displays.SelectedIndex=0;
        return current;
    }

    async Task ReleasePrimaryDisplayAsync()
    {
        if(primaryReservation is {} owned){await owned.DisposeAsync();primaryReservation=null;}
        displayGuard=null;
    }

    async Task RetryOwnedDisplayCleanupAsync()
    {
        try
        {
            var attempted=false;
            if(pendingPrimaryDisplayCleanup is {} owned)
            {
                // This reservation carries the exact guarded CCD lease and the
                // install receipt. Re-dispose it only; never enumerate or remove a
                // display by friendly name.
                await owned.DisposeAsync();
                if(ReferenceEquals(pendingPrimaryDisplayCleanup,owned))pendingPrimaryDisplayCleanup=null;
                attempted=true;
            }
            foreach(var native in additionalSessions.Where(session=>session.HasPendingDisplayCleanup).ToArray())
            {
                await native.RetryDisplayCleanupAsync();
                attempted=true;
            }
            foreach(var item in browserCleanupReservations.ToArray())
            {
                await item.Value.DisposeAsync();
                browserCleanupReservations.TryRemove(item.Key,out _);
                attempted=true;
            }
            if(!attempted)throw new IOException("没有可安全重试的 TabLink 副屏所有权记录；未更改任何显示设备。");
            if(HasPendingOwnedDisplayCleanup)throw new IOException("仍有本次连接拥有的副屏等待精确回收；未执行其他显示维护。");
            if(browserHost is not null&&browserChoice is {} choice)
            {
                BeginConnectionHealth(ConnectionHealthPath.Browser,$"选择浏览器线路 {choice.InterfaceAlias} · {choice.LocalAddress}");
                MarkHealthRouteReady($"{choice.InterfaceAlias} · {choice.LocalAddress}:27185 · 本地 HTTPS/WebRTC 已启动");
                MarkHealthAuthenticationStarted("本次副屏已精确回收，等待浏览器重新配对");
                SetStatus("已精确回收本次副屏；浏览器接入仍在等待重新配对");
            }
            else
            {
                StopConnectionHealth("本次拥有的虚拟副屏已精确回收");
                SetStatus("已精确回收并卸载本次连接拥有的虚拟副屏");
            }
        }
        catch(Exception ex)
        {
            var detail=OwnedDisplayCleanupFailureDetail(ex);
            MarkOwnedDisplayCleanupAttention(detail);
            Log(detail);
            throw new IOException(detail,ex);
        }
    }

    async Task EnsureOwnedDisplayCleanupBeforeNewConnectionAsync()
    {
        if(!HasPendingOwnedDisplayCleanup)return;
        Log("新连接开始前先重试本程序保留的精确副屏租约；不会按设备名称清理其他显示设备。");
        await RetryOwnedDisplayCleanupAsync();
        if(HasPendingOwnedDisplayCleanup)
            throw new IOException("上一块副屏仍在等待精确回收；未开始新连接。");
    }

    static string OwnedDisplayCleanupFailureDetail(Exception ex) =>
        "收回并卸载本次拥有的虚拟副屏失败："+ex;

    async Task AdaptDisplayAsync(FrameServer source,TabletDisplayProfile profile)
    {
        if(!ReferenceEquals(server,source)||tabletProfile is null||adaptingDisplay||busy||closing)return;
        if(profile.Width==tabletProfile.Width&&profile.Height==tabletProfile.Height&&profile.RequestedRefreshRate==tabletProfile.RequestedRefreshRate)return;
        if(networkChoice is not null)
        {
            Log($"平板方向已变化，网络会话重新匹配 {profile.Width} × {profile.Height}。");
            source.RequestReconnect();return;
        }
        adaptingDisplay=true;
        try
        {
            await GuardAsync(async()=>
            {
                Log($"平板方向已变化，重新匹配 {profile.Width} × {profile.Height}。");
                await StopAsync();await ConnectAsync();
            });
        }
        finally{adaptingDisplay=false;}
    }
    Task StopAsync()
    {
        if(stopTask is {IsCompleted:false})return stopTask;
        return stopTask=StopCoreAsync();
    }
    async Task StopCoreAsync()
    {
        stopping=true;UpdateButtons();
        if(networkPreparation is {IsCompleted:false} preparing)
        {
            server?.RequestReconnect();
            try{await preparing;}catch(Exception ex){Log("网络屏幕准备已结束："+ex.Message);}
        }
        var running=server;var ownedCapture=capture;var ownedApproval=approved;var ownedAdb=adb;var ownedReverse=reverseCreated;var ownedGuard=displayGuard;var ownedReservation=primaryReservation;var ownedPower=activePower;var ownedFirewall=networkFirewall;
        var displayCollected=true;
        server=null;capture=null;approved=null;reverseCreated=false;
        displayGuard=null;
        primaryReservation=null;primaryTargetKey=null;
        activePower=null;
        networkFirewall=null;networkChoice=null;networkDisplay=null;tabletProfile=null;ClearPairing();
        welcome?.Close();welcome=null;
        if(running is null&&ownedCapture is null&&ownedApproval is null&&ownedGuard is null&&ownedFirewall is null)
        {
            stopping=false;
            if(!HasAdditionalSessions&&!connectionHealth.Snapshot().Steps.Any(step=>step.State==ConnectionHealthState.Attention))StopConnectionHealth("连接已停止");
            UpdateButtons();return;
        }
        stopping=true;UpdateButtons();
        try
        {
            try{if(running is not null)await running.DisposeAsync();}
            catch(Exception ex){Log("画面服务结束时报告："+ex.Message);}
            finally
            {
                try { ownedCapture?.Dispose(); }
                catch(Exception ex){Log("画面输入结束时报告："+ex.Message);}
            }
            if(ownedReverse&&ownedApproval is not null&&ownedAdb is not null)
            {
                try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await ownedAdb.RemoveReverseAsync(ownedApproval,cancellationToken:timeout.Token);}
                catch(Exception ex){Log("画面服务已关闭；设备离线或策略已变化，未能清理 USB 转发。"+ex.Message);}
            }
            try{if(ownedReservation is not null)await ownedReservation.DisposeAsync();else ownedGuard?.Dispose();}
            catch(Exception ex)
            {
                displayCollected=false;
                if(ownedReservation is not null)pendingPrimaryDisplayCleanup=ownedReservation;
                var detail=OwnedDisplayCleanupFailureDetail(ex);
                MarkOwnedDisplayCleanupAttention(detail);
                Log(detail);
            }
        }
        finally
        {
            if(ownedFirewall is not null)try{await ownedFirewall.DisposeAsync();}catch(Exception ex){Log("清理本次防火墙规则失败："+ex.Message);}
            ownedPower?.Dispose();
            stopping=false;
            Diagnostics.Save("session-health.json",()=>new{timestamp=DateTimeOffset.Now,pid=Environment.ProcessId,receiving=false,stopped=true,displays=VirtualDisplayManager.GetDisplays()},Log);
            if(!IsDisposed)
            {
                if(displayCollected&&!HasAdditionalSessions&&!connectionHealth.Snapshot().Steps.Any(step=>step.State==ConnectionHealthState.Attention))StopConnectionHealth("连接已停止并回收本次副屏");
                SetStatus(displayCollected?"已停止连接，虚拟副屏设备已卸载":"已停止传输，虚拟副屏卸载待重试；再次连接时会自动处理");metrics.Text="USB 直连 · 只接管手动选中的设备";
                displays.Items.Clear();
                foreach(var item in VirtualDisplayManager.GetDisplays().Where(d=>d.IsTabLinkCompatible))displays.Items.Add(new DisplayChoice(item));
                if(displays.Items.Count==0)displays.Items.Add("当前没有虚拟副屏 · 连接时自动安装");displays.SelectedIndex=0;UpdateButtons();
            }
        }
    }
    async Task MonitorAsync()
    {
        await MonitorAdditionalAsync();
        if(server is {} healthServer)RefreshPrimaryConnectionHealth(healthServer);
        if(monitoring||stopping||preparingNetwork||server is null)return;monitoring=true;
        var observedApproval=approved;var observedServer=server;
        try
        {
            if(observedApproval is not null)
            {
                var inventory=await UsbInventory.ReadAsync(lifetime.Token);
                if(!ReferenceEquals(server,observedServer)||!ReferenceEquals(approved,observedApproval))return;
                if(!inventory.Any(d=>d.Serial.Equals(observedApproval.Serial,StringComparison.OrdinalIgnoreCase)&&d.Vid.Equals(observedApproval.UsbIdentity.Vid,StringComparison.OrdinalIgnoreCase)&&d.Pid.Equals(observedApproval.UsbIdentity.Pid,StringComparison.OrdinalIgnoreCase)))
                {Log("选中的平板已离线或 USB 身份改变，停止传输。");await StopAsync();return;}
            }
            else if(networkChoice is not null)
            {
                var observedNetwork=networkChoice;
                var available=await IsNetworkAvailableAsync();
                if(!ReferenceEquals(server,observedServer)||!ReferenceEquals(networkChoice,observedNetwork))return;
                if(!available) {Log("选中的网络线路已断开或身份变化，停止传输。");await StopAsync();return;}
                if(capture is null)
                {
                    if(DateTime.UtcNow-sessionStartedUtc>TimeSpan.FromMinutes(5)) {Log("配对二维码已超时，请重新开始连接。");await StopAsync();}
                    return;
                }
            }
            else return;
            if(server is not null)
            {
                if(server.ClientDisplayProfile is { } changed&&tabletProfile is { } configured
                    &&(changed.Width!=configured.Width||changed.Height!=configured.Height||changed.RequestedRefreshRate!=configured.RequestedRefreshRate))
                {await AdaptDisplayAsync(server,changed);return;}
                var now=DateTime.UtcNow;
                var inputDesktop=InputDesktopAvailability.Query();
                var presentation=presentationDeadline.Evaluate(now,server.LastClientProgressUtc,server.CapturePaused,inputDesktop);
                var capturePaused=presentation.CapturePaused;
                var deadline=presentation.DeadlineUtc;
                var resumeDeadlineUtc=presentationDeadline.RecoveryDeadlineUtc;
                displayGuard?.Renew(deadline);
                if(now>deadline)
                {const string message="平板超过 20 秒没有报告新的客户端进度，自动停止副屏。";MarkConnectionHealthAttention(message);Log(message);await StopAsync();return;}
                var receiving=!capturePaused&&server.ClientConnected&&server.LastClientProgressUtc>now.AddSeconds(-5);
                if(capturePaused)status.Text="画面采集正在恢复，连接保留";
                else if(receiving)status.Text="平板已连接，正在传输副屏";
                if(receiving&&inputDesktop.IsAvailable&&displayGuard is not null)
                {
                    // Store only a freshly verified position of this same output.
                    // A transition may race this read; capture recovery owns the retry.
                    try
                    {
                        var live=displayGuard.RefreshRememberedLayout();
                        if(displays.SelectedItem is not DisplayChoice selected||selected.Info.Bounds!=live.Bounds)
                        {displays.Items.Clear();displays.Items.Add(new DisplayChoice(live));displays.SelectedIndex=0;}
                    }
                    catch(IOException){}
                }
                var sample=DateTime.UtcNow;var delta=server.PresentedFrames-previousPresented;
                var measuredFps=delta<0?0:delta/Math.Max(0.001,(sample-previousSampleUtc).TotalSeconds);
                previousPresented=server.PresentedFrames;previousSampleUtc=sample;
                var progressText=capturePaused?"画面暂停 · 会话保留":server.HasRecentPresentation?"设备已实际显示":server.HasRecentSubmission?"解码提交正常 · 呈现待验证":server.FramesSent>0?"电脑已发送 · 等待解码":"等待画面";
                metrics.Text=$"{progressText} · {tabletProfile?.Width} × {tabletProfile?.Height} · 屏幕 {server.ClientDisplayProfile?.RefreshRate??tabletProfile?.RefreshRate:F0} / 目标 {tabletProfile?.RequestedRefreshRate} Hz · 提交 {server.ClientSubmittedFps:F1} / 呈现 {server.ClientPresentedFps:F1} 帧/秒";
                Diagnostics.Save("session-health.json",()=>new{timestamp=DateTimeOffset.Now,pid=Environment.ProcessId,serial=approved?.Serial,transport=networkChoice is null?"ADB":"TLS",networkInterface=networkChoice?.InterfaceAlias,receiving,capturePaused,inputDesktop,resumeDeadlineUtc,windowVisible=Visible,measuredPresentedFps=measuredFps,server.ClientSubmittedFps,server.ClientPresentedFps,server.ClientDecoder,targetProfile=tabletProfile,clientProfile=server.ClientDisplayProfile,server.FramesSent,server.PresentedFrames,server.PresentedWidth,server.PresentedHeight,server.LastPresentedUtc,server.SubmittedFrames,server.LastSubmittedUtc,server.HasRecentSubmission,server.HasRecentPresentation,sendPerformance=server.SendPerformance,displays=VirtualDisplayManager.GetDisplays()},Log);
                RefreshPrimaryConnectionHealth(server);
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){if(ReferenceEquals(server,observedServer)&&ReferenceEquals(approved,observedApproval)){Log("连接监测失败："+ex.Message);await StopAsync();}}finally{monitoring=false;}
    }
    void LoadRules(){rules.Items.Clear();foreach(var rule in settings.ExcludedDevices)rules.Items.Add(new RuleChoice(rule));}
    async Task AddRuleAsync()
    {
        var rule=new DeviceExclusionRule{Serial=string.IsNullOrWhiteSpace(serial.Text)?null:serial.Text.Trim(),Vid=string.IsNullOrWhiteSpace(vid.Text)?null:vid.Text.Trim().ToUpperInvariant(),Pid=string.IsNullOrWhiteSpace(pid.Text)?null:pid.Text.Trim().ToUpperInvariant(),Label=label.Text.Trim()};
        if(rule.Serial is null&&(rule.Vid is null||rule.Pid is null))throw new ArgumentException("请填写设备序列号，或同时填写四位 VID 和 PID。");
        if(settings.ExcludedDevices.Any(x=>string.Equals(x.Serial,rule.Serial,StringComparison.OrdinalIgnoreCase)&&string.Equals(x.Vid,rule.Vid,StringComparison.OrdinalIgnoreCase)&&string.Equals(x.Pid,rule.Pid,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("相同的排除规则已经存在。");
        await SaveRulesAsync([..settings.ExcludedDevices,rule]);
        serial.Clear();vid.Clear();pid.Clear();label.Clear();Log("排除规则已添加并保存。");
    }
    async Task RemoveRuleAsync()
    {
        if(rules.SelectedItem is not RuleChoice choice)return;
        await SaveRulesAsync(settings.ExcludedDevices.Where(x=>!ReferenceEquals(x,choice.Rule)).ToList());
        Log("已删除选中的排除规则。");
    }
    async Task SaveRulesAsync(List<DeviceExclusionRule> nextRules)
    {
        var next=new DevicePolicySettings{SchemaVersion=settings.SchemaVersion,AdbPath=settings.AdbPath,ExcludedDevices=nextRules};
        DevicePolicy.ValidateSettings(next);
        // Stop before changing the policy so owned ADB reverse cleanup still
        // has its valid approval. Network sessions unrelated to this rule stay up.
        var stopCurrent=approved is not null;
        if(networkChoice is {Kind:NetworkInterfaceKind.Usb} selected)
        {
            var choices=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(next),lifetime.Token);
            stopCurrent=!choices.Any(x=>SameInterface(x,selected));
        }
        if(stopCurrent){Log("排除策略更新：先结束受影响的 USB 会话，清理本次连接。");await StopAsync();}
        await ApplyAdditionalPolicyAsync(next);
        store.Save(next);
        // Publish an entire new list; never mutate a list being enumerated by
        // a background USB/network validation operation.
        settings.ExcludedDevices=nextRules;
        LoadRules();
        if(server is null)await RefreshNetworksAsync();
    }
    async Task GuardAsync(Func<Task> action)
    {
        if(busy||stopping)return;busy=true;UpdateButtons();
        try{await action();}catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}catch(Exception ex){ShowError(ex);}finally{busy=false;if(!IsDisposed)UpdateButtons();}
    }
    void ShowError(Exception ex)
    {
        if(HasPendingOwnedDisplayCleanup)MarkOwnedDisplayCleanupAttention(OwnedDisplayCleanupFailureDetail(ex));
        else MarkConnectionHealthAttention(ex.Message);
        Log(ex.Message);status.Text="需要处理连接条件";
        if(Visible&&WindowState!=FormWindowState.Minimized)MessageBox.Show(this,ex.Message,"TabLink",MessageBoxButtons.OK,MessageBoxIcon.Information);
        else tray.ShowBalloonTip(5000,"TabLink 需要处理连接条件",ex.Message,ToolTipIcon.Info);
    }
    void UpdateButtons()
    {
        var idle=!busy&&!stopping&&!closing&&!HasAnySessions&&settingsValid;
        connectionMode.Enabled=!busy&&!stopping&&!closing&&!HasAnySessions;
        refresh.Enabled=idle;chooseAdb.Enabled=idle;connect.Enabled=idle&&adb is not null;installApk.Enabled=idle&&adb is not null;
        devices.Enabled=idle;displays.Enabled=idle;touch.Enabled=idle;stop.Enabled=!busy&&!stopping&&server is not null;
        trayStop.Enabled=!closing&&HasAnySessions;
        var canEditRules=!busy&&!stopping&&!closing&&settingsValid;
        repairAdb.Enabled=canEditRules;
        addRule.Enabled=canEditRules;removeRule.Enabled=canEditRules&&rules.SelectedItem is RuleChoice;serial.Enabled=canEditRules;vid.Enabled=canEditRules;pid.Enabled=canEditRules;label.Enabled=canEditRules;
        UpdateNetworkButtons(idle);
        UpdateAdditionalButtons();
        UpdateHealthRepairButton();
    }
    sealed record DeviceChoice(AdbDevice Device,DevicePolicyDecision Decision){public override string ToString()=>$"{Device.Model?.Replace('_',' ')??"Android"} · {Device.Serial}  {(Decision.Allowed?"USB 已验证":"[已阻止] "+Decision.Reason)}";}
    sealed record DisplayChoice(VirtualDisplayInfo Info){public override string ToString()=>$"{Info.FriendlyName} · {Info.Bounds.Width} × {Info.Bounds.Height} · {Info.DeviceName}";}
    sealed record RuleChoice(DeviceExclusionRule Rule){public override string ToString()=>$"{Rule.Label??"排除设备"}     {(Rule.Serial is null?"":"序列号 "+Rule.Serial)}  {(Rule.Vid is null?"":"VID:PID "+Rule.Vid+":"+Rule.Pid)}";}
}
