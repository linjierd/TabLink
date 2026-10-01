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
    readonly Button manageTrustedDevices=new(){Text="可信设备"};
    readonly Button copyPairing=new(){Text="复制连接链接",Enabled=false,AutoSize=true,Padding=new Padding(10,5,10,5),Margin=new Padding(0,14,0,12)};
    readonly PictureBox pairingQr=new(){Size=new Size(220,220),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.White,Visible=false};
    readonly Label pairingHint=new(){AutoSize=true,MaximumSize=new Size(430,0),Text="选择线路后点击“开始配对”。首次扫码登记可信设备；以后可自动发现并重连。"};
    NetworkInterfaceChoice? networkChoice;
    NetworkFirewall? networkFirewall;
    NetworkFirewall? discoveryFirewall;
    NativeDiscoveryService? networkDiscovery;
    VirtualDisplayInfo? networkDisplay;
    Task? networkPreparation;
    Task? networkStartTask;
    bool preparingNetwork;
    string? pairingUri;
    DateTime lastNetworkCheckUtc;
    DateTime nextTrustedAutoStartUtc;
    bool trustedAutoStartSuppressed;
    readonly bool diagnosticPairing;
    static string DiagnosticPairingPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","diagnostic-network-pairing.txt");

    Control BuildNetworkPanel()
    {
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(20),AutoScroll=true};
        var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=6};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));page.Controls.Add(layout);
        var help=new Label{AutoSize=true,Dock=DockStyle.Top,Margin=new Padding(0,0,0,12),Text="电脑和平板接入同一局域网；使用数据线时，请在平板开启 USB 网络共享。\n打开 TabLink 客户端扫码即可连接，无需开发者模式。"};
        networks.Margin=new Padding(0,0,0,12);layout.Controls.Add(networks);
        var actions=Flow(refreshNetworks,startNetwork,stopNetwork,manageTrustedDevices);actions.AutoSize=true;actions.Margin=new Padding(0,0,0,12);layout.Controls.Add(actions);
        layout.Controls.Add(help);
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
        stopNetwork.Click+=async(_,_)=>await GuardAsync(StopNetworkByUserAsync);
        manageTrustedDevices.Click+=(_,_)=>ShowTrustedDevices();
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

    Task StartNetworkAsync()
    {
        trustedAutoStartSuppressed=false;
        if(server is {} listening)
        {
            if(capture is not null||!listening.CanRefreshRegistration)
                throw new InvalidOperationException("设备正在认证、重连或传输，暂时不能生成新的配对二维码。");
            PublishNetworkPairing(listening,refresh:true);
            SetStatus("新的配对二维码将在 5 分钟后失效；已登记设备仍可自动重连");
            return Task.CompletedTask;
        }
        return networkStartTask=StartNetworkCoreAsync(autoTrusted:false);
    }

    async Task StopNetworkByUserAsync()
    {
        trustedAutoStartSuppressed=true;
        await StopAsync();
    }

    async Task TryAutoStartTrustedNetworkAsync()
    {
        if(trustedAutoStartSuppressed||closing||stopping||busy||HasAnySessions||nativeTrust is not {Count:>0}||
            DateTime.UtcNow<nextTrustedAutoStartUtc)return;
        nextTrustedAutoStartUtc=DateTime.UtcNow.AddSeconds(10);
        try
        {
            await RefreshNetworksAsync();
            if(networks.SelectedItem is NetworkInterfaceChoice)
                networkStartTask=StartNetworkCoreAsync(autoTrusted:true);
            if(networkStartTask is not null)await networkStartTask;
        }
        catch(Exception ex){Log("可信设备自动监听尚未就绪："+SafeError(ex));}
    }

    async Task StartNetworkCoreAsync(bool autoTrusted)
    {
        if(server is not null)return;
        if(HasAdditionalSessions)throw new InvalidOperationException("TabLink 只允许一个副屏连接。请先停止当前原生或浏览器连接。");
        var selected=networks.SelectedItem as NetworkInterfaceChoice??throw new InvalidOperationException("请先连接 Wi-Fi，或在平板开启 USB 网络共享，然后刷新线路。");
        var trust=nativeTrust??throw new IOException("长期可信配对存储不可用："+(nativeTrustError??"请检查受保护的 ProgramData 存储。"));
        var fresh=await Task.Run(()=>NetworkInterfaceCatalog.GetChoices(settings),lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        var choice=fresh.SingleOrDefault(x=>SameInterface(x,selected))??throw new IOException("所选线路已变化或已被排除，请刷新后重选。");
        await EnsureOwnedDisplayCleanupBeforeNewConnectionAsync();
        _=VideoPipeline.FindFfmpeg();
        var encoderGeneration=BeginEncoderSelectionConnection();
        primaryEncoderGeneration=encoderGeneration;
        try
        {
            BeginConnectionHealth(ConnectionHealthPath.NativeNetwork,$"选择线路 {choice.InterfaceAlias} · {choice.LocalAddress}");
            SetStatus("正在为选中的线路准备加密配对…");
            networkChoice=choice;
            networkFirewall=await NetworkFirewall.OpenAsync(choice,lifetime.Token);
            discoveryFirewall=await NetworkFirewall.OpenAsync(choice,lifetime.Token,NativeDiscoveryService.Port,"UDP",
                acceptLocalBroadcast:true);
            networkDiscovery=new NativeDiscoveryService(choice,trust.HostId);
            lifetime.Token.ThrowIfCancellationRequested();
            FrameServer? created=null;
            var options=new NetworkSessionOptions(choice.LocalAddress,NetworkSessionOptions.DefaultPort,
                trust.CreateServerCertificate(),trust);
            try
            {
                created=new FrameServer(options,(profile,ct)=>PrepareNetworkOnUiAsync(created!,profile,ct),
                    ct=>NetworkVideo(encoderGeneration,ct),touch.Checked?message=>capture?.Input(message):null,()=>capture?.ReleaseInput());
            }
            catch{options.Dispose();throw;}
            server=created;
            created.Status+=SetStatus;
            created.DisplayProfileChanged+=p=>{if(!IsDisposed)BeginInvoke(async()=>await AdaptDisplayAsync(created,p));};
            created.Start();
            MarkHealthRouteReady($"{choice.InterfaceAlias} · {choice.LocalAddress}:27184 · TLS 监听已启动");
            MarkHealthAuthenticationStarted(trust.Count>0?"等待已信任设备签名认证，或扫描二维码登记新设备":"等待客户端扫描二维码并完成一次性注册");
            sessionStartedUtc=DateTime.UtcNow;lastNetworkCheckUtc=DateTime.MinValue;
            if(autoTrusted)
            {
                ClearPairing();
                pairingHint.Text="正在等待已登记设备完成签名重连。\n\n如需登记另一台设备，请点击“生成新配对二维码”；二维码仅在生成后的 5 分钟内有效。";
            }
            else PublishNetworkPairing(created,refresh:false);
            SetStatus(autoTrusted?"正在等待已信任平板自动重连，尚未启用副屏":"等待平板扫码或已信任设备自动重连，尚未启用副屏");
            metrics.Text=$"{choice.InterfaceAlias} · {choice.LocalAddress} · 持久主机身份 + TLS · 无需 USB 调试";
        }
        catch(Exception ex){MarkConnectionHealthAttention(SafeError(ex));await StopAsync();throw;}
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
            MarkHealthDisplayProfile(profile);
            if(capture is null||!sameMode)
            {
                MarkHealthDisplayPreparing("正在按设备报告的模式准备唯一虚拟副屏");
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
                MarkHealthDisplayReady(networkDisplay);
            }
            tabletProfile=profile;
            var quality=new AdaptiveVideoSession(profile,selectedQuality,networkChoice?.Kind switch
            {
                NetworkInterfaceKind.WiFi=>VideoTransportKind.WiFi,
                NetworkInterfaceKind.Usb=>VideoTransportKind.Usb,
                NetworkInterfaceKind.Ethernet=>VideoTransportKind.Ethernet,
                _=>VideoTransportKind.Unknown
            });
            source.AttachQualitySession(quality);
            videoQuality=quality;
            MarkHealthPipelineStarting("正在启动桌面捕获与 H.264 编码器");
            sessionStartedUtc=DateTime.UtcNow;presentationDeadline.Reset(sessionStartedUtc);
            previousPresented=0;previousSampleUtc=sessionStartedUtc;
            Diagnostics.Save("tablet-display-profile.json",()=>profile,Log);
            pairingHint.Text=$"已通过 {networkChoice?.InterfaceAlias} 认证。\n\n副屏：{profile.Width} × {profile.Height}\n请求刷新率：{profile.RequestedRefreshRate} Hz\n\n× 隐藏到托盘后继续传输。\n停止连接会收回副屏；已登记设备可在下次启动后自动重连。";
            SetStatus("平板已配对，等待首帧显示确认…");
        }
        catch(Exception ex)
        {
            if(ex is not OperationCanceledException){var summary=SafeError(ex);MarkConnectionHealthAttention(summary);Log("网络副屏准备失败："+summary);pairingHint.Text="副屏准备失败："+summary+"\n\n修正后请在平板重新连接。";}
            capture?.Dispose();capture=null;
            await ReleasePrimaryDisplayAsync();
            activePower?.Dispose();activePower=null;
            networkDisplay=null;tabletProfile=null;
            throw;
        }
        finally{preparingNetwork=false;}
    }

    IAsyncEnumerable<VideoPacket> NetworkVideo(EncoderSelectionGeneration encoderGeneration,CancellationToken ct)
    {
        var display=networkDisplay??throw new IOException("网络副屏尚未准备好。");
        var profile=tabletProfile??throw new IOException("尚未收到平板屏幕参数。");
        var lease=displayGuard?.Lease??throw new IOException("副屏保护组件未启动。");
        var quality=videoQuality??throw new IOException("本次连接的画质控制器尚未准备好。");
        return VideoPipeline.StreamAsync(display,profile,ct,Log,lease,quality:quality,
            encoderOptions:CurrentEncoderOptions(),encoderSelected:snapshot=>ReportEncoderSelection(encoderGeneration,snapshot));
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
        pairingHint.Text="选择线路后点击“开始配对”。首次扫码登记可信设备；以后可自动发现并重连。";
    }

    void PublishNetworkPairing(FrameServer source,bool refresh)
    {
        pairingUri=refresh?source.RefreshRegistrationUri():source.NetworkConnectionUri;
        if(pairingUri is null)throw new IOException("无法创建短期配对链接。");
        // Explicit integration-test mode only. This short-lived credential
        // stays in the user's profile, outside distributable diagnostics.
        if(diagnosticPairing)File.WriteAllText(DiagnosticPairingPath,pairingUri);
        using var generator=new QRCodeGenerator();
        using var data=generator.CreateQrCode(pairingUri,QRCodeGenerator.ECCLevel.M);
        using var code=new PngByteQRCode(data);
        using var stream=new MemoryStream(code.GetGraphic(8));
        using var decoded=Image.FromStream(stream);
        pairingQr.Image?.Dispose();pairingQr.Image=new Bitmap(decoded);pairingQr.Visible=true;
        pairingHint.Text=$"在平板 TabLink 中点击“扫码连接”。\n\n线路：{networkChoice?.InterfaceAlias}\n地址：{networkChoice?.LocalAddress}:27184\n\n首次扫码会把这台平板登记为可信设备；以后可自动发现并完成签名重连。二维码只能使用一次，并会在 5 分钟后失效。\n\n无法扫码时，可复制连接链接到平板粘贴。";
    }

    void ExpireNetworkPairingUi()
    {
        ClearPairing();
        pairingHint.Text="配对二维码已使用或已超过 5 分钟。已登记设备仍可自动重连；如需添加设备，请点击“生成新配对二维码”。";
        UpdateButtons();
    }
    void UpdateNetworkButtons(bool idle)
    {
        refreshNetworks.Enabled=networks.Enabled=!busy&&!stopping&&!closing&&settingsValid;
        var canRefreshRegistration=!busy&&!stopping&&!closing&&capture is null&&server is {} listening&&
            listening.CanRefreshRegistration&&networkChoice is not null;
        startNetwork.Enabled=idle&&networks.SelectedItem is NetworkInterfaceChoice||canRefreshRegistration;
        startNetwork.Text=canRefreshRegistration?(pairingUri is null?"生成新配对二维码":"更换配对二维码"):"开始配对";
        stopNetwork.Enabled=!busy&&!stopping&&server is not null;
        copyPairing.Enabled=pairingUri is not null&&!stopping;
        manageTrustedDevices.Enabled=!busy&&!stopping&&!closing&&nativeTrust is not null;
        manageTrustedDevices.Text=$"可信设备（{nativeTrust?.Count??0}）";
    }

    void ShowTrustedDevices()
    {
        var trust=nativeTrust;
        if(trust is null){ShowError(new IOException("长期可信配对存储不可用："+(nativeTrustError??"请检查受保护存储。")));return;}
        using var dialog=new Form
        {
            Text="TabLink · 可信设备",StartPosition=FormStartPosition.CenterParent,Size=new Size(680,430),
            MinimumSize=new Size(560,340),Font=Font,BackColor=Color.White,ShowInTaskbar=false
        };
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=1,RowCount=3};
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(620,0),ForeColor=muted,
            Text="首次扫码后保存设备公钥。自动发现只提供地址提示；每次连接仍要通过持久电脑证书和新的签名挑战。移除后，该设备必须重新扫码。"},0,0);
        var list=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,GridLines=true,HideSelection=false};
        list.Columns.Add("设备名称",220);list.Columns.Add("最近使用",180);list.Columns.Add("设备身份",210);
        root.Controls.Add(list,0,1);
        var remove=new Button{Text="移除选中设备",AutoSize=true};
        var close=new Button{Text="关闭",AutoSize=true,DialogResult=DialogResult.OK};
        root.Controls.Add(Flow(remove,close),0,2);dialog.Controls.Add(root);dialog.AcceptButton=close;
        void Reload()
        {
            list.Items.Clear();
            foreach(var item in trust.Snapshot())
            {
                var row=new ListViewItem(item.DisplayName){Tag=item};
                row.SubItems.Add(item.LastUsedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
                row.SubItems.Add(item.DeviceId[..16]+"…");list.Items.Add(row);
            }
            remove.Enabled=list.SelectedItems.Count==1;
        }
        list.SelectedIndexChanged+=(_,_)=>remove.Enabled=list.SelectedItems.Count==1;
        remove.Click+=async(_,_)=>
        {
            if(list.SelectedItems.Count!=1||list.SelectedItems[0].Tag is not TrustedDeviceInfo selected)return;
            remove.Enabled=false;
            var revoked=false;
            try
            {
                revoked=trust.Revoke(selected.DeviceId);
                if(!revoked)return;
                if(string.Equals(server?.AuthenticatedDeviceId,selected.DeviceId,StringComparison.Ordinal))
                    await StopAsync();
                Log("已撤销一台可信设备；未记录或显示完整设备身份。");
            }
            catch(Exception ex)
            {
                var message=revoked
                    ? "可信设备已经撤销，但停止其当前连接时出错。该设备不能再次认证；请检查连接状态后重试停止。"
                    : "撤销可信设备失败，原有信任记录保持不变。";
                ShowError(new IOException(message,ex));
            }
            finally
            {
                if(!dialog.IsDisposed)Reload();
                if(!IsDisposed)UpdateButtons();
            }
        };
        Reload();dialog.ShowDialog(this);UpdateButtons();
    }
}
