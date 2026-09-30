using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace TabLink.Windows;

internal sealed record UpdaterHelperDigest(string Name, long Length, string Sha256);
internal sealed record ProtectedTreeFileDigest(string RelativePath, long Length, string Sha256);

internal sealed record ProtectedUpdaterBundle(
    string CommonApplicationDataRoot,
    string DirectoryPath,
    string ExecutablePath,
    string BundleSha256,
    IReadOnlyList<UpdaterHelperDigest> Files);

internal sealed record ProtectedUpdateTransactionPaths(
    string ProgramDataRoot,
    string SecurityRoot,
    string TransactionsRoot,
    string TransactionRoot,
    string Staging,
    string Backup,
    string Failed);

internal static class ProtectedUpdaterStager
{
    internal static readonly string[] RequiredFileNames =
    [
        "TabLink.Updater.exe",
        "TabLink.Updater.dll",
        "TabLink.Updater.deps.json",
        "TabLink.Updater.runtimeconfig.json"
    ];

    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    const FileSystemRights UsersReadAndExecuteRights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    internal static ProtectedUpdaterBundle Stage(string sourceDirectory, string commonApplicationData)
    {
        EnsureElevated();
        var sourceRoot = NormalizeExistingDirectory(sourceDirectory, "更新组件源目录");
        var programDataRoot = NormalizeExistingDirectory(commonApplicationData, "ProgramData");
        WindowsUpdatePathPolicy.VerifyExistingDirectoryChainWithoutReparse(programDataRoot);
        var snapshots = RequiredFileNames.Select(name => ReadSourceDigest(sourceRoot, name)).ToArray();
        var bundleHash = ComputeBundleSha256(snapshots);
        var bundleDirectory = GetBundleDirectory(programDataRoot, bundleHash);
        var tabLinkRoot = Path.Combine(programDataRoot, "TabLink");
        var updaterRoot = Path.Combine(tabLinkRoot, "Updater");

        EnsureProtectedDirectory(tabLinkRoot, programDataRoot);
        EnsureProtectedDirectory(updaterRoot, tabLinkRoot);
        EnsureProtectedDirectory(bundleDirectory, updaterRoot);

        foreach (var snapshot in snapshots)
            CopyOrValidate(sourceRoot, bundleDirectory, snapshot);

        ValidateProtectedBundle(programDataRoot, bundleDirectory, snapshots);
        return new(programDataRoot, bundleDirectory, Path.Combine(bundleDirectory, RequiredFileNames[0]), bundleHash, snapshots);
    }

