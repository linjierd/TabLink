using TabLink.Core;
using TabLink.Windows;

internal static class Program
{
    const string OriginalSerial = "TABLINK_RECOVERY_TEST_001";
    const string OriginalToken = "RecoveryToken_0123456789";
    static readonly UsbDeviceIdentity OriginalIdentity = new(OriginalSerial, "1234", "5678");
    static readonly AdbReverseEndpoint Endpoint = new(54321);
    static readonly List<OfflineAdbRunner> ObservedRunners = [];
    static readonly List<string> Failures = [];
    static int assertions;

    static async Task Main()
    {
        await TestAsync("missing mapping is rebuilt exactly and the original token reconnects", MissingMappingRebuildsAndLaunchesOriginalTokenAsync);
        await TestAsync("existing owned mapping reconnects without rebind", ExistingOwnedMappingDoesNotRebindAsync);
        await TestAsync("existing unowned mapping is terminal", ExistingUnownedMappingFailsClosedAsync);
        await TestAsync("conflicting and malformed mappings retire ownership without mutation", ConflictAndMalformedMappingsFailClosedAsync);
        await TestAsync("empty and offline inventories are retryable", AbsentAndOfflineDevicesAreRetryableAsync);
        await TestAsync("identity and exclusion changes fail closed", IdentityAndExclusionChangesFailClosedAsync);
        await TestAsync("stop cancels blocked inspection without rebind or launch", StopCancelsBlockedInspectionAsync);
        await TestAsync("stop drains an in-flight reverse mutation through receipt publication", StopDrainsInFlightReverseMutationAsync);
        await TestAsync("publish failure after creation is terminal and records the callback", PublishFailureAfterCreationIsTerminalAsync);
        await TestAsync("stop drains initial route publication and Android launch", StopDrainsInitialSetupAsync);
        await TestAsync("recovery gate is single-flight with bounded backoff", RecoveryGateIsSingleFlightWithBoundedBackoffAsync);
        await TestAsync("all commands remain scoped and avoid destructive adb verbs", AllCommandsStayScopedAndNonDestructiveAsync);

        if (Failures.Count != 0)
            throw new Exception(string.Join(Environment.NewLine, Failures));
        Console.WriteLine($"PASS: {assertions} USB recovery assertions; all ADB activity was handled by an offline fake.");
    }

    static async Task StopDrainsInitialSetupAsync()
    {
        var barrier = new UsbSessionSetupBarrier();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiptPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLaunchCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchCompleted = false;

        var setup = barrier.RunAsync(async _ =>
        {
            entered.TrySetResult();
            await allowReceipt.Task;
            receiptPublished.TrySetResult();
            await allowLaunchCompletion.Task;
            launchCompleted = true;
        }, CancellationToken.None);
        await entered.Task;
        var stopDrain = barrier.DrainAsync();
        Check(barrier.InFlight && !stopDrain.IsCompleted,
            "stop observes and waits for the published initial USB setup slot");

        allowReceipt.TrySetResult();
        await receiptPublished.Task;
        Check(!stopDrain.IsCompleted && !launchCompleted,
            "stop cannot snapshot immediately after route receipt publication while launch is still active");

        allowLaunchCompletion.TrySetResult();
        await Task.WhenAll(setup, stopDrain);
        Check(launchCompleted && !barrier.InFlight,
            "stop resumes only after route ownership and Android launch have both settled");

        var secondRan = false;
        await barrier.RunAsync(_ => { secondRan = true; return Task.CompletedTask; }, CancellationToken.None);
        Check(secondRan && !barrier.InFlight, "a drained setup slot can be reused by a later session");
    }

