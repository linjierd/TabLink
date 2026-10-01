using System.Text.Json;
using TabLink.Core;
using TabLink.Windows;

static class UsbCleanupCoordinatorTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        await OwnedCleanupRetainsOnlyDeferredReceipts(check);
        await PreparedNeverAuthorizesDeletion(check);
        await IndependentGateInstancesSerialize(check);
        await RetiredExceptionRequiresTheFullReceipt(check);
        await FiveReceiptsRotateFairly(check);
        await InvalidRecordBlocksNewMutation(check);
    }

    static async Task OwnedCleanupRetainsOnlyDeferredReceipts(Action<bool, string> check)
    {
        using var test = new Scenario();
        var first = test.Receipt(55101);
        var second = test.Receipt(55102);
        Own(test.Queue, first);
        Own(test.Queue, second);
        var calls = new List<(Guid Id, bool Retired, bool Authorized)>();
        var diagnostics = new List<string>();
        var coordinator = new UsbReverseCleanupCoordinator(test.Queue, test.Gate,
            (receipt, retired, removalAuthorized, _) =>
            {
                calls.Add((receipt.Id, retired, removalAuthorized));
                return Task.FromResult(receipt == first
                    ? new UsbReverseCleanupResult(UsbReverseCleanupStatus.Removed,
                        "serial SECRET_TEST_SERIAL must not be logged")
                    : new UsbReverseCleanupResult(UsbReverseCleanupStatus.DeviceUnavailable, "offline"));
            }, (_, value) => diagnostics.Add(JsonSerializer.Serialize(value)),
            processId: 1234, processStartTicks: 5678);
        coordinator.MarkRetiredByThisProcess(first);

        var pass = await coordinator.ProcessPendingAsync("test-pass");
        check(pass == new UsbReverseCleanupPass(2, 1, 1),
            "one cleanup pass completes only terminal results and retains deferred receipts");
        check(calls.Single(x => x.Id == first.Id) == (first.Id, true, true) &&
              calls.Single(x => x.Id == second.Id) == (second.Id, false, true),
            "only the exact in-memory retired Owned receipt receives both exceptions");
        check(test.Queue.ReadPending(int.MaxValue).SequenceEqual([second]),
            "completed receipt has a tombstone while the unavailable receipt remains pending");
        check(diagnostics.All(x => !x.Contains("SECRET_TEST_SERIAL", StringComparison.Ordinal)),
            "pending-cleanup diagnostics omit serials and cleanup messages");

        var reserved = coordinator.ReadReservedEndpoints();
        check(reserved.SetEquals([second.Endpoint]),
            "every pending endpoint remains reserved against a new session");
        var mutationSawReservation = false;
        await coordinator.RunMutationAsync((endpoints, _) =>
        {
            mutationSawReservation = endpoints.SetEquals([second.Endpoint]);
            return Task.CompletedTask;
        });
        check(mutationSawReservation,
            "a USB mutation receives the immutable pending-endpoint snapshot while holding the gate");

        var restarted = new UsbReverseCleanupCoordinator(test.Queue, test.Gate,
            (receipt, retired, removalAuthorized, _) =>
            {
                check(receipt == second && !retired && removalAuthorized,
                    "a process restart cannot recover the retired-owner exception from disk");
                return Task.FromResult(new UsbReverseCleanupResult(
                    UsbReverseCleanupStatus.DeviceUnavailable, "offline"));
            });
        await restarted.ProcessPendingAsync("restart-test");
    }

    static async Task PreparedNeverAuthorizesDeletion(Action<bool, string> check)
    {
        using var test = new Scenario();
        var prepared = test.Receipt(55201);
        test.Queue.Prepare(prepared);
        var called = false;
        var removalAttempted = false;
        var coordinator = new UsbReverseCleanupCoordinator(test.Queue, test.Gate,
            (receipt, retired, removalAuthorized, _) =>
            {
                called = receipt == prepared && !retired;
                removalAttempted = removalAuthorized;
                return Task.FromResult(new UsbReverseCleanupResult(
                    UsbReverseCleanupStatus.PreparedAbandoned, "sealed without ADB mutation"));
            });

        var pass = await coordinator.ProcessPendingAsync("prepared-test");
        check(called && !removalAttempted && pass == new UsbReverseCleanupPass(1, 1, 0),
            "Prepared cleanup is processed only with removal authorization disabled");
        check(test.Queue.ReadPendingEntries(8).Count == 0 &&
              File.Exists(test.CompletedPath(prepared.Id)),
            "an abandoned Prepared intent is permanently tombstoned without becoming Owned");
    }

    static async Task IndependentGateInstancesSerialize(Action<bool, string> check)
    {
        using var test = new Scenario();
        var firstGate = test.NewGate();
        var secondGate = test.NewGate();
        var first = new UsbReverseCleanupCoordinator(test.Queue, firstGate,
            (_, _, _, _) => throw new Exception("cleanup is not expected"));
        var second = new UsbReverseCleanupCoordinator(test.Queue, secondGate,
            (_, _, _, _) => throw new Exception("cleanup is not expected"));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var one = first.RunMutationAsync(async (_, _) =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;
        var two = second.RunMutationAsync((_, _) =>
        {
            secondEntered = true;
            return Task.CompletedTask;
        });
        await Task.Delay(150);
        check(!secondEntered,
            "two independent gate objects targeting one lock file cannot overlap mutations");
        releaseFirst.TrySetResult();
        await Task.WhenAll(one, two);
        check(secondEntered,
            "the second gate instance proceeds after the first file-lock owner releases it");
    }

    static async Task RetiredExceptionRequiresTheFullReceipt(Action<bool, string> check)
    {
        using var test = new Scenario();
        var exact = test.Receipt(55301);
        Own(test.Queue, exact);
        var prepared = test.Receipt(55302);
        test.Queue.Prepare(prepared);
        var wrongOwner = test.Receipt(55303, ownerPid: 1235);
        Own(test.Queue, wrongOwner);
        var completed = test.Receipt(55304);
        Own(test.Queue, completed);
        test.Queue.Complete(completed);

        var observed = new List<(Guid Id, bool Retired, bool Authorized)>();
        var coordinator = new UsbReverseCleanupCoordinator(test.Queue, test.Gate,
            (receipt, retired, removalAuthorized, _) =>
            {
                observed.Add((receipt.Id, retired, removalAuthorized));
                return Task.FromResult(new UsbReverseCleanupResult(
                    removalAuthorized ? UsbReverseCleanupStatus.Removed :
                    UsbReverseCleanupStatus.PreparedAbandoned, "done"));
            }, processId: 1234, processStartTicks: 5678);

        var mismatches = new[]
        {
            exact with { Serial = "OTHER_TEST_SERIAL" },
            exact with { Vid = "ABCD" },
            exact with { Pid = "ABCD" },
            exact with { Endpoint = new AdbReverseEndpoint(55991) },
            exact with { OwnerUserSid = "S-1-5-21-1-2-3-1002" },
            exact with { OwnerPid = 1235 },
            exact with { OwnerStartUtcTicks = 5679 },
            exact with { SchemaVersion = UsbReverseLease.CurrentSchemaVersion - 1 }
        };
        check(mismatches.All(receipt =>
                Throws<InvalidDataException>(() => coordinator.MarkRetiredByThisProcess(receipt))),
            "retired-owner authority compares every receipt field rather than trusting only its ID");
        check(Throws<InvalidDataException>(() => coordinator.MarkRetiredByThisProcess(prepared)) &&
              Throws<InvalidDataException>(() => coordinator.MarkRetiredByThisProcess(wrongOwner)) &&
              Throws<InvalidDataException>(() => coordinator.MarkRetiredByThisProcess(completed)),
            "Prepared, wrong-process, and Completed receipts cannot gain the retired-owner exception");

        coordinator.MarkRetiredByThisProcess(exact);
        coordinator.MarkRetiredByThisProcess(exact);
        var pass = await coordinator.ProcessPendingAsync("retired-proof-test");
        check(pass == new UsbReverseCleanupPass(3, 3, 0),
            "all queued states finish under their own explicit cleanup result");
        check(observed.Single(x => x.Id == exact.Id) == (exact.Id, true, true) &&
              observed.Single(x => x.Id == prepared.Id) == (prepared.Id, false, false) &&
              observed.Single(x => x.Id == wrongOwner.Id) == (wrongOwner.Id, false, true),
            "only the exact full Owned receipt is retired and Prepared still lacks removal authority");
    }

    static async Task FiveReceiptsRotateFairly(Action<bool, string> check)
    {
        using var test = new Scenario();
        var receipts = Enumerable.Range(0, 5).Select(i => test.Receipt(55401 + i)).ToArray();
        foreach (var receipt in receipts) Own(test.Queue, receipt);
        var oldest = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < receipts.Length; i++)
            File.SetLastWriteTimeUtc(test.PendingPath(receipts[i].Id), oldest.AddSeconds(i));

        var calls = new List<Guid>();
        var coordinator = new UsbReverseCleanupCoordinator(test.Queue, test.Gate,
            (receipt, _, removalAuthorized, _) =>
            {
                check(removalAuthorized, "fair-rotation entries remain Owned");
                calls.Add(receipt.Id);
                return Task.FromResult(new UsbReverseCleanupResult(
                    UsbReverseCleanupStatus.DeviceUnavailable, "retry later"));
            });

        var firstPass = await coordinator.ProcessPendingAsync("fairness-1");
        var firstIds = calls.ToArray();
        calls.Clear();
        var secondPass = await coordinator.ProcessPendingAsync("fairness-2");
        var secondIds = calls.ToArray();
        check(firstPass == new UsbReverseCleanupPass(4, 0, 4) &&
              secondPass == new UsbReverseCleanupPass(4, 0, 4),
            "each retry pass stays bounded to four records");
        check(firstIds.SequenceEqual(receipts.Take(4).Select(x => x.Id)) &&
              !firstIds.Contains(receipts[4].Id) && secondIds[0] == receipts[4].Id,
            "deferred records rotate behind the fifth record so bounded passes cannot starve it");
    }

    static async Task InvalidRecordBlocksNewMutation(Action<bool, string> check)
    {
        using var test = new Scenario();
        File.WriteAllText(Path.Combine(test.Folder, "not-a-valid-record.json"), "{}");
        var invoked = false;
        try
        {
            var coordinator = new UsbReverseCleanupCoordinator(test.Queue, test.Gate,
                (_, _, _, _) => throw new Exception("cleanup is not expected"));
            await coordinator.RunMutationAsync((_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            });
            throw new Exception("Expected invalid queue failure");
        }
        catch (InvalidDataException) { }
        check(!invoked, "an invalid queue record fails closed before any new USB mutation");
    }

    static void Own(PendingUsbReverseCleanupQueue queue, UsbReverseLease receipt)
    {
        queue.Prepare(receipt);
        queue.Activate(receipt);
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    sealed class Scenario : IDisposable
    {
        readonly string testParent = Path.Combine(AppContext.BaseDirectory, "test-artifacts");
        readonly string root;
        internal string Folder => Path.Combine(root, "pending");
        internal string GatePath => Path.Combine(root, "mutation.lock");
        internal PendingUsbReverseCleanupQueue Queue { get; }
        internal UsbReverseMutationGate Gate { get; }

        internal Scenario()
        {
            root = Path.Combine(testParent,
                "TabLink-UsbCoordinator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Path.Combine(Folder, "completed"));
            Queue = new(Folder, fileSecurity: new TestUsbReverseQueueFileSecurity());
            File.WriteAllBytes(GatePath, []);
            Gate = NewGate();
        }

        internal UsbReverseMutationGate NewGate() => new(GatePath, TimeSpan.FromSeconds(3));

        internal UsbReverseLease Receipt(int port, int ownerPid = 1234,
            long ownerStartTicks = 5678, string ownerUserSid = "S-1-5-21-1-2-3-1001") => new(
            Guid.NewGuid(), "SECRET_TEST_SERIAL", "1234", "5678", new AdbReverseEndpoint(port),
            ownerPid, ownerStartTicks, ownerUserSid, UsbReverseLease.CurrentSchemaVersion);

        internal string PendingPath(Guid id) => Path.Combine(Folder, id.ToString("N") + ".json");
        internal string CompletedPath(Guid id) =>
            Path.Combine(Folder, "completed", id.ToString("N") + ".json");

        public void Dispose()
        {
            var full = Path.GetFullPath(root);
            var parent = Path.GetFullPath(testParent).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("TabLink-UsbCoordinator-", StringComparison.Ordinal))
                throw new IOException("Refusing to delete an unexpected coordinator-test directory");
            Directory.Delete(full, true);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
    }
}
