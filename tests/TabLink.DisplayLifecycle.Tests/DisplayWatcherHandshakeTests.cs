using System.Diagnostics;
using System.Security.Cryptography;

namespace TabLink.Windows;

internal static class DisplayWatcherHandshakeTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-artifacts",
            "display-watcher-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ParentSignalsOnlyAfterChildReady(root, check);
            ParentRejectsExitAndTimeout(root, check);
            ChildBindsProtectedIdentityBeforeSignal(root, check);
            ChildRejectsOwnerReuse(root, check);
            ProductionWrapperRetainsExactKernelHandle(root, check);
            ArgumentsAreStrictAndNonceIsRedacted(root, check);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void ParentSignalsOnlyAfterChildReady(string root, Action<bool, string> check)
    {
        var start = BaseStart(root, out var leaseId);
        var fake = new FakeStartedWatcherProcess();
        string? nonce = null;
        var environment = new DisplayWatcherHandshake.ParentEnvironment(info =>
        {
            check(info.ArgumentList.Count == 6 && info.ArgumentList[3] == "4321" &&
                info.ArgumentList[4] == "987654321",
                "display watcher parent appends one exact owner identity and one nonce");
            nonce = info.ArgumentList[5];
            check(nonce.Length == 64 && nonce.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'),
                "display watcher parent creates a 256-bit uppercase hexadecimal nonce");
            using var ready = EventWaitHandle.OpenExisting(DisplayWatcherHandshake.ReadyEventName(nonce));
            ready.Set();
            return fake;
        }, TimeSpan.FromMilliseconds(2));

        DisplayWatcherHandshake.StartAndWait(start, 4321, 987654321, environment, TimeSpan.FromSeconds(1));
        check(fake.Disposed && nonce is not null && !leaseId.Equals(Guid.Empty),
            "parent accepts verified readiness and releases only its local child-process wrapper");
    }

    private static void ParentRejectsExitAndTimeout(string root, Action<bool, string> check)
    {
        var exitedStart = BaseStart(root, out _);
        var exited = new FakeStartedWatcherProcess { HasExitedValue = true, ExitCodeValue = 2 };
        try
        {
            DisplayWatcherHandshake.StartAndWait(exitedStart, 4321, 987654321,
                new(_ => exited, TimeSpan.FromMilliseconds(2)), TimeSpan.FromMilliseconds(50));
            throw new Exception("Expected early watcher exit rejection");
        }
        catch (IOException ex)
        {
            check(ex.Message.Contains("代码 2", StringComparison.Ordinal) && exited.Disposed,
                "parent fails the constructor when the watcher exits before readiness");
        }

        var timeoutStart = BaseStart(root, out _);
        var waiting = new FakeStartedWatcherProcess();
        var timer = Stopwatch.StartNew();
        try
        {
            DisplayWatcherHandshake.StartAndWait(timeoutStart, 4321, 987654321,
                new(_ => waiting, TimeSpan.FromMilliseconds(2)), TimeSpan.FromMilliseconds(30));
            throw new Exception("Expected watcher readiness timeout");
        }
        catch (TimeoutException)
        {
            check(timer.Elapsed < TimeSpan.FromSeconds(2) && waiting.Disposed,
                "parent timeout is bounded and does not retain its process wrapper");
        }

        try
        {
            DisplayWatcherHandshake.StartAndWait(BaseStart(root, out _), 4321, 987654321,
                new(_ => new FakeStartedWatcherProcess(), TimeSpan.FromMilliseconds(2)), TimeSpan.FromSeconds(11));
            throw new Exception("Expected overlong readiness timeout rejection");
        }
        catch (ArgumentOutOfRangeException)
        {
            check(true, "parent never permits a display-watcher readiness wait longer than ten seconds");
        }
    }

    private static void ChildBindsProtectedIdentityBeforeSignal(string root, Action<bool, string> check)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
            DisplayWatcherHandshake.ReadyEventName(nonce), out var created);
        check(created, "child handshake test owns a fresh local readiness event");
        var currentPath = Path.Combine(root, new string('A', 64) + ".json");
        var leaseId = Guid.NewGuid();
        var request = DisplayWatcherHandshake.ParseChildArguments(
            [DisplayWatcherHandshake.Command, currentPath, leaseId.ToString("D"), "4321", "987654321", nonce]);
        var owner = new FakeRetainedOwnerProcess(4321, 987654321);
        using (var child = DisplayWatcherHandshake.OpenChild(request, new(_ => owner)))
        {
            try
            {
                child.SignalReadyAfterLoopEntry();
                throw new Exception("Expected pre-validation signal rejection");
            }
            catch (InvalidOperationException)
            {
                check(!ready.WaitOne(0), "child cannot signal readiness before protected state validation");
            }
            try
            {
                child.ConfirmProtectedStateValidated(currentPath, leaseId, 4321, 987654322);
                throw new Exception("Expected protected owner mismatch rejection");
            }
            catch (InvalidDataException)
            {
                check(!ready.WaitOne(0), "mismatched protected owner state cannot complete the handshake");
            }

            child.ConfirmProtectedStateValidated(currentPath, leaseId, 4321, 987654321);
            check(child.ObserveOwner(4321, 987654321) == DisplayWatcherHandshake.OwnerObservation.VerifiedRunning &&
                !owner.Disposed, "child retains the exact verified owner handle throughout watcher startup");
            child.SignalReadyAfterLoopEntry();
            check(ready.WaitOne(TimeSpan.FromMilliseconds(100)),
                "child signals only after protected identity is bound inside the watcher loop");
            owner.HasExitedValue = true;
            check(child.ObserveOwner(4321, 987654321) == DisplayWatcherHandshake.OwnerObservation.Exited,
                "watcher observes owner exit through the retained exact process handle");
        }
        check(owner.Disposed, "child releases the retained owner handle after the watcher loop ends");
    }

    private static void ChildRejectsOwnerReuse(string root, Action<bool, string> check)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
            DisplayWatcherHandshake.ReadyEventName(nonce), out _);
        var request = DisplayWatcherHandshake.ParseChildArguments(
            [DisplayWatcherHandshake.Command, Path.Combine(root, new string('B', 64) + ".json"),
                Guid.NewGuid().ToString("D"), "4321", "987654321", nonce]);
        var reused = new FakeRetainedOwnerProcess(4321, 987654322);
        try
        {
            using var _ = DisplayWatcherHandshake.OpenChild(request, new(_ => reused));
            throw new Exception("Expected PID reuse rejection");
        }
        catch (IOException)
        {
            check(reused.Disposed && !ready.WaitOne(0),
                "PID reuse or owner start-time mismatch is rejected before readiness");
        }
    }

    private static void ProductionWrapperRetainsExactKernelHandle(string root, Action<bool, string> check)
    {
        using var current = Process.GetCurrentProcess();
        var expectedStart = current.StartTime.ToUniversalTime().Ticks;
        var retained = new DisplayWatcherHandshake.RetainedOwnerProcess(Environment.ProcessId);
        check(retained.Id == Environment.ProcessId && retained.StartTimeUtcTicks == expectedStart &&
            retained.HasRetainedKernelHandle && !retained.HasExited,
            "production watcher wrapper retains the exact current-process kernel handle");
        retained.Dispose();
        check(!retained.HasRetainedKernelHandle,
            "production watcher wrapper closes its retained kernel handle when the watcher ends");

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
            DisplayWatcherHandshake.ReadyEventName(nonce), out _);
        var mismatched = DisplayWatcherHandshake.ParseChildArguments(
            [DisplayWatcherHandshake.Command, Path.Combine(root, new string('E', 64) + ".json"),
                Guid.NewGuid().ToString("D"), Environment.ProcessId.ToString(),
                (expectedStart + 1).ToString(), nonce]);
        try
        {
            using var _ = DisplayWatcherHandshake.OpenChild(mismatched);
            throw new Exception("Expected production owner start-time mismatch rejection");
        }
        catch (IOException)
        {
            check(!ready.WaitOne(0),
                "production watcher wrapper still rejects PID/start-time mismatch before readiness");
        }
    }

    private static void ArgumentsAreStrictAndNonceIsRedacted(string root, Action<bool, string> check)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var currentPath = Path.Combine(root, new string('C', 64) + ".json");
        var leaseId = Guid.NewGuid();
        var valid = new[] { DisplayWatcherHandshake.Command, currentPath, leaseId.ToString("D"), "4321", "987654321", nonce };
        var request = DisplayWatcherHandshake.ParseChildArguments(valid);
        check(!request.ToString().Contains(nonce, StringComparison.Ordinal) &&
            request.ToString().Contains("[nonce-redacted]", StringComparison.Ordinal),
            "child request diagnostics redact the rendezvous nonce");

        var malformed = new List<string[]>
        {
            valid.Concat(new[] { "extra" }).ToArray(),
            new[] { DisplayWatcherHandshake.Command, "relative.json", leaseId.ToString("D"), "4321", "987654321", nonce },
            new[] { DisplayWatcherHandshake.Command, currentPath, leaseId.ToString("N"), "4321", "987654321", nonce },
            new[] { DisplayWatcherHandshake.Command, currentPath, leaseId.ToString("D"), "04321", "987654321", nonce },
            new[] { DisplayWatcherHandshake.Command, currentPath, leaseId.ToString("D"), "4321", "987654321", nonce.ToLowerInvariant() }
        };
        var rejected = 0;
        var leaked = false;
        foreach (var args in malformed)
        {
            try { _ = DisplayWatcherHandshake.ParseChildArguments(args); }
            catch (ArgumentException ex)
            {
                rejected++;
                leaked |= ex.ToString().Contains(nonce, StringComparison.Ordinal);
            }
        }
        check(rejected == malformed.Count, "child rejects extra, relative, noncanonical and malformed handshake arguments");
        check(!leaked, "argument-validation failures never include the rendezvous nonce");
    }

    private static ProcessStartInfo BaseStart(string root, out Guid leaseId)
    {
        leaseId = Guid.NewGuid();
        var start = new ProcessStartInfo("fake-tablink.exe") { UseShellExecute = false };
        start.ArgumentList.Add(DisplayWatcherHandshake.Command);
        start.ArgumentList.Add(Path.Combine(root, new string('D', 64) + ".json"));
        start.ArgumentList.Add(leaseId.ToString("D"));
        return start;
    }

    private sealed class FakeStartedWatcherProcess : DisplayWatcherHandshake.IStartedWatcherProcess
    {
        internal bool HasExitedValue { get; set; }
        internal int ExitCodeValue { get; set; }
        internal bool Disposed { get; private set; }
        public bool HasExited => HasExitedValue;
        public int ExitCode => ExitCodeValue;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeRetainedOwnerProcess(int id, long startTimeUtcTicks) :
        DisplayWatcherHandshake.IRetainedOwnerProcess
    {
        internal bool HasExitedValue { get; set; }
        internal bool Disposed { get; private set; }
        public int Id { get; } = id;
        public long StartTimeUtcTicks { get; } = startTimeUtcTicks;
        public bool HasExited => HasExitedValue;
        public void Dispose() => Disposed = true;
    }
}
