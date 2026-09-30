package com.tablink.client;

/** Maps media PTS to a bounded local monotonic presentation clock. Own on the codec thread. */
public final class RenderClock {
    public static final long MAX_AHEAD_NANOS = 25_000_000L;
    private static final long DISCONTINUITY_NANOS = 100_000_000L;
    private final boolean enabled;
    private final long leadNanos;
    private final long minimumLeadNanos;
    private final long frameNanos;
    private final long frameMicros;
    private boolean anchored, hasOutput, hasScheduled;
    private long anchorPtsUs, anchorNanos, lastOutputPtsUs, lastOutputNanos, lastScheduledNanos;
    private long outputs, scheduled, immediate, lateDrops, crowdedDrops, orderDrops, reanchors, aheadClamps, leadFloorReanchors, insufficientLeadFrames;
    private long lastAheadNanos, minimumObservedAheadNanos, maximumAheadNanos;
    private int consecutiveRecoveryDrops;
    private int consecutiveLowLeadFrames;

    public static final class Decision {
        public final boolean render;
        public final long renderNanos;
        private Decision(boolean render, long renderNanos) { this.render = render; this.renderNanos = renderNanos; }
    }

    public RenderClock(float fps, boolean enabled) {
        if (!Float.isFinite(fps) || fps < 1 || fps > 240) throw new IllegalArgumentException("Invalid frame rate");
        this.enabled = enabled;
        frameNanos = Math.round(1_000_000_000.0 / fps);
        frameMicros = (long) Math.ceil(1_000_000.0 / fps);
        leadNanos = Math.min(MAX_AHEAD_NANOS, frameNanos * 2);
        minimumLeadNanos = leadNanos * 3 / 4;
    }

    /** latestInputPtsUs is only a freshness hint, never a presentation acknowledgement. */
    public Decision plan(long ptsUs, long latestInputPtsUs, long nowNanos) {
        outputs++;
        if (ptsUs < 0 || (hasOutput && ptsUs <= lastOutputPtsUs)) {
            orderDrops++;
            return new Decision(false, 0);
        }
        boolean gap = hasOutput && (nowNanos < lastOutputNanos
                || nowNanos - lastOutputNanos > DISCONTINUITY_NANOS
                || ptsUs - lastOutputPtsUs > DISCONTINUITY_NANOS / 1000);
        if (hasOutput && nowNanos < lastOutputNanos) hasScheduled = false;
        lastOutputPtsUs = ptsUs;
        lastOutputNanos = nowNanos;
        hasOutput = true;
        if (!enabled) {
            immediate++;
            return new Decision(true, nowNanos);
        }
        boolean newerInputWaiting = latestInputPtsUs > ptsUs && latestInputPtsUs - ptsUs > frameMicros;
        if (gap) anchored = false;
        if (!anchored) {
            // Following a stall, discard already-decoded old outputs until close to the input head.
            if (skipOldOutput(newerInputWaiting)) return new Decision(false, 0);
            anchor(ptsUs, nowNanos + leadNanos);
        }
        long target;
        try {
            target = Math.addExact(anchorNanos, Math.multiplyExact(Math.subtractExact(ptsUs, anchorPtsUs), 1000));
        } catch (ArithmeticException discontinuity) {
            anchor(ptsUs, nowNanos + leadNanos);
            target = anchorNanos;
        }
        if (target < nowNanos && nowNanos - target > Math.min(frameNanos, MAX_AHEAD_NANOS)) {
            if (skipOldOutput(newerInputWaiting)) return new Decision(false, 0);
            // A sustained slower source must keep displaying its newest frame, not drop forever.
            anchor(ptsUs, nowNanos + leadNanos);
            target = anchorNanos;
        }
        if (target < nowNanos + minimumLeadNanos) {
            // Media PTS and the local arrival clock can slowly diverge after startup.
            // Ignore one isolated jitter sample: a hard floor and ceiling can otherwise fight every frame.
            insufficientLeadFrames++;
            if (++consecutiveLowLeadFrames >= 2) {
                leadFloorReanchors++;
                anchor(ptsUs, nowNanos + leadNanos);
                target = anchorNanos;
            }
        } else consecutiveLowLeadFrames = 0;
        if (target > nowNanos + MAX_AHEAD_NANOS) {
            // Correct drift/bursts by moving the media anchor, never enqueue an unbounded future.
            aheadClamps++;
            anchor(ptsUs, nowNanos + MAX_AHEAD_NANOS);
            target = anchorNanos;
        }
        target = Math.max(nowNanos, target);
        if (hasScheduled && target <= lastScheduledNanos) {
            // Do not submit two buffers with the same future deadline after a burst/re-anchor.
            crowdedDrops++;
            return new Decision(false, 0);
        }
        hasScheduled = true;
        lastScheduledNanos = target;
        lastAheadNanos = target - nowNanos;
        minimumObservedAheadNanos = scheduled == 0 ? lastAheadNanos : Math.min(minimumObservedAheadNanos, lastAheadNanos);
        maximumAheadNanos = Math.max(maximumAheadNanos, lastAheadNanos);
        scheduled++;
        consecutiveRecoveryDrops = 0;
        return new Decision(true, target);
    }

    private boolean skipOldOutput(boolean newerInputWaiting) {
        // Input can permanently lead decoded output by several hardware pipeline buffers.
        // It is only a freshness hint: never require catching it indefinitely before showing frames.
        if (!newerInputWaiting || consecutiveRecoveryDrops >= 2) return false;
        consecutiveRecoveryDrops++;
        lateDrops++;
        return true;
    }

    private void anchor(long ptsUs, long renderNanos) {
        if (scheduled > 0 || lateDrops > 0) reanchors++;
        anchorPtsUs = ptsUs;
        anchorNanos = renderNanos;
        anchored = true;
        consecutiveRecoveryDrops = 0;
        consecutiveLowLeadFrames = 0;
    }

    public boolean enabled() { return enabled; }
    public long leadNanos() { return leadNanos; }
    public long minimumLeadNanos() { return minimumLeadNanos; }
    public long outputFrames() { return outputs; }
    public long scheduledFrames() { return scheduled; }
    public long immediateFrames() { return immediate; }
    public long lateDrops() { return lateDrops; }
    public long crowdedDrops() { return crowdedDrops; }
    public long orderDrops() { return orderDrops; }
    public long reanchors() { return reanchors; }
    public long aheadClamps() { return aheadClamps; }
    public long leadFloorReanchors() { return leadFloorReanchors; }
    public long insufficientLeadFrames() { return insufficientLeadFrames; }
    public long lastAheadNanos() { return lastAheadNanos; }
    public long maximumAheadNanos() { return maximumAheadNanos; }
    public long minimumObservedAheadNanos() { return minimumObservedAheadNanos; }
    public long droppedFrames() { return lateDrops + crowdedDrops + orderDrops; }
}
