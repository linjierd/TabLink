using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.DriverSetup;

internal static class DriverLifecycleSecurityPolicy
{
    internal static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(AdministratorsSid);
        security.SetGroup(AdministratorsSid);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
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

    internal static bool HasExactProtectedAcl(FileSystemSecurity security, bool directory)
    {
        if (!security.AreAccessRulesProtected) return false;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid)) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2) return false;
        var required = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid };
        var expectedInheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !required.Remove(sid) || rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != expectedInheritance || rule.PropagationFlags != PropagationFlags.None)
                return false;
        }
        return required.Count == 0;
    }
}

internal interface IDriverLifecycleHandoffProtection
{
    void EnsureAndVerifyRoot(string root);
    FileStream CreateProtectedFile(string path);
    void VerifyProtectedFile(string path);
}

internal sealed class ProductionDriverLifecycleHandoffProtection : IDriverLifecycleHandoffProtection
{
    readonly string programDataRoot;
    readonly string expectedRoot;

    internal ProductionDriverLifecycleHandoffProtection(string programDataRoot)
    {
        this.programDataRoot = NormalizeLocalRoot(programDataRoot);
        expectedRoot = Path.Combine(this.programDataRoot, "TabLink", "DriverLifecycle", "Handoffs");
    }

    internal string Root => expectedRoot;

    public void EnsureAndVerifyRoot(string root)
    {
        if (!Path.GetFullPath(root).Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("驱动生命周期移交目录偏离受保护 ProgramData 路径。");
        TabLink.Windows.WindowsUpdatePathPolicy.VerifySafeNamespaceChain(programDataRoot, "驱动生命周期移交父路径");
        var tabLink = Path.Combine(programDataRoot, "TabLink");
        var lifecycle = Path.Combine(tabLink, "DriverLifecycle");
        EnsureProtectedDirectory(tabLink);
        EnsureProtectedDirectory(lifecycle);
        EnsureProtectedDirectory(expectedRoot);
    }

    public FileStream CreateProtectedFile(string path)
    {
        VerifyDirectChild(path);
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
            FileOptions.WriteThrough, DriverLifecycleSecurityPolicy.CreateFileSecurity());
    }

