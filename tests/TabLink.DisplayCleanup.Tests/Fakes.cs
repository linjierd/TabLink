using System.Drawing;

namespace TabLink.Windows;

// This executable links ONLY the production allocator. These deliberately
// in-memory display and guard doubles cannot enumerate/mutate a real display,
// write lease files, start watchers or require elevation.
internal interface ISingleDisplayDriverController
{
    Task EnsureSingleAsync(TabLink.Core.TabletDisplayProfile profile, CancellationToken cancellationToken);
    Task RemoveOwnedAsync(CancellationToken cancellationToken);
}

internal sealed class FakeSingleDisplayDriverController : ISingleDisplayDriverController
{
    internal int EnsureCalls { get; private set; }
    internal int RemoveCalls { get; private set; }
    internal int DiscoveryCallsAtEnsure { get; private set; } = -1;
    internal bool DevicePresent { get; private set; }
    internal bool FailEnsure { get; set; }
    internal int RemoveFailuresRemaining { get; set; }
    internal Action? AfterEnsure { get; set; }
    internal List<string> Events { get; } = [];

    public Task EnsureSingleAsync(TabLink.Core.TabletDisplayProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCalls++;
        DiscoveryCallsAtEnsure = VirtualDisplayManager.DiscoveryCalls;
        Events.Add("ensure");
        if (FailEnsure) throw new IOException("Injected driver preparation failure.");
        DevicePresent = true;
        AfterEnsure?.Invoke();
        return Task.CompletedTask;
    }

    public Task RemoveOwnedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveCalls++;
        Events.Add("remove");
        if (VirtualDisplayManager.Active.Count != 0)
            throw new InvalidOperationException("Driver removal ran before display detach.");
        if (RemoveFailuresRemaining-- > 0) throw new IOException("Injected driver removal failure.");
        DevicePresent = false;
        return Task.CompletedTask;
    }
}

// DisplaySessionAllocator's production singleton references the concrete
// lifecycle at type initialization. The harness never uses that singleton,
// but provides this inert name so only the allocator source needs to be linked.
internal sealed class SingleDisplayDriverLifecycle : ISingleDisplayDriverController
{
    internal static SingleDisplayDriverLifecycle Shared { get; } = new();
    public Task EnsureSingleAsync(TabLink.Core.TabletDisplayProfile profile, CancellationToken cancellationToken) =>
        Task.CompletedTask;
    public Task RemoveOwnedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed record DisplayDesktopState(int Width, int Height, uint Frequency);
internal sealed record DisplayLease(Guid LeaseId, string DeviceName, string TargetIdentity,
    string SourceIdentity, DisplayDesktopState OriginalMode);
internal sealed record DisplayChangeResult(bool Success, bool Changed, string Message);
internal sealed record DisplayMode(int Width, int Height, int RefreshRate);
internal sealed record VirtualDisplayInfo(string DeviceName, string FriendlyName, bool IsPrimary,
    Rectangle Bounds, bool IsTabLinkCompatible);
internal sealed record VirtualDisplaySource(string SourceKey, string DeviceName, bool IsActive,
    bool IsCloned, bool IsInUse);
internal sealed record VirtualDisplayTarget(string TargetKey, bool IsAvailable, bool IsActive,
    bool IsPrimary, IReadOnlyList<VirtualDisplaySource> Sources);

internal static class VirtualDisplayManager
{
    internal sealed record ActiveTarget(string Source, DisplayDesktopState Mode);
    static readonly Dictionary<string, ActiveTarget> active = new();
    internal static int DiscoveryCalls { get; private set; }
    internal static int MutationCalls { get; private set; }
    internal static Func<DisplayLease, DisplayChangeResult?>? SetModeFault { get; set; }
    internal static IReadOnlyDictionary<string, ActiveTarget> Active => active;

    internal static void Reset()
    {
        active.Clear(); DiscoveryCalls = 0; MutationCalls = 0; SetModeFault = null;
        SessionGuard.Reset();
    }

    internal static IReadOnlyList<VirtualDisplayTarget> GetTargets()
    {
        DiscoveryCalls++;
        var usedSources = active.Values.Select(v => v.Source).ToHashSet();
        return Enumerable.Range(1, 4).Select(t =>
        {
            var target = "target-" + t;
            active.TryGetValue(target, out var current);
            return new VirtualDisplayTarget(target, true, current is not null, false,
                Enumerable.Range(1, 4).Select(s =>
                {
                    var source = "source-" + s;
                    return new VirtualDisplaySource(source, "DISPLAY" + s,
                        current?.Source == source, false, usedSources.Contains(source));
                }).ToArray());
        }).ToArray();
    }

