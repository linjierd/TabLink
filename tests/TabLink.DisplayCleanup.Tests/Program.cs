using TabLink.Core;
using TabLink.Windows;

var profile = new TabletDisplayProfile(1200, 1920, 0, 1, 90, 1200, 1920, [new(1200, 1920, 90, 1)]);
var allocator = new DisplaySessionAllocator();
var assertions = 0;
var scenarios = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}

async Task<T> Reject<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T ex) { assertions++; return ex; }
    throw new Exception("Expected " + typeof(T).Name + ": " + message);
}

async Task Run(string name, Func<Task> body)
{
    VirtualDisplayManager.Reset();
    await body();
    Check(VirtualDisplayManager.Active.Count == 0, name + ": every test-owned display is released");
    scenarios++;
    Console.WriteLine("PASS " + name);
}

await Run("a second device is rejected globally before any display work", async () =>
{
    var first = await allocator.AcquireAsync(Guid.NewGuid(), profile);
    var discoveries = VirtualDisplayManager.DiscoveryCalls;
    var mutations = VirtualDisplayManager.MutationCalls;
    var guards = SessionGuard.Created;
    var error = await Reject<InvalidOperationException>(
        () => allocator.AcquireAsync(Guid.NewGuid(), profile), "only one extended display may exist");
    Check(error.Message.Contains("一", StringComparison.Ordinal) || error.Message.Contains("副屏", StringComparison.Ordinal),
        "single-display rejection is actionable");
    Check(VirtualDisplayManager.DiscoveryCalls == discoveries && VirtualDisplayManager.MutationCalls == mutations,
        "rejected second device does not enumerate or mutate displays");
    Check(SessionGuard.Created == guards, "rejected second device creates no ownership marker");
    Check(first.Guard.DisposeCalls == 0, "rejected second device leaves the active owner untouched");
    await first.DisposeAsync();
});

await Run("duplicate acquire for the same device is rejected without cleanup", async () =>
{
    var id = Guid.NewGuid();
    var active = await allocator.AcquireAsync(id, profile);
    await Reject<InvalidOperationException>(() => allocator.AcquireAsync(id, profile), "same active owner");
    Check(active.Guard.DisposeCalls == 0, "duplicate request cannot dispose the active owner");
    await active.DisposeAsync();
});

await Run("disconnect frees the only display for a different device", async () =>
{
    var first = await allocator.AcquireAsync(Guid.NewGuid(), profile);
    var target = first.TargetKey;
    await first.DisposeAsync();
    var replacement = await allocator.AcquireAsync(Guid.NewGuid(), profile);
    Check(replacement.TargetKey == target, "the sole display is reused only after completed disconnect");
    await replacement.DisposeAsync();
});

await Run("one failed stop is retried before reconnect", async () =>
{
    var id = Guid.NewGuid();
    var old = await allocator.AcquireAsync(id, profile);
    old.Guard.FailuresRemaining = 1;
    await Reject<IOException>(() => old.DisposeAsync().AsTask(), "first stop fails");
    var replacement = await allocator.AcquireAsync(id, profile);
    Check(replacement.TargetKey == old.TargetKey, "cleanup makes the sole target available again");
    Check(!ReferenceEquals(old, replacement), "reconnect returns a new reservation");
    Check(old.Guard.DisposeCalls == 2, "failed cleanup is retried once before reconnect");
    await replacement.DisposeAsync();
});

await Run("watcher completion releases a stale in-process reservation", async () =>
{
    var id = Guid.NewGuid();
    var old = await allocator.AcquireAsync(id, profile);
    old.Guard.AlwaysFail = true;
    await Reject<IOException>(() => old.DisposeAsync().AsTask(), "stop fails until watcher completes");
    old.Guard.CompleteWatcherCleanup();
    var replacement = await allocator.AcquireAsync(id, profile);
    Check(replacement.TargetKey == old.TargetKey, "watcher-retired sole target is reusable");
    await replacement.DisposeAsync();
});

