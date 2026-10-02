namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly ToolStripMenuItem trayUpdate = new("正在检查正式版更新…") { Visible = false };
    WindowsUpdateCoordinator? updateCoordinator;
    CancellationTokenSource? updateChecksLifetime;
    string? updateConfigurationError;
    string? announcedUpdateRelease;
    CancellationTokenSource? idleUpdateDelay;
    bool updateExitStarted;

    void ConfigureAutomaticUpdates()
    {
        trayMenu.Items.Insert(2, trayUpdate);
        trayUpdate.Click += async (_, _) =>
        {
            await InstallReadyUpdateAsync();
        };
        RefreshUpdatePreferenceUi();
    }

    void BeginAutomaticUpdateChecks()
    {
        if(verificationMode||CurrentUpdateMode==TabLink.Core.UpdateMode.Never||closing||lifetime.IsCancellationRequested||
            updateCoordinator is not null)return;
        try
        {
            var coordinator=WindowsUpdateCoordinator.CreateDefault(Log);
            var checks=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            coordinator.ReadyChanged+=OnUpdateReadyChanged;
            updateCoordinator=coordinator;
            updateChecksLifetime=checks;
            updateConfigurationError=null;
            UpdateAutomaticPackageDownloadPolicy();
            coordinator.Start(checks.Token);
            RefreshUpdatePreferenceUi();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or
            System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
        {
            updateConfigurationError="正式版更新配置当前不可用："+SafeError(ex);
            Log("自动更新配置未启用："+SafeError(ex));
            RefreshUpdatePreferenceUi();
        }
    }

    async Task StopAutomaticUpdateChecksAsync()
    {
        CancelIdleUpdateDelay();
        var checks=updateChecksLifetime;updateChecksLifetime=null;
        var coordinator=updateCoordinator;updateCoordinator=null;
        if(coordinator is not null)
            coordinator.ReadyChanged-=OnUpdateReadyChanged;
        try{if(checks is not null)await Task.Run(checks.Cancel);}
        catch(Exception ex){Log("取消自动更新后台任务时出现错误："+SafeError(ex));}
        try{if(coordinator is not null)await coordinator.WaitForWorkerAsync();}
        catch(Exception ex){Log("自动更新后台任务结束时出现错误："+SafeError(ex));}
        finally
        {
            coordinator?.Dispose();
            checks?.Dispose();
        }
        updateConfigurationError=null;
        announcedUpdateRelease=null;
        trayUpdate.Visible=false;
        RefreshUpdatePreferenceUi();
    }

    async Task ApplyUpdateModeAsync(TabLink.Core.UpdateMode mode)
    {
        if(mode==TabLink.Core.UpdateMode.Never)
        {
            await StopAutomaticUpdateChecksAsync();
            return;
        }
        if(mode!=TabLink.Core.UpdateMode.Automatic)CancelIdleUpdateDelay();
        BeginAutomaticUpdateChecks();
        EvaluateAutomaticUpdateApplication();
        trayUpdate.Visible=updateCoordinator?.Ready is not null;
        RefreshUpdatePreferenceUi();
    }

    async Task InstallReadyUpdateAsync()
    {
        if(CurrentUpdateMode==TabLink.Core.UpdateMode.Never||updateCoordinator?.Ready is null||
            closing||exitStarting||updateExitStarted)return;
        if(!CanInstallReadyUpdateFromCurrentLocation)
        {
            RestoreFromTray();
            SetStatus("更新已安全下载；请从正式安装目录启动后安装："+ExpectedProtectedInstallDirectory);
            RefreshUpdatePreferenceUi();
            return;
        }
        RestoreFromTray();
        SetStatus("正在结束连接，随后安装正式版更新…");
        await ExitAsync(requireReadyUpdater:true);
        if(!closing)RefreshUpdatePreferenceUi();
    }

    void OnUpdateReadyChanged(PendingWindowsUpdate? update)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnUpdateReadyChanged(update)); } catch (InvalidOperationException) { } return; }
        if(CurrentUpdateMode==TabLink.Core.UpdateMode.Never)
        {
            trayUpdate.Visible=false;
            RefreshUpdatePreferenceUi();
            return;
        }
        trayUpdate.Visible = update is not null;
        trayUpdate.Text = update is null ? "没有待安装更新" : "重启并安装正式版 " + update.Version;
        if (update is not null && announcedUpdateRelease != update.ReleaseId)
        {
            announcedUpdateRelease = update.ReleaseId;
            var message=!CanInstallReadyUpdateFromCurrentLocation
                ? "版本 "+update.Version+" 已安全下载。请从正式安装目录启动后安装。"
                : CurrentUpdateMode==TabLink.Core.UpdateMode.DownloadThenAsk
                ? "版本 "+update.Version+" 已安全下载。请从设置页或托盘中选择重启安装。"
                : HasAnySessions||HasPendingNetworkStart||connectionStarts.IsStarting
                    ? "版本 " + update.Version + " 已安全下载。当前副屏保持连接；最后一台设备停止后自动安装。"
                    : "版本 " + update.Version + " 已安全下载。电脑空闲时将自动重启并安装。";
            tray.ShowBalloonTip(5000, "TabLink 正式版更新已准备完成", message, ToolTipIcon.Info);
        }
        RefreshUpdatePreferenceUi();
        EvaluateAutomaticUpdateApplication();
    }

    void EvaluateAutomaticUpdateApplication()
    {
        var hasConnectionWork=HasAnySessions||HasPendingNetworkStart||connectionStarts.IsStarting;
        var disposition = AutomaticUpdateApplyPolicy.Evaluate(CurrentUpdateMode==TabLink.Core.UpdateMode.Automatic&&
            CanInstallReadyUpdateFromCurrentLocation,
            updateCoordinator?.Ready is not null,hasConnectionWork,busy,stopping,closing||exitStarting||updateExitStarted);
        if (disposition != AutomaticUpdateApplyDisposition.ScheduleWhenIdle)
        {
            CancelIdleUpdateDelay();
            return;
        }
        if (idleUpdateDelay is not null) return;
        var delay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        idleUpdateDelay = delay;
        _ = ApplyReadyUpdateAfterIdleDelayAsync(delay);
    }

    void UpdateAutomaticPackageDownloadPolicy()
    {
        var coordinator=updateCoordinator;
        if(coordinator is null)return;
        coordinator.SetPackageDownloadsAllowed(AutomaticUpdateApplyPolicy.ShouldAllowPackageDownload(
            HasAnySessions||HasPendingNetworkStart,busy,stopping,connectionStarts.IsStarting));
    }

    async Task ApplyReadyUpdateAfterIdleDelayAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), delay.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (!ReferenceEquals(idleUpdateDelay, delay)) delay.Dispose(); }
        if (!ReferenceEquals(idleUpdateDelay, delay)) return;
        idleUpdateDelay = null; delay.Dispose();
        var hasConnectionWork=HasAnySessions||HasPendingNetworkStart||connectionStarts.IsStarting;
        if(AutomaticUpdateApplyPolicy.Evaluate(CurrentUpdateMode==TabLink.Core.UpdateMode.Automatic&&
            CanInstallReadyUpdateFromCurrentLocation,
            updateCoordinator?.Ready is not null,hasConnectionWork,busy,stopping,closing||exitStarting||updateExitStarted)!=
            AutomaticUpdateApplyDisposition.ScheduleWhenIdle)return;
        updateExitStarted = true;
        SetStatus("正在安装已验证的正式版更新…");
        Log("电脑当前没有副屏会话，开始自动安装已下载的正式版更新。");
        await ExitAsync(requireReadyUpdater: true);
    }

    void CancelIdleUpdateDelay()
    {
        var pending=idleUpdateDelay;idleUpdateDelay=null;
        if(pending is null)return;
        try{pending.Cancel();}finally{pending.Dispose();}
    }

    void DisposeAutomaticUpdates()
    {
        CancelIdleUpdateDelay();
        try{updateChecksLifetime?.Cancel();}catch(ObjectDisposedException){}
        updateChecksLifetime?.Dispose();updateChecksLifetime=null;
        if(updateCoordinator is not null)
        {
            updateCoordinator.ReadyChanged-=OnUpdateReadyChanged;
            updateCoordinator.Dispose();updateCoordinator=null;
        }
    }
}