    static async Task MissingMappingRebuildsAndLaunchesOriginalTokenAsync()
    {
        var context = CreateContext(mapping: "");
        var connected = false;
        var owned = true;
        var retired = 0;
        var published = 0;
        var launchReservations = 0;
        var postLaunchPolls = 0;
        Task Delay(TimeSpan _, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Runner.LaunchCount > 0 && ++postLaunchPolls == 2)
                connected = true;
            return Task.CompletedTask;
        }

        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
            () => true, () => connected, () => owned,
            () => { retired++; owned = false; },
            () => { published++; owned = true; }, Delay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => { launchReservations++; return true; }, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.Connected, "missing mapping recovery connects");
        Check(result.RouteStatus == AdbReversePortStatus.Created, "missing mapping reports Created");
        Check(result.ClientLaunchIssued, "missing mapping recovery records the launch");
        Check(retired == 1 && published == 1 && owned, "old receipt is retired before the new receipt is published");
        Check(launchReservations == 1, "one client launch reservation is requested");
        Check(postLaunchPolls == 2, "connection is observed after a delayed post-launch poll");
        Check(context.Runner.ReverseCreateCount == 1, "one reverse mapping is created");
        Check(context.Runner.LaunchCount == 1, "one Android launch is issued");

        var targeted = context.Runner.TargetCalls;
        Check(targeted.Count == 3, "missing mapping path has exactly three targeted commands");
        Check(targeted[0].SequenceEqual(Target("reverse", "--list")), "inspection precedes all mutation");
        Check(targeted[1].SequenceEqual(Target("reverse", "--no-rebind", "tcp:54321", "tcp:27183")),
            "mapping is created with exact --no-rebind endpoints");
        Check(targeted[2].SequenceEqual(Target("shell", "am", "start", "-n", "com.tablink.client/.MainActivity",
            "--es", "token", OriginalToken, "--ei", "port", "54321")),
            "client launch uses the original session token and fixed per-session endpoint");
    }

    static async Task ExistingOwnedMappingDoesNotRebindAsync()
    {
        var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
        var connected = false;
        var retired = 0;
        var published = 0;
        var reserved = 0;
        Task Delay(TimeSpan _, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            connected = true;
            return Task.CompletedTask;
        }
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
            () => true, () => connected, () => true, () => retired++, () => published++, Delay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => { reserved++; return true; }, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.Connected, "owned existing route reconnects");
        Check(result.RouteStatus == AdbReversePortStatus.Existing, "owned existing route is reported");
        Check(!result.ClientLaunchIssued, "native reconnect does not launch the client");
        Check(context.Runner.ReverseCreateCount == 0, "owned existing route is not rebound");
        Check(context.Runner.LaunchCount == 0 && reserved == 0, "native reconnect needs no launch reservation");
        Check(retired == 0 && published == 0, "owned receipt is unchanged");
        Check(context.Runner.TargetCalls.Count == 1 && context.Runner.TargetCalls[0].SequenceEqual(Target("reverse", "--list")),
            "owned existing route performs read-only inspection only");
    }

    static async Task ExistingUnownedMappingFailsClosedAsync()
    {
        var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
        var callbacks = 0;
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
            () => true, () => false, () => false, () => callbacks++, () => callbacks++, NoDelay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => throw new Exception("terminal path must not reserve a launch"), CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, "unowned existing route is terminal");
        Check(result.RouteStatus == AdbReversePortStatus.Existing, "unowned route status is preserved");
        Check(!result.ClientLaunchIssued && context.Runner.LaunchCount == 0, "unowned route never launches");
        Check(context.Runner.ReverseCreateCount == 0 && callbacks == 0, "unowned route is never mutated or claimed");
    }

    static async Task ConflictAndMalformedMappingsFailClosedAsync()
    {
        foreach (var mapping in new[] { "UsbFfs tcp:54321 tcp:30000\n", "unexpected reverse output\n" })
        {
            var context = CreateContext(mapping: mapping);
            var retired = 0;
            var published = 0;
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => retired++, () => published++, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
                () => throw new Exception("terminal path must not reserve a launch"), CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, "conflict/malformed route is terminal");
            Check(retired == 1, "conflict/malformed route retires stale ownership");
            Check(published == 0, "conflict/malformed route does not publish ownership");
            Check(context.Runner.ReverseCreateCount == 0 && context.Runner.LaunchCount == 0,
                "conflict/malformed route has no mutation or launch");
            Check(context.Runner.TargetCalls.Count == 1 && context.Runner.TargetCalls[0].SequenceEqual(Target("reverse", "--list")),
                "conflict/malformed route stops after inspection");
        }
    }

    static async Task AbsentAndOfflineDevicesAreRetryableAsync()
    {
        var cases = new[]
        {
            CreateContext(mapping: "", deviceListing: "List of devices attached\n"),
            CreateContext(mapping: "", deviceState: "offline")
        };
        foreach (var context in cases)
        {
            var callbacks = 0;
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => callbacks++, () => callbacks++, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
                () => throw new Exception("retryable inventory failure must not reserve a launch"), CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.RetryableFailure, "absent/offline device is retryable");
            Check(result.RouteStatus is null && !result.ClientLaunchIssued, "inventory failure precedes route mutation");
            Check(callbacks == 0 && context.Runner.TargetCalls.Count == 0,
                "absent/offline device has no targeted command or ownership callback");
        }
    }

    static async Task IdentityAndExclusionChangesFailClosedAsync()
    {
        var identityChanged = CreateContext(mapping: "");
        identityChanged.Inventory.Clear();
        identityChanged.Inventory.Add(OriginalIdentity with { Pid = "FFFF" });
        await AssertPolicyFailureIsTerminalAsync(identityChanged, "changed USB identity");

        var newlyExcluded = CreateContext(mapping: "");
        newlyExcluded.Settings.ExcludedDevices.Add(new DeviceExclusionRule
            { Serial = OriginalSerial, Label = "offline test exclusion" });
        await AssertPolicyFailureIsTerminalAsync(newlyExcluded, "new exclusion rule");
    }

    static async Task AssertPolicyFailureIsTerminalAsync(TestContext context, string caseName)
    {
        var callbacks = 0;
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
            () => true, () => false, () => true, () => callbacks++, () => callbacks++, NoDelay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => throw new Exception("policy failure must not reserve a launch"), CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, caseName + " is terminal");
        Check(result.RouteStatus is null && !result.ClientLaunchIssued, caseName + " fails before route inspection");
        Check(context.Runner.TargetCalls.Count == 0 && callbacks == 0, caseName + " performs no mutation or ownership change");
    }

    static async Task StopCancelsBlockedInspectionAsync()
    {
        var context = CreateContext(mapping: "");
        context.Runner.BlockInspection = true;
        var callbacks = 0;
        using var stop = new CancellationTokenSource();
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
            () => true, () => false, () => true, () => callbacks++, () => callbacks++, NoDelay);
        var task = runner.RunAsync(TimeSpan.FromMinutes(1),
            () => throw new Exception("cancelled inspection must not reserve a launch"), stop.Token);

        await context.Runner.InspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Check(result.Status == UsbRecoveryExecutionStatus.Cancelled, "stop cancellation is reported as cancelled");
        Check(context.Runner.ReverseCreateCount == 0 && context.Runner.LaunchCount == 0,
            "cancelled inspection cannot rebind or launch");
        Check(callbacks == 0, "cancelled inspection cannot change ownership");
        Check(context.Runner.TargetCalls.Count == 1 && context.Runner.TargetCalls[0].SequenceEqual(Target("reverse", "--list")),
            "cancelled path reaches only the blocked inspection");
    }

    static async Task PublishFailureAfterCreationIsTerminalAsync()
    {
        foreach(var failure in new Func<Exception>[]
        {
            () => new IOException("offline receipt write failure"),
            () => new TimeoutException("offline receipt mutex timeout")
        })
        {
            var context = CreateContext(mapping: "");
            var retired = 0;
            var publishAttempted = 0;
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.Approved, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => retired++,
                () => { publishAttempted++; throw failure(); }, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
                () => throw new Exception("failed publish must not reserve a launch"), CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, "publish failure is terminal");
            Check(result.RouteStatus == AdbReversePortStatus.Created, "created route remains explicitly reported");
            Check(!result.ClientLaunchIssued && context.Runner.LaunchCount == 0, "publish failure never launches the client");
            Check(retired == 1 && publishAttempted == 1, "old ownership retires and created callback executes exactly once");
            Check(context.Runner.ReverseCreateCount == 1, "reverse creation succeeded before publish failed");
            Check(context.Runner.TargetCalls.Last().SequenceEqual(Target("reverse", "--no-rebind", "tcp:54321", "tcp:27183")),
                "publish failure follows the exact safe reverse creation");
        }
    }

    static async Task StopDrainsInFlightReverseMutationAsync()
    {
        var context = CreateContext(mapping: "");
        context.Runner.BlockReverse = true;
        using var stop = new CancellationTokenSource();
        var barrier = new UsbSessionSetupBarrier();
        var receiptPublished = false;
        var launchIssued = false;

        var setup = barrier.RunAsync(async ct =>
        {
            var inspected = await context.Client.InspectReversePortAsync(context.Approved, Endpoint, ct);
            Check(inspected.Status == AdbReversePortStatus.Missing,
                "initial setup confirms the random endpoint is absent before mutation");
            ct.ThrowIfCancellationRequested();
            await context.Client.ReversePortAsync(context.Approved, Endpoint, ct);
            receiptPublished = true;
            if (ct.IsCancellationRequested) return;
            launchIssued = true;
        }, stop.Token);

        await context.Runner.ReverseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var drain = barrier.DrainAsync();
        await Task.Delay(50);
        Check(!setup.IsCompleted && !drain.IsCompleted && !receiptPublished,
            "Stop cancellation cannot interrupt a reverse mutation after its final cancellation boundary");

        context.Runner.ReleaseReverse.TrySetResult(true);
        await Task.WhenAll(setup, drain).WaitAsync(TimeSpan.FromSeconds(5));
        Check(receiptPublished && !launchIssued,
            "the completed mutation publishes cleanup authority before Stop resumes and suppresses a late launch");
        Check(context.Runner.ReverseCreateCount == 1,
            "the in-flight reverse mutation completes exactly once after Stop");
    }

    static async Task RecoveryGateIsSingleFlightWithBoundedBackoffAsync()
    {
        var gate = new UsbSessionRecoveryGate();
        var now = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Check(gate.TryBegin(now, sessionActive: true, clientConnected: false) is null,
            "gate does not repair before an authenticated connection arms it");
        gate.ObserveConnected();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => gate.TryBegin(now, sessionActive: true, clientConnected: false))));
        var active = attempts.Where(x => x.HasValue).Select(x => x.GetValueOrDefault()).Single();
        Check(attempts.Count(x => x.HasValue) == 1 && gate.InFlight, "concurrent scheduling admits one in-flight attempt");

        var reservations = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => gate.TryReserveLaunch(active))));
        Check(reservations.Count(x => x) == 1, "one client launch is reserved for the active attempt");

        gate.Complete(active, now, clientConnected: false, retryable: true);
        Check(gate.Failures == 1 && gate.NextAttemptUtc == now.AddSeconds(2), "first retry backs off for two seconds");
        Check(gate.TryBegin(now.AddTicks(1), true, false) is null, "retry cannot start before its first backoff");

        var second = gate.TryBegin(now.AddSeconds(2), true, false);
        Check(second is { Number: 2 }, "second attempt starts at the first deadline");
        Check(gate.TryReserveLaunch(second!.Value) && !gate.TryReserveLaunch(second.Value),
            "second attempt receives exactly one fresh launch reservation");
        gate.Complete(second.Value, now.AddSeconds(2), clientConnected: false, retryable: true);
        Check(gate.NextAttemptUtc == now.AddSeconds(6), "second retry adds a four-second backoff");

        var third = gate.TryBegin(now.AddSeconds(6), true, false);
        Check(third is { Number: 3 }, "third bounded attempt starts at the second deadline");
        Check(gate.TryReserveLaunch(third!.Value) && !gate.TryReserveLaunch(third.Value),
            "third attempt also remains single-launch");
        gate.Complete(third!.Value, now.AddSeconds(6), clientConnected: false, retryable: true);
        Check(gate.RequiresSessionStop && gate.NextAttemptUtc == DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            "three retryable failures require the session to stop");
        Check(gate.TryBegin(now.AddYears(1), true, false) is null, "no fourth attempt can start");

        var terminalGate = new UsbSessionRecoveryGate();
        terminalGate.ObserveConnected();
        var terminal = terminalGate.TryBegin(now, true, false)!.Value;
        terminalGate.Complete(terminal, now, clientConnected: false, retryable: false);
        Check(terminalGate.Disabled && terminalGate.RequiresSessionStop, "terminal failure disables the episode immediately");
    }

    static Task AllCommandsStayScopedAndNonDestructiveAsync()
    {
        Check(ObservedRunners.Count >= 10, "all runner scenarios participate in the command audit");
        foreach (var runner in ObservedRunners)
        foreach (var call in runner.Calls)
        {
            Check(!call.Any(ForbiddenArgument), "no forbidden adb server, global reverse or tcpip argument is emitted");
            if (call.SequenceEqual(new[] { "devices", "-l" })) continue;
            Check(call.Length >= 3 && call[0] == "-s" && call[1] == OriginalSerial,
                "every target command uses only the originally approved serial");
        }
        return Task.CompletedTask;
    }

    static TestContext CreateContext(string mapping, string deviceState = "device", string? deviceListing = null)
    {
        var settings = new DevicePolicySettings { ExcludedDevices = [] };
        var policy = new DevicePolicy(settings);
        var inventory = new List<UsbDeviceIdentity> { OriginalIdentity };
        var approved = policy.Approve(new AdbDevice(OriginalSerial, "device", "Offline_Test_Tablet", "1"), inventory);
        var fake = new OfflineAdbRunner(OriginalSerial)
        {
            ReverseListOutput = mapping,
            DeviceState = deviceState,
            DevicesStandardOutput = deviceListing
        };
        Task<IReadOnlyList<UsbDeviceIdentity>> Inventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>(inventory.ToArray());
        }
        var client = new AdbClient("E:\\offline-tests\\fake-adb.exe", policy, Inventory, fake);
        ObservedRunners.Add(fake);
        return new(client, approved, fake, settings, inventory);
    }

    static Task NoDelay(TimeSpan _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    static string[] Target(params string[] command) => ["-s", OriginalSerial, .. command];

    static bool ForbiddenArgument(string argument) =>
        argument.Equals("kill-server", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("start-server", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--remove-all", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("tcpip", StringComparison.OrdinalIgnoreCase);

    static async Task TestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Failures.Add(name + ": " + ex);
            Console.WriteLine("FAIL " + name);
        }
    }

    static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("Assertion failed: " + message);
    }

    sealed record TestContext(AdbClient Client, ApprovedUsbDevice Approved, OfflineAdbRunner Runner,
        DevicePolicySettings Settings, List<UsbDeviceIdentity> Inventory);

    sealed class OfflineAdbRunner(string expectedSerial) : IAdbProcessRunner
    {
        readonly object sync = new();
        readonly List<string[]> calls = [];
        int reverseCreateCount;
        int launchCount;

        public string ReverseListOutput { get; init; } = "";
        public string DeviceState { get; init; } = "device";
        public string? DevicesStandardOutput { get; init; }
        public bool BlockInspection { get; set; }
        public bool BlockReverse { get; set; }
        public TaskCompletionSource<bool> InspectionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReverseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReverse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReverseCreateCount => Volatile.Read(ref reverseCreateCount);
        public int LaunchCount => Volatile.Read(ref launchCount);
        public IReadOnlyList<string[]> Calls { get { lock (sync) return calls.Select(x => x.ToArray()).ToArray(); } }
        public IReadOnlyList<string[]> TargetCalls => Calls.Where(x => !x.SequenceEqual(new[] { "devices", "-l" })).ToArray();

        public async Task<AdbCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = arguments.ToArray();
            ValidateSafety(call);
            lock (sync) calls.Add(call);

            if (call.SequenceEqual(new[] { "devices", "-l" }))
            {
                var listing = DevicesStandardOutput ??
                    $"List of devices attached\n{expectedSerial} {DeviceState} model:Offline_Test_Tablet transport_id:1\n";
                return new AdbCommandResult(0, listing, "");
            }
            if (call.SequenceEqual(Target("reverse", "--list")))
            {
                InspectionEntered.TrySetResult(true);
                if (BlockInspection)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AdbCommandResult(0, ReverseListOutput, "");
            }
            if (call.SequenceEqual(Target("reverse", "--no-rebind", "tcp:54321", "tcp:27183")))
            {
                ReverseEntered.TrySetResult(true);
                if (BlockReverse)
                    await ReleaseReverse.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref reverseCreateCount);
                return new AdbCommandResult(0, "", "");
            }
            if (call.SequenceEqual(Target("shell", "am", "start", "-n", "com.tablink.client/.MainActivity",
                "--es", "token", OriginalToken, "--ei", "port", "54321")))
            {
                Interlocked.Increment(ref launchCount);
                return new AdbCommandResult(0, "Starting: Intent", "");
            }
            throw new InvalidOperationException("Unexpected offline ADB command shape.");
        }

        void ValidateSafety(string[] call)
        {
            if (call.Any(ForbiddenArgument))
                throw new InvalidOperationException("Forbidden ADB command reached the offline runner.");
            if (call.SequenceEqual(new[] { "devices", "-l" })) return;
            if (call.Length < 3 || call[0] != "-s" || call[1] != expectedSerial)
                throw new InvalidOperationException("ADB target escaped the originally approved serial.");
        }
    }
}
