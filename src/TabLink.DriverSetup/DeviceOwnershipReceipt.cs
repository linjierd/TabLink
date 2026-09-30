using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static string LifecycleRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TabLink");
    private static string LifecycleDirectory => Path.Combine(LifecycleRoot, "DriverLifecycle");
    private static string InstanceReceiptPath => Path.Combine(LifecycleDirectory, "owned-display.json");
    private static string ExpectedPackageMarker => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", ExpectedHashes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key.ToUpperInvariant() + "=" + x.Value.ToUpperInvariant())))));

    private sealed record DeviceOwnershipReceipt(int SchemaVersion, string HardwareId, string InstanceId,
        Guid Generation, string PackageMarker, DateTimeOffset CreatedUtc, string State);

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
        else VerifyProtectedDirectory(path);
    }

    private static void VerifyProtectedDirectoryChain()
    {
        VerifyProtectedDirectory(LifecycleRoot);
        VerifyProtectedDirectory(LifecycleDirectory);
    }

    private static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateProtectedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

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
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !owner.Equals(AdministratorsSid) && !owner.Equals(SystemSid))
            throw new UnauthorizedAccessException("驱动生命周期路径所有者不受信任：" + path);
        var required = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow || !required.Remove(sid) ||
                (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl ||
                directory && (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) !=
                    (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                throw new UnauthorizedAccessException("驱动生命周期路径 ACL 不符合仅 SYSTEM/Administrators 可写要求：" + path);
        }
        if (required.Count != 0 || rules.Length != 2)
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
