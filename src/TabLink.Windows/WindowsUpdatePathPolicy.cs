using System.Security.AccessControl;
using System.Security.Principal;

namespace TabLink.Windows;

internal sealed record FormalWindowsUpdatePaths(
    string ProgramFilesRoot,
    string ProgramDataRoot,
    string InstallDirectory,
    string ProgramDataTabLinkRoot,
    string TransactionsRoot);

internal static class WindowsUpdatePathPolicy
{
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier CreatorOwnerSid = new(WellKnownSidType.CreatorOwnerSid, null);
    static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    const FileSystemRights MutationRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    const uint GenericWrite = 0x40000000;
    const uint GenericAll = 0x10000000;

    internal static FormalWindowsUpdatePaths Resolve(string installDirectory, string programFiles, string programData)
    {
        var filesRoot = NormalizeLocalDirectoryPath(programFiles, nameof(programFiles));
        var dataRoot = NormalizeLocalDirectoryPath(programData, nameof(programData));
        if (!Path.GetPathRoot(filesRoot)!.Equals(Path.GetPathRoot(dataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Program Files 与 ProgramData 必须位于同一实际本地卷。");

        var expectedInstall = NormalizeLocalDirectoryPath(Path.Combine(filesRoot, "TabLink"), nameof(installDirectory));
        var suppliedInstall = NormalizeLocalDirectoryPath(installDirectory, nameof(installDirectory));
        if (!suppliedInstall.Equals(expectedInstall, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows 自动安装只能从正式目录运行：" + expectedInstall);

        var tabLinkData = NormalizeLocalDirectoryPath(Path.Combine(dataRoot, "TabLink"), nameof(programData));
        var transactions = NormalizeLocalDirectoryPath(Path.Combine(tabLinkData, "Transactions"), nameof(programData));
        return new(filesRoot, dataRoot, expectedInstall, tabLinkData, transactions);
    }

    internal static string GetExpectedInstallDirectory(string programFiles) =>
        NormalizeLocalDirectoryPath(Path.Combine(NormalizeLocalDirectoryPath(programFiles, nameof(programFiles)), "TabLink"), nameof(programFiles));

    internal static FormalWindowsUpdatePaths ValidateFormalInstallLocation(string installDirectory, string programFiles, string programData)
    {
        var paths = Resolve(installDirectory, programFiles, programData);
        var namespaceChain = EnumerateDirectoryChain(paths.ProgramFilesRoot)
            .Concat(EnumerateDirectoryChain(paths.ProgramDataRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var directory in namespaceChain)
        {
            VerifyExistingDirectoryWithoutReparse(directory);
            VerifySafeNamespaceContainer(directory, "自动更新父路径 " + directory);
        }
        VerifyExistingDirectoryWithoutReparse(paths.InstallDirectory);
        VerifyStrictContainer(paths.InstallDirectory, "TabLink 正式安装目录");
        if (Directory.Exists(paths.ProgramDataTabLinkRoot))
        {
            VerifyExistingDirectoryWithoutReparse(paths.ProgramDataTabLinkRoot);
            VerifyStrictContainer(paths.ProgramDataTabLinkRoot, "ProgramData TabLink 受保护目录");
        }
        else if (File.Exists(paths.ProgramDataTabLinkRoot))
            throw new InvalidDataException("ProgramData TabLink 受保护目录被普通文件占用。");
        return paths;
    }

    internal static void VerifyExistingDirectoryChainWithoutReparse(string path)
    {
        foreach (var directory in EnumerateDirectoryChain(path)) VerifyExistingDirectoryWithoutReparse(directory);
    }

    internal static void VerifySafeNamespaceChain(string path, string description)
    {
        foreach (var directory in EnumerateDirectoryChain(path))
        {
            VerifyExistingDirectoryWithoutReparse(directory);
            VerifySafeNamespaceContainer(directory, description + " " + directory);
        }
    }

    internal static bool HasNoOrdinaryUserMutationAccess(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedPrivilegedSid(owner)) return false;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var rawRights = unchecked((uint)(int)rule.FileSystemRights);
            if ((rule.FileSystemRights & MutationRights) == 0 && (rawRights & (GenericWrite | GenericAll)) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrustedPrivilegedSid(sid)) return false;
        }
        return true;
    }

    internal static bool HasSafeAncestorNamespaceAcl(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedPrivilegedSid(owner)) return false;
        const FileSystemRights replacementRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) return false;
            if (IsTrustedPrivilegedSid(sid)) continue;
            if (sid.Equals(CreatorOwnerSid) && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            // An inherit-only ACE cannot replace the existing next component.
            // Every actual descendant is inspected separately, and the TabLink
            // protection boundaries reject any mutation ACE that did inherit.
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var rawRights = unchecked((uint)(int)rule.FileSystemRights);
            if ((rule.FileSystemRights & replacementRights) != 0 || (rawRights & GenericAll) != 0) return false;
        }
        return true;
    }

    internal static void VerifySafeFile(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(description + "不存在。", path);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException(description + "必须是普通文件。");
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasNoOrdinaryUserMutationAccess(security))
            throw new UnauthorizedAccessException(description + "的 owner 或 DACL 允许普通用户修改、删除或更改安全设置。");
    }

    internal static IReadOnlyList<string> EnumerateDirectoryChain(string path)
    {
        var current = new DirectoryInfo(NormalizeLocalDirectoryPath(path, nameof(path)));
        var result = new List<string>();
        while (true)
        {
            result.Add(current.FullName.TrimEnd(Path.DirectorySeparatorChar) + (current.Parent is null ? Path.DirectorySeparatorChar : ""));
            var parent = current.Parent;
            if (parent is null) break;
            current = parent;
        }
        result.Reverse();
        return result;
    }

    internal static bool IsDescendantOrSelf(string root, string path)
    {
        var normalizedRoot = NormalizeLocalDirectoryPath(root, nameof(root));
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static void VerifyStrictContainer(string path, string description)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasNoOrdinaryUserMutationAccess(security))
            throw new UnauthorizedAccessException(description + "的 owner 或 DACL 允许普通用户写入、删除或更改安全设置。");
    }

    static void VerifySafeNamespaceContainer(string path, string description)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!HasSafeAncestorNamespaceAcl(security))
            throw new UnauthorizedAccessException(description + "的 owner 或 DACL 允许普通用户替换既有受保护子目录。");
    }

    static bool IsTrustedPrivilegedSid(SecurityIdentifier sid) =>
        sid.Equals(SystemSid) || sid.Equals(AdministratorsSid) || sid.Equals(TrustedInstallerSid);

    static void VerifyExistingDirectoryWithoutReparse(string path)
    {
        var current = new DirectoryInfo(path);
        current.Refresh();
        if (!current.Exists) throw new DirectoryNotFoundException("路径父链不存在：" + current.FullName);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("自动更新路径父链不能包含重解析点：" + current.FullName);
    }

    static string NormalizeLocalDirectoryPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException("路径必须是本地盘符上的绝对路径。", parameterName);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is null || root.StartsWith("\\\\", StringComparison.Ordinal) || root.Length != 3 || root[1] != ':' || root[2] != Path.DirectorySeparatorChar)
            throw new ArgumentException("路径必须位于本地盘符卷。", parameterName);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