    internal static string TargetKey(DisplayLease lease) => lease.TargetIdentity;
    internal static string SourceKey(DisplayLease lease) => lease.SourceIdentity;
    internal static IReadOnlyList<VirtualDisplayInfo> GetDisplays() => active.Select(pair =>
        new VirtualDisplayInfo(DeviceName(pair.Value.Source), pair.Key, false,
            new Rectangle(0, 0, pair.Value.Mode.Width, pair.Value.Mode.Height), true)).ToArray();
    internal static DisplayLease CaptureLease(VirtualDisplayInfo display)
    {
        var pair = active.Single(p => DeviceName(p.Value.Source) == display.DeviceName);
        return new(Guid.NewGuid(), display.DeviceName, pair.Key, pair.Value.Source, pair.Value.Mode);
    }
    internal static DisplayLease CaptureDetachedLease(VirtualDisplayTarget target, VirtualDisplaySource source,
        int width, int height, int refreshRate) =>
        new(Guid.NewGuid(), source.DeviceName, target.TargetKey, source.SourceKey, new(width, height, (uint)refreshRate));
    internal static IReadOnlyList<DisplayMode> GetSupportedModes(DisplayLease lease) => [new(1200, 1920, 90)];
    internal static DisplayChangeResult Restore(DisplayLease lease)
    {
        MutationCalls++;
        if (active.Any(p => p.Key != lease.TargetIdentity && p.Value.Source == lease.SourceIdentity))
            throw new InvalidOperationException("Allocator attempted to reuse another session's source.");
        active[lease.TargetIdentity] = new(lease.SourceIdentity, lease.OriginalMode);
        return new(true, true, "restored");
    }
    internal static DisplayChangeResult SetMode(DisplayLease lease, int width, int height, int refreshRate)
    {
        MutationCalls++;
        var fault = SetModeFault?.Invoke(lease);
        if (fault is not null) return fault;
        active[lease.TargetIdentity] = new(lease.SourceIdentity, new(width, height, (uint)refreshRate));
        return new(true, true, "mode set");
    }
    internal static DisplayChangeResult Detach(DisplayLease lease)
    {
        MutationCalls++;
        active.Remove(lease.TargetIdentity);
        return new(true, true, "detached");
    }
    // Simulate a changing topology while the old ownership marker is still
    // unreadable/unretired. The allocator must protect both target AND source.
    internal static void MakeInactiveWithoutRetiringGuard(DisplayLease lease) => active.Remove(lease.TargetIdentity);
    static string DeviceName(string source) => "DISPLAY" + source.Split('-')[1];
}

internal sealed class SessionGuard : IDisposable
{
    static readonly Dictionary<string, Guid> markers = new();
    internal static HashSet<string> AlwaysFailTargets { get; } = [];
    internal static int OwnedDisposeAttempts { get; private set; }
    internal static int Created { get; private set; }
    internal int FailuresRemaining { get; set; }
    internal bool AlwaysFail { get; set; }
    internal int DisposeCalls { get; private set; }
    internal Action? BeforeOwnedDispose { get; set; }
    internal DisplayLease Lease { get; }
    bool ended;

    internal SessionGuard(DisplayLease lease, bool requireUnowned = false)
    {
        Lease = lease;
        if (requireUnowned && markers.ContainsKey(lease.TargetIdentity))
            throw new IOException("Guard ownership has not been released.");
        markers[lease.TargetIdentity] = lease.LeaseId;
        Created++;
    }
    internal static void Reset()
    { markers.Clear(); AlwaysFailTargets.Clear(); OwnedDisposeAttempts = 0; Created = 0; }
    internal static DisplayLease ReuseRememberedPosition(DisplayLease lease) => lease;
    internal void CompleteWatcherCleanup()
    {
        if (markers.GetValueOrDefault(Lease.TargetIdentity) != Lease.LeaseId) return;
        VirtualDisplayManager.Detach(Lease);
        markers.Remove(Lease.TargetIdentity);
    }
    internal void SupersedeOwnership() => markers[Lease.TargetIdentity] = Guid.NewGuid();
    internal static void RetireExternalOwner(string target) => markers.Remove(target);
    public void Dispose()
    {
        DisposeCalls++;
        if (ended) return;
        if (markers.GetValueOrDefault(Lease.TargetIdentity) != Lease.LeaseId) { ended = true; return; }
        BeforeOwnedDispose?.Invoke();
        OwnedDisposeAttempts++;
        if (AlwaysFail || AlwaysFailTargets.Contains(Lease.TargetIdentity) || FailuresRemaining-- > 0)
            throw new IOException("Injected temporary cleanup failure.");
        VirtualDisplayManager.Detach(Lease);
        markers.Remove(Lease.TargetIdentity);
        ended = true;
    }
}
