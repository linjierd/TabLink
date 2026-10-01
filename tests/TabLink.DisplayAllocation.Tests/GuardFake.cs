namespace TabLink.Windows;

// Tests exercise snapshot selection only. Accidental allocation cannot start a watcher.
internal interface ISingleDisplayDriverController
{
    Task EnsureSingleAsync(TabLink.Core.TabletDisplayProfile profile, CancellationToken cancellationToken);
    Task RemoveOwnedAsync(CancellationToken cancellationToken);
}

internal sealed class SingleDisplayDriverLifecycle : ISingleDisplayDriverController
{
    internal static SingleDisplayDriverLifecycle Shared { get; } = new();
    public Task EnsureSingleAsync(TabLink.Core.TabletDisplayProfile profile, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Snapshot tests must not prepare a native driver.");
    public Task RemoveOwnedAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Snapshot tests must not remove a native driver.");
}

internal sealed class SessionGuard : IDisposable
{
    internal sealed class GuardStartupFailureException(SessionGuard recoveryGuard)
        : IOException("Injected startup failure")
    {
        internal SessionGuard RecoveryGuard { get; } = recoveryGuard;
    }
    internal SessionGuard(DisplayLease lease, bool requireUnowned = false) => throw new InvalidOperationException("Tests must not create a display guard.");
    internal DisplayLease Lease => throw new InvalidOperationException("No native allocation in snapshot tests.");
    public void Dispose() => throw new InvalidOperationException("No native allocation in snapshot tests.");
    internal static DisplayLease ReuseRememberedPosition(DisplayLease lease) => lease;
}
