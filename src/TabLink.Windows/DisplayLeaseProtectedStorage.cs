namespace TabLink.Windows;

/// <summary>
/// Machine-protected storage for the display ownership protocol.  The UI and
/// its watchdog both run elevated, so no renewable lease, immutable bootstrap,
/// or ownership marker may remain below the unelevated user's LocalAppData.
/// The directory is global because one physical VDD target must have exactly
/// one ownership marker across every Windows user and RDP session.
/// </summary>
internal static class DisplayLeaseProtectedStorage
{
    internal sealed class ManagedWriteCommittedException(string path, Exception innerException)
        : IOException("显示租约状态已经原子提交，但提交后权限复核失败。", innerException)
    {
        internal string CommittedPath { get; } = Path.GetFullPath(path);
    }

    [ThreadStatic] static FileStream? heldMutationLock;
    [ThreadStatic] static int heldMutationDepth;
    internal static string ProgramDataRoot => Path.GetFullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    internal static string TabLinkRoot => Path.Combine(ProgramDataRoot, "TabLink");
    internal static string DisplayLeaseRoot => Path.Combine(TabLinkRoot, "DisplayLeases");
    internal static string LeaseFolder => DisplayLeaseRoot;
    internal static string MutationLockPath => Path.Combine(DisplayLeaseRoot, ".display-lease.lock");

    internal static void EnsureLeaseFolder()
    {
        ValidateProgramDataRoot();
        ProtectedUpdaterStager.EnsureProtectedDirectory(TabLinkRoot, ProgramDataRoot);
        ProtectedUpdaterStager.EnsureProtectedDirectory(DisplayLeaseRoot, TabLinkRoot);
        EnsureProtectedLockFile();
        VerifyLeaseFolder();
    }

    internal static void VerifyLeaseFolder()
    {
        VerifyDirectoryChain();
        ProtectedUpdaterStager.VerifyProtectedFile(MutationLockPath);
    }

    internal static bool IsManagedFile(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), LeaseFolder, StringComparison.OrdinalIgnoreCase);
    }

    internal static void WriteManagedFileAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Path.GetFullPath(path);
        if (!IsManagedFile(full)) throw new InvalidDataException("显示租约文件不在受保护目录中。");
        VerifyDirectoryChain();
        var temporary = Path.Combine(LeaseFolder, ".display-lease-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = ProtectedUpdaterStager.CreateProtectedFile(temporary, LeaseFolder))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            ProtectedUpdaterStager.VerifyProtectedFile(temporary);
            File.Move(temporary, full, overwrite: true);
            try { ProtectedUpdaterStager.VerifyProtectedFile(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Security.SecurityException or InvalidOperationException)
            {
                // Move succeeded and the new bytes are now authoritative. The
                // caller must not treat this as a pre-commit failure and forget
                // an ownership marker that may already be visible to a watcher.
                throw new ManagedWriteCommittedException(full, ex);
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static IDisposable AcquireLock()
    {
        if (heldMutationLock is not null)
        {
            heldMutationDepth++;
            return new MutationLockLease();
        }
        EnsureLeaseFolder();
        VerifyLeaseFolder();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            try
            {
                heldMutationLock = new FileStream(MutationLockPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
                heldMutationDepth = 1;
                return new MutationLockLease();
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    sealed class MutationLockLease : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (heldMutationDepth <= 0 || heldMutationLock is null)
                throw new InvalidOperationException("显示租约保护锁状态无效。");
            heldMutationDepth--;
            if (heldMutationDepth != 0) return;
            heldMutationLock.Dispose();
            heldMutationLock = null;
        }
    }

    internal static void VerifyWatcherFiles(string currentPath, Guid leaseId)
    {
        if (leaseId == Guid.Empty) throw new InvalidDataException("副屏保护会话标识无效。");
        var current = Path.GetFullPath(currentPath);
        if (!string.Equals(Path.GetDirectoryName(current), LeaseFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("副屏保护文件不在受保护目录中。");
        VerifyLeaseFolder();
        ProtectedUpdaterStager.VerifyProtectedFile(current);
        ProtectedUpdaterStager.VerifyProtectedFile(current + ".lease-id");
        ProtectedUpdaterStager.VerifyProtectedFile(current + "." + leaseId.ToString("N") + ".initial.json");
    }

    private static void ValidateProgramDataRoot()
    {
        if (string.IsNullOrWhiteSpace(ProgramDataRoot) ||
            ProgramDataRoot.Equals(Path.GetPathRoot(ProgramDataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ProgramData 路径无效，不能创建显示租约保护目录。");
        WindowsUpdatePathPolicy.VerifySafeNamespaceChain(ProgramDataRoot, "显示租约保护父路径");
    }

    private static void VerifyDirectoryChain()
    {
        ValidateProgramDataRoot();
        ProtectedUpdaterStager.VerifyProtectedDirectory(TabLinkRoot);
        ProtectedUpdaterStager.VerifyProtectedDirectory(DisplayLeaseRoot);
    }

    private static void EnsureProtectedLockFile()
    {
        if (File.Exists(MutationLockPath))
        {
            ProtectedUpdaterStager.VerifyProtectedFile(MutationLockPath);
            return;
        }
        try
        {
            using var created = ProtectedUpdaterStager.CreateProtectedFile(MutationLockPath, DisplayLeaseRoot);
            created.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(MutationLockPath)) { }
        ProtectedUpdaterStager.VerifyProtectedFile(MutationLockPath);
    }

}