    internal static ProtectedUpdateTransactionPaths GetTransactionPaths(string installDirectory, string programFiles, string programData, string transactionId)
    {
        if (transactionId.Length != 32 || !transactionId.All(Uri.IsHexDigit))
            throw new ArgumentException("更新事务标识必须是 32 位十六进制字符串。", nameof(transactionId));
        var formal = WindowsUpdatePathPolicy.Resolve(installDirectory, programFiles, programData);
        var securityRoot = formal.ProgramDataTabLinkRoot;
        var transactionsRoot = formal.TransactionsRoot;
        var transactionRoot = Path.Combine(transactionsRoot, transactionId.ToLowerInvariant());
        foreach (var path in new[] { securityRoot, transactionsRoot, transactionRoot })
            if (!Path.GetPathRoot(path)!.Equals(Path.GetPathRoot(formal.InstallDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新事务目录与正式安装目录不在同一卷。");
        return new(formal.ProgramDataRoot, securityRoot, transactionsRoot, transactionRoot,
            Path.Combine(transactionRoot, "staging"), Path.Combine(transactionRoot, "backup"), Path.Combine(transactionRoot, "failed"));
    }

    internal static ProtectedUpdateTransactionPaths CreateTransactionRoot(string installDirectory, string programFiles, string programData, string transactionId)
    {
        EnsureElevated();
        var formal = WindowsUpdatePathPolicy.ValidateFormalInstallLocation(installDirectory, programFiles, programData);
        var paths = GetTransactionPaths(formal.InstallDirectory, formal.ProgramFilesRoot, formal.ProgramDataRoot, transactionId);
        EnsureProtectedDirectory(paths.SecurityRoot, paths.ProgramDataRoot);
        EnsureProtectedDirectory(paths.TransactionsRoot, paths.SecurityRoot);
        EnsureProtectedDirectory(paths.TransactionRoot, paths.TransactionsRoot);
        if (Directory.Exists(paths.Staging) || File.Exists(paths.Staging) || Directory.Exists(paths.Backup) || File.Exists(paths.Backup) || Directory.Exists(paths.Failed) || File.Exists(paths.Failed))
            throw new IOException("更新事务目录已经包含保留名称。");
        return paths;
    }

    internal static void CleanupAbandonedPreReadyTransaction(string installDirectory, string programFiles, string programData, string transactionId)
    {
        EnsureElevated();
        var formal = WindowsUpdatePathPolicy.ValidateFormalInstallLocation(installDirectory, programFiles, programData);
        VerifyInstallTreeSecurity(formal.InstallDirectory);
        var paths = GetTransactionPaths(formal.InstallDirectory, formal.ProgramFilesRoot, formal.ProgramDataRoot, transactionId);
        if (!Directory.Exists(paths.TransactionRoot)) return;
        VerifyProtectedDirectory(paths.SecurityRoot);
        VerifyProtectedDirectory(paths.TransactionsRoot);
        VerifyProtectedTree(paths.TransactionRoot);
        if (Directory.Exists(paths.Backup) || File.Exists(paths.Backup) || Directory.Exists(paths.Failed) || File.Exists(paths.Failed))
            throw new InvalidDataException("拒绝清理已经进入目录交换阶段的更新事务。");
        Directory.Delete(paths.TransactionRoot, true);
    }

    internal static void VerifyBeforeLaunch(ProtectedUpdaterBundle bundle, string sourceDirectory)
    {
        EnsureElevated();
        var sourceRoot = NormalizeExistingDirectory(sourceDirectory, "更新组件源目录");
        var expectedDirectory = GetBundleDirectory(bundle.CommonApplicationDataRoot, bundle.BundleSha256);
        if (!Path.GetFullPath(bundle.DirectoryPath).Equals(expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("受保护更新组件目录与内容标识不匹配。");

        ValidateProtectedBundle(bundle.CommonApplicationDataRoot, bundle.DirectoryPath, bundle.Files);
        var locked = new List<(FileStream Source, FileStream Target, UpdaterHelperDigest Expected)>();
        try
        {
            foreach (var expected in NormalizeDigests(bundle.Files))
            {
                var source = GetDirectChild(sourceRoot, expected.Name);
                var target = GetDirectChild(bundle.DirectoryPath, expected.Name);
                WindowsUpdatePathPolicy.VerifySafeFile(source, "启动前更新组件源文件 " + expected.Name);
                RejectReparsePoint(source, "更新组件源文件");
                RejectReparsePoint(target, "受保护更新组件文件");
                var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
                try
                {
                    var targetStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
                    locked.Add((sourceStream, targetStream, expected));
                }
                catch
                {
                    sourceStream.Dispose();
                    throw;
                }
            }

            foreach (var item in locked)
            {
                if (item.Source.Length != item.Expected.Length || item.Target.Length != item.Expected.Length)
                    throw new InvalidDataException("更新组件大小在启动前发生变化：" + item.Expected.Name);
                var expectedHash = Convert.FromHexString(item.Expected.Sha256);
                var sourceHash = HashAndRewind(item.Source);
                var targetHash = HashAndRewind(item.Target);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, expectedHash) ||
                    !CryptographicOperations.FixedTimeEquals(targetHash, expectedHash) ||
                    !CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                    throw new InvalidDataException("更新组件 SHA-256 在启动前发生变化：" + item.Expected.Name);
            }
        }
        finally
        {
            for (var index = locked.Count - 1; index >= 0; index--)
            {
                locked[index].Target.Dispose();
                locked[index].Source.Dispose();
            }
        }

        // FileShare.None locks are intentionally released immediately before
        // CreateProcess. Ordinary users still cannot replace the protected
        // files because the owner and DACL are verified again here.
        ValidateProtectedBundle(bundle.CommonApplicationDataRoot, bundle.DirectoryPath, bundle.Files);
    }

    internal static string ComputeBundleSha256(IEnumerable<UpdaterHelperDigest> files)
    {
        var normalized = NormalizeDigests(files);
        using var material = new MemoryStream();
        foreach (var file in normalized)
        {
            var line = Encoding.UTF8.GetBytes(file.Name + "\0" + file.Length.ToString(CultureInfo.InvariantCulture) + "\0" + file.Sha256 + "\n");
            material.Write(line);
        }
        return Convert.ToHexString(SHA256.HashData(material.ToArray()));
    }

    internal static string GetBundleDirectory(string commonApplicationData, string bundleSha256)
    {
        var root = Path.GetFullPath(commonApplicationData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathRooted(root) || root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ProgramData 路径无效。", nameof(commonApplicationData));
        var normalizedHash = NormalizeSha256(bundleSha256, nameof(bundleSha256));
        var updaterRoot = Path.GetFullPath(Path.Combine(root, "TabLink", "Updater")).TrimEnd(Path.DirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(updaterRoot, "sha256-" + normalizedHash.ToLowerInvariant()));
        if (!result.StartsWith(updaterRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("受保护更新组件目录越过 ProgramData 边界。");
        return result;
    }

    internal static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid);
        security.SetGroup(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid);
        security.SetGroup(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static DirectorySecurity CreateInstallDirectorySecurity()
    {
        var security = CreateDirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateInstallFileSecurity()
    {
        var security = CreateFileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }

    internal static FileStream CreateProtectedFile(string path, string expectedParent)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetDirectoryName(full)!.Equals(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("拒绝在受保护目录之外创建更新文件。");
        VerifyProtectedDirectory(parent);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("受保护更新文件已经存在：" + full);
        return new FileInfo(full).Create(
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            128 * 1024,
            FileOptions.WriteThrough | FileOptions.SequentialScan,
            CreateFileSecurity());
    }

    internal static void ProtectExistingTree(string root)
    {
        EnsureElevated();
        var fullRoot = NormalizeExistingDirectory(root, "待保护更新目录");
        var rootInfo = new DirectoryInfo(fullRoot);
        rootInfo.SetAccessControl(CreateDirectorySecurity());
        VerifyProtectedDirectory(fullRoot);

        var entries = Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories).ToArray();
        foreach (var entry in entries)
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新目录树不能包含重解析点：" + entry);

        foreach (var directory in entries.Where(Directory.Exists).OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar)))
            new DirectoryInfo(directory).SetAccessControl(CreateDirectorySecurity());
        foreach (var file in entries.Where(File.Exists))
            new FileInfo(file).SetAccessControl(CreateFileSecurity());
        VerifyProtectedTree(fullRoot);
    }

    internal static void ApplyInstallTreeSecurity(string root)
    {
        EnsureElevated();
        var fullRoot = NormalizeExistingDirectory(root, "安装目录");
        var rootInfo = new DirectoryInfo(fullRoot);
        rootInfo.SetAccessControl(CreateInstallDirectorySecurity());

        var entries = Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories).ToArray();
        foreach (var entry in entries)
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装目录树不能包含重解析点：" + entry);
        foreach (var directory in entries.Where(Directory.Exists).OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar)))
            new DirectoryInfo(directory).SetAccessControl(CreateInstallDirectorySecurity());
        foreach (var file in entries.Where(File.Exists))
            new FileInfo(file).SetAccessControl(CreateInstallFileSecurity());
        VerifyInstallTreeSecurity(fullRoot);
    }

    internal static void VerifyProtectedTree(string root)
    {
        var fullRoot = NormalizeExistingDirectory(root, "受保护更新目录");
        VerifyProtectedDirectory(fullRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("受保护更新目录树包含重解析点：" + entry);
            if (Directory.Exists(entry)) VerifyProtectedDirectory(entry);
            else VerifyProtectedFile(entry);
        }
    }

    internal static void VerifyInstallTreeSecurity(string root)
    {
        var fullRoot = NormalizeExistingDirectory(root, "安装目录");
        VerifyInstallDirectory(fullRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装目录树包含重解析点：" + entry);
            if (Directory.Exists(entry)) VerifyInstallDirectory(entry);
            else VerifyInstallFile(entry);
        }
    }

    internal static IReadOnlyList<ProtectedTreeFileDigest> CaptureInstallTreeSnapshot(string root)
    {
        VerifyInstallTreeSecurity(root);
        return CaptureTreeSnapshot(root);
    }

    internal static void VerifyTreeSnapshot(string root, IReadOnlyList<ProtectedTreeFileDigest> expected, bool requireProtectedTree)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (requireProtectedTree) VerifyProtectedTree(root); else VerifyInstallTreeSecurity(root);
        var actual = CaptureTreeSnapshot(root);
        if (actual.Count != expected.Count) throw new InvalidDataException("回滚目录文件集合已改变。");
        for (var index = 0; index < actual.Count; index++)
        {
            var left = actual[index];
            var right = expected[index];
            if (!left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase) || left.Length != right.Length ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left.Sha256), Convert.FromHexString(right.Sha256)))
                throw new InvalidDataException("回滚目录文件内容已改变：" + right.RelativePath);
        }
    }

    internal static bool HasExactProtectedAcl(FileSystemSecurity security, bool directory)
    {
        if (!security.AreAccessRulesProtected) return false;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid))) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2) return false;
        var required = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid };
        var expectedInheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !required.Remove(sid) || rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != expectedInheritance || rule.PropagationFlags != PropagationFlags.None)
                return false;
        }
        return required.Count == 0;
    }

    internal static bool HasExactInstallAcl(FileSystemSecurity security, bool directory)
    {
        if (!security.AreAccessRulesProtected) return false;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid))) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 3) return false;
        var trusted = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid };
        var sawUsers = false;
        var expectedInheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                rule.InheritanceFlags != expectedInheritance || rule.PropagationFlags != PropagationFlags.None)
                return false;
            if (sid.Equals(UsersSid))
            {
                if (sawUsers || rule.FileSystemRights != UsersReadAndExecuteRights) return false;
                sawUsers = true;
            }
            else if (!trusted.Remove(sid) || rule.FileSystemRights != FileSystemRights.FullControl) return false;
        }
        return sawUsers && trusted.Count == 0;
    }

    static UpdaterHelperDigest[] NormalizeDigests(IEnumerable<UpdaterHelperDigest> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var input = files.ToArray();
        if (input.Length != RequiredFileNames.Length) throw new InvalidDataException("更新组件文件集合不完整。");
        var expected = new HashSet<string>(RequiredFileNames, StringComparer.Ordinal);
        var normalized = new List<UpdaterHelperDigest>(input.Length);
        foreach (var file in input)
        {
            if (!expected.Remove(file.Name)) throw new InvalidDataException("更新组件包含重复或未知文件：" + file.Name);
            if (file.Length <= 0) throw new InvalidDataException("更新组件文件为空：" + file.Name);
            normalized.Add(new(file.Name, file.Length, NormalizeSha256(file.Sha256, nameof(files))));
        }
        if (expected.Count != 0) throw new InvalidDataException("更新组件缺少配套文件：" + string.Join(", ", expected));
        return normalized.OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
    }

    static IReadOnlyList<ProtectedTreeFileDigest> CaptureTreeSnapshot(string root)
    {
        var fullRoot = NormalizeExistingDirectory(root, "待校验目录");
        var result = new List<ProtectedTreeFileDigest>();
        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            RejectReparsePoint(path, "待校验文件");
            var relative = Path.GetRelativePath(fullRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidDataException("待校验文件越过目录边界。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            result.Add(new(relative, stream.Length, Convert.ToHexString(HashAndRewind(stream))));
        }
        return result.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string NormalizeSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA-256 必须是 64 位十六进制字符串。", parameterName);
        return value.ToUpperInvariant();
    }

    static UpdaterHelperDigest ReadSourceDigest(string sourceRoot, string name)
    {
        var path = GetDirectChild(sourceRoot, name);
        WindowsUpdatePathPolicy.VerifySafeFile(path, "更新组件源文件 " + name);
        RejectReparsePoint(path, "更新组件源文件");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("更新组件文件大小无效：" + name);
        if (name.EndsWith(".json", StringComparison.Ordinal))
        {
            if (stream.Length > 8L * 1024 * 1024) throw new InvalidDataException("更新组件 JSON 过大：" + name);
            try
            {
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新组件 JSON 根节点必须是对象：" + name);
            }
            catch (JsonException ex) { throw new InvalidDataException("更新组件 JSON 无效：" + name, ex); }
        }
        var hash = HashAndRewind(stream);
        return new(name, stream.Length, Convert.ToHexString(hash));
    }

    static void CopyOrValidate(string sourceRoot, string targetRoot, UpdaterHelperDigest expected)
    {
        var sourcePath = GetDirectChild(sourceRoot, expected.Name);
        var targetPath = GetDirectChild(targetRoot, expected.Name);
        WindowsUpdatePathPolicy.VerifySafeFile(sourcePath, "更新组件源文件 " + expected.Name);
        RejectReparsePoint(sourcePath, "更新组件源文件");
        if (File.Exists(targetPath))
        {
            VerifyProtectedFile(targetPath);
            CompareLockedFiles(sourcePath, targetPath, expected);
            return;
        }

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        var sourceHash = HashAndRewind(source);
        if (source.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(sourceHash, Convert.FromHexString(expected.Sha256)))
            throw new InvalidDataException("更新组件源文件在复制前发生变化：" + expected.Name);
        using (var target = new FileInfo(targetPath).Create(
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            128 * 1024,
            FileOptions.WriteThrough | FileOptions.SequentialScan,
            CreateFileSecurity()))
        {
            source.CopyTo(target);
            target.Flush(true);
            var targetHash = HashAndRewind(target);
            if (target.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                throw new InvalidDataException("受保护更新组件复制后哈希不一致：" + expected.Name);
        }
        VerifyProtectedFile(targetPath);
    }

    static void CompareLockedFiles(string sourcePath, string targetPath, UpdaterHelperDigest expected)
    {
        RejectReparsePoint(targetPath, "受保护更新组件文件");
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        using var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        if (source.Length != expected.Length || target.Length != expected.Length)
            throw new InvalidDataException("更新组件大小不一致：" + expected.Name);
        var expectedHash = Convert.FromHexString(expected.Sha256);
        var sourceHash = HashAndRewind(source);
        var targetHash = HashAndRewind(target);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, expectedHash) ||
            !CryptographicOperations.FixedTimeEquals(targetHash, expectedHash) ||
            !CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
            throw new InvalidDataException("更新组件文件不一致：" + expected.Name);
    }

    static void ValidateProtectedBundle(string programDataRoot, string bundleDirectory, IReadOnlyList<UpdaterHelperDigest> expectedFiles)
    {
        var expectedHash = ComputeBundleSha256(expectedFiles);
        if (!Path.GetFullPath(bundleDirectory).Equals(GetBundleDirectory(programDataRoot, expectedHash), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("受保护更新组件路径与文件哈希不一致。");
        var tabLinkRoot = Path.Combine(Path.GetFullPath(programDataRoot).TrimEnd(Path.DirectorySeparatorChar), "TabLink");
        var updaterRoot = Path.Combine(tabLinkRoot, "Updater");
        VerifyProtectedDirectory(tabLinkRoot);
        VerifyProtectedDirectory(updaterRoot);
        VerifyProtectedDirectory(bundleDirectory);
        if (Directory.EnumerateDirectories(bundleDirectory).Any()) throw new InvalidDataException("受保护更新组件目录包含意外子目录。");
        var names = Directory.EnumerateFiles(bundleDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var required = RequiredFileNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(required, StringComparer.Ordinal)) throw new InvalidDataException("受保护更新组件目录文件集合不完整或包含额外文件。");
        foreach (var expected in NormalizeDigests(expectedFiles))
        {
            var path = GetDirectChild(bundleDirectory, expected.Name);
            VerifyProtectedFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
            if (stream.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(HashAndRewind(stream), Convert.FromHexString(expected.Sha256)))
                throw new InvalidDataException("受保护更新组件校验失败：" + expected.Name);
        }
    }

    internal static void EnsureProtectedDirectory(string path, string expectedParent)
    {
        var full = NormalizeDirectoryPath(path);
        if (!NormalizeDirectoryPath(Path.GetDirectoryName(full)!).Equals(NormalizeDirectoryPath(expectedParent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("拒绝在预期受保护层级之外创建更新目录。");
        if (!Directory.Exists(full))
        {
            try { new DirectoryInfo(full).Create(CreateDirectorySecurity()); }
            catch (IOException) when (Directory.Exists(full)) { }
        }
        VerifyProtectedDirectory(full);
    }

    internal static void VerifyProtectedDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("受保护更新目录不存在：" + path);
        var info = new DirectoryInfo(path);
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("受保护更新目录不能是重解析点：" + path);
        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasExactProtectedAcl(security, true)) throw new UnauthorizedAccessException("受保护更新目录的 owner 或 DACL 不符合策略：" + path);
    }

    internal static void VerifyProtectedFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("受保护更新组件不存在。", path);
        RejectReparsePoint(path, "受保护更新组件文件");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasExactProtectedAcl(security, false)) throw new UnauthorizedAccessException("受保护更新组件的 owner 或 DACL 不符合策略：" + path);
    }

    static void VerifyInstallDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("安装目录不存在：" + path);
        var info = new DirectoryInfo(path);
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("安装目录不能是重解析点：" + path);
        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasExactInstallAcl(security, true)) throw new UnauthorizedAccessException("安装目录的 owner 或 DACL 不符合执行策略：" + path);
    }

    static void VerifyInstallFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("安装文件不存在。", path);
        RejectReparsePoint(path, "安装文件");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasExactInstallAcl(security, false)) throw new UnauthorizedAccessException("安装文件的 owner 或 DACL 不符合执行策略：" + path);
    }

    static string NormalizeExistingDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(description + "不能为空。", nameof(path));
        var full = NormalizeDirectoryPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(description + "不存在：" + full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(description + "不能是重解析点。");
        return full;
    }

    static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return root is not null && full.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    static string GetDirectChild(string parent, string name)
    {
        if (Path.GetFileName(name) != name || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("更新组件文件名无效。");
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(root, name));
        if (!Path.GetDirectoryName(result)!.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新组件路径越过预期目录。");
        return result;
    }

    static void RejectReparsePoint(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(description + "不存在。", path);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException(description + "不是普通文件：" + path);
    }

    static byte[] HashAndRewind(FileStream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return hash;
    }

    static void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("自动更新只能由已提升权限的 TabLink 主进程启动。");
    }
}
