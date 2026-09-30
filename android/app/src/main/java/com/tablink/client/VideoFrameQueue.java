package com.tablink.client;

import java.util.ArrayDeque;

/** Bounded compressed input. Only the network producer may wait; codec polling never waits. */
public final class VideoFrameQueue {
    private final ArrayDeque<VideoAccessUnit> frames = new ArrayDeque<>();
    private final int capacity;
    private boolean needsKeyFrame = true;
    private long lastPts = -1;
    private long dropped;
    private boolean closed;
    private long generation;
    private int highWaterMark;
    private long overflowEvents, overflowDrops, awaitingKeyFrameDrops, expiredDrops, reorderedDrops;
    private long backpressureWaits, backpressureTimeouts, backpressureWaitNanos, longestBackpressureWaitNanos;

    public VideoFrameQueue(int capacity) {
        if (capacity < 1) throw new IllegalArgumentException("Queue capacity must be positive");
        this.capacity = capacity;
    }

    public synchronized boolean offer(VideoAccessUnit frame) {
        try { return offerWithBackpressure(frame, 0); }
        catch (InterruptedException impossible) { Thread.currentThread().interrupt(); return false; }
    }

    /** Waits at most the caller's budget (capped at 25 ms), releasing this monitor while waiting. */
    public synchronized boolean offerWithBackpressure(VideoAccessUnit frame, long requestedWaitNanos)
            throws InterruptedException {
        if (closed) return false;
        if (frame.ptsUs <= lastPts) { dropped++; reorderedDrops++; return false; }
        long budget = Math.max(0, Math.min(25_000_000L, requestedWaitNanos));
        long epoch = generation;
        if (frames.size() >= capacity && budget > 0) {
            long began = System.nanoTime();
            backpressureWaits++;
            try {
                long remaining = budget;
                while (frames.size() >= capacity && !closed && generation == epoch
                        && frame.ptsUs > lastPts && remaining > 0) {
                    wait(remaining / 1_000_000L, (int) (remaining % 1_000_000L));
                    remaining = budget - (System.nanoTime() - began);
                }
            } finally {
                long elapsed = Math.max(0, System.nanoTime() - began);
                backpressureWaitNanos += elapsed;
                longestBackpressureWaitNanos = Math.max(longestBackpressureWaitNanos, elapsed);
            }
            if (closed || generation != epoch) return false;
            // Another producer may have committed while wait released the monitor.
            if (frame.ptsUs <= lastPts) { dropped++; reorderedDrops++; return false; }
            if (frames.size() >= capacity) backpressureTimeouts++;
        }
        // Do not advance ordering state while a producer is waiting or cancelled.
        lastPts = frame.ptsUs;
        if (frames.size() >= capacity) {
            overflowEvents++;
            overflowDrops += frames.size();
            dropped += frames.size();
            frames.clear();
            needsKeyFrame = true;
        }
        if (needsKeyFrame && !frame.keyFrame) {
            dropped++; awaitingKeyFrameDrops++; notifyAll(); return false;
        }
        if (frame.keyFrame) needsKeyFrame = false;
        frames.add(frame);
        highWaterMark = Math.max(highWaterMark, frames.size());
        notifyAll();
        return true;
    }

    public synchronized VideoAccessUnit poll(long nowNanos) {
        while (!frames.isEmpty()) {
            VideoAccessUnit frame = frames.remove();
            notifyAll();
            if (nowNanos - frame.receivedNanos > 150_000_000L) {
                dropped++; expiredDrops++;
                needsKeyFrame = true;
                continue;
            }
            if (needsKeyFrame && !frame.keyFrame) { dropped++; awaitingKeyFrameDrops++; continue; }
            if (frame.keyFrame) needsKeyFrame = false;
            return frame;
        }
        return null;
    }

    public synchronized void clear() {
        frames.clear(); needsKeyFrame = true; lastPts = -1; generation++; notifyAll();
    }
    public synchronized void close() { closed = true; clear(); }
    public synchronized long droppedFrames() { return dropped; }
    public synchronized int size() { return frames.size(); }

    public synchronized Snapshot snapshot() {
        return new Snapshot(frames.size(), highWaterMark, overflowEvents, overflowDrops, awaitingKeyFrameDrops,
                expiredDrops, reorderedDrops, backpressureWaits, backpressureTimeouts,
                backpressureWaitNanos, longestBackpressureWaitNanos);
    }

    public static final class Snapshot {
        public final int size, highWaterMark;
        public final long overflowEvents, overflowDrops, awaitingKeyFrameDrops, expiredDrops, reorderedDrops;
        public final long backpressureWaits, backpressureTimeouts, backpressureWaitNanos, longestBackpressureWaitNanos;
        Snapshot(int size, int highWaterMark, long overflowEvents, long overflowDrops, long awaitingKeyFrameDrops,
                long expiredDrops, long reorderedDrops, long backpressureWaits, long backpressureTimeouts,
                long backpressureWaitNanos, long longestBackpressureWaitNanos) {
            this.size = size; this.highWaterMark = highWaterMark; this.overflowEvents = overflowEvents;
            this.overflowDrops = overflowDrops; this.awaitingKeyFrameDrops = awaitingKeyFrameDrops;
            this.expiredDrops = expiredDrops; this.reorderedDrops = reorderedDrops;
            this.backpressureWaits = backpressureWaits; this.backpressureTimeouts = backpressureTimeouts;
            this.backpressureWaitNanos = backpressureWaitNanos;
            this.longestBackpressureWaitNanos = longestBackpressureWaitNanos;
        }
    }
}
