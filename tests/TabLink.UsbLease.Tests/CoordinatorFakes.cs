namespace TabLink.Windows;

static class SessionGuard
{
    internal static string PendingReverseFolder => Path.Combine(AppContext.BaseDirectory,
        "test-artifacts", "TabLink-UsbLease-Shared-NotUsed");
}

static class Diagnostics
{
    internal static void Save(string name, object value) { }
}

static class UsbReverseProtectedStorage
{
    internal static string MutationLockPath => Path.Combine(AppContext.BaseDirectory,
        "test-artifacts", "TabLink-UsbLease-Mutation-NotUsed.lock");
    internal static IUsbReverseQueueFileSecurity QueueFileSecurity { get; } =
        new TestUsbReverseQueueFileSecurity();
    internal static PendingUsbReverseCleanupQueue OpenQueue() =>
        new(SessionGuard.PendingReverseFolder, fileSecurity: QueueFileSecurity);
    internal static void EnsureMutationLockFile() { }
    internal static void VerifyMutationLockFile() { }
}

internal sealed class TestUsbReverseQueueFileSecurity : IUsbReverseQueueFileSecurity
{
    public void VerifyStorage(string folder, string completedFolder)
    {
        VerifyDirectory(folder);
        VerifyDirectory(completedFolder);
    }

    public FileStream CreateProtectedFile(string path, string expectedParent)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(expectedParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("fake protected file escaped its expected parent");
        VerifyDirectory(parent);
        return new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
    }

    public void VerifyProtectedFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("fake protected file missing", full);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("fake protected queue entry is not an ordinary file");
    }

    static void VerifyDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("fake protected queue directory is a reparse point");
    }
}

static class TrustedBundledAdb
{
    internal static int StageCalls { get; private set; }
    internal static string? StageAndGetVerifiedPath()
    {
        StageCalls++;
        return null;
    }
    internal static void Reset() => StageCalls = 0;
}
