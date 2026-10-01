using System.Net;
using TabLink.Windows;

var assertions = 0;
var scenarios = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    assertions++;
}

void Run(string name, Action body)
{
    body();
    scenarios++;
    Console.WriteLine("PASS " + name);
}

NetworkInterfaceChoice Route(string address, string id = "WIFI-001", string alias = "Wi-Fi",
    int prefix = 24, int index = 7, NetworkInterfaceKind kind = NetworkInterfaceKind.WiFi,
    string? serial = null) =>
    new(IPAddress.Parse(address), alias, prefix, kind, serial, id, index);

TrustedNetworkRouteProbe Probe(TrustedNetworkRouteMonitor monitor, TrustedNetworkRouteGeneration generation) =>
    monitor.BeginProbe(generation) ?? throw new InvalidOperationException("Expected a probe token.");

var epoch = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

Run("all live modes share exact firewall binding equality", () =>
{
    var selected=Route("192.168.5.20","WIFI-CASE","Wi-Fi",24,7);
    var casing=Route("192.168.5.20","wifi-case","Wi-Fi",24,7);
    var renamed=Route("192.168.5.20","WIFI-CASE","Wi-Fi 2",24,7);
    var reindexed=Route("192.168.5.20","WIFI-CASE","Wi-Fi",24,8);
    Check(selected.HasSameBinding(casing),
        "interface identity comparison is case-insensitive while retaining one exact binding");
    Check(!selected.HasSameBinding(renamed)&&!selected.HasSameBinding(reindexed),
        "alias or interface-index changes invalidate browser and native session firewall bindings");
});

Run("three successful misses spanning eight seconds produce one recovery", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var original = Route("192.168.10.20");
    var replacement = Route("192.168.10.21");
    var generation = monitor.Start(original);

    var first = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    var second = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(4), [replacement], false);
    var third = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(8), [replacement], false);

    Check(first.State == TrustedNetworkRouteProbeState.Missing && first.ConsecutiveMissing == 1,
        "first unique replacement is evidence, not recovery");
    Check(second.State == TrustedNetworkRouteProbeState.Missing && second.ConsecutiveMissing == 2,
        "second unique replacement remains below the count threshold");
    Check(third.State == TrustedNetworkRouteProbeState.RecoveryReady && third.ConsecutiveMissing == 3 &&
          third.MissingDuration == TimeSpan.FromSeconds(8),
        "third observation at eight seconds crosses both thresholds");
    Check(third.Recovery is { Original: var recoveredOriginal, Replacement: var recoveredReplacement } &&
          recoveredOriginal == original && recoveredReplacement == replacement,
        "recovery carries the exact original and replacement routes");
    Check(monitor.BeginProbe(generation) is null, "pending recovery suppresses additional probes");

    var recovery = third.Recovery!;
    Check(!monitor.TryClaimRecovery(recovery with { }),
        "an equal-looking recovery object cannot forge ownership of the emitted request");
    var claims = 0;
    Parallel.Invoke(
        () => { if (monitor.TryClaimRecovery(recovery)) Interlocked.Increment(ref claims); },
        () => { if (monitor.TryClaimRecovery(recovery)) Interlocked.Increment(ref claims); });
    Check(claims == 1, "a recovery token can be claimed exactly once under concurrency");
    Check(!monitor.IsCurrent(generation) && monitor.BeginProbe(generation) is null,
        "claiming recovery invalidates its generation before external work");
});

Run("probe count alone cannot bypass the eight second floor", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("10.0.0.10"));
    var replacement = Route("10.0.0.11");

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(1), [replacement], false);
    var third = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(2), [replacement], false);
    var fourth = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(8), [replacement], false);

    Check(third.State == TrustedNetworkRouteProbeState.Missing && third.ConsecutiveMissing == 3 && third.Recovery is null,
        "three fast misses do not recover");
    Check(fourth.State == TrustedNetworkRouteProbeState.RecoveryReady && fourth.ConsecutiveMissing == 4,
        "later consistent evidence may satisfy the elapsed-time floor");
});

Run("present route resets accumulated loss evidence", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var original = Route("172.16.4.10");
    var replacement = Route("172.16.4.11");
    var generation = monitor.Start(original);

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    var present = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(4), [original with { }, replacement], false);
    var restarted = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(12), [replacement], false);

    Check(present.State == TrustedNetworkRouteProbeState.Present && present.ConsecutiveMissing == 0,
        "the exact trusted binding wins when it remains in inventory");
    Check(restarted.State == TrustedNetworkRouteProbeState.Missing && restarted.ConsecutiveMissing == 1 &&
          restarted.MissingDuration == TimeSpan.Zero,
        "loss evidence restarts after the route was observed present");
});

