namespace TabLink.Windows;

internal enum ConnectionStartKind
{
    NativeNetwork,
    AdbCompatibility,
    Browser,
    AdditionalNative
}

/// <summary>
/// Process-local, app-wide gate that permits only one connection start to own
/// shared preparation state at a time. It performs no UI, device or network work.
/// </summary>
internal sealed class ConnectionStartCoordinator
{
    readonly object sync = new();
    ConnectionStartLease? active;

    internal bool IsStarting
    {
        get
        {
            lock (sync) return active is not null;
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (sync) return active?.Released ?? Task.CompletedTask;
    }

    internal void InvalidateActive()
    {
        lock (sync) active?.Invalidate();
    }

    internal ConnectionStartLease Acquire(ConnectionStartKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (sync)
        {
            if (active is not null)
                throw new InvalidOperationException($"A {active.Kind} connection start already owns the app-wide start lease.");
            return active = new ConnectionStartLease(this, kind);
        }
    }

    internal bool IsCurrent(ConnectionStartLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (sync)
            return ReferenceEquals(active, lease) && !lease.IsDisposed && !lease.IsInvalidated;
    }

    internal void ThrowIfNotCurrent(ConnectionStartLease lease)
    {
        if (!IsCurrent(lease))
            throw new OperationCanceledException("The connection start lease is no longer current.");
    }

    internal void Release(ConnectionStartLease lease)
    {
        lock (sync)
        {
            // Reference identity is the authority. A disposed predecessor must
            // never clear a later lease, even if it is released again or late.
            if (ReferenceEquals(active, lease)) active = null;
            // Complete while holding the same lock that publishes the idle
            // state. WaitForIdleAsync can therefore never observe idle before
            // the releasing action's drain task is complete.
            lease.SignalReleased();
        }
    }
}

internal sealed class ConnectionStartLease : IDisposable
{
    readonly ConnectionStartCoordinator owner;
    readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int disposed;
    int invalidated;

    internal ConnectionStartLease(ConnectionStartCoordinator owner, ConnectionStartKind kind)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Kind = kind;
    }

    internal ConnectionStartKind Kind { get; }
    internal bool IsCurrent => owner.IsCurrent(this);
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
    internal bool IsInvalidated => Volatile.Read(ref invalidated) != 0;
    internal Task Released => released.Task;

    internal void ThrowIfNotCurrent() => owner.ThrowIfNotCurrent(this);
    internal void Invalidate()=>Interlocked.Exchange(ref invalidated,1);
    internal void SignalReleased()=>released.TrySetResult();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.Release(this);
    }
}
