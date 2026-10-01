using TabLink.Core;

namespace TabLink.Windows;

internal sealed class DisplaySessionReservation : IAsyncDisposable
{
    readonly DisplaySessionAllocator owner;
    internal DisplaySessionReservation(DisplaySessionAllocator owner, Guid sessionId, VirtualDisplayInfo display, SessionGuard guard)
    { this.owner = owner; SessionId = sessionId; CurrentDisplay = display; Guard = guard; }
    internal Guid SessionId { get; }
    internal VirtualDisplayInfo CurrentDisplay { get; }
    internal SessionGuard Guard { get; }
    internal DisplayLease Lease => Guard.Lease;
    internal string TargetKey => VirtualDisplayManager.TargetKey(Lease);
    public ValueTask DisposeAsync() => new(owner.ReleaseAsync(this));
}

// All native preparation is serialized; independent capture/transport runs outside this gate.
// Per-target guard files provide the separate cross-process crash protection.
internal sealed class DisplaySessionAllocator
{
    internal static DisplaySessionAllocator Shared { get; } = new(SingleDisplayDriverLifecycle.Shared);
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Dictionary<Guid, DisplaySessionReservation> Reservations = new();
    // Cleanup belongs to the allocator, not to a UI/transport disposal task that
    // may already have faulted or dropped its reservation reference.
    static readonly Dictionary<Guid, PendingDisplayCleanup> PendingCleanup = new();
    readonly ISingleDisplayDriverController? driverController;
    bool driverRemovalPending;

    // A null controller is used only by memory-only allocator harnesses. The
    // production singleton always supplies the real owned-device lifecycle.
    internal DisplaySessionAllocator(ISingleDisplayDriverController? driverController = null) =>
        this.driverController = driverController;

    sealed class PendingDisplayCleanup(Guid sessionId, SessionGuard guard, DisplaySessionReservation? reservation)
    {
        internal Guid SessionId { get; } = sessionId;
        internal SessionGuard Guard { get; } = guard;
        internal DisplaySessionReservation? Reservation { get; } = reservation;
        internal Exception? LastError { get; set; }
    }