await Run("persistent cleanup failure blocks every new device", async () =>
{
    var failed = await allocator.AcquireAsync(Guid.NewGuid(), profile);
    failed.Guard.AlwaysFail = true;
    await Reject<IOException>(() => failed.DisposeAsync().AsTask(), "persistent cleanup failure");
    VirtualDisplayManager.MakeInactiveWithoutRetiringGuard(failed.Lease);
    var discoveries = VirtualDisplayManager.DiscoveryCalls;
    var error = await Reject<IOException>(() => allocator.AcquireAsync(Guid.NewGuid(), profile), "pending cleanup owns the sole display");
    Check(error.Message.Contains("回收", StringComparison.Ordinal) || error.Message.Contains("重试", StringComparison.Ordinal),
        "pending cleanup rejection explains recovery");
    Check(VirtualDisplayManager.Active.Count == 0, "blocked request cannot fall back to another virtual target");
    Check(VirtualDisplayManager.DiscoveryCalls == discoveries, "blocked request never searches for a second target");
    failed.Guard.AlwaysFail = false;
    await failed.DisposeAsync();
});

await Run("late and double dispose cannot remove a replacement", async () =>
{
    var id = Guid.NewGuid();
    var old = await allocator.AcquireAsync(id, profile);
    old.Guard.FailuresRemaining = 1;
    await Reject<IOException>(() => old.DisposeAsync().AsTask(), "first release fails");
    var replacement = await allocator.AcquireAsync(id, profile);
    var oldCalls = old.Guard.DisposeCalls;
    await old.DisposeAsync();
    await old.DisposeAsync();
    Check(old.Guard.DisposeCalls == oldCalls, "stale disposal is ignored by reservation identity");
    Check(replacement.Guard.DisposeCalls == 0, "stale disposal never touches replacement guard");
    await replacement.DisposeAsync();
    await replacement.DisposeAsync();
});

await Run("failed acquisition rollback remains the sole protected slot", async () =>
{
    var id = Guid.NewGuid();
    DisplayLease? partial = null;
    VirtualDisplayManager.SetModeFault = lease =>
    {
        partial = lease;
        SessionGuard.AlwaysFailTargets.Add(lease.TargetIdentity);
        return new(false, false, "Injected mode configuration failure.");
    };
    await Reject<AggregateException>(() => allocator.AcquireAsync(id, profile), "mode failure plus rollback failure");
    Check(partial is not null, "failure happens after restore and guard ownership");
    VirtualDisplayManager.SetModeFault = null;
    VirtualDisplayManager.MakeInactiveWithoutRetiringGuard(partial!);
    await Reject<IOException>(() => allocator.AcquireAsync(Guid.NewGuid(), profile), "another device cannot bypass pending rollback");
    Check(VirtualDisplayManager.Active.Count == 0, "failed acquisition cannot allocate a spare target");
    SessionGuard.AlwaysFailTargets.Clear();
    var recovered = await allocator.AcquireAsync(id, profile);
    Check(recovered.TargetKey == partial!.TargetIdentity, "original device recovers after rollback completes");
    await recovered.DisposeAsync();
});

await Run("already-cancelled acquire has no display side effects", async () =>
{
    var discoveries = VirtualDisplayManager.DiscoveryCalls;
    var mutations = VirtualDisplayManager.MutationCalls;
    var guards = SessionGuard.Created;
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Reject<OperationCanceledException>(() => allocator.AcquireAsync(Guid.NewGuid(), profile, cancelled.Token), "cancelled before gate acquisition");
    Check(VirtualDisplayManager.DiscoveryCalls == discoveries && VirtualDisplayManager.MutationCalls == mutations,
        "cancelled acquisition does not enumerate or mutate displays");
    Check(SessionGuard.Created == guards, "cancelled acquisition creates no guard");
});

await Run("release and reconnect serialize through the same gate", async () =>
{
    var id = Guid.NewGuid();
    var old = await allocator.AcquireAsync(id, profile);
    old.Guard.FailuresRemaining = 1;
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    old.Guard.BeforeOwnedDispose = () =>
    {
        entered.Set();
        if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test did not release fake guard");
    };
    var release = Task.Run(() => old.DisposeAsync().AsTask());
    Check(entered.Wait(TimeSpan.FromSeconds(10)), "release entered guard while holding allocator gate");
    var reconnect = allocator.AcquireAsync(id, profile);
    try { Check(!reconnect.IsCompleted, "acquire waits while cleanup owns the gate"); }
    finally { resume.Set(); }
    await Reject<IOException>(() => release, "in-flight stop reports its injected failure");
    var replacement = await reconnect.WaitAsync(TimeSpan.FromSeconds(10));
    Check(old.Guard.DisposeCalls == 2, "waiting acquisition retries pending release");
    Check(replacement.TargetKey == old.TargetKey, "serialized reconnect reuses the sole target");
    await replacement.DisposeAsync();
});

