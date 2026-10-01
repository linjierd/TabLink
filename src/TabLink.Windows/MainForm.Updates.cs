namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly ToolStripMenuItem trayUpdate = new("正在检查正式版更新…") { Visible = false };
    WindowsUpdateCoordinator? updateCoordinator;
    string? announcedUpdateRelease;
    CancellationTokenSource? idleUpdateDelay;
    bool updateExitStarted;

    void ConfigureAutomaticUpdates()
    {
        trayMenu.Items.Insert(2, trayUpdate);
        trayUpdate.Click += async (_, _) =>
        {
            if (closing) return;
            RestoreFromTray();
            SetStatus("正在结束连接，随后安装正式版更新…");
            await ExitAsync(requireReadyUpdater: true);
        };
        try
        {
            updateCoordinator = WindowsUpdateCoordinator.CreateDefault(Log);
            updateCoordinator.ReadyChanged += OnUpdateReadyChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        { Log("自动更新配置未启用：" + SafeError(ex)); }
    }

    void BeginAutomaticUpdateChecks() => updateCoordinator?.Start(lifetime.Token);

    void OnUpdateReadyChanged(PendingWindowsUpdate? update)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnUpdateReadyChanged(update)); } catch (InvalidOperationException) { } return; }
        trayUpdate.Visible = update is not null;
        trayUpdate.Text = update is null ? "没有待安装更新" : "重启并安装正式版 " + update.Version;
        if (update is not null && announcedUpdateRelease != update.ReleaseId)
        {
            announcedUpdateRelease = update.ReleaseId;
            var message = HasAnySessions
                ? "版本 " + update.Version + " 已安全下载。当前副屏保持连接；最后一台设备停止后自动安装。"
                : "版本 " + update.Version + " 已安全下载。电脑空闲时将自动重启并安装。";
            tray.ShowBalloonTip(5000, "TabLink 正式版更新已准备完成", message, ToolTipIcon.Info);
        }
        EvaluateAutomaticUpdateApplication();
    }

    void EvaluateAutomaticUpdateApplication()
    {
        var disposition = AutomaticUpdateApplyPolicy.Evaluate(updateCoordinator?.Ready is not null, HasAnySessions, busy, stopping, closing || updateExitStarted);
        if (disposition != AutomaticUpdateApplyDisposition.ScheduleWhenIdle)
        {
            var pending = idleUpdateDelay; idleUpdateDelay = null;
            if (pending is not null) { pending.Cancel(); pending.Dispose(); }
            return;
        }
        if (idleUpdateDelay is not null) return;
        var delay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        idleUpdateDelay = delay;
        _ = ApplyReadyUpdateAfterIdleDelayAsync(delay);
    }

    async Task ApplyReadyUpdateAfterIdleDelayAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), delay.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (!ReferenceEquals(idleUpdateDelay, delay)) delay.Dispose(); }
        if (!ReferenceEquals(idleUpdateDelay, delay)) return;
        idleUpdateDelay = null; delay.Dispose();
        if (AutomaticUpdateApplyPolicy.Evaluate(updateCoordinator?.Ready is not null, HasAnySessions, busy, stopping, closing || updateExitStarted) != AutomaticUpdateApplyDisposition.ScheduleWhenIdle) return;
        updateExitStarted = true;
        SetStatus("正在安装已验证的正式版更新…");
        Log("电脑当前没有副屏会话，开始自动安装已下载的正式版更新。");
        await ExitAsync(requireReadyUpdater: true);
    }
}