    public void VerifyProtectedFile(string path)
    {
        VerifyDirectChild(path);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("驱动生命周期移交记录不存在或不是普通文件。");
        if (!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(
            info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), directory: false))
            throw new UnauthorizedAccessException("驱动生命周期移交记录 ACL 不符合仅 SYSTEM/Administrators 可写要求。");
    }

    void EnsureProtectedDirectory(string path)
    {
        if (File.Exists(path)) throw new IOException("驱动生命周期受保护目录被普通文件占用：" + path);
        if (!Directory.Exists(path)) new DirectoryInfo(path).Create(DriverLifecycleSecurityPolicy.CreateDirectorySecurity());
        var info = new DirectoryInfo(path);
        info.Refresh();
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("驱动生命周期受保护目录不存在或是重解析点：" + path);
        if (!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(
            info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), directory: true))
            throw new UnauthorizedAccessException("驱动生命周期受保护目录 ACL 不符合仅 SYSTEM/Administrators 可写要求：" + path);
    }

    void VerifyDirectChild(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("驱动生命周期移交文件越过受保护目录边界。");
    }

    static string NormalizeLocalRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException("ProgramData 必须是本地绝对路径。", nameof(path));
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full);
        if (root is null || root.StartsWith("\\\\", StringComparison.Ordinal) || root.Length != 3 || root[1] != ':' ||
            full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ProgramData 必须位于本地盘符的非根目录。", nameof(path));
        return full;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DriverLifecycleHandoffRecord(
    int SchemaVersion,
    Guid HandoffId,
    string NonceHex,
    int HostPid,
    long HostStartUtcTicks,
    string Command,
    string[] Arguments,
    string CommandArgumentsSha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string State);

internal sealed record DriverLifecycleHandoffCredentials(
    Guid HandoffId, string NonceHex, int HostPid, long HostStartUtcTicks)
{
    internal static DriverLifecycleHandoffCredentials Parse(
        string id, string nonce, string pid, string startUtcTicks)
    {
        if (!Guid.TryParseExact(id, "N", out var handoffId) || handoffId == Guid.Empty || id != handoffId.ToString("N"))
            throw new ArgumentException("驱动生命周期移交 ID 无效。");
        if (!DriverLifecycleHandoffProtocol.IsCanonicalSha256(nonce))
            throw new ArgumentException("驱动生命周期移交 nonce 无效。");
        if (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPid) || hostPid <= 0 ||
            pid != hostPid.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("驱动生命周期移交宿主 PID 无效。");
        if (!long.TryParse(startUtcTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 ||
            startUtcTicks != ticks.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("驱动生命周期移交宿主启动时间无效。");
        return new(handoffId, nonce, hostPid, ticks);
    }

    internal void AppendTo(Collection<string> arguments)
    {
        arguments.Add("--handoff-id");
        arguments.Add(HandoffId.ToString("N"));
        arguments.Add("--handoff-nonce");
        arguments.Add(NonceHex);
        arguments.Add("--handoff-host-pid");
        arguments.Add(HostPid.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--handoff-host-start-utc-ticks");
        arguments.Add(HostStartUtcTicks.ToString(CultureInfo.InvariantCulture));
    }
}

internal sealed record HeldDriverCommandInvocation(
    string Command, string[] CommandArguments, DriverLifecycleHandoffCredentials Credentials)
{
    internal static HeldDriverCommandInvocation Parse(string[] args, int commandArgumentCount)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (commandArgumentCount < 0 || args.Length != 1 + commandArgumentCount + 8)
            throw new ArgumentException("受保护的 held 命令参数数量无效。");
        var offset = 1 + commandArgumentCount;
        if (args[offset] != "--handoff-id" || args[offset + 2] != "--handoff-nonce" ||
            args[offset + 4] != "--handoff-host-pid" || args[offset + 6] != "--handoff-host-start-utc-ticks")
            throw new ArgumentException("受保护的 held 命令移交参数顺序无效。");
        var credentials = DriverLifecycleHandoffCredentials.Parse(
            args[offset + 1], args[offset + 3], args[offset + 5], args[offset + 7]);
        return new(args[0], args.Skip(1).Take(commandArgumentCount).ToArray(), credentials);
    }
}

internal interface IExactHostProcessLease : IDisposable
{
    bool IsRunning { get; }
}

internal sealed class ExactHostProcessLease : IExactHostProcessLease
{
    readonly Process process;

    ExactHostProcessLease(Process process) => this.process = process;

    internal static IExactHostProcessLease Open(int pid, long expectedStartUtcTicks)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { throw new IOException("驱动生命周期移交宿主进程已退出。", ex); }
        try
        {
            // Accessing SafeHandle forces an exact kernel process handle to be
            // retained, preventing a later PID reuse from satisfying the ticket.
            _ = process.SafeHandle.DangerousGetHandle();
            var actual = process.StartTime.ToUniversalTime().Ticks;
            if (actual != expectedStartUtcTicks || process.HasExited)
                throw new IOException("驱动生命周期移交宿主身份与 PID/启动时间不匹配。");
            return new ExactHostProcessLease(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void Dispose() => process.Dispose();
}

internal sealed class ConsumedDriverLifecycleHandoff : IDisposable
{
    readonly IExactHostProcessLease host;
    internal ConsumedDriverLifecycleHandoff(IExactHostProcessLease host) => this.host = host;
    public void Dispose() => host.Dispose();
}

internal static class DriverLifecycleHandoffProtocol
{
    internal const int SchemaVersion = 1;
    internal const string PreparedState = "Prepared";
    // The helper's operation mutex has a two-minute bounded wait. Keep one
    // additional minute for process startup and strict validation, while still
    // making a leaked credential useless after a short window.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    internal const string LifecycleMutexName = @"Global\TabLink.SingleDisplayDriverLifecycle.1";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool IsCanonicalSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static string ComputeCommandArgumentsSha256(string command, IReadOnlyList<string> arguments)
    {
        ValidateCommandAndArguments(command, arguments);
        using var material = new MemoryStream();
        WriteLengthPrefixed(material, command);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, arguments.Count);
        material.Write(count);
        foreach (var argument in arguments) WriteLengthPrefixed(material, argument);
        return Convert.ToHexString(SHA256.HashData(material.ToArray()));
    }

    internal static void ValidateRecord(DriverLifecycleHandoffRecord record, string expectedFileId)
    {
        if (record.SchemaVersion != SchemaVersion || record.State != PreparedState || record.HandoffId == Guid.Empty ||
            record.HandoffId.ToString("N") != expectedFileId || !IsCanonicalSha256(record.NonceHex) ||
            record.HostPid <= 0 || record.HostStartUtcTicks <= 0 || record.Arguments is null ||
            record.CreatedUtc.Offset != TimeSpan.Zero || record.ExpiresUtc.Offset != TimeSpan.Zero ||
            record.ExpiresUtc <= record.CreatedUtc || record.ExpiresUtc - record.CreatedUtc != Lifetime)
            throw new InvalidDataException("驱动生命周期移交记录结构或时限无效。");
        var digest = ComputeCommandArgumentsSha256(record.Command, record.Arguments);
        if (!FixedHexEquals(digest, record.CommandArgumentsSha256))
            throw new InvalidDataException("驱动生命周期移交记录的命令参数摘要无效。");
    }

    internal static void ValidateCommandAndArguments(string command, IReadOnlyList<string> arguments)
    {
        if (command is not "--prepare-single-display-held" and not "--remove-session-display-held")
            throw new ArgumentException("命令不允许使用驱动生命周期 held 移交。", nameof(command));
        if (arguments.Count > 3 || arguments.Any(x => x is null || x.Length == 0 || x.Length > 16 ||
            x.Any(c => c is < '0' or > '9')))
            throw new ArgumentException("held 命令参数不是规范化无符号十进制值。", nameof(arguments));
        if (command == "--prepare-single-display-held" && arguments.Count != 3 ||
            command == "--remove-session-display-held" && arguments.Count != 0)
            throw new ArgumentException("held 命令与参数数量不匹配。", nameof(arguments));
    }

    internal static bool FixedHexEquals(string? left, string? right)
    {
        if (left is null || right is null || !IsCanonicalSha256(left) || !IsCanonicalSha256(right)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    static void WriteLengthPrefixed(Stream target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        target.Write(length);
        target.Write(bytes);
    }
}

internal sealed class DriverLifecycleHandoffStore
{
    const int MaximumRecordBytes = 16 * 1024;
    readonly string root;
    readonly IDriverLifecycleHandoffProtection protection;
    readonly Func<DateTimeOffset> utcNow;

    internal DriverLifecycleHandoffStore(string root, IDriverLifecycleHandoffProtection protection,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            throw new ArgumentException("驱动生命周期移交目录必须是绝对路径。", nameof(root));
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        this.protection = protection ?? throw new ArgumentNullException(nameof(protection));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal static DriverLifecycleHandoffStore CreateProduction()
    {
        var security = new ProductionDriverLifecycleHandoffProtection(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        return new(security.Root, security);
    }

    internal DriverLifecycleHandoffRecord CreateRecord(string command, IReadOnlyList<string> arguments,
        int hostPid, long hostStartUtcTicks, Guid? handoffId = null, string? nonce = null)
    {
        DriverLifecycleHandoffProtocol.ValidateCommandAndArguments(command, arguments);
        if (hostPid <= 0 || hostStartUtcTicks <= 0) throw new ArgumentOutOfRangeException(nameof(hostPid));
        var now = utcNow().ToUniversalTime();
        var id = handoffId ?? Guid.NewGuid();
        if (id == Guid.Empty) throw new ArgumentException("移交 ID 不能是空 GUID。", nameof(handoffId));
        var secret = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!DriverLifecycleHandoffProtocol.IsCanonicalSha256(secret))
            throw new ArgumentException("移交 nonce 必须是 256 位规范大写十六进制值。", nameof(nonce));
        var normalized = arguments.ToArray();
        return new(DriverLifecycleHandoffProtocol.SchemaVersion, id, secret, hostPid, hostStartUtcTicks,
            command, normalized, DriverLifecycleHandoffProtocol.ComputeCommandArgumentsSha256(command, normalized),
            now, now + DriverLifecycleHandoffProtocol.Lifetime, DriverLifecycleHandoffProtocol.PreparedState);
    }

    internal void Publish(DriverLifecycleHandoffRecord record)
    {
        DriverLifecycleHandoffProtocol.ValidateRecord(record, record.HandoffId.ToString("N"));
        using var storeLock = AcquireStoreLock();
        CleanupExpiredAbandonedRecordsCore();
        var prepared = PreparedPath(record.HandoffId);
        var consumed = ConsumedPath(record.HandoffId);
        var temporary = TemporaryPath(record);
        if (File.Exists(prepared) || File.Exists(consumed) || File.Exists(temporary))
            throw new IOException("驱动生命周期移交 ID 已存在，拒绝覆盖或重放。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, DriverLifecycleHandoffProtocol.JsonOptions);
        if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("驱动生命周期移交记录过大。");
        var temporaryCreated = false;
        try
        {
            using (var stream = protection.CreateProtectedFile(temporary))
            {
                temporaryCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            protection.VerifyProtectedFile(temporary);
            File.Move(temporary, prepared, overwrite: false);
            protection.VerifyProtectedFile(prepared);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                if (!temporaryCreated) DeleteOnlyIfExact(temporary, record);
                else
                {
                    // This call created the unique path under the store lock.
                    // It may contain only a prefix if serialization I/O failed,
                    // so verify the protected file identity and remove exactly
                    // that temporary instead of trying to parse partial JSON.
                    protection.VerifyProtectedFile(temporary);
                    File.Delete(temporary);
                }
            }
        }
    }

    internal ConsumedDriverLifecycleHandoff Consume(DriverLifecycleHandoffCredentials credentials,
        string command, IReadOnlyList<string> arguments,
        Func<int, long, IExactHostProcessLease> openHost,
        Func<string, bool> lifecycleMutexIsHeld)
    {
        ArgumentNullException.ThrowIfNull(openHost);
        ArgumentNullException.ThrowIfNull(lifecycleMutexIsHeld);
        DriverLifecycleHandoffProtocol.ValidateCommandAndArguments(command, arguments);
        using var storeLock = AcquireStoreLock();
        var prepared = PreparedPath(credentials.HandoffId);
        var consumed = ConsumedPath(credentials.HandoffId);
        if (!File.Exists(prepared))
            throw new InvalidDataException(File.Exists(consumed)
                ? "驱动生命周期移交记录已经消费，拒绝重放。"
                : "驱动生命周期移交记录不存在。");
        protection.VerifyProtectedFile(prepared);
        var record = ReadRecord(prepared, credentials.HandoffId);
        ValidateRequest(record, credentials, command, arguments, utcNow().ToUniversalTime());
        var host = openHost(credentials.HostPid, credentials.HostStartUtcTicks);
        try
        {
            if (!host.IsRunning) throw new IOException("驱动生命周期移交宿主已退出。");
            if (!lifecycleMutexIsHeld(DriverLifecycleHandoffProtocol.LifecycleMutexName))
                throw new IOException("驱动生命周期移交宿主未继续持有唯一显示生命周期锁。");
            File.Move(prepared, consumed, overwrite: false);
            protection.VerifyProtectedFile(consumed);
            if (!host.IsRunning || !lifecycleMutexIsHeld(DriverLifecycleHandoffProtocol.LifecycleMutexName))
                throw new IOException("驱动生命周期移交消费后，宿主或显示生命周期锁已失效。");
            return new(host);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    internal void CleanupExact(DriverLifecycleHandoffRecord record)
    {
        using var storeLock = AcquireStoreLock();
        foreach (var path in new[] { PreparedPath(record.HandoffId), ConsumedPath(record.HandoffId), TemporaryPath(record) })
            if (File.Exists(path)) DeleteOnlyIfExact(path, record);
    }

    internal void CleanupExpiredAbandonedRecords(Func<int, long, bool>? exactHostIsRunning = null)
    {
        using var storeLock = AcquireStoreLock();
        CleanupExpiredAbandonedRecordsCore(exactHostIsRunning);
    }

    void CleanupExpiredAbandonedRecordsCore(Func<int, long, bool>? exactHostIsRunning = null)
    {
        exactHostIsRunning ??= IsExactHostRunning;
        var now = utcNow().ToUniversalTime();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var suffix = name.EndsWith(".prepared.json", StringComparison.Ordinal) ? ".prepared.json" :
                name.EndsWith(".consumed.json", StringComparison.Ordinal) ? ".consumed.json" : null;
            if (suffix is null) continue;
            var idText = name[..^suffix.Length];
            if (idText.Length != 32 || idText.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                continue;
            protection.VerifyProtectedFile(path);
            var record = ReadRecord(path, Guid.ParseExact(idText, "N"));
            if (now <= record.ExpiresUtc || exactHostIsRunning(record.HostPid, record.HostStartUtcTicks)) continue;
            DeleteOnlyIfExact(path, record);
        }
    }

    IDisposable AcquireStoreLock()
    {
        protection.EnsureAndVerifyRoot(root);
        var path = Path.Combine(root, ".handoff.lock");
        if (!File.Exists(path))
        {
            try
            {
                using var created = protection.CreateProtectedFile(path);
                created.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        protection.VerifyProtectedFile(path);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }

    DriverLifecycleHandoffRecord ReadRecord(string path, Guid expectedId)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumRecordBytes)
            throw new InvalidDataException("驱动生命周期移交记录长度无效。");
        var record = JsonSerializer.Deserialize<DriverLifecycleHandoffRecord>(stream, DriverLifecycleHandoffProtocol.JsonOptions)
            ?? throw new InvalidDataException("驱动生命周期移交记录为空。");
        DriverLifecycleHandoffProtocol.ValidateRecord(record, expectedId.ToString("N"));
        return record;
    }

    static void ValidateRequest(DriverLifecycleHandoffRecord record, DriverLifecycleHandoffCredentials credentials,
        string command, IReadOnlyList<string> arguments, DateTimeOffset now)
    {
        if (record.HandoffId != credentials.HandoffId || record.HostPid != credentials.HostPid ||
            record.HostStartUtcTicks != credentials.HostStartUtcTicks ||
            !DriverLifecycleHandoffProtocol.FixedHexEquals(record.NonceHex, credentials.NonceHex) ||
            !record.Command.Equals(command, StringComparison.Ordinal) || !record.Arguments.SequenceEqual(arguments, StringComparer.Ordinal) ||
            !DriverLifecycleHandoffProtocol.FixedHexEquals(record.CommandArgumentsSha256,
                DriverLifecycleHandoffProtocol.ComputeCommandArgumentsSha256(command, arguments)))
            throw new InvalidDataException("驱动生命周期移交凭据、宿主身份或命令参数不匹配。");
        if (now < record.CreatedUtc - TimeSpan.FromSeconds(5) || now > record.ExpiresUtc)
            throw new InvalidDataException("驱动生命周期移交记录尚未生效或已经过期。");
    }

    void DeleteOnlyIfExact(string path, DriverLifecycleHandoffRecord expected)
    {
        protection.VerifyProtectedFile(path);
        var actual = ReadRecord(path, expected.HandoffId);
        if (!RecordsExactlyEqual(actual, expected))
            throw new IOException("驱动生命周期移交清理目标已变化，未删除任何记录。");
        File.Delete(path);
    }

    static bool RecordsExactlyEqual(DriverLifecycleHandoffRecord left, DriverLifecycleHandoffRecord right) =>
        left.SchemaVersion == right.SchemaVersion && left.HandoffId == right.HandoffId &&
        DriverLifecycleHandoffProtocol.FixedHexEquals(left.NonceHex, right.NonceHex) &&
        left.HostPid == right.HostPid && left.HostStartUtcTicks == right.HostStartUtcTicks &&
        left.Command == right.Command && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
        DriverLifecycleHandoffProtocol.FixedHexEquals(left.CommandArgumentsSha256, right.CommandArgumentsSha256) &&
        left.CreatedUtc.EqualsExact(right.CreatedUtc) && left.ExpiresUtc.EqualsExact(right.ExpiresUtc) && left.State == right.State;

    static bool IsExactHostRunning(int pid, long startUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            _ = process.SafeHandle.DangerousGetHandle();
            return process.StartTime.ToUniversalTime().Ticks == startUtcTicks && !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    string PreparedPath(Guid id) => DirectPath(id.ToString("N") + ".prepared.json");
    string ConsumedPath(Guid id) => DirectPath(id.ToString("N") + ".consumed.json");
    string TemporaryPath(DriverLifecycleHandoffRecord record) => DirectPath(
        record.HandoffId.ToString("N") + "." + record.NonceHex + "." +
        record.HostPid.ToString(CultureInfo.InvariantCulture) + "." +
        record.HostStartUtcTicks.ToString(CultureInfo.InvariantCulture) + ".tmp");

    string DirectPath(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("驱动生命周期移交文件名无效。");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("驱动生命周期移交文件越过受保护目录边界。");
        return path;
    }
}

internal sealed class DriverLifecycleHandoffTicket
{
    readonly DriverLifecycleHandoffStore store;
    internal DriverLifecycleHandoffRecord Record { get; }
    internal DriverLifecycleHandoffCredentials Credentials => new(
        Record.HandoffId, Record.NonceHex, Record.HostPid, Record.HostStartUtcTicks);

    DriverLifecycleHandoffTicket(DriverLifecycleHandoffStore store, DriverLifecycleHandoffRecord record)
    {
        this.store = store;
        Record = record;
    }

    internal static DriverLifecycleHandoffTicket CreateProduction(string command, IReadOnlyList<string> arguments)
    {
        var store = DriverLifecycleHandoffStore.CreateProduction();
        using var current = Process.GetCurrentProcess();
        var record = store.CreateRecord(command, arguments, Environment.ProcessId,
            current.StartTime.ToUniversalTime().Ticks);
        return new(store, record);
    }

    internal void Publish() => store.Publish(Record);
    internal void CleanupExact() => store.CleanupExact(Record);
}

internal static class DriverLifecycleHandoffRuntime
{
    internal static ConsumedDriverLifecycleHandoff ConsumeProduction(
        DriverLifecycleHandoffCredentials credentials, string command, IReadOnlyList<string> arguments)
    {
        return DriverLifecycleHandoffStore.CreateProduction().Consume(credentials, command, arguments,
            ExactHostProcessLease.Open, IsLifecycleMutexHeld);
    }

    static bool IsLifecycleMutexHeld(string mutexName)
    {
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }
            return !acquired;
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