await Run("cancelled waiter cannot mutate while release holds the gate", async () =>
{
    var old = await allocator.AcquireAsync(Guid.NewGuid(), profile);
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    old.Guard.BeforeOwnedDispose = () =>
    {
        entered.Set();
        if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test did not release fake guard");
    };
    var release = Task.Run(() => old.DisposeAsync().AsTask());
    Check(entered.Wait(TimeSpan.FromSeconds(10)), "release acquired gate before cancellation test");
    var discoveries = VirtualDisplayManager.DiscoveryCalls;
    var created = SessionGuard.Created;
    using var cancel = new CancellationTokenSource();
    var waiting = allocator.AcquireAsync(Guid.NewGuid(), profile, cancel.Token);
    cancel.Cancel();
    try
    {
        await Reject<OperationCanceledException>(() => waiting, "waiter cancellation");
        Check(VirtualDisplayManager.DiscoveryCalls == discoveries && SessionGuard.Created == created,
            "cancelled waiter never begins allocation");
    }
    finally { resume.Set(); }
    await release.WaitAsync(TimeSpan.FromSeconds(10));
});

await Run("first connection prepares one driver before display discovery and disconnect removes it", async () =>
{
    var driver = new FakeSingleDisplayDriverController();
    var managed = new DisplaySessionAllocator(driver);
    var reservation = await managed.AcquireAsync(Guid.NewGuid(), profile);
    Check(driver.EnsureCalls == 1 && driver.DiscoveryCallsAtEnsure == 0,
        "driver preparation completes before allocator enumerates targets");
    Check(driver.DevicePresent, "driver is present only for the active session");
    await reservation.DisposeAsync();
    Check(driver.RemoveCalls == 1 && !driver.DevicePresent,
        "last disconnect removes the owned virtual display device");
    Check(driver.Events.SequenceEqual(["ensure", "remove"]), "driver lifecycle order is prepare then remove");
});

await Run("terminal USB recovery failure precisely releases the sole display and driver", async () =>
{
    var driver = new FakeSingleDisplayDriverController();
    var managed = new DisplaySessionAllocator(driver);
    var reservation = await managed.AcquireAsync(Guid.NewGuid(), profile);
    var gate = new UsbSessionRecoveryGate();
    var now = DateTime.UtcNow;
    gate.ObserveConnected();
    var attempt = gate.TryBegin(now, sessionActive: true, clientConnected: false)!.Value;
    gate.Complete(attempt, now.AddMilliseconds(1), clientConnected: false, retryable: false);
    Check(gate.RequiresSessionStop, "terminal USB ownership or policy failure requires immediate session stop");
    if(gate.RequiresSessionStop) await reservation.DisposeAsync();
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 1 && !driver.DevicePresent,
        "terminal recovery failure removes exactly the one driver prepared by this reservation");
    Check(reservation.Guard.DisposeCalls == 1 && VirtualDisplayManager.Active.Count == 0,
        "terminal recovery failure disposes the exact display guard once");
    await reservation.DisposeAsync();
    Check(driver.RemoveCalls == 1 && reservation.Guard.DisposeCalls == 1,
        "a repeated stop cannot remove the driver or display twice");
});

await Run("exhausted USB retries precisely release the same sole display", async () =>
{
    var driver = new FakeSingleDisplayDriverController();
    var managed = new DisplaySessionAllocator(driver);
    var reservation = await managed.AcquireAsync(Guid.NewGuid(), profile);
    var gate = new UsbSessionRecoveryGate();
    var now = DateTime.UtcNow;
    gate.ObserveConnected();
    foreach(var offset in new[]{0,2,6})
    {
        var attempt=gate.TryBegin(now.AddSeconds(offset),true,false)!.Value;
        gate.Complete(attempt,now.AddSeconds(offset),clientConnected:false,retryable:true);
    }
    Check(gate.RequiresSessionStop && gate.Failures == UsbSessionRecoveryGate.MaximumAttempts,
        "three retryable failures exhaust the fixed recovery episode");
    if(gate.RequiresSessionStop)await reservation.DisposeAsync();
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 1 && reservation.Guard.DisposeCalls == 1,
        "retry exhaustion cleans the original reservation without preparing a second display");
});

