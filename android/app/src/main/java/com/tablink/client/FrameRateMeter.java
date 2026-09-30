package com.tablink.client;

/** Uses actual render callback timestamps; never substitutes target or panel Hz. */
public final class FrameRateMeter {
    private long startNanos = -1;
    private long lastNanos = -1;
    private int frames;
    private double fps;

    public synchronized double presented(long renderNanos) {
        if (renderNanos <= lastNanos) return fps;
        lastNanos = renderNanos;
        if (startNanos < 0) { startNanos = renderNanos; frames = 0; return fps; }
        frames++;
        long elapsed = renderNanos - startNanos;
        if (elapsed >= 1_000_000_000L) {
            fps = frames * 1_000_000_000.0 / elapsed;
            frames = 0;
            startNanos = renderNanos;
        }
        return fps;
    }
}