Run("unavailable and ambiguous inventories never accumulate", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("192.168.40.10"));
    var firstCandidate = Route("192.168.40.11");
    var secondCandidate = Route("192.168.40.12");

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [firstCandidate], false);
    var unavailable = monitor.CompleteUnavailableProbe(Probe(monitor, generation));
    var afterUnavailable = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(9), [firstCandidate], false);
    var ambiguous = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(10),
        [firstCandidate, secondCandidate], false);
    var afterAmbiguous = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(20), [firstCandidate], false);

    Check(unavailable.State == TrustedNetworkRouteProbeState.Unavailable && unavailable.ConsecutiveMissing == 0,
        "enumeration exception is explicit and clears evidence");
    Check(afterUnavailable.ConsecutiveMissing == 1 && afterUnavailable.Recovery is null,
        "unavailable enumeration cannot bridge the recovery threshold");
    Check(ambiguous.State == TrustedNetworkRouteProbeState.Ambiguous && ambiguous.ConsecutiveMissing == 0,
        "multiple replacement endpoints are ambiguous");
    Check(afterAmbiguous.ConsecutiveMissing == 1 && afterAmbiguous.Recovery is null,
        "ambiguity cannot bridge the recovery threshold");
});

Run("recent presentation suppresses stale inventory evidence", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("192.168.50.10"));
    var replacement = Route("192.168.50.11");

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    var suppressed = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(5), [replacement], true);
    var restarted = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(14), [replacement], false);

    Check(suppressed.State == TrustedNetworkRouteProbeState.SuppressedByPresentation && suppressed.ConsecutiveMissing == 0,
        "fresh physical presentation clears missing-route evidence");
    Check(restarted.ConsecutiveMissing == 1 && restarted.Recovery is null,
        "route loss must be re-proven after presentation suppression");
});

Run("stable USB identity permits one replacement across adapter recreation", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var original = Route("192.168.70.2", "USB-OLD", "USB Ethernet", 24, 31,
        NetworkInterfaceKind.Usb, "Tablet-A");
    var replacement = Route("192.168.70.3", "USB-NEW", "USB Ethernet 2", 24, 44,
        NetworkInterfaceKind.Usb, "tablet-a");
    var generation = monitor.Start(original);

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(4), [replacement], false);
    var ready = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(8), [replacement], false);

    Check(ready.State == TrustedNetworkRouteProbeState.RecoveryReady && ready.Recovery?.Replacement == replacement,
        "USB serial is the stable identity across a new interface id and endpoint");
});

Run("firewall binding changes require debounced recovery even at the same address", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var original = Route("192.168.75.2", "USB-OLD", "USB Ethernet", 24, 31,
        NetworkInterfaceKind.Usb, "Tablet-A");
    var replacement = Route("192.168.75.2", "USB-NEW", "USB Ethernet 2", 24, 44,
        NetworkInterfaceKind.Usb, "tablet-a");
    var generation = monitor.Start(original);

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(4), [replacement], false);
    var ready = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(8), [replacement], false);

    Check(ready.State == TrustedNetworkRouteProbeState.RecoveryReady && ready.Recovery?.Replacement == replacement,
        "a recreated USB binding rebuilds its listener and firewall lease even when its address is unchanged");
});

Run("Wi-Fi alias or index changes require a new exact binding", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var original = Route("192.168.76.20", "WIFI-ONE", "Wi-Fi", 24, 7);
    var replacement = original with { InterfaceAlias = "Wireless LAN", InterfaceIndex = 17 };
    var generation = monitor.Start(original);

    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);
    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(4), [replacement], false);
    var ready = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(8), [replacement], false);

    Check(ready.State == TrustedNetworkRouteProbeState.RecoveryReady && ready.Recovery?.Replacement == replacement,
        "a changed firewall interface alias or IPv4 index cannot be reported as the old binding");
});

