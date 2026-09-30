using TabLink.Core;
using TabLink.Windows;

const string Serial = "TEST_TABLET";
var receipt = new UsbReverseLease(Guid.NewGuid(), Serial, "19D2", "0306", 123, 456);
var identity = new UsbDeviceIdentity(Serial, "19D2", "0306");
var assertions = 0;

async Task<(UsbReverseCleanupResult Result, FakeRunner Runner)> Run(string mapping = "UsbFfs tcp:27183 tcp:27183\n",
    bool running = false, DevicePolicySettings? settings = null, UsbDeviceIdentity? usb = null,
    string state = "device", bool vanishBeforeRemove = false, string? listing = null)
{
    var runner = new FakeRunner { Mapping = mapping, State = state, VanishBeforeRemove = vanishBeforeRemove, Listing = listing };
    var result = await UsbReverseLease.CleanupOwnedAsync(receipt, settings ?? new(), "fake-adb.exe",
        _ => Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>([usb ?? identity]), runner, (_, _) => running);
    return (result, runner);
}
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    assertions++;
}

var removed = await Run();
Check(removed.Result.Status == UsbReverseCleanupStatus.Removed, "owned orphan mapping removed");
Check(removed.Runner.Calls.Last().SequenceEqual(new[] { "-s", Serial, "reverse", "--remove", "tcp:27183" }), "only exact serial and port removed");
Check(removed.Runner.Calls.Count(x => x.SequenceEqual(new[] { "devices", "-l" })) == 2, "AdbClient revalidates immediately before removal");
Check(!removed.Runner.Calls.SelectMany(x => x).Any(x => x is "--remove-all" or "kill-server" or "--no-rebind"), "no global removal, server kill, or replacement");
var active = await Run(running: true);
Check(active.Result.Status == UsbReverseCleanupStatus.OwnerRunning && active.Runner.Calls.Count == 0, "live owner completely untouched");
var absent = await Run(mapping: "");
Check(absent.Result.Status == UsbReverseCleanupStatus.AlreadyAbsent && !absent.Runner.Removed, "absent mapping requires no mutation");
var changed = await Run(mapping: "UsbFfs tcp:27183 tcp:30000\n");
Check(changed.Result.Status == UsbReverseCleanupStatus.MappingChanged && !changed.Runner.Removed, "another destination is preserved");
var others = await Run(mapping: "UsbFfs tcp:27184 tcp:27183\n");
Check(others.Result.Complete && !others.Runner.Removed, "another device endpoint is preserved");
var malformed = await Run(mapping: "tcp:27183 tcp:27183\n");
Check(malformed.Result.Status == UsbReverseCleanupStatus.Failed && !malformed.Runner.Removed, "unknown list format fails closed");
var excludedSettings = new DevicePolicySettings();
excludedSettings.ExcludedDevices.Add(new() { Serial = Serial, Label = "newly excluded tablet" });
var excluded = await Run(settings: excludedSettings);
Check(excluded.Result.Status == UsbReverseCleanupStatus.PolicyBlocked && !excluded.Runner.Removed, "current exclusion blocks cleanup");
var replacedUsb = await Run(usb: identity with { Pid = "FFFF" });
Check(replacedUsb.Result.Status == UsbReverseCleanupStatus.IdentityChanged && !replacedUsb.Runner.Removed, "changed physical identity blocks cleanup");
var unauthorized = await Run(state: "unauthorized");
Check(unauthorized.Result.Status == UsbReverseCleanupStatus.PolicyBlocked && !unauthorized.Runner.Removed, "unauthorized device blocks cleanup");
var vanished = await Run(vanishBeforeRemove: true);
Check(vanished.Result.Status == UsbReverseCleanupStatus.PolicyBlocked && !vanished.Runner.Removed, "unplug between inspection and removal blocks mutation");
var duplicate = await Run(listing: $"List of devices attached\n{Serial} device\n{Serial} device\n");
Check(duplicate.Result.Status == UsbReverseCleanupStatus.DeviceUnavailable && !duplicate.Runner.Removed, "ambiguous serial blocks mutation");
Console.WriteLine($"PASS: {assertions} USB reverse ownership and policy assertions; no real ADB or devices used.");

sealed class FakeRunner : IAdbProcessRunner
{
    public string Mapping = "";
    public string State = "device";
    public string? Listing;
    public bool VanishBeforeRemove;
    public bool Removed;
    public List<string[]> Calls { get; } = [];
    public Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(arguments.ToArray());
        if (arguments.SequenceEqual(new[] { "devices", "-l" }))
        {
            var gone = VanishBeforeRemove && Calls.Count(x => x[0] == "devices") > 1;
            return Task.FromResult(new AdbCommandResult(0, Listing ?? (gone ? "List of devices attached\n" : $"List of devices attached\nTEST_TABLET {State} model:W202DS\n"), ""));
        }
        if (arguments.SequenceEqual(new[] { "-s", "TEST_TABLET", "reverse", "--list" }))
            return Task.FromResult(new AdbCommandResult(0, Mapping, ""));
        if (arguments.SequenceEqual(new[] { "-s", "TEST_TABLET", "reverse", "--remove", "tcp:27183" }))
        {
            Removed = true;
            return Task.FromResult(new AdbCommandResult(0, "", ""));
        }
        throw new Exception("Unexpected command: " + string.Join(' ', arguments));
    }
}
