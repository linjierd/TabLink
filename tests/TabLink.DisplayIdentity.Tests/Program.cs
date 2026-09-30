using System.Drawing;
using TabLink.Windows;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
var primary = new DisplayDesktopState(@"\\.\DISPLAY1", 0, 0, 2560, 1600, 32, 240, 0, 0, 0, true);
var own = new DisplayDesktopState(@"\\.\DISPLAY32", 2560, 0, 1200, 1920, 32, 90, 0, 0, 0, false);
var lease = new DisplayLease(Guid.NewGuid(), own.DeviceName, @"ROOT\DISPLAY\0002", @"\\?\DISPLAY#MTT1337#original-monitor",
    101, 0, 0, 101, 0, 4, own, [primary]);
var path = new VirtualDisplayManager.ActiveDisplay(own.DeviceName, "TabLink virtual", true, true, false,
    lease.AdapterInstanceId, lease.MonitorPath, new(101, 0), 0, new(101, 0), 4, true);
DisplayRelocation Resolve(DisplayDesktopState current, params VirtualDisplayManager.ActiveDisplay[] paths) =>
    VirtualDisplayManager.ResolveLayoutSnapshot(lease, paths.Length == 0 ? [path] : paths, [primary, current]);
void Reject(Action action, string message)
{
    try { action(); throw new Exception("Expected rejection: " + message); }
    catch (IOException) { assertions++; }
}

foreach (var moved in new[] { own, own with { X = -1200, Y = -160 }, own with { X = 500, Y = 1600 }, own with { X = 0, Y = -1920 } })
{
    var current = Resolve(moved);
    Check(current.CurrentDisplay.Bounds == new Rectangle(moved.X, moved.Y, 1200, 1920)
        && current.CurrentDisplay.IsTabLinkCompatible && !current.CurrentDisplay.IsPrimary,
        "same physical identity follows positive or negative X/Y without changing size");
    Check(current.RememberedLease.LeaseId == lease.LeaseId
        && current.RememberedLease.OriginalMode.X == moved.X && current.RememberedLease.OriginalMode.Y == moved.Y,
        "remembered preference preserves ownership generation and saves moved position");
    Check(current.RememberedLease.OtherDisplays.Single() == primary, "physical primary mode remains 2560x1600 at 240 Hz");
}
Check(lease.OriginalMode == own, "relocation does not mutate the immutable active/bootstrap lease");
Reject(() => Resolve(own with { IsPrimary = true }), "virtual display became primary");
Reject(() => Resolve(own with { Width = 1920 }), "width changed");
Reject(() => Resolve(own with { Height = 1200 }), "height changed");
Reject(() => Resolve(own with { Frequency = 60 }), "refresh rate changed");
Reject(() => Resolve(own with { Orientation = 1 }), "rotation changed");
Reject(() => Resolve(own with { BitsPerPixel = 24 }), "pixel format changed");
Reject(() => Resolve(own, path with { IsCloned = true }), "clone flag set");
Reject(() => Resolve(own, path, path with { TargetId = 5 }), "two active targets share the GDI source");
Reject(() => Resolve(own, path with { IsActive = false }), "inactive output");
Reject(() => Resolve(own, path with { IsMttDriver = false }), "non-Root-MttVDD driver");
Reject(() => Resolve(own, path with { IsMttMonitor = false }), "monitor hardware proof missing");
Reject(() => Resolve(own, path with { AdapterInstanceId = @"ROOT\DISPLAY\0003" }), "driver instance replaced");
Reject(() => Resolve(own, path with { MonitorPath = @"\\?\DISPLAY#MTT1337#replacement" }), "monitor instance replaced");
Reject(() => Resolve(own, path with { AdapterId = new(102, 0) }), "source adapter LUID changed");
Reject(() => Resolve(own, path with { SourceId = 1 }), "source ID changed");
Reject(() => Resolve(own, path with { TargetAdapterId = new(102, 0) }), "target adapter LUID changed");
Reject(() => Resolve(own, path with { TargetId = 5 }), "target ID changed");
Reject(() => Resolve(own, path with { SourceName = @"\\.\DISPLAY33" }), "GDI source changed");
Reject(() => VirtualDisplayManager.ResolveLayoutSnapshot(lease, [path], [own]), "no independent primary remains");

var movedLeft = Resolve(own with { X = -1200, Y = -160 }).RememberedLease;
var fresh = lease with { LeaseId = Guid.NewGuid() };
var reused = VirtualDisplayManager.ReuseRememberedPosition(fresh, movedLeft);
Check(reused.LeaseId == fresh.LeaseId && reused.OriginalMode.X == -1200 && reused.OriginalMode.Y == -160,
    "fresh inactive proof retains its new generation but reuses verified adjacent saved position");
Check(VirtualDisplayManager.ReuseRememberedPosition(fresh with { AdapterLowPart = 102 }, movedLeft).OriginalMode == own,
    "driver restart LUID prevents reusing another identity's position");
Check(VirtualDisplayManager.ReuseRememberedPosition(fresh with { OtherDisplays = [primary with { Width = 1920 }] }, movedLeft).OriginalMode == own,
    "changed physical monitor layout rejects stale reconnect coordinates");
Check(VirtualDisplayManager.ReuseRememberedPosition(fresh, movedLeft with { OriginalMode = own with { X = 100, Y = 100 } }).OriginalMode == own,
    "saved position overlapping primary is rejected without changing primary");
Check(VirtualDisplayManager.ReuseRememberedPosition(fresh, movedLeft with { OriginalMode = own with { X = -4000, Y = 0 } }).OriginalMode == own,
    "disconnected saved desktop island is rejected");
Check(VirtualDisplayManager.ReuseRememberedPosition(fresh with { OriginalMode = own with { Width = 1920, Height = 1200 } }, movedLeft).OriginalMode.X == own.X,
    "a new larger mode cannot overlap primary merely to preserve old left-edge coordinates");
Check(VirtualDisplayManager.RememberedLayoutEquals(movedLeft, movedLeft with { LeaseId = Guid.NewGuid() }),
    "unchanged identity/mode/layout does not need a disk write solely for a different reconnect ID");
Check(!VirtualDisplayManager.RememberedLayoutEquals(lease, movedLeft), "actual X/Y movement is persisted");
Console.WriteLine($"PASS: {assertions} stable display identity/relocation assertions; pure snapshots only, no display enumeration or topology mutation.");
