package com.tablink.client;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Pure-Java scoring for AVC decoder capabilities reported by Android. */
final class DecoderCandidateSelector {
    private static final long HARDWARE_SCORE = 100_000;
    private static final long SIZE_SCORE = 30_000;
    private static final long PERFORMANCE_POINT_SCORE = 20_000;
    private static final long FRAME_RATE_SCORE = 10_000;
    private static final long LOW_LATENCY_SCORE = 3_000;
    private static final long SOFTWARE_PENALTY = 80_000;
    private static final long FAILURE_PENALTY = 250_000;

    static final class Candidate {
        final String name;
        final boolean hardwareAccelerated;
        final boolean softwareOnly;
        final boolean sizeSupported;
        final boolean frameRateSupported;
        final boolean performancePointsDeclared;
        final boolean performancePointCovers;
        final boolean lowLatency;
        final int knownFailures;
        final int discoveryOrder;

        Candidate(String name, boolean hardwareAccelerated, boolean softwareOnly,
                boolean sizeSupported, boolean frameRateSupported,
                boolean performancePointsDeclared, boolean performancePointCovers,
                boolean lowLatency, int knownFailures, int discoveryOrder) {
            if (name == null || name.isEmpty()) throw new IllegalArgumentException("Decoder name is required");
            this.name = name;
            this.hardwareAccelerated = hardwareAccelerated;
            this.softwareOnly = softwareOnly;
            this.sizeSupported = sizeSupported;
            this.frameRateSupported = frameRateSupported;
            this.performancePointsDeclared = performancePointsDeclared;
            this.performancePointCovers = performancePointCovers;
            this.lowLatency = lowLatency;
            this.knownFailures = Math.max(0, knownFailures);
            this.discoveryOrder = Math.max(0, discoveryOrder);
        }

        long score() {
            long score = sizeSupported ? SIZE_SCORE : -SIZE_SCORE;
            if (hardwareAccelerated && !softwareOnly) score += HARDWARE_SCORE;
            if (softwareOnly) score -= SOFTWARE_PENALTY;
            score += frameRateSupported ? FRAME_RATE_SCORE : -FRAME_RATE_SCORE;
            if (performancePointsDeclared)
                score += performancePointCovers ? PERFORMANCE_POINT_SCORE : -PERFORMANCE_POINT_SCORE;
            if (lowLatency) score += LOW_LATENCY_SCORE;
            score -= FAILURE_PENALTY * Math.min(100, knownFailures);
            return score;
        }
    }

    /** Unsupported dimensions are ineligible; every eligible software codec remains a last-resort fallback. */
    static List<Candidate> rank(List<Candidate> discovered) {
        ArrayList<Candidate> ranked = new ArrayList<>();
        if (discovered != null) {
            for (Candidate candidate : discovered)
                if (candidate != null && candidate.sizeSupported) ranked.add(candidate);
        }
        Collections.sort(ranked, new Comparator<Candidate>() {
            @Override public int compare(Candidate left, Candidate right) {
                int byScore = Long.compare(right.score(), left.score());
                if (byScore != 0) return byScore;
                int byOrder = Integer.compare(left.discoveryOrder, right.discoveryOrder);
                return byOrder != 0 ? byOrder : left.name.compareTo(right.name);
            }
        });
        return Collections.unmodifiableList(ranked);
    }

    /** Process-lifetime evidence only: transient codec failures must not become a persistent device blacklist. */
    static final class FailureHistory {
        private final Map<String, Integer> failures = new HashMap<>();

        synchronized int count(String name) {
            Integer count = failures.get(name);
            return count == null ? 0 : count;
        }

        synchronized int record(String name) {
            int count = Math.min(100, count(name) + 1);
            failures.put(name, count);
            return count;
        }

        synchronized void clear() { failures.clear(); }
    }

    private DecoderCandidateSelector() { }
}
