using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

namespace TabLink.Windows;

internal sealed class WindowsUpdateCoordinator : IDisposable
{
    static readonly TimeSpan ManifestSourceTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan UpdateOperationTimeout = TimeSpan.FromMinutes(3);
    readonly UpdateChannelConfiguration configuration;
    readonly UpdateClient client;
    readonly HttpClient http;
    readonly string applicationDirectory;
    readonly string cacheDirectory;
    readonly Action<string> log;
    readonly object sync = new();
    readonly object downloadSync = new();
    readonly SemaphoreSlim checkWake = new(0, 1);
    Task? worker;
    PendingWindowsUpdate? ready;
    bool updaterStarted;
    bool policyBlocked;
    bool packageDownloadsAllowed = true;
    CancellationTokenSource? activePackagePolicy;

    internal WindowsUpdateCoordinator(UpdateChannelConfiguration configuration, UpdateClient client, HttpClient http, string applicationDirectory, string cacheDirectory, Action<string> log)
    {
        this.configuration = configuration;
        this.client = client;
        this.http = http;
        this.applicationDirectory = applicationDirectory;
        this.cacheDirectory = cacheDirectory;
        this.log = log;
    }

    public event Action<PendingWindowsUpdate?>? ReadyChanged;
    public PendingWindowsUpdate? Ready { get { lock (sync) return ready; } }

