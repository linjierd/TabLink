using System.Text.Json;
using TabLink.Core;

// Child-process modes exercise ArgumentList and timeout handling without ever executing adb.
if (args.FirstOrDefault() == "--test-process")
{
    if (args.ElementAtOrDefault(1) == "sleep") await Task.Delay(TimeSpan.FromSeconds(30));
    else Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
    return;
}

int passed = 0;
var failures = new List<string>();
var good = new AdbDevice("TEST-TABLET-SERIAL-0001", "device", "Test_Tablet");
IReadOnlyList<UsbDeviceIdentity> inventory = [new("TEST-TABLET-SERIAL-0001", "18D1", "4EE7")];
string temporary = Path.Combine(Path.GetTempPath(), "TabLink-Core-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    await Test("Default exclusions contain only the two F50 VID/PID identities", () =>
    {
        var settings = new DevicePolicySettings();
        Assert(settings.ExcludedDevices.Count == 2);
        Assert(settings.ExcludedDevices.All(rule => string.IsNullOrWhiteSpace(rule.Serial)));
        Assert(settings.ExcludedDevices.Any(rule => rule.Vid == "19D2" && rule.Pid == "0246"));
        Assert(settings.ExcludedDevices.Any(rule => rule.Vid == "19D2" && rule.Pid == "0621"));
        var fictionalF50Serial = "TEST-F50-SERIAL-0001";
        Assert(new DevicePolicy(settings).Evaluate(
            new(fictionalF50Serial, "device"), [new(fictionalF50Serial, "18D1", "2D00")]).Allowed);
    });
    await Test("VID/PID exclusion overrides an otherwise valid tablet", () =>
    {
        var policy = new DevicePolicy(new());
        Assert(!policy.Evaluate(good, [new(good.Serial, "19d2", "0246")]).Allowed);
        Assert(!policy.Evaluate(good, [new(good.Serial, "19D2", "0621")]).Allowed);
    });
    await Test("Serial exclusions ignore case and have deny priority", () =>
    {
        var settings = new DevicePolicySettings();
        settings.ExcludedDevices.Add(new() { Serial = "test-tablet-serial-0001", Label = "User excluded" });
        Assert(!new DevicePolicy(settings).Evaluate(good, inventory).Allowed);
    });
    await Test("ADB USB hint cannot replace Windows USB inventory", () =>
    {
        var hinted = good with { Details = "usb:1-2 model:Test_Tablet" };
        var policy = new DevicePolicy(new());
        Assert(!policy.Evaluate(hinted, []).Allowed);
        Assert(!policy.Evaluate(hinted, [new("OTHER", "18D1", "4EE7")]).Allowed);
        Assert(policy.Evaluate(good, inventory).Allowed); // Windows ADB often has no usb: field.
    });
    await Test("Network, emulator, unauthorized and ambiguous identities denied", () =>
    {
        var policy = new DevicePolicy(new());
        foreach (var serial in new[] { "192.168.1.2:5555", "emulator-5554", "adb-abc._adb-tls-connect._tcp" })
            Assert(!policy.Evaluate(new(serial, "device"), [new(serial, "18D1", "4EE7")]).Allowed);
        Assert(!policy.Evaluate(good with { State = "unauthorized" }, inventory).Allowed);
        Assert(!policy.Evaluate(good, [new("TEST-TABLET-SERIAL-0001", "18D1", "4EE7"), new("TEST-TABLET-SERIAL-0001", "1234", "0001")]).Allowed);
    });
    await Test("Parse devices filters network but retains unauthorized and offline USB entries", () =>
    {
        var parsed = AdbClient.ParseDevices("List of devices attached\nTEST-TABLET-SERIAL-0001 device product:p model:Test_Tablet transport_id:7\n192.168.1.2:5555 device\nemulator-5554 device\nadb-abc._adb-tls-connect._tcp device\nWAIT unauthorized\nOFF offline\n");
        Assert(parsed.Count == 3 && parsed[0].Serial == "TEST-TABLET-SERIAL-0001" && parsed[0].Model == "Test_Tablet" && parsed[0].TransportId == "7");
        Assert(parsed[1].Serial == "WAIT" && parsed[1].State == "unauthorized" && parsed[2].Serial == "OFF" && parsed[2].State == "offline");
        foreach (var device in parsed.Skip(1))
            Assert(!new DevicePolicy(new()).Evaluate(device, [new(device.Serial, "18D1", "4EE7")]).Allowed);
    });
    await Test("Settings roundtrip preserves exclusions and configurable adb path", () =>
    {
        var store = new SettingsStore(Path.Combine(temporary, "settings.json"));
        var settings = store.Load();
        Assert(settings.ExcludedDevices.Count == 2);
        settings.AdbPath = @"C:\Android SDK\platform-tools\adb.exe";
        settings.ExcludedDevices.Add(new() { Serial = "TEST-USER-EXCLUDED-0001", Label = "Keep untouched" });
        store.Save(settings);
        var read = store.Load();
        Assert(read.AdbPath == settings.AdbPath && read.ExcludedDevices.Count == 3 && read.ExcludedDevices[2].Serial == "TEST-USER-EXCLUDED-0001");
    });
    await Test("Damaged and incomplete settings fail closed", () =>
    {
        var path = Path.Combine(temporary, "broken.json");
        foreach (var json in new[] { "{", "{}", "null", "{\"SchemaVersion\":1,\"ExcludedDevices\":null}",
            "{\"SchemaVersion\":2,\"ExcludedDevices\":[]}",
            "{\"SchemaVersion\":1,\"ExcludedDevices\":[{\"Vid\":\"19D2\"}]}",
            "{\"SchemaVersion\":1,\"ExcludedDevices\":[],\"excludedDevices\":[]}",
            "{\"SchemaVersion\":1,\"ExcludedDevices\":[{\"Serial\":\"A\",\"serial\":\"B\"}]}" })
        {
            File.WriteAllText(path, json);
            Throws<SettingsLoadException>(() => new SettingsStore(path).Load());
            Assert(File.ReadAllText(path) == json); // Loading must not rewrite the user's rules.
        }
    });
    await Test("Invalid in-memory settings block approval", () =>
    {
        var settings = new DevicePolicySettings();
        settings.ExcludedDevices.Add(new() { Vid = "19D2", Pid = "invalid" });
        Assert(!new DevicePolicy(settings).Evaluate(good, inventory).Allowed);
    });
    await TestAsync("All target commands use explicitly approved serial and bounded reverse", async () =>
    {
        var runner = new FakeRunner();
        var policy = new DevicePolicy(new());
        var client = Client(policy, runner);
        var approved = await client.ApproveAsync(good);
        await client.ReversePortAsync(approved);
        await client.LaunchAsync(approved, "0123456789abcdef0123456789abcdef");
        await client.RemoveReverseAsync(approved);
        var targets = runner.Calls.Where(x => x[0] != "devices").ToArray();
        Assert(targets.Length == 3 && targets.All(x => x[0] == "-s" && x[1] == "TEST-TABLET-SERIAL-0001"));
        Assert(targets[0].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "reverse", "--no-rebind", "tcp:27183", "tcp:27183" }));
        Assert(targets[2].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "reverse", "--remove", "tcp:27183" }));
        Assert(!runner.Calls.SelectMany(x => x).Any(x => x is "kill-server" or "tcpip" or "--remove-all"));
    });
    await TestAsync("Disconnect and hardware change revoke target execution", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        var approved = policy.Approve(good, inventory);
        var client = new AdbClient("fake-adb", policy, _ => Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>([]), runner);
        await ThrowsAsync<DevicePolicyException>(() => client.ReversePortAsync(approved));
        Assert(runner.Calls.All(x => x[0] == "devices"));
        var changed = new AdbClient("fake-adb", policy, _ => Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>([new("TEST-TABLET-SERIAL-0001", "18D1", "2D00")]), runner);
        await ThrowsAsync<DevicePolicyException>(() => changed.ReversePortAsync(approved));
    });
    await TestAsync("Policy changes and approvals from another policy are rejected", async () =>
    {
        var settings = new DevicePolicySettings();
        var policy = new DevicePolicy(settings);
        var approved = policy.Approve(good, inventory);
        var runner = new FakeRunner();
        settings.ExcludedDevices.Add(new() { Serial = "OTHER", Label = "new rule" });
        await ThrowsAsync<DevicePolicyException>(() => Client(policy, runner).ReversePortAsync(approved));
        await ThrowsAsync<DevicePolicyException>(() => Client(new DevicePolicy(new()), runner).ReversePortAsync(approved));
        Assert(runner.Calls.All(x => x[0] == "devices"));
    });
    await TestAsync("Injection strings and non-TabLink ports are rejected", async () =>
    {
        var policy = new DevicePolicy(new());
        Assert(!policy.Evaluate(new("TEST-TABLET-SERIAL-0001; whoami", "device"), inventory).Allowed);
        var runner = new FakeRunner();
        var client = Client(policy, runner);
        var approved = policy.Approve(good, inventory);
        await ThrowsAsync<ArgumentException>(() => client.LaunchAsync(approved, "validtoken1234567;reboot"));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReversePortAsync(approved, 5555));
        Assert(runner.Calls.Count == 0);
    });
    await TestAsync("APK path is one literal argument including spaces and metacharacters", async () =>
    {
        var apk = Path.Combine(temporary, "client & sample.apk");
        File.WriteAllText(apk, "test-only");
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        await Client(policy, runner).InstallApkAsync(policy.Approve(good, inventory), apk);
        Assert(runner.Calls.Last().SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "install", "-r", apk }));
    });
    await TestAsync("Process ArgumentList preserves metacharacters literally", async () =>
    {
        var runner = new AdbProcessRunner();
        string[] values = ["space value", "& echo unexpected", "$(injection)", "quote\"value"];
        var result = await runner.RunAsync(Environment.ProcessPath!, new[] { "--test-process", "echo" }.Concat(values).ToArray(), TimeSpan.FromSeconds(5), default);
        var echoed = JsonSerializer.Deserialize<string[]>(result.StandardOutput)!;
        Assert(result.ExitCode == 0 && echoed.SequenceEqual(new[] { "echo" }.Concat(values)));
    });
    await TestAsync("Process timeout terminates only invoked test child", async () =>
    {
        await ThrowsAsync<TimeoutException>(() => new AdbProcessRunner().RunAsync(Environment.ProcessPath!, ["--test-process", "sleep"], TimeSpan.FromMilliseconds(250), default));
    });
    await TestAsync("Process cancellation propagates as cancellation", async () =>
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await ThrowsAsync<OperationCanceledException>(() => new AdbProcessRunner().RunAsync(Environment.ProcessPath!, ["--test-process", "sleep"], TimeSpan.FromSeconds(10), cancellation.Token));
    });
    await TestAsync("Nonzero adb exit is surfaced without a second target command", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetExitCode = 1 };
        await ThrowsAsync<AdbCommandException>(() => Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory)));
        Assert(runner.Calls.Count == 2);
    });
    await TestAsync("Android launch errors fail even when adb returns exit zero", async () =>
    {
        foreach (var error in new[] { "Starting: Intent\nError: Activity class {com.tablink.client/.MainActivity} does not exist.",
            "java.lang.SecurityException: Permission Denial", "Error type 3" })
        {
            var policy = new DevicePolicy(new());
            var runner = new FakeRunner { TargetStandardOutput = error };
            await ThrowsAsync<AdbCommandException>(() => Client(policy, runner).LaunchAsync(policy.Approve(good, inventory), "0123456789abcdef"));
            Assert(runner.Calls.Count == 2);
        }
    });
    await TestAsync("Android launch stderr exception also fails with exit zero", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardError = "Exception occurred while executing 'start':" };
        await ThrowsAsync<AdbCommandException>(() => Client(policy, runner).LaunchAsync(policy.Approve(good, inventory), "0123456789abcdef"));
    });
    await TestAsync("Existing reverse mapping is neither replaced nor automatically removed", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetExitCode = 1, TargetStandardError = "error: cannot rebind existing socket" };
        try { await Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory)); throw new Exception("Expected reservation failure."); }
        catch (AdbCommandException ex) { Assert(ex.Message.Contains("explicitly remove", StringComparison.Ordinal)); }
        Assert(runner.Calls.Count == 2 && runner.Calls.Last().Contains("--no-rebind"));
        Assert(!runner.Calls.SelectMany(x => x).Any(x => x is "--remove" or "--remove-all"));
    });
    await TestAsync("Approval cannot execute after device becomes unauthorized or offline", async () =>
    {
        foreach (var state in new[] { "unauthorized", "offline" })
        {
            var policy = new DevicePolicy(new());
            var runner = new FakeRunner { DeviceState = state };
            await ThrowsAsync<DevicePolicyException>(() => Client(policy, runner).ReversePortAsync(policy.Approve(good, inventory)));
            Assert(runner.Calls.Count == 1 && runner.Calls[0][0] == "devices");
        }
    });
    await Test("Tablet native portrait chooses supported 90 Hz over active 60 Hz", () =>
    {
        var profile = TabletDisplayProfile.Parse(DisplayProfileJson());
        Assert(profile.Width == 1200 && profile.Height == 1920 && profile.Rotation == 0);
        Assert(profile.NativeWidth == 1200 && profile.NativeHeight == 1920 && profile.RefreshRate == 60);
        Assert(profile.RequestedRefreshRate == 90 && profile.SupportedModes.Count == 3);
        // A faster unrelated resolution must not raise this panel's requested Hz.
        Assert(profile.SupportedModes.Any(m => m.RefreshRate == 120));
    });
    await Test("Landscape rotation keeps native panel identity and supported rate", () =>
    {
        var profile = TabletDisplayProfile.Parse(DisplayProfileJson(landscape: true));
        Assert(profile.Width == 1920 && profile.Height == 1200 && profile.Rotation == 1);
        Assert(profile.NativeWidth == 1200 && profile.NativeHeight == 1920 && profile.RequestedRefreshRate == 90);
    });
    await Test("Malformed tablet profiles fail closed before display configuration", () =>
    {
        foreach (var json in new[] { "{", "null", "{}", new string('x', 65537),
            DisplayProfileJson().Replace("\"rotation\":0", "\"rotation\":4"),
            DisplayProfileJson().Replace("\"width\":1200", "\"width\":1201"),
            DisplayProfileJson().Replace("\"refreshRate\":60", "\"refreshRate\":0"),
            DisplayProfileJson().Replace("\"nativeWidth\":1200", "\"nativeWidth\":1440"),
            DisplayProfileJson().Replace("\"height\":1920", "\"height\":9000"),
            "{\"width\":1200,\"height\":1920,\"rotation\":0,\"refreshRate\":60,\"nativeWidth\":1200,\"nativeHeight\":1920,\"supportedModes\":null}" })
        {
            try { TabletDisplayProfile.Parse(json); throw new Exception("Malformed profile was accepted: " + json[..Math.Min(json.Length, 100)]); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        }
    });
    await TestAsync("Display provider query targets only the approved USB serial", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Row: 0 json=" + DisplayProfileJson() + "\n" };
        var result = await Client(policy, runner).ReadDisplayProfileAsync(policy.Approve(good, inventory));
        Assert(result.RequestedRefreshRate == 90);
        var target = runner.Calls.Where(c => c[0] != "devices").Single();
        Assert(target.SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "shell", "content", "query", "--uri", "content://com.tablink.client.display/capabilities", "--user", "0" }));
    });
    await TestAsync("Stopped provider fallback launches only TabLink and requeries approved serial", async () =>
    {
        var policy = new DevicePolicy(new());
        var queries = 0;
        var runner = new FakeRunner
        {
            TargetHandler = command => command.Contains("query")
                ? new(0, ++queries == 1 ? "No result found." : "Row: 0 json=" + DisplayProfileJson(true), "")
                : new(0, "Starting: Intent { cmp=com.tablink.client/.MainActivity }", "")
        };
        var result = await Client(policy, runner).ReadDisplayProfileAsync(policy.Approve(good, inventory));
        var targets = runner.Calls.Where(c => c[0] != "devices").ToArray();
        Assert(queries == 2 && targets.Length == 3 && result.Rotation == 1);
        Assert(targets.All(c => c[0] == "-s" && c[1] == "TEST-TABLET-SERIAL-0001"));
        Assert(targets[0].SequenceEqual(targets[2]));
        Assert(targets[1].SequenceEqual(new[] { "-s", "TEST-TABLET-SERIAL-0001", "shell", "am", "start", "-n", "com.tablink.client/.MainActivity", "--ez", "profileOnly", "true" }));
        Assert(runner.Calls.Count(c => c[0] == "devices") == 3);
    });
    await TestAsync("Display provider fallback rechecks USB approval before activation", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner();
        runner.TargetHandler = _ => { runner.DeviceState = "offline"; return new(0, "No result found.", ""); };
        await ThrowsAsync<DevicePolicyException>(() => Client(policy, runner).ReadDisplayProfileAsync(policy.Approve(good, inventory)));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 1 && !runner.Calls.Any(c => c.Contains("am")));
    });
    await TestAsync("Malformed provider result does not trigger activation or guess a display mode", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetStandardOutput = "Row: 0 json={}" };
        await ThrowsAsync<InvalidDataException>(() => Client(policy, runner).ReadDisplayProfileAsync(policy.Approve(good, inventory)));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 1);
    });
    await TestAsync("Provider fallback surfaces Android activation errors", async () =>
    {
        var policy = new DevicePolicy(new());
        var runner = new FakeRunner { TargetHandler = command => command.Contains("query")
            ? new(0, "No result found.", "") : new(0, "Error type 3\nActivity class does not exist", "") };
        await ThrowsAsync<AdbCommandException>(() => Client(policy, runner).ReadDisplayProfileAsync(policy.Approve(good, inventory)));
        Assert(runner.Calls.Count(c => c[0] != "devices") == 2);
    });
}
finally
{
    // Delete only this test's freshly generated temporary directory.
    Directory.Delete(temporary, recursive: true);
}
Console.WriteLine($"{passed} passed; {failures.Count} failed. No real adb or USB device was accessed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

AdbClient Client(DevicePolicy policy, FakeRunner runner) => new("fake-adb", policy, _ => Task.FromResult(inventory), runner);
Task Test(string name, Action action) => TestAsync(name, () => { action(); return Task.CompletedTask; });
async Task TestAsync(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex); Console.WriteLine("FAIL " + name); }
}
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

