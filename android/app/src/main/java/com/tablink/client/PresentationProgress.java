package com.tablink.client;

/** Counts newly presented images, excluding Surface redraws and stalled images. */
public final class PresentationProgress {
    public static final class Report {
        public final long sequence;
        public final int width;
        public final int height;
        Report(long sequence, int width, int height) {
            this.sequence = sequence;
            this.width = width;
            this.height = height;
        }
    }

    private long latestFrame;
    private long sequence;
    private long lastReportMillis = -1;

    public synchronized Report presented(long frameId, int width, int height, long nowMillis) {
        if (frameId <= latestFrame || width <= 0 || height <= 0) return null;
        latestFrame = frameId;
        sequence++;
        if (lastReportMillis >= 0 && nowMillis - lastReportMillis < 1000) return null;
        lastReportMillis = nowMillis;
        return new Report(sequence, width, height);
    }
}
