using System.Diagnostics;
using System.Text.Json;

namespace TabLink.Windows;

// The watcher runs outside the UI process so a host crash still retires the
// selected virtual output. A shared lock covers ownership checks and mutation.
internal sealed class SessionGuard : IDisposable
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
    internal static string LastDisplayPath => Path.Combine(Folder, "last-display.json");
    internal static string LeaseFolder => Path.Combine(Folder, "display-leases");
    internal static string LeasePath(DisplayLease lease) => Path.Combine(LeaseFolder, VirtualDisplayManager.GetTargetStorageKey(lease) + ".json");
    internal static string RememberedPath(DisplayLease lease) => Path.Combine(LeaseFolder, VirtualDisplayManager.GetTargetStorageKey(lease) + ".last.json");
    static readonly string LeaseMutex = @"Local\TabLink.DisplayLease." + Process.GetCurrentProcess().SessionId;
    readonly string path;
    readonly DisplayLease lease;
    DisplayLease? rememberedLease;
    readonly long ownerStartTicks;
    UsbReverseLease? reverseLease;
    volatile bool stopRequested;
    bool cleanupComplete;

    internal DisplayLease Lease => lease;

    // Reconstitute an already-published lease without launching a watcher. The
    // isolated lifecycle harness supplies temporary state and a fake adapter.
    internal SessionGuard(DisplayLease lease, string ownershipPath, long ownerStartTicks)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipPath);
        if (lease.LeaseId == Guid.Empty || ownerStartTicks <= 0)
            throw new IOException("副屏会话标识无效");
        this.lease = lease;
        rememberedLease = lease;
        path = ownershipPath;
        this.ownerStartTicks = ownerStartTicks;
    }

    internal SessionGuard(DisplayLease lease, bool requireUnowned = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.LeaseId == Guid.Empty) throw new IOException("副屏会话标识无效");
        this.lease = lease;
        rememberedLease = lease;
        Directory.CreateDirectory(LeaseFolder);
        using var owner = Process.GetCurrentProcess();
        ownerStartTicks = owner.StartTime.ToUniversalTime().Ticks;
        path = LeasePath(lease);
        WithLeaseLock(() =>
        {
            if (requireUnowned && File.Exists(path) && File.Exists(MarkerPath(path)))
            {
                var ownerId = ReadMarker(path);
                if (ownerId != Guid.Empty)
                {
                    var existing = ReadState(path, ownerId);
                    if (!existing.StopRequested && OwnerIsRunning(existing))
                        throw new IOException("此虚拟显示目标仍由另一个连接占用。");
                }
            }
            CleanupPreviousOwner(path);
            RetireLegacyOwnership(lease);
            var state = new WatchState(lease, Environment.ProcessId, ownerStartTicks, DateTime.UtcNow.AddSeconds(25));
            try
            {
                // The immutable bootstrap lets a watcher recover even if its
                // first read of the renewable JSON encounters corruption.
                WriteAtomic(BootstrapPath(path, lease.LeaseId), state);
                WriteAtomic(RememberedPath(lease), lease);
                WriteAtomic(path, state);
                WriteAtomic(MarkerPath(path), lease.LeaseId);
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位 TabLink 程序"))
                { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--watch-display");
                start.ArgumentList.Add(path);
                start.ArgumentList.Add(lease.LeaseId.ToString());
                using var watcher = Process.Start(start) ?? throw new IOException("无法启动副屏保护程序");
            }
            catch (Exception startError)
            {
                stopRequested = true;
                // Constructor failure otherwise leaves no object for the caller
                // to dispose. The caller selected this exact lease before entry.
                var rollback = VirtualDisplayManager.Detach(lease);
                SaveDiagnostic("display-guard-start-error.json", new { timestamp = DateTimeOffset.Now, error = startError.Message, rollback });
                throw new IOException("无法启动副屏保护，已尝试收回副屏。" + rollback.Message, startError);
            }
            return 0;
        });
    }

    internal void Renew(DateTime deadlineUtc)
    {
        if (deadlineUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("副屏保护期限必须使用 UTC", nameof(deadlineUtc));
        WithLeaseLock(() =>
        {
            if (stopRequested) return 0;
            if (ReadMarker(path) != lease.LeaseId)
                throw new IOException("副屏会话所有权已变化，不能续约旧连接。");
            WriteAtomic(path, new WatchState(lease, Environment.ProcessId, ownerStartTicks, deadlineUtc, reverseLease));
            return 0;
        });
    }

    internal VirtualDisplayInfo RefreshRememberedLayout()
    {
        return WithLeaseLock(() =>
        {
            if (stopRequested || ReadMarker(path) != lease.LeaseId)
                throw new IOException("副屏所有权已经变化，不能记录旧会话的布局。");
            var current = VirtualDisplayManager.ResolveLayout(lease);
            if (rememberedLease is null || !VirtualDisplayManager.RememberedLayoutEquals(rememberedLease, current.RememberedLease))
            {
                WriteAtomic(RememberedPath(lease), current.RememberedLease);
                rememberedLease = current.RememberedLease;
            }
            return current.CurrentDisplay;
        });
    }

    internal static DisplayLease CaptureDetachedLease(int width, int height, int refreshRate)
    {
        return WithLeaseLock(() =>
        {
            var fresh = VirtualDisplayManager.CaptureDetachedLease(width, height, refreshRate);
            return ReuseRememberedPosition(fresh);
        });
    }

    internal static DisplayLease ReuseRememberedPosition(DisplayLease fresh)
    {
        try
        {
            var rememberedPath = RememberedPath(fresh);
            if (!File.Exists(rememberedPath)) rememberedPath = LastDisplayPath; // Read-only single-display migration.
            if (!File.Exists(rememberedPath)) return fresh;
            var remembered = JsonSerializer.Deserialize<DisplayLease>(File.ReadAllText(rememberedPath));
            return remembered is null ? fresh : VirtualDisplayManager.ReuseRememberedPosition(fresh, remembered);
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is InvalidOperationException or ArgumentException)
        {
            SaveDiagnostic("display-position-recovery.json", new { timestamp = DateTimeOffset.Now, error = ex.Message });
            return fresh;
        }
    }

    internal void AttachReverse(UsbReverseLease receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Id == Guid.Empty || receipt.OwnerPid != Environment.ProcessId || receipt.OwnerStartUtcTicks != ownerStartTicks)
            throw new IOException("USB 通道记录不属于当前连接进程");
        WithLeaseLock(() =>
        {
            if (stopRequested || ReadMarker(path) != lease.LeaseId)
                throw new IOException("副屏会话已结束，无法登记 USB 通道");
            if (reverseLease is not null && reverseLease != receipt)
                throw new IOException("本次连接已经登记了另一个 USB 通道");
            var state = ReadState(path, lease.LeaseId) with { ReverseLease = receipt };
            reverseLease = receipt;
            // Only this optional receipt is added to the initial proof; the
            // captured display and process identities never change.
            WriteAtomic(BootstrapPath(path, lease.LeaseId), state);
            WriteAtomic(path, state);
            return 0;
        });
    }

    public void Dispose()
    {
        // A failed lock, storage write, or native detach must never let this
        // stopped session renew itself or register another USB mapping.
        stopRequested = true;
        WithLeaseLock(() =>
        {
            if (cleanupComplete) return 0;
            // An old UI continuation, just like an old watcher, must not detach
            // a screen after another session has acquired it. A missing or
            // unreadable marker throws and retains pending cleanup authority;
            // it is never treated as evidence that the output was released.
            if (ReadMarker(path) != lease.LeaseId)
            {
                cleanupComplete = true;
                return 0;
            }
            // Publish explicit stop before native cleanup, which can itself
            // throw. The watcher can then retry without waiting for expiry.
            try { WriteAtomic(path, new WatchState(lease, Environment.ProcessId, ownerStartTicks, DateTime.UtcNow, reverseLease, StopRequested: true)); }
            catch (Exception ex) when (IsStorageFailure(ex)) { }
            RememberBeforeDetach(lease);
            var result = VirtualDisplayManager.Detach(lease);
            SaveDiagnostic("display-stop.json", new { timestamp = DateTimeOffset.Now, result });
            if (!result.Success) throw new IOException(result.Message);
            WriteAtomic(MarkerPath(path), Guid.Empty);
            cleanupComplete = true;
            return 0;
        });
    }

    internal static SessionGuard? RestorePrevious()
    {
        return WithLeaseLock(() =>
        {
            var saved = Directory.Exists(LeaseFolder) ? Directory.GetFiles(LeaseFolder, "*.last.json") : [];
            if (saved.Length > 1) throw new IOException("有多个副屏恢复记录，请为设备选择明确的显示目标。");
            var previousPath = saved.Length == 1 ? saved[0] : LastDisplayPath;
            if (!File.Exists(previousPath)) return null;
            var previous = JsonSerializer.Deserialize<DisplayLease>(File.ReadAllText(previousPath))
                ?? throw new IOException("副屏恢复记录无效");
            var lease = previous with { LeaseId = Guid.NewGuid() };
            // Publish fresh ownership and start crash protection BEFORE enabling
            // the saved output. The shared lock excludes an expired old watcher.
            var guard = new SessionGuard(lease);
            try
            {
                var result = VirtualDisplayManager.Restore(lease);
                if (!result.Success) throw new IOException(result.Message);
                return guard;
            }
            catch (Exception restoreError)
            {
                try { guard.Dispose(); }
                catch (Exception rollbackError)
                {
                    throw new IOException("副屏恢复失败，自动收回也未完成：" + rollbackError.Message, restoreError);
                }
                throw;
            }
        });
    }

    internal static Task<int> WatchAsync(string file, string expectedLeaseId, Func<Task>? afterOwnerExitDetach = null)
    {
        if (!Guid.TryParse(expectedLeaseId, out var id) || id == Guid.Empty || !IsAllowedWatchPath(file))
            return Task.FromResult(2);
        var environment = afterOwnerExitDetach is null ? WatchEnvironment.Local : WatchEnvironment.Local with
        { AfterOwnerExitDetach = afterOwnerExitDetach };
        return WatchOwnedAsync(file, id, environment);
    }

    internal static bool IsAllowedWatchPath(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            if (full.Equals(Path.Combine(Folder, "active-display-lease.json"), StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(Path.GetDirectoryName(full), LeaseFolder, StringComparison.OrdinalIgnoreCase)) return false;
            var name = Path.GetFileName(full);
            return name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal) &&
                name.AsSpan(0, 64).ToArray().All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // Separate from the command-line path check so tests can use temporary files
    // and a fake display adapter without touching the user's active lease.
    internal static async Task<int> WatchOwnedAsync(string file, Guid expectedLeaseId, WatchEnvironment? environment = null)
    {
        environment ??= WatchEnvironment.Local;
        WatchState? initial = null;
        WatchState? last = null;
        var desktopWasUnavailable = false;
        var recoveryDeadline = DateTime.MinValue;
        var failedDetachAttempts = 0;
        var reverseCleanupAttempted = false;
        while (true)
        {
            try
            {
                var detachedAfterOwnerExit = false;
                var outcome = WithLeaseLock(() =>
                {
                    // This small independent marker remains readable if the
                    // renewable JSON is corrupt. Never fall back across owners.
                    if (ReadMarker(file) != expectedLeaseId) return 0;
                    var now = environment.UtcNow();
                    initial ??= ReadState(BootstrapPath(file, expectedLeaseId), expectedLeaseId, now);
                    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), LeaseFolder, StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFullPath(file).Equals(LeasePath(initial.Lease), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("副屏保护文件不属于记录中的显示目标。");
                    var unreadable = false;
                    try
                    {
                        var current = ReadState(file, expectedLeaseId, now);
                        if (current.OwnerPid != initial.OwnerPid || current.OwnerStartUtcTicks != initial.OwnerStartUtcTicks ||
                            JsonSerializer.Serialize(current.Lease) != JsonSerializer.Serialize(initial.Lease))
                            throw new InvalidDataException("副屏保护记录中的设备或进程身份发生变化");
                        last = current;
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        unreadable = true;
                        last ??= initial;
                    }
                    var owner = environment.OwnerStatus(last);
                    var running = owner != OwnerLiveness.Exited;
                    var desktop = environment.InputDesktop();
                    var deadline = unreadable ? last.DeadlineUtc.AddSeconds(5) : last.DeadlineUtc;
                    // Only independent local evidence and a verified PID/start-time pair can
                    // suspend missing-frame recovery. A remote "paused" flag has no authority.
                    if (owner == OwnerLiveness.VerifiedRunning && !unreadable && !last.StopRequested)
                    {
                        if (desktop.IsUnavailable)
                        {
                            if (!desktopWasUnavailable)
                                SaveDiagnostic("display-watchdog-desktop.json", new { timestamp = now, state = "paused", desktop });
                            desktopWasUnavailable = true;
                            return -1;
                        }
                        if (desktop.IsAvailable && desktopWasUnavailable)
                        {
                            desktopWasUnavailable = false;
                            recoveryDeadline = now.AddSeconds(20);
                            SaveDiagnostic("display-watchdog-desktop.json", new { timestamp = now, state = "resuming", recoveryDeadline, desktop });
                        }
                        // Unknown probe errors do not create or extend any grace period.
                        if (desktop.IsAvailable && now <= recoveryDeadline) return -1;
                    }
                    if (running && !last.StopRequested && now <= deadline) return -1;
                    // Read, deadline evaluation and detach all hold the same
                    // cross-process lock used by constructor/Renew/Dispose.
                    RememberBeforeDetach(last.Lease);
                    var result = VirtualDisplayManager.Detach(last.Lease);
                    SaveDiagnostic("display-watchdog.json", new
                    {
                        timestamp = DateTimeOffset.Now,
                        reason = !running ? "host-exited" : last.StopRequested ? "stop-requested" : unreadable ? "lease-unreadable" : "no-new-frames",
                        result
                    });
                    if (!running && !reverseCleanupAttempted)
                    {
                        var receipt = FindReverseReceipt(file, last);
                        if (receipt is not null)
                        {
                            CleanupReverse(receipt, "watcher-host-exited");
                            reverseCleanupAttempted = true;
                        }
                    }
                    if (result.Success)
                    {
                        // Retire ownership too: a delayed UI renewal must stop
                        // after the watchdog has already collected its output.
                        WriteAtomic(MarkerPath(file), Guid.Empty);
                        detachedAfterOwnerExit = !running;
                        return 0;
                    }
                    // Still attempt crash/explicit-stop cleanup while another desktop is active.
                    // If Windows rejects that mutation, retry after it returns instead of abandoning
                    // the exact owned display after three inaccessible-desktop attempts.
                    if (desktop.IsUnavailable) { failedDetachAttempts = 0; return -1; }
                    return ++failedDetachAttempts >= 3 ? 1 : -1;
                });
                // Keep the compact proof for diagnosis and retry if a device
                // was unavailable when its owned reverse mapping was retired.
                if (outcome >= 0)
                {
                    if (outcome == 0 && detachedAfterOwnerExit && environment.AfterOwnerExitDetach is not null)
                    {
                        try { await environment.AfterOwnerExitDetach().ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            // The owned output is already detached and its
                            // marker retired. Keep this failure visible without
                            // expanding authority to another driver instance.
                            SaveDiagnostic("display-watchdog-driver-remove-error.json", new
                            { timestamp = DateTimeOffset.Now, error = ex.Message });
                            return 1;
                        }
                    }
                    return outcome;
                }
            }
            catch (Exception ex) when (IsStorageFailure(ex) || ex is TimeoutException)
            {
                // Without a readable ownership marker it is unsafe to apply an
                // old lease: a newer session may already own the same output.
                // Retry transient failures; never detach using an unverified ID.
                SaveDiagnostic("display-watchdog-error.json", new { timestamp = DateTimeOffset.Now, error = ex.Message });
            }
            catch (Exception ex)
            {
                SaveDiagnostic("display-watchdog-error.json", new { timestamp = DateTimeOffset.Now, error = ex.Message });
                return 1;
            }
            await environment.Delay();
        }
    }

    static WatchState ReadState(string file, Guid expectedId, DateTime? nowUtc = null)
    {
        var state = JsonSerializer.Deserialize<WatchState>(File.ReadAllText(file));
        if (state?.Lease is null || state.Lease.LeaseId != expectedId || state.OwnerPid <= 0 ||
            state.OwnerStartUtcTicks <= 0 || state.DeadlineUtc.Kind != DateTimeKind.Utc ||
            state.DeadlineUtc > (nowUtc ?? DateTime.UtcNow).AddMinutes(1))
            throw new InvalidDataException("副屏保护记录无效或会话不匹配");
        return state;
    }

    static Guid ReadMarker(string file)
    {
        // The all-zero ID is an explicit retired marker, never an active lease.
        return JsonSerializer.Deserialize<Guid>(File.ReadAllText(MarkerPath(file)));
    }

    static bool OwnerIsRunning(WatchState state) => ReadOwnerStatus(state) != OwnerLiveness.Exited;

    static OwnerLiveness ReadOwnerStatus(WatchState state)
    {
        try
        {
            using var owner = Process.GetProcessById(state.OwnerPid);
            return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == state.OwnerStartUtcTicks
                ? OwnerLiveness.VerifiedRunning : OwnerLiveness.Exited;
        }
        catch (ArgumentException) { return OwnerLiveness.Exited; }
        catch (InvalidOperationException) { return OwnerLiveness.Exited; }
        // An access failure is not proof that the owner exited. Its deadline
        // still bounds the lease if the host cannot produce rendering evidence.
        catch (System.ComponentModel.Win32Exception) { return OwnerLiveness.Unverified; }
    }

    static void CleanupPreviousOwner(string file)
    {
        try
        {
            if (!File.Exists(MarkerPath(file))) return;
            var id = ReadMarker(file);
            if (id == Guid.Empty)
            {
                // A retired display can still have an unavailable device's
                // pending cleanup receipt. Its saved state identifies that ID.
                id = JsonSerializer.Deserialize<WatchState>(File.ReadAllText(file))?.Lease?.LeaseId ?? Guid.Empty;
                if (id == Guid.Empty) return;
            }
            WatchState previous;
            try { previous = ReadState(file, id); }
            catch (Exception ex) when (IsStorageFailure(ex)) { previous = ReadState(BootstrapPath(file, id), id); }
            if (OwnerIsRunning(previous)) return;
            var receipt = FindReverseReceipt(file, previous);
            if (receipt is not null) CleanupReverse(receipt, "before-new-owner");
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // No valid receipt means no authority to remove any mapping. The
            // normal no-rebind connect operation will also reject an occupied port.
            SaveDiagnostic("usb-reverse-recovery-error.json", new { timestamp = DateTimeOffset.Now, error = ex.Message });
        }
    }

    static void RetireLegacyOwnership(DisplayLease next)
    {
        var oldPath = Path.Combine(Folder, "active-display-lease.json");
        if (!File.Exists(oldPath) || !File.Exists(MarkerPath(oldPath))) return;
        var id = ReadMarker(oldPath);
        if (id == Guid.Empty) return;
        var previous = ReadState(oldPath, id);
        if (VirtualDisplayManager.GetTargetStorageKey(previous.Lease) != VirtualDisplayManager.GetTargetStorageKey(next)) return;
        if (OwnerIsRunning(previous)) throw new IOException("此虚拟屏仍由旧版连接占用，请先停止该连接。");
        CleanupPreviousOwner(oldPath);
        WriteAtomic(MarkerPath(oldPath), Guid.Empty);
    }

    static UsbReverseLease? FindReverseReceipt(string file, WatchState state)
    {
        UsbReverseLease? receipt = null;
        try
        {
            var backup = ReadState(BootstrapPath(file, state.Lease.LeaseId), state.Lease.LeaseId);
            if (backup.OwnerPid == state.OwnerPid && backup.OwnerStartUtcTicks == state.OwnerStartUtcTicks &&
                JsonSerializer.Serialize(backup.Lease) == JsonSerializer.Serialize(state.Lease))
                receipt = backup.ReverseLease;
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { }
        return receipt is not null && receipt.OwnerPid == state.OwnerPid && receipt.OwnerStartUtcTicks == state.OwnerStartUtcTicks
            ? receipt : null;
    }

    static void CleanupReverse(UsbReverseLease receipt, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            // Keep this thread's Mutex while asynchronous ADB work runs on the
            // pool; never block the UI synchronization context's continuations.
            var result = Task.Run(() => UsbReverseLease.CleanupAfterOwnerExitAsync(receipt, timeout.Token)).GetAwaiter().GetResult();
            SaveDiagnostic("usb-reverse-recovery.json", new { timestamp = DateTimeOffset.Now, reason, receipt.Id, result });
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is OperationCanceledException or TimeoutException)
        {
            SaveDiagnostic("usb-reverse-recovery-error.json", new { timestamp = DateTimeOffset.Now, reason, receipt.Id, error = ex.Message });
        }
    }

    static T WithLeaseLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, LeaseMutex);
        var locked = false;
        try
        {
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new TimeoutException("另一个副屏会话操作尚未完成");
            return action();
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    static bool IsStorageFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;

    static void RememberBeforeDetach(DisplayLease lease)
    {
        // Called only while the lease-ID ownership lock is held. Failure to read an unavailable
        // desktop must not block crash/stop cleanup or broaden the original display authority.
        try
        {
            var current = VirtualDisplayManager.ResolveLayout(lease).RememberedLease;
            DisplayLease? saved = null;
            var rememberedPath = RememberedPath(lease);
            try { if (File.Exists(rememberedPath)) saved = JsonSerializer.Deserialize<DisplayLease>(File.ReadAllText(rememberedPath)); }
            catch (Exception ex) when (IsStorageFailure(ex)) { }
            if (saved is null || !VirtualDisplayManager.RememberedLayoutEquals(saved, current)) WriteAtomic(rememberedPath, current);
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is InvalidOperationException or ArgumentException)
        { SaveDiagnostic("display-position-recovery.json", new { timestamp = DateTimeOffset.Now, error = ex.Message }); }
    }
    static string MarkerPath(string file) => file + ".lease-id";
    static string BootstrapPath(string file, Guid id) => file + "." + id.ToString("N") + ".initial.json";

    static void SaveDiagnostic(string name, object value)
    {
        try { Diagnostics.Save(name, value); }
        catch (Exception ex) when (IsStorageFailure(ex)) { }
    }

    static void WriteAtomic<T>(string file, T value)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (IsStorageFailure(ex)) { }
        }
    }

    internal enum OwnerLiveness { Exited, VerifiedRunning, Unverified }

    internal sealed record WatchEnvironment(Func<DateTime> UtcNow, Func<WatchState, OwnerLiveness> OwnerStatus,
        Func<InputDesktopStatus> InputDesktop, Func<Task> Delay, Func<Task>? AfterOwnerExitDetach = null)
    {
        internal static WatchEnvironment Local { get; } = new(() => DateTime.UtcNow, ReadOwnerStatus,
            InputDesktopAvailability.Query, () => Task.Delay(2000));
    }

    internal sealed record WatchState(DisplayLease Lease, int OwnerPid, long OwnerStartUtcTicks, DateTime DeadlineUtc,
        UsbReverseLease? ReverseLease = null, bool StopRequested = false);
}