Run("two USB bindings with one serial and address remain ambiguous", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var original = Route("192.168.77.2", "USB-OLD", "USB Ethernet", 24, 31,
        NetworkInterfaceKind.Usb, "Tablet-A");
    var first = Route("192.168.77.2", "USB-A", "USB Ethernet 2", 24, 44,
        NetworkInterfaceKind.Usb, "tablet-a");
    var second = Route("192.168.77.2", "USB-B", "USB Ethernet 3", 24, 45,
        NetworkInterfaceKind.Usb, "TABLET-A");
    var generation = monitor.Start(original);

    var result = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [first, second], false);

    Check(result.State == TrustedNetworkRouteProbeState.Ambiguous && result.ConsecutiveMissing == 0 && result.Recovery is null,
        "distinct firewall bindings are not deduplicated into an arbitrary recovery target");
});

Run("unrelated identities and changing candidates cannot authorize migration", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("10.20.0.10", "WIFI-A"));
    var unrelated = Route("10.20.0.11", "WIFI-B");
    var candidateA = Route("10.20.0.12", "wifi-a");
    var candidateB = Route("10.20.0.13", "WIFI-A");

    var none = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [unrelated], false);
    var one = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(1), [candidateA], false);
    var two = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(5), [candidateA], false);
    var changed = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(10), [candidateB], false);

    Check(none.State == TrustedNetworkRouteProbeState.Missing && none.ConsecutiveMissing == 0,
        "another interface cannot become a migration candidate");
    Check(one.ConsecutiveMissing == 1 && two.ConsecutiveMissing == 2,
        "same Wi-Fi interface id is stable independent of casing");
    Check(changed.State == TrustedNetworkRouteProbeState.Missing && changed.ConsecutiveMissing == 1 && changed.Recovery is null,
        "a different endpoint restarts the evidence chain");
});

Run("duplicate inventory rows represent one unique endpoint", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("192.168.90.10"));
    var replacement = Route("192.168.90.11");
    var result = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch,
        [replacement, replacement with { }], false);

    Check(result.State == TrustedNetworkRouteProbeState.Missing && result.ConsecutiveMissing == 1,
        "identical duplicate rows are deduplicated before ambiguity checks");
});

Run("probe sequence is single flight and rejects duplicate or forged completions", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("192.168.100.10"));
    var replacement = Route("192.168.100.11");
    var first = Probe(monitor, generation);
    Check(monitor.BeginProbe(generation) is null, "a generation permits only one in-flight inventory probe");

    var accepted = monitor.CompleteSuccessfulProbe(first, epoch, [replacement], false);
    var duplicate = monitor.CompleteSuccessfulProbe(first, epoch.AddSeconds(8), [replacement], false);
    var second = Probe(monitor, generation);
    var forged = monitor.CompleteSuccessfulProbe(new(generation, second.Sequence + 100), epoch.AddSeconds(9), [replacement], false);
    var validAfterForgery = monitor.CompleteSuccessfulProbe(second, epoch.AddSeconds(10), [replacement], false);

    Check(accepted.ConsecutiveMissing == 1, "issued probe is accepted once");
    Check(duplicate.State == TrustedNetworkRouteProbeState.Stale,
        "a duplicate completion is rejected");
    Check(forged.State == TrustedNetworkRouteProbeState.Stale && validAfterForgery.ConsecutiveMissing == 2,
        "a forged sequence cannot consume the real in-flight probe");
});

Run("start and invalidate make every old token stale", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var firstGeneration = monitor.Start(Route("192.168.110.10", "WIFI-A"));
    var oldProbe = Probe(monitor, firstGeneration);
    var secondGeneration = monitor.Start(Route("192.168.120.10", "WIFI-B"));

    var late = monitor.CompleteSuccessfulProbe(oldProbe, epoch, [Route("192.168.110.11", "WIFI-A")], false);
    Check(late.State == TrustedNetworkRouteProbeState.Stale, "replacement Start invalidates old probe results");
    Check(!monitor.Invalidate(firstGeneration) && monitor.IsCurrent(secondGeneration),
        "stale cleanup cannot invalidate a newer generation");
    Check(monitor.Invalidate(secondGeneration) && !monitor.IsCurrent(secondGeneration),
        "current generation invalidates exactly once");
    Check(!monitor.Invalidate(secondGeneration) && monitor.BeginProbe(secondGeneration) is null,
        "duplicate invalidation and later probes remain inert");
});

