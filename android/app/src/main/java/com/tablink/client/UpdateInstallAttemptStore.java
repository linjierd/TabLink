package com.tablink.client;

import android.content.SharedPreferences;

import java.util.HashSet;
import java.util.Map;

/** Serialises PackageInstaller callbacks with persisted current-attempt state. */
final class UpdateInstallAttemptStore {
    private static final Object LOCK = new Object();
    private static final HashSet<String> REVOKED_TOKENS = new HashSet<>();

    static final class Recovery {
        final UpdateInstallAttempt current;
        final UpdateInstallAttempt.Result result;
        final boolean retryRequired;
        final int abandonSessionId;
        final boolean storageFailure;
        Recovery(UpdateInstallAttempt current, UpdateInstallAttempt.Result result,
                boolean retryRequired, int abandonSessionId, boolean storageFailure) {
            this.current = current;
            this.result = result;
            this.retryRequired = retryRequired;
            this.abandonSessionId = abandonSessionId;
            this.storageFailure = storageFailure;
        }
    }

    private UpdateInstallAttemptStore() { }

    static Recovery recover(SharedPreferences preferences, int pendingUserActionStatus) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            boolean revokedCurrent = snapshot.current != null
                    && REVOKED_TOKENS.contains(snapshot.current.token);
            boolean damaged = snapshot.damagedCurrent || snapshot.damagedResult;
            boolean pendingMismatch = snapshot.result != null
                    && snapshot.result.status == pendingUserActionStatus
                    && (snapshot.current == null || !snapshot.current.matches(snapshot.result.attempt));
            boolean terminalWithCurrent = snapshot.result != null
                    && snapshot.result.status != pendingUserActionStatus && snapshot.current != null;
            boolean mismatchedResult = snapshot.result != null && snapshot.current != null
                    && !snapshot.current.matches(snapshot.result.attempt);
            boolean repairResult = snapshot.damagedResult || pendingMismatch
                    || terminalWithCurrent || mismatchedResult;
            // A malformed or mismatched result makes the entire transaction
            // ambiguous. Abandon a still-live session as well as deleting the
            // result so recovery always returns a clean RETRY state.
            boolean repairCurrent = snapshot.damagedCurrent || revokedCurrent || repairResult;
            boolean retry = damaged || revokedCurrent || pendingMismatch || terminalWithCurrent || mismatchedResult;
            boolean storageFailure = false;
            if (repairCurrent || repairResult) {
                SharedPreferences.Editor editor = preferences.edit();
                if (repairCurrent) removeCurrent(editor);
                if (repairCurrent || repairResult) removeResult(editor);
                boolean repaired = commit(editor);
                if (!repaired) repaired = clearAttemptState(preferences);
                storageFailure = !repaired;
                if (repairCurrent) {
                    revoke(snapshot.current);
                    releaseGate(snapshot);
                }
            }
            UpdateInstallAttempt current = repairCurrent ? null : snapshot.current;
            UpdateInstallAttempt.Result result = repairCurrent || repairResult ? null : snapshot.result;
            return new Recovery(current, result, retry || storageFailure,
                    repairCurrent && snapshot.current != null ? snapshot.current.sessionId
                            : repairCurrent ? snapshot.abandonCandidateSessionId : -1,
                    storageFailure);
        }
    }

    static boolean begin(SharedPreferences preferences, UpdateInstallAttempt attempt) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            if (attempt == null || snapshot.damagedCurrent || snapshot.damagedResult
                    || snapshot.current != null) return false;
            if (!UpdateInstallerUiGate.tryBeginInstallerTransaction(attempt)) return false;
            REVOKED_TOKENS.remove(attempt.token);
            SharedPreferences.Editor editor = preferences.edit()
                    .putInt(UpdateInstallAttempt.CURRENT_SESSION_ID, attempt.sessionId)
                    .putString(UpdateInstallAttempt.CURRENT_TOKEN, attempt.token);
            removeResult(editor);
            boolean saved = commit(editor);
            if (!saved) {
                // commit() may already have changed SharedPreferences' in-memory
                // map even when the disk write failed. Revoke and erase it again
                // before releasing the gate so it cannot be restored later.
                revoke(attempt);
                clearAttemptState(preferences);
                UpdateInstallerUiGate.installerTransactionFinished(attempt);
            }
            return saved;
        }
    }

    static UpdateInstallerUiGate.LaunchResult launchConfirmation(SharedPreferences preferences,
            UpdateInstallAttempt attempt, int status, String message, Runnable launch) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            if (!accepts(snapshot, attempt)) return UpdateInstallerUiGate.LaunchResult.STALE_ATTEMPT;
            return UpdateInstallerUiGate.tryLaunchInstallerConfirmation(attempt, () -> {
                SharedPreferences.Editor editor = preferences.edit()
                        .putInt(UpdateInstallAttempt.RESULT_SESSION_ID, attempt.sessionId)
                        .putString(UpdateInstallAttempt.RESULT_TOKEN, attempt.token)
                        .putInt(UpdateInstallAttempt.RESULT_STATUS, status)
                        .putString(UpdateInstallAttempt.RESULT_MESSAGE, message);
                boolean saved = commit(editor);
                if (!saved) saved = persistResult(preferences, attempt, status, message, false);
                if (!saved) {
                    throw new IllegalStateException("无法保存安装确认状态");
                }
                launch.run();
            });
        }
    }

    static boolean finish(SharedPreferences preferences, UpdateInstallAttempt attempt,
            int status, String message) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            if (!accepts(snapshot, attempt)) return false;
            SharedPreferences.Editor editor = preferences.edit()
                    .remove(UpdateInstallAttempt.CURRENT_SESSION_ID)
                    .remove(UpdateInstallAttempt.CURRENT_TOKEN)
                    .putInt(UpdateInstallAttempt.RESULT_SESSION_ID, attempt.sessionId)
                    .putString(UpdateInstallAttempt.RESULT_TOKEN, attempt.token)
                    .putInt(UpdateInstallAttempt.RESULT_STATUS, status)
                    .putString(UpdateInstallAttempt.RESULT_MESSAGE, message);
            boolean saved = commit(editor);
            if (!saved) saved = persistResult(preferences, attempt, status, message, true);
            if (!saved) {
                revoke(attempt);
                clearAttemptState(preferences);
            }
            // A terminal callback must never strand the display gate, even when
            // the result could not be made durable.
            UpdateInstallerUiGate.installerTransactionFinished(attempt);
            return saved;
        }
    }

    static boolean discardIfCurrent(SharedPreferences preferences, UpdateInstallAttempt attempt,
            boolean forceGateClear) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            if (!accepts(snapshot, attempt)) return false;
            revoke(attempt);
            SharedPreferences.Editor editor = preferences.edit();
            removeCurrent(editor);
            removeResult(editor);
            boolean saved = commit(editor);
            if (!saved) saved = clearAttemptState(preferences);
            if (saved || forceGateClear) UpdateInstallerUiGate.installerTransactionFinished(attempt);
            return saved;
        }
    }

    static Recovery disableAndClear(SharedPreferences preferences) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            revoke(snapshot.current);
            SharedPreferences.Editor editor = preferences.edit()
                    .putString(UpdateModePreference.MODE_KEY, UpdateStateMachine.Mode.NEVER.name())
                    .remove(UpdateModePreference.LEGACY_AUTO_KEY);
            removeCurrent(editor);
            removeResult(editor);
            boolean saved = commit(editor);
            if (!saved) saved = persistNeverAndClear(preferences);
            releaseGate(snapshot);
            int abandon = snapshot.sessionToAbandonOnClear();
            return new Recovery(null, null, true, abandon, !saved);
        }
    }

    static Recovery clearAll(SharedPreferences preferences) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            revoke(snapshot.current);
            SharedPreferences.Editor editor = preferences.edit();
            removeCurrent(editor);
            removeResult(editor);
            boolean saved = commit(editor);
            if (!saved) saved = clearAttemptState(preferences);
            releaseGate(snapshot);
            int abandon = snapshot.sessionToAbandonOnClear();
            return new Recovery(null, null, true, abandon, !saved);
        }
    }

    /** Atomically persists a conflict marker while revoking every old install authority. */
    static Recovery blockConflictFloorAndClear(SharedPreferences preferences,
            String floorUtcKey, String floorDecisionKey, long utc, String decision) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            revoke(snapshot.current);
            boolean saved = persistFloorAndClear(preferences, floorUtcKey,
                    floorDecisionKey, utc, decision);
            if (!saved) saved = persistFloorAndClear(preferences, floorUtcKey,
                    floorDecisionKey, utc, decision);
            releaseGate(snapshot);
            return new Recovery(null, null, true, snapshot.sessionToAbandonOnClear(), !saved);
        }
    }

    /** Atomically replaces a blocked manifest floor and removes every old install authority. */
    static Recovery replaceBlockedFloorAndClear(SharedPreferences preferences,
            String floorUtcKey, String floorDecisionKey, long blockedUtc, String blockedDecision,
            long utc, String decision) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            revoke(snapshot.current);
            SharedPreferences.Editor editor = preferences.edit()
                    .putLong(floorUtcKey, utc)
                    .putString(floorDecisionKey, decision);
            removeCurrent(editor);
            removeResult(editor);
            boolean saved = commit(editor);
            if (!saved) restoreBlockedFloorAndClear(preferences, floorUtcKey,
                    floorDecisionKey, blockedUtc, blockedDecision);
            releaseGate(snapshot);
            return new Recovery(null, null, true, snapshot.sessionToAbandonOnClear(), !saved);
        }
    }

    static UpdateInstallAttempt current(SharedPreferences preferences) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            return snapshot.damagedCurrent || snapshot.current != null
                    && REVOKED_TOKENS.contains(snapshot.current.token) ? null : snapshot.current;
        }
    }

    static UpdateInstallAttempt.Result result(SharedPreferences preferences) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            return snapshot.damagedResult ? null : snapshot.result;
        }
    }

    static boolean clearResult(SharedPreferences preferences, UpdateInstallAttempt.Result result) {
        synchronized (LOCK) {
            UpdateInstallAttempt.Snapshot snapshot = read(preferences);
            if (result == null || snapshot.result == null
                    || !snapshot.result.attempt.matches(result.attempt)
                    || snapshot.result.status != result.status) return false;
            SharedPreferences.Editor editor = preferences.edit();
            removeResult(editor);
            boolean saved = commit(editor);
            if (!saved) saved = clearResultState(preferences);
            return saved;
        }
    }

    private static UpdateInstallAttempt.Snapshot read(SharedPreferences preferences) {
        try {
            Map<String, ?> values = preferences.getAll();
            return UpdateInstallAttempt.read(values);
        } catch (RuntimeException failure) {
            return UpdateInstallAttempt.read(null);
        }
    }

    private static boolean accepts(UpdateInstallAttempt.Snapshot snapshot,
            UpdateInstallAttempt attempt) {
        return snapshot.accepts(attempt) && !REVOKED_TOKENS.contains(attempt.token);
    }

    private static void revoke(UpdateInstallAttempt attempt) {
        if (attempt == null) return;
        REVOKED_TOKENS.add(attempt.token);
    }

    private static void releaseGate(UpdateInstallAttempt.Snapshot snapshot) {
        if (snapshot.current != null)
            UpdateInstallerUiGate.installerTransactionFinished(snapshot.current);
        else if (snapshot.damagedCurrent) UpdateInstallerUiGate.clearInstallerTransactionForRecovery();
    }

    private static boolean clearAttemptState(SharedPreferences preferences) {
        SharedPreferences.Editor cleanup = preferences.edit();
        removeCurrent(cleanup);
        removeResult(cleanup);
        return commit(cleanup);
    }

    private static boolean clearResultState(SharedPreferences preferences) {
        SharedPreferences.Editor cleanup = preferences.edit();
        removeResult(cleanup);
        return commit(cleanup);
    }

    private static boolean persistResult(SharedPreferences preferences,
            UpdateInstallAttempt attempt, int status, String message, boolean terminal) {
        SharedPreferences.Editor retry = preferences.edit()
                .putInt(UpdateInstallAttempt.RESULT_SESSION_ID, attempt.sessionId)
                .putString(UpdateInstallAttempt.RESULT_TOKEN, attempt.token)
                .putInt(UpdateInstallAttempt.RESULT_STATUS, status)
                .putString(UpdateInstallAttempt.RESULT_MESSAGE, message);
        if (terminal) removeCurrent(retry);
        return commit(retry);
    }

    private static boolean persistNeverAndClear(SharedPreferences preferences) {
        SharedPreferences.Editor cleanup = preferences.edit()
                .putString(UpdateModePreference.MODE_KEY, UpdateStateMachine.Mode.NEVER.name())
                .remove(UpdateModePreference.LEGACY_AUTO_KEY);
        removeCurrent(cleanup);
        removeResult(cleanup);
        return commit(cleanup);
    }

    private static void restoreBlockedFloorAndClear(SharedPreferences preferences,
            String floorUtcKey, String floorDecisionKey, long blockedUtc, String blockedDecision) {
        persistFloorAndClear(preferences, floorUtcKey, floorDecisionKey,
                blockedUtc, blockedDecision);
    }

    private static boolean persistFloorAndClear(SharedPreferences preferences,
            String floorUtcKey, String floorDecisionKey, long utc, String decision) {
        SharedPreferences.Editor editor = preferences.edit()
                .putLong(floorUtcKey, utc)
                .putString(floorDecisionKey, decision);
        removeCurrent(editor);
        removeResult(editor);
        return commit(editor);
    }

    private static boolean commit(SharedPreferences.Editor editor) {
        try { return editor.commit(); }
        catch (RuntimeException failure) { return false; }
    }

    private static void removeCurrent(SharedPreferences.Editor editor) {
        editor.remove(UpdateInstallAttempt.CURRENT_SESSION_ID)
                .remove(UpdateInstallAttempt.CURRENT_TOKEN);
    }

    private static void removeResult(SharedPreferences.Editor editor) {
        editor.remove(UpdateInstallAttempt.RESULT_SESSION_ID)
                .remove(UpdateInstallAttempt.RESULT_TOKEN)
                .remove(UpdateInstallAttempt.RESULT_STATUS)
                .remove(UpdateInstallAttempt.RESULT_MESSAGE);
    }
}
