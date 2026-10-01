using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using TabLink.DriverSetup;

var assertions = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
void Reject(Action action, string message)
{
    try { action(); throw new Exception("Expected rejection: " + message); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Xml.XmlException) { assertions++; }
}
void RejectHandoff(Action action, string message)
{
    try { action(); throw new Exception("Expected protected handoff rejection: " + message); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
    { assertions++; }
}
void RejectLeaseScan(Action action, string message)
{
    try { action(); throw new Exception("Expected lease scan rejection: " + message); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
    { assertions++; }
}
var original = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="utf-8"?>
<vdd_settings><monitors><count>1</count></monitors><gpu><friendlyname>keep-gpu</friendlyname></gpu>
<global><g_refresh_rate>90</g_refresh_rate></global><resolutions>
<resolution custom="preserve"><width>1200</width><height>1920</height><refresh_rate>90</refresh_rate><refresh_rate>60</refresh_rate></resolution>
<resolution><width>720</width><height>1280</height><refresh_rate>75</refresh_rate></resolution>
</resolutions><options><HardwareCursor>true</HardwareCursor><custom-option>unchanged</custom-option></options></vdd_settings>
""");
foreach (var count in new[] { 1 })
{
    var bytes = DriverInstaller.BuildPoolConfiguration(original, count);
    var root = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!;
    Check((int)root.Element("monitors")!.Element("count")! == count, "configured count " + count);
    Check(root.Element("gpu")!.Element("friendlyname")!.Value == "keep-gpu" && root.Element("options")!.Element("custom-option")!.Value == "unchanged", "unrelated configuration preserved");
    var rows = root.Element("resolutions")!.Elements("resolution").ToArray();
    Check(rows.Single(r => (int)r.Element("width")! == 1200).Attribute("custom")!.Value == "preserve" &&
        rows.Single(r => (int)r.Element("width")! == 1200).Elements("refresh_rate").Select(e => e.Value).SequenceEqual(["90", "60"]), "existing native tablet resolution and attributes remain intact");
    foreach (var (w, h) in new[] { (720, 1280), (1280, 720), (1080, 1920), (1920, 1080) })
    {
        var row = rows.Single(r => (int)r.Element("width")! == w && (int)r.Element("height")! == h);
        Check(new[] { "30", "60" }.All(rate => row.Elements("refresh_rate").Any(e => e.Value == rate)), "browser orientation supports 30 and60 Hz");
    }
    Check(rows.Single(r => (int)r.Element("width")! == 720).Elements("refresh_rate").Any(e => e.Value == "75"), "existing extra refresh retained");
    Check(DriverInstaller.BuildPoolConfiguration(bytes, count).SequenceEqual(bytes), "pool merge is byte-idempotent");
    var profile = XDocument.Parse(Encoding.UTF8.GetString(DriverInstaller.BuildProfileConfiguration(bytes, 1600, 900, 90))).Root!;
    Check((int)profile.Element("monitors")!.Element("count")! == count, "legacy profile API preserves existing pool count");
}
var legacyMulti = XDocument.Parse(Encoding.UTF8.GetString(original));
legacyMulti.Root!.Element("monitors")!.Element("count")!.Value = "4";
var singleBytes = DriverInstaller.BuildSingleDisplayConfiguration(Encoding.UTF8.GetBytes(legacyMulti.ToString()), 1920, 1200, 90);
var single = XDocument.Parse(Encoding.UTF8.GetString(singleBytes)).Root!;
Check((int)single.Element("monitors")!.Element("count")! == 1, "connection preparation collapses a legacy four-display pool to one");
foreach (var (width, height) in new[] { (1920, 1200), (1200, 1920) })
{
    var row = single.Element("resolutions")!.Elements("resolution")
        .Single(r => (int)r.Element("width")! == width && (int)r.Element("height")! == height);
    Check(row.Elements("refresh_rate").Any(e => e.Value == "90"), "single display preparation adds the tablet native mode in both orientations");
}
Check(DriverInstaller.BuildSingleDisplayConfiguration(singleBytes, 1920, 1200, 90).SequenceEqual(singleBytes),
    "single display preparation is byte-idempotent");
Reject(() => DriverInstaller.BuildPoolConfiguration(original, 0), "zero count");
Reject(() => DriverInstaller.BuildPoolConfiguration(original, 2), "a second virtual display is forbidden");
Reject(() => DriverInstaller.BuildPoolConfiguration(original, 8), "the former multi-display maximum is forbidden");
Check(DriverInstaller.ReadConfiguredCount(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(original).ToArray()) == 1, "UTF8 BOM count can be read for rollback");
Reject(() => DriverInstaller.BuildPoolConfiguration(Encoding.UTF8.GetBytes("<!DOCTYPE vdd_settings [<!ENTITY x SYSTEM 'file:///C:/private'>]><vdd_settings>&x;</vdd_settings>"), 1), "DTD rejected");
Reject(() => DriverInstaller.BuildPoolConfiguration(Encoding.UTF8.GetBytes("<vdd_settings><monitors><count>1</count><count>3</count></monitors><resolutions/></vdd_settings>"), 1), "ambiguous monitor count rejected");
Reject(() => DriverInstaller.BuildPoolConfiguration(new byte[131073], 1), "oversized XML rejected");
var many = XDocument.Parse(Encoding.UTF8.GetString(original));
many.Root!.Element("resolutions")!.ReplaceNodes(Enumerable.Range(0, 31).Select(i => new XElement("resolution", new XElement("width", 1000 + i), new XElement("height", 700), new XElement("refresh_rate", 60))));
Reject(() => DriverInstaller.BuildPoolConfiguration(Encoding.UTF8.GetBytes(many.ToString()), 1), "post-merge resolution cap checked");
var modeOverflow = XDocument.Parse(Encoding.UTF8.GetString(original));
modeOverflow.Root!.Element("global")!.ReplaceNodes(Enumerable.Range(30, 40).Select(rate => new XElement("g_refresh_rate", rate)));
Reject(() => DriverInstaller.BuildPoolConfiguration(Encoding.UTF8.GetBytes(modeOverflow.ToString()), 1), "global refresh cross-product cap checked");
Check(DisplayConfigurationActivity.IsLeaseFileName(new string('A', 64) + ".json"), "only active per-target lease filename recognized");
Check(!DisplayConfigurationActivity.IsLeaseFileName(new string('A', 64) + ".last.json") && !DisplayConfigurationActivity.IsLeaseFileName("file.initial.json"), "remembered and bootstrap state excluded from live ownership scan");
var displayCommands = new[] { "--install", "--uninstall", "--prepare-single-display", "--prepare-single-display-held",
    "--remove-session-display", "--remove-session-display-held", "--configure-display", "--configure-pool", "--collect-idle-pool" };
Check(displayCommands.All(DisplayMutationBoundary.IsDisplayMutationCommand) &&
      !DisplayMutationBoundary.IsDisplayMutationCommand("--prepare-tablet-only"),
    "every virtual-display mutation command enters the shared lifecycle boundary");
RejectHandoff(() => HeldDriverCommandInvocation.Parse(
    ["--prepare-single-display-held", "1920", "1200", "90"], 3),
    "held helper without protected handoff credentials");

Check(DriverOperationLock.MutexName == @"Global\TabLink.DriverSetup.25.7.23",
    "driver operation lock is distinct from the full-session lifecycle mutex");
var operationName = @"Local\TabLink.DriverOperation.Tests." + Guid.NewGuid().ToString("N");
using var firstEntered = new ManualResetEventSlim();
using var releaseFirst = new ManualResetEventSlim();
var operationOrder = new List<string>();
var firstOperation = Task.Run(() => DriverOperationLock.Run(operationName, TimeSpan.FromSeconds(5), () =>
{
    operationOrder.Add("first-enter");
    firstEntered.Set();
    if (!releaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test did not release first operation");
    operationOrder.Add("first-exit");
    return 1;
}));
Check(firstEntered.Wait(TimeSpan.FromSeconds(5)), "first helper transaction acquired the operation mutex");
var secondOperation = Task.Run(() => DriverOperationLock.Run(operationName, TimeSpan.FromSeconds(5), () =>
{
    operationOrder.Add("second-enter");
    return 2;
}));
Check(!secondOperation.Wait(TimeSpan.FromMilliseconds(150)), "second helper transaction waits instead of racing the first");
releaseFirst.Set();
Check(await firstOperation == 1 && await secondOperation == 2 &&
    operationOrder.SequenceEqual(["first-enter", "first-exit", "second-enter"]),
    "prepare/remove helper transactions execute in serialized order");

try { DriverOperationLock.Run<int>(operationName, TimeSpan.FromSeconds(2), () => throw new IOException("injected helper failure")); }
catch(IOException) { assertions++; }
Check(DriverOperationLock.Run(operationName, TimeSpan.FromSeconds(2), () => 7) == 7,
    "helper exception releases the operation mutex for strict cleanup");

var abandonedName = @"Local\TabLink.DriverOperation.Abandoned.Tests." + Guid.NewGuid().ToString("N");
using var abandonedAcquired = new ManualResetEventSlim();
var abandonedOwner = new Thread(() =>
{
    var mutex = new Mutex(false, abandonedName);
    mutex.WaitOne();
    abandonedAcquired.Set();
    // Deliberately exit without ReleaseMutex: a crashed helper abandons it.
});
abandonedOwner.Start();
Check(abandonedAcquired.Wait(TimeSpan.FromSeconds(5)) && abandonedOwner.Join(TimeSpan.FromSeconds(5)),
    "test helper abandoned its owned operation mutex");
Check(DriverOperationLock.Run(abandonedName, TimeSpan.FromSeconds(2), () => 9) == 9,
    "strict cleanup can acquire a crash-abandoned helper operation mutex");

// Prove the production order is lifecycle -> operation. Holding operation makes
// a standalone maintenance command wait while it already excludes a competing
// lifecycle owner; releasing operation completes without a lock-order cycle.
var orderLifecycle = @"Local\TabLink.Lifecycle.Order.Tests." + Guid.NewGuid().ToString("N");
var orderOperation = @"Local\TabLink.Operation.Order.Tests." + Guid.NewGuid().ToString("N");
using (var operationOwned = new ManualResetEventSlim())
using (var releaseOperation = new ManualResetEventSlim())
{
    var operationOwner = new Thread(() =>
    {
        using var heldOperation = new Mutex(false, orderOperation);
        heldOperation.WaitOne();
        operationOwned.Set();
        releaseOperation.Wait();
        heldOperation.ReleaseMutex();
    });
    operationOwner.Start();
    Check(operationOwned.Wait(TimeSpan.FromSeconds(2)), "test owns operation mutex before lock-order probe");
    using var boundaryStarted = new ManualResetEventSlim();
    var standalone = Task.Run(() =>
    {
        boundaryStarted.Set();
        return DisplayMutationBoundary.RunStandalone(orderLifecycle, orderOperation,
            TimeSpan.FromSeconds(5), () => 17);
    });
    Check(boundaryStarted.Wait(TimeSpan.FromSeconds(2)), "standalone display maintenance entered boundary");
    Thread.Sleep(100);
    var lifecycleProbe = Task.Run(() =>
    {
        using var probe = new Mutex(false, orderLifecycle);
        var acquired = probe.WaitOne(TimeSpan.FromMilliseconds(200));
        if (acquired) probe.ReleaseMutex();
        return acquired;
    });
    Check(!await lifecycleProbe, "standalone maintenance owns lifecycle before waiting for operation");
    releaseOperation.Set();
    Check(operationOwner.Join(TimeSpan.FromSeconds(2)) && await standalone == 17,
        "lifecycle-to-operation boundary completes after operation is released");
}

// Simulate the Windows host retaining lifecycle across driver preparation and
// the first SessionGuard marker. Ordinary maintenance must wait, while the
// a protected *-held child is authorized separately by the one-time handoff.
var gapLifecycle = @"Local\TabLink.Lifecycle.Gap.Tests." + Guid.NewGuid().ToString("N");
var gapOperation = @"Local\TabLink.Operation.Gap.Tests." + Guid.NewGuid().ToString("N");
using var gapOwned = new ManualResetEventSlim();
using var releaseGap = new ManualResetEventSlim();
var lifecycleOwner = new Thread(() =>
{
    using var mutex = new Mutex(false, gapLifecycle);
    mutex.WaitOne();
    gapOwned.Set();
    releaseGap.Wait();
    mutex.ReleaseMutex();
});
lifecycleOwner.Start();
Check(gapOwned.Wait(TimeSpan.FromSeconds(2)), "host owns lifecycle during prepare-to-marker gap");
var maintenance = Task.Run(() => DisplayMutationBoundary.RunStandalone(gapLifecycle, gapOperation,
    TimeSpan.FromSeconds(5), () => 23));
Check(!maintenance.Wait(TimeSpan.FromMilliseconds(200)),
    "maintenance cannot judge the prepare-to-first-marker gap idle");
releaseGap.Set();
Check(lifecycleOwner.Join(TimeSpan.FromSeconds(2)) && await maintenance == 23,
    "maintenance resumes only after host releases the complete display lifecycle");

// The receipt and handoff trees use one exact, inheritance-protected ACL
// predicate. These are in-memory descriptors only; no ProgramData path is read.
var protectedDirectoryAcl = DriverLifecycleSecurityPolicy.CreateDirectorySecurity();
var protectedFileAcl = DriverLifecycleSecurityPolicy.CreateFileSecurity();
Check(DriverLifecycleSecurityPolicy.HasExactProtectedAcl(protectedDirectoryAcl, directory: true),
    "exact protected directory ACL accepted");
Check(DriverLifecycleSecurityPolicy.HasExactProtectedAcl(protectedFileAcl, directory: false),
    "exact protected file ACL accepted");
var inheritedAcl = DriverLifecycleSecurityPolicy.CreateDirectorySecurity();
inheritedAcl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
Check(!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(inheritedAcl, directory: true),
    "ACL with inheritance enabled rejected");
var missingRightsAcl = new FileSecurity();
missingRightsAcl.SetAccessRuleProtection(true, false);
missingRightsAcl.SetOwner(DriverLifecycleSecurityPolicy.AdministratorsSid);
missingRightsAcl.AddAccessRule(new FileSystemAccessRule(DriverLifecycleSecurityPolicy.SystemSid,
    FileSystemRights.Modify, AccessControlType.Allow));
missingRightsAcl.AddAccessRule(new FileSystemAccessRule(DriverLifecycleSecurityPolicy.AdministratorsSid,
    FileSystemRights.FullControl, AccessControlType.Allow));
Check(!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(missingRightsAcl, directory: false),
    "ACL with missing rights rejected");
var wrongPropagationAcl = new DirectorySecurity();
wrongPropagationAcl.SetAccessRuleProtection(true, false);
wrongPropagationAcl.SetOwner(DriverLifecycleSecurityPolicy.AdministratorsSid);
wrongPropagationAcl.AddAccessRule(new FileSystemAccessRule(DriverLifecycleSecurityPolicy.SystemSid,
    FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
    PropagationFlags.InheritOnly, AccessControlType.Allow));
wrongPropagationAcl.AddAccessRule(new FileSystemAccessRule(DriverLifecycleSecurityPolicy.AdministratorsSid,
    FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
    PropagationFlags.None, AccessControlType.Allow));
Check(!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(wrongPropagationAcl, directory: true),
    "ACL with extra propagation rejected");
var extraPrincipalAcl = DriverLifecycleSecurityPolicy.CreateFileSecurity();
extraPrincipalAcl.AddAccessRule(new FileSystemAccessRule(
    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
Check(!DriverLifecycleSecurityPolicy.HasExactProtectedAcl(extraPrincipalAcl, directory: false),
    "ACL with extra principal and permissions rejected");

// Exercise the held-helper protocol entirely below the E-drive test output.
// The fake protection adapter deliberately performs no ACL changes, while the
// production ACL predicate above is tested independently.
var handoffParent = Path.Combine(AppContext.BaseDirectory, "test-artifacts");
var handoffRoot = Path.Combine(handoffParent, "DriverHandoff-" + Guid.NewGuid().ToString("N"));
var handoffNow = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
var handoffStore = new DriverLifecycleHandoffStore(handoffRoot,
    new TestHandoffProtection(handoffRoot), () => handoffNow);
const string prepareHeld = "--prepare-single-display-held";
string[] prepareArguments = ["1920", "1200", "90"];
const int fakeHostPid = 4242;
const long fakeHostStart = 638949600000000000;
const string nonceA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
try
{
    var invocationRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments,
        fakeHostPid, fakeHostStart, Guid.ParseExact("11111111111111111111111111111111", "N"), nonceA);
    var invocation = HeldDriverCommandInvocation.Parse([
        prepareHeld, "1920", "1200", "90",
        "--handoff-id", invocationRecord.HandoffId.ToString("N"),
        "--handoff-nonce", invocationRecord.NonceHex,
        "--handoff-host-pid", fakeHostPid.ToString(),
        "--handoff-host-start-utc-ticks", fakeHostStart.ToString()
    ], 3);
    Check(invocation.CommandArguments.SequenceEqual(prepareArguments) &&
        invocation.Credentials.HostPid == fakeHostPid, "held helper parses only the strict credential suffix");

    handoffStore.Publish(invocationRecord);
    var retainedHost = new FakeExactHostProcessLease();
    using (handoffStore.Consume(invocation.Credentials, prepareHeld, prepareArguments,
        (pid, ticks) => pid == fakeHostPid && ticks == fakeHostStart
            ? retainedHost : throw new IOException("wrong fake host"), _ => true))
    {
        Check(!retainedHost.Disposed, "exact host process handle retained through authorized mutation scope");
    }
    Check(retainedHost.Disposed, "exact host process handle released after authorized mutation scope");
    RejectHandoff(() => handoffStore.Consume(invocation.Credentials, prepareHeld, prepareArguments,
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "consumed ticket replay");
    handoffStore.CleanupExact(invocationRecord);

    var forgedRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments, fakeHostPid, fakeHostStart,
        Guid.ParseExact("22222222222222222222222222222222", "N"), nonceA);
    handoffStore.Publish(forgedRecord);
    var forgedCredentials = new DriverLifecycleHandoffCredentials(forgedRecord.HandoffId,
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", fakeHostPid, fakeHostStart);
    RejectHandoff(() => handoffStore.Consume(forgedCredentials, prepareHeld, prepareArguments,
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "forged nonce");
    handoffStore.CleanupExact(forgedRecord);

    var expiredRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments, fakeHostPid, fakeHostStart,
        Guid.ParseExact("33333333333333333333333333333333", "N"), nonceA);
    handoffStore.Publish(expiredRecord);
    handoffNow = expiredRecord.ExpiresUtc + TimeSpan.FromSeconds(1);
    RejectHandoff(() => handoffStore.Consume(new(expiredRecord.HandoffId, expiredRecord.NonceHex,
        fakeHostPid, fakeHostStart), prepareHeld, prepareArguments,
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "expired ticket");
    handoffStore.CleanupExpiredAbandonedRecords((_, _) => false);
    RejectHandoff(() => handoffStore.Consume(new(expiredRecord.HandoffId, expiredRecord.NonceHex,
        fakeHostPid, fakeHostStart), prepareHeld, prepareArguments,
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "expired abandoned ticket cleanup");
    handoffNow = new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero);

    var changedRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments, fakeHostPid, fakeHostStart,
        Guid.ParseExact("44444444444444444444444444444444", "N"), nonceA);
    handoffStore.Publish(changedRecord);
    var changedCredentials = new DriverLifecycleHandoffCredentials(changedRecord.HandoffId,
        changedRecord.NonceHex, fakeHostPid, fakeHostStart);
    RejectHandoff(() => handoffStore.Consume(changedCredentials, prepareHeld, ["1920", "1200", "91"],
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "changed command argument");
    RejectHandoff(() => handoffStore.Consume(changedCredentials, "--remove-session-display-held", [],
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "changed command");
    handoffStore.CleanupExact(changedRecord);

    var identityRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments, fakeHostPid, fakeHostStart,
        Guid.ParseExact("55555555555555555555555555555555", "N"), nonceA);
    handoffStore.Publish(identityRecord);
    RejectHandoff(() => handoffStore.Consume(new(identityRecord.HandoffId, identityRecord.NonceHex,
        fakeHostPid + 1, fakeHostStart), prepareHeld, prepareArguments,
        (_, _) => new FakeExactHostProcessLease(), _ => true).Dispose(), "host identity in credentials changed");
    RejectHandoff(() => handoffStore.Consume(new(identityRecord.HandoffId, identityRecord.NonceHex,
        fakeHostPid, fakeHostStart), prepareHeld, prepareArguments,
        (_, _) => throw new IOException("exact host process start mismatch"), _ => true).Dispose(),
        "exact host process handle identity mismatch");
    handoffStore.CleanupExact(identityRecord);

    var unlockedRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments, fakeHostPid, fakeHostStart,
        Guid.ParseExact("66666666666666666666666666666666", "N"), nonceA);
    handoffStore.Publish(unlockedRecord);
    RejectHandoff(() => handoffStore.Consume(new(unlockedRecord.HandoffId, unlockedRecord.NonceHex,
        fakeHostPid, fakeHostStart), prepareHeld, prepareArguments,
        (_, _) => new FakeExactHostProcessLease(), _ => false).Dispose(), "lifecycle mutex no longer held");
    handoffStore.CleanupExact(unlockedRecord);

    var concurrentRecord = handoffStore.CreateRecord(prepareHeld, prepareArguments, fakeHostPid, fakeHostStart,
        Guid.ParseExact("77777777777777777777777777777777", "N"), nonceA);
    handoffStore.Publish(concurrentRecord);
    var concurrentCredentials = new DriverLifecycleHandoffCredentials(concurrentRecord.HandoffId,
        concurrentRecord.NonceHex, fakeHostPid, fakeHostStart);
    using var startConsumers = new ManualResetEventSlim();
    Task<bool> Consumer() => Task.Run(() =>
    {
        startConsumers.Wait();
        try
        {
            using var consumed = handoffStore.Consume(concurrentCredentials, prepareHeld, prepareArguments,
                (_, _) => new FakeExactHostProcessLease(), _ => true);
            return true;
        }
        catch (InvalidDataException) { return false; }
    });
    var consumers = new[] { Consumer(), Consumer() };
    startConsumers.Set();
    var consumerResults = await Task.WhenAll(consumers);
    Check(consumerResults.Count(x => x) == 1 && consumerResults.Count(x => !x) == 1,
        "atomic move permits exactly one concurrent ticket consumer");
    handoffStore.CleanupExact(concurrentRecord);
}
finally
{
    var full = Path.GetFullPath(handoffRoot);
    var parent = Path.GetFullPath(handoffParent);
    if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(full).StartsWith("DriverHandoff-", StringComparison.Ordinal))
        throw new IOException("Refusing to delete unexpected handoff test directory");
    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
}

var leaseRoot = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "DriverLeaseScan-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(leaseRoot);
try
{
    var currentName = new string('B', 64) + ".json";
    var currentPath = Path.Combine(leaseRoot, currentName);
    void ResetLeaseRoot()
    {
        foreach (var entry in Directory.GetFileSystemEntries(leaseRoot))
            if (File.Exists(entry)) File.Delete(entry); else Directory.Delete(entry, true);
    }
    void WriteState(string path, Guid id, int pid = 101, long ticks = 202,
        bool extra = false, bool legacyReverse = false)
    {
        object state = extra
            ? new { Lease = new { LeaseId = id, DeviceName = "FAKE" }, OwnerPid = pid, OwnerStartUtcTicks = ticks,
                DeadlineUtc = DateTime.UtcNow, ReverseLeaseV2 = (object?)null, StopRequested = false, Unauthorized = true }
            : legacyReverse
                ? new { Lease = new { LeaseId = id, DeviceName = "FAKE" }, OwnerPid = pid, OwnerStartUtcTicks = ticks,
                    DeadlineUtc = DateTime.UtcNow, ReverseLeaseV2 = (object?)null, StopRequested = false,
                    ReverseLease = new { Id = Guid.NewGuid(), Serial = "LEGACY-IGNORED" } }
            : (object)new { Lease = new { LeaseId = id, DeviceName = "FAKE" }, OwnerPid = pid, OwnerStartUtcTicks = ticks,
                DeadlineUtc = DateTime.UtcNow, ReverseLeaseV2 = (object?)null, StopRequested = false };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(state));
    }
    string Bootstrap(Guid id) => currentPath + "." + id.ToString("N") + ".initial.json";
    void WriteGeneration(Guid id, Guid? marker, int pid = 101, long ticks = 202)
    {
        WriteState(currentPath, id, pid, ticks);
        WriteState(Bootstrap(id), id, pid, ticks);
        if (marker.HasValue) File.WriteAllText(currentPath + ".lease-id",
            System.Text.Json.JsonSerializer.Serialize(marker.Value));
    }
    void Scan(Func<int, long, DisplayConfigurationActivity.LeaseOwnerStatus> owner,
        Action<string>? verifier = null) => DisplayConfigurationActivity.AssertProtectedLeaseDirectoryIdle(
            leaseRoot, verifier ?? (_ => { }), owner);
    void RejectLease(Action action, string reason)
    {
        try { action(); throw new Exception("Expected lease rejection: " + reason); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { assertions++; }
    }

    var retiredId = Guid.NewGuid();
    WriteGeneration(retiredId, Guid.Empty);
    var verified = new List<string>();
    Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Running, verified.Add);
    Check(verified.Count == 3, "explicit empty marker accepts one coherent retired triplet and verifies every state file");

    ResetLeaseRoot();
    var retiredWithExtraId = Guid.NewGuid();
    var extraBootstrapId = Guid.NewGuid();
    WriteGeneration(retiredWithExtraId, Guid.Empty, pid: 111, ticks: 222);
    WriteState(Bootstrap(extraBootstrapId), extraBootstrapId, pid: 333, ticks: 444);
    RejectLease(() => Scan((pid, _) => pid == 333
        ? DisplayConfigurationActivity.LeaseOwnerStatus.Running
        : DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "retired marker cannot hide an extra bootstrap with a running owner");
    Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited);
    assertions++;

    ResetLeaseRoot();
    var liveId = Guid.NewGuid();
    WriteGeneration(liveId, liveId);
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Running),
        "marker-referenced live owner");
    Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited);
    assertions++;

    ResetLeaseRoot();
    var oldId = Guid.NewGuid();
    var uncommittedId = Guid.NewGuid();
    WriteGeneration(oldId, oldId, pid: 303, ticks: 404);
    WriteState(currentPath, uncommittedId, pid: 505, ticks: 606);
    WriteState(Bootstrap(uncommittedId), uncommittedId, pid: 505, ticks: 606);
    Scan((pid, _) => pid is 303 or 505 ? DisplayConfigurationActivity.LeaseOwnerStatus.Exited
        : DisplayConfigurationActivity.LeaseOwnerStatus.Unverified);
    assertions++;
    RejectLease(() => Scan((pid, _) => pid == 505 ? DisplayConfigurationActivity.LeaseOwnerStatus.Running
        : DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "new current committed before marker still has a live owner");

    ResetLeaseRoot();
    var noMarkerId = Guid.NewGuid();
    WriteGeneration(noMarkerId, marker: null, pid: 707, ticks: 808);
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Unverified),
        "missing marker with unverifiable owner");
    Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited);
    assertions++;

    ResetLeaseRoot();
    File.WriteAllText(currentPath + ".lease-id", System.Text.Json.JsonSerializer.Serialize(Guid.Empty));
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited), "orphan marker");

    ResetLeaseRoot();
    var malformedId = Guid.NewGuid();
    File.WriteAllText(Bootstrap(malformedId), "{broken-json");
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited), "malformed orphan bootstrap");

    ResetLeaseRoot();
    WriteGeneration(Guid.NewGuid(), Guid.Empty);
    File.WriteAllText(Path.Combine(leaseRoot, ".display-lease-deadbeef.tmp"), "partial");
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited), "unauthorized extra state shape");

    ResetLeaseRoot();
    var extraId = Guid.NewGuid();
    WriteState(currentPath, extraId, extra: true);
    WriteState(Bootstrap(extraId), extraId);
    File.WriteAllText(currentPath + ".lease-id", System.Text.Json.JsonSerializer.Serialize(Guid.Empty));
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited), "extra authorization field");

    ResetLeaseRoot();
    WriteGeneration(Guid.NewGuid(), Guid.Empty);
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited,
        _ => throw new UnauthorizedAccessException("injected ACL failure")), "state file ACL failure");

    ResetLeaseRoot();
    var protectedLegacyReverseId = Guid.NewGuid();
    WriteState(currentPath, protectedLegacyReverseId, legacyReverse: true);
    WriteState(Bootstrap(protectedLegacyReverseId), protectedLegacyReverseId);
    File.WriteAllText(currentPath + ".lease-id",
        System.Text.Json.JsonSerializer.Serialize(Guid.Empty));
    RejectLease(() => Scan((_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "protected ProgramData-shaped scanner must reject legacy ReverseLease");
}
finally
{
    var full = Path.GetFullPath(leaseRoot);
    var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts"));
    if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(full).StartsWith("DriverLeaseScan-", StringComparison.Ordinal))
        throw new IOException("Refusing to delete unexpected lease-scan test directory");
    Directory.Delete(full, true);
    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
}

var legacyLeaseRoot = Path.Combine(AppContext.BaseDirectory, "test-artifacts",
    "LegacyLeaseScan-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(legacyLeaseRoot);
try
{
    var legacyStatePath = Path.Combine(legacyLeaseRoot, "active-display-lease.json");
    var legacyMarkerPath = legacyStatePath + ".lease-id";
    const int legacyOwnerPid = 1201;
    const long legacyOwnerTicks = 638949600000001201;
    var legacyLeaseId = Guid.NewGuid();

    void WriteLegacy(string json, Guid marker)
    {
        File.WriteAllText(legacyStatePath, json);
        File.WriteAllText(legacyMarkerPath, System.Text.Json.JsonSerializer.Serialize(marker));
    }
    string LegacyJson(Guid id, string extra = "") => $$"""
        {"Lease":{"LeaseId":"{{id:D}}","DeviceName":"LEGACY"},"OwnerPid":{{legacyOwnerPid}},"OwnerStartUtcTicks":{{legacyOwnerTicks}},"DeadlineUtc":"2026-10-01T00:00:00Z","ReverseLease":{"Id":"{{Guid.NewGuid():D}}","Serial":"LEGACY-IGNORED"},"StopRequested":false{{extra}}}
        """;

    var acceptedJson = LegacyJson(legacyLeaseId);
    WriteLegacy(acceptedJson, legacyLeaseId);
    var beforeState = File.ReadAllBytes(legacyStatePath);
    var beforeMarker = File.ReadAllBytes(legacyMarkerPath);
    var ownerChecks = 0;
    DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(legacyLeaseRoot, (pid, ticks) =>
    {
        Check(pid == legacyOwnerPid && ticks == legacyOwnerTicks,
            "legacy read-only scan uses the recorded owner identity");
        ownerChecks++;
        return DisplayConfigurationActivity.LeaseOwnerStatus.Exited;
    });
    Check(ownerChecks == 1, "legacy ReverseLease with matching marker and exited owner is accepted");
    Check(File.Exists(legacyStatePath) && File.Exists(legacyMarkerPath) &&
        File.ReadAllBytes(legacyStatePath).SequenceEqual(beforeState) &&
        File.ReadAllBytes(legacyMarkerPath).SequenceEqual(beforeMarker),
        "legacy compatibility scan neither rewrites nor deletes old state");

    WriteLegacy(acceptedJson, Guid.Empty);
    var emptyMarkerState = File.ReadAllBytes(legacyStatePath);
    var emptyMarker = File.ReadAllBytes(legacyMarkerPath);
    DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(legacyLeaseRoot,
        (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited);
    Check(File.ReadAllBytes(legacyStatePath).SequenceEqual(emptyMarkerState) &&
        File.ReadAllBytes(legacyMarkerPath).SequenceEqual(emptyMarker),
        "empty legacy marker with exited owner is accepted without changing either file");
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Running),
        "empty legacy marker cannot hide a running owner");
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Unverified),
        "empty legacy marker cannot hide an unverifiable owner");

    var legacyV2Json = acceptedJson.Replace("\"ReverseLease\":", "\"ReverseLeaseV2\":",
        StringComparison.Ordinal);
    WriteLegacy(legacyV2Json, legacyLeaseId);
    DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(legacyLeaseRoot,
        (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited);
    assertions++;

    WriteLegacy(LegacyJson(legacyLeaseId), legacyLeaseId);

    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Running),
        "legacy owner still running");
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Unverified),
        "legacy owner identity cannot be verified");

    WriteLegacy(LegacyJson(legacyLeaseId), Guid.NewGuid());
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "legacy marker mismatch");

    WriteLegacy(LegacyJson(legacyLeaseId, ",\"UnknownAuthorization\":true"), legacyLeaseId);
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "legacy unknown root field");

    WriteLegacy(LegacyJson(legacyLeaseId, ",\"ReverseLeaseV2\":null"), legacyLeaseId);
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "legacy state cannot contain both reverse receipt field generations");

    var duplicateOwnerJson = LegacyJson(legacyLeaseId, $",\"OwnerPid\":{legacyOwnerPid}");
    WriteLegacy(duplicateOwnerJson, legacyLeaseId);
    RejectLeaseScan(() => DisplayConfigurationActivity.AssertLegacyLeaseDirectoryInactive(
        legacyLeaseRoot, (_, _) => DisplayConfigurationActivity.LeaseOwnerStatus.Exited),
        "legacy duplicate root field");
}
finally
{
    var full = Path.GetFullPath(legacyLeaseRoot);
    var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts"));
    if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(full).StartsWith("LegacyLeaseScan-", StringComparison.Ordinal))
        throw new IOException("Refusing to delete unexpected legacy lease-scan test directory");
    if (Directory.Exists(full)) Directory.Delete(full, true);
    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
}
Console.WriteLine($"PASS: {assertions} single-display driver configuration assertions; XML, isolated mutexes and E-drive injected handoff storage only, no driver or display commands executed.");

internal sealed class TestHandoffProtection : IDriverLifecycleHandoffProtection
{
    readonly string root;

    internal TestHandoffProtection(string root) =>
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public void EnsureAndVerifyRoot(string path)
    {
        if (!Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("test handoff root changed");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("test handoff root is a reparse point");
    }

    public FileStream CreateProtectedFile(string path)
    {
        VerifyDirectChild(path);
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.WriteThrough);
    }

    public void VerifyProtectedFile(string path)
    {
        VerifyDirectChild(path);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("test protected file missing or invalid");
    }

    void VerifyDirectChild(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("test handoff file escaped root");
    }
}

internal sealed class FakeExactHostProcessLease : IExactHostProcessLease
{
    internal bool Disposed { get; private set; }
    public bool IsRunning => !Disposed;
    public void Dispose() => Disposed = true;
}
