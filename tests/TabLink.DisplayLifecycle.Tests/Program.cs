using System.Text.Json;
using TabLink.Windows;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
var available = new InputDesktopStatus(InputDesktopState.Available, "fake-normal-desktop");
var unavailable = new InputDesktopStatus(InputDesktopState.Unavailable, "fake-secure-desktop");
var unknown = new InputDesktopStatus(InputDesktopState.Unknown, "fake-probe-error", 5);

using (var test = new Scenario())
{
    test.Desktop = t => t.Seconds < 70 ? unavailable : available;
    Check(await test.Run() == 0, "secure desktop recovery completes");
    Check(test.DetachTimes.SequenceEqual([92]), "70-second secure desktop survives original 20-second deadline, then grants exactly one 20-second recovery window");
    Check(test.DetachLeaseIds.All(id => id == test.Lease.LeaseId), "only the originally owned virtual display is detached");
}
using (var test = new Scenario())
{
    test.Desktop = t => t.Seconds < 70 ? unavailable : available;
    test.AfterTick = t => { if (t.Seconds == 80) t.WriteState(t.State with { DeadlineUtc = t.Now.AddSeconds(20) }); };
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([102]), "real post-resume renewal supersedes grace without manufacturing an ACK");
}
using (var test = new Scenario())
{
    test.Desktop = _ => unavailable;
    test.Owner = t => t.Seconds < 30 ? SessionGuard.OwnerLiveness.VerifiedRunning : SessionGuard.OwnerLiveness.Exited;
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([30]), "owner exit reclaims even while secure desktop remains unavailable");
}
using (var test = new Scenario())
{
    test.Desktop = t => t.Seconds < 70 ? unavailable : available;
    test.Owner = t => t.Seconds < 30 ? SessionGuard.OwnerLiveness.VerifiedRunning : SessionGuard.OwnerLiveness.Exited;
    test.DetachSucceeds = t => t.Seconds >= 70;
    Check(await test.Run() == 0 && test.DetachTimes.First() == 30 && test.DetachTimes.Last() == 70,
        "crash cleanup is attempted immediately and retried until desktop returns when Windows rejects early attempts");
    Check(test.DetachTimes.Count > 3 && test.DetachLeaseIds.Distinct().Single() == test.Lease.LeaseId,
        "secure desktop does not abandon failed crash cleanup after three attempts or expand its display authority");
}
using (var test = new Scenario())
{
    test.Desktop = _ => unavailable;
    test.AfterTick = t => { if (t.Seconds == 12) t.WriteState(t.State with { StopRequested = true, DeadlineUtc = t.Now }); };
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([12]), "explicit stop is not suspended by secure desktop");
}
using (var test = new Scenario())
{
    test.Desktop = _ => unknown;
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([22]), "unknown desktop errors cannot extend missing-frame deadline");
}
using (var test = new Scenario())
{
    test.Desktop = _ => unavailable;
    test.Owner = _ => SessionGuard.OwnerLiveness.Unverified;
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([22]), "unverified process identity cannot authorize indefinite pause");
}
using (var test = new Scenario())
{
    test.Desktop = t => t.Seconds < 40 ? unavailable : unknown;
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([40]), "probe errors following secure desktop do not count as confirmed recovery or receive grace");
}
using (var test = new Scenario())
{
    test.Desktop = _ => unavailable;
    test.AfterTick = t => { if (t.Seconds == 30) File.WriteAllText(t.Path, "broken json"); };
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([30]), "unreadable lease still expires from immutable ownership proof during desktop pause");
}
using (var test = new Scenario())
{
    test.Desktop = _ => unavailable;
    test.AfterTick = t => { if (t.Seconds == 30) File.WriteAllText(t.Path + ".lease-id", JsonSerializer.Serialize(Guid.NewGuid())); };
    Check(await test.Run() == 0 && test.DetachTimes.Count == 0, "a newer lease always retires the old watcher without detaching any display");
}
using (var test = new Scenario())
{
    test.Desktop = _ => available;
    test.Owner = t => t.Seconds < 4 ? SessionGuard.OwnerLiveness.VerifiedRunning : SessionGuard.OwnerLiveness.Exited;
    Check(await test.Run() == 0 && test.DetachTimes.SequenceEqual([4]), "process exit has priority over an unexpired lease");
}
Check(available.IsAvailable && !available.IsUnavailable && unavailable.IsUnavailable && !unknown.IsUnavailable,
    "availability states do not equate probe errors with confirmed desktop unavailability");
