using TabLink.Windows;

var assertions = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
void Reject(Action action, string message)
{
    try { action(); throw new Exception("Expected rejection: " + message); }
    catch (IOException) { assertions++; }
}
var primary = new DisplayDesktopState("PHYSICAL", 0, 0, 2560, 1600, 32, 60, 0, 0, 0, true);
VirtualDisplayManager.ActiveDisplay Candidate(uint target, uint source, bool active = false, bool available = true) =>
    new("DISPLAY" + source, "VDD", true, true, false, @"ROOT\DISPLAY\FAKE", "MONITOR" + target,
        new(101, 0), source, new(101, 0), target, active, available);
var paths = new[] { Candidate(256, 0), Candidate(256, 1), Candidate(256, 2) };
var targets = VirtualDisplayManager.BuildTargets(paths, [primary]);
Check(targets.Count == 1 && targets.Single().Sources.Count == 3, "one configured monitor may expose several CCD source combinations but remains one target");
var first = DisplaySessionAllocator.SelectTarget(targets, [], []);
Reject(() => DisplaySessionAllocator.SelectTarget(targets, [first.Target.TargetKey], []), "the only target is already reserved");
Reject(() => DisplaySessionAllocator.SelectTarget(targets, [], targets.Single().Sources.Select(s => s.SourceKey)), "all source paths for the only target are reserved");
var reused = DisplaySessionAllocator.SelectTarget(targets, [], [], first.Target.TargetKey);
Check(reused.Target.TargetKey == first.Target.TargetKey && reused.Source.SourceKey == first.Source.SourceKey, "after release the same single target/source pair can be reused");
Reject(() => DisplaySessionAllocator.SelectTarget(targets, [first.Target.TargetKey], [], first.Target.TargetKey), "preferred occupied target never falls back to another device");
Reject(() => DisplaySessionAllocator.SelectTarget(targets, [], [], "missing-target"), "missing preferred target never silently rebinds");
var activePaths = paths.Select(p => p.TargetId == 256 && p.SourceId == 0 ? p with { IsActive = true } : p).ToArray();
var activeTargets = VirtualDisplayManager.BuildTargets(activePaths, [primary]);
Check(activeTargets.All(t => t.Sources.Single(s => s.SourceId == 0).IsInUse), "active source is marked occupied on every possible target combination");
Reject(() => DisplaySessionAllocator.SelectTarget(activeTargets, [activeTargets.Single().TargetKey], []), "no second target is available while the single display is owned");
var primaryTarget = VirtualDisplayManager.BuildTargets([Candidate(256, 0, true)], [primary with { DeviceName = "DISPLAY0" }]);
Reject(() => DisplaySessionAllocator.SelectTarget(primaryTarget, [], []), "virtual primary must not be allocated");
var unavailable = VirtualDisplayManager.BuildTargets([Candidate(256, 0, available: false)], [primary]);
Reject(() => DisplaySessionAllocator.SelectTarget(unavailable, [], []), "unavailable monitor excluded");
var cloned = VirtualDisplayManager.BuildTargets([Candidate(256, 0, true) with { IsCloned = true }], [primary]);
Reject(() => DisplaySessionAllocator.SelectTarget(cloned, [], []), "cloned active target excluded");
var ambiguous = VirtualDisplayManager.BuildTargets([Candidate(256, 0, true), Candidate(256, 1, true)], [primary]);
Reject(() => DisplaySessionAllocator.SelectTarget(ambiguous, [], []), "ambiguous target with two active sources excluded");
Check(VirtualDisplayManager.BuildTargets([Candidate(256, 0) with { IsMttDriver = false }, Candidate(257, 1) with { IsMttMonitor = false }], [primary]).Count == 0,
    "driver and monitor identity proof both required");
var epoch = VirtualDisplayManager.BuildTargets([Candidate(256, 0) with { TargetAdapterId = new(102, 0) }], [primary]);
Check(epoch[0].TargetKey != targets[0].TargetKey, "driver LUID epoch changes invalidate preferred runtime target");
var layout = primary with { DeviceName = "DISPLAY0", IsPrimary = false, X = 2560 };
DisplayLease Lease(uint target, uint source = 0) => new(Guid.NewGuid(), "DISPLAY" + source, @"ROOT\DISPLAY\FAKE", "MONITOR" + target,
    101, 0, source, 101, 0, target, layout, [primary]);
var a = Lease(256); var b = Lease(257);
Check(VirtualDisplayManager.GetTargetStorageKey(a) != VirtualDisplayManager.GetTargetStorageKey(b), "monitor guards have different storage keys on same adapter");
Check(VirtualDisplayManager.GetTargetStorageKey(a) != VirtualDisplayManager.GetTargetStorageKey(a with { TargetId = 257 }), "target ID disambiguates guards even if a driver repeats monitor paths");
Check(VirtualDisplayManager.GetTargetStorageKey(a) == VirtualDisplayManager.GetTargetStorageKey(a with { LeaseId = Guid.NewGuid(), SourceId = 2, DeviceName = "DISPLAY2" }),
    "same-target ownership transfer shares marker even if source name changes");
Check(VirtualDisplayManager.EncodeSourceModeIndex(1, 7) == 7, "legacy path uses the full source mode index");
Check(VirtualDisplayManager.EncodeSourceModeIndex(9, 7) == 0x0007ffff, "virtual-aware path packs source index above INVALID clone group");
Check(VirtualDisplayManager.EncodeSourceModeIndex(8, 0) == 0x0000ffff, "first virtual source index does not become clone group zero");
try { VirtualDisplayManager.EncodeSourceModeIndex(9, 0xffff); throw new Exception("Expected virtual index overflow rejection"); }
catch (ArgumentOutOfRangeException) { assertions++; }
VirtualDisplayManager.ValidateLifecycleInterop(); assertions++;
var monitorA = primary with { DeviceName = "VIRTUAL_A", X = 3840, Width = 1280, Height = 720, IsPrimary = false };
var monitorB = monitorA with { DeviceName = "VIRTUAL_B", X = 5120 };
var movable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VIRTUAL_A", "VIRTUAL_B" };
var layoutBefore = new[] { primary, monitorA, monitorB };
var layoutAfter = new[] { primary, monitorA with { X = 2560 }, monitorB with { X = 3840 } };
Check(VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, layoutAfter, movable), "Windows may compact only proven virtual target positions after removing a bridge display");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, [primary with { X = -10 }, layoutAfter[1], layoutAfter[2]], movable), "physical primary movement is rejected");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, [primary, layoutAfter[1] with { Width = 1920 }, layoutAfter[2]], movable), "virtual resolution changes are rejected");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, [primary, layoutAfter[1] with { Frequency = 90 }, layoutAfter[2]], movable), "virtual refresh changes are rejected");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, [primary, layoutAfter[1] with { IsPrimary = true }, layoutAfter[2]], movable), "virtual promotion to primary is rejected");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, [primary, layoutAfter[1], monitorB with { X = 2600 }], movable), "automatic relocation cannot overlap another screen");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, [primary, layoutAfter[1]], movable), "another output disappearing is rejected");
Check(!VirtualDisplayManager.IsOnlyOwnedPositionChange(layoutBefore, layoutAfter, new HashSet<string> { "VIRTUAL_A" }), "an unproven output cannot be moved");
Console.WriteLine($"PASS: {assertions} allocation snapshot/interop assertions; no display enumeration or native mutations executed.");
