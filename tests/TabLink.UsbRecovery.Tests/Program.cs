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
        await TestAsync("Android user changes and malformed user output stop before route inspection", AndroidUserMismatchStopsBeforeRouteInspectionAsync);
        await TestAsync("Android user change during native reconnect wait blocks the client launch", AndroidUserChangeBeforeLaunchStopsWithoutIssuingLaunchAsync);
        await TestAsync("provider publication failure preserves exact route cleanup ownership", ProviderFailurePreservesOwnedRouteAsync);
        await TestAsync("Android user switch after publication blocks marker activation", AndroidUserSwitchAfterPublicationBlocksActivationAsync);
        await TestAsync("cancellation after publication preserves exact route cleanup ownership", CancellationAfterPublicationPreservesOwnedRouteAsync);
        await TestAsync("empty and offline inventories are retryable", AbsentAndOfflineDevicesAreRetryableAsync);
        await TestAsync("recovery detail redacts fake ADB stderr, path and serial markers", SensitiveAdbFailuresAreRedactedAsync);
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

        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => connected, () => owned,
            () => { retired++; owned = false; },
            () => { published++; owned = true; }, Delay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => { launchReservations++; return true; }, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.Connected, "missing mapping recovery connects");
        Check(result.RouteStatus == AdbReversePortStatus.Created, "missing mapping reports Created");
        Check(result.ClientLaunchCompleted, "missing mapping recovery records the completed launch");
        Check(retired == 1 && published == 1 && owned, "old receipt is retired before the new receipt is published");
        Check(launchReservations == 1, "one client launch reservation is requested");
        Check(postLaunchPolls == 2, "connection is observed after a delayed post-launch poll");
        Check(context.Runner.ReverseCreateCount == 1, "one reverse mapping is created");
        Check(context.Runner.LaunchCount == 1, "one Android launch is issued");

        var targeted = context.Runner.TargetCalls;
        Check(targeted.Count == 7, "missing mapping path has the complete seven-command recovery transaction");
        Check(targeted[0].SequenceEqual(Target("shell", "am", "get-current-user")),
            "bound Android user is checked before route inspection");
        Check(targeted[1].SequenceEqual(Target("reverse", "--list")), "inspection precedes all mutation");
        Check(targeted[2].SequenceEqual(Target("reverse", "--no-rebind", "tcp:54321", "tcp:27183")),
            "mapping is created with exact --no-rebind endpoints");
        Check(targeted[3].SequenceEqual(Target("shell", "am", "get-current-user")),
            "bound Android user is checked again immediately before launch");
        Check(IsProtectedPublication(targeted[4], context.Runner.PublishedActivation),
            "protected provider publication uses the original token, endpoint and a random marker");
        Check(targeted[5].SequenceEqual(Target("shell", "am", "get-current-user")),
            "bound Android user is rechecked after provider publication");
        Check(IsMarkerActivation(targeted[6], context.Runner.PublishedActivation),
            "exported MainActivity receives only the one-shot marker, never the token or port");
    }

    static async Task ProviderFailurePreservesOwnedRouteAsync()
    {
        const string privateMarker = "PRIVATE_PROVIDER_FAILURE";
        var context = CreateContext(mapping: "");
        context.Runner.ProviderPublicationError =
            "Error while accessing provider:\njava.lang.SecurityException: " + privateMarker;
        var connected = false;
        var owned = true;
        var retired = 0;
        var published = 0;
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => connected, () => owned,
            () => { retired++; owned = false; },
            () => { published++; owned = true; }, NoDelay);

        var result = await runner.RunAsync(TimeSpan.FromSeconds(10), () => true, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure,
            "provider response failure is terminal for the incompatible client session");
        Check(result.RouteStatus == AdbReversePortStatus.Created && !result.ClientLaunchCompleted,
            "provider response failure retains the created route and does not claim activation success");
        Check(retired == 1 && published == 1 && owned,
            "provider response failure preserves the newly published exact cleanup receipt");
        Check(context.Runner.ReverseCreateCount == 1 && context.Runner.PublicationCount == 1
                && context.Runner.LaunchCount == 0,
            "provider response failure occurs after exact route creation and before marker activation");
        Check(!result.Detail.Contains(privateMarker, StringComparison.Ordinal),
            "provider response failure does not expose raw Android output");
        Check(context.Runner.TargetCalls.Count == 5
                && IsProtectedPublication(context.Runner.TargetCalls[4], context.Runner.PublishedActivation),
            "provider failure stops immediately after the protected publication command");
    }

    static async Task AndroidUserSwitchAfterPublicationBlocksActivationAsync()
    {
        var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
        context.Runner.ChangeAndroidUserAfterPublication = true;
        var ownershipCallbacks = 0;
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => false, () => true,
            () => ownershipCallbacks++, () => ownershipCallbacks++, NoDelay);

        var result = await runner.RunAsync(TimeSpan.FromSeconds(10), () => true, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure,
            "Android user switch after publication fails closed");
        Check(result.RouteStatus == AdbReversePortStatus.Existing && !result.ClientLaunchCompleted,
            "user switch retains the existing route but never reports a client launch");
        Check(context.Runner.PublicationCount == 1 && context.Runner.LaunchCount == 0,
            "one protected value is published but its marker is never activated for another user");
        Check(ownershipCallbacks == 0,
            "user switch does not retire the valid exact cleanup receipt");
        var targeted = context.Runner.TargetCalls;
        Check(targeted.Count == 5 && IsProtectedPublication(targeted[3], context.Runner.PublishedActivation)
                && targeted[4].SequenceEqual(Target("shell", "am", "get-current-user")),
            "the second bound-user check is the last command after publication");
    }

    static async Task CancellationAfterPublicationPreservesOwnedRouteAsync()
    {
        var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
        context.Runner.BlockUserCheckAfterPublication = true;
        var ownershipCallbacks = 0;
        using var stop = new CancellationTokenSource();
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => false, () => true,
            () => ownershipCallbacks++, () => ownershipCallbacks++, NoDelay);

        var attempt = runner.RunAsync(TimeSpan.FromSeconds(10), () => true, stop.Token);
        await context.Runner.PostPublicationUserCheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var result = await attempt.WaitAsync(TimeSpan.FromSeconds(5));

        Check(result.Status == UsbRecoveryExecutionStatus.Cancelled,
            "external stop cancels a handoff between protected publication and activation");
        Check(result.RouteStatus == AdbReversePortStatus.Existing && !result.ClientLaunchCompleted,
            "cancelled handoff retains the exact route and never reports activation");
        Check(context.Runner.PublicationCount == 1 && context.Runner.LaunchCount == 0,
            "cancelled handoff publishes one short-lived value without activating its marker");
        Check(ownershipCallbacks == 0,
            "cancelled handoff preserves the valid exact cleanup receipt for Stop");
        var targeted = context.Runner.TargetCalls;
        Check(targeted.Count == 5 && IsProtectedPublication(targeted[3], context.Runner.PublishedActivation)
                && targeted[4].SequenceEqual(Target("shell", "am", "get-current-user")),
            "cancellation interrupts the post-publication user check before marker activation");
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
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => connected, () => true, () => retired++, () => published++, Delay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => { reserved++; return true; }, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.Connected, "owned existing route reconnects");
        Check(result.RouteStatus == AdbReversePortStatus.Existing, "owned existing route is reported");
        Check(!result.ClientLaunchCompleted, "native reconnect does not launch the client");
        Check(context.Runner.ReverseCreateCount == 0, "owned existing route is not rebound");
        Check(context.Runner.LaunchCount == 0 && reserved == 0, "native reconnect needs no launch reservation");
        Check(retired == 0 && published == 0, "owned receipt is unchanged");
        Check(context.Runner.TargetCalls.Count == 2 &&
            context.Runner.TargetCalls[0].SequenceEqual(Target("shell", "am", "get-current-user")) &&
            context.Runner.TargetCalls[1].SequenceEqual(Target("reverse", "--list")),
            "owned existing route validates the bound user and performs read-only inspection only");
    }

    static async Task ExistingUnownedMappingFailsClosedAsync()
    {
        var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
        var callbacks = 0;
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => false, () => false, () => callbacks++, () => callbacks++, NoDelay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => throw new Exception("terminal path must not reserve a launch"), CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, "unowned existing route is terminal");
        Check(result.RouteStatus == AdbReversePortStatus.Existing, "unowned route status is preserved");
        Check(!result.ClientLaunchCompleted && context.Runner.LaunchCount == 0, "unowned route never launches");
        Check(context.Runner.ReverseCreateCount == 0 && callbacks == 1,
            "unowned route is never mutated or claimed and any prepared intent is terminally revoked");
    }

    static async Task ConflictAndMalformedMappingsFailClosedAsync()
    {
        foreach (var mapping in new[] { "UsbFfs tcp:54321 tcp:30000\n", "unexpected reverse output\n" })
        {
            var context = CreateContext(mapping: mapping);
            var retired = 0;
            var published = 0;
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => retired++, () => published++, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
                () => throw new Exception("terminal path must not reserve a launch"), CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, "conflict/malformed route is terminal");
            Check(retired == 1, "conflict/malformed route retires stale ownership");
            Check(published == 0, "conflict/malformed route does not publish ownership");
            Check(context.Runner.ReverseCreateCount == 0 && context.Runner.LaunchCount == 0,
                "conflict/malformed route has no mutation or launch");
            Check(context.Runner.TargetCalls.Count == 2 && context.Runner.TargetCalls[1].SequenceEqual(Target("reverse", "--list")),
                "conflict/malformed route stops after inspection");
        }
    }

    static async Task AndroidUserMismatchStopsBeforeRouteInspectionAsync()
    {
        foreach (var output in new[] { "13\n", "not-a-user\n" })
        {
            var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
            context.Runner.CurrentAndroidUserOutput = output;
            var callbacks = 0;
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => callbacks++, () => callbacks++, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10), () => true, CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure,
                "changed or malformed Android user is terminal");
            Check(result.RouteStatus is null && !result.ClientLaunchCompleted,
                "Android user failure occurs before route inspection or launch");
            Check(context.Runner.TargetCalls.Count == 1 &&
                context.Runner.TargetCalls[0].SequenceEqual(Target("shell", "am", "get-current-user")),
                "Android user failure executes only the user validation command");
            Check(context.Runner.ReverseCreateCount == 0 && context.Runner.LaunchCount == 0 && callbacks == 0,
                "Android user failure preserves route ownership without mutation");
        }
    }

    static async Task AndroidUserChangeBeforeLaunchStopsWithoutIssuingLaunchAsync()
    {
        var context = CreateContext(mapping: "UsbFfs tcp:54321 tcp:27183\n");
        var callbacks = 0;
        var changed = false;
        Task ChangeUserDuringWait(TimeSpan _, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!changed)
            {
                context.Runner.CurrentAndroidUserOutput = "13\n";
                changed = true;
            }
            return Task.CompletedTask;
        }
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => false, () => true, () => callbacks++, () => callbacks++, ChangeUserDuringWait);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10), () => true, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure &&
            result.RouteStatus == AdbReversePortStatus.Existing,
            "user change after inspection becomes a terminal recovery result for the inspected route");
        Check(!result.ClientLaunchCompleted && context.Runner.LaunchCount == 0,
            "failed pre-launch user validation cannot claim or execute an Android launch");
        Check(context.Runner.TargetCalls.Count == 3 &&
            context.Runner.TargetCalls[0].SequenceEqual(Target("shell", "am", "get-current-user")) &&
            context.Runner.TargetCalls[1].SequenceEqual(Target("reverse", "--list")) &&
            context.Runner.TargetCalls[2].SequenceEqual(Target("shell", "am", "get-current-user")),
            "user is validated at recovery start and again before any client launch");
        Check(callbacks == 0 && context.Runner.ReverseCreateCount == 0,
            "user change after an owned route inspection leaves ownership and mapping unchanged");
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
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => callbacks++, () => callbacks++, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
                () => throw new Exception("retryable inventory failure must not reserve a launch"), CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.RetryableFailure, "absent/offline device is retryable");
            Check(result.RouteStatus is null && !result.ClientLaunchCompleted, "inventory failure precedes route mutation");
            Check(callbacks == 0 && context.Runner.TargetCalls.Count == 0,
                "absent/offline device has no targeted command or ownership callback");
        }
    }

    static async Task SensitiveAdbFailuresAreRedactedAsync()
    {
        const string marker = "SENSITIVE-RECOVERY-MARKER serial=TABLINK_RECOVERY_TEST_001 path=C:\\Users\\Private\\adb.exe";
        var context = CreateContext(mapping: "", reverseListExitCode: 91, reverseListError: marker);
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => false, () => true, () => { }, () => { }, NoDelay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10), () => true, CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.RetryableFailure,
            "nonzero ADB inspection remains a retryable recovery failure");
        Check(result.Detail.Contains(nameof(AdbCommandException), StringComparison.Ordinal) &&
              result.Detail.Contains("91", StringComparison.Ordinal),
            "recovery detail preserves only the ADB error type and exit code");
        Check(!result.Detail.Contains(marker, StringComparison.Ordinal) &&
              !result.Detail.Contains(OriginalSerial, StringComparison.Ordinal) &&
              !result.Detail.Contains("C:\\Users\\Private", StringComparison.OrdinalIgnoreCase),
            "recovery detail excludes raw stderr, serial and ADB path markers before MainForm can log it");
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
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
            () => true, () => false, () => true, () => callbacks++, () => callbacks++, NoDelay);
        var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
            () => throw new Exception("policy failure must not reserve a launch"), CancellationToken.None);

        Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, caseName + " is terminal");
        Check(result.RouteStatus is null && !result.ClientLaunchCompleted, caseName + " fails before route inspection");
        Check(context.Runner.TargetCalls.Count == 0 && callbacks == 0, caseName + " performs no mutation or ownership change");
    }

    static async Task StopCancelsBlockedInspectionAsync()
    {
        var context = CreateContext(mapping: "");
        context.Runner.BlockInspection = true;
        var callbacks = 0;
        using var stop = new CancellationTokenSource();
        var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
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
        Check(context.Runner.TargetCalls.Count == 2 && context.Runner.TargetCalls[1].SequenceEqual(Target("reverse", "--list")),
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
            var runner = new UsbSessionRecoveryAttemptRunner(context.Client, context.AndroidUser, Endpoint, OriginalToken,
                () => true, () => false, () => true, () => retired++,
                () => { publishAttempted++; throw failure(); }, NoDelay);
            var result = await runner.RunAsync(TimeSpan.FromSeconds(10),
                () => throw new Exception("failed publish must not reserve a launch"), CancellationToken.None);

            Check(result.Status == UsbRecoveryExecutionStatus.TerminalFailure, "publish failure is terminal");
            Check(result.RouteStatus == AdbReversePortStatus.Created, "created route remains explicitly reported");
            Check(!result.ClientLaunchCompleted && context.Runner.LaunchCount == 0, "publish failure never launches the client");
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

    static TestContext CreateContext(string mapping, string deviceState = "device", string? deviceListing = null,
        int reverseListExitCode = 0, string? reverseListError = null)
    {
        var settings = new DevicePolicySettings { ExcludedDevices = [] };
        var policy = new DevicePolicy(settings);
        var inventory = new List<UsbDeviceIdentity> { OriginalIdentity };
        var approved = policy.Approve(new AdbDevice(OriginalSerial, "device", "Offline_Test_Tablet", "1"), inventory);
        var fake = new OfflineAdbRunner(OriginalSerial)
        {
            ReverseListOutput = mapping,
            ReverseListExitCode = reverseListExitCode,
            ReverseListError = reverseListError ?? ""
        };
        Task<IReadOnlyList<UsbDeviceIdentity>> Inventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UsbDeviceIdentity>>(inventory.ToArray());
        }
        var client = new AdbClient("E:\\offline-tests\\fake-adb.exe", policy, Inventory, fake);
        var androidUser = client.BindCurrentAndroidUserAsync(approved).GetAwaiter().GetResult();
        fake.ResetCalls();
        fake.DeviceState = deviceState;
        fake.DevicesStandardOutput = deviceListing;
        ObservedRunners.Add(fake);
        return new(client, approved, androidUser, fake, settings, inventory);
    }

    static Task NoDelay(TimeSpan _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    static string[] Target(params string[] command) => ["-s", OriginalSerial, .. command];

    static bool IsProtectedPublication(string[] call, string? activation)
    {
        if (!IsActivationMarker(activation)) return false;
        return call.SequenceEqual(Target("shell", "content", "insert",
            "--uri", "content://com.tablink.client.adb/session",
            "--user", "0",
            "--bind", "activation:s:" + activation,
            "--bind", "token:s:" + OriginalToken,
            "--bind", "port:i:54321"));
    }

    static bool IsMarkerActivation(string[] call, string? activation) =>
        IsActivationMarker(activation) &&
        call.SequenceEqual(Target("shell", "am", "start", "--user", "0",
            "-n", "com.tablink.client/.MainActivity",
            "-a", "com.tablink.client.APPLY_ADB_SESSION",
            "--es", "adbActivation", activation!));

    static bool IsActivationMarker(string? value) => value is { Length: 32 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    sealed record TestContext(AdbClient Client, ApprovedUsbDevice Approved, ApprovedAndroidUser AndroidUser,
        OfflineAdbRunner Runner,
        DevicePolicySettings Settings, List<UsbDeviceIdentity> Inventory);

    sealed class OfflineAdbRunner(string expectedSerial) : IAdbProcessRunner
    {
        readonly object sync = new();
        readonly List<string[]> calls = [];
        int reverseCreateCount;
        int publicationCount;
        int launchCount;
        string? publishedActivation;

        public string ReverseListOutput { get; init; } = "";
        public int ReverseListExitCode { get; init; }
        public string ReverseListError { get; init; } = "";
        public string DeviceState { get; set; } = "device";
        public string? DevicesStandardOutput { get; set; }
        public string CurrentAndroidUserOutput { get; set; } = "0\n";
        public string ProviderPublicationError { get; set; } = "";
        public bool ChangeAndroidUserAfterPublication { get; set; }
        public bool BlockUserCheckAfterPublication { get; set; }
        public bool BlockInspection { get; set; }
        public bool BlockReverse { get; set; }
        public TaskCompletionSource<bool> InspectionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReverseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReverse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PostPublicationUserCheckEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReverseCreateCount => Volatile.Read(ref reverseCreateCount);
        public int PublicationCount => Volatile.Read(ref publicationCount);
        public int LaunchCount => Volatile.Read(ref launchCount);
        public string? PublishedActivation { get { lock (sync) return publishedActivation; } }
        public IReadOnlyList<string[]> Calls { get { lock (sync) return calls.Select(x => x.ToArray()).ToArray(); } }
        public IReadOnlyList<string[]> TargetCalls => Calls.Where(x => !x.SequenceEqual(new[] { "devices", "-l" })).ToArray();
        public void ResetCalls() { lock (sync) calls.Clear(); }

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
                return new AdbCommandResult(ReverseListExitCode, ReverseListOutput, ReverseListError);
            }
            if (call.SequenceEqual(Target("shell", "am", "get-current-user")))
            {
                if (BlockUserCheckAfterPublication && PublicationCount > 0)
                {
                    PostPublicationUserCheckEntered.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return new AdbCommandResult(0, CurrentAndroidUserOutput, "");
            }
            if (call.SequenceEqual(Target("reverse", "--no-rebind", "tcp:54321", "tcp:27183")))
            {
                ReverseEntered.TrySetResult(true);
                if (BlockReverse)
                    await ReleaseReverse.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref reverseCreateCount);
                return new AdbCommandResult(0, "", "");
            }
            if (call.Length == 15 &&
                call[0] == "-s" && call[1] == expectedSerial &&
                call[2] == "shell" && call[3] == "content" && call[4] == "insert" &&
                call[5] == "--uri" && call[6] == "content://com.tablink.client.adb/session" &&
                call[7] == "--user" && call[8] == "0" &&
                call[9] == "--bind" && call[10].StartsWith("activation:s:", StringComparison.Ordinal) &&
                call[11] == "--bind" && call[12] == "token:s:" + OriginalToken &&
                call[13] == "--bind" && call[14] == "port:i:54321")
            {
                var activation = call[10]["activation:s:".Length..];
                if (!IsActivationMarker(activation))
                    throw new InvalidOperationException("Protected publication used an invalid activation marker.");
                lock (sync) publishedActivation = activation;
                Interlocked.Increment(ref publicationCount);
                if (ProviderPublicationError.Length != 0)
                    return new AdbCommandResult(0, "", ProviderPublicationError);
                if (ChangeAndroidUserAfterPublication)
                    CurrentAndroidUserOutput = "13\n";
                return new AdbCommandResult(0, "", "");
            }
            if (IsMarkerActivation(call, PublishedActivation))
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
