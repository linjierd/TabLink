using System.Text.Json;
using System.Text.Json.Nodes;
using TabLink.Windows;

static class PendingUsbReverseCleanupQueueTests
{
    internal static void Run(Action<bool, string> check)
    {
        PreparedOwnedAndCompletedStatesAreMonotonic(check);
        ConcurrentExactOperationsConverge(check);
        TombstoneDominatesInterruptedCompletion(check);
        ExpiredTombstonesCompactOnlyWhenSafe(check);
        FiveEntriesDeferFairly(check);
        MalformedAndFutureRecordsFailClosed(check);
        InvalidDeviceIdentitiesFailClosed(check);
        BoundsNeverEvictOldEvidence(check);
        ProtectedFileBoundaryCoversEveryDurableQueueObject(check);
    }

    static void PreparedOwnedAndCompletedStatesAreMonotonic(Action<bool, string> check)
    {
        using var test = new QueueScenario();
        var receipt = test.Receipt(50101, 1);

        test.Queue.Prepare(receipt);
        test.Queue.Prepare(receipt);
        var prepared = test.Queue.ReadPendingEntries(8).Single();
        check(prepared == new PendingUsbReverseCleanupEntry(receipt, UsbReverseQueueState.Prepared),
            "repeated exact prepares converge to one non-removal Prepared record");
        using (var document = JsonDocument.Parse(File.ReadAllBytes(test.PendingPath(receipt.Id))))
            check(document.RootElement.GetProperty("schema").GetInt32() ==
                  PendingUsbReverseCleanupQueue.CurrentEnvelopeSchema &&
                  document.RootElement.GetProperty("kind").GetString() == "prepared" &&
                  document.RootElement.GetProperty("receipt").GetProperty("Id").GetGuid() == receipt.Id,
                "Prepared is stored in an explicit versioned immutable envelope");

        test.Queue.Activate(receipt);
        test.Queue.Activate(receipt);
        var owned = test.Queue.ReadPendingEntries(8).Single();
        check(owned == new PendingUsbReverseCleanupEntry(receipt, UsbReverseQueueState.Owned),
            "activation promotes the exact Prepared receipt and is idempotent while Owned");
        check(Throws<InvalidDataException>(() => test.Queue.Prepare(receipt)),
            "an Owned receipt cannot be demoted or rebound through Prepare");

        var unprepared = test.Receipt(50102, 2);
        check(Throws<InvalidDataException>(() => test.Queue.Activate(unprepared)) &&
              !File.Exists(test.PendingPath(unprepared.Id)),
            "Activate without an exact durable Prepared record fails closed");

        test.Queue.Complete(receipt);
        test.Queue.Complete(receipt);
        check(test.Queue.ReadPendingEntries(8).Count == 0 &&
              !File.Exists(test.PendingPath(receipt.Id)) && File.Exists(test.CompletedPath(receipt.Id)),
            "completion is idempotent and leaves a durable tombstone before pending authority disappears");
        check(Throws<InvalidDataException>(() => test.Queue.Prepare(receipt)) &&
              Throws<InvalidDataException>(() => test.Queue.Activate(receipt)),
            "a Completed receipt cannot be prepared or activated again");
    }

