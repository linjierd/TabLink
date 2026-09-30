namespace TabLink.Updater;

internal sealed record UpdaterArguments(
    string PackagePath,
    long PackageSize,
    string PackageSha256,
    string ManifestEnvelopePath,
    string ReleaseId,
    int Build,
    string InstallDirectory,
    string TargetExecutable,
    int WaitPid,
    long WaitStartUtcTicks,
    string Version,
    string TransactionDirectory,
    string ReadySignalPath,
    string HandshakeNonce,
    string ProtectedTransactionId)
{
    public static UpdaterArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("更新程序参数无效。");
        }
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("缺少更新参数：" + name);
        var known = new[] { "--package", "--size", "--sha256", "--manifest-envelope", "--release-id", "--build", "--install-dir", "--target-exe", "--wait-pid", "--wait-start-utc-ticks", "--version", "--transaction-dir", "--ready-signal", "--handshake-nonce", "--protected-transaction-id" };
        if (values.Keys.Any(key => !known.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("更新程序包含未知参数。");
        if (!long.TryParse(Required("--size"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0)
            throw new ArgumentException("更新包大小无效。");
        if (!int.TryParse(Required("--wait-pid"), out var pid) || pid <= 0 || !long.TryParse(Required("--wait-start-utc-ticks"), out var ticks) || ticks <= 0)
            throw new ArgumentException("等待进程标识无效。");
        if (!int.TryParse(Required("--build"), out var build) || build < 1) throw new ArgumentException("更新构建号无效。");
        var hash = Required("--sha256").ToUpperInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("更新包 SHA-256 无效。");
        var install = Path.GetFullPath(Required("--install-dir")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Required("--target-exe");
        if (target != Path.GetFileName(target) || !target.Equals("TabLink.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("更新目标程序无效。");
        var handshakeNonce = TabLink.Windows.UpdateSignalSecurity.NormalizeNonce(Required("--handshake-nonce"), "--handshake-nonce");
        var protectedTransactionId = Required("--protected-transaction-id").ToLowerInvariant();
        if (protectedTransactionId.Length != 32 || !protectedTransactionId.All(Uri.IsHexDigit)) throw new ArgumentException("受保护更新事务标识无效。");
        return new(Path.GetFullPath(Required("--package")), size, hash, Path.GetFullPath(Required("--manifest-envelope")), Required("--release-id"), build, install, target, pid, ticks, Required("--version"), Path.GetFullPath(Required("--transaction-dir")), Path.GetFullPath(Required("--ready-signal")), handshakeNonce, protectedTransactionId);
    }
}
