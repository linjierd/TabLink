using System.Security.Cryptography;
using System.Text;
using TabLink.Core;

namespace TabLink.Windows;

/// <summary>
/// Creates the only storage that can authorize automatic elevated USB cleanup.
/// The user's LocalAppData records remain useful for display recovery and
/// diagnosis, but are never promoted into ADB removal authority.
/// </summary>
internal static class UsbReverseProtectedStorage
{
    static readonly IUsbReverseQueueFileSecurity ProtectedQueueFiles = new ProtectedQueueFileSecurity();
    internal static string ProgramDataRoot => Path.GetFullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    internal static string TabLinkRoot => Path.Combine(ProgramDataRoot, "TabLink");
    internal static string QueueFolder => Path.Combine(TabLinkRoot, "UsbReverseCleanup");
    internal static string MutationLockPath => Path.Combine(QueueFolder, ".mutation.lock");
    internal static IUsbReverseQueueFileSecurity QueueFileSecurity => ProtectedQueueFiles;

    internal static PendingUsbReverseCleanupQueue OpenQueue()
    {
        EnsureProtectedDirectoryChain(QueueFolder);
        var completed = Path.Combine(QueueFolder, "completed");
        ProtectedUpdaterStager.EnsureProtectedDirectory(completed, QueueFolder);
        return new PendingUsbReverseCleanupQueue(QueueFolder, fileSecurity: QueueFileSecurity);
    }

    internal static void VerifyQueueStorage()
    {
        WindowsUpdatePathPolicy.VerifySafeNamespaceChain(ProgramDataRoot, "USB 清理保护父路径");
        ProtectedUpdaterStager.VerifyProtectedDirectory(TabLinkRoot);
        ProtectedUpdaterStager.VerifyProtectedDirectory(QueueFolder);
        ProtectedUpdaterStager.VerifyProtectedDirectory(Path.Combine(QueueFolder, "completed"));
    }

    internal static void EnsureMutationLockFile()
    {
        OpenQueue();
        if (!File.Exists(MutationLockPath))
        {
            try { using var _ = ProtectedUpdaterStager.CreateProtectedFile(MutationLockPath, QueueFolder); }
            catch (IOException) when (File.Exists(MutationLockPath)) { }
        }
        VerifyMutationLockFile();
    }

    internal static void VerifyMutationLockFile()
    {
        VerifyQueueStorage();
        ProtectedUpdaterStager.VerifyProtectedFile(MutationLockPath);
    }

