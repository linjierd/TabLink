using System.ComponentModel;
using System.Diagnostics;
using TabLink.Core;

namespace TabLink.Windows;

// Kept behind a narrow interface so allocator tests never launch the elevated
// helper or touch a real display device.
internal interface ISingleDisplayDriverController
{
    Task EnsureSingleAsync(TabletDisplayProfile profile, CancellationToken cancellationToken);
    Task RemoveOwnedAsync(CancellationToken cancellationToken);
}

internal sealed class SingleDisplayDriverLifecycle : ISingleDisplayDriverController
{
    internal static SingleDisplayDriverLifecycle Shared { get; } = new();
    readonly object gate = new();
    CrossProcessMutexLease? activeLease;

    public async Task EnsureSingleAsync(TabletDisplayProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        CrossProcessMutexLease lease;
        lock (gate)
        {
            if (activeLease is not null) throw new InvalidOperationException("唯一虚拟显示设备已经处于连接生命周期中。");
            lease = new CrossProcessMutexLease(@"Global\TabLink.SingleDisplayDriverLifecycle.1");
            activeLease = lease;
        }
        try
        {
            await RunHelperAsync("--prepare-single-display-held",
                [profile.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 profile.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 profile.RequestedRefreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            // A helper can finish the PnP transaction and then fail while
            // writing its user-facing result JSON. Always run the strict,
            // receipted cleanup command after any launched prepare failure;
            // it is idempotent when prepare already rolled itself back. Keep
            // the cross-process lease until that cleanup has finished so a
            // second TabLink process cannot install between prepare and undo.
            Exception? cleanupError = null;
            if (HelperMayHaveStarted(original))
            {
                try { await RunHelperAsync("--remove-session-display-held", [], CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { cleanupError = cleanup; }
            }
            Exception? releaseError = null;
            try { ReleaseActiveLease(lease); }
            catch (Exception release) { releaseError = release; }
            if (cleanupError is not null || releaseError is not null)
                throw new AggregateException("单屏驱动准备失败，且严格回收或生命周期锁释放未完成。",
                    new[] { original, cleanupError, releaseError }.Where(x => x is not null).Cast<Exception>());
            throw;
        }
    }

    public Task RemoveOwnedAsync(CancellationToken cancellationToken) =>
        RemoveOwnedCoreAsync(cancellationToken);

    private async Task RemoveOwnedCoreAsync(CancellationToken cancellationToken)
    {
        CrossProcessMutexLease? lease;
        lock (gate) lease = activeLease;
        if (lease is null)
        {
            await RunHelperAsync("--remove-session-display", [], cancellationToken).ConfigureAwait(false);
            return;
        }

        Exception? removalError = null;
        try
        {
            // Transfer is deliberately avoided: the helper knows the caller
            // already owns the named lifecycle mutex, and removal completes
            // before this process releases it to a waiting connector.
            await RunHelperAsync("--remove-session-display-held", [], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { removalError = ex; }
        Exception? releaseError = null;
        try { ReleaseActiveLease(lease); }
        catch (Exception ex) { releaseError = ex; }
        if (removalError is not null && releaseError is not null)
            throw new AggregateException("卸载唯一虚拟显示设备且释放生命周期锁时均发生错误。", removalError, releaseError);
        if (removalError is not null) throw removalError;
        if (releaseError is not null) throw releaseError;
    }

    internal async Task RemoveAfterOwnerExitAsync()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { await RemoveOwnedAsync(CancellationToken.None).ConfigureAwait(false); return; }
            catch (Exception ex) { last = ex; }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        throw new IOException("异常退出后未能卸载受保护回执对应的虚拟显示设备。", last);
    }

    private static async Task RunHelperAsync(string command, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        // Cancellation is checked before mutation. Once the helper starts, it
        // must be observed to completion: abandoning an in-flight PnP install
        // could otherwise leave a late device after the receiver disconnected.
        cancellationToken.ThrowIfCancellationRequested();
        var helper = VirtualDisplayManager.InstallerPath;
        if (!File.Exists(helper))
            throw new FileNotFoundException("缺少虚拟副屏管理组件，请使用完整的 TabLink 安装目录。", helper);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(command);
        foreach (var value in values) start.ArgumentList.Add(value);
        start.ArgumentList.Add("--quiet");
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("无法启动虚拟副屏管理组件。"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 740 or 1223)
        {
            throw new IOException(ex.NativeErrorCode == 1223
                ? "Windows 管理员授权已取消，未安装虚拟副屏。"
                : "TabLink 需要以管理员身份运行，才能按需安装和卸载虚拟副屏。", ex);
        }
        using (process)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var resultName = command.StartsWith("--prepare-single-display", StringComparison.Ordinal)
                    ? "single-display-prepare-result.json" : "single-display-remove-result.json";
                throw new IOException("虚拟副屏操作未完成。详情：" + Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink", resultName));
            }
        }
    }

    private static bool HelperMayHaveStarted(Exception error) => error switch
    {
        FileNotFoundException => false,
        IOException { InnerException: Win32Exception { NativeErrorCode: 740 or 1223 } } => false,
        _ => true
    };

    private void ReleaseActiveLease(CrossProcessMutexLease expected)
    {
        lock (gate)
        {
            if (!ReferenceEquals(activeLease, expected)) return;
            activeLease = null;
        }
        expected.Dispose();
    }

    // System.Threading.Mutex is thread-affine. A dedicated owner thread keeps
    // the cross-process exclusion alive across awaits and for the whole active
    // display session. If the host crashes, Windows abandons the mutex and the
    // independent display watcher can acquire it before exact-device removal.
    private sealed class CrossProcessMutexLease : IDisposable
    {
        readonly ManualResetEventSlim acquired = new();
        readonly ManualResetEventSlim release = new();
        readonly Thread owner;
        Exception? error;
        int disposed;

        internal CrossProcessMutexLease(string name)
        {
            owner = new Thread(() => Own(name)) { IsBackground = true, Name = "TabLink display lifecycle lock" };
            owner.Start();
            if (!acquired.Wait(TimeSpan.FromSeconds(35)))
            {
                Dispose();
                throw new TimeoutException("等待唯一虚拟显示设备生命周期锁超时。");
            }
            if (error is not null)
            {
                Dispose();
                throw new IOException("无法取得唯一虚拟显示设备生命周期锁。", error);
            }
        }

        void Own(string name)
        {
            using var mutex = new Mutex(false, name);
            var locked = false;
            try
            {
                try { locked = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new TimeoutException("另一个 TabLink 进程仍持有虚拟显示设备。");
                acquired.Set();
                release.Wait();
            }
            catch (Exception ex) { error = ex; acquired.Set(); }
            finally { if (locked) mutex.ReleaseMutex(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            release.Set();
            if (owner.IsAlive && !owner.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("释放唯一虚拟显示设备生命周期锁超时。");
            acquired.Dispose();
            release.Dispose();
        }
    }
}