    static void ConcurrentExactOperationsConverge(Action<bool, string> check)
    {
        using var test = new QueueScenario();
        var receipt = test.Receipt(50111, 10);
        Task.WaitAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => test.Queue.Prepare(receipt))).ToArray());
        check(test.Queue.ReadPendingEntries(8).SequenceEqual(
                [new PendingUsbReverseCleanupEntry(receipt, UsbReverseQueueState.Prepared)]),
            "concurrent exact prepares converge to one Prepared record");

        Task.WaitAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => test.Queue.Activate(receipt))).ToArray());
        check(test.Queue.ReadPendingEntries(8).SequenceEqual(
                [new PendingUsbReverseCleanupEntry(receipt, UsbReverseQueueState.Owned)]),
            "concurrent exact activations converge to one Owned record");

        Task.WaitAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => test.Queue.Complete(receipt))).ToArray());
        check(test.Queue.ReadPendingEntries(8).Count == 0 &&
              !File.Exists(test.PendingPath(receipt.Id)) && File.Exists(test.CompletedPath(receipt.Id)),
            "concurrent exact completions converge to one durable tombstone");
    }

    static void TombstoneDominatesInterruptedCompletion(Action<bool, string> check)
    {
        using var test = new QueueScenario();
        var receipt = test.Receipt(50201, 11);
        Own(test.Queue, receipt);
        var pendingBytes = File.ReadAllBytes(test.PendingPath(receipt.Id));
        test.Queue.Complete(receipt);

        // Recreate a crash after durable tombstone write but before pending delete.
        File.WriteAllBytes(test.PendingPath(receipt.Id), pendingBytes);
        check(test.Queue.ReadPendingEntries(8).Count == 0 && !File.Exists(test.PendingPath(receipt.Id)),
            "an exact completed tombstone wins after interrupted completion and repairs duplicate pending state");

        var conflict = receipt with { Endpoint = new AdbReverseEndpoint(50202) };
        using var other = new QueueScenario();
        Own(other.Queue, conflict);
        File.Copy(other.PendingPath(conflict.Id), test.PendingPath(receipt.Id), true);
        var errors = new List<string>();
        check(test.Queue.ReadPendingEntries(8, errors.Add).Count == 0 && errors.Count == 1 &&
              File.Exists(test.PendingPath(receipt.Id)),
            "a mismatched pending record beside a tombstone fails closed and remains available for diagnosis");
        check(Throws<InvalidDataException>(() => test.Queue.Prepare(conflict)),
            "a conflicting body cannot reuse an ID held by a completed tombstone");
    }

    static void ExpiredTombstonesCompactOnlyWhenSafe(Action<bool, string> check)
    {
        using (var test = new QueueScenario(maximumCompletedRecords: 2,
                   completedRetention: TimeSpan.Zero,
                   ownerIsRunning: (pid, _) => pid == 2202))
        {
            var runningOwner = test.Receipt(50211, 12, ownerPid: 2202, ownerStartTicks: 1202);
            var exitedOwner = test.Receipt(50212, 13, ownerPid: 2203, ownerStartTicks: 1203);
            var newcomer = test.Receipt(50213, 14, ownerPid: 2204, ownerStartTicks: 1204);
            test.Queue.Complete(runningOwner);
            test.Queue.Complete(exitedOwner);
            var expired = DateTime.UtcNow.AddDays(-30);
            File.SetLastWriteTimeUtc(test.CompletedPath(runningOwner.Id), expired);
            File.SetLastWriteTimeUtc(test.CompletedPath(exitedOwner.Id), expired.AddSeconds(1));

            test.Queue.Complete(newcomer);
            check(File.Exists(test.CompletedPath(runningOwner.Id)) &&
                  !File.Exists(test.CompletedPath(exitedOwner.Id)) &&
                  File.Exists(test.CompletedPath(newcomer.Id)),
                "compaction removes only an expired tombstone whose exact owner has exited");
        }

        using (var test = new QueueScenario(maximumCompletedRecords: 1,
                   completedRetention: TimeSpan.Zero, ownerIsRunning: (_, _) => false))
        {
            var protectedReceipt = test.Receipt(50221, 15);
            Own(test.Queue, protectedReceipt);
            var pendingBytes = File.ReadAllBytes(test.PendingPath(protectedReceipt.Id));
            test.Queue.Complete(protectedReceipt);
            File.WriteAllBytes(test.PendingPath(protectedReceipt.Id), pendingBytes);
            var expired = DateTime.UtcNow.AddDays(-30);
            File.SetLastWriteTimeUtc(test.CompletedPath(protectedReceipt.Id), expired);
            File.SetLastWriteTimeUtc(test.PendingPath(protectedReceipt.Id), expired);
            var newcomer = test.Receipt(50222, 16);

            check(Throws<IOException>(() => test.Queue.Complete(newcomer)) &&
                  File.Exists(test.CompletedPath(protectedReceipt.Id)) &&
                  File.Exists(test.PendingPath(protectedReceipt.Id)) &&
                  !File.Exists(test.CompletedPath(newcomer.Id)),
                "an expired tombstone with matching interrupted pending evidence is never compacted");
        }
    }

    static void FiveEntriesDeferFairly(Action<bool, string> check)
    {
        using var test = new QueueScenario();
        var receipts = Enumerable.Range(0, 5)
            .Select(i => test.Receipt(50231 + i, 40 + i)).ToArray();
        foreach (var receipt in receipts) Own(test.Queue, receipt);
        var originalSequences = receipts.Select(receipt =>
            ReadSequence(test.PendingPath(receipt.Id))).ToArray();
        var oldest = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < receipts.Length; i++)
            File.SetLastWriteTimeUtc(test.PendingPath(receipts[i].Id), oldest.AddSeconds(i));

        var firstPass = test.Queue.ReadPendingEntries(4).ToArray();
        foreach (var entry in firstPass) test.Queue.Defer(entry);
        var secondPass = test.Queue.ReadPendingEntries(4).ToArray();
        check(firstPass.Select(x => x.Receipt.Id).SequenceEqual(receipts.Take(4).Select(x => x.Id)) &&
              secondPass[0].Receipt.Id == receipts[4].Id,
            "persistent sequence, rather than filesystem time, rotates a bounded four-record pass behind the fifth record");
        var deferredSequences = firstPass.Select(entry =>
            ReadSequence(test.PendingPath(entry.Receipt.Id))).ToArray();
        check(originalSequences.SequenceEqual([1L, 2L, 3L, 4L, 5L]) &&
              deferredSequences.All(sequence => sequence > originalSequences[^1]) &&
              deferredSequences.SequenceEqual(deferredSequences.Order()),
            "defer persists a strictly increasing retry sequence even when mtimes are coarse or reordered");
        check(secondPass.All(x => x.State == UsbReverseQueueState.Owned),
            "fair retry rotation preserves each entry's Owned state");
    }

    static void MalformedAndFutureRecordsFailClosed(Action<bool, string> check)
    {
        using var test = new QueueScenario();
        var malformedId = Guid.Parse("00000000-0000-0000-0000-000000000021");
        var future = test.Receipt(50301, 22);
        var duplicate = test.Receipt(50302, 23);

        test.Queue.Prepare(future);
        test.Queue.Prepare(duplicate);
        var futureNode = JsonNode.Parse(File.ReadAllText(test.PendingPath(future.Id)))!.AsObject();
        futureNode["schema"] = PendingUsbReverseCleanupQueue.CurrentEnvelopeSchema + 1;
        File.WriteAllText(test.PendingPath(future.Id), futureNode.ToJsonString());

        var duplicateJson = File.ReadAllText(test.PendingPath(duplicate.Id));
        duplicateJson = duplicateJson.Replace(
            $"\"schema\": {PendingUsbReverseCleanupQueue.CurrentEnvelopeSchema}",
            $"\"schema\": {PendingUsbReverseCleanupQueue.CurrentEnvelopeSchema},\n  \"schema\": {PendingUsbReverseCleanupQueue.CurrentEnvelopeSchema}",
            StringComparison.Ordinal);
        File.WriteAllText(test.PendingPath(duplicate.Id), duplicateJson);
        File.WriteAllText(test.PendingPath(malformedId), "{broken-json");
        File.WriteAllText(Path.Combine(test.Folder, "not-a-receipt.json"), "{}");

        var errors = new List<string>();
        check(test.Queue.ReadPendingEntries(8, errors.Add).Count == 0 && errors.Count == 4,
            "malformed, future-schema, duplicate-property, and invalid-name records all fail closed");
        check(Directory.EnumerateFiles(test.Folder, "*.json").Count() == 4,
            "invalid records remain intact instead of being silently discarded");

        using var small = new QueueScenario(maximumRecordBytes: 64);
        var tooLarge = small.Receipt(50303, 24);
        check(Throws<InvalidDataException>(() => small.Queue.Prepare(tooLarge)) &&
              !File.Exists(small.PendingPath(tooLarge.Id)),
            "oversized serialized records are rejected before authority is published");

        using var damagedTombstone = new QueueScenario();
        var blocked = damagedTombstone.Receipt(50304, 25);
        damagedTombstone.Queue.Prepare(blocked);
        Directory.CreateDirectory(Path.GetDirectoryName(damagedTombstone.CompletedPath(blocked.Id))!);
        File.WriteAllText(damagedTombstone.CompletedPath(blocked.Id), "{broken-json");
        errors.Clear();
        check(damagedTombstone.Queue.ReadPendingEntries(8, errors.Add).Count == 0 && errors.Count == 1 &&
              File.Exists(damagedTombstone.PendingPath(blocked.Id)),
            "a damaged tombstone blocks pending authority and preserves its pending proof");

        using var futureTombstone = new QueueScenario();
        var completed = futureTombstone.Receipt(50305, 26);
        futureTombstone.Queue.Complete(completed);
        var completedNode = JsonNode.Parse(
            File.ReadAllText(futureTombstone.CompletedPath(completed.Id)))!.AsObject();
        completedNode["schema"] = PendingUsbReverseCleanupQueue.CurrentEnvelopeSchema + 1;
        File.WriteAllText(futureTombstone.CompletedPath(completed.Id), completedNode.ToJsonString());
        check(Throws<InvalidDataException>(() => futureTombstone.Queue.Prepare(completed)) &&
              !File.Exists(futureTombstone.PendingPath(completed.Id)),
            "a future-schema tombstone fails closed and cannot be overwritten or bypassed");
    }

    static void InvalidDeviceIdentitiesFailClosed(Action<bool, string> check)
    {
        using var test = new QueueScenario();
        var baseline = test.Receipt(50311, 27);
        var invalid = new[]
        {
            baseline with { Id = Guid.NewGuid(), Serial = "127.0.0.1:5555" },
            baseline with { Id = Guid.NewGuid(), Serial = "-command" },
            baseline with { Id = Guid.NewGuid(), Vid = "12G4" },
            baseline with { Id = Guid.NewGuid(), Pid = "123" },
            baseline with { Id = Guid.NewGuid(), OwnerUserSid = "not-a-sid" }
        };

        check(invalid.All(receipt =>
                Throws<InvalidDataException>(() => test.Queue.Prepare(receipt)) &&
                Throws<InvalidDataException>(() => test.Queue.Complete(receipt))),
            "network or command-like serials, malformed VID/PID, and invalid user SIDs never enter the queue");
        check(!Directory.EnumerateFiles(test.Folder, "*.json", SearchOption.AllDirectories).Any(),
            "invalid device identities cannot create pending or completed authority files");
    }

    static void BoundsNeverEvictOldEvidence(Action<bool, string> check)
    {
        using var test = new QueueScenario(maximumPendingRecords: 2, maximumCompletedRecords: 1);
        var first = test.Receipt(50401, 31);
        var second = test.Receipt(50402, 32);
        var third = test.Receipt(50403, 33);
        Own(test.Queue, first);
        Own(test.Queue, second);
        check(Throws<IOException>(() => test.Queue.Prepare(third)) &&
              test.Queue.ReadPending(8).ToHashSet().SetEquals([first, second]),
            "a full pending queue rejects new authority without evicting old receipts");

        test.Queue.Complete(first);
        Own(test.Queue, third);
        check(test.Queue.ReadPending(8).ToHashSet().SetEquals([second, third]),
            "capacity is released only after an exact receipt has a durable completed tombstone");
        var secondBytes = File.ReadAllBytes(test.PendingPath(second.Id));
        check(Throws<IOException>(() => test.Queue.Complete(second)) &&
              File.ReadAllBytes(test.PendingPath(second.Id)).SequenceEqual(secondBytes),
            "a full tombstone store fails closed before deleting pending proof");
    }

    static void ProtectedFileBoundaryCoversEveryDurableQueueObject(Action<bool, string> check)
    {
        using (var test = new QueueScenario())
        {
            var receipt = test.Receipt(50411, 51);
            test.Queue.Prepare(receipt);
            test.Queue.Activate(receipt);
            test.Queue.Complete(receipt);

            var queueLock = Path.Combine(test.Folder, ".queue.lock");
            check(test.Security.CreatedPaths.Contains(queueLock, StringComparer.OrdinalIgnoreCase) &&
                  test.Security.CreatedPaths.Any(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(Path.GetDirectoryName(path), test.Folder, StringComparison.OrdinalIgnoreCase)) &&
                  test.Security.CreatedPaths.Any(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(Path.GetDirectoryName(path), Path.Combine(test.Folder, "completed"),
                          StringComparison.OrdinalIgnoreCase)),
                "queue lock plus pending and tombstone staging files are all created through the protected-file API");
            check(test.Security.VerifiedPaths.Contains(queueLock, StringComparer.OrdinalIgnoreCase) &&
                  test.Security.VerificationCount(queueLock) >= 5 &&
                  test.Security.VerifiedPaths.Contains(test.PendingPath(receipt.Id), StringComparer.OrdinalIgnoreCase) &&
                  test.Security.VerifiedPaths.Contains(test.CompletedPath(receipt.Id), StringComparer.OrdinalIgnoreCase),
                "the acquired lock is reverified and each durable pending record and tombstone is reverified after commit and on read");
        }

        using (var test = new QueueScenario())
        {
            var receipt = test.Receipt(50412, 52);
            test.Queue.Prepare(receipt);
            test.Security.MarkUnsafe(test.PendingPath(receipt.Id));
            var errors = new List<string>();
            check(Throws<UnauthorizedAccessException>(() => test.Queue.RequirePending(receipt)) &&
                  test.Queue.ReadPendingEntries(8, errors.Add).Count == 0 && errors.Count == 1,
                "an unsafe pending-file ACL is rejected both by strict reads and bounded queue enumeration");
        }

        using (var test = new QueueScenario())
        {
            var receipt = test.Receipt(50413, 53);
            test.Queue.Complete(receipt);
            test.Security.MarkUnsafe(test.CompletedPath(receipt.Id));
            check(Throws<UnauthorizedAccessException>(() => test.Queue.Prepare(receipt)),
                "an unsafe tombstone ACL cannot be used to erase or recreate cleanup authority");
        }

        using (var test = new QueueScenario())
        {
            _ = test.Queue.ReadPendingEntries(8);
            var queueLock = Path.Combine(test.Folder, ".queue.lock");
            test.Security.MarkUnsafe(queueLock);
            check(Throws<UnauthorizedAccessException>(() => test.Queue.Prepare(test.Receipt(50414, 54))),
                "an unsafe queue-lock ACL blocks all queue mutations before any record is written");
        }


        using (var test = new QueueScenario())
        {
            _ = test.Queue.ReadPendingEntries(8); // Establish a verified protected lock first.
            var receipt = test.Receipt(50415, 55);
            test.Security.MarkNextCreatedUnsafe();
            check(Throws<UnauthorizedAccessException>(() => test.Queue.Prepare(receipt)) &&
                  !File.Exists(test.PendingPath(receipt.Id)) &&
                  test.Security.CreatedPaths.Any(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                      File.Exists(path)),
                "an unsafe temporary-file ACL fails before commit and preserves the rejected object for diagnosis");
        }
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

    static long ReadSequence(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("sequence").GetInt64();
    }

    sealed class QueueScenario : IDisposable
    {
        readonly string root;
        readonly string testParent;
        internal string Folder => Path.Combine(root, "pending");
        internal TestUsbReverseQueueFileSecurity Security { get; } = new();
        internal PendingUsbReverseCleanupQueue Queue { get; }

        internal QueueScenario(
            int maximumPendingRecords = PendingUsbReverseCleanupQueue.DefaultMaximumPendingRecords,
            int maximumRecordBytes = PendingUsbReverseCleanupQueue.DefaultMaximumRecordBytes,
            int maximumCompletedRecords = PendingUsbReverseCleanupQueue.DefaultMaximumCompletedRecords,
            TimeSpan? completedRetention = null,
            Func<int, long, bool>? ownerIsRunning = null)
        {
            testParent = Path.Combine(AppContext.BaseDirectory, "queue-test-temp");
            root = Path.Combine(testParent, "TabLink-PendingUsbQueue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Path.Combine(Folder, "completed"));
            Queue = new(Folder, maximumPendingRecords, maximumRecordBytes, maximumCompletedRecords,
                completedRetention, ownerIsRunning, Security);
        }

        internal UsbReverseLease Receipt(int port, int suffix, int ownerPid = 1234,
            long ownerStartTicks = 5678) => new(
            Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"), ownerPid, ownerStartTicks,
            new AdbReverseEndpoint(port));

        internal string PendingPath(Guid id) => Path.Combine(Folder, id.ToString("N") + ".json");
        internal string CompletedPath(Guid id) =>
            Path.Combine(Folder, "completed", id.ToString("N") + ".json");

        public void Dispose()
        {
            var full = Path.GetFullPath(root);
            var parent = Path.GetFullPath(testParent).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("TabLink-PendingUsbQueue-", StringComparison.Ordinal))
                throw new IOException("Refusing to delete an unexpected pending-queue test directory");
            Directory.Delete(full, true);
            if (!Directory.EnumerateFileSystemEntries(testParent).Any()) Directory.Delete(testParent);
        }
    }
}
