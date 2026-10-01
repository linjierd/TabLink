package com.tablink.client;

/**
 * Counts access units accepted by MediaCodec for one TCP connection.
 * Decoder reconfiguration keeps the same instance; reconnecting creates a new one.
 */
public final class SubmissionProgress {
    private static final long REPORT_INTERVAL_MILLIS = 1000;

    public static final class Report {
        public final long frames;
        public final long ptsUs;
        public final int width;
        public final int height;
        public final double fps;
        public final String decoder;

        Report(long frames, long ptsUs, int width, int height, double fps, String decoder) {
            this.frames = frames;
            this.ptsUs = ptsUs;
            this.width = width;
            this.height = height;
            this.fps = fps;
            this.decoder = decoder;
        }
    }

    private long frames;
    private boolean hasSubmissionTime;
    private boolean hasRateStart;
    private long lastSubmissionNanos;
    private long rateStartNanos;
    private long rateFrames;
    private long lastReportMillis = -1;
    private double fps;

    /** Returns a cumulative report immediately, then at most once per second. */
    public synchronized Report submitted(long ptsUs, int width, int height, long submittedNanos,
            long nowMillis, String decoder) {
        if (ptsUs < 0 || width < 1 || height < 1 || width > 8192 || height > 8192
                || nowMillis < 0 || (hasSubmissionTime && submittedNanos <= lastSubmissionNanos)) return null;

        lastSubmissionNanos = submittedNanos;
        hasSubmissionTime = true;
        frames++;
        if (!hasRateStart) {
            rateStartNanos = submittedNanos;
            hasRateStart = true;
            rateFrames = 0;
        } else {
            rateFrames++;
            long elapsed = submittedNanos - rateStartNanos;
            if (elapsed >= 1_000_000_000L) {
                fps = Math.min(300, rateFrames * 1_000_000_000.0 / elapsed);
                rateStartNanos = submittedNanos;
                rateFrames = 0;
            }
        }

        if (lastReportMillis >= 0 && nowMillis - lastReportMillis < REPORT_INTERVAL_MILLIS) return null;
        lastReportMillis = nowMillis;
        String safeDecoder = decoder == null ? "" : decoder;
        if (safeDecoder.length() > 160) safeDecoder = safeDecoder.substring(0, 160);
        return new Report(frames, ptsUs, width, height, fps, safeDecoder);
    }
}
