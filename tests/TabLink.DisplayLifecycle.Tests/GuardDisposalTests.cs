using System.Diagnostics;
using System.Text.Json;
using TabLink.Windows;

static class GuardDisposalTests
{
    internal static void Run(Action<bool, string> check)
    {
        using (var test = new DisposalScenario())
        {
            var first = new UsbReverseLease(Guid.NewGuid(), Environment.ProcessId, test.OwnerStartTicks, new AdbReverseEndpoint(49155));
            var second = new UsbReverseLease(Guid.NewGuid(), Environment.ProcessId, test.OwnerStartTicks, new AdbReverseEndpoint(49156));
            test.Guard.AttachReverse(first);
            check(test.ReadState().ReverseLease == first,
                "the first created USB mapping publishes its exact crash-cleanup receipt");
            var queue = new PendingUsbReverseCleanupQueue(test.PendingFolder);
            queue.Prepare(first);queue.Activate(first);
            test.Guard.RetainReverseForCleanup(first);
            var pending = queue.ReadPending(8);
            check(pending.Contains(first),
                "an active guard can preserve its exact receipt independently before stop retires the display lease");
            test.Guard.ReplaceReverse(first, null);
            check(test.ReadState().ReverseLease is null,
                "a missing or uncertain mapping retires its old cleanup authority before repair");
            test.Guard.ReplaceReverse(null, second);
            check(test.ReadState().ReverseLease == second,
                "a successfully recreated mapping publishes a fresh cleanup receipt");
            check(Throws<IOException>(() => test.Guard.ReplaceReverse(first, null)),
                "a stale recovery completion cannot replace the current USB receipt");
            test.Guard.Dispose();
        }

        using (var test = new DisposalScenario())
        {
            var receipt = new UsbReverseLease(Guid.NewGuid(), Environment.ProcessId, test.OwnerStartTicks, new AdbReverseEndpoint(49157));
            test.Guard.AttachReverse(receipt);
            var trustedBootstrap = File.ReadAllText(test.BootstrapPath);
            var valid = test.ReadState();
            File.WriteAllText(test.Path, JsonSerializer.Serialize(valid with { OwnerPid = valid.OwnerPid + 1 }));
            check(Throws<IOException>(() => test.Guard.ReplaceReverse(receipt, null)),
                "a well-formed renewable file with changed owner identity cannot update USB cleanup authority");
            check(File.ReadAllText(test.BootstrapPath) == trustedBootstrap,
                "rejected renewable identity cannot be promoted into the immutable crash-cleanup proof");
            File.WriteAllText(test.Path, JsonSerializer.Serialize(valid));
            test.Guard.Dispose();
        }

        using (var test = new DisposalScenario())
        {
            test.Detach = _ => new(false, false, "temporary native rejection");
            check(Throws<IOException>(test.Guard.Dispose), "failed UI detach is reported instead of marking cleanup complete");
            check(test.Marker == test.Lease.LeaseId && test.ReadState().StopRequested,
                "failed UI detach retains ownership and publishes explicit stop for the watcher");
            var stoppedState = File.ReadAllText(test.Path);
            test.Guard.Renew(DateTime.UtcNow.AddSeconds(20));
            check(File.ReadAllText(test.Path) == stoppedState, "stopped guard cannot renew a pending cleanup lease");
            check(Throws<IOException>(() => test.Guard.AttachReverse(new(Guid.NewGuid(), Environment.ProcessId,
                test.OwnerStartTicks, new AdbReverseEndpoint(49158)))),
                "stopped guard cannot register another USB reverse receipt while cleanup is pending");
            check(Throws<IOException>(() => test.Guard.RefreshRememberedLayout()), "stopped guard cannot continue recording session layout");
            test.Detach = _ => new(true, true, "retry succeeded");
            test.Guard.Dispose();
            check(test.DetachIds.SequenceEqual([test.Lease.LeaseId, test.Lease.LeaseId]) && test.Marker == Guid.Empty,
                "second Dispose retries the exact owned display and retires its marker after success");
            test.Guard.Dispose();
            check(test.DetachIds.Count == 2, "successful guard disposal remains idempotent");
        }

        using (var test = new DisposalScenario())
        {
            test.Detach = _ => throw new IOException("native enumeration temporarily failed");
            check(Throws<IOException>(test.Guard.Dispose) && test.ReadState().StopRequested,
                "a throwing native detach also leaves explicit stop state eligible for watchdog cleanup");
            test.Detach = _ => new(true, true, "retry succeeded");
            test.Guard.Dispose();
            check(test.DetachIds.Count == 2 && test.Marker == Guid.Empty, "Dispose retries after a thrown native cleanup error");
        }

        foreach (var nextOwner in new[] { Guid.Empty, Guid.NewGuid() })
        {
            using var test = new DisposalScenario();
            test.Detach = _ => new(false, false, "temporary native rejection");
            check(Throws<IOException>(test.Guard.Dispose), "first disposal remains pending before independent retirement or replacement");
            test.Marker = nextOwner;
            test.Guard.Dispose();
            test.Guard.Dispose();
            check(test.DetachIds.Count == 1 && test.Marker == nextOwner,
                nextOwner == Guid.Empty
                    ? "watcher-retired ownership completes pending Dispose without another detach"
                    : "superseded ownership completes old Dispose without touching the newer display session or marker");
        }

        foreach (var failure in new[] { "missing", "malformed", "locked" })
        {
            using var test = new DisposalScenario();
            FileStream? markerLock = null;
            if (failure == "missing") File.Delete(test.MarkerPath);
            else if (failure == "malformed") File.WriteAllText(test.MarkerPath, "not a lease id");
            else markerLock = new(test.MarkerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                check(ThrowsStorage(test.Guard.Dispose), $"{failure} ownership marker does not silently complete disposal");
                check(test.DetachIds.Count == 0, $"{failure} ownership marker cannot authorize any display mutation");
                var state = File.ReadAllText(test.Path);
                test.Guard.Renew(DateTime.UtcNow.AddSeconds(20));
                check(File.ReadAllText(test.Path) == state, $"{failure} marker failure still prevents stopped guard renewal");
                check(Throws<IOException>(() => test.Guard.AttachReverse(new(Guid.NewGuid(), Environment.ProcessId,
                    test.OwnerStartTicks, new AdbReverseEndpoint(49159)))),
                    $"{failure} marker failure still prevents USB receipt registration");
            }
            finally { markerLock?.Dispose(); }
            test.Marker = test.Lease.LeaseId;
            test.Guard.Dispose();
            check(test.DetachIds.SequenceEqual([test.Lease.LeaseId]) && test.Marker == Guid.Empty,
                $"{failure} marker can be repaired and then the exact pending lease can be disposed");
        }

        using (var test = new DisposalScenario())
        {
            test.Detach = _ => new(false, false, "temporary native rejection");
            using (var stateLock = new FileStream(test.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                check(Throws<IOException>(test.Guard.Dispose), "unwritable renewable state does not forget a failed native detach");
            test.Detach = _ => new(true, true, "retry succeeded");
            test.Guard.Dispose();
            check(test.DetachIds.Count == 2 && test.Marker == Guid.Empty && test.ReadState().StopRequested,
                "Dispose retries and publishes stop after temporary renewable-state write failure");
        }

        using (var test = new DisposalScenario())
        {
            using (var markerLock = new FileStream(test.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(ThrowsStorage(test.Guard.Dispose), "successful detach with failed marker retirement remains retryable");
            check(test.Marker == test.Lease.LeaseId && test.DetachIds.Count == 1,
                "failed marker retirement retains exact ownership evidence after successful detach");
            test.Guard.Dispose();
            check(test.Marker == Guid.Empty && test.DetachIds.All(id => id == test.Lease.LeaseId),
                "a later Dispose finishes marker retirement without expanding cleanup authority");
        }
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static bool ThrowsStorage(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }

    sealed class DisposalScenario : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "test-artifacts",
            "TabLink-GuardDisposal-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal string MarkerPath => Path + ".lease-id";
        internal string BootstrapPath => Path + "." + Lease.LeaseId.ToString("N") + ".initial.json";
        internal string PendingFolder => System.IO.Path.Combine(directory, "pending-usb-reverse");
        internal DisplayLease Lease { get; } = new(Guid.NewGuid(), @"\\.\FAKE-GUARD-DISPOSAL-ONLY");
        internal long OwnerStartTicks { get; }
        internal SessionGuard Guard { get; }
        internal List<Guid> DetachIds { get; } = [];
        internal Func<DisplayLease, DisplayChangeResult> Detach { get; set; } = _ => new(true, true, "fake detach only");
        internal Guid Marker
        {
            get => JsonSerializer.Deserialize<Guid>(File.ReadAllText(MarkerPath));
            set => File.WriteAllText(MarkerPath, JsonSerializer.Serialize(value));
        }

        internal DisposalScenario()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "lease.json");
            Directory.CreateDirectory(PendingFolder);
            Directory.CreateDirectory(System.IO.Path.Combine(PendingFolder, "completed"));
            using var owner = Process.GetCurrentProcess();
            OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks;
            var state = new SessionGuard.WatchState(Lease, Environment.ProcessId, OwnerStartTicks, DateTime.UtcNow.AddSeconds(20));
            File.WriteAllText(Path, JsonSerializer.Serialize(state));
            Marker = Lease.LeaseId;
            Guard = new SessionGuard(Lease, Path, OwnerStartTicks, PendingFolder);
            VirtualDisplayManager.OnDetach = lease =>
            {
                DetachIds.Add(lease.LeaseId);
                return Detach(lease);
            };
        }

        internal SessionGuard.WatchState ReadState() => JsonSerializer.Deserialize<SessionGuard.WatchState>(File.ReadAllText(Path))!;

        public void Dispose()
        {
            VirtualDisplayManager.OnDetach = _ => throw new Exception("Unexpected fake detach after disposal test");
            var resolved = System.IO.Path.GetFullPath(directory);
            var expectedParent = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
                "test-artifacts")).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(System.IO.Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith("TabLink-GuardDisposal-", StringComparison.Ordinal))
                throw new IOException("Refusing to delete an unexpected disposal-test directory");
            Directory.Delete(resolved, true);
            if (Directory.Exists(expectedParent) && !Directory.EnumerateFileSystemEntries(expectedParent).Any())
                Directory.Delete(expectedParent);
        }
    }
}
