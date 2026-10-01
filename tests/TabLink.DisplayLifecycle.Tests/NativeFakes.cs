namespace TabLink.Windows;

// This test executable links the real watcher, but never links the real display adapter,
// diagnostics writer, or USB cleanup. An accidental mutation remains entirely in memory.
public sealed record DisplayLease(Guid LeaseId, string DeviceName);
public sealed record DisplayChangeResult(bool Success, bool Changed, string Message);
public sealed record VirtualDisplayInfo(string DeviceName);
internal sealed record DisplayRelocation(VirtualDisplayInfo CurrentDisplay, DisplayLease RememberedLease);
static class VirtualDisplayManager
{
    public static Func<DisplayLease, DisplayChangeResult> OnDetach = _ => throw new Exception("Unexpected fake detach");
    public static DisplayChangeResult Detach(DisplayLease lease) => OnDetach(lease);
    public static DisplayChangeResult Restore(DisplayLease lease) => throw new Exception("Tests must not restore displays");
    internal static DisplayRelocation ResolveLayout(DisplayLease lease) => throw new IOException("No native desktop in pure lifecycle tests");
    internal static bool RememberedLayoutEquals(DisplayLease left, DisplayLease right) => left == right;
    public static DisplayLease CaptureDetachedLease(int width, int height, int refreshRate) => throw new Exception("Tests must not enumerate native displays");
    internal static DisplayLease ReuseRememberedPosition(DisplayLease fresh, DisplayLease remembered) => fresh;
    internal static string GetTargetStorageKey(DisplayLease lease) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.DeviceName)));
}
internal readonly record struct AdbReverseEndpoint(int DevicePort)
{
    internal bool IsValid => DevicePort is >= 49152 and <= 65535;
}
internal sealed record UsbReverseLease(Guid Id, int OwnerPid, long OwnerStartUtcTicks,
    AdbReverseEndpoint Endpoint, string OwnerUserSid = "S-1-5-21-1-2-3-1001", int SchemaVersion = 3)
{
    internal const int CurrentSchemaVersion = 3;
    internal string Serial { get; init; } = "TEST_TABLET_QUEUE";
    internal string Vid { get; init; } = "1234";
    internal string Pid { get; init; } = "5678";
    internal static string CurrentUserSid() => "S-1-5-21-1-2-3-1001";
    internal static Func<UsbReverseLease,CancellationToken,Task<UsbReverseCleanupResult>> OnCleanup =
        (_,_) => throw new Exception("Tests must not use ADB");
    internal static Task<UsbReverseCleanupResult> CleanupAfterOwnerExitAsync(UsbReverseLease receipt, CancellationToken cancellationToken) =>
        OnCleanup(receipt,cancellationToken);
}
static class UsbReverseProtectedStorage
{
    internal static string QueueFolder => Path.Combine(AppContext.BaseDirectory, "protected-queue-not-used");
    internal static IUsbReverseQueueFileSecurity QueueFileSecurity { get; } =
        new TestUsbReverseQueueFileSecurity();
    internal static PendingUsbReverseCleanupQueue OpenQueue() =>
        new(QueueFolder, fileSecurity: QueueFileSecurity);
}

internal sealed class TestUsbReverseQueueFileSecurity : IUsbReverseQueueFileSecurity
{
    readonly object sync = new();
    readonly HashSet<string> created = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> verificationCounts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> unsafePaths = new(StringComparer.OrdinalIgnoreCase);
    bool markNextCreatedUnsafe;

    internal IReadOnlyCollection<string> CreatedPaths
    {
        get { lock (sync) return created.ToArray(); }
    }

    internal IReadOnlyCollection<string> VerifiedPaths
    {
        get { lock (sync) return verified.ToArray(); }
    }

    internal void MarkUnsafe(string path)
    {
        lock (sync) unsafePaths.Add(Path.GetFullPath(path));
    }

    internal int VerificationCount(string path)
    {
        lock (sync) return verificationCounts.GetValueOrDefault(Path.GetFullPath(path));
    }

    internal void MarkNextCreatedUnsafe()
    {
        lock (sync) markNextCreatedUnsafe = true;
    }