var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var presentation = new SessionPresentationDeadline();
presentation.Reset(origin);
Check(SessionPresentationDeadline.HasSafeEncoderProbeBudget &&
    SessionPresentationDeadline.EncoderProbeBudget == TimeSpan.FromSeconds(30) &&
    SessionPresentationDeadline.InitialPresentationTimeout == TimeSpan.FromSeconds(45),
    "encoder probing has one explicit budget below the first-presentation deadline");
Check(presentation.Evaluate(origin.AddSeconds(1), null, false, available).DeadlineUtc == origin.AddSeconds(45),
    "without progress or a recovery event, the first-presentation deadline remains fixed after encoder probing");
var oscillatingDeadlines = Enumerable.Range(5, 100).Select(second =>
    presentation.Evaluate(origin.AddSeconds(second), null, second % 2 == 1, available).DeadlineUtc).ToArray();
Check(oscillatingDeadlines.All(deadline => deadline == origin.AddSeconds(45)),
    "paused/running oscillation without real ACK cannot renew the fixed first-presentation grace");
Check(presentation.LastProgressUtc == origin, "capture pause never fabricates presentation progress");

presentation.Reset(origin);
presentation.Evaluate(origin.AddSeconds(5), null, true, available);
var afterAck = presentation.Evaluate(origin.AddSeconds(17), origin.AddSeconds(17), false, available);
Check(afterAck.DeadlineUtc == origin.AddSeconds(37) && presentation.RecoveryDeadlineUtc == DateTime.MinValue,
    "a real ACK restores its own deadline and clears the consumed recovery opportunity");
Check(presentation.Evaluate(origin.AddSeconds(20), origin.AddSeconds(17), true, available).DeadlineUtc == origin.AddSeconds(40),
    "real ACK progress permits a subsequent capture failure one new recovery opportunity");
Check(presentation.Evaluate(origin.AddSeconds(35), origin.AddSeconds(17), false, available).DeadlineUtc == origin.AddSeconds(40)
    && presentation.Evaluate(origin.AddSeconds(39), origin.AddSeconds(16), true, available).DeadlineUtc == origin.AddSeconds(40),
    "repeated or older ACK values do not unlock more recovery grace");

presentation.Reset(origin);
for (var second = 0; second <= 70; second += 2)
{
    var state = presentation.Evaluate(origin.AddSeconds(second), null, true, unavailable);
    if (state.DeadlineUtc != origin.AddSeconds(second + 20) || !state.CapturePaused)
        throw new Exception("Confirmed local unavailable desktop must retain its renewable pause");
}
Check(presentation.LastProgressUtc == origin, "a 70-second local desktop pause preserves the true last ACK timestamp");
Check(presentation.Evaluate(origin.AddSeconds(72), null, true, available).DeadlineUtc == origin.AddSeconds(92),
    "confirmed return from unavailable desktop grants one fixed 20-second recovery window");
Check(Enumerable.Range(73, 50).All(second =>
    presentation.Evaluate(origin.AddSeconds(second), null, second % 2 == 0, available).DeadlineUtc == origin.AddSeconds(92)),
    "post-desktop recovery is not extended by capture-state oscillation or elapsed time");
Check(presentation.Evaluate(origin.AddSeconds(130), null, true, unavailable).DeadlineUtc == origin.AddSeconds(150)
    && presentation.Evaluate(origin.AddSeconds(140), null, false, available).DeadlineUtc == origin.AddSeconds(160),
    "a genuinely new local unavailable-desktop interval can independently pause and recover again");

presentation.Reset(origin);
Check(Enumerable.Range(0, 100).All(second =>
    presentation.Evaluate(origin.AddSeconds(second), null, second % 2 == 0, unknown).DeadlineUtc == origin.AddSeconds(45)),
    "unknown probes and host pause flags cannot indefinitely renew the deadline");
presentation.Evaluate(origin.AddSeconds(110), null, true, unavailable);
presentation.Reset(origin.AddSeconds(200));
Check(presentation.Evaluate(origin.AddSeconds(201), null, false, available).DeadlineUtc == origin.AddSeconds(245)
    && presentation.RecoveryDeadlineUtc == DateTime.MinValue,
    "a new connection cannot inherit the previous connection's desktop transition or grace");
