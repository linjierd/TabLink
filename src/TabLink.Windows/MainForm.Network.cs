using System.Diagnostics;
using QRCoder;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly ComboBox networks=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Button refreshNetworks=new(){Text="刷新线路"};
    readonly Button startNetwork=new(){Text="开始配对",BackColor=Color.FromArgb(31,105,210),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
    readonly Button stopNetwork=new(){Text="停止连接",Enabled=false};
    readonly Button copyPairing=new(){Text="复制连接链接",Enabled=false,AutoSize=true,Padding=new Padding(10,5,10,5),Margin=new Padding(0,14,0,12)};
    readonly PictureBox pairingQr=new(){Size=new Size(220,220),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.White,Visible=false};
    readonly Label pairingHint=new(){AutoSize=true,MaximumSize=new Size(430,0),Text="选择线路后点击“开始配对”。二维码仅对本次连接有效。"};
    NetworkInterfaceChoice? networkChoice;
    NetworkFirewall? networkFirewall;
    VirtualDisplayInfo? networkDisplay;
    Task? networkPreparation;
    Task? networkStartTask;
    bool preparingNetwork;
    string? pairingUri;
    DateTime lastNetworkCheckUtc;
    readonly bool diagnosticPairing;
    static string DiagnosticPairingPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","diagnostic-network-pairing.txt");

    Control BuildNetworkPanel()
    {
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(20),AutoScroll=true};
        var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=6};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));page.Controls.Add(layout);
        var help=new Label{AutoSize=true,Dock=DockStyle.Top,Margin=new Padding(0,0,0,12),Text="电脑和平板接入同一局域网；使用数据线时，请在平板开启 USB 网络共享。\n打开 TabLink 客户端扫码即可连接，无需开发者模式。"};
        layout.Controls.Add(help);
        networks.Margin=new Padding(0,0,0,12);layout.Controls.Add(networks);
        var actions=Flow(refreshNetworks,startNetwork,stopNetwork);actions.AutoSize=true;actions.Margin=new Padding(0,0,0,12);layout.Controls.Add(actions);
        var pairing=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=Padding.Empty};
        pairing.Controls.Add(pairingQr);
        var instructions=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(20,12,0,0)};
        instructions.Controls.Add(pairingHint);instructions.Controls.Add(copyPairing);pairing.Controls.Add(instructions);layout.Controls.Add(pairing);
        var openApk=new Button{Text="打开 APK 所在文件夹"};
        var tools=Flow(openApk);tools.AutoSize=true;tools.Margin=new Padding(0,14,0,10);layout.Controls.Add(tools);
        var notes=new Label{AutoSize=true,Dock=DockStyle.Top,ForeColor=muted,Text="USB 网络共享可能同时改变电脑的上网线路；TabLink 不修改默认路由或 DNS。\n平板完成认证并上报屏幕参数后，电脑才按需安装唯一虚拟屏；停止连接会卸载该设备并清理监听与防火墙规则。\n实际帧率受无线信号和设备性能影响；APK 继续按屏幕原生尺寸与支持的刷新率请求显示。"};layout.Controls.Add(notes);
        help.MaximumSize=notes.MaximumSize=new Size(870,0);
        page.SizeChanged+=(_,_)=>{var width=Math.Max(300,page.ClientSize.Width-page.Padding.Horizontal-30);help.MaximumSize=notes.MaximumSize=new Size(width,0);};
        refreshNetworks.Click+=async(_,_)=>await GuardAsync(RefreshNetworksAsync);
        startNetwork.Click+=async(_,_)=>await GuardAsync(StartNetworkAsync);
        stopNetwork.Click+=async(_,_)=>await GuardAsync(StopAsync);
        copyPairing.Click+=(_,_)=>{if(pairingUri is not null)try{Clipboard.SetText(pairingUri);}catch(Exception ex){ShowError(ex);}};
        openApk.Click+=(_,_)=>
        {
            var apk=Path.Combine(AppContext.BaseDirectory,"android","TabLink.apk");
            if(!File.Exists(apk)){ShowError(new IOException("请使用完整交付包；缺少 android/TabLink.apk。"));return;}
            var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add("/select,"+apk);Process.Start(start);
        };
        return page;
    }

    async Task RefreshNetworksAsync()
    {
        var previous=networks.SelectedItem as NetworkInterfaceChoice;
        var browserPrevious=browserNetworks.SelectedItem as NetworkInterfaceChoice;
        var available=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        networks.Items.Clear();browserNetworks.Items.Clear();
        foreach(var item in available){networks.Items.Add(item);browserNetworks.Items.Add(item);}
        networks.SelectedItem=available.FirstOrDefault(x=>previous is not null&&SameInterface(x,previous));
        browserNetworks.SelectedItem=available.FirstOrDefault(x=>browserPrevious is not null&&SameInterface(x,browserPrevious));
        if(networks.SelectedIndex<0&&networks.Items.Count>0)networks.SelectedIndex=0;
        if(browserNetworks.SelectedIndex<0&&browserNetworks.Items.Count>0)browserNetworks.SelectedIndex=0;
        if(networks.Items.Count==0)
        {
            const string unavailable="尚无可用线路：请连接 Wi-Fi 或开启平板 USB 网络共享";
            networks.Items.Add(unavailable);networks.SelectedIndex=0;browserNetworks.Items.Add(unavailable);browserNetworks.SelectedIndex=0;
        }
        if(!HasAnySessions&&connectionMode.SelectedIndex==0){status.Text="选择 Wi-Fi 或 USB 网络共享线路，开始配对";metrics.Text="本地加密连接 · 无需 USB 调试";}
        Log($"网络线路刷新完成：{available.Count} 条；被排除的 USB 设备与虚拟网卡不会参与配对。");
        UpdateButtons();
    }

    Task StartNetworkAsync()=>networkStartTask=StartNetworkCoreAsync();

    async Task StartNetworkCoreAsync()
    {
        if(server is not null)return;
        if(HasAdditionalSessions)throw new InvalidOperationException("TabLink 只允许一个副屏连接。请先停止当前原生或浏览器连接。");
        var selected=networks.SelectedItem as NetworkInterfaceChoice??throw new InvalidOperationException("请先连接 Wi-Fi，或在平板开启 USB 网络共享，然后刷新线路。");
        var fresh=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        var choice=fresh.SingleOrDefault(x=>SameInterface(x,selected))??throw new IOException("所选线路已变化或已被排除，请刷新后重选。");
        _=VideoPipeline.FindFfmpeg();
        try
        {
            SetStatus("正在为选中的线路准备加密配对…");
            networkChoice=choice;
            networkFirewall=await NetworkFirewall.OpenAsync(choice,lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            FrameServer? created=null;
            var options=new NetworkSessionOptions(choice.LocalAddress);
            try
            {
                created=new FrameServer(options,(profile,ct)=>PrepareNetworkOnUiAsync(created!,profile,ct),
                    NetworkVideo,touch.Checked?message=>capture?.Input(message):null,()=>capture?.ReleaseInput());
            }
            catch{options.Dispose();throw;}
            server=created;
            created.Status+=SetStatus;
            created.DisplayProfileChanged+=p=>{if(!IsDisposed)BeginInvoke(async()=>await AdaptDisplayAsync(created,p));};
            created.Start();
            sessionStartedUtc=DateTime.UtcNow;lastNetworkCheckUtc=DateTime.MinValue;
            pairingUri=created.NetworkConnectionUri??throw new IOException("无法创建配对链接。");
            // Explicit integration-test mode only. This short-lived credential
            // stays in the user's profile, outside distributable diagnostics.
            if(diagnosticPairing)File.WriteAllText(DiagnosticPairingPath,pairingUri);
            using var generator=new QRCodeGenerator();
            using var data=generator.CreateQrCode(pairingUri,QRCodeGenerator.ECCLevel.M);
            using var code=new PngByteQRCode(data);
            using var stream=new MemoryStream(code.GetGraphic(8));
            using var decoded=Image.FromStream(stream);
            pairingQr.Image?.Dispose();pairingQr.Image=new Bitmap(decoded);pairingQr.Visible=true;
            pairingHint.Text=$"在平板 TabLink 中点击“扫码连接”。\n\n线路：{choice.InterfaceAlias}\n地址：{choice.LocalAddress}:27184\n\n二维码包含本次授权密钥，请只让自己的平板扫描。未连接时 5 分钟失效。\n\n无法扫码时，可复制连接链接到平板粘贴。";
            SetStatus("等待平板扫码配对，尚未启用副屏");
            metrics.Text=$"{choice.InterfaceAlias} · {choice.LocalAddress} · TLS 加密 · 无需 USB 调试";
        }
        catch{await StopAsync();throw;}
    }

    Task PrepareNetworkOnUiAsync(FrameServer source,TabletDisplayProfile profile,CancellationToken ct)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(IsDisposed||Disposing){completion.SetCanceled();return completion.Task;}
        try
        {
            BeginInvoke(async()=>
            {
                if(ct.IsCancellationRequested||stopping||closing||!ReferenceEquals(source,server)){completion.TrySetCanceled();return;}
                var task=PrepareNetworkSerializedAsync(networkPreparation,source,profile,ct);networkPreparation=task;
                try{await task;completion.TrySetResult();}
                catch(OperationCanceledException){completion.TrySetCanceled();}
                catch(Exception ex){completion.TrySetException(ex);}
            });
        }
        catch(InvalidOperationException){completion.TrySetCanceled();}
        return completion.Task;
    }

    async Task PrepareNetworkSerializedAsync(Task? previous,FrameServer source,TabletDisplayProfile profile,CancellationToken ct)
    {
        if(previous is not null)try{await previous;}catch(Exception){ /* Previous session owns its rollback. */ }
        ct.ThrowIfCancellationRequested();
        if(stopping||closing||!ReferenceEquals(source,server))throw new OperationCanceledException();
        await PrepareNetworkDisplayAsync(source,profile,ct);
    }

    async Task PrepareNetworkDisplayAsync(FrameServer source,TabletDisplayProfile profile,CancellationToken ct)
    {
        preparingNetwork=true;
        try
        {
            ct.ThrowIfCancellationRequested();
            if(!ReferenceEquals(source,server))throw new OperationCanceledException();
            var sameMode=tabletProfile is {} old&&old.Width==profile.Width&&old.Height==profile.Height&&old.RequestedRefreshRate==profile.RequestedRefreshRate;
            if(capture is null||!sameMode)
            {
                capture?.Dispose();capture=null;
                await ReleasePrimaryDisplayAsync();
                activePower?.Dispose();activePower=null;
                tabletProfile=profile;
                Log($"加密连接已认证，平板报告：{profile.Width} × {profile.Height}，当前 {profile.RefreshRate:F1} Hz，目标 {profile.RequestedRefreshRate} Hz。");
                networkDisplay=await PrepareDisplayAsync(profile,ct);
                ct.ThrowIfCancellationRequested();
                if(!ReferenceEquals(source,server))throw new OperationCanceledException();
                activePower=new ActiveDisplayPower();
                capture=new DesktopCapture(networkDisplay,identity:displayGuard!.Lease);
            }
            tabletProfile=profile;
            sessionStartedUtc=DateTime.UtcNow;presentationDeadline.Reset(sessionStartedUtc);
            previousPresented=0;previousSampleUtc=sessionStartedUtc;
            Diagnostics.Save("tablet-display-profile.json",()=>profile,Log);
            pairingHint.Text=$"已通过 {networkChoice?.InterfaceAlias} 配对。\n\n副屏：{profile.Width} × {profile.Height}\n请求刷新率：{profile.RequestedRefreshRate} Hz\n\n× 隐藏到托盘后继续传输。\n停止连接将使当前二维码失效。";
            SetStatus("平板已配对，等待首帧显示确认…");
        }
        catch(Exception ex)
        {
            if(ex is not OperationCanceledException){Log("网络副屏准备失败："+ex.Message);pairingHint.Text="副屏准备失败："+ex.Message+"\n\n修正后请在平板重新连接。";}
            capture?.Dispose();capture=null;
            await ReleasePrimaryDisplayAsync();
            activePower?.Dispose();activePower=null;
            networkDisplay=null;tabletProfile=null;
            throw;
        }
        finally{preparingNetwork=false;}
    }

    IAsyncEnumerable<VideoPacket> NetworkVideo(CancellationToken ct)
    {
        var display=networkDisplay??throw new IOException("网络副屏尚未准备好。");
        var profile=tabletProfile??throw new IOException("尚未收到平板屏幕参数。");
        var lease=displayGuard?.Lease??throw new IOException("副屏保护组件未启动。");
        return VideoPipeline.StreamAsync(display,profile,ct,Log,lease);
    }

    async Task<bool> IsNetworkAvailableAsync()
    {
        var selected=networkChoice;if(selected is null)return false;
        if(DateTime.UtcNow-lastNetworkCheckUtc<TimeSpan.FromSeconds(6))return true;
        var available=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        if(ReferenceEquals(selected,networkChoice))lastNetworkCheckUtc=DateTime.UtcNow;
        return available.Any(x=>SameInterface(x,selected));
    }

    static bool SameInterface(NetworkInterfaceChoice a,NetworkInterfaceChoice b)=>a.InterfaceId==b.InterfaceId&&a.LocalAddress.Equals(b.LocalAddress)&&a.UsbSerial==b.UsbSerial&&a.PrefixLength==b.PrefixLength;

    void ClearPairing()
    {
        if(diagnosticPairing)try{File.Delete(DiagnosticPairingPath);}catch(IOException){}catch(UnauthorizedAccessException){}
        pairingUri=null;var old=pairingQr.Image;pairingQr.Image=null;pairingQr.Visible=false;old?.Dispose();
        pairingHint.Text="选择线路后点击“开始配对”。二维码仅对本次连接有效。";
    }
    void UpdateNetworkButtons(bool idle)
    {
        refreshNetworks.Enabled=networks.Enabled=!busy&&!stopping&&!closing&&settingsValid;
        startNetwork.Enabled=idle&&networks.SelectedItem is NetworkInterfaceChoice;
        stopNetwork.Enabled=!busy&&!stopping&&server is not null;
        copyPairing.Enabled=pairingUri is not null&&!stopping;
    }
}
