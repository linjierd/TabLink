package com.tablink.client;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Keeps a bounded, order-independent set of untrusted discovery route hints.
 * Certificate pinning and trusted-device authentication remain the authority.
 */
final class DiscoveryCandidateSet {
    static final int DEFAULT_LIMIT = 8;

    private static final Comparator<Candidate> SCORE_ORDER = (left, right) -> {
        int score = compareUnsigned(left.score, right.score);
        if (score != 0) return score;
        score = left.host.compareTo(right.host);
        return score != 0 ? score : Integer.compare(left.port, right.port);
    };

    private final byte[] seed;
    private final int limit;
    private final Map<String, Candidate> byHost = new HashMap<>();

    DiscoveryCandidateSet(String nonce, int limit) {
        if (nonce == null || !nonce.matches("[0-9a-f]{32}"))
            throw new IllegalArgumentException("发现随机数无效");
        if (limit < 1 || limit > 32) throw new IllegalArgumentException("发现候选上限无效");
        seed = nonce.getBytes(StandardCharsets.US_ASCII);
        this.limit = limit;
    }

    boolean add(String host, int port) {
        if (!PairingLink.isUnicastIpv4(host) || !PairingLink.isNativePort(port)) return false;
        // One source address gets one route. This prevents a single responder from
        // consuming the entire bound by advertising every native port.
        if (byHost.containsKey(host)) return false;
        Candidate next = new Candidate(host, port, score(host));
        if (byHost.size() < limit) {
            byHost.put(host, next);
            return true;
        }
        Candidate worst = Collections.max(byHost.values(), SCORE_ORDER);
        if (SCORE_ORDER.compare(next, worst) >= 0) return false;
        byHost.remove(worst.host);
        byHost.put(host, next);
        return true;
    }

    List<Candidate> snapshot() {
        ArrayList<Candidate> result = new ArrayList<>(byHost.values());
        Collections.sort(result, SCORE_ORDER);
        return Collections.unmodifiableList(result);
    }

    private byte[] score(String host) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            digest.update(seed);
            digest.update((byte) 0);
            digest.update(host.getBytes(StandardCharsets.US_ASCII));
            return digest.digest();
        } catch (GeneralSecurityException impossible) {
            throw new IllegalStateException("SHA-256 不可用", impossible);
        }
    }

    private static int compareUnsigned(byte[] left, byte[] right) {
        for (int index = 0; index < left.length; index++) {
            int difference = (left[index] & 0xff) - (right[index] & 0xff);
            if (difference != 0) return difference;
        }
        return 0;
    }

    static final class Candidate {
        final String host;
        final int port;
        private final byte[] score;

        Candidate(String host, int port, byte[] score) {
            this.host = host;
            this.port = port;
            this.score = score;
        }
    }
}