using (var first = new Scenario("FAKE-A"))
using (var second = new Scenario("FAKE-B"))
{
    var untouched = File.ReadAllBytes(second.Path);
    Check(SessionGuard.LeasePath(first.Lease) != SessionGuard.LeasePath(second.Lease), "two display guards use separate ownership files");
    Check(SessionGuard.RememberedPath(first.Lease) != SessionGuard.RememberedPath(second.Lease), "two display guards remember their layouts separately");
    Check(await first.Run() == 0 && first.DetachLeaseIds.All(id => id == first.Lease.LeaseId), "first display expires and releases only its own lease");
    Check(File.ReadAllBytes(second.Path).SequenceEqual(untouched) && JsonSerializer.Deserialize<Guid>(File.ReadAllText(second.Path + ".lease-id")) == second.Lease.LeaseId,
        "stopping first watcher does not change second display's state or marker");
    Check(await second.Run() == 0 && second.DetachLeaseIds.All(id => id == second.Lease.LeaseId), "second display independently retains crash cleanup");
    Check(SessionGuard.IsAllowedWatchPath(SessionGuard.LeasePath(first.Lease)), "scoped target watcher path accepted");
    Check(!SessionGuard.IsAllowedWatchPath(SessionGuard.RememberedPath(first.Lease)), "remembered-layout file cannot run as watcher state");
}
Check(SessionGuard.IsAllowedWatchPath(System.IO.Path.Combine(SessionGuard.Folder, "active-display-lease.json")), "exact legacy watcher path accepted for migration");
Check(!SessionGuard.IsAllowedWatchPath(System.IO.Path.Combine(SessionGuard.LeaseFolder, "arbitrary.json")), "non-hash watcher filename rejected");
Check(!SessionGuard.IsAllowedWatchPath(System.IO.Path.Combine(SessionGuard.LeaseFolder + "-other", new string('A', 64) + ".json")), "sibling directory rejected");
Check(!SessionGuard.IsAllowedWatchPath(System.IO.Path.Combine(SessionGuard.LeaseFolder, "..", new string('A', 64) + ".json")), "path escaping scoped directory rejected");
Check(!SessionGuard.IsAllowedWatchPath(System.IO.Path.Combine(SessionGuard.LeaseFolder, "nested", new string('A', 64) + ".json")), "nested directory rejected");

var ownerExit = new FakeDriverOwnerProcess(4321, 987654321);
var ownerReady = false;
var ownerCleanupCalls = 0;
var ownerWatch = DriverOwnerWatchdog.WatchVerifiedOwnerAsync(4321, 987654321, _ => ownerExit,
    () => ownerReady = true, () => { ownerCleanupCalls++; return Task.CompletedTask; });
Check(ownerReady && !ownerWatch.IsCompleted && ownerCleanupCalls == 0,
    "process watchdog verifies identity and signals readiness before waiting, without early cleanup");
ownerExit.Exit();
Check(await ownerWatch == 0 && ownerCleanupCalls == 1 && ownerExit.Disposed,
    "verified owner exit invokes strict cleanup exactly once and disposes the retained process handle");

var wrongOwner = new FakeDriverOwnerProcess(4321, 987654322);
var wrongReady = false;
var wrongCleanup = false;
try
{
    await DriverOwnerWatchdog.WatchVerifiedOwnerAsync(4321, 987654321, _ => wrongOwner,
        () => wrongReady = true, () => { wrongCleanup = true; return Task.CompletedTask; });
    throw new Exception("Expected owner start-time mismatch rejection");
}
catch(IOException) { assertions++; }
Check(!wrongReady && !wrongCleanup && wrongOwner.Disposed,
    "PID reuse or start-time mismatch cannot authorize readiness or driver cleanup");

var wrongPidOwner = new FakeDriverOwnerProcess(4322, 987654321);
var wrongPidReady = false;
try
{
    await DriverOwnerWatchdog.WatchVerifiedOwnerAsync(4321, 987654321, _ => wrongPidOwner,
        () => wrongPidReady = true, () => throw new Exception("cleanup must not run"));
    throw new Exception("Expected owner PID mismatch rejection");
}
catch(IOException) { assertions++; }
Check(!wrongPidReady && wrongPidOwner.Disposed,
    "both PID and start time must match before the watchdog signals readiness");