Run("clock rollback and malformed input fail closed", () =>
{
    var monitor = new TrustedNetworkRouteMonitor();
    var generation = monitor.Start(Route("192.168.130.10"));
    var replacement = Route("192.168.130.11");
    monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch.AddSeconds(5), [replacement], false);
    var rollback = monitor.CompleteSuccessfulProbe(Probe(monitor, generation), epoch, [replacement], false);

    Check(rollback.State == TrustedNetworkRouteProbeState.Missing && rollback.ConsecutiveMissing == 1 &&
          rollback.MissingDuration == TimeSpan.Zero,
        "a backward wall clock restarts the evidence window");
    CheckThrows<ArgumentException>(() => monitor.CompleteSuccessfulProbe(Probe(monitor, generation),
        DateTime.SpecifyKind(epoch, DateTimeKind.Local), [replacement], false),
        "non-UTC observations are rejected");
    CheckThrows<ArgumentException>(() => new TrustedNetworkRouteMonitor().Start(
        Route("127.0.0.1")), "loopback cannot become a trusted route");
});

Run("all connection start kinds share one atomic lease", () =>
{
    var coordinator = new ConnectionStartCoordinator();
    var acquired = new System.Collections.Concurrent.ConcurrentBag<ConnectionStartLease>();
    var rejected = 0;
    var kinds = Enum.GetValues<ConnectionStartKind>();

    Parallel.For(0, 64, index =>
    {
        try { acquired.Add(coordinator.Acquire(kinds[index % kinds.Length])); }
        catch (InvalidOperationException) { Interlocked.Increment(ref rejected); }
    });

    Check(acquired.Count == 1 && rejected == 63,
        "concurrent starts across all four kinds elect exactly one owner");
    var winner = acquired.Single();
    Check(coordinator.IsStarting && winner.IsCurrent,
        "the elected owner is the only current app-wide start lease");
    winner.ThrowIfNotCurrent();
    winner.Dispose();
    Check(!coordinator.IsStarting && !winner.IsCurrent,
        "disposing the winner atomically releases the app-wide start slot");
});

Run("released lease is stale and the slot can be acquired again", () =>
{
    var coordinator = new ConnectionStartCoordinator();
    var first = coordinator.Acquire(ConnectionStartKind.NativeNetwork);
    first.Dispose();

    CheckThrows<OperationCanceledException>(first.ThrowIfNotCurrent,
        "a released lease cannot authorize work after an await");
    var second = coordinator.Acquire(ConnectionStartKind.AdbCompatibility);
    Check(second.Kind == ConnectionStartKind.AdbCompatibility && second.IsCurrent && coordinator.IsStarting,
        "another connection kind can acquire the slot after release");
    second.Dispose();
});

Run("late or repeated release cannot clear a successor lease", () =>
{
    var coordinator = new ConnectionStartCoordinator();
    var predecessor = coordinator.Acquire(ConnectionStartKind.Browser);
    predecessor.Dispose();
    var successor = coordinator.Acquire(ConnectionStartKind.AdditionalNative);

    Parallel.For(0, 64, _ => predecessor.Dispose());

    Check(successor.IsCurrent && coordinator.IsStarting,
        "a predecessor's repeated late disposal leaves its successor current");
    CheckThrows<InvalidOperationException>(() => coordinator.Acquire(ConnectionStartKind.NativeNetwork),
        "an active successor continues to reject every competing acquisition");
    successor.ThrowIfNotCurrent();
    successor.Dispose();
    successor.Dispose();
    Check(!coordinator.IsStarting && !successor.IsCurrent,
        "disposing the current lease is idempotent");
});

Run("invalid connection start kind is rejected without occupying the gate", () =>
{
    var coordinator = new ConnectionStartCoordinator();
    CheckThrows<ArgumentOutOfRangeException>(() => coordinator.Acquire((ConnectionStartKind)999),
        "undefined connection kinds are rejected");
    Check(!coordinator.IsStarting,
        "invalid acquisition does not leave a phantom start owner");
});

Run("shutdown invalidates the active start but drains its completion", () =>
{
    var coordinator = new ConnectionStartCoordinator();
    Check(coordinator.WaitForIdleAsync().IsCompleted,
        "an idle coordinator has no phantom start to drain");
    var lease = coordinator.Acquire(ConnectionStartKind.AdbCompatibility);
    var drain = coordinator.WaitForIdleAsync();
    Check(!drain.IsCompleted && lease.IsCurrent,
        "the app-wide drain remains pending while ADB startup owns the lease");

    coordinator.InvalidateActive();
    Check(coordinator.IsStarting && !lease.IsCurrent && !drain.IsCompleted,
        "shutdown revokes startup authority without pretending the action has already unwound");
    CheckThrows<OperationCanceledException>(lease.ThrowIfNotCurrent,
        "an invalidated starter cannot publish resources after its next await");

    lease.Dispose();
    Check(drain.IsCompleted && !coordinator.IsStarting,
        "the shutdown drain completes only when the invalidated action releases its lease");
});

