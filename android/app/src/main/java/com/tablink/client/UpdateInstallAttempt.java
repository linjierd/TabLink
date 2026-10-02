package com.tablink.client;

import java.security.SecureRandom;
import java.util.Map;

/** Pure value/parser for one PackageInstaller submission and its persisted result. */
final class UpdateInstallAttempt {
    static final String CURRENT_SESSION_ID = "install-current-session-id";
    static final String CURRENT_TOKEN = "install-current-token";
    static final String RESULT_SESSION_ID = "install-result-session-id";
    static final String RESULT_TOKEN = "install-result-token";
    static final String RESULT_STATUS = "install-result-status";
    static final String RESULT_MESSAGE = "install-result-message";
    private static final SecureRandom RANDOM = new SecureRandom();

    final int sessionId;
    final String token;

    private UpdateInstallAttempt(int sessionId, String token) {
        this.sessionId = sessionId;
        this.token = token;
    }

    static UpdateInstallAttempt create(int sessionId) {
        if (sessionId < 0) throw new IllegalArgumentException("sessionId");
        byte[] random = new byte[32];
        RANDOM.nextBytes(random);
        StringBuilder token = new StringBuilder(64);
        for (byte value : random) token.append(Character.forDigit((value >>> 4) & 15, 16))
                .append(Character.forDigit(value & 15, 16));
        return new UpdateInstallAttempt(sessionId, token.toString());
    }

    static UpdateInstallAttempt validated(int sessionId, String token) {
        return sessionId >= 0 && validToken(token) ? new UpdateInstallAttempt(sessionId, token) : null;
    }

    static UpdateInstallAttempt validatedCallback(int attemptSessionId, String token,
            int installerSessionId) {
        return attemptSessionId == installerSessionId
                ? validated(attemptSessionId, token) : null;
    }

    static boolean isSessionRestorable(UpdateInstallAttempt attempt,
            Map<Integer, Boolean> sessions, boolean hasReliableSealStatus) {
        if (attempt == null || sessions == null || !hasReliableSealStatus) return false;
        Boolean sealed = sessions.get(attempt.sessionId);
        return Boolean.TRUE.equals(sealed);
    }

    boolean matches(UpdateInstallAttempt other) {
        return other != null && sessionId == other.sessionId && token.equals(other.token);
    }

    String callbackIdentity() {
        return "tablink-internal://update-install-result/" + token;
    }

    static Snapshot read(Map<String, ?> values) {
        if (values == null) return new Snapshot(null, null, true, true, -1);
        boolean hasCurrentSession = values.containsKey(CURRENT_SESSION_ID);
        boolean hasCurrentToken = values.containsKey(CURRENT_TOKEN);
        Object rawCurrentSession = values.get(CURRENT_SESSION_ID);
        Object rawCurrentToken = values.get(CURRENT_TOKEN);
        int abandonCandidate = rawCurrentSession instanceof Integer && (Integer) rawCurrentSession >= 0
                ? (Integer) rawCurrentSession : -1;
        UpdateInstallAttempt current = hasCurrentSession && hasCurrentToken
                && rawCurrentSession instanceof Integer && rawCurrentToken instanceof String
                ? validated((Integer) rawCurrentSession, (String) rawCurrentToken) : null;
        boolean damagedCurrent = hasCurrentSession != hasCurrentToken
                || hasCurrentSession && current == null;

        boolean hasResultSession = values.containsKey(RESULT_SESSION_ID);
        boolean hasResultToken = values.containsKey(RESULT_TOKEN);
        boolean hasResultStatus = values.containsKey(RESULT_STATUS);
        boolean hasResultMessage = values.containsKey(RESULT_MESSAGE);
        boolean anyResult = hasResultSession || hasResultToken || hasResultStatus || hasResultMessage;
        Result result = null;
        if (hasResultSession && hasResultToken && hasResultStatus && hasResultMessage
                && values.get(RESULT_SESSION_ID) instanceof Integer
                && values.get(RESULT_TOKEN) instanceof String
                && values.get(RESULT_STATUS) instanceof Integer
                && values.get(RESULT_MESSAGE) instanceof String) {
            UpdateInstallAttempt resultAttempt = validated((Integer) values.get(RESULT_SESSION_ID),
                    (String) values.get(RESULT_TOKEN));
            int status = (Integer) values.get(RESULT_STATUS);
            String message = (String) values.get(RESULT_MESSAGE);
            if (resultAttempt != null && status != Integer.MIN_VALUE && message.length() <= 512)
                result = new Result(resultAttempt, status, message);
        }
        boolean damagedResult = anyResult && result == null;
        return new Snapshot(current, result, damagedCurrent, damagedResult, abandonCandidate);
    }

    private static boolean validToken(String token) {
        if (token == null || token.length() != 64) return false;
        for (int i = 0; i < token.length(); i++) {
            char value = token.charAt(i);
            if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) return false;
        }
        return true;
    }

    static final class Result {
        final UpdateInstallAttempt attempt;
        final int status;
        final String message;
        Result(UpdateInstallAttempt attempt, int status, String message) {
            this.attempt = attempt;
            this.status = status;
            this.message = message;
        }
    }

    static final class Snapshot {
        final UpdateInstallAttempt current;
        final Result result;
        final boolean damagedCurrent;
        final boolean damagedResult;
        final int abandonCandidateSessionId;
        Snapshot(UpdateInstallAttempt current, Result result, boolean damagedCurrent,
                boolean damagedResult, int abandonCandidateSessionId) {
            this.current = current;
            this.result = result;
            this.damagedCurrent = damagedCurrent;
            this.damagedResult = damagedResult;
            this.abandonCandidateSessionId = abandonCandidateSessionId;
        }

        boolean accepts(UpdateInstallAttempt callback) {
            return !damagedCurrent && current != null && current.matches(callback);
        }

        int sessionToAbandonOnClear() {
            return current != null ? current.sessionId : abandonCandidateSessionId;
        }
    }
}
