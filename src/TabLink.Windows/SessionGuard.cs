using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Windows;

// The watcher runs outside the UI process so a host crash still retires the
// selected virtual output. A shared lock covers ownership checks and mutation.
internal sealed class SessionGuard : IDisposable
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
    internal static string LastDisplayPath => Path.Combine(Folder, "last-display.json");
    internal static string RememberedFolder => Path.Combine(Folder, "display-layouts");
    internal static string LeaseFolder => DisplayLeaseProtectedStorage.LeaseFolder;
    internal static string PendingReverseFolder => UsbReverseProtectedStorage.QueueFolder;
    internal static string LeasePath(DisplayLease lease) => Path.Combine(LeaseFolder, VirtualDisplayManager.GetTargetStorageKey(lease) + ".json");
    internal static string RememberedPath(DisplayLease lease) => Path.Combine(RememberedFolder, VirtualDisplayManager.GetTargetStorageKey(lease) + ".last.json");
    internal static string LegacyRememberedPath(DisplayLease lease) => Path.Combine(Folder, "display-leases",
        VirtualDisplayManager.GetTargetStorageKey(lease) + ".last.json");
    readonly string path;
    readonly string pendingReverseFolder;
    readonly DisplayLease lease;
    DisplayLease? rememberedLease;
    readonly long ownerStartTicks;
    UsbReverseLease? reverseLease;
    volatile bool stopRequested;
    bool cleanupComplete;

    internal DisplayLease Lease => lease;

    // Reconstitute an already-published lease without launching a watcher. The
    // isolated lifecycle harness supplies temporary state and a fake adapter.
    internal SessionGuard(DisplayLease lease, string ownershipPath, long ownerStartTicks,
        string? pendingReverseFolder = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipPath);
        if (lease.LeaseId == Guid.Empty || ownerStartTicks <= 0)
            throw new IOException("副屏会话标识无效");
        this.lease = lease;
        rememberedLease = lease;
        path = ownershipPath;
        this.pendingReverseFolder = pendingReverseFolder ?? PendingReverseFolder;
        this.ownerStartTicks = ownerStartTicks;
    }

    internal SessionGuard(DisplayLease lease, bool requireUnowned = false)
        : this(lease, requireUnowned, GuardStartupEnvironment.Local)
    {
    }

    // The injected launcher keeps the isolated lifecycle tests from starting a
    // child copy of themselves. Production always uses the sealed local value.
    internal SessionGuard(DisplayLease lease, bool requireUnowned, GuardStartupEnvironment startup)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(startup);
        if (lease.LeaseId == Guid.Empty) throw new IOException("副屏会话标识无效");
        this.lease = lease;
        rememberedLease = lease;
        pendingReverseFolder = PendingReverseFolder;
        DisplayLeaseProtectedStorage.EnsureLeaseFolder();
        using var owner = Process.GetCurrentProcess();
        ownerStartTicks = owner.StartTime.ToUniversalTime().Ticks;
        path = LeasePath(lease);
        var state = new WatchState(lease, Environment.ProcessId, ownerStartTicks, DateTime.UtcNow.AddSeconds(25));
        var markerPublished = false;
        try
        {
            WithLeaseLock(() =>
            {
                if (requireUnowned && File.Exists(MarkerPath(path)))
                {
                    var ownerId = ReadMarker(path);
                    if (ownerId != Guid.Empty)
                    {
                        // Marker selects the authoritative generation. A process
                        // may have crashed after overwriting current but before
                        // switching marker; fall back to marker's immutable
                        // bootstrap instead of rejecting before stale cleanup.
                        var existing = ReadAuthoritativeState(path, ownerId);
                        if (!existing.StopRequested && OwnerIsRunning(existing))
                            throw new IOException("此虚拟显示目标仍由另一个连接占用。");
                        if (File.Exists(path))
                        {
                            var current = ReadState(path);
                            if (current.Lease.LeaseId != ownerId && OwnerIsRunning(current))
                                throw new IOException("此虚拟显示目标存在仍由活动进程持有的未提交 generation。");
                        }
                    }
                }
                CleanupPreviousOwner(path);
                startup.RetireLegacyOwnership(lease);
                // The immutable bootstrap lets a watcher recover even if its
                // first read of the renewable JSON encounters corruption.
                WriteAtomic(BootstrapPath(path, lease.LeaseId), state);
                startup.PersistRememberedLayout(lease);
                WriteAtomic(path, state);
                WriteAtomic(MarkerPath(path), lease.LeaseId);
                markerPublished = true;
                return 0;
            });

            // Do not hold the machine-wide display file lock while waiting for
            // readiness: the child must acquire that same lock to validate the
            // protected files and enter its first loop iteration.
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位 TabLink 程序"))
            { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--watch-display");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(lease.LeaseId.ToString("D"));
            startup.StartWatcher(start, Environment.ProcessId, ownerStartTicks);
        }
        catch (Exception startError)
        {
            stopRequested = true;
            if (!markerPublished &&
                startError is DisplayLeaseProtectedStorage.ManagedWriteCommittedException committed &&
                Path.GetFullPath(committed.CommittedPath).Equals(
                    Path.GetFullPath(MarkerPath(path)), StringComparison.OrdinalIgnoreCase))
            {
                // The atomic replace completed before ACL post-verification
                // failed. Preserve exact cleanup authority even if a second
                // metadata read also fails; never forget a visible marker.
                markerPublished = true;
            }

            var ownsMarker = markerPublished;
            DisplayChangeResult? rollback = null;
            Exception? rollbackError = null;
            try
            {
                WithLeaseLock(() =>
                {
                    if (markerPublished)
                    {
                        try { ownsMarker = ReadMarker(path) == lease.LeaseId; }
                        catch (Exception ex) when (IsStorageFailure(ex))
                        {
                            // The exact marker was committed while this process
                            // held the protected machine-wide lock. A transient
                            // read failure cannot erase that authority proof.
                            ownsMarker = true;
                        }
                    }
                    if (ownsMarker)
                    {
                        try { WriteAtomic(path, state with { DeadlineUtc = DateTime.UtcNow, StopRequested = true }); }
                        catch (Exception ex) when (IsStorageFailure(ex)) { }
                    }
                    // If our marker was superseded, another exact owner now has
                    // authority and this failed constructor must not detach it.
                    if (ownsMarker || !markerPublished)
                    {
                        try { rollback = VirtualDisplayManager.Detach(lease); }
                        catch (Exception ex) { rollbackError = ex; }
                    }
                    if (rollback?.Success == true && ownsMarker)
                    {
                        if (ReadMarker(path) != lease.LeaseId)
                            throw new IOException("副屏保护启动失败后所有权标记已经变化，未覆盖新的会话。");
                        WriteAtomic(MarkerPath(path), Guid.Empty);
                        cleanupComplete = true;
                        ownsMarker = false;
                    }
                    return 0;
                });
            }
            catch (Exception ex) { rollbackError = ex; }

            SaveDiagnostic("display-guard-start-error.json", new
            { timestamp = DateTimeOffset.Now, error = startError.Message, rollback, rollbackError = rollbackError?.Message });
            if (ownsMarker)
            {
                // The constructor cannot return this object, so expose a
                // reconstituted exact guard to the allocator. Its existing
                // PendingCleanup path can retry Dispose without broadening
                // authority or losing the committed marker.
                var recovery = new SessionGuard(lease, path, ownerStartTicks, pendingReverseFolder)
                { stopRequested = true };
                throw new GuardStartupFailureException(
                    "无法启动副屏保护，显示回滚仍需重试。" + (rollback?.Message ?? rollbackError?.Message),
                    startError, recovery);
            }
            throw new IOException("无法启动副屏保护，已尝试收回副屏。" +
                (rollback?.Message ?? rollbackError?.Message), startError);
        }
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
        DisplayLeaseProtectedStorage.EnsureLeaseFolder();
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
            DisplayLeaseProtectedStorage.EnsureLeaseFolder();
            var rememberedPath = RememberedPath(fresh);
            if (!File.Exists(rememberedPath)) rememberedPath = LegacyRememberedPath(fresh); // Read-only 0.8.6 migration.
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
        => ReplaceReverse(null, receipt);

    internal void RetainReverseForCleanup(UsbReverseLease receipt)
    {
        ValidateReverseReceipt(receipt);
        WithLeaseLock(() =>
        {
            if (stopRequested || ReadMarker(path) != lease.LeaseId)
                throw new IOException("副屏会话已结束，无法保存 USB 待清理记录");
            var state = ReadState(path, lease.LeaseId);
            if (state.OwnerPid != Environment.ProcessId || state.OwnerStartUtcTicks != ownerStartTicks ||
                JsonSerializer.Serialize(state.Lease) != JsonSerializer.Serialize(lease) ||
                reverseLease != receipt || state.ReverseLease != receipt)
                throw new IOException("USB 待清理记录不属于当前副屏会话");
            PendingQueue().RequirePending(receipt, UsbReverseQueueState.Owned);
            return 0;
        });
    }

    PendingUsbReverseCleanupQueue PendingQueue() =>
        pendingReverseFolder.Equals(PendingReverseFolder, StringComparison.OrdinalIgnoreCase)
            ? UsbReverseProtectedStorage.OpenQueue()
            : new PendingUsbReverseCleanupQueue(pendingReverseFolder);

    /// <summary>
    /// Atomically retires or replaces the exact reverse receipt published for
    /// this display lease. Recovery clears the old receipt before recreating a
    /// missing mapping, then publishes a fresh receipt only after --no-rebind
    /// succeeds. A crash in between may leave an inert mapping, but can never
    /// authorize the watcher to delete a mapping whose ownership is uncertain.
    /// </summary>
    internal void ReplaceReverse(UsbReverseLease? expected, UsbReverseLease? next)
    {
        ValidateReverseReceipt(expected);
        ValidateReverseReceipt(next);
        WithLeaseLock(() =>
        {
            if (stopRequested || ReadMarker(path) != lease.LeaseId)
                throw new IOException("副屏会话已结束，无法登记 USB 通道");
            var state = ReadState(path, lease.LeaseId);
            if (state.OwnerPid != Environment.ProcessId || state.OwnerStartUtcTicks != ownerStartTicks ||
                JsonSerializer.Serialize(state.Lease) != JsonSerializer.Serialize(lease))
                throw new IOException("副屏会话身份记录已经变化，拒绝更新 USB 通道记录");
            if (reverseLease != expected || state.ReverseLease != expected)
                throw new IOException("USB 通道所有权记录已经变化，拒绝替换旧记录");
            // Rebuild from the in-memory immutable identities. Never promote a
            // merely well-formed renewable file into crash-cleanup authority.
            var updated = new WatchState(lease, Environment.ProcessId, ownerStartTicks,
                state.DeadlineUtc, next, state.StopRequested);
            // Bootstrap is the crash-cleanup authority. Clear it before an old
            // receipt can be mistaken for a future mapping; publish a new one
            // there only after this process has demonstrably created it.
            WriteAtomic(BootstrapPath(path, lease.LeaseId), updated);
            WriteAtomic(path, updated);
            reverseLease = next;
            return 0;
        });
    }

    void ValidateReverseReceipt(UsbReverseLease? receipt)
    {
        if (receipt is not null && (receipt.SchemaVersion != UsbReverseLease.CurrentSchemaVersion ||
            receipt.Id == Guid.Empty || !receipt.Endpoint.IsValid
            || receipt.OwnerPid != Environment.ProcessId || receipt.OwnerStartUtcTicks != ownerStartTicks ||
            !string.Equals(receipt.OwnerUserSid, UsbReverseLease.CurrentUserSid(), StringComparison.Ordinal)))
            throw new IOException("USB 通道记录不属于当前连接进程");
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
        DisplayLeaseProtectedStorage.EnsureLeaseFolder();
        return WithLeaseLock(() =>
        {
            var saved = Directory.Exists(RememberedFolder) ? Directory.GetFiles(RememberedFolder, "*.last.json") : [];
            if (saved.Length == 0)
            {
                var legacyFolder = Path.Combine(Folder, "display-leases");
                saved = Directory.Exists(legacyFolder) ? Directory.GetFiles(legacyFolder, "*.last.json") : [];
            }
            if (saved.Length > 1) throw new IOException("有多个副屏恢复记录，请为设备选择明确的显示目标。");
            var previousPath = saved.Length == 1 ? saved[0] : LastDisplayPath;
            if (!File.Exists(previousPath)) return null;
            var previous = JsonSerializer.Deserialize<DisplayLease>(File.ReadAllText(previousPath))
                ?? throw new IOException("副屏恢复记录无效");
            var lease = previous with { LeaseId = Guid.NewGuid() };
            // Publish fresh ownership and start crash protection BEFORE enabling
            // the saved output. The shared lock excludes an expired old watcher.
            SessionGuard guard;
            try { guard = new SessionGuard(lease); }
            catch (GuardStartupFailureException startupFailure)
            {
                // RestorePrevious has no allocator reservation that can retain a
                // constructor recovery guard. Attempt the same exact cleanup
                // here while the re-entrant machine-wide lease lock is held.
                // If cleanup still fails, preserve the retryable guard on the
                // exception rather than losing the published ownership marker.
                try { startupFailure.RecoveryGuard.Dispose(); }
                catch (Exception rollbackError)
                {
                    throw new GuardStartupFailureException(
                        "无法启动副屏恢复保护，自动收回也未完成：" + rollbackError.Message,
                        new AggregateException(startupFailure, rollbackError),
                        startupFailure.RecoveryGuard);
                }
                throw;
            }
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

    internal static Task<int> WatchAsync(string file, string expectedLeaseId,
        Func<Task>? afterOwnerExitDetach = null,
        DisplayWatcherHandshake.ChildContext? handshake = null)
    {
        try
        {
            DisplayLeaseProtectedStorage.VerifyLeaseFolder();
            if (!Guid.TryParse(expectedLeaseId, out var id) || id == Guid.Empty || !IsAllowedWatchPath(file))
                return Task.FromResult(2);
            DisplayLeaseProtectedStorage.VerifyWatcherFiles(file, id);
            var environment = afterOwnerExitDetach is null ? WatchEnvironment.Local : WatchEnvironment.Local with
            { AfterOwnerExitDetach = afterOwnerExitDetach };
            if (handshake is not null)
            {
                environment = environment with
                {
                    OwnerStatus = state => handshake.ObserveOwner(state.OwnerPid, state.OwnerStartUtcTicks) switch
                    {
                        DisplayWatcherHandshake.OwnerObservation.VerifiedRunning => OwnerLiveness.VerifiedRunning,
                        DisplayWatcherHandshake.OwnerObservation.Exited => OwnerLiveness.Exited,
                        _ => OwnerLiveness.Unverified
                    },
                    OnVerifiedLoopEntry = state =>
                    {
                        handshake.ConfirmProtectedStateValidated(file, id,
                            state.OwnerPid, state.OwnerStartUtcTicks);
                        handshake.SignalReadyAfterLoopEntry();
                    }
                };
            }
            return WatchOwnedAsync(file, id, environment);
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is ArgumentException or InvalidOperationException
                                   or System.Security.SecurityException)
        {
            SaveDiagnostic("display-watchdog-storage-error.json", new { timestamp = DateTimeOffset.Now, error = ex.Message });
            return Task.FromResult(2);
        }
    }

    internal static bool IsAllowedWatchPath(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
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
        var displayDetached = false;
        var reverseQueued = false;
        var loopEntryConfirmed = false;
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
                    var currentVerified = false;
                    try
                    {
                        var current = ReadState(file, expectedLeaseId, now);
                        if (current.OwnerPid != initial.OwnerPid || current.OwnerStartUtcTicks != initial.OwnerStartUtcTicks ||
                            JsonSerializer.Serialize(current.Lease) != JsonSerializer.Serialize(initial.Lease))
                            throw new InvalidDataException("副屏保护记录中的设备或进程身份发生变化");
                        last = current;
                        currentVerified = true;
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        unreadable = true;
                        last ??= initial;
                    }
                    var owner = environment.OwnerStatus(last);
                    if (!loopEntryConfirmed && environment.OnVerifiedLoopEntry is not null)
                    {
                        if (!currentVerified)
                            throw new InvalidDataException("副屏保护 current 尚未通过首次完整验证。");
                        if (owner != OwnerLiveness.VerifiedRunning)
                            throw new IOException("副屏所有者未能在保护进程就绪前通过精确身份验证。");
                        environment.OnVerifiedLoopEntry(last);
                        loopEntryConfirmed = true;
                    }
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
                    if (!displayDetached)
                    {
                        RememberBeforeDetach(last.Lease);
                        var result = VirtualDisplayManager.Detach(last.Lease);
                        SaveDiagnostic("display-watchdog.json", new
                        {
                            timestamp = DateTimeOffset.Now,
                            reason = !running ? "host-exited" : last.StopRequested ? "stop-requested" : unreadable ? "lease-unreadable" : "no-new-frames",
                            result
                        });
                        displayDetached = result.Success;
                    }
                    if (!running)
                    {
                        if (!reverseQueued)
                        {
                            // The machine-protected queue is the only cleanup
                            // authority. LocalAppData may be unreadable or from
                            // an older release; neither can delay exact display
                            // and driver reclamation. Process the protected
                            // queue only after the display ownership lock exits.
                            reverseQueued = true;
                        }
                    }
                    if (displayDetached)
                    {
                        // Retire ownership too: a delayed UI renewal must stop
                        // after the watchdog has already collected its output.
                        WriteAtomic(MarkerPath(file), Guid.Empty);
                        detachedAfterOwnerExit = !running;
                        return 0;
                    }
                    // Keep retrying the same exact lease. A fourth or later
                    // native attempt can succeed after a transient desktop or
                    // adapter failure; a retry never broadens display authority.
                    return -1;
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
                    if (outcome == 0 && detachedAfterOwnerExit && reverseQueued)
                    {
                        try { environment.ProcessPendingReverse?.Invoke(); }
                        catch (Exception ex)
                        {
                            // The display and driver are already reclaimed. Keep
                            // the durable receipt for a later bounded retry.
                            SaveDiagnostic("usb-reverse-pending-error.json", new
                            { timestamp = DateTimeOffset.Now, reason = "watcher-post-display", error = ex.Message });
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
        var state = ReadState(file, nowUtc);
        if (state.Lease.LeaseId != expectedId)
            throw new InvalidDataException("副屏保护记录无效或会话不匹配");
        return state;
    }

    static WatchState ReadState(string file, DateTime? nowUtc = null)
    {
        var state = JsonSerializer.Deserialize<WatchState>(File.ReadAllText(file));
        if (state?.Lease is null || state.Lease.LeaseId == Guid.Empty || state.OwnerPid <= 0 ||
            state.OwnerStartUtcTicks <= 0 || state.DeadlineUtc.Kind != DateTimeKind.Utc ||
            state.DeadlineUtc > (nowUtc ?? DateTime.UtcNow).AddMinutes(1))
            throw new InvalidDataException("副屏保护记录无效或会话不匹配");
        return state;
    }

    static WatchState ReadAuthoritativeState(string file, Guid expectedId, DateTime? nowUtc = null)
    {
        try { return ReadState(file, expectedId, nowUtc); }
        catch (Exception ex) when (IsStorageFailure(ex))
        { return ReadState(BootstrapPath(file, expectedId), expectedId, nowUtc); }
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

    internal static void CleanupPreviousOwner(string file)
    {
        Guid? legacyReceiptId = null;
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
            var previous = ReadAuthoritativeState(file, id);
            if (OwnerIsRunning(previous)) return;
            var legacyReceipt = previous.ReverseLease;
            if (legacyReceipt is not null && legacyReceipt.OwnerPid == previous.OwnerPid &&
                legacyReceipt.OwnerStartUtcTicks == previous.OwnerStartUtcTicks)
                legacyReceiptId = legacyReceipt.Id;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // No valid receipt means no authority to remove any mapping. The
            // normal no-rebind connect operation will also reject an occupied port.
            SaveDiagnostic("usb-reverse-recovery-error.json", new { timestamp = DateTimeOffset.Now, error = ex.Message });
            return;
        }

        if (legacyReceiptId is { } receiptId)
            // 0.8.6 and earlier wrote this file below user-writable LocalAppData.
            // It can explain an orphan but can never be promoted into elevated
            // ADB removal authority. ADB restart/device reconnect retires it.
            SaveDiagnostic("usb-reverse-untrusted-legacy.json", new
            { timestamp = DateTimeOffset.Now, reason = "legacy-localappdata-receipt", Id = receiptId });
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
        // Legacy LocalAppData is user-writable and remains read-only. It can
        // conservatively veto a new allocation while its recorded owner is
        // alive, but it never authorizes cleanup and we never rewrite it.
        CleanupPreviousOwner(oldPath);
    }

    static T WithLeaseLock<T>(Func<T> action)
    {
        using var leaseLock = DisplayLeaseProtectedStorage.AcquireLock();
        return action();
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        if (DisplayLeaseProtectedStorage.IsManagedFile(file))
        {
            DisplayLeaseProtectedStorage.WriteManagedFileAtomically(file, bytes);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))
            ?? throw new InvalidDataException("显示布局目录无效。"));
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
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
        Func<InputDesktopStatus> InputDesktop, Func<Task> Delay, Func<Task>? AfterOwnerExitDetach = null,
        Action<UsbReverseLease>? RetainPendingReverse = null, Action? ProcessPendingReverse = null,
        Action<WatchState>? OnVerifiedLoopEntry = null)
    {
        internal static WatchEnvironment Local { get; } = new(() => DateTime.UtcNow, ReadOwnerStatus,
            InputDesktopAvailability.Query, () => Task.Delay(2000),
            ProcessPendingReverse: () => UsbReverseCleanupCoordinator.Shared.ProcessPendingBlocking("watcher-owner-exited"));
    }

    internal sealed record GuardStartupEnvironment(
        Action<ProcessStartInfo, int, long> StartWatcher,
        Action<DisplayLease> RetireLegacyOwnership,
        Action<DisplayLease> PersistRememberedLayout)
    {
        internal static GuardStartupEnvironment Local { get; } = new(
            (start, ownerPid, ownerStartTicks) =>
                DisplayWatcherHandshake.StartAndWait(start, ownerPid, ownerStartTicks),
            SessionGuard.RetireLegacyOwnership,
            lease => WriteAtomic(RememberedPath(lease), lease));
    }

    internal sealed class GuardStartupFailureException(string message, Exception innerException,
        SessionGuard recoveryGuard) : IOException(message, innerException)
    {
        internal SessionGuard RecoveryGuard { get; } = recoveryGuard;
    }

    internal sealed record WatchState(DisplayLease Lease, int OwnerPid, long OwnerStartUtcTicks, DateTime DeadlineUtc,
        [property: JsonPropertyName("ReverseLeaseV2")] UsbReverseLease? ReverseLease = null,
        bool StopRequested = false);
}
