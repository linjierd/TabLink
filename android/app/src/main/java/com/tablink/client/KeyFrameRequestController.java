package com.tablink.client;

/** Connection-local coalescing token bucket for authenticated 0x15 requests. */
final class KeyFrameRequestController {
    private static final double CAPACITY = 2.0;
    private static final double TOKENS_PER_MILLISECOND = 1.0 / 1000.0;

    private long highestEpoch;
    private long pendingEpoch;
    private long tokenTimestampMillis = -1;
    private double tokens = CAPACITY;

    /**
     * Records a new logical recovery. Returns its epoch when one request may be
     * sent now, otherwise zero. The newest ineligible/rate-limited epoch remains
     * coalesced for retry on a later session tick.
     */
    synchronized long request(long epoch, long nowMillis, boolean eligible) {
        if (epoch <= 0 || nowMillis < 0) throw new IllegalArgumentException("Invalid recovery request");
        if (epoch <= highestEpoch) return 0;
        highestEpoch = epoch;
        pendingEpoch = epoch;
        return drain(nowMillis, eligible);
    }

    /** Retries only the newest pending recovery without creating another epoch. */
    synchronized long retry(long nowMillis, boolean eligible) {
        if (nowMillis < 0) throw new IllegalArgumentException("Invalid retry time");
        return drain(nowMillis, eligible);
    }

    synchronized void cancelPending() { pendingEpoch = 0; }
    synchronized void cancelPendingThrough(long epoch) {
        if (epoch < 0) throw new IllegalArgumentException("Cancelled epoch must be nonnegative");
        if (pendingEpoch <= epoch) pendingEpoch = 0;
    }
    synchronized long pendingEpoch() { return pendingEpoch; }

    private long drain(long nowMillis, boolean eligible) {
        refill(nowMillis);
        if (!eligible || pendingEpoch <= 0 || tokens < 1.0) return 0;
        tokens -= 1.0;
        long allowed = pendingEpoch;
        pendingEpoch = 0;
        return allowed;
    }

    private void refill(long nowMillis) {
        if (tokenTimestampMillis < 0) {
            tokenTimestampMillis = nowMillis;
            return;
        }
        if (nowMillis <= tokenTimestampMillis) return;
        tokens = Math.min(CAPACITY, tokens + (nowMillis - tokenTimestampMillis) * TOKENS_PER_MILLISECOND);
        tokenTimestampMillis = nowMillis;
    }
}