    internal static void EnsureProtectedDirectoryChain(string leaf)
    {
        if (string.IsNullOrWhiteSpace(ProgramDataRoot) ||
            ProgramDataRoot.Equals(Path.GetPathRoot(ProgramDataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ProgramData 路径无效，不能创建 USB 清理保护目录。");
        WindowsUpdatePathPolicy.VerifySafeNamespaceChain(ProgramDataRoot, "USB 清理保护父路径");
        ProtectedUpdaterStager.EnsureProtectedDirectory(TabLinkRoot, ProgramDataRoot);
        if (!leaf.Equals(TabLinkRoot, StringComparison.OrdinalIgnoreCase))
            ProtectedUpdaterStager.EnsureProtectedDirectory(leaf, TabLinkRoot);
        WindowsUpdatePathPolicy.VerifySafeNamespaceChain(ProgramDataRoot, "USB 清理保护父路径");
        ProtectedUpdaterStager.VerifyProtectedDirectory(TabLinkRoot);
        if (!leaf.Equals(TabLinkRoot, StringComparison.OrdinalIgnoreCase))
            ProtectedUpdaterStager.VerifyProtectedDirectory(leaf);
    }

    sealed class ProtectedQueueFileSecurity : IUsbReverseQueueFileSecurity
    {
        public void VerifyStorage(string folder, string completedFolder)
        {
            var expectedFolder = Path.GetFullPath(QueueFolder);
            var expectedCompleted = Path.Combine(expectedFolder, "completed");
            if (!Path.GetFullPath(folder).Equals(expectedFolder, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(completedFolder).Equals(expectedCompleted, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("USB 清理队列路径不属于受保护的 ProgramData 存储。");
            VerifyQueueStorage();
        }

        public FileStream CreateProtectedFile(string path, string expectedParent)
        {
            VerifyManagedPath(path, expectedParent);
            return ProtectedUpdaterStager.CreateProtectedFile(path, expectedParent);
        }

        public void VerifyProtectedFile(string path)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidDataException("USB 清理队列文件缺少父目录。");
            VerifyManagedPath(path, parent);
            ProtectedUpdaterStager.VerifyProtectedFile(path);
        }

        static void VerifyManagedPath(string path, string expectedParent)
        {
            var full = Path.GetFullPath(path);
            var actualParent = Path.GetDirectoryName(full)
                ?? throw new InvalidDataException("USB 清理队列文件缺少父目录。");
            var suppliedParent = Path.GetFullPath(expectedParent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var queue = Path.GetFullPath(QueueFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var completed = Path.Combine(queue, "completed");
            if (!actualParent.Equals(suppliedParent, StringComparison.OrdinalIgnoreCase) ||
                !(actualParent.Equals(queue, StringComparison.OrdinalIgnoreCase) ||
                  actualParent.Equals(completed, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("USB 清理队列文件路径超出受保护目录。");
        }
    }
}

/// <summary>
/// Stages the pinned Android Platform-Tools triplet into protected ProgramData
/// before any elevated caller launches it. Public packages may omit these
/// files: an explicitly located hash-matching r37 triplet can seed the protected
/// cache, while background cleanup only reuses that cache or a matching bundle
/// and never consults user settings, environment variables, or PATH.
/// </summary>
internal sealed record TrustedAdbStorageBoundary(
    string TabLinkRoot,
    Action<string> VerifyExistingSourceDirectoryChain,
    Action<string> EnsureProtectedDirectoryChain,
    Action<string, string> EnsureProtectedDirectory,
    Func<string, string, FileStream> CreateProtectedFile,
    Action<string> VerifyProtectedDirectory,
    Action<string> VerifyProtectedFile);

internal static class TrustedBundledAdb
{
    static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adb.exe"] = "957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71",
            ["AdbWinApi.dll"] = "120BEF587119C6CB926B86B9BE90FDFBCE38937588EAE28CD91A94CE63C7B965",
            ["AdbWinUsbApi.dll"] = "6CA69A2CA0E31309C087D288F058977D421AD03500E4C3E1DBD981241A069C60"
        };

    static TrustedAdbStorageBoundary ProductionStorage => new(
        UsbReverseProtectedStorage.TabLinkRoot,
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse,
        UsbReverseProtectedStorage.EnsureProtectedDirectoryChain,
        ProtectedUpdaterStager.EnsureProtectedDirectory,
        ProtectedUpdaterStager.CreateProtectedFile,
        ProtectedUpdaterStager.VerifyProtectedDirectory,
        ProtectedUpdaterStager.VerifyProtectedFile);

    /// <summary>
    /// Locates a complete platform-tools installation using the ordinary UI
    /// precedence rules, but never executes it in place. Only the fixed,
    /// audited triplet is copied into protected ProgramData and returned.
    /// </summary>
    internal static string? LocateStageAndGetVerifiedPath(string? configuredPath)
    {
        Exception? protectedFailure = null;
        try
        {
            var protectedPath = TryGetVerifiedProtectedPath();
            if (protectedPath is not null) return protectedPath;
        }
        catch (Exception ex) when (IsTrustStorageFailure(ex)) { protectedFailure = ex; }
        var source = AdbLocator.FindAdbPath(configuredPath);
        if (source is not null) return StageAndGetVerifiedPath(source);
        if (protectedFailure is not null)
            throw new InvalidDataException("受保护 ADB 副本校验失败，且没有可信源可用于恢复。", protectedFailure);
        return null;
    }

    /// <summary>
    /// Background recovery never trusts settings, SDK variables, or PATH. It
    /// may use only the fixed triplet shipped beside TabLink.
    /// </summary>
    internal static string? StageAndGetVerifiedPath()
    {
        Exception? protectedFailure = null;
        try
        {
            var protectedPath = TryGetVerifiedProtectedPath();
            if (protectedPath is not null) return protectedPath;
        }
        catch (Exception ex) when (IsTrustStorageFailure(ex)) { protectedFailure = ex; }
        var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "tools", "platform-tools", "adb.exe"));
        var source = Path.GetDirectoryName(bundled)!;
        if (!ExpectedHashes.Keys.All(name => File.Exists(Path.Combine(source, name))))
        {
            if (protectedFailure is not null)
                throw new InvalidDataException("受保护 ADB 副本校验失败，且完整交付目录没有可信源可用于恢复。", protectedFailure);
            return null;
        }
        return StageAndGetVerifiedPath(bundled);
    }

    /// <summary>
    /// Verifies a located or explicitly selected source and returns only its
    /// protected copy. Callers must never execute <paramref name="sourceAdbPath"/>.
    /// </summary>
    internal static string StageAndGetVerifiedPath(string sourceAdbPath)
        => StageAndGetVerifiedPath(sourceAdbPath, ExpectedHashes, ProductionStorage);

    /// <summary>
    /// The injected overload exists so crash-cut staging repair can be proven
    /// entirely inside an offline fixture. Production always supplies the
    /// fixed r37 manifest and exact ProgramData protection boundary above.
    /// </summary>
    internal static string StageAndGetVerifiedPath(string sourceAdbPath,
        IReadOnlyDictionary<string, string> expectedHashes,
        TrustedAdbStorageBoundary storage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAdbPath);
        ArgumentNullException.ThrowIfNull(expectedHashes);
        ArgumentNullException.ThrowIfNull(storage);
        var hashes = NormalizeExpectedHashes(expectedHashes);
        var executable = Path.GetFullPath(sourceAdbPath);
        if (!string.Equals(Path.GetFileName(executable), "adb.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Android Platform-Tools 主程序文件名必须是 adb.exe。");
        var source = Path.GetDirectoryName(executable)
            ?? throw new InvalidDataException("Android Platform-Tools 源目录无效。");
        storage.VerifyExistingSourceDirectoryChain(source);

        // Verify and keep every source handle open before creating any durable
        // target state. FileShare.Read prevents a user-writable SDK triplet from
        // being changed or replaced between hashing and the protected copy.
        var inputs = new List<(string Name, string Hash, FileStream Stream)>();
        try
        {
            foreach (var expected in hashes)
                inputs.Add((expected.Key, expected.Value,
                    OpenVerifiedSource(Path.Combine(source, expected.Key), expected.Value)));

            var adbRoot = Path.Combine(storage.TabLinkRoot, "Adb");
            var target = ProtectedTargetDirectory(storage.TabLinkRoot, hashes);
            storage.EnsureProtectedDirectoryChain(adbRoot);
            storage.EnsureProtectedDirectory(target, adbRoot);

            foreach (var input in inputs)
            {
                input.Stream.Position = 0;
                var targetPath = Path.Combine(target, input.Name);
                if (File.Exists(targetPath))
                {
                    try { VerifyProtectedFile(targetPath, input.Hash, storage); }
                    catch (InvalidDataException)
                    {
                        // Only replace bad content after the file itself still
                        // passes the protected owner/DACL, ordinary-file, and
                        // non-reparse predicate. Unsafe objects are preserved
                        // for diagnosis and fail closed rather than being used
                        // as a privileged deletion primitive.
                        storage.VerifyProtectedFile(targetPath);
                        File.Delete(targetPath);
                    }
                }
                if (!File.Exists(targetPath))
                {
                    using var output = storage.CreateProtectedFile(targetPath, target);
                    input.Stream.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                VerifyProtectedFile(targetPath, input.Hash, storage);
            }
            return VerifyProtectedTarget(target, hashes, storage);
        }
        finally { foreach (var input in inputs) input.Stream.Dispose(); }
    }

    static string? TryGetVerifiedProtectedPath()
    {
        var target = ProtectedTargetDirectory();
        if (!Directory.Exists(target)) return null;
        WindowsUpdatePathPolicy.VerifySafeNamespaceChain(UsbReverseProtectedStorage.ProgramDataRoot,
            "ADB 受保护副本父路径");
        ProtectedUpdaterStager.VerifyProtectedDirectory(UsbReverseProtectedStorage.TabLinkRoot);
        ProtectedUpdaterStager.VerifyProtectedDirectory(Path.Combine(UsbReverseProtectedStorage.TabLinkRoot, "Adb"));
        return VerifyProtectedTarget(target);
    }

    static string ProtectedTargetDirectory()
        => ProtectedTargetDirectory(UsbReverseProtectedStorage.TabLinkRoot, ExpectedHashes);

    internal static string ProtectedTargetDirectory(string tabLinkRoot,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabLinkRoot);
        var hashes = NormalizeExpectedHashes(expectedHashes);
        var manifest = string.Join("\n", hashes.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + ":" + x.Value));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(tabLinkRoot), "Adb", "sha256-" + identity);
    }

    static string VerifyProtectedTarget(string target)
        => VerifyProtectedTarget(target, ExpectedHashes, ProductionStorage);

    static string VerifyProtectedTarget(string target,
        IReadOnlyDictionary<string, string> expectedHashes,
        TrustedAdbStorageBoundary storage)
    {
        storage.VerifyProtectedDirectory(target);
        if (Directory.EnumerateDirectories(target).Any())
            throw new InvalidDataException("受保护 ADB 目录包含意外子目录。");
        var names = Directory.EnumerateFiles(target).Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expectedHashes.Keys.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("受保护 ADB 目录文件集合不完整或包含额外文件。");
        foreach (var expected in expectedHashes)
            VerifyProtectedFile(Path.Combine(target, expected.Key), expected.Value, storage);
        return Path.Combine(target, "adb.exe");
    }

    internal static bool IsTrustStorageFailure(Exception ex) => ex is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException;

    static void VerifyProtectedFile(string path, string expectedHash)
        => VerifyProtectedFile(path, expectedHash, ProductionStorage);

    static void VerifyProtectedFile(string path, string expectedHash,
        TrustedAdbStorageBoundary storage)
    {
        storage.VerifyProtectedFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        if (!Hash(stream).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("受保护 Android Platform-Tools 内容已变化：" + Path.GetFileName(path));
    }

    static IReadOnlyDictionary<string, string> NormalizeExpectedHashes(
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        ArgumentNullException.ThrowIfNull(expectedHashes);
        var requiredNames = ExpectedHashes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var suppliedNames = expectedHashes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!suppliedNames.SequenceEqual(requiredNames, StringComparer.Ordinal))
            throw new ArgumentException("ADB 固定哈希清单必须只包含受支持的三件套。", nameof(expectedHashes));
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in requiredNames)
        {
            var digest = expectedHashes[name];
            byte[] bytes;
            try { bytes = Convert.FromHexString(digest); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new ArgumentException("ADB 固定 SHA-256 清单无效。", nameof(expectedHashes), ex);
            }
            if (bytes.Length != SHA256.HashSizeInBytes)
                throw new ArgumentException("ADB 固定 SHA-256 清单无效。", nameof(expectedHashes));
            normalized.Add(name, Convert.ToHexString(bytes));
        }
        return normalized;
    }

    static FileStream OpenVerifiedSource(string path, string expectedHash)
    {
        RejectNonRegularFile(path, "ADB 源文件");
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        try
        {
            if (input.Length <= 0 || !Hash(input).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Android Platform-Tools 固定 SHA-256 校验失败：" + Path.GetFileName(path));
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    static string Hash(Stream stream)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    static void RejectNonRegularFile(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(description + "不存在：" + Path.GetFileName(path));
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException(description + "必须是普通文件：" + Path.GetFileName(path));
    }
}
