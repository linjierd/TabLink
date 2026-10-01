package com.tablink.client;

/**
 * Connection-scoped, cumulative receiver telemetry. Reports never count as
 * decoder submission or presentation evidence.
 */
final class ReceiverFeedbackProgress {
    static final long REPORT_INTERVAL_MILLIS = 1000;

    static final class DecoderMetrics {
        final long sourceId;
        final int queueDepth, queueCapacity, queueHighWaterMark;
        final boolean awaitingKeyFrame;
        final long recoveryKeyFramePtsUs;
        final long inputDroppedFrames, overflowEvents, overflowDrops, expiredDrops;
        final long awaitingKeyFrameDrops, backpressureTimeouts, renderDrops;

        DecoderMetrics(long sourceId, int queueDepth, int queueCapacity, int queueHighWaterMark,
                boolean awaitingKeyFrame, long recoveryKeyFramePtsUs, long inputDroppedFrames, long overflowEvents,
                long overflowDrops, long expiredDrops, long awaitingKeyFrameDrops,
                long backpressureTimeouts, long renderDrops) {
            this.sourceId = sourceId;
            this.queueDepth = queueDepth;
            this.queueCapacity = queueCapacity;
            this.queueHighWaterMark = queueHighWaterMark;
            this.awaitingKeyFrame = awaitingKeyFrame;
            this.recoveryKeyFramePtsUs = recoveryKeyFramePtsUs;
            this.inputDroppedFrames = inputDroppedFrames;
            this.overflowEvents = overflowEvents;
            this.overflowDrops = overflowDrops;
            this.expiredDrops = expiredDrops;
            this.awaitingKeyFrameDrops = awaitingKeyFrameDrops;
            this.backpressureTimeouts = backpressureTimeouts;
            this.renderDrops = renderDrops;
        }
    }

    static final class Report {
        final long sequence, decoderEpoch, recoveryEpoch;
        final long receivedVideoFrames, receivedVideoBytes, submittedFrames, presentedFrames;
        final int queueDepth, queueCapacity, queueHighWaterMark;
        final long inputDroppedFrames, overflowEvents, overflowDrops, expiredDrops;
        final long awaitingKeyFrameDrops, backpressureTimeouts, renderDrops, decoderFallbacks;
        final boolean awaitingKeyFrame;

        Report(long sequence, long decoderEpoch, long recoveryEpoch, long receivedVideoFrames,
                long receivedVideoBytes, long submittedFrames, long presentedFrames,
                int queueDepth, int queueCapacity, int queueHighWaterMark,
                long inputDroppedFrames, long overflowEvents, long overflowDrops, long expiredDrops,
                long awaitingKeyFrameDrops, long backpressureTimeouts, long renderDrops,
                long decoderFallbacks, boolean awaitingKeyFrame) {
            this.sequence = sequence;
            this.decoderEpoch = decoderEpoch;
            this.recoveryEpoch = recoveryEpoch;
            this.receivedVideoFrames = receivedVideoFrames;
            this.receivedVideoBytes = receivedVideoBytes;
            this.submittedFrames = submittedFrames;
            this.presentedFrames = presentedFrames;
            this.queueDepth = queueDepth;
            this.queueCapacity = queueCapacity;
            this.queueHighWaterMark = queueHighWaterMark;
            this.inputDroppedFrames = inputDroppedFrames;
            this.overflowEvents = overflowEvents;
            this.overflowDrops = overflowDrops;
            this.expiredDrops = expiredDrops;
            this.awaitingKeyFrameDrops = awaitingKeyFrameDrops;
            this.backpressureTimeouts = backpressureTimeouts;
            this.renderDrops = renderDrops;
            this.decoderFallbacks = decoderFallbacks;
            this.awaitingKeyFrame = awaitingKeyFrame;
        }

        String toJson() {
            return new StringBuilder(512)
                    .append('{')
                    .append("\"kind\":\"receiver-feedback\"")
                    .append(",\"sequence\":").append(sequence)
                    .append(",\"decoderEpoch\":").append(decoderEpoch)
                    .append(",\"recoveryEpoch\":").append(recoveryEpoch)
                    .append(",\"receivedVideoFrames\":").append(receivedVideoFrames)
                    .append(",\"receivedVideoBytes\":").append(receivedVideoBytes)
                    .append(",\"submittedFrames\":").append(submittedFrames)
                    .append(",\"presentedFrames\":").append(presentedFrames)
                    .append(",\"queueDepth\":").append(queueDepth)
                    .append(",\"queueCapacity\":").append(queueCapacity)
                    .append(",\"queueHighWaterMark\":").append(queueHighWaterMark)
                    .append(",\"inputDroppedFrames\":").append(inputDroppedFrames)
                    .append(",\"overflowEvents\":").append(overflowEvents)
                    .append(",\"overflowDrops\":").append(overflowDrops)
                    .append(",\"expiredDrops\":").append(expiredDrops)
                    .append(",\"awaitingKeyFrameDrops\":").append(awaitingKeyFrameDrops)
                    .append(",\"backpressureTimeouts\":").append(backpressureTimeouts)
                    .append(",\"renderDrops\":").append(renderDrops)
                    .append(",\"decoderFallbacks\":").append(decoderFallbacks)
                    .append(",\"awaitingKeyFrame\":").append(awaitingKeyFrame)
                    .append('}').toString();
        }
    }

