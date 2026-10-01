package com.tablink.client;

import java.util.Arrays;
import java.util.List;

/** Exercises decoder ordering and failure demotion without Android or a physical codec. */
public final class DecoderCandidateSelectorTest {
    private static int assertions;

    public static void main(String[] args) {
        hardwareAndSoftwareFallback();
        performanceAndLatency();
        dimensionsAndFrameRate();
        failureHistory();
        stableOrdering();
        System.out.println("DecoderCandidateSelector: " + assertions + " assertions passed");
    }

    private static void hardwareAndSoftwareFallback() {
        DecoderCandidateSelector.Candidate software = candidate("c2.android.avc.decoder",
                false, true, true, true, false, false, true, 0, 0);
        DecoderCandidateSelector.Candidate hardware = candidate("c2.vendor.avc.decoder",
                true, false, true, true, false, false, false, 0, 1);
        List<DecoderCandidateSelector.Candidate> ranked = DecoderCandidateSelector.rank(
                Arrays.asList(software, hardware));
        check(ranked.size() == 2, "software decoder remains eligible as fallback");
        check(ranked.get(0) == hardware && ranked.get(1) == software,
                "hardware decoder ranks before software fallback");
    }

    private static void performanceAndLatency() {
        DecoderCandidateSelector.Candidate unsupportedPoint = candidate("hardware-no-point",
                true, false, true, true, true, false, true, 0, 0);
        DecoderCandidateSelector.Candidate coveredPoint = candidate("hardware-covered-point",
                true, false, true, true, true, true, false, 0, 1);
        DecoderCandidateSelector.Candidate unknownPoint = candidate("hardware-unknown-point",
                true, false, true, true, false, false, false, 0, 2);
        List<DecoderCandidateSelector.Candidate> ranked = DecoderCandidateSelector.rank(
                Arrays.asList(unsupportedPoint, unknownPoint, coveredPoint));
        check(ranked.get(0) == coveredPoint, "covering performance point outranks unknown capability");
        check(ranked.get(2) == unsupportedPoint,
                "declared performance points that miss the stream are penalized despite low latency");
        DecoderCandidateSelector.Candidate regular = candidate("regular",
                true, false, true, true, false, false, false, 0, 0);
        DecoderCandidateSelector.Candidate lowLatency = candidate("low-latency",
                true, false, true, true, false, false, true, 0, 1);
        check(DecoderCandidateSelector.rank(Arrays.asList(regular, lowLatency)).get(0) == lowLatency,
                "low-latency capability breaks an otherwise equal tie");
    }

    private static void dimensionsAndFrameRate() {
        DecoderCandidateSelector.Candidate wrongSize = candidate("wrong-size",
                true, false, false, true, true, true, true, 0, 0);
        DecoderCandidateSelector.Candidate supportedRate = candidate("supported-rate",
                true, false, true, true, false, false, false, 0, 1);
        DecoderCandidateSelector.Candidate unsupportedRate = candidate("unsupported-rate",
                true, false, true, false, false, false, true, 0, 2);
        List<DecoderCandidateSelector.Candidate> ranked = DecoderCandidateSelector.rank(
                Arrays.asList(wrongSize, unsupportedRate, supportedRate));
        check(ranked.size() == 2 && !ranked.contains(wrongSize),
                "unsupported dimensions are rejected before codec creation");
        check(ranked.get(0) == supportedRate,
                "declared frame-rate support outweighs low-latency preference");
    }

    private static void failureHistory() {
        DecoderCandidateSelector.FailureHistory history = new DecoderCandidateSelector.FailureHistory();
        check(history.count("vendor") == 0, "unseen decoder has no failure penalty");
        check(history.record("vendor") == 1 && history.count("vendor") == 1,
                "runtime failure is retained for the process lifetime");
        DecoderCandidateSelector.Candidate failedHardware = candidate("vendor",
                true, false, true, true, true, true, true, history.count("vendor"), 0);
        DecoderCandidateSelector.Candidate software = candidate("software",
                false, true, true, true, false, false, false, 0, 1);
        check(DecoderCandidateSelector.rank(Arrays.asList(failedHardware, software)).get(0) == software,
                "known failure demotes a hardware codec below an unfailed software fallback");
        history.clear();
        check(history.count("vendor") == 0, "testable failure history may be cleared without persistence");
    }

    private static void stableOrdering() {
        DecoderCandidateSelector.Candidate first = candidate("z-decoder",
                true, false, true, true, false, false, false, 0, 0);
        DecoderCandidateSelector.Candidate second = candidate("a-decoder",
                true, false, true, true, false, false, false, 0, 1);
        List<DecoderCandidateSelector.Candidate> ranked = DecoderCandidateSelector.rank(Arrays.asList(second, first));
        check(ranked.get(0) == first && ranked.get(1) == second,
                "equal scores retain Android discovery order");
        try {
            ranked.add(first);
            throw new AssertionError("rank result was mutable");
        } catch (UnsupportedOperationException expected) { assertions++; }
    }

    private static DecoderCandidateSelector.Candidate candidate(String name,
            boolean hardware, boolean software, boolean size, boolean rate,
            boolean pointsDeclared, boolean pointCovers, boolean lowLatency,
            int failures, int order) {
        return new DecoderCandidateSelector.Candidate(name, hardware, software, size, rate,
                pointsDeclared, pointCovers, lowLatency, failures, order);
    }

    private static void check(boolean condition, String message) {
        assertions++;
        if (!condition) throw new AssertionError(message);
    }
}
