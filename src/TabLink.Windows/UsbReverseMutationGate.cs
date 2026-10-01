namespace TabLink.Windows;

/// <summary>
/// Serializes TabLink-owned ADB reverse mutations across the UI and crash
/// watcher processes and Windows sessions. The protected ProgramData lock file
/// is shared by every elevated TabLink instance and is released by the OS when
/// a process exits, including an abnormal exit.
/// </summary>
internal sealed class UsbReverseMutationGate
{
    readonly string lockPath;
    readonly TimeSpan waitTimeout;
    readonly Action? validateStorage;

    static readonly Lazy<UsbReverseMutationGate> SharedGate = new(() =>
    {
        UsbReverseProtectedStorage.EnsureMutationLockFile();
        return new(UsbReverseProtectedStorage.MutationLockPath, TimeSpan.FromSeconds(20),
            UsbReverseProtectedStorage.VerifyMutationLockFile);
    });
    internal static UsbReverseMutationGate Shared => SharedGate.Value;

    internal UsbReverseMutationGate(string lockPath, TimeSpan waitTimeout, Action? validateStorage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        if (waitTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        this.lockPath = Path.GetFullPath(lockPath);
        this.waitTimeout = waitTimeout;
        this.validateStorage = validateStorage;
    }

    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + waitTimeout;
            FileStream? lockStream = null;
            while (lockStream is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                validateStorage?.Invoke();
                try
                {
                    lockStream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(100)
                        ? remaining : TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
                if (lockStream is null && DateTime.UtcNow >= deadline)
                    throw new TimeoutException("另一个 USB 通道操作尚未完成。");
            }
            await using (lockStream.ConfigureAwait(false))
                return await action(cancellationToken).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    internal async Task RunAsync(Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunAsync(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}
