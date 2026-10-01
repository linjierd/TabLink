using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    await Run("support bundle exports only the fixed reviewed entry allow-list", () =>
    {
        var folder = Folder("support-bundle");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "TabLink-Support.zip");
        var prepared = SupportBundleExporter.Prepare(SampleSupportBundle());
        SupportBundleExporter.WriteAtomic(target, prepared);
        Check(File.Exists(target), "support ZIP is committed");
        using var archive = ZipFile.OpenRead(target);
        string[] expected = ["manifest.json", "compatibility.json", "diagnostics.json", "issue-summary.txt", "README.txt"];
        Check(archive.Entries.Select(entry => entry.FullName).SequenceEqual(expected), "support ZIP has the exact ordered allow-list");
        Check(archive.Entries.All(entry => entry.LastWriteTime.Year==2000&&entry.LastWriteTime.Month==1&&entry.LastWriteTime.Day==1&&
            entry.LastWriteTime.Hour==0&&entry.LastWriteTime.Minute==0&&entry.LastWriteTime.Second==0),
            "support ZIP entry timestamps are fixed and reveal no export timezone");
        Check(archive.Entries.All(entry=>entry.ExternalAttributes==0),"support ZIP entry attributes are fixed");
        var extracted = archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }, StringComparer.Ordinal);
        Check(extracted.All(item => prepared.Entries[item.Key].SequenceEqual(item.Value)), "ZIP bytes exactly match the in-app preview payload");
        var combined = Encoding.UTF8.GetString(extracted.Values.SelectMany(value => value).ToArray());
        foreach (var forbidden in new[] { @"C:\Users\TEST-USER\Private", "10.20.30.40", "TEST-SERIAL-001", "USB\\VID_1234&PID_5678", "PAIRING-TOKEN-TEST" })
            Check(!combined.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "support ZIP excludes marker: " + forbidden);
        using (var compatibility = JsonDocument.Parse(extracted["compatibility.json"]))
        {
            var rootElement = compatibility.RootElement;
            Check(rootElement.GetProperty("tabLinkVersion").GetString() == "0.8.8", "compatibility keeps the useful application version");
            Check(rootElement.GetProperty("windowsVersion").GetString() == "10.0.26100", "compatibility keeps the Windows build");
            Check(rootElement.GetProperty("display").GetProperty("requestedRefreshRate").GetInt32() == 90, "compatibility keeps the requested refresh rate");
            Check(rootElement.GetProperty("encoder").GetProperty("encoder").GetString() == "nvenc", "compatibility keeps the encoder class");
        }
        using (var diagnostics = JsonDocument.Parse(extracted["diagnostics.json"]))
        {
            Check(diagnostics.RootElement.GetProperty("health").GetArrayLength() == 6, "diagnostics contains all six typed health stages");
            Check(diagnostics.RootElement.GetProperty("clientConnected").GetBoolean(),"diagnostics labels real client connectivity explicitly");
            Check(!diagnostics.RootElement.TryGetProperty("connectionActive",out _),"diagnostics does not conflate pending work with a connected client");
        }
        using (var manifest = JsonDocument.Parse(extracted["manifest.json"]))
        {
            var entries = manifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Check(entries.Length == 4, "manifest hashes every non-manifest entry");
            foreach (var entry in entries)
            {
                var name = entry.GetProperty("name").GetString()!;
                Check(entry.GetProperty("size").GetInt32() == extracted[name].Length, "manifest size matches " + name);
                Check(entry.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(extracted[name])), "manifest hash matches " + name);
            }
        }
        Check(!Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path).StartsWith(".TabLink-Support.zip.", StringComparison.Ordinal)), "successful export leaves no staging file");
        return Task.CompletedTask;
    });

    await Run("support bundle rejects free-form metadata and preview divergence", () =>
    {
        var sample = SampleSupportBundle();
        foreach(var type in new[] { typeof(SupportBundleSnapshot),typeof(SupportDisplayProfile),typeof(SupportVideoStatus),typeof(SupportHealthStep) })
            Check(!type.GetProperties().Any(property=>property.PropertyType==typeof(string)),
                type.Name+" exposes no free-form string field for paths, serials, addresses or tokens");
        CheckThrows<ArgumentNullException>(() => SupportBundleExporter.Prepare(sample with
        {
            ApplicationVersion = null!
        }), "missing typed application version is rejected");
        CheckThrows<ArgumentOutOfRangeException>(() => SupportBundleExporter.Prepare(sample with
        {
            WindowsVersion = new Version(10,0,1_000_000,0)
        }), "unbounded numeric token cannot masquerade as version metadata");
        CheckThrows<ArgumentOutOfRangeException>(() => SupportBundleExporter.Prepare(sample with
        {
            OsArchitecture = (Architecture)int.MaxValue
        }), "unknown architecture enum is rejected");
        var duplicate = sample.Health.ToArray();
        duplicate[5] = duplicate[0];
        CheckThrows<ArgumentException>(() => SupportBundleExporter.Prepare(sample with { Health = duplicate }), "duplicate health stage is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.Prepare(sample with { Health = sample.Health.Reverse().ToArray() }),
            "out-of-order health stages are rejected");

        Check(SupportBundleExporter.MapEncoderBackend("Nvenc")==SupportEncoderBackend.Nvenc,"known encoder backend maps exactly");
        CheckThrows<ArgumentOutOfRangeException>(()=>SupportBundleExporter.MapEncoderBackend("FutureBackend"),
            "unknown encoder backend fails closed");
        Check(SupportBundleExporter.NormalizeRate(double.NaN) is null&&SupportBundleExporter.NormalizeRate(double.PositiveInfinity) is null&&
            SupportBundleExporter.NormalizeRate(1001) is null,"unknown or out-of-range rates remain unavailable instead of becoming zero");
        var sensitivePath=@"E:\Private\User Name\support.zip";
        Check(!SupportBundleExporter.FailureSummary(new IOException("write failed: "+sensitivePath)).Contains(sensitivePath,StringComparison.Ordinal),
            "I/O failure summary does not retain a destination path");
        Check(!SupportBundleExporter.FailureSummary(new UnauthorizedAccessException("denied: "+sensitivePath)).Contains(sensitivePath,StringComparison.Ordinal),
            "access failure summary does not retain a destination path");

        var prepared = SupportBundleExporter.Prepare(sample);
        var folder = Folder("support-preview-mismatch");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "support.zip");
        CheckThrows<InvalidDataException>(() => SupportBundleExporter.WriteAtomic(target,
            prepared with { PreviewText = prepared.PreviewText + "unexpected" }), "changed content cannot differ from the reviewed preview");
        Check(!File.Exists(target), "preview mismatch creates no target");
        Check(!Directory.EnumerateFiles(folder).Any(), "preview mismatch creates no staging file");

        var changedEntries=prepared.Entries.ToDictionary(item=>item.Key,item=>item.Value.ToArray(),StringComparer.Ordinal);
        changedEntries["diagnostics.json"][^2]^=1;
        CheckThrows<InvalidDataException>(()=>SupportBundleExporter.WriteAtomic(target,
            new PreparedSupportBundle(changedEntries,PreviewFor(changedEntries))),
            "manifest hashes are revalidated even when content and preview are changed together");
        Check(!Directory.EnumerateFiles(folder).Any(),"manifest mismatch creates no staging file");

        var valid = SupportBundleExporter.Prepare(sample);
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,"support.zip:secret.zip"),valid),
            "NTFS alternate data stream syntax is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,".zip"),valid),
            "empty ZIP base name is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,"support.zip."),valid),
            "trailing dot is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,"support.zip "),valid),
            "trailing space is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,"CON.zip"),valid),
            "Windows reserved device name is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,"support.txt"),valid),
            "wrong extension is rejected");
        CheckThrows<ArgumentException>(() => SupportBundleExporter.WriteAtomic("relative-support.zip",valid),
            "relative destination is rejected");
        CheckThrows<DirectoryNotFoundException>(() => SupportBundleExporter.WriteAtomic(Path.Combine(folder,"missing","support.zip"),valid),
            "missing destination directory is rejected without fallback");
        Check(!Directory.EnumerateFiles(folder).Any(), "invalid destinations create no file or alternate stream");
        return Task.CompletedTask;
    });

    await Run("support bundle reports only a current connected display", () =>
    {
        var stale=new SupportDisplayProfile(1920,1200,1200,1920,1,90);
        var pendingCandidate=SupportBundleExporter.CreateNativeCandidate(false,null,stale);
        Check(!pendingCandidate.ClientConnected&&pendingCandidate.Display is null,
            "disconnected native source discards a prepared or last-known display");
        var adbCandidate=SupportBundleExporter.CreateNativeCandidate(true,null,stale);
        Check(adbCandidate.ClientConnected&&ReferenceEquals(adbCandidate.Display,stale),
            "connected ADB source uses its lifecycle-bound prepared display when no network profile exists");
        var reported=new SupportDisplayProfile(1200,1920,1200,1920,0,60);
        var networkCandidate=SupportBundleExporter.CreateNativeCandidate(true,reported,stale);
        Check(ReferenceEquals(networkCandidate.Display,reported),"current client-reported profile takes priority over a prepared fallback");
        var pending=SupportBundleExporter.SelectCurrentConnection([pendingCandidate],false,stale);
        Check(!pending.ClientConnected&&pending.Display is null&&pending.NativeSourceIndex==-1,
            "pending or last-known profiles are not reported as a current display");
        var native=SupportBundleExporter.SelectCurrentConnection([adbCandidate],false,null);
        Check(native.ClientConnected&&ReferenceEquals(native.Display,stale)&&native.NativeSourceIndex==0,
            "connected primary native display is selected");
        var additional=SupportBundleExporter.SelectCurrentConnection([new(false,null),new(true,stale)],false,null);
        Check(additional.ClientConnected&&ReferenceEquals(additional.Display,stale)&&additional.NativeSourceIndex==1,
            "connected native-client session is selected when the primary listener is idle");
        var browser=SupportBundleExporter.SelectCurrentConnection([],true,stale);
        Check(browser.ClientConnected&&ReferenceEquals(browser.Display,stale),"streaming browser display is selected");
        var nativeWins=SupportBundleExporter.SelectCurrentConnection([new(true,stale)],true,new SupportDisplayProfile(720,1280,720,1280,0,60));
        Check(ReferenceEquals(nativeWins.Display,stale)&&nativeWins.NativeSourceIndex==0,
            "native display wins deterministically if an impossible dual-state snapshot is observed");
        return Task.CompletedTask;
    });

    await Run("support bundle failed write preserves the previous target and cleans staging", () =>
    {
        var folder = Folder("support-atomic-failure");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "support.zip");
        var previous = Encoding.ASCII.GetBytes("previous support artifact");
        File.WriteAllBytes(target, previous);
        var prepared = SupportBundleExporter.Prepare(SampleSupportBundle());
        CheckThrows<IOException>(() => SupportBundleExporter.WriteAtomic(target, prepared, (stream, _) =>
        {
            stream.Write([1, 2, 3, 4]);
            throw new IOException("simulated archive failure");
        }), "partial archive failure propagates");
        Check(File.ReadAllBytes(target).SequenceEqual(previous), "failed export retains the previous target byte-for-byte");
        Check(Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(new[] { "support.zip" }), "failed export removes its staging file");
        using (var held=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read))
            CheckThrowsEither<IOException,UnauthorizedAccessException>(()=>SupportBundleExporter.WriteAtomic(target,prepared),
                "locked destination rejects replacement");
        Check(File.ReadAllBytes(target).SequenceEqual(previous),"locked replacement retains the previous target byte-for-byte");
        Check(Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(new[] { "support.zip" }),
            "locked replacement removes its staging file");
        SupportBundleExporter.WriteAtomic(target, prepared);
        using var archive = ZipFile.OpenRead(target);
        Check(archive.Entries.Count == 5, "a later retry atomically replaces the old target with a complete ZIP");
        return Task.CompletedTask;
    });

    Console.WriteLine($"PASS: {scenarios} diagnostics/support-bundle scenarios, {assertions} assertions. Temporary filesystem fixtures only; real MainForm and live connection were not exercised.");
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
SupportBundleSnapshot SampleSupportBundle()
{
    SupportHealthStep[] health =
    [
        new(SupportHealthStage.RouteAndListener,SupportHealthState.Healthy,SupportHealthReason.Ready,SupportHealthRecovery.None),
        new(SupportHealthStage.AuthenticationAndDisplayProfile,SupportHealthState.Healthy,SupportHealthReason.DisplayProfileReceived,SupportHealthRecovery.None),
        new(SupportHealthStage.SingleVirtualDisplay,SupportHealthState.Healthy,SupportHealthReason.DisplayReady,SupportHealthRecovery.None),
        new(SupportHealthStage.CaptureEncodeSend,SupportHealthState.Healthy,SupportHealthReason.FrameSent,SupportHealthRecovery.None),
        new(SupportHealthStage.ClientDecodeSubmission,SupportHealthState.Healthy,SupportHealthReason.DecodeSubmitted,SupportHealthRecovery.None),
        new(SupportHealthStage.ClientPresentationCallback,SupportHealthState.Healthy,SupportHealthReason.FramePresented,SupportHealthRecovery.None)
    ];
    return new(new DateTimeOffset(2026,10,2,2,30,0,TimeSpan.Zero),new Version(0,8,8),new Version(10,0,26100),new Version(10,0,0),Architecture.X64,Architecture.X64,
        SupportQualityPreset.Automatic,SupportEncoderPreference.Automatic,false,true,SupportConnectionPath.NativeNetwork,
        new SupportDisplayProfile(1920,1200,1200,1920,1,90),
        new SupportVideoStatus(SupportEncoderBackend.Nvenc,true,1920,1200,90,90,false,10800,10790,10780,89.8,89.7),health);
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    assertions++;
}
void CheckThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { assertions++; return; }
    throw new InvalidOperationException("FAILED: " + message);
}
void CheckThrowsEither<TFirst,TSecond>(Action action,string message) where TFirst:Exception where TSecond:Exception
{
    try { action(); }
    catch(Exception error) when(error is TFirst or TSecond) { assertions++; return; }
    throw new InvalidOperationException("FAILED: "+message);
}
string PreviewFor(IReadOnlyDictionary<string,byte[]> entries)
{
    string[] order=["manifest.json","compatibility.json","diagnostics.json","issue-summary.txt","README.txt"];
    return string.Join("\r\n\r\n",order.Select(name=>$"===== {name} =====\r\n{Encoding.UTF8.GetString(entries[name])}"));
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