await Run("driver preparation refusal never discovers or activates a display", async () =>
{
    var driver = new FakeSingleDisplayDriverController { FailEnsure = true };
    var managed = new DisplaySessionAllocator(driver);
    await Reject<IOException>(() => managed.AcquireAsync(Guid.NewGuid(), profile), "driver preparation fails transactionally");
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 0,
        "transactional preparation failure is reported once without a second allocator cleanup command");
    Check(VirtualDisplayManager.DiscoveryCalls == 0 && VirtualDisplayManager.MutationCalls == 0 && SessionGuard.Created == 0,
        "allocator performs no display work after helper refusal");
    Check(!driver.DevicePresent, "transactional preparation failure leaves no fake driver");
});

await Run("a second device is rejected before another driver helper call", async () =>
{
    var driver = new FakeSingleDisplayDriverController();
    var managed = new DisplaySessionAllocator(driver);
    var first = await managed.AcquireAsync(Guid.NewGuid(), profile);
    await Reject<InvalidOperationException>(() => managed.AcquireAsync(Guid.NewGuid(), profile), "one global display slot");
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 0,
        "rejected second device cannot install, restart or uninstall the driver");
    await first.DisposeAsync();
    Check(driver.RemoveCalls == 1, "the actual owner still performs the only removal");
});

await Run("connection failure after driver preparation removes the temporary device", async () =>
{
    var driver = new FakeSingleDisplayDriverController();
    var managed = new DisplaySessionAllocator(driver);
    VirtualDisplayManager.SetModeFault = _ => new(false, false, "Injected mode failure.");
    await Reject<IOException>(() => managed.AcquireAsync(Guid.NewGuid(), profile), "mode failure after preparation");
    VirtualDisplayManager.SetModeFault = null;
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 1,
        "post-prepare connection failure executes owned driver rollback");
    Check(!driver.DevicePresent, "failed connection leaves no virtual display device");
});

await Run("cancellation observed after helper completion still removes the device", async () =>
{
    using var cancel = new CancellationTokenSource();
    var driver = new FakeSingleDisplayDriverController { AfterEnsure = cancel.Cancel };
    var managed = new DisplaySessionAllocator(driver);
    await Reject<OperationCanceledException>(() => managed.AcquireAsync(Guid.NewGuid(), profile, cancel.Token),
        "cancellation races completed helper transaction");
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 1 && !driver.DevicePresent,
        "completed preparation is rolled back before cancellation escapes");
    Check(VirtualDisplayManager.DiscoveryCalls == 0 && SessionGuard.Created == 0,
        "post-helper cancellation never enumerates or activates a display");
});

await Run("failed driver removal is retried before the next installation", async () =>
{
    var driver = new FakeSingleDisplayDriverController { RemoveFailuresRemaining = 1 };
    var managed = new DisplaySessionAllocator(driver);
    var first = await managed.AcquireAsync(Guid.NewGuid(), profile);
    await Reject<IOException>(() => first.DisposeAsync().AsTask(), "first helper removal fails after display detach");
    Check(VirtualDisplayManager.Active.Count == 0 && driver.DevicePresent,
        "removal failure leaves no active desktop output but records pending device cleanup");
    var replacement = await managed.AcquireAsync(Guid.NewGuid(), profile);
    Check(driver.Events.Take(4).SequenceEqual(["ensure", "remove", "remove", "ensure"]),
        "next connection retries pending removal before any new preparation");
    await replacement.DisposeAsync();
    Check(driver.RemoveCalls == 3 && !driver.DevicePresent,
        "replacement disconnect completes the final removal");
});

await Run("double disconnect cannot invoke driver removal twice", async () =>
{
    var driver = new FakeSingleDisplayDriverController();
    var managed = new DisplaySessionAllocator(driver);
    var reservation = await managed.AcquireAsync(Guid.NewGuid(), profile);
    await reservation.DisposeAsync();
    await reservation.DisposeAsync();
    Check(driver.EnsureCalls == 1 && driver.RemoveCalls == 1,
        "stale repeated disposal is idempotent for the device helper");
});

Console.WriteLine($"PASS: {scenarios} single-display cleanup lifecycle scenarios, {assertions} assertions; production allocator with memory-only display/guard doubles. No native display, guard file or process calls.");