Run("registration restart policy preserves only a live user window", () =>
{
    var deadline=epoch.AddMinutes(5);
    Check(TrustedNetworkRegistrationPolicy.Evaluate(false,false,null,0,epoch)==
          TrustedNetworkRegistrationStartMode.PublishRegistration,
        "an explicit new pairing request publishes a QR even before the first trusted device exists");
    Check(TrustedNetworkRegistrationPolicy.Evaluate(false,true,deadline,0,epoch)==
          TrustedNetworkRegistrationStartMode.PublishRegistration,
        "route recovery preserves a still-live explicit registration window");
    Check(TrustedNetworkRegistrationPolicy.Evaluate(false,true,deadline,1,deadline)==
          TrustedNetworkRegistrationStartMode.TrustedReconnectOnly,
        "the original registration deadline is exclusive and cannot be extended by recovery");
});

Run("registration restart policy never creates an unusable hidden listener", () =>
{
    Check(TrustedNetworkRegistrationPolicy.Evaluate(true,true,null,1,epoch)==
          TrustedNetworkRegistrationStartMode.TrustedReconnectOnly,
        "trusted recovery without a QR waits only for a registered device");
    Check(TrustedNetworkRegistrationPolicy.Evaluate(true,true,null,0,epoch)==
          TrustedNetworkRegistrationStartMode.RequireExplicitPairing,
        "revoking the final trusted device cancels automatic hidden-listener recovery");
    Check(TrustedNetworkRegistrationPolicy.Evaluate(false,true,epoch.AddSeconds(-1),0,epoch)==
          TrustedNetworkRegistrationStartMode.RequireExplicitPairing,
        "an expired QR with no trusted device requires another explicit pairing action");
    CheckThrows<ArgumentException>(() => TrustedNetworkRegistrationPolicy.Evaluate(false,true,
        DateTime.SpecifyKind(epoch.AddMinutes(1),DateTimeKind.Local),0,epoch),
        "a non-UTC registration deadline fails closed");
});

Run("registration transfer window cannot gain lifetime during retries", () =>
{
    long monotonic=10_000;
    var deadline=epoch.AddSeconds(10);
    var window=TrustedNetworkRegistrationWindow.TryCreate(TimeSpan.FromSeconds(20),deadline,epoch,
        ()=>monotonic)??throw new InvalidOperationException("Expected a live registration window.");
    Check(window.DeadlineUtc==deadline&&window.Remaining(epoch)==TimeSpan.FromSeconds(10),
        "the original UTC deadline caps a longer service-side remainder");

    monotonic+=4_000;
    Check(window.Remaining(epoch.AddMinutes(-10))==TimeSpan.FromSeconds(6),
        "rolling UTC backward cannot restore elapsed monotonic registration lifetime");
    Check(window.Remaining(deadline) is null,
        "reaching the original UTC deadline expires the transfer even if monotonic time remains");
    Check(window.Remaining(epoch) is null,
        "a wall-clock rollback cannot revive a transfer after either deadline expired it");

    monotonic=20_000;
    var shorter=TrustedNetworkRegistrationWindow.TryCreate(TimeSpan.FromSeconds(3),deadline,epoch,
        ()=>monotonic)??throw new InvalidOperationException("Expected a service-capped registration window.");
    Check(shorter.Remaining(epoch)==TimeSpan.FromSeconds(3),
        "the old server's shorter remaining lifetime caps the replacement window");
    monotonic+=3_000;
    Check(shorter.Remaining(epoch) is null,
        "the monotonic service deadline is exclusive and expires without a UI timer tick");
    Check(TrustedNetworkRegistrationWindow.TryCreate(TimeSpan.FromSeconds(1),epoch,epoch,()=>0) is null,
        "an already-expired UI deadline cannot create a transfer window");
    CheckThrows<ArgumentException>(() => TrustedNetworkRegistrationWindow.TryCreate(TimeSpan.FromSeconds(1),
        DateTime.SpecifyKind(deadline,DateTimeKind.Local),epoch,()=>0),
        "a non-UTC transfer deadline fails closed");
});

Console.WriteLine($"PASS: {scenarios} scenarios, {assertions} assertions; no UI, network or device API was called.");

void CheckThrows<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { assertions++; return; }
    throw new InvalidOperationException("FAILED: " + message);
}
