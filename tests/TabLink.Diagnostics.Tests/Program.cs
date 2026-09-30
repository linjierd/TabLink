using System.Collections.Concurrent;
using System.Text.Json;
using TabLink.Windows;

// Compile-linked persistence tests only. No MainForm, capture, display driver,
// running application, device, real diagnostics directory or ACL is touched.
var root = Path.Combine(Path.GetTempPath(), "TabLink-Diagnostics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assertions = 0;
var scenarios = 0;
try
{
    await Run("creates output directory and replaces a complete JSON snapshot", () =>
    {
        var folder = Folder("normal");
        var store = new DiagnosticStore(folder);
        Check(store.TrySave("health.json", () => new { connected = true, frames = 19 }), "initial save succeeds");
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "health.json"))))
            Check(document.RootElement.GetProperty("frames").GetInt32() == 19, "saved snapshot is readable JSON");
        Check(store.TrySave("health.json", () => new { connected = true, frames = 71 }), "replacement save succeeds");
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "health.json"))))
            Check(document.RootElement.GetProperty("frames").GetInt32() == 71, "replacement has the latest snapshot");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("locked destination retains last good snapshot and recovers", () =>
    {
        var folder = Folder("locked");
        var store = new DiagnosticStore(folder);
        var path = Path.Combine(folder, "health.json");
        Check(store.TrySave("health.json", () => new { frames = 1 }), "seed save succeeds");
        var previous = File.ReadAllBytes(path);
        var warnings = new List<string>();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!store.TrySave("health.json", () => new { frames = 2 }, warnings.Add), "locked replacement reports false");
            Check(!store.TrySave("health.json", () => new { frames = 3 }, warnings.Add), "repeated locked replacement reports false");
            Check(previous.SequenceEqual(File.ReadAllBytes(path)), "last good JSON remains byte-for-byte intact");
            Check(warnings.Count == 1, "one warning during repeated write failure");
            Check(!string.IsNullOrWhiteSpace(warnings[0]), "failure warning is meaningful");
            CheckNoTemporaryFiles(folder, "health.json");
        }
        Check(store.TrySave("health.json", () => new { frames = 4 }, warnings.Add), "unlock allows recovery");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(!store.TrySave("health.json", () => new { frames = 5 }, warnings.Add), "later failure is contained again");
        Check(warnings.Count == 2, "successful recovery re-enables a later warning");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("a file blocking the output directory is contained and retryable", () =>
    {
        var folder = Folder("blocked-root");
        File.WriteAllText(folder, "fixture sentinel");
        var store = new DiagnosticStore(folder);
        var warnings = new List<string>();
        Check(!store.TrySave("health.json", () => new { frames = 1 }, warnings.Add), "blocked directory reports false");
        Check(!store.TrySave("health.json", () => new { frames = 2 }, warnings.Add), "repeated directory failure reports false");
        Check(File.ReadAllText(folder) == "fixture sentinel", "blocking file remains untouched");
        Check(warnings.Count == 1, "directory failure is not repeatedly reported");
        File.Delete(folder);
        Check(store.TrySave("health.json", () => new { frames = 3 }, warnings.Add), "retry creates recovered directory");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("snapshot collection exceptions do not truncate earlier JSON", () =>
    {
        var folder = Folder("capture-failure");
        var store = new DiagnosticStore(folder);
        var path = Path.Combine(folder, "health.json");
        Check(store.TrySave("health.json", () => new { frames = 18 }), "seed save succeeds");
        var previous = File.ReadAllBytes(path);
        var calls = 0;
        Check(!store.TrySave("health.json", () => { calls++; throw new InvalidOperationException("snapshot unavailable"); }), "capture failure is contained");
        Check(calls == 1, "snapshot delegate is invoked exactly once");
        Check(previous.SequenceEqual(File.ReadAllBytes(path)), "capture failure retains previous bytes");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("cyclic data and failing property getter cannot truncate JSON", () =>
    {
        var folder = Folder("serialization-failure");
        var store = new DiagnosticStore(folder);
        var path = Path.Combine(folder, "health.json");
        Check(store.TrySave("health.json", () => new { frames = 21 }), "seed save succeeds");
        var previous = File.ReadAllBytes(path);
        var cycle = new CyclicSnapshot();
        cycle.Next = cycle;
        Check(!store.TrySave("health.json", () => cycle), "cyclic snapshot failure is contained");
        Check(previous.SequenceEqual(File.ReadAllBytes(path)), "cycle failure keeps earlier JSON");
        Check(!store.TrySave("health.json", () => new ThrowingPropertySnapshot()), "property getter failure is contained");
        Check(previous.SequenceEqual(File.ReadAllBytes(path)), "property getter failure keeps earlier JSON");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("failure notification exceptions do not escape persistence", () =>
    {
        var folder = Folder("notification-failure");
        File.WriteAllText(folder, "fixture sentinel");
        var store = new DiagnosticStore(folder);
        var callbacks = 0;
        void ThrowingNotification(string message) { callbacks++; throw new InvalidOperationException("UI is closing"); }
        Check(!store.TrySave("health.json", () => new { frames = 1 }, ThrowingNotification), "throwing notifier is contained");
        Check(!store.TrySave("health.json", () => new { frames = 2 }, ThrowingNotification), "subsequent failure remains contained");
        Check(callbacks == 1, "throwing notifier is still suppressed during the same failure interval");
        File.Delete(folder);
        Check(store.TrySave("health.json", () => new { frames = 3 }, ThrowingNotification), "notifier failure does not prevent recovery");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("warning suppression is per file and starts only after a reporter is present", () =>
    {
        var folder = Folder("per-file-warning");
        var store = new DiagnosticStore(folder);
        var warnings = new List<string>();
        Check(!store.TrySave("health.json", () => throw new IOException("capture unavailable")), "unreported failure is contained");
        Check(!store.TrySave("health.json", () => throw new IOException("capture unavailable"), warnings.Add), "later reported failure is contained");
        Check(warnings.Count == 1, "absence of a reporter does not consume the warning");
        Check(store.TrySave("other.json", () => new { frames = 90 }), "an unrelated diagnostic file can save");
        Check(!store.TrySave("health.json", () => throw new IOException("capture unavailable"), warnings.Add), "health failure remains contained");
        Check(warnings.Count == 1, "unrelated successful output does not reset failed health warning");
        Check(!store.TrySave("second.json", () => throw new IOException("capture unavailable"), warnings.Add), "separate file failure is contained");
        Check(warnings.Count == 2, "another failed file has its own warning");
        Check(store.TrySave("health.json", () => new { frames = 180 }), "health recovers");
        Check(!store.TrySave("health.json", () => throw new IOException("capture unavailable"), warnings.Add), "a new failure interval is contained");
        Check(warnings.Count == 3, "same-file recovery resets warning suppression");
        CheckNoTemporaryFiles(folder, "health.json", "other.json");
        return Task.CompletedTask;
    });

    await Run("partial staged write followed by simulated disk full preserves last good JSON", () =>
    {
        var folder = Folder("disk-full");
        var failWrites = false;
        var store = new DiagnosticStore(folder, (stream, bytes) =>
        {
            if (!failWrites) { stream.Write(bytes); return; }
            stream.Write(bytes.AsSpan(0, Math.Min(7, bytes.Length)));
            throw new IOException("Simulated disk full after partial staged write", unchecked((int)0x80070070));
        });
        var path = Path.Combine(folder, "health.json");
        Check(store.TrySave("health.json", () => new { frames = 100 }), "disk-full fixture seed succeeds");
        var previous = File.ReadAllBytes(path);
        failWrites = true;
        Check(!store.TrySave("health.json", () => new { frames = 200 }), "partial-write disk-full exception is contained");
        Check(previous.SequenceEqual(File.ReadAllBytes(path)), "partial staged write cannot truncate committed JSON");
        CheckNoTemporaryFiles(folder, "health.json");
        failWrites = false;
        Check(store.TrySave("health.json", () => new { frames = 300 }), "space-recovery simulation permits a later successful save");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Check(document.RootElement.GetProperty("frames").GetInt32() == 300, "recovered snapshot contains the latest value");
        CheckNoTemporaryFiles(folder, "health.json");
        return Task.CompletedTask;
    });

    await Run("invalid file names cannot escape the diagnostics directory", () =>
    {
        var folder = Folder("path-safety");
        var store = new DiagnosticStore(folder);
        var outside = Path.Combine(root, "outside.json");
        File.WriteAllText(outside, "outside sentinel");
        string[] invalidNames = ["", ".", "..", "../outside.json", "..\\outside.json", outside, "nested/health.json", "nested\\health.json", "health.json:stream", "bad?name.json", "bad\0name.json", "CON.json", "nul.json", "COM1.json", "LPT9.json"];
        foreach (var name in invalidNames)
            Check(!store.TrySave(name, () => new { frames = 1 }), "invalid file name rejected: " + name.Replace("\0", "[NUL]"));
        Check(File.ReadAllText(outside) == "outside sentinel", "outside sentinel cannot be overwritten");
        Check(!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(), "invalid names leave no files below output root");
        return Task.CompletedTask;
    });

    await Run("relative output root cannot fall back into a working directory", () =>
    {
        var originalWorkingDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var store = new DiagnosticStore("relative-diagnostics");
            var captured = false;
            Check(!store.TrySave("health.json", () => { captured = true; return new { frames = 1 }; }), "relative root is contained");
            Check(!captured, "invalid root is rejected before collecting the snapshot");
            Check(!Directory.Exists(Path.Combine(root, "relative-diagnostics")), "no accidental working-directory output is created");
        }
        finally { Environment.CurrentDirectory = originalWorkingDirectory; }
        return Task.CompletedTask;
    });

    await Run("one store serializes concurrent writers without failed snapshots", async () =>
    {
        var folder = Folder("concurrent-writers");
        var store = new DiagnosticStore(folder);
        var writes = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var revision = 1; revision <= 20; revision++)
            {
                var snapshot = new ConcurrentSnapshot(writer, revision, new string((char)('a' + writer), 16384));
                if (store.TrySave("health.json", () => snapshot)) Interlocked.Increment(ref writes);
            }
        })));
        Check(writes == 160, "all concurrent writers on one store save successfully");
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "health.json"))))
            Check(document.RootElement.GetProperty("Revision").GetInt32() == 20, "serialized writers finish with a complete last revision");
        CheckNoTemporaryFiles(folder, "health.json");
    });

    foreach (var storeCount in new[] { 1, 4 })
    await Run($"reader contention with {storeCount} store(s) never exposes partial JSON or leaks temporary files", async () =>
    {
        var folder = Folder("concurrent-" + storeCount);
        var stores = Enumerable.Range(0, storeCount).Select(_ => new DiagnosticStore(folder)).ToArray();
        var path = Path.Combine(folder, "health.json");
        Check(stores[0].TrySave("health.json", () => new ConcurrentSnapshot(0, 0, new string('a', 16384))), "concurrent seed succeeds");
        using var stop = new CancellationTokenSource();
        var failures = new ConcurrentQueue<string>();
        var writeFailures = new ConcurrentQueue<string>();
        var writes = 0;
        var reads = 0;
        var readContentions = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    var payload = document.RootElement.GetProperty("Payload").GetString();
                    if (payload is null || payload.Length != 16384 || payload.Any(c => c != payload[0]))
                        failures.Enqueue("reader observed incomplete or mixed payload");
                    Interlocked.Increment(ref reads);
                }
                // Windows replacement and an external reader can race with a
                // pending delete. This test requires complete successful reads,
                // not uninterrupted availability of a best-effort diagnostic.
                catch (IOException) { Interlocked.Increment(ref readContentions); }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref readContentions); }
                catch (Exception ex) { failures.Enqueue("reader: " + ex.GetType().Name + ": " + ex.Message); }
                Thread.Yield();
            }
        });
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
            {
                for (var revision = 1; revision <= 20; revision++)
                {
                    var snapshot = new ConcurrentSnapshot(writer, revision, new string((char)('a' + writer), 16384));
                    if (stores[writer % stores.Length].TrySave("health.json", () => snapshot, writeFailures.Enqueue)) Interlocked.Increment(ref writes);
                }
            })));
        }
        finally { stop.Cancel(); await reader; }
        Check(writes > 0, "writers publish snapshots despite transient contention: " + string.Join("; ", writeFailures.Take(3)));
        Check(reads > 0, "reader sampled the output during concurrent writes");
        Check(failures.IsEmpty, "all sampled snapshots are complete: " + string.Join("; ", failures.Take(3)));
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var finalRevision = document.RootElement.GetProperty("Revision").GetInt32();
            Check(finalRevision is >= 1 and <= 20, "final snapshot is a writer's complete committed revision");
        }
        CheckNoTemporaryFiles(folder, "health.json");
        Console.WriteLine($"  contention sample: {writes}/160 writes committed, {reads} complete reads, {readContentions} transient read conflicts");
    });

    await Run("a monitor-like loop continues when diagnostic persistence fails", () =>
    {
        var folder = Folder("monitor-like");
        File.WriteAllText(folder, "fixture sentinel");
        var store = new DiagnosticStore(folder);
        var stopped = false;
        var completedTicks = 0;
        try
        {
            for (var tick = 0; tick < 4; tick++)
            {
                _ = store.TrySave("health.json", () => new { frames = tick * 90 });
                completedTicks++;
            }
        }
        catch { stopped = true; }
        Check(!stopped, "diagnostic I/O failure cannot reach the simulated monitor stop branch");
        Check(completedTicks == 4, "all simulated health ticks continue");
        return Task.CompletedTask;
    });

    Console.WriteLine($"PASS: {scenarios} diagnostic persistence scenarios, {assertions} assertions. Temporary filesystem fixtures only; real MainForm and live connection were not exercised.");
}
finally
{
    var checkedRoot = Path.GetFullPath(root);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (checkedRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(checkedRoot).StartsWith("TabLink-Diagnostics-", StringComparison.Ordinal)
        && Directory.Exists(checkedRoot))
        Directory.Delete(checkedRoot, recursive: true);
}

string Folder(string name) => Path.Combine(root, name);
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    assertions++;
}
void CheckNoTemporaryFiles(string folder, params string[] expectedNames)
{
    var actual = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    Check(actual.SequenceEqual(expectedNames.Order(StringComparer.Ordinal)), "only committed JSON files remain in " + Path.GetFileName(folder));
}
async Task Run(string name, Func<Task> scenario)
{
    await scenario();
    scenarios++;
    Console.WriteLine("PASS " + name);
}

sealed class CyclicSnapshot
{
    public CyclicSnapshot? Next { get; set; }
}
sealed class ThrowingPropertySnapshot
{
    public string Value => throw new InvalidOperationException("snapshot property unavailable");
}
sealed record ConcurrentSnapshot(int Writer, int Revision, string Payload);