var uncertainOwner = new FakeDriverOwnerProcess(4321, 987654321);
var uncertainCleanup = false;
var uncertainWatch = DriverOwnerWatchdog.WatchVerifiedOwnerAsync(4321, 987654321, _ => uncertainOwner,
    () => { }, () => { uncertainCleanup = true; return Task.CompletedTask; });
uncertainOwner.Fail(new IOException("Injected wait failure"));
try
{
    await uncertainWatch;
    throw new Exception("Expected owner wait failure");
}
catch(IOException) { assertions++; }
Check(!uncertainCleanup && uncertainOwner.Disposed,
    "an unverified process-wait failure fails closed without invoking driver cleanup");

var failingCleanupOwner = new FakeDriverOwnerProcess(4321, 987654321);
var failingCleanupCalls = 0;
var failingCleanupWatch = DriverOwnerWatchdog.WatchVerifiedOwnerAsync(4321, 987654321, _ => failingCleanupOwner,
    () => { }, () => { failingCleanupCalls++; throw new IOException("Injected strict cleanup failure"); });
failingCleanupOwner.Exit();
try
{
    await failingCleanupWatch;
    throw new Exception("Expected strict cleanup callback failure");
}
catch(IOException) { assertions++; }
Check(failingCleanupCalls == 1 && failingCleanupOwner.Disposed,
    "strict cleanup callback failure faults the watcher once so the child process returns failure");

GuardDisposalTests.Run(Check);
Console.WriteLine($"PASS: {assertions} display lifecycle assertions; injected clocks/desktops/process identities, fake display adapter, no native display or USB operations.");

sealed class Scenario : IDisposable
{
    readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TabLink-DisplayLifecycle-" + Guid.NewGuid().ToString("N"));
    readonly DateTime origin = DateTime.UtcNow;
    public string Path { get; }
    public int Seconds { get; private set; }
    public DateTime Now => origin.AddSeconds(Seconds);
    public DisplayLease Lease { get; }
    public SessionGuard.WatchState State { get; private set; }
    public Func<Scenario, InputDesktopStatus> Desktop { get; set; } = _ => new(InputDesktopState.Available, "fake");
    public Func<Scenario, SessionGuard.OwnerLiveness> Owner { get; set; } = _ => SessionGuard.OwnerLiveness.VerifiedRunning;
    public Func<Scenario, bool> DetachSucceeds { get; set; } = _ => true;
    public Action<Scenario>? AfterTick { get; set; }
    public List<int> DetachTimes { get; } = [];
    public List<Guid> DetachLeaseIds { get; } = [];
    public Scenario(string deviceName = @"\\.\FAKE-TABLINK-ONLY")
    {
        Lease = new(Guid.NewGuid(), deviceName);
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "lease.json");
        State = new(Lease, 1234, 5678, Now.AddSeconds(20));
        WriteState(State);
        File.WriteAllText(Path + "." + Lease.LeaseId.ToString("N") + ".initial.json", JsonSerializer.Serialize(State));
        File.WriteAllText(Path + ".lease-id", JsonSerializer.Serialize(Lease.LeaseId));
    }
    public void WriteState(SessionGuard.WatchState state)
    {
        State = state;
        File.WriteAllText(Path, JsonSerializer.Serialize(state));
    }
    public Task<int> Run()
    {
        VirtualDisplayManager.OnDetach = lease =>
        {
            DetachTimes.Add(Seconds);
            DetachLeaseIds.Add(lease.LeaseId);
            return new(DetachSucceeds(this), true, "fake mutation only");
        };
        return SessionGuard.WatchOwnedAsync(Path, Lease.LeaseId, new(() => Now, _ => Owner(this), () => Desktop(this), () =>
        {
            Seconds += 2;
            if (Seconds > 180) throw new Exception("Watcher failed to reach a bounded test outcome");
            AfterTick?.Invoke(this);
            return Task.CompletedTask;
        }));
    }
    public void Dispose() => Directory.Delete(directory, true);
}

sealed class FakeDriverOwnerProcess(int id, long startTicks) : DriverOwnerWatchdog.IDriverOwnerProcess
{
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Id { get; } = id;
    public long StartTimeUtcTicks { get; } = startTicks;
    public bool Disposed { get; private set; }
    public Task WaitForExitAsync() => exited.Task;
    public void Exit() => exited.TrySetResult();
    public void Fail(Exception error) => exited.TrySetException(error);
    public void Dispose() => Disposed = true;
}
