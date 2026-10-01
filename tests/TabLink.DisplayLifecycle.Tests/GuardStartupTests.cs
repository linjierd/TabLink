using System.Text.Json;
using TabLink.Windows;

static class GuardStartupTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-artifacts",
            "GuardStartup-" + Guid.NewGuid().ToString("N"));
        var previousFolder = DisplayLeaseProtectedStorage.LeaseFolder;
        var detachIds = new List<Guid>();
        FileStream? markerLock = null;
        Directory.CreateDirectory(root);
        DisplayLeaseProtectedStorage.LeaseFolder = Path.Combine(root, "protected-display-leases");
        VirtualDisplayManager.OnDetach = lease =>
        {
            detachIds.Add(lease.LeaseId);
            return new(true, true, "fake startup rollback succeeded");
        };
        try
        {
            var first = new DisplayLease(Guid.NewGuid(), @"\\.\FAKE-GUARD-STARTUP-ONLY");
            var failedLauncher = new SessionGuard.GuardStartupEnvironment(
                (_, _, _) => throw new IOException("injected watcher start failure"),
                _ => { },
                _ => { });
            var failed = false;
            try { _ = new SessionGuard(first, requireUnowned: true, failedLauncher); }
            catch (IOException) { failed = true; }

            var ownershipPath = SessionGuard.LeasePath(first);
            var markerPath = ownershipPath + ".lease-id";
            check(failed && detachIds.SequenceEqual([first.LeaseId]),
                "watcher startup failure rolls back only the exact selected display");
            check(File.Exists(markerPath) && JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == Guid.Empty,
                "successful startup rollback retires the exact marker even though no guard object escapes the constructor");
            check(string.Equals(Path.GetDirectoryName(ownershipPath), DisplayLeaseProtectedStorage.LeaseFolder,
                    StringComparison.OrdinalIgnoreCase),
                "display ownership current, marker and bootstrap are published only below protected display-lease storage");

            var second = first with { LeaseId = Guid.NewGuid() };
            check(SessionGuard.LeasePath(first) == SessionGuard.LeasePath(second),
                "all owners of the same global display target converge on one protected marker path");
            var childLockEntered = false;
            var lockProbeLauncher = new SessionGuard.GuardStartupEnvironment(
                (_, _, _) =>
                {
                    Exception? childError = null;
                    var child = new Thread(() =>
                    {
                        try
                        {
                            using var childLease = DisplayLeaseProtectedStorage.AcquireLock();
                            childLockEntered = true;
                        }
                        catch (Exception ex) { childError = ex; }
                    }) { IsBackground = true };
                    child.Start();
                    if (!child.Join(TimeSpan.FromSeconds(2)))
                        throw new TimeoutException("injected child could not enter the display lease lock");
                    if (childError is not null)
                        throw new IOException("injected child display-lock probe failed", childError);
                },
                _ => { },
                _ => { });
            var successfulLauncher = new SessionGuard.GuardStartupEnvironment(
                (_, _, _) => { },
                _ => { },
                _ => { });
            using (var guard = new SessionGuard(second, requireUnowned: true, lockProbeLauncher))
            {
                check(childLockEntered &&
                      JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == second.LeaseId,
                    "parent releases the display lease lock before waiting for child readiness");
            }
            check(JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == Guid.Empty &&
                  detachIds.SequenceEqual([first.LeaseId, second.LeaseId]),
                "the replacement guard retains normal exact-display disposal and marker retirement");

            var third = first with { LeaseId = Guid.NewGuid() };
            SessionGuard? recovery = null;
            VirtualDisplayManager.OnDetach = lease =>
            {
                detachIds.Add(lease.LeaseId);
                return new(false, false, "injected native rollback failure");
            };
            try { _ = new SessionGuard(third, requireUnowned: true, failedLauncher); }
            catch (SessionGuard.GuardStartupFailureException ex) { recovery = ex.RecoveryGuard; }
            check(recovery is not null && JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == third.LeaseId,
                "failed watcher startup exposes an exact retryable guard when the first native rollback fails");
            var stopped = JsonSerializer.Deserialize<SessionGuard.WatchState>(File.ReadAllText(ownershipPath));
            check(stopped?.StopRequested == true,
                "a retained startup-recovery guard publishes stop before allocator-owned retry");
            VirtualDisplayManager.OnDetach = lease =>
            {
                detachIds.Add(lease.LeaseId);
                return new(true, true, "injected allocator retry succeeded");
            };
            recovery!.Dispose();
            check(JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == Guid.Empty &&
                  detachIds.TakeLast(2).SequenceEqual([third.LeaseId, third.LeaseId]),
                "the exposed recovery guard retries only the same display and retires its marker");

            var fourth = first with { LeaseId = Guid.NewGuid() };
            SessionGuard? unreadableRecovery = null;
            var unreadableLauncher = new SessionGuard.GuardStartupEnvironment(
                (_, _, _) =>
                {
                    markerLock = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    throw new IOException("injected watcher start failure after marker commit");
                },
                _ => { },
                _ => { });
            try { _ = new SessionGuard(fourth, requireUnowned: true, unreadableLauncher); }
            catch (SessionGuard.GuardStartupFailureException ex) { unreadableRecovery = ex.RecoveryGuard; }
            check(unreadableRecovery is not null,
                "a committed marker with a transient read failure remains owned and exposes a retryable guard");
            markerLock!.Dispose();
            unreadableRecovery!.Dispose();
            check(JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == Guid.Empty &&
                  detachIds.TakeLast(2).SequenceEqual([fourth.LeaseId, fourth.LeaseId]),
                "marker-read recovery later retires only the same committed display lease");

            var fifth = first with { LeaseId = Guid.NewGuid() };
            SessionGuard? committedRecovery = null;
            var injectedPostVerify = false;
            DisplayLeaseProtectedStorage.AfterCommitFailure = committedPath =>
            {
                if (injectedPostVerify || !committedPath.Equals(Path.GetFullPath(markerPath), StringComparison.OrdinalIgnoreCase))
                    return null;
                injectedPostVerify = true;
                return new UnauthorizedAccessException("injected marker post-commit verification failure");
            };
            VirtualDisplayManager.OnDetach = lease =>
            {
                detachIds.Add(lease.LeaseId);
                return new(false, false, "injected detach failure after committed marker");
            };
            try { _ = new SessionGuard(fifth, requireUnowned: true, successfulLauncher); }
            catch (SessionGuard.GuardStartupFailureException ex) { committedRecovery = ex.RecoveryGuard; }
            check(injectedPostVerify && committedRecovery is not null &&
                  JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == fifth.LeaseId,
                "post-verification failure after atomic marker commit preserves exact recovery authority");
            DisplayLeaseProtectedStorage.AfterCommitFailure = null;
            VirtualDisplayManager.OnDetach = lease =>
            {
                detachIds.Add(lease.LeaseId);
                return new(true, true, "injected committed-marker recovery succeeded");
            };
            committedRecovery!.Dispose();
            check(JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == Guid.Empty &&
                  detachIds.TakeLast(2).SequenceEqual([fifth.LeaseId, fifth.LeaseId]),
                "committed-marker recovery retries only the exact lease and retires its marker");

            var authoritativeOld = first with { LeaseId = Guid.NewGuid() };
            var uncommittedCurrent = first with { LeaseId = Guid.NewGuid() };
            var replacement = first with { LeaseId = Guid.NewGuid() };
            var oldState = new SessionGuard.WatchState(authoritativeOld, int.MaxValue, 1,
                DateTime.UtcNow.AddSeconds(-1));
            var uncommittedState = new SessionGuard.WatchState(uncommittedCurrent, int.MaxValue - 1, 1,
                DateTime.UtcNow.AddSeconds(-1));
            File.WriteAllText(ownershipPath, JsonSerializer.Serialize(uncommittedState));
            File.WriteAllText(ownershipPath + "." + authoritativeOld.LeaseId.ToString("N") + ".initial.json",
                JsonSerializer.Serialize(oldState));
            File.WriteAllText(markerPath, JsonSerializer.Serialize(authoritativeOld.LeaseId));
            VirtualDisplayManager.OnDetach = lease =>
            {
                detachIds.Add(lease.LeaseId);
                return new(true, true, "injected stale authoritative generation cleanup succeeded");
            };
            using (var replacementGuard = new SessionGuard(replacement, requireUnowned: true, successfulLauncher))
            {
                check(JsonSerializer.Deserialize<Guid>(File.ReadAllText(markerPath)) == replacement.LeaseId,
                    "require-unowned falls back to marker bootstrap when current is a later abandoned generation");
            }
            check(detachIds.TakeLast(1).Single() == replacement.LeaseId,
                "replacement guard retains exact disposal after marker-bootstrap crash-cut recovery");
        }
        finally
        {
            markerLock?.Dispose();
            DisplayLeaseProtectedStorage.AfterCommitFailure = null;
            VirtualDisplayManager.OnDetach = _ => throw new Exception("Unexpected fake detach after startup tests");
            DisplayLeaseProtectedStorage.LeaseFolder = previousFolder;
            var full = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts"));
            if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("GuardStartup-", StringComparison.Ordinal))
                throw new IOException("Refusing to delete an unexpected startup-test directory");
            Directory.Delete(full, recursive: true);
            if (Directory.Exists(expectedParent) && !Directory.EnumerateFileSystemEntries(expectedParent).Any())
                Directory.Delete(expectedParent);
        }
    }
}
