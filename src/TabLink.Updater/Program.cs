using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TabLink.Windows;

namespace TabLink.Updater;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? logPath = null;
        try
        {
            var options = UpdaterArguments.Parse(args);
            ValidatePaths(options);
            using var owner = GetExactOwner(options);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var transactionPaths = ProtectedUpdaterStager.CreateTransactionRoot(
                options.InstallDirectory, programFiles, programData, options.ProtectedTransactionId);
            var protectedLog = Path.Combine(transactionPaths.TransactionRoot, "updater.log");
            using (ProtectedUpdaterStager.CreateProtectedFile(protectedLog, transactionPaths.TransactionRoot)) { }
            logPath = protectedLog;
            return RunAsync(options, owner, transactionPaths,
                message => File.AppendAllText(protectedLog, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}")).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try { if (logPath is not null) File.AppendAllText(logPath, ex + Environment.NewLine); } catch { }
            return 1;
        }
    }

    static async Task<int> RunAsync(UpdaterArguments options, Process owner, ProtectedUpdateTransactionPaths transactionPaths, Action<string> log)
    {
        var staging = transactionPaths.Staging;
        var backup = transactionPaths.Backup;
        var failed = transactionPaths.Failed;
        log("验证更新包并解压到隔离暂存目录。");
        FileStream? lockedPackage = null;
        ExtractedUpdatePackage? extracted = null;
        IReadOnlyList<ProtectedTreeFileDigest>? rollbackSnapshot = null;
        var preflight = new UpdatePreflightState();
        var readySignaled = false;
        try
        {
            lockedPackage = new FileStream(options.PackagePath, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await ValidateSignedPackageAsync(options, lockedPackage).ConfigureAwait(false);
            preflight = preflight with { SignedPackageValidated = true };
            lockedPackage.Position = 0;
            extracted = SafeZipExtractor.Extract(lockedPackage, staging, protectForElevatedExecution: true);
            lockedPackage.Position = 0;
            SafeZipExtractor.VerifyExtractedFiles(lockedPackage, staging, extracted, requireProtectedTree: true);
            preflight = preflight with { ExtractedFilesVerified = true };
            ProtectedUpdaterStager.VerifyProtectedTree(staging);
            preflight = preflight with { ProtectedStagingVerified = true };
            rollbackSnapshot = ProtectedUpdaterStager.CaptureInstallTreeSnapshot(options.InstallDirectory);
            WriteProtectedJson(Path.Combine(transactionPaths.TransactionRoot, "rollback-manifest.json"),
                transactionPaths.TransactionRoot, rollbackSnapshot);
            preflight = preflight with { RollbackSnapshotCaptured = true };
            preflight.DemandReadyAllowed();
            ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
            WriteReadySignal(options);
            readySignaled = true;

            log("等待指定的 TabLink 主进程正常退出。");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                await owner.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            // Re-validate both the signed ZIP and every extracted file after
            // the owner exits and immediately before the directory swap.
            lockedPackage.Position = 0;
            await ValidateSignedPackageAsync(options, lockedPackage).ConfigureAwait(false);
            lockedPackage.Position = 0;
            SafeZipExtractor.VerifyExtractedFiles(lockedPackage, staging, extracted, requireProtectedTree: true);
            ProtectedUpdaterStager.VerifyProtectedTree(staging);
            ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
            ProtectedUpdaterStager.VerifyTreeSnapshot(options.InstallDirectory, rollbackSnapshot!, requireProtectedTree: false);
            lockedPackage.Dispose();
            lockedPackage = null;
        }
        catch
        {
            lockedPackage?.Dispose();
            TryDeleteProtectedStaging(staging, transactionPaths.TransactionRoot);
            if (readySignaled) await WaitAndRelaunchExistingAsync(owner, options, log).ConfigureAwait(false);
            throw;
        }
        if (rollbackSnapshot is null) throw new InvalidDataException("未能建立受保护回滚快照。");
        var originalMoved = false;
        var swapped = false;
        try
        {
            ProtectedUpdaterStager.VerifyProtectedTree(staging);
            if (Directory.Exists(backup) || File.Exists(backup) || Directory.Exists(failed) || File.Exists(failed))
                throw new IOException("受保护更新事务的 backup 或 failed 名称已被占用。");
            Directory.Move(options.InstallDirectory, backup);
            originalMoved = true;
            ProtectedUpdaterStager.ProtectExistingTree(backup);
            ProtectedUpdaterStager.VerifyProtectedTree(backup);
            ProtectedUpdaterStager.VerifyTreeSnapshot(backup, rollbackSnapshot, requireProtectedTree: true);
            Directory.Move(staging, options.InstallDirectory);
            swapped = true;
            ProtectedUpdaterStager.ApplyInstallTreeSecurity(options.InstallDirectory);
            ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
            var health = Path.Combine(transactionPaths.TransactionRoot, "startup-health.json");
            if (File.Exists(health) || Directory.Exists(health)) throw new IOException("受保护启动健康信号路径已被占用。");
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var updated = StartTarget(options, health, nonce);
            if (!await WaitForHealthAsync(updated, health, nonce, options.InstallDirectory, TimeSpan.FromSeconds(45)).ConfigureAwait(false))
                throw new IOException("新版 TabLink 未能通过启动健康检查。");
            WriteProtectedJson(Path.Combine(transactionPaths.TransactionRoot, "success.json"), transactionPaths.TransactionRoot,
                new { version = options.Version, installedAtUtc = DateTimeOffset.UtcNow, backup });
            log("新版启动验证通过；保留上一版本备份以便人工恢复。");
            return 0;
        }
        catch (Exception updateFailure)
        {
            log("新版启动失败，正在恢复上一版本：" + updateFailure.Message);
            try
            {
                if (swapped && Directory.Exists(options.InstallDirectory))
                {
                    Directory.Move(options.InstallDirectory, failed);
                    ProtectedUpdaterStager.ProtectExistingTree(failed);
                    ProtectedUpdaterStager.VerifyProtectedTree(failed);
                }
                if (originalMoved && Directory.Exists(backup))
                {
                    ProtectedUpdaterStager.ProtectExistingTree(backup);
                    ProtectedUpdaterStager.VerifyProtectedTree(backup);
                    ProtectedUpdaterStager.VerifyTreeSnapshot(backup, rollbackSnapshot, requireProtectedTree: true);
                    Directory.Move(backup, options.InstallDirectory);
                    ProtectedUpdaterStager.ApplyInstallTreeSecurity(options.InstallDirectory);
                    ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
                    ProtectedUpdaterStager.VerifyTreeSnapshot(options.InstallDirectory, rollbackSnapshot, requireProtectedTree: false);
                }
                StartExisting(options);
                log("上一版本已恢复并重新启动。失败版本保留用于诊断：" + failed);
            }
            catch (Exception rollbackFailure)
            {
                log("自动恢复失败：" + rollbackFailure);
                throw new AggregateException("更新失败且无法自动恢复。上一版本备份位于：" + backup, updateFailure, rollbackFailure);
            }
            return 2;
        }
        finally { lockedPackage?.Dispose(); }
    }

    static void ValidatePaths(UpdaterArguments options)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        WindowsUpdatePathPolicy.ValidateFormalInstallLocation(options.InstallDirectory, programFiles, programData);
        ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
        var target = Path.GetFullPath(Path.Combine(options.InstallDirectory, options.TargetExecutable));
        if (!Path.GetDirectoryName(target)!.Equals(options.InstallDirectory, StringComparison.OrdinalIgnoreCase) || !File.Exists(target) ||
            (File.GetAttributes(target) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new FileNotFoundException("找不到普通文件形式的当前 TabLink 主程序。", target);
        if (!File.Exists(options.PackagePath) || (File.GetAttributes(options.PackagePath) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("更新包不存在或是重解析点。", options.PackagePath);
        if (!File.Exists(options.ManifestEnvelopePath) || (File.GetAttributes(options.ManifestEnvelopePath) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("签名更新清单不存在或是重解析点。", options.ManifestEnvelopePath);
        if (new FileInfo(options.ManifestEnvelopePath).Length is <= 0 or > 384 * 1024) throw new InvalidDataException("签名更新清单大小无效。");
        var localUpdates = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink", "updates")).TrimEnd(Path.DirectorySeparatorChar);
        var localTransactions = Path.Combine(localUpdates, "transactions");
        if (!WindowsUpdatePathPolicy.IsDescendantOrSelf(localUpdates, options.PackagePath) || options.PackagePath.Equals(localUpdates, StringComparison.OrdinalIgnoreCase) ||
            !WindowsUpdatePathPolicy.IsDescendantOrSelf(localUpdates, options.ManifestEnvelopePath) || options.ManifestEnvelopePath.Equals(localUpdates, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(options.TransactionDirectory)!.Equals(localTransactions, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包和事务目录必须位于 TabLink 本机更新缓存中。");
        if (!Directory.Exists(options.TransactionDirectory)) throw new DirectoryNotFoundException("更新握手目录不存在。" + options.TransactionDirectory);
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(localUpdates);
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(options.TransactionDirectory);
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(Path.GetDirectoryName(options.PackagePath)!);
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(Path.GetDirectoryName(options.ManifestEnvelopePath)!);
        if (!Path.GetDirectoryName(options.ReadySignalPath)!.Equals(options.TransactionDirectory, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(options.ReadySignalPath) != "helper-ready.json" || File.Exists(options.ReadySignalPath) || Directory.Exists(options.ReadySignalPath))
            throw new InvalidDataException("更新程序握手路径无效。");
    }

    static void WriteProtectedJson<T>(string path, string parent, T value)
    {
        using var stream = ProtectedUpdaterStager.CreateProtectedFile(path, parent);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(true);
    }

    static async Task ValidateSignedPackageAsync(UpdaterArguments options, FileStream package)
    {
        var envelope = await File.ReadAllBytesAsync(options.ManifestEnvelopePath).ConfigureAwait(false);
        var manifest = UpdateManifestVerifier.VerifyAndParse(envelope, Convert.FromBase64String(UpdateTrust.ManifestSignerSpkiBase64));
        var artifact = manifest.Artifacts.SingleOrDefault(a => a.Platform == "windows-x64") ?? throw new InvalidDataException("签名清单不包含 Windows 正式版安装包。");
        if (manifest.MinimumProtocolVersion > UpdateManifestVerifier.CurrentProtocolVersion || manifest.ReleaseId != options.ReleaseId || artifact.Version != options.Version || artifact.Build != options.Build || artifact.Size != options.PackageSize || !artifact.Sha256.Equals(options.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新参数与签名清单不一致。");
        var currentExecutable = Path.Combine(options.InstallDirectory, options.TargetExecutable);
        var currentVersion = FileVersionInfo.GetVersionInfo(currentExecutable).ProductVersion?.Split('+')[0];
        if (string.IsNullOrWhiteSpace(currentVersion) || !UpdateVersionPolicy.IsUpgrade(currentVersion, artifact.Version))
            throw new InvalidDataException("签名安装包版本不高于当前正式版，拒绝降级或重复安装。");
        if (package.Length != options.PackageSize) throw new InvalidDataException("更新包大小已改变。");
        var hash = await SHA256.HashDataAsync(package).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(options.PackageSha256))) throw new InvalidDataException("更新包 SHA-256 已改变。");
    }

    static Process GetExactOwner(UpdaterArguments options)
    {
        Process process;
        try { process = Process.GetProcessById(options.WaitPid); }
        catch (ArgumentException ex) { throw new InvalidDataException("需要更新的 TabLink 进程已经不存在。", ex); }
        try
        {
            var expected = Path.GetFullPath(Path.Combine(options.InstallDirectory, options.TargetExecutable));
            var actual = Path.GetFullPath(process.MainModule?.FileName ?? "");
            if (process.StartTime.ToUniversalTime().Ticks != options.WaitStartUtcTicks || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("等待进程的启动时间或程序路径不匹配。");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    static void WriteReadySignal(UpdaterArguments options)
    {
        ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(options.TransactionDirectory);
        if (File.Exists(options.ReadySignalPath) || Directory.Exists(options.ReadySignalPath))
            throw new IOException("更新握手信号已被预占。");
        var temporary = options.ReadySignalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var updater = Process.GetCurrentProcess();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new
                {
                    waitPid = options.WaitPid,
                    waitStartUtcTicks = options.WaitStartUtcTicks,
                    updaterPid = Environment.ProcessId,
                    updaterStartUtcTicks = updater.StartTime.ToUniversalTime().Ticks,
                    nonce = options.HandshakeNonce,
                    readyAtUtc = DateTimeOffset.UtcNow
                });
                stream.Flush(true);
            }
            File.Move(temporary, options.ReadySignalPath, false);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    static async Task WaitAndRelaunchExistingAsync(Process owner, UpdaterArguments options, Action<string> log)
    {
        try
        {
            if (!owner.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await owner.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            if (Directory.Exists(options.InstallDirectory) && File.Exists(Path.Combine(options.InstallDirectory, options.TargetExecutable)))
            {
                StartExisting(options);
                log("更新预检失败；原版本未被修改并已重新启动。");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { log("更新预检失败后无法自动重新启动原版本：" + ex.Message); }
    }

    static void StartExisting(UpdaterArguments options)
    {
        ProtectedUpdaterStager.VerifyInstallTreeSecurity(options.InstallDirectory);
        var target = Path.Combine(options.InstallDirectory, options.TargetExecutable);
        _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = options.InstallDirectory })
            ?? throw new IOException("无法重新启动原版本 TabLink。");
    }

    static Process StartTarget(UpdaterArguments options, string healthPath, string nonce)
    {
        var target = Path.Combine(options.InstallDirectory, options.TargetExecutable);
        var start = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = options.InstallDirectory };
        start.ArgumentList.Add("--post-update-health");
        start.ArgumentList.Add(healthPath);
        start.ArgumentList.Add(nonce);
        return Process.Start(start) ?? throw new IOException("无法启动新版 TabLink。");
    }

    static async Task<bool> WaitForHealthAsync(Process process, string path, string nonce, string installDirectory, TimeSpan timeout)
    {
        var expectedPid = process.Id;
        var expectedStartTicks = process.StartTime.ToUniversalTime().Ticks;
        var expectedExecutable = Path.GetFullPath(Path.Combine(installDirectory, "TabLink.exe"));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return false;
            if (File.Exists(path))
            {
                try
                {
                    var parent = Path.GetDirectoryName(path)!;
                    WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(parent);
                    ProtectedUpdaterStager.VerifyProtectedDirectory(parent);
                    ProtectedUpdaterStager.VerifyProtectedFile(path);
                    var info = new FileInfo(path);
                    if (info.Length is <= 0 or > 64 * 1024) return false;
                    var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    if (UpdateSignalSecurity.ValidateHealth(bytes, expectedPid, expectedStartTicks, expectedExecutable, nonce))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                        return !process.HasExited;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        return false;
    }

    static void TryDeleteProtectedStaging(string directory, string expectedParent)
    {
        var full = Path.GetFullPath(directory);
        if (!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).Equals("staging", StringComparison.Ordinal)) return;
        try
        {
            if (!Directory.Exists(full)) return;
            ProtectedUpdaterStager.VerifyProtectedTree(full);
            Directory.Delete(full, true);
        }
        catch { }
    }
}
