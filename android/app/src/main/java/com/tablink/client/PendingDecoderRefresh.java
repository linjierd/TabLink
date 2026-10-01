package com.tablink.client;

/** Keeps the newest unsent 0x15 generation across writer eligibility races. */
final class PendingDecoderRefresh {
    private long generation;
    private long cancelledThrough;

    synchronized long get() { return generation; }

    synchronized void set(long value) {
        if (value <= 0) throw new IllegalArgumentException("Refresh generation must be positive");
        if (value > cancelledThrough) generation = Math.max(generation, value);
    }

    /** Claims exactly this generation before its packet becomes visible to the writer. */
    synchronized boolean claim(long expected) {
        if (expected <= cancelledThrough || generation != expected) return false;
        generation = 0;
        return true;
    }

    /** Cancels this recovery and blocks a writer holding an already-claimed stale packet. */
    synchronized void cancelThrough(long value) {
        if (value < 0) throw new IllegalArgumentException("Cancelled generation must be nonnegative");
        cancelledThrough = Math.max(cancelledThrough, value);
        if (generation <= cancelledThrough) generation = 0;
    }

    /** Lets the writer reject a packet that was claimed before its recovery completed. */
    synchronized boolean isCancelled(long value) {
        if (value <= 0) throw new IllegalArgumentException("Refresh generation must be positive");
        return value <= cancelledThrough;
    }

    /** Starts a new TCP connection after its preceding writer has terminated. */
    synchronized void reset() { generation = cancelledThrough = 0; }

    synchronized void defer(long value) {
        if (value <= 0) return;
        if (value <= cancelledThrough || generation >= value) return;
        generation = value;
    }
}

/**
 * Owns the logical decoder-recovery epoch and the wire-generation cutoff as
 * one synchronized state. A presentation callback may only complete the exact
 * recovery it observed; the returned cutoff never includes a later recovery.
 */
final class DecoderRecoveryState {
    static final class Snapshot {
        final long epoch;
        final String kind;

        private Snapshot(long epoch, String kind) {
            this.epoch = epoch;
            this.kind = kind;
        }
    }

    static final class Completion {
        final long epoch;
        final long wireGenerationCutoff;

        private Completion(long epoch, long wireGenerationCutoff) {
            this.epoch = epoch;
            this.wireGenerationCutoff = wireGenerationCutoff;
        }
    }

    private long activeEpoch;
    private long wireGeneration;
    private boolean waiting;
    private String kind = "";

    synchronized boolean start(long epoch, String recoveryKind) {
        if (epoch <= 0) throw new IllegalArgumentException("Recovery epoch must be positive");
        if (recoveryKind == null || recoveryKind.isEmpty())
            throw new IllegalArgumentException("Recovery kind must not be empty");
        if (epoch <= activeEpoch) return false;
        activeEpoch = epoch;
        kind = recoveryKind;
        waiting = true;
        return true;
    }

    synchronized Snapshot snapshot() {
        return waiting ? new Snapshot(activeEpoch, kind) : null;
    }

    synchronized boolean isWaiting() { return waiting; }

    synchronized long issue(long epoch) {
        if (!waiting || epoch != activeEpoch) return 0;
        if (wireGeneration == Long.MAX_VALUE)
            throw new IllegalStateException("Decoder refresh generation exhausted");
        return ++wireGeneration;
    }

    synchronized Completion complete(Snapshot expected) {
        if (expected == null || !waiting || expected.epoch != activeEpoch) return null;
        waiting = false;
        kind = "";
        return new Completion(expected.epoch, wireGeneration);
    }

    synchronized Completion cancel() {
        waiting = false;
        kind = "";
        return new Completion(activeEpoch, wireGeneration);
    }

    synchronized void reset() {
        activeEpoch = 0;
        wireGeneration = 0;
        waiting = false;
        kind = "";
    }
}
