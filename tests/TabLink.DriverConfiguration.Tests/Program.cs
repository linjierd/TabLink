using System.Text;
using System.Xml.Linq;
using TabLink.DriverSetup;

var assertions = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
void Reject(Action action, string message)
{
    try { action(); throw new Exception("Expected rejection: " + message); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Xml.XmlException) { assertions++; }
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
Console.WriteLine($"PASS: {assertions} single-display driver configuration assertions; XML, filename and isolated test-mutex paths only, no driver or display commands executed.");
