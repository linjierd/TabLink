namespace TabLink.Windows;

/// <summary>
/// Publishes the whole initial USB route-and-launch transaction before it can
/// yield. Stop drains this slot before snapshotting reverse ownership, so a
/// successful --no-rebind can neither arrive after cleanup nor escape it.
/// </summary>
internal sealed class UsbSessionSetupBarrier
{
    readonly object sync = new();
    Task? active;

    internal bool InFlight { get { lock (sync) return active is not null; } }

    internal Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (active is not null)
                throw new InvalidOperationException("USB initial connection setup is already running.");
            active = completion.Task;
        }
        _ = ExecuteAsync(operation, cancellationToken, completion);
        return completion.Task;
    }

    internal async Task DrainAsync()
    {
        Task? snapshot;
        lock (sync) snapshot = active;
        if (snapshot is not null) await snapshot.ConfigureAwait(false);
    }

    async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await operation(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(active, completion.Task)) active = null;
            }
        }

        if (failure is null) completion.TrySetResult();
        else if (failure is OperationCanceledException cancelled)
            completion.TrySetCanceled(cancelled.CancellationToken);
        else completion.TrySetException(failure);
    }
}