    private long sequence, decoderEpoch, recoveryEpoch;
    private long receivedVideoFrames, receivedVideoBytes, submittedFrames, presentedFrames;
    private long decoderFallbacks;
    private long decoderSourceId = -1;
    private long lastInputDroppedFrames, lastOverflowEvents, lastOverflowDrops, lastExpiredDrops;
    private long lastAwaitingKeyFrameDrops, lastBackpressureTimeouts, lastRenderDrops;
    private long inputDroppedFrames, overflowEvents, overflowDrops, expiredDrops;
    private long awaitingKeyFrameDrops, backpressureTimeouts, renderDrops;
    private int queueDepth, queueCapacity, queueHighWaterMark;
    private boolean decoderActive, queueAwaitingKeyFrame;
    private long lastReportMillis = -1;

    synchronized void receivedVideo(int bytes) {
        if (bytes < 0) throw new IllegalArgumentException("Video bytes must be nonnegative");
        receivedVideoFrames++;
        receivedVideoBytes = saturatedAdd(receivedVideoBytes, bytes);
    }

    synchronized void submitted() { submittedFrames++; }
    synchronized void presented() { presentedFrames++; }

    synchronized void decoderSelected(boolean fallback) {
        decoderActive = true;
        decoderEpoch++;
        if (fallback) decoderFallbacks++;
    }

    synchronized long recoveryStarted() {
        recoveryEpoch++;
        if (recoveryEpoch <= 0) recoveryEpoch = 1;
        return recoveryEpoch;
    }

    synchronized void observeDecoder(DecoderMetrics metrics) {
        if (metrics == null || metrics.sourceId <= 0) return;
        // VideoDecoder ids increase monotonically. A callback/ticker can finish
        // observing an old decoder after a replacement has already been seen;
        // never let that stale sample reopen the old counter source.
        if (decoderSourceId > 0 && metrics.sourceId < decoderSourceId) return;
        if (metrics.sourceId != decoderSourceId) {
            decoderSourceId = metrics.sourceId;
            lastInputDroppedFrames = lastOverflowEvents = lastOverflowDrops = lastExpiredDrops = 0;
            lastAwaitingKeyFrameDrops = lastBackpressureTimeouts = lastRenderDrops = 0;
        }
        inputDroppedFrames = saturatedAdd(inputDroppedFrames,
                nonnegativeDelta(metrics.inputDroppedFrames, lastInputDroppedFrames));
        overflowEvents = saturatedAdd(overflowEvents,
                nonnegativeDelta(metrics.overflowEvents, lastOverflowEvents));
        overflowDrops = saturatedAdd(overflowDrops,
                nonnegativeDelta(metrics.overflowDrops, lastOverflowDrops));
        expiredDrops = saturatedAdd(expiredDrops,
                nonnegativeDelta(metrics.expiredDrops, lastExpiredDrops));
        awaitingKeyFrameDrops = saturatedAdd(awaitingKeyFrameDrops,
                nonnegativeDelta(metrics.awaitingKeyFrameDrops, lastAwaitingKeyFrameDrops));
        backpressureTimeouts = saturatedAdd(backpressureTimeouts,
                nonnegativeDelta(metrics.backpressureTimeouts, lastBackpressureTimeouts));
        renderDrops = saturatedAdd(renderDrops,
                nonnegativeDelta(metrics.renderDrops, lastRenderDrops));
        lastInputDroppedFrames = metrics.inputDroppedFrames;
        lastOverflowEvents = metrics.overflowEvents;
        lastOverflowDrops = metrics.overflowDrops;
        lastExpiredDrops = metrics.expiredDrops;
        lastAwaitingKeyFrameDrops = metrics.awaitingKeyFrameDrops;
        lastBackpressureTimeouts = metrics.backpressureTimeouts;
        lastRenderDrops = metrics.renderDrops;
        queueDepth = Math.max(0, metrics.queueDepth);
        queueCapacity = Math.max(0, metrics.queueCapacity);
        queueHighWaterMark = Math.max(queueHighWaterMark, Math.max(0, metrics.queueHighWaterMark));
        queueAwaitingKeyFrame = metrics.awaitingKeyFrame;
    }

    synchronized void decoderStopped() {
        decoderActive = false;
        queueDepth = 0;
        queueCapacity = 0;
        queueAwaitingKeyFrame = false;
    }

    synchronized Report report(long nowMillis, boolean sessionAwaitingKeyFrame) {
        if (nowMillis < 0) throw new IllegalArgumentException("Report time must be nonnegative");
        // MediaCodec selection and the first queue snapshot are published by
        // different callbacks. Do not expose the brief selected-but-unsampled
        // window as queueCapacity=0; the strict host would correctly reject it.
        if (!decoderActive || decoderEpoch <= 0 || queueCapacity <= 0) return null;
        if (lastReportMillis >= 0 && nowMillis - lastReportMillis < REPORT_INTERVAL_MILLIS) return null;
        lastReportMillis = nowMillis;
        sequence++;
        return new Report(sequence, decoderEpoch, recoveryEpoch, receivedVideoFrames,
                receivedVideoBytes, submittedFrames, presentedFrames, queueDepth, queueCapacity,
                queueHighWaterMark, inputDroppedFrames, overflowEvents, overflowDrops, expiredDrops,
                awaitingKeyFrameDrops, backpressureTimeouts, renderDrops, decoderFallbacks,
                sessionAwaitingKeyFrame || queueAwaitingKeyFrame);
    }

    private static long nonnegativeDelta(long current, long previous) {
        // Counters never reset within one VideoDecoder source. A lower value is
        // an out-of-order snapshot from another thread, not a new contribution.
        return current >= previous ? current - previous : 0;
    }

    private static long saturatedAdd(long left, long right) {
        if (right <= 0) return left;
        return Long.MAX_VALUE - left < right ? Long.MAX_VALUE : left + right;
    }
}
