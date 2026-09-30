using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly TextBox diagnosticReport=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
    readonly Button diagnose=new(){Text="检测连接"};
    readonly Button repairAdb=new(){Text="修复：使用内置 ADB"};
    readonly Button repairConnection=new(){Text="修复：重建选中连接"};
    readonly ComboBox requestedModes=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=310};
    readonly Button refreshModes=new(){Text="读取设备请求模式"};
    readonly Button repairMode=new(){Text="配置选中显示模式"};
    bool diagnosing;

    Control BuildDiagnosticsPanel()
    {
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(18)};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.Controls.Add(new Label{Dock=DockStyle.Fill,ForeColor=muted,Text="检测工具、USB 授权、排除规则、网络和副屏资源；结果会说明可执行的修复。\nADB 仅用于安卓 USB 调试兼容模式。免调试连接请开启设备的 USB 网络共享或使用 Wi-Fi。\n浏览器首次连接需信任本机证书。苹果与 HarmonyOS NEXT 不使用 ADB。"},0,0);
        layout.Controls.Add(Flow(diagnose,repairAdb,repairConnection),0,1);
        layout.Controls.Add(diagnosticReport,0,2);page.Controls.Add(layout);
        diagnose.Click+=async(_,_)=>await DiagnoseAsync();
        repairAdb.Click+=async(_,_)=>await GuardAsync(RepairAdbAsync);
        repairConnection.Click+=async(_,_)=>await GuardAsync(RepairConnectionAsync);
        return page;
    }

    void RefreshRequestedModes()
    {
        requestedModes.Items.Clear();
        var profiles=additionalSessions.Select(x=>x.RequestedProfile).Append(lastRequestedProfile).Where(x=>x is not null).Cast<TabletDisplayProfile>()
            .DistinctBy(p=>(p.Width,p.Height,p.RequestedRefreshRate));
        foreach(var profile in profiles)requestedModes.Items.Add(new RequestedMode(profile));
        if(requestedModes.Items.Count>0)requestedModes.SelectedIndex=0;
        else diagnosticReport.Text="尚未收到原生客户端屏幕参数。连接时会自动读取参数、写入唯一副屏模式并按需安装驱动。";
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
                catch(Exception ex){found.Add(new("配置","需要处理",ex.Message+"；请修正配置文件，检测不会覆盖损坏的配置。"));return found;}
                var bundled=Path.Combine(AppContext.BaseDirectory,"tools","platform-tools","adb.exe");
                var verification=VerifyBundledAdb(bundled);
                found.Add(new("内置 ADB",verification is null?"通过":"需要处理",verification??"三件套哈希与随包固定 Google 版本一致。"));
                var adbLocation=AdbLocator.FindAdbPath(snapshot.AdbPath);
                if(verification is not null&&string.Equals(adbLocation,bundled,StringComparison.OrdinalIgnoreCase))adbLocation=null;
                if(adbLocation is null)found.Add(new("ADB 选择","需要处理","当前 ADB 路径失效。内置组件完整时，可点击“修复：使用内置 ADB”。"));
                else
                {
                    var version=await new AdbProcessRunner().RunAsync(adbLocation,["version"],TimeSpan.FromSeconds(4),deadline.Token);
                    found.Add(new("ADB 执行",version.ExitCode==0?"通过":"需要处理",version.ExitCode==0?adbLocation+"\r\n"+version.StandardOutput.Trim():version.StandardError));
                }
                var usb=await UsbInventory.ReadAsync(deadline.Token);
                found.Add(new("Windows USB","信息",$"当前存在 {usb.Count} 个具有序列号的 USB 身份。"));
                if(adbLocation is not null)
                {
                    var diagnosticPolicy=new DevicePolicy(snapshot);
                    var client=new AdbClient(adbLocation,diagnosticPolicy,UsbInventory.ReadAsync);
                    var devices=await client.ListDevicesAsync(deadline.Token);
                    if(devices.Count==0)found.Add(new("安卓调试设备","信息","未发现 ADB 设备。免调试网络模式无需 ADB；调试模式需在安卓开启 USB 调试并允许此电脑。"));
                    foreach(var device in devices)
                    {
                        var verdict=diagnosticPolicy.Evaluate(device,usb);
                        var detail=device.State switch{"unauthorized"=>"请在此设备解锁后允许电脑的 USB 调试授权。","offline"=>"设备 ADB 离线，请重插该设备数据线并重新授权。",_=>verdict.Allowed?"可在 USB 调试页选择并连接。":verdict.Reason};
                        found.Add(new("安卓 "+device.Serial,verdict.Allowed?"通过":"需要处理",detail));
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
                try{found.Add(new("H.264 编码器","通过",VideoPipeline.FindFfmpeg()));}catch(Exception ex){found.Add(new("H.264 编码器","需要处理",ex.Message+"；请使用完整交付目录。"));}
                var apk=Path.Combine(AppContext.BaseDirectory,"android","TabLink.apk");
                found.Add(new("安卓客户端",File.Exists(apk)?"通过":"需要处理",File.Exists(apk)?"客户端随包提供；在 USB 调试页可安装到明确选中的安卓设备。":"缺少 android/TabLink.apk，请恢复完整交付目录。"));
                var conflicts=Process.GetProcessesByName("ExtensoDeskServer");
                found.Add(new("USB 冲突",conflicts.Length==0?"通过":"需要处理",conflicts.Length==0?"未发现 ExtensoDeskServer 进程。":"ExtensoDeskServer 正在运行，可能接管 USB 设备。请先退出它的服务再连接。"));foreach(var p in conflicts)p.Dispose();
                return found;
            },deadline.Token);
            if(server is {} primary)results.Add(new("主连接","信息",$"客户端连接={primary.ClientConnected}，已显示={primary.PresentedFrames} 帧，采集暂停={primary.CapturePaused}。"));
            foreach(var s in additionalSessions.Where(x=>!x.IsStopped))results.Add(new("独立设备 "+s.Port,"信息",s.State));
            foreach(var s in browserStates.Values)results.Add(new("浏览器 "+s.Id.ToString()[..8],"信息",s.Message));
            Diagnostics.Save("connection-diagnosis.json",()=>new{timestamp=DateTimeOffset.Now,findings=results},Log);
        }
        catch(OperationCanceledException){results.Add(new("检测","未完成","检测已超时或取消。已有连接继续运行，可重试检测。"));}
        catch(Exception ex){results.Add(new("检测","需要处理",ex.Message));}
        finally
        {
            diagnosticReport.Text=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"\r\n\r\n"+string.Join("\r\n\r\n",results.Select(x=>$"[{x.State}] {x.Check}\r\n{x.Detail}"));
            diagnosing=false;diagnose.Enabled=!closing;
        }
    }

    static string? VerifyBundledAdb(string executable)
    {
        var root=Path.GetDirectoryName(executable)!;
        var expected=new Dictionary<string,string>{{"adb.exe","957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71"},{"AdbWinApi.dll","120BEF587119C6CB926B86B9BE90FDFBCE38937588EAE28CD91A94CE63C7B965"},{"AdbWinUsbApi.dll","6CA69A2CA0E31309C087D288F058977D421AD03500E4C3E1DBD981241A069C60"}};
        foreach(var file in expected)
        {
            var path=Path.Combine(root,file.Key);if(!File.Exists(path))return "缺少 "+file.Key+"；请恢复完整安装包。";
            using var stream=File.OpenRead(path);if(Convert.ToHexString(SHA256.HashData(stream))!=file.Value)return file.Key+" 校验不匹配；未执行此内置文件，请恢复完整安装包。";
        }
        return null;
    }
    async Task RepairAdbAsync()
    {
        if(!settingsValid)throw new IOException("配置文件尚未成功读取。请先恢复原配置，不能用 ADB 路径修复覆盖排除规则："+store.Path);
        var bundled=Path.Combine(AppContext.BaseDirectory,"tools","platform-tools","adb.exe");
        if(VerifyBundledAdb(bundled) is {} error)throw new IOException(error);
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
