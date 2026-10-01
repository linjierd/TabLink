using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    private static readonly SecurityIdentifier SystemSid = DriverLifecycleSecurityPolicy.SystemSid;
    private static readonly SecurityIdentifier AdministratorsSid = DriverLifecycleSecurityPolicy.AdministratorsSid;
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static string ProgramDataRoot => Path.GetFullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    private static string LifecycleRoot => Path.Combine(ProgramDataRoot, "TabLink");
    private static string LifecycleDirectory => Path.Combine(LifecycleRoot, "DriverLifecycle");
    internal static string DisplayLeaseRoot => Path.Combine(LifecycleRoot, "DisplayLeases");
    private static string DisplayLeaseLockPath => Path.Combine(DisplayLeaseRoot, ".display-lease.lock");
    private static string InstanceReceiptPath => Path.Combine(LifecycleDirectory, "owned-display.json");
    private static string ExpectedPackageMarker => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", ExpectedHashes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key.ToUpperInvariant() + "=" + x.Value.ToUpperInvariant())))));

    private sealed record DeviceOwnershipReceipt(int SchemaVersion, string HardwareId, string InstanceId,
        Guid Generation, string PackageMarker, DateTimeOffset CreatedUtc, string State);

    internal static IDisposable AcquireDisplayLeaseMutationLock()
    {
        VerifyDisplayLeaseParentNamespace();
        EnsureProtectedDirectory(LifecycleRoot);
        EnsureProtectedDirectory(DisplayLeaseRoot);
        if (!File.Exists(DisplayLeaseLockPath))
        {
            try
            {
                using var created = new FileInfo(DisplayLeaseLockPath).Create(
                    FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 1,
                    FileOptions.WriteThrough, CreateProtectedFileSecurity());
                created.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(DisplayLeaseLockPath)) { }
        }
        VerifyDisplayLeaseProtectedNamespace();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            try
            {
                return new FileStream(DisplayLeaseLockPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
        }
    }

    internal static void VerifyDisplayLeaseProtectedNamespace()
    {
        VerifyDisplayLeaseParentNamespace();
        VerifyProtectedDirectory(LifecycleRoot);
        VerifyProtectedDirectory(DisplayLeaseRoot);
        VerifyProtectedFile(DisplayLeaseLockPath);
    }

    internal static void VerifyDisplayLeaseStateFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), DisplayLeaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("显示租约状态文件越过受保护目录边界。");
        VerifyProtectedFile(full);
    }

    private static void VerifyDisplayLeaseParentNamespace()
    {
        if (string.IsNullOrWhiteSpace(ProgramDataRoot) ||
            ProgramDataRoot.Equals(Path.GetPathRoot(ProgramDataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ProgramData 路径无效，不能创建显示租约保护目录。");
        TabLink.Windows.WindowsUpdatePathPolicy.VerifySafeNamespaceChain(
            ProgramDataRoot, "显示租约保护父路径");
    }

    private static string? ReadOwnedInstanceReceipt()
    {
        if (!File.Exists(InstanceReceiptPath)) return null;
        VerifyProtectedDirectoryChain();
        VerifyProtectedFile(InstanceReceiptPath);
        if ((File.GetAttributes(InstanceReceiptPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("TabLink 虚拟显示设备回执不能是重解析点。");
        var receipt = JsonSerializer.Deserialize<DeviceOwnershipReceipt>(File.ReadAllBytes(InstanceReceiptPath))
            ?? throw new InvalidDataException("TabLink 虚拟显示设备回执为空。");
        if (receipt.SchemaVersion != 1 || receipt.HardwareId != HardwareId || receipt.Generation == Guid.Empty ||
            receipt.State != "installed" || !receipt.PackageMarker.Equals(ExpectedPackageMarker, StringComparison.Ordinal) ||
            receipt.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5) || receipt.CreatedUtc < DateTimeOffset.UtcNow.AddYears(-5) ||
            receipt.InstanceId.Length is < 5 or > 1024 || receipt.InstanceId.Contains('*') || receipt.InstanceId.Contains('?') ||
            !receipt.InstanceId.StartsWith("ROOT\\DISPLAY\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TabLink 虚拟显示设备回执内容无效。");
        return receipt.InstanceId;
    }

    private static void WriteOwnedInstanceReceipt(string exactInstance)
    {
        if (!IsOwnedConfiguration() || string.IsNullOrWhiteSpace(exactInstance) || exactInstance.Contains('*') || exactInstance.Contains('?') ||
            !exactInstance.StartsWith("ROOT\\DISPLAY\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法为未确认所有权的虚拟显示设备写入回执。");
        EnsureProtectedDirectoryChain();
        var receipt = new DeviceOwnershipReceipt(1, HardwareId, exactInstance, Guid.NewGuid(),
            ExpectedPackageMarker, DateTimeOffset.UtcNow, "installed");
        var temporary = Path.Combine(LifecycleDirectory, "owned-display." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, receipt, new JsonSerializerOptions { WriteIndented = true });
                output.Flush(true);
            }
            new FileInfo(temporary).SetAccessControl(CreateProtectedFileSecurity());
            VerifyProtectedFile(temporary);
            File.Move(temporary, InstanceReceiptPath, true);
            new FileInfo(InstanceReceiptPath).SetAccessControl(CreateProtectedFileSecurity());
            VerifyProtectedFile(InstanceReceiptPath);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    private static void DeleteOwnedInstanceReceipt(string exactInstance)
    {
        var saved = ReadOwnedInstanceReceipt();
        if (saved is null) return;
        if (!saved.Equals(exactInstance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("受保护设备回执已指向另一个实例，未删除回执。");
        File.Delete(InstanceReceiptPath);
    }

    private static void EnsureProtectedDirectoryChain()
    {
        EnsureProtectedDirectory(LifecycleRoot);
        EnsureProtectedDirectory(LifecycleDirectory);
        VerifyProtectedDirectoryChain();
    }

    private static void EnsureProtectedDirectory(string path)
    {
        if (File.Exists(path)) throw new IOException("驱动生命周期目录路径被文件占用。");
        if (!Directory.Exists(path)) new DirectoryInfo(path).Create(CreateProtectedDirectorySecurity());
        VerifyProtectedDirectory(path);
    }

    private static void VerifyProtectedDirectoryChain()
    {
        VerifyProtectedDirectory(LifecycleRoot);
        VerifyProtectedDirectory(LifecycleDirectory);
    }

    private static DirectorySecurity CreateProtectedDirectorySecurity()
        => DriverLifecycleSecurityPolicy.CreateDirectorySecurity();

    private static FileSecurity CreateProtectedFileSecurity()
        => DriverLifecycleSecurityPolicy.CreateFileSecurity();

    private static void VerifyProtectedDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("驱动生命周期目录不存在或是重解析点：" + path);
        VerifyRules(info.GetAccessControl(), path, true);
    }

    private static void VerifyProtectedFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("驱动生命周期回执不存在或是重解析点。");
        VerifyRules(info.GetAccessControl(), path, false);
    }

    private static void VerifyRules(FileSystemSecurity security, string path, bool directory)
    {
        if (!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(security, directory))
            throw new UnauthorizedAccessException("驱动生命周期路径 ACL 缺少受信任主体或包含额外规则：" + path);
    }

    private static void HardenOwnedConfigurationSecurity()
    {
        if (!IsOwnedConfiguration())
            throw new UnauthorizedAccessException("不能收紧不属于 TabLink 的虚拟显示配置目录。");
        ApplyConfigurationDirectorySecurity();
        foreach (var path in new[] { Path.Combine(ConfigurationDirectory, "vdd_settings.xml"), Path.Combine(ConfigurationDirectory, "tablink-owner.txt") })
        {
            new FileInfo(path).SetAccessControl(CreateConfigurationFileSecurity());
            VerifyConfigurationSecurity(new FileInfo(path).GetAccessControl(), path, false);
        }
        if (!IsOwnedConfiguration())
            throw new IOException("虚拟显示配置在权限收紧期间发生变化。");
    }

    private static void ApplyConfigurationDirectorySecurity()
    {
        var info = new DirectoryInfo(ConfigurationDirectory);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("虚拟显示配置目录不存在或是重解析点。");
        info.SetAccessControl(CreateConfigurationDirectorySecurity());
        VerifyConfigurationSecurity(info.GetAccessControl(), ConfigurationDirectory, true);
    }

    private static void VerifyOwnedConfigurationSecurity()
    {
        if (!Directory.Exists(ConfigurationDirectory) || File.Exists(ConfigurationDirectory) ||
            (File.GetAttributes(ConfigurationDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("虚拟显示配置目录不存在、被文件占用或是重解析点。");
        VerifyConfigurationSecurity(new DirectoryInfo(ConfigurationDirectory).GetAccessControl(), ConfigurationDirectory, true);
        foreach (var path in new[] { Path.Combine(ConfigurationDirectory, "vdd_settings.xml"), Path.Combine(ConfigurationDirectory, "tablink-owner.txt") })
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("虚拟显示配置文件不存在或是重解析点：" + path);
            VerifyConfigurationSecurity(new FileInfo(path).GetAccessControl(), path, false);
        }
    }

    private static DirectorySecurity CreateConfigurationDirectorySecurity()
    {
        var security = CreateProtectedDirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void CreateSecureConfigurationDirectoryExclusive()
    {
        var descriptor = CreateConfigurationDirectorySecurity().GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SECURITY_ATTRIBUTES
            {
                Length = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = false
            };
            if (!CreateDirectoryWithSecurity(ConfigurationDirectory, ref attributes))
                ThrowLastError("无法以严格权限独占创建虚拟显示配置目录");
            VerifyConfigurationSecurity(new DirectoryInfo(ConfigurationDirectory).GetAccessControl(), ConfigurationDirectory, true);
        }
        finally { Marshal.FreeHGlobal(descriptorPointer); }
    }

    private static FileSecurity CreateConfigurationFileSecurity()
    {
        var security = CreateProtectedFileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }

    private static void VerifyConfigurationSecurity(FileSystemSecurity security, string path, bool directory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !owner.Equals(AdministratorsSid) && !owner.Equals(SystemSid))
            throw new UnauthorizedAccessException("虚拟显示配置路径所有者不受信任：" + path);
        var required = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid, UsersSid };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow || !required.Remove(sid))
                throw new UnauthorizedAccessException("虚拟显示配置 ACL 包含额外、继承或拒绝规则：" + path);
            var expected = sid.Equals(UsersSid) ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl;
            const FileSystemRights writeOrOwnership = FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if ((rule.FileSystemRights & expected) != expected || sid.Equals(UsersSid) &&
                (rule.FileSystemRights & writeOrOwnership) != 0 ||
                directory && (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) !=
                    (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                throw new UnauthorizedAccessException("虚拟显示配置 ACL 权限不符合要求：" + path);
        }
        if (required.Count != 0 || rules.Length != 3)
            throw new UnauthorizedAccessException("虚拟显示配置 ACL 缺少受信任主体或包含额外规则：" + path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWithSecurity(string path, ref SECURITY_ATTRIBUTES attributes);
}
