using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    // Developer-only rendering of this app's own controls, without showing the
    // form or invoking connection, display, driver or certificate operations.
    internal static void RenderUi(string directory)
    {
        Directory.CreateDirectory(directory);
        RenderLanguageSuite(Path.Combine(directory,"zh-CN"),ProductLanguage.SimplifiedChinese);
        RenderLanguageSuite(Path.Combine(directory,"en"),ProductLanguage.English);
    }

    static void RenderLanguageSuite(string directory,ProductLanguage language)
    {
        Directory.CreateDirectory(directory);
        var views=new (string Name,int Tab,int Mode)[]
        {
            ("connection-client",0,0),("connection-browser",0,1),("connection-usb-debug",0,2),
            ("device-settings",1,-1),("diagnostics-log",2,-1)
        };
        VerifyWindowSizingPolicy();
        foreach(var view in views)RenderView(directory,view.Name,view.Tab,view.Mode,PreferredExpandedWindowSize,language,
            requireNoInternalScroll:view.Tab==0&&view.Mode==0);
        RenderHelpView(directory,language);
        foreach(var view in views)RenderView(directory,"compact-"+view.Name,view.Tab,view.Mode,PreferredCompactWindowSize,language);
        RenderView(directory,"compact-settings-author-custom",1,-1,new Size(760,640),language,form=>
        {
            var custom=new AuthorFooterPreferences
            {
                Enabled=true,
                SummaryText=language==ProductLanguage.SimplifiedChinese?"只有一块副屏，可自定义底部提示":"One second screen; the footer summary can be customised",
                AuthorText=language==ProductLanguage.SimplifiedChinese?"作者：这是一段用于验证窄窗口与高 DPI 自动换行的较长自定义显示名称（团队 / 社区维护者）":"Author: A deliberately long custom display name used to verify narrow-window and high-DPI wrapping (team / community maintainer)",
                GitHubLabel=language==ProductLanguage.SimplifiedChinese?"项目主页、源代码和问题反馈":"Project home, source and issue tracker",
                GitHubUrl="https://github.com/example/example-project",
                BlogLabel=language==ProductLanguage.SimplifiedChinese?"博客、教程与完整使用说明":"Blog, tutorials and full guide",
                BlogUrl="https://example.com/tablink/guide"
            };
            form.PopulateAuthorFooterEditors(custom);form.ApplyAuthorFooterPreferences(custom);
        });
        RenderView(directory,"compact-footer-hidden",0,0,new Size(760,640),language,form=>
        {
            var hidden=AuthorFooterPreferences.CreateDefault();hidden.Enabled=false;
            form.PopulateAuthorFooterEditors(hidden);form.ApplyAuthorFooterPreferences(hidden);
        });
        RenderView(directory,"compact-footer-maximum",0,0,new Size(760,640),language,form=>
        {
            var maximum=new AuthorFooterPreferences
            {
                Enabled=true,
                SummaryText=new string(language==ProductLanguage.SimplifiedChinese?'提':'S',AuthorFooterPreferences.MaximumSummaryTextLength),
                AuthorText=new string(language==ProductLanguage.SimplifiedChinese?'作':'A',AuthorFooterPreferences.MaximumAuthorTextLength),
                GitHubLabel=new string('G',AuthorFooterPreferences.MaximumLinkLabelLength),
                GitHubUrl="https://github.com/example/example-project",
                BlogLabel=new string(language==ProductLanguage.SimplifiedChinese?'博':'B',AuthorFooterPreferences.MaximumLinkLabelLength),
                BlogUrl="https://example.com/tablink/guide"
            };
            form.PopulateAuthorFooterEditors(maximum);form.ApplyAuthorFooterPreferences(maximum);
        });
    }
    static void RenderView(string directory,string name,int tab,int mode,Size size,ProductLanguage language,Action<MainForm>? configure=null,
        bool requireNoInternalScroll=false)
    {
        Console.WriteLine($"render:start {language}/{name}");
        var form=new MainForm(verification:true,verificationLanguage:language);
        try
        {
            form.activationTimer.Stop();form.monitor.Stop();form.tray.Visible=false;
            form.Size=size;form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
            form.mainTabs.SelectedIndex=tab;
            if(mode>=0)form.connectionMode.SelectedIndex=mode;
            configure?.Invoke(form);
            PrepareView(form);
            if(name.EndsWith("connection-browser",StringComparison.Ordinal))VerifyBrowserButtonContract(form);
            Console.WriteLine($"render:prepared {language}/{name}");
            VerifyLanguageContract(form,language,
                verifyDynamicStatus:name=="connection-client"&&language==ProductLanguage.English,
                verifyDefaultFooter:configure is null);
            Console.WriteLine($"render:verified {language}/{name}");
            if(requireNoInternalScroll&&form.clientConnectionPanel is ScrollableControl client&&
                (client.VerticalScroll.Visible||client.HorizontalScroll.Visible))
                throw new InvalidOperationException($"默认展开窗口仍需滚动才能查看连接副屏内容：client={client.ClientSize}，display={client.DisplayRectangle.Size}，vertical={client.VerticalScroll.Visible}，horizontal={client.HorizontalScroll.Visible}，children={string.Join(';',client.Controls.Cast<Control>().SelectMany(value=>new[]{ $"{value.GetType().Name}:{value.Bounds}" }.Concat(value.Controls.Cast<Control>().Select(child=>$"{child.GetType().Name}:{child.Bounds}:visible={child.Visible}:margin={child.Margin}"))))}。");
            if(tab==2&&form.mainTabs.TabPages[2].Controls[0] is TableLayoutPanel support&&
                support.GetControlFromPosition(0,0) is ScrollableControl diagnostics&&diagnostics.HorizontalScroll.Visible)
                throw new InvalidOperationException($"检测页不应出现水平滚动条：client={diagnostics.ClientSize}，display={diagnostics.DisplayRectangle.Size}，children={string.Join(';',diagnostics.Controls.Cast<Control>().Select(value=>$"{value.GetType().Name}:{value.Bounds}:preferred={value.PreferredSize}:maximum={value.MaximumSize}"))}。");
            Save(form,directory,name);
            Console.WriteLine($"render:saved {language}/{name}");
        }
        finally
        {
            form.closing=true;
            form.DisposeAutomaticUpdates();
            form.activationTimer.Dispose();form.monitor.Dispose();
            form.tray.Visible=false;form.tray.Dispose();form.trayMenu.Dispose();
            form.nativeTrust?.Dispose();form.lifetime.Dispose();form.Dispose();
            Console.WriteLine($"render:disposed {language}/{name}");
        }
    }

    static void RenderHelpView(string directory,ProductLanguage language)
    {
        Console.WriteLine($"render:start {language}/help");
        using var owner=new MainForm(verification:true,verificationLanguage:language);
        owner.activationTimer.Stop();owner.monitor.Stop();owner.tray.Visible=false;
        using var help=owner.CreateHelpWindow();
        help.ShowInTaskbar=false;help.Opacity=0;help.Show();Application.DoEvents();
        try
        {
            help.PerformLayout();help.Refresh();Application.DoEvents();
            var expected=language==ProductLanguage.SimplifiedChinese?"TabLink 使用帮助":"TabLink Help";
            if(help.Text!=expected||!FindControlText(help,expected))
                throw new InvalidOperationException("帮助窗口未按当前语言完整呈现。");
            if(FindNamedControl(help,"helpBody") is not ScrollableControl body||body.HorizontalScroll.Visible||
                FindNamedControl(help,"helpClose") is not Button{Visible:true} close||
                !help.RectangleToScreen(help.ClientRectangle).Contains(close.RectangleToScreen(close.ClientRectangle)))
                throw new InvalidOperationException("帮助窗口出现水平裁切或关闭按钮不可见。");
            Save(help,directory,"help");
            Console.WriteLine($"render:saved {language}/help");
        }
        finally
        {
            help.Close();owner.closing=true;owner.DisposeAutomaticUpdates();owner.activationTimer.Dispose();owner.monitor.Dispose();
            owner.tray.Visible=false;owner.tray.Dispose();owner.trayMenu.Dispose();owner.nativeTrust?.Dispose();owner.lifetime.Dispose();
            Console.WriteLine($"render:disposed {language}/help");
        }
    }

    static bool FindControlText(Control root,string text)=>root.Text==text||root.Controls.Cast<Control>().Any(child=>FindControlText(child,text));
    static Control? FindNamedControl(Control root,string name)=>root.Name==name?root:
        root.Controls.Cast<Control>().Select(child=>FindNamedControl(child,name)).FirstOrDefault(found=>found is not null);

    static void VerifyBrowserButtonContract(MainForm form)
    {
        form.browserNetworks.Items.Clear();
        form.browserNetworks.Items.Add(new NetworkInterfaceChoice(System.Net.IPAddress.Parse("192.0.2.20"),
            "UI verification route",24,NetworkInterfaceKind.WiFi,null,"00000000-0000-0000-0000-000000000020",20));
        form.browserNetworks.SelectedIndex=0;form.UpdateBrowserButtons(ready:true);
        if(form.startBrowser.Enabled!=BrowserRtcSession.IsSupported)
            throw new InvalidOperationException("浏览器接入按钮没有反映当前构建是否包含接收组件。");
    }

    static void VerifyLanguageContract(MainForm form,ProductLanguage language,bool verifyDynamicStatus,bool verifyDefaultFooter)
    {
        var english=language==ProductLanguage.English;
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        Require(form.Text==(english?"TabLink · Second screen":"TabLink · 平板副屏"),"窗口标题未按验证语言呈现。");
        Require(form.mainTabs.TabPages.Cast<TabPage>().Select(page=>page.Text).SequenceEqual(english
            ?["Connect display","Settings","Diagnostics & logs"]
            :["连接副屏","设置","检测与日志"]),"主标签未完整本地化。");
        var clientBounds=form.RectangleToScreen(form.ClientRectangle);
        var settingsBounds=form.openSettings.RectangleToScreen(form.openSettings.ClientRectangle);
        var helpBounds=form.openHelp.RectangleToScreen(form.openHelp.ClientRectangle);
        Require(form.openSettings.Text==(english?"Settings":"设置")&&form.openHelp.Text==(english?"Help":"帮助")&&
            form.openSettings.Visible&&form.openHelp.Visible&&
            clientBounds.Contains(settingsBounds)&&clientBounds.Contains(helpBounds),
            $"标题区设置和帮助按钮未完整呈现或超出窗口客户区：client={clientBounds}，settings={settingsBounds}，help={helpBounds}。");
        var selectedTab=form.mainTabs.SelectedIndex;form.openSettings.PerformClick();
        Require(form.mainTabs.SelectedIndex==1,"标题区设置按钮没有打开设置页。");
        form.mainTabs.SelectedIndex=selectedTab;
        if(verifyDefaultFooter)
            Require(form.footerContent.Visible&&form.footerBehaviour.Text==(english
                ?"One second screen only; selecting × keeps TabLink running in the tray"
                :"只有一块副屏，点 × 后在托盘继续运行")&&
                form.authorFooterAuthor.Text==(english?"Author: Zhang Linjie (Jey / @linjierd)":"作者：张林杰（Jey / @linjierd）")&&
                form.authorFooterGitHub.Text=="GitHub"&&form.authorFooterBlog.Text==(english?"Blog: linjie.space":"博客：linjie.space"),
                "默认底部提示、作者和链接未完整呈现。");
        Require(form.connectionMode.Items.Cast<object>().Select(item=>item.ToString()).SequenceEqual(english
            ?["TabLink client (recommended)","Browser connection","USB debugging (compatibility)"]
            :["TabLink 客户端（推荐）","浏览器接入","USB 调试（兼容）"]),"连接方式未完整本地化。");
        Require(form.healthStages.Columns.Cast<ColumnHeader>().Select(column=>column.Text).SequenceEqual(english
            ?["Connection stage","State","Current evidence and guidance"]
            :["连接阶段","状态","当前证据与建议"]),"检测列表列标题未完整本地化。");
        Require(form.healthStages.Columns[0].Width==(english?340:210),"检测列表阶段列没有按语言保留完整标题宽度。");
        Require(form.systemLanguage.Text==(english?"Follow system":"跟随系统")&&
            form.chineseLanguage.Text=="简体中文"&&form.englishLanguage.Text=="English", "语言选择器未完整本地化。");
        Require(language==ProductLanguage.SimplifiedChinese?form.chineseLanguage.Checked:form.englishLanguage.Checked,
            "验证窗口没有使用显式语言覆盖。");
        Require(form.automaticUpdates.Text==(english?"Automatic updates (recommended)":"自动更新（推荐）")&&
            form.downloadThenAskUpdates.Text==(english?"Download automatically, install manually":"自动下载后手动安装")&&
            form.neverUpdates.Text==(english?"Never update":"从不更新"),"更新设置未完整本地化。");
        if(verifyDynamicStatus)VerifyActiveStatusLanguageRoundTrip(form,language);
    }

    static void VerifyActiveStatusLanguageRoundTrip(MainForm form,ProductLanguage requestedLanguage)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        var originalLanguage=form.uiLanguage;
        var originalChinese=form.localizedStatusChinese;
        var originalEnglish=form.localizedStatusEnglish;
        var originalRuntime=form.runtimeStatusSource;
        var originalServer=form.server;
        var originalCapture=form.capture;
        VerifyChoiceLocalization(form);
        VerifyEncoderMetricsLocalization(form);
        VerifyTransientLanguageRoundTrip(form);
        var session=(FrameServer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FrameServer));
        var capture=(DesktopCapture)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopCapture));
        var cases=new (string Chinese,string English)[]
        {
            ("正在为选中的线路准备加密配对…","Preparing encrypted pairing on the selected route…"),
            ("平板已连接，正在传输副屏","Tablet connected; streaming the second screen"),
            ("画面采集正在恢复，连接保留","Video capture is recovering; the connection remains active"),
            ("USB 通道中断，正在自动恢复（2/3）…","USB channel interrupted; recovering automatically (2/3)…"),
            ("正在安装已验证的正式版更新…","Installing the verified stable update…")
        };
        try
        {
            form.server=session;
            form.capture=capture;
            for(var index=0;index<cases.Length;index++)
            {
                var item=cases[index];
                form.ShowStatus(item.Chinese,item.English);
                form.uiLanguage=ProductLanguage.English;
                if(index==0)form.ApplyUiLanguage();else form.RefreshLocalizedStatus();
                Require(form.status.Text==item.English,"活动状态切换到 English 后未保留动态语义。");
                Require(ReferenceEquals(form.server,session)&&ReferenceEquals(form.capture,capture),
                    "切换界面语言时替换或断开了活动会话对象。");
                form.uiLanguage=ProductLanguage.SimplifiedChinese;
                if(index==0)form.ApplyUiLanguage();else form.RefreshLocalizedStatus();
                Require(form.status.Text==item.Chinese,"活动状态从 English 切回简体中文后未恢复动态语义。");
                Require(ReferenceEquals(form.server,session)&&ReferenceEquals(form.capture,capture),
                    "切回界面语言时替换或断开了活动会话对象。");
            }
        }
        finally
        {
            form.server=originalServer;
            form.capture=originalCapture;
            form.localizedStatusChinese=originalChinese;
            form.localizedStatusEnglish=originalEnglish;
            form.runtimeStatusSource=originalRuntime;
            form.uiLanguage=originalLanguage;
            form.ApplyUiLanguage();
            Require(form.uiLanguage==requestedLanguage,"动态状态验证后未恢复请求的界面语言。");
        }
    }

    static void VerifyChoiceLocalization(MainForm form)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        var originalLanguage=form.uiLanguage;
        var originalItems=form.networks.Items.Cast<object>().ToArray();
        var originalSelected=form.networks.SelectedItem;
        try
        {
            var wifi=new NetworkInterfaceChoice(System.Net.IPAddress.Parse("192.0.2.10"),"Tablet Wi-Fi",24,NetworkInterfaceKind.WiFi,null,"wifi-id",7);
            var usb=new NetworkInterfaceChoice(System.Net.IPAddress.Parse("192.0.2.20"),"USB tether",24,NetworkInterfaceKind.Usb,"serial", "usb-id",8);
            var ethernet=new NetworkInterfaceChoice(System.Net.IPAddress.Parse("192.0.2.30"),"Dock",24,NetworkInterfaceKind.Ethernet,null,"ethernet-id",9);
            form.networks.Items.Clear();
            form.networks.Items.AddRange([wifi,usb,ethernet,"尚无可用线路：请连接 Wi-Fi 或开启平板 USB 网络共享"]);
            form.networks.SelectedItem=usb;
            form.uiLanguage=ProductLanguage.English;
            form.RebuildNetworkChoiceDisplay(form.networks);
            var english=form.networks.Items.Cast<object>().Select(item=>form.networks.GetItemText(item)??"").ToArray();
            Require(english[0].StartsWith("Wi-Fi ·",StringComparison.Ordinal)&&
                english[1].StartsWith("USB network ·",StringComparison.Ordinal)&&
                english[2].StartsWith("Ethernet ·",StringComparison.Ordinal)&&
                english[3].StartsWith("No route is available",StringComparison.Ordinal)&&
                (form.networks.SelectedItem as NetworkInterfaceChoice)?.InterfaceId=="usb-id",
                "English network choices or stable InterfaceId selection were not preserved.");
            form.uiLanguage=ProductLanguage.SimplifiedChinese;
            form.RebuildNetworkChoiceDisplay(form.networks);
            var chinese=form.networks.Items.Cast<object>().Select(item=>form.networks.GetItemText(item)??"").ToArray();
            Require(chinese[0].StartsWith("Wi-Fi ·",StringComparison.Ordinal)&&
                chinese[1].StartsWith("USB 网络 ·",StringComparison.Ordinal)&&
                chinese[2].StartsWith("有线网络 ·",StringComparison.Ordinal)&&
                chinese[3].StartsWith("尚无可用线路",StringComparison.Ordinal)&&
                (form.networks.SelectedItem as NetworkInterfaceChoice)?.InterfaceId=="usb-id",
                "中文网络选项或稳定 InterfaceId 选择未保留。");
            var profile=new TabletDisplayProfile(1200,1920,0,1,90,1200,1920,[new(1200,1920,90,1)]);
            Require(new RequestedMode(profile,ProductLanguage.English).ToString().EndsWith(" (reported by device)",StringComparison.Ordinal)&&
                new RequestedMode(profile,ProductLanguage.SimplifiedChinese).ToString().EndsWith("（设备报告）",StringComparison.Ordinal),
                "设备请求显示模式未按界面语言呈现。");
        }
        finally
        {
            form.networks.Items.Clear();
            form.networks.Items.AddRange(originalItems);
            form.networks.SelectedItem=originalSelected;
            form.uiLanguage=originalLanguage;
        }
    }

    static void VerifyEncoderMetricsLocalization(MainForm form)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        var originalLanguage=form.uiLanguage;
        var originalRuntime=form.encoderRuntime;
        var originalMetrics=form.metrics.Text;
        var originalMetricsChinese=form.metricsChinese;
        var originalMetricsEnglish=form.metricsEnglish;
        try
        {
            form.encoderRuntime=new VideoEncoderRuntimeSnapshot("LibX264","libx264",false,1920,1200,90,30,true,
                "设备请求 90 fps，软件兼容模式最高 30 fps",null,null,null,null,null,[]);
            form.uiLanguage=ProductLanguage.English;
            form.RefreshEncoderMetrics();
            Require(form.metrics.Text.Contains("Encoder Software x264",StringComparison.Ordinal)&&
                form.metrics.Text.Contains("90 fps",StringComparison.Ordinal)&&form.metrics.Text.Contains("30 fps",StringComparison.Ordinal)&&
                !WindowsUiText.ContainsHan(form.metrics.Text),"English encoder metrics contain untranslated text or lost frame-rate evidence.");
            form.uiLanguage=ProductLanguage.SimplifiedChinese;
            form.RefreshEncoderMetrics();
            Require(form.metrics.Text.Contains("编码 软件 x264",StringComparison.Ordinal)&&
                form.metrics.Text.Contains("设备请求 90 fps",StringComparison.Ordinal),"中文编码器指标未恢复原始语义。");
        }
        finally
        {
            form.encoderRuntime=originalRuntime;
            form.metrics.Text=originalMetrics;
            form.metricsChinese=originalMetricsChinese;
            form.metricsEnglish=originalMetricsEnglish;
            form.uiLanguage=originalLanguage;
        }
    }

    static void VerifyTransientLanguageRoundTrip(MainForm form)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        var originalLanguage=form.uiLanguage;
        var originalPairingText=form.pairingHint.Text;
        var originalPairingChinese=form.pairingHintChinese;
        var originalPairingEnglish=form.pairingHintEnglish;
        var originalBrowserText=form.browserHint.Text;
        var originalBrowserChinese=form.browserHintChinese;
        var originalBrowserEnglish=form.browserHintEnglish;
        var originalMetricsText=form.metrics.Text;
        var originalMetricsChinese=form.metricsChinese;
        var originalMetricsEnglish=form.metricsEnglish;
        var originalDiagnosticText=form.diagnosticReport.Text;
        var originalDiagnosticLanguage=form.diagnosticReportLanguage;
        var originalDiagnosticNeedsRerun=form.diagnosticReportNeedsRerun;
        try
        {
            form.SetPairingHint("动态配对 1920 × 1200 @ 90 Hz","Dynamic pairing 1920 × 1200 @ 90 Hz");
            form.SetBrowserHint("二维码到期 12:34:56","QR code expires at 12:34:56");
            form.SetMetrics("设备呈现回调正常 · 90 帧/秒","Device presentation callback healthy · 90 fps");
            form.diagnosticReport.Text="旧语言检测结果";
            form.diagnosticReportLanguage=ProductLanguage.SimplifiedChinese;
            form.diagnosticReportNeedsRerun=false;
            form.uiLanguage=ProductLanguage.English;
            form.RefreshLocalizedTransientText();
            Require(form.pairingHint.Text.StartsWith("Dynamic pairing",StringComparison.Ordinal)&&
                form.browserHint.Text.StartsWith("QR code expires",StringComparison.Ordinal)&&
                form.metrics.Text.Contains("90 fps",StringComparison.Ordinal)&&
                form.diagnosticReport.Text.StartsWith("The interface language changed",StringComparison.Ordinal)&&
                !WindowsUiText.ContainsHan(form.pairingHint.Text+form.browserHint.Text+form.metrics.Text+form.diagnosticReport.Text),
                "English transient pairing, browser, metrics or diagnostics text was not redrawn safely.");
            form.uiLanguage=ProductLanguage.SimplifiedChinese;
            form.RefreshLocalizedTransientText();
            Require(form.pairingHint.Text.StartsWith("动态配对",StringComparison.Ordinal)&&
                form.browserHint.Text.StartsWith("二维码到期",StringComparison.Ordinal)&&
                form.metrics.Text.Contains("90 帧/秒",StringComparison.Ordinal)&&
                form.diagnosticReport.Text.StartsWith("界面语言已切换",StringComparison.Ordinal),
                "中文动态配对、浏览器、性能或检测提示未恢复。" );
        }
        finally
        {
            form.pairingHint.Text=originalPairingText;
            form.pairingHintChinese=originalPairingChinese;form.pairingHintEnglish=originalPairingEnglish;
            form.browserHint.Text=originalBrowserText;
            form.browserHintChinese=originalBrowserChinese;form.browserHintEnglish=originalBrowserEnglish;
            form.metrics.Text=originalMetricsText;
            form.metricsChinese=originalMetricsChinese;form.metricsEnglish=originalMetricsEnglish;
            form.diagnosticReport.Text=originalDiagnosticText;
            form.diagnosticReportLanguage=originalDiagnosticLanguage;
            form.diagnosticReportNeedsRerun=originalDiagnosticNeedsRerun;
            form.uiLanguage=originalLanguage;
        }
    }
    static void PrepareView(MainForm form)
    {
        form.mainTabs.SelectedTab!.CreateControl();form.ShowConnectionMode();
        var original=form.Size;
        form.Size=new Size(original.Width+1,original.Height+1);Application.DoEvents();
        form.Size=original;
        for(var pass=0;pass<3;pass++)
        {
            form.connectionHost.PerformLayout();form.mainTabs.PerformLayout();form.PerformLayout();
            form.Refresh();Application.DoEvents();
        }
        using var warmup=new Bitmap(form.Width,form.Height);
        form.DrawToBitmap(warmup,new Rectangle(Point.Empty,form.Size));
        form.Refresh();Application.DoEvents();
    }

    static void VerifyWindowSizingPolicy()
    {
        var large=CalculateInitialWindowBounds(new Rectangle(0,0,2048,1232),96);
        if(large!=new Rectangle(484,56,1080,1120))
            throw new InvalidOperationException("大工作区默认窗口居中验证失败。");
        var standard=CalculateInitialWindowBounds(new Rectangle(0,0,1920,1040),96);
        if(standard!=new Rectangle(420,0,1080,1040))
            throw new InvalidOperationException("1080p 工作区默认窗口验证失败。");
        var shortWork=CalculateInitialWindowBounds(new Rectangle(0,0,1366,728),96);
        if(shortWork!=new Rectangle(143,0,1080,728))
            throw new InvalidOperationException("低高度工作区约束验证失败。");
        var smallWork=new Rectangle(-800,40,800,600);
        var small=CalculateInitialWindowBounds(smallWork,144);
        if(small!=smallWork)
            throw new InvalidOperationException("小屏工作区约束验证失败。");
        if(ScaleLogicalSize(PreferredExpandedWindowSize,144)!=new Size(1620,1680))
            throw new InvalidOperationException("逻辑窗口尺寸的 DPI 换算验证失败。");
    }
    static void Save(Form form,string directory,string name)
    {
        using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
        using(var graphics=Graphics.FromImage(bitmap))
        {
            var root=(TableLayoutPanel)form.Controls[0];
            var clientLeft=(form.Width-form.ClientSize.Width)/2;
            var clientTop=form.Height-form.ClientSize.Height-clientLeft;
            foreach(Control control in root.Controls)
            {
                if(!control.Visible||control.Width<=0||control.Height<=0)continue;
                using var layer=new Bitmap(control.Width,control.Height);
                control.DrawToBitmap(layer,new Rectangle(Point.Empty,control.Size));
                using var encoded=new MemoryStream();
                layer.Save(encoded,System.Drawing.Imaging.ImageFormat.Png);encoded.Position=0;
                using var rendered=new Bitmap(encoded);
                graphics.DrawImageUnscaled(rendered,clientLeft+root.Left+control.Left,clientTop+root.Top+control.Top);
            }
        }
        using var flattened=new Bitmap(bitmap.Width,bitmap.Height,System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(flattened))graphics.DrawImageUnscaled(bitmap,Point.Empty);
        flattened.Save(Path.Combine(directory,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
    }
}