    internal async Task<DisplaySessionReservation> AcquireAsync(Guid sessionId, TabletDisplayProfile profile,
        CancellationToken cancellationToken = default, string? preferredTargetKey = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("设备会话标识无效。", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(profile);
        await Gate.WaitAsync(cancellationToken);
        SessionGuard? guard = null;
        var startupRecoveryGuards = new List<SessionGuard>();
        var driverPrepared = false;
        SessionGuard CreateGuard(DisplayLease ownedLease, bool requireUnowned = false)
        {
            try { return new SessionGuard(ownedLease, requireUnowned); }
            catch (SessionGuard.GuardStartupFailureException ex)
            {
                startupRecoveryGuards.Add(ex.RecoveryGuard);
                throw;
            }
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pendingReservations = PendingCleanup.Values.Where(p => p.Reservation is not null)
                .Select(p => p.Reservation!).ToHashSet(ReferenceEqualityComparer.Instance);
            if (Reservations.Values.Any(r => !pendingReservations.Contains(r)))
                throw new InvalidOperationException("TabLink 只允许一块虚拟扩展屏。请先断开当前设备，再连接另一台设备。");
            var hadPendingCleanup = PendingCleanup.Count != 0;
            RetryPendingCleanup(cancellationToken);
            if (PendingCleanup.Count != 0)
                throw new IOException("上一块副屏仍在等待安全回收。TabLink 不会启用第二块虚拟屏；请稍后重试。",
                    PendingCleanup.Values.First().LastError);
            if (hadPendingCleanup || driverRemovalPending)
                await RemoveDriverAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (driverController is not null)
            {
                await driverController.EnsureSingleAsync(profile, cancellationToken).ConfigureAwait(false);
                driverPrepared = true;
                // Observe cancellation only after the helper transaction is
                // fully complete, then run the owned removal path below.
                cancellationToken.ThrowIfCancellationRequested();
            }
            var ownedGuards = Reservations.Values.Select(r => r.Guard)
                .Concat(PendingCleanup.Values.Select(p => p.Guard)).ToArray();
            var selected = SelectTarget(VirtualDisplayManager.GetTargets(),
                ownedGuards.Select(g => VirtualDisplayManager.TargetKey(g.Lease)),
                ownedGuards.Select(g => VirtualDisplayManager.SourceKey(g.Lease)), preferredTargetKey);
            var target = selected.Target;
            var source = selected.Source;
            cancellationToken.ThrowIfCancellationRequested();
            DisplayLease lease;
            if (target.IsActive)
            {
                var current = VirtualDisplayManager.GetDisplays().Single(d => d.DeviceName.Equals(source.DeviceName, StringComparison.OrdinalIgnoreCase) && d.IsTabLinkCompatible);
                lease = VirtualDisplayManager.CaptureLease(current);
                if (VirtualDisplayManager.TargetKey(lease) != target.TargetKey || VirtualDisplayManager.SourceKey(lease) != source.SourceKey)
                    throw new IOException("分配期间虚拟屏身份发生变化，未接管该屏。");
            }
            else lease = CaptureInactive(target, source, profile);
            if (!VirtualDisplayManager.GetSupportedModes(lease).Any(m => m.Width == profile.Width && m.Height == profile.Height && m.RefreshRate == profile.RequestedRefreshRate))
                throw new IOException("此设备需要的显示模式尚未配置。请停止所有副屏连接后配置驱动模式。");
            cancellationToken.ThrowIfCancellationRequested();
            guard = CreateGuard(lease, requireUnowned: true);

            // A differently sized active output could overlap another device's desktop.
            // Detach this target only, then rebuild its placement against the fresh layout.
            if (target.IsActive && (lease.OriginalMode.Width != profile.Width || lease.OriginalMode.Height != profile.Height || lease.OriginalMode.Frequency != profile.RequestedRefreshRate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(VirtualDisplayManager.Detach(lease));
                var refreshed = VirtualDisplayManager.GetTargets().Single(t => t.TargetKey == target.TargetKey);
                var refreshedSource = refreshed.Sources.Single(s => s.SourceKey == source.SourceKey && !s.IsInUse);
                lease = CaptureInactive(refreshed, refreshedSource, profile);
                var previous = guard;
                guard = CreateGuard(lease);
                previous.Dispose(); // The new marker supersedes only this same target.
            }
            cancellationToken.ThrowIfCancellationRequested();
            Require(VirtualDisplayManager.Restore(lease));
            cancellationToken.ThrowIfCancellationRequested();
            Require(VirtualDisplayManager.SetMode(lease, profile.Width, profile.Height, profile.RequestedRefreshRate));
            cancellationToken.ThrowIfCancellationRequested();
            var display = VirtualDisplayManager.GetDisplays().Single(d => d.IsTabLinkCompatible && d.DeviceName.Equals(lease.DeviceName, StringComparison.OrdinalIgnoreCase));
            var finalLease = VirtualDisplayManager.CaptureLease(display);
            if (VirtualDisplayManager.TargetKey(finalLease) != target.TargetKey || VirtualDisplayManager.SourceKey(finalLease) != source.SourceKey)
                throw new IOException("副屏恢复后的身份验证失败，停止本次分配。");
            var provisional = guard;
            guard = CreateGuard(finalLease);
            provisional.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            var reservation = new DisplaySessionReservation(this, sessionId, display, guard);
            Reservations.Add(sessionId, reservation);
            guard = null;
            return reservation;
        }
        catch (Exception original)
        {
            Exception? cleanupFailure = null;
            var cleanupCandidates = startupRecoveryGuards
                .Concat(guard is null ? Enumerable.Empty<SessionGuard>() : [guard])
                .Distinct().ToArray();
            foreach (var cleanupGuard in cleanupCandidates)
            {
                // Preparation can fail before a reservation is returned. Keep
                // that guard too if rollback needs another attempt.
                try { CompleteCleanup(QueueCleanup(sessionId, cleanupGuard, null)); }
                catch (Exception cleanupError)
                {
                    cleanupFailure = cleanupFailure is null ? cleanupError
                        : new AggregateException(cleanupFailure, cleanupError);
                }
            }
            if (driverPrepared && cleanupFailure is null)
            {
                try { await RemoveDriverAsync().ConfigureAwait(false); }
                catch (Exception driverError) { cleanupFailure = driverError; }
            }
            if (cleanupFailure is not null)
                throw new AggregateException("副屏分配失败，且本次虚拟显示设备的回收需要检查。", original, cleanupFailure);
            throw;
        }
        finally { Gate.Release(); }
    }

    internal async Task ReleaseAsync(DisplaySessionReservation reservation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (!Reservations.TryGetValue(reservation.SessionId, out var current) || !ReferenceEquals(current, reservation))
            {
                if (Reservations.Count == 0 && PendingCleanup.Count == 0 && driverRemovalPending)
                    await RemoveDriverAsync().ConfigureAwait(false);
                return;
            }
            CompleteCleanup(QueueCleanup(reservation.SessionId, reservation.Guard, reservation));
            // Guard disposal detaches the exact lease and retires its marker.
            // Only then may the helper remove the exact receipted device node.
            if (Reservations.Count == 0 && PendingCleanup.Count == 0)
                await RemoveDriverAsync().ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    // Callers hold Gate throughout ownership checks and each guarded cleanup.
    static PendingDisplayCleanup QueueCleanup(Guid sessionId, SessionGuard guard, DisplaySessionReservation? reservation)
    {
        if (!PendingCleanup.TryGetValue(guard.Lease.LeaseId, out var cleanup))
        {
            cleanup = new PendingDisplayCleanup(sessionId, guard, reservation);
            PendingCleanup.Add(guard.Lease.LeaseId, cleanup);
        }
        return cleanup;
    }

    static void CompleteCleanup(PendingDisplayCleanup cleanup)
    {
        try { cleanup.Guard.Dispose(); }
        catch (Exception ex) { cleanup.LastError = ex; throw; }
        // Dispose succeeds only after retiring this owner, or after observing
        // a retired/superseded ownership marker under the cross-process lock.
        if (cleanup.Reservation is { } released &&
            Reservations.TryGetValue(cleanup.SessionId, out var current) && ReferenceEquals(current, released))
            Reservations.Remove(cleanup.SessionId);
        PendingCleanup.Remove(cleanup.Guard.Lease.LeaseId);
    }

    static void RetryPendingCleanup(CancellationToken cancellationToken)
    {
        foreach (var cleanup in PendingCleanup.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { CompleteCleanup(cleanup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or
                InvalidOperationException or System.Text.Json.JsonException)
            {
                // Keep target AND source reserved. A different free output may
                // still be allocated without touching any active neighbor.
            }
        }
    }

    async Task RemoveDriverAsync()
    {
        if (driverController is null) { driverRemovalPending = false; return; }
        try
        {
            await driverController.RemoveOwnedAsync(CancellationToken.None).ConfigureAwait(false);
            driverRemovalPending = false;
        }
        catch
        {
            driverRemovalPending = true;
            throw;
        }
    }

    static DisplayLease CaptureInactive(VirtualDisplayTarget target, VirtualDisplaySource source, TabletDisplayProfile profile) =>
        SessionGuard.ReuseRememberedPosition(VirtualDisplayManager.CaptureDetachedLease(target, source, profile.Width, profile.Height, profile.RequestedRefreshRate));

    static void Require(DisplayChangeResult result)
    { if (!result.Success) throw new IOException(result.Message); }

    internal static (VirtualDisplayTarget Target, VirtualDisplaySource Source) SelectTarget(
        IReadOnlyList<VirtualDisplayTarget> targets, IEnumerable<string> reservedTargets,
        IEnumerable<string> reservedSources, string? preferredTargetKey = null)
    {
        var targetKeys = reservedTargets.ToHashSet(StringComparer.Ordinal);
        var sourceKeys = reservedSources.ToHashSet(StringComparer.Ordinal);
        foreach (var target in targets.OrderBy(t => t.IsActive ? 1 : 0).ThenBy(t => t.TargetKey, StringComparer.Ordinal))
        {
            if (!target.IsAvailable || target.IsPrimary || targetKeys.Contains(target.TargetKey) ||
                (preferredTargetKey is not null && preferredTargetKey != target.TargetKey)) continue;
            if (target.IsActive && target.Sources.Count(s => s.IsActive) != 1) continue;
            var sources = target.Sources.Where(s => !s.IsCloned && !sourceKeys.Contains(s.SourceKey) &&
                !string.IsNullOrWhiteSpace(s.DeviceName) && (target.IsActive ? s.IsActive : !s.IsInUse));
            var source = sources.OrderBy(s => s.SourceKey, StringComparer.Ordinal).FirstOrDefault();
            if (source is not null) return (target, source);
        }
        throw new IOException(preferredTargetKey is null
            ? "唯一的虚拟显示目标不可用。请先停止当前连接，再重新连接设备。"
            : "此设备原来的虚拟显示目标目前不可用，未改用其他设备的屏幕。");
    }
}
