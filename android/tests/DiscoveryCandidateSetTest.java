package com.tablink.client;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class DiscoveryCandidateSetTest {
    private static final String NONCE = "00112233445566778899aabbccddeeff";

    public static void main(String[] args) {
        firstFalseOfferDoesNotSuppressRealHost();
        duplicateSourceCannotConsumeCapacity();
        boundedSelectionDoesNotDependOnArrivalOrder();
        invalidRoutesAreRejected();
        invalidConstructionIsRejected();
        System.out.println("PASS bounded multi-candidate discovery resists first-offer suppression");
    }

    private static void firstFalseOfferDoesNotSuppressRealHost() {
        DiscoveryCandidateSet candidates = new DiscoveryCandidateSet(NONCE, 8);
        check(candidates.add("192.168.50.201", 27184), "first untrusted route is collected");
        check(candidates.add("192.168.50.10", 27184), "later real route is also collected");
        List<String> routes = routes(candidates.snapshot());
        check(routes.contains("192.168.50.201:27184"), "first route remains only a candidate");
        check(routes.contains("192.168.50.10:27184"), "later trusted host can still be attempted");
    }

    private static void duplicateSourceCannotConsumeCapacity() {
        DiscoveryCandidateSet candidates = new DiscoveryCandidateSet(NONCE, 3);
        check(candidates.add("10.0.0.10", 27184), "first route from a source is accepted");
        check(!candidates.add("10.0.0.10", 27186), "same source cannot advertise another port");
        check(!candidates.add("10.0.0.10", 27184), "exact duplicate is ignored");
        candidates.add("10.0.0.11", 27184);
        candidates.add("10.0.0.12", 27184);
        check(candidates.snapshot().size() == 3, "duplicates do not consume the candidate bound");
    }

    private static void boundedSelectionDoesNotDependOnArrivalOrder() {
        ArrayList<String> hosts = new ArrayList<>();
        for (int index = 1; index <= 24; index++) hosts.add("172.20.4." + index);
        DiscoveryCandidateSet forward = new DiscoveryCandidateSet(NONCE, 8);
        for (String host : hosts) forward.add(host, 27184);
        DiscoveryCandidateSet reverse = new DiscoveryCandidateSet(NONCE, 8);
        Collections.reverse(hosts);
        for (String host : hosts) reverse.add(host, 27184);
        List<String> selected = routes(forward.snapshot());
        check(selected.size() == 8, "candidate memory remains bounded");
        check(selected.equals(routes(reverse.snapshot())),
                "a response rush cannot win solely by arriving first");
    }

    private static void invalidRoutesAreRejected() {
        DiscoveryCandidateSet candidates = new DiscoveryCandidateSet(NONCE, 8);
        check(!candidates.add("127.0.0.1", 27184), "loopback is rejected");
        check(!candidates.add("224.0.0.1", 27184), "multicast is rejected");
        check(!candidates.add("192.168.1.2", 27185), "browser port is rejected");
        check(!candidates.add("192.168.1.2", 27193), "discovery port is rejected");
        check(candidates.snapshot().isEmpty(), "invalid routes leave no candidates");
    }

    private static void invalidConstructionIsRejected() {
        expectReject(() -> new DiscoveryCandidateSet(null, 8), "missing nonce");
        expectReject(() -> new DiscoveryCandidateSet(NONCE.toUpperCase(java.util.Locale.ROOT), 8),
                "noncanonical nonce");
        expectReject(() -> new DiscoveryCandidateSet(NONCE, 0), "zero bound");
        expectReject(() -> new DiscoveryCandidateSet(NONCE, 33), "excessive bound");
    }

    private static List<String> routes(List<DiscoveryCandidateSet.Candidate> candidates) {
        ArrayList<String> result = new ArrayList<>();
        for (DiscoveryCandidateSet.Candidate candidate : candidates)
            result.add(candidate.host + ":" + candidate.port);
        return result;
    }

    private static void expectReject(Runnable action, String message) {
        try {
            action.run();
            throw new AssertionError("Expected rejection: " + message);
        } catch (IllegalArgumentException expected) { }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