    public static WindowsUpdateCoordinator CreateDefault(Action<string> log)
    {
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink", "updates");
        var configuration = UpdateChannelConfiguration.Load(applicationDirectory, localRoot);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var publicKey = Convert.FromBase64String(UpdateTrust.ManifestSignerSpkiBase64);
        var currentText = typeof(WindowsUpdateCoordinator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? throw new InvalidDataException("无法读取当前 TabLink 版本。");
        var client = new UpdateClient(http, publicKey, localRoot, StableSemanticVersion.Parse(currentText), configuration.CohortId,
            ManifestSourceTimeout);
        return new(configuration, client, http, applicationDirectory, localRoot, log);
    }

    public void Start(CancellationToken lifetime)
    {
        lock (sync) worker ??= RunAsync(lifetime);
    }

    internal async Task WaitForWorkerAsync()
    {
        Task? running;
        lock (sync) running = worker;
        if (running is not null) await running.ConfigureAwait(false);
    }

    public void SetPackageDownloadsAllowed(bool allowed)
    {
        CancellationTokenSource? cancel = null;
        var wake = false;
        lock (downloadSync)
        {
            if (packageDownloadsAllowed == allowed) return;
            packageDownloadsAllowed = allowed;
            if (allowed) wake = true;
            else cancel = activePackagePolicy;
        }
        if (cancel is not null)
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (wake)
        {
            try { checkWake.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    CancellationTokenSource BeginPackageAttempt()
    {
        lock (downloadSync)
        {
            var policy = new CancellationTokenSource();
            if (!packageDownloadsAllowed) policy.Cancel();
            activePackagePolicy = policy;
            return policy;
        }
    }

    void EndPackageAttempt(CancellationTokenSource policy)
    {
        lock (downloadSync)
            if (ReferenceEquals(activePackagePolicy, policy)) activePackagePolicy = null;
        policy.Dispose();
    }

    async Task RunAsync(CancellationToken lifetime)
    {
        if (!configuration.Enabled || configuration.ManifestUris.Count == 0) return;
        while (!lifetime.IsCancellationRequested)
        {
            CancellationTokenSource? packagePolicy = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(UpdateOperationTimeout);
                packagePolicy = BeginPackageAttempt();
                using var packageLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, packagePolicy.Token);
                var checkedUpdate = await client.CheckAndDownloadAsync(configuration.ManifestUris, timeout.Token,
                    packageLifetime.Token).ConfigureAwait(false);
                policyBlocked = false;
                SetReady(checkedUpdate);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (packagePolicy?.IsCancellationRequested == true)
            {
                SetReady(null);
                log("副屏正在使用，已暂停正式版安装包下载；连接结束后自动继续。");
            }
            catch (UpdatePolicyBlockedException ex)
            {
                policyBlocked = true;
                SetReady(null);
                log("自动更新已安全阻止，旧缓存不会被复用：" + ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
            {
                log("自动更新检查暂不可用：" + ex.Message);
                if (policyBlocked) SetReady(null);
                else
                {
                    try { SetReady(await client.TryLoadPendingAsync(lifetime).ConfigureAwait(false)); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                }
            }
            finally
            {
                if (packagePolicy is not null) EndPackageAttempt(packagePolicy);
            }
            try { await checkWake.WaitAsync(configuration.CheckInterval, lifetime).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    void SetReady(PendingWindowsUpdate? update)
    {
        bool changed;
        lock (sync)
        {
            changed = ready?.ReleaseId != update?.ReleaseId || ready?.Version != update?.Version || ready?.Build != update?.Build;
            ready = update;
        }
        if (changed) ReadyChanged?.Invoke(update);
    }

    public async Task<bool> TryLaunchReadyUpdaterAsync()
    {
        lock (sync)
        {
            if (updaterStarted || ready is null) return false;
            updaterStarted = true;
        }
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var expectedInstall = WindowsUpdatePathPolicy.GetExpectedInstallDirectory(programFiles);
            if (!applicationDirectory.Equals(expectedInstall, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请从正式安装目录运行 TabLink 后再自动安装更新：" + expectedInstall);
            WindowsUpdatePathPolicy.ValidateFormalInstallLocation(applicationDirectory, programFiles, programData);
            ProtectedUpdaterStager.VerifyInstallTreeSecurity(applicationDirectory);
            var pending = await client.TryLoadPendingAsync(CancellationToken.None).ConfigureAwait(false);
            if (pending is null) throw new InvalidDataException("已下载更新未能重新通过签名和哈希验证。");
            var protectedHelper = ProtectedUpdaterStager.Stage(
                applicationDirectory,
                programData);
            var transactionDirectory = Path.Combine(cacheDirectory, "transactions", DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transactionDirectory);
            if ((File.GetAttributes(transactionDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新事务目录不能是重解析点。");
            using var current = Process.GetCurrentProcess();
            var helper = protectedHelper.ExecutablePath;
            var readySignal = Path.Combine(transactionDirectory, "helper-ready.json");
            var handshakeNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var protectedTransactionId = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = protectedHelper.DirectoryPath };
            Add(start, "--package", pending.PackagePath);
            Add(start, "--size", pending.Size.ToString(CultureInfo.InvariantCulture));
            Add(start, "--sha256", pending.Sha256);
            Add(start, "--manifest-envelope", pending.ManifestEnvelopePath);
            Add(start, "--release-id", pending.ReleaseId);
            Add(start, "--build", pending.Build.ToString(CultureInfo.InvariantCulture));
            Add(start, "--install-dir", applicationDirectory);
            Add(start, "--target-exe", "TabLink.exe");
            Add(start, "--wait-pid", current.Id.ToString(CultureInfo.InvariantCulture));
            Add(start, "--wait-start-utc-ticks", current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            Add(start, "--version", pending.Version);
            Add(start, "--transaction-dir", transactionDirectory);
            Add(start, "--ready-signal", readySignal);
            Add(start, "--handshake-nonce", handshakeNonce);
            Add(start, "--protected-transaction-id", protectedTransactionId);
            WindowsUpdatePathPolicy.ValidateFormalInstallLocation(applicationDirectory, programFiles, programData);
            ProtectedUpdaterStager.VerifyInstallTreeSecurity(applicationDirectory);
            ProtectedUpdaterStager.VerifyBeforeLaunch(protectedHelper, applicationDirectory);
            using var updater = Process.Start(start) ?? throw new IOException("无法启动独立更新程序。");
            var updaterStartTicks = updater.StartTime.ToUniversalTime().Ticks;
            if (!await WaitForUpdaterHandshakeAsync(updater, readySignal, current.Id, current.StartTime.ToUniversalTime().Ticks,
                updater.Id, updaterStartTicks, handshakeNonce).ConfigureAwait(false))
            {
                try { if (!updater.HasExited) updater.Kill(true); } catch (InvalidOperationException) { }
                try { await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
                if (!updater.HasExited) throw new IOException("更新程序未能在超时后终止，拒绝清理仍在使用的事务目录。");
                ProtectedUpdaterStager.CleanupAbandonedPreReadyTransaction(
                    applicationDirectory, programFiles, programData, protectedTransactionId);
                throw new IOException("独立更新程序未能确认当前 TabLink 进程，已取消本次安装。");
            }
            log("正式版 " + pending.Version + " 已准备完成；TabLink 退出后将自动安装并验证启动。");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            lock (sync) updaterStarted = false;
            log("自动安装暂未启动：" + ex.Message);
            return false;
        }
    }

    static void Add(ProcessStartInfo start, string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }

    static async Task<bool> WaitForUpdaterHandshakeAsync(Process updater, string path, int expectedPid, long expectedStartTicks,
        int expectedUpdaterPid, long expectedUpdaterStartTicks, string expectedNonce)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (updater.HasExited) return false;
            try
            {
                WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(Path.GetDirectoryName(path)!);
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length is <= 0 or > 64 * 1024) return false;
                    var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    return UpdateSignalSecurity.ValidateHandshake(bytes, expectedPid, expectedStartTicks,
                        expectedUpdaterPid, expectedUpdaterStartTicks, expectedNonce);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return false; }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (downloadSync) active = activePackagePolicy;
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
        http.Dispose();
    }
}