static string DisplayProfileJson(bool landscape = false) => JsonSerializer.Serialize(new
{
    width = landscape ? 1920 : 1200, height = landscape ? 1200 : 1920, rotation = landscape ? 1 : 0,
    activeModeId = 1, refreshRate = 60, nativeWidth = 1200, nativeHeight = 1920,
    supportedModes = new[] { new { width = 1200, height = 1920, refreshRate = 60, modeId = 1 },
        new { width = 1200, height = 1920, refreshRate = 90, modeId = 2 },
        new { width = 800, height = 1280, refreshRate = 120, modeId = 3 } }
});

sealed class FakeRunner : IAdbProcessRunner
{
    public List<string[]> Calls { get; } = [];
    public int TargetExitCode { get; set; }
    public string DeviceState { get; set; } = "device";
    public string TargetStandardOutput { get; set; } = "";
    public string TargetStandardError { get; set; } = "";
    public Func<string[], AdbCommandResult>? TargetHandler { get; set; }
    public Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(arguments.ToArray());
        return Task.FromResult(arguments[0] == "devices"
            ? new AdbCommandResult(0, $"List of devices attached\nTEST-TABLET-SERIAL-0001 {DeviceState} model:Test_Tablet transport_id:1\n", "")
            : TargetHandler?.Invoke(arguments.ToArray()) ?? new AdbCommandResult(TargetExitCode, TargetStandardOutput, TargetStandardError));
    }
}
