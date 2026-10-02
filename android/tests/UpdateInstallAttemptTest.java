package com.tablink.client;

import java.util.HashMap;
import java.util.Map;

/** Pure-JVM tests for PackageInstaller attempt identity and damaged preference recovery. */
public final class UpdateInstallAttemptTest {
    private static int assertions;

    public static void main(String[] args) {
        UpdateInstallAttempt first = UpdateInstallAttempt.create(41);
        UpdateInstallAttempt second = UpdateInstallAttempt.create(42);
        check(first.token.length() == 64 && second.token.length() == 64
                        && !first.token.equals(second.token),
                "every installer commit receives an independent random token");
        check(!first.callbackIdentity().equals(second.callbackIdentity())
                        && first.callbackIdentity().endsWith(first.token),
                "random token makes PendingIntent identity unique per attempt");
        check(UpdateInstallAttempt.validatedCallback(first.sessionId, first.token,
                        first.sessionId).matches(first)
                        && UpdateInstallAttempt.validatedCallback(first.sessionId, first.token,
                                second.sessionId) == null
                        && UpdateInstallAttempt.validatedCallback(first.sessionId, first.token, -1) == null,
                "callback requires the PackageInstaller session id to match the private attempt id");
        Map<Integer, Boolean> committedSessions = new HashMap<>();
        committedSessions.put(second.sessionId, true);
        committedSessions.put(first.sessionId, true);
        Map<Integer, Boolean> unsealedSession = new HashMap<>();
        unsealedSession.put(first.sessionId, false);
        check(UpdateInstallAttempt.isSessionRestorable(first, committedSessions, true)
                        && !UpdateInstallAttempt.isSessionRestorable(first,
                                java.util.Collections.singletonMap(second.sessionId, true), true)
                        && !UpdateInstallAttempt.isSessionRestorable(first, unsealedSession, true)
                        && !UpdateInstallAttempt.isSessionRestorable(first, committedSessions, false)
                        && !UpdateInstallAttempt.isSessionRestorable(first, null, true),
                "restart recovery requires the exact sealed session and fails closed without seal status");

        Map<String, Object> currentValues = new HashMap<>();
        currentValues.put(UpdateInstallAttempt.CURRENT_SESSION_ID, first.sessionId);
        currentValues.put(UpdateInstallAttempt.CURRENT_TOKEN, first.token);
        UpdateInstallAttempt.Snapshot current = UpdateInstallAttempt.read(currentValues);
        check(!current.damagedCurrent && current.accepts(first) && !current.accepts(second),
                "only the persisted current tuple accepts a callback");
        check(current.sessionToAbandonOnClear() == first.sessionId,
                "selecting never exposes the exact current PackageInstaller session to abandon");

        Map<String, Object> clearedByNever = new HashMap<>(currentValues);
        clearedByNever.remove(UpdateInstallAttempt.CURRENT_SESSION_ID);
        clearedByNever.remove(UpdateInstallAttempt.CURRENT_TOKEN);
        UpdateInstallAttempt.Snapshot afterNever = UpdateInstallAttempt.read(clearedByNever);
        check(!afterNever.accepts(first) && afterNever.sessionToAbandonOnClear() < 0,
                "after never atomically clears the tuple, a late callback has no authority");

        currentValues.put(UpdateInstallAttempt.RESULT_SESSION_ID, first.sessionId);
        currentValues.put(UpdateInstallAttempt.RESULT_TOKEN, first.token);
        currentValues.put(UpdateInstallAttempt.RESULT_STATUS, -1);
        currentValues.put(UpdateInstallAttempt.RESULT_MESSAGE, "pending");
        UpdateInstallAttempt.Snapshot pending = UpdateInstallAttempt.read(currentValues);
        check(!pending.damagedResult && pending.result != null
                        && pending.result.attempt.matches(first) && pending.result.status == -1,
                "result is bound to the same session and token");

        Map<String, Object> damagedCurrent = new HashMap<>();
        damagedCurrent.put(UpdateInstallAttempt.CURRENT_SESSION_ID, "41");
        damagedCurrent.put(UpdateInstallAttempt.CURRENT_TOKEN, first.token);
        UpdateInstallAttempt.Snapshot badCurrent = UpdateInstallAttempt.read(damagedCurrent);
        check(badCurrent.damagedCurrent && badCurrent.current == null && !badCurrent.accepts(first),
                "wrong current-session preference type is rejected without a cast");

        Map<String, Object> partialCurrent = new HashMap<>();
        partialCurrent.put(UpdateInstallAttempt.CURRENT_SESSION_ID, first.sessionId);
        UpdateInstallAttempt.Snapshot incompleteCurrent = UpdateInstallAttempt.read(partialCurrent);
        check(incompleteCurrent.damagedCurrent && incompleteCurrent.abandonCandidateSessionId == first.sessionId,
                "partial current tuple is cleared while retaining a safe abandon candidate");
        check(incompleteCurrent.sessionToAbandonOnClear() == first.sessionId,
                "damaged current state still exposes a safe session for best-effort abandon");

        Map<String, Object> damagedResult = new HashMap<>();
        damagedResult.put(UpdateInstallAttempt.RESULT_SESSION_ID, first.sessionId);
        damagedResult.put(UpdateInstallAttempt.RESULT_TOKEN, first.token);
        damagedResult.put(UpdateInstallAttempt.RESULT_STATUS, "success");
        damagedResult.put(UpdateInstallAttempt.RESULT_MESSAGE, 7);
        UpdateInstallAttempt.Snapshot badResult = UpdateInstallAttempt.read(damagedResult);
        check(badResult.damagedResult && badResult.result == null,
                "wrong previous-result preference types are rejected without a cast");

        Map<String, Object> invalidToken = new HashMap<>();
        invalidToken.put(UpdateInstallAttempt.CURRENT_SESSION_ID, first.sessionId);
        invalidToken.put(UpdateInstallAttempt.CURRENT_TOKEN, "not-a-random-token");
        check(UpdateInstallAttempt.read(invalidToken).damagedCurrent,
                "malformed persisted attempt token is damaged state");
        check(UpdateInstallAttempt.read(null).damagedCurrent
                        && UpdateInstallAttempt.read(null).damagedResult,
                "unreadable preferences fail closed without throwing");

        Map<String, Object> noMode = new HashMap<>();
        check(UpdateModePreference.read(noMode).mode == UpdateStateMachine.Mode.AUTOMATIC,
                "missing update preference remains automatic");
        Map<String, Object> legacyTrue = new HashMap<>();
        legacyTrue.put(UpdateModePreference.LEGACY_AUTO_KEY, true);
        Map<String, Object> legacyFalse = new HashMap<>();
        legacyFalse.put(UpdateModePreference.LEGACY_AUTO_KEY, false);
        check(UpdateModePreference.read(legacyTrue).mode == UpdateStateMachine.Mode.AUTOMATIC
                        && UpdateModePreference.read(legacyTrue).migrateLegacy,
                "legacy true migrates to automatic");
        check(UpdateModePreference.read(legacyFalse).mode == UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK
                        && UpdateModePreference.read(legacyFalse).migrateLegacy,
                "legacy false preserves manual-update behavior as download-then-ask");
        Map<String, Object> damagedMode = new HashMap<>();
        damagedMode.put(UpdateModePreference.MODE_KEY, 1);
        check(UpdateModePreference.read(damagedMode).damaged
                        && UpdateModePreference.read(damagedMode).mode == UpdateStateMachine.Mode.NEVER,
                "only a damaged preference fails closed to never");

        System.out.println("PASS: " + assertions + " installer-attempt assertions");
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        assertions++;
    }
}