    public void VerifyStorage(string folder, string completedFolder)
    {
        VerifyOrdinaryDirectory(folder);
        VerifyOrdinaryDirectory(completedFolder);
    }

    public FileStream CreateProtectedFile(string path, string expectedParent)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(expectedParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("fake protected file escaped its expected parent");
        VerifyOrdinaryDirectory(parent);
        var stream = new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        lock (sync)
        {
            created.Add(full);
            if (markNextCreatedUnsafe)
            {
                unsafePaths.Add(full);
                markNextCreatedUnsafe = false;
            }
        }
        return stream;
    }

    public void VerifyProtectedFile(string path)
    {
        var full = Path.GetFullPath(path);
        lock (sync)
        {
            if (unsafePaths.Contains(full))
                throw new UnauthorizedAccessException("injected unsafe queue ACL");
            verified.Add(full);
            verificationCounts[full] = verificationCounts.GetValueOrDefault(full) + 1;
        }
        if (!File.Exists(full)) throw new FileNotFoundException("fake protected file missing", full);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("fake protected queue entry is not an ordinary file");
    }

    static void VerifyOrdinaryDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("fake protected queue directory is a reparse point");
    }
}
static class DisplayLeaseProtectedStorage
{
    internal sealed class ManagedWriteCommittedException(string path, Exception innerException)
        : IOException("fake protected write committed before verification failed", innerException)
    {
        internal string CommittedPath { get; } = Path.GetFullPath(path);
    }

    [ThreadStatic] static FileStream? heldLock;
    [ThreadStatic] static int lockDepth;
    internal static Func<string, Exception?>? AfterCommitFailure { get; set; }
    internal static string LeaseFolder { get; set; } = Path.Combine(AppContext.BaseDirectory,
        "display-lease-protected-tests");
    internal static void EnsureLeaseFolder() => Directory.CreateDirectory(LeaseFolder);
    internal static void VerifyLeaseFolder()
    {
        if (!Directory.Exists(LeaseFolder)) throw new DirectoryNotFoundException(LeaseFolder);
    }
    internal static bool IsManagedFile(string path) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(LeaseFolder), StringComparison.OrdinalIgnoreCase);
    internal static void WriteManagedFileAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        VerifyLeaseFolder();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            var postCommitFailure = AfterCommitFailure?.Invoke(Path.GetFullPath(path));
            if (postCommitFailure is not null)
                throw new ManagedWriteCommittedException(path, postCommitFailure);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
    internal static IDisposable AcquireLock()
    {
        if (heldLock is not null)
        {
            lockDepth++;
            return new LockLease();
        }
        EnsureLeaseFolder();
        var lockPath = Path.Combine(LeaseFolder, ".display-lease.lock");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            try
            {
                heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                lockDepth = 1;
                return new LockLease();
            }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(10); }
        }
    }
    sealed class LockLease : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lockDepth--;
            if (lockDepth != 0) return;
            heldLock!.Dispose();
            heldLock = null;
        }
    }
    internal static void VerifyWatcherFiles(string currentPath, Guid leaseId)
    {
        if (!File.Exists(currentPath) || !File.Exists(currentPath + ".lease-id") ||
            !File.Exists(currentPath + "." + leaseId.ToString("N") + ".initial.json"))
            throw new InvalidDataException("fake protected watcher files are incomplete");
    }
}
internal enum UsbReverseCleanupStatus { Removed, AlreadyAbsent, DeviceUnavailable, MappingChanged, Failed }
internal sealed record UsbReverseCleanupResult(UsbReverseCleanupStatus Status, string Message)
{
    internal bool Complete => Status is UsbReverseCleanupStatus.Removed or UsbReverseCleanupStatus.AlreadyAbsent;
}
static class Diagnostics
{
    internal static Action<string, object>? OnSave;
    public static void Save(string name, object value) => OnSave?.Invoke(name, value);
}
static class UsbReverseCleanupCoordinator
{
    internal static UsbReverseCleanupCoordinatorFake Shared { get; } = new();
    internal sealed class UsbReverseCleanupCoordinatorFake
    {
        internal void ProcessPendingBlocking(string reason) { }
    }
}
