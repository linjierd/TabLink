package com.tablink.client;

import java.util.regex.Pattern;

/**
 * Process-local, one-shot USB session configuration written through the
 * DUMP-protected ADB provider. Bearer tokens are never persisted to disk.
 */
final class AdbSessionConfiguration {
    static final long MAX_AGE_MILLIS = 30_000L;
    private static final long MAX_AGE_NANOS = MAX_AGE_MILLIS * 1_000_000L;
    private static final Pattern ACTIVATION = Pattern.compile("[0-9a-f]{32}");
    private static final Pattern TOKEN = Pattern.compile("[A-Za-z0-9_-]{16,256}");
    private static Pending pending;

    private AdbSessionConfiguration() { }

    static final class Pending {
        final String activation;
        final String token;
        final int port;
        final long publishedNanos;

        private Pending(String activation, String token, int port, long publishedNanos) {
            this.activation = activation;
            this.token = token;
            this.port = port;
            this.publishedNanos = publishedNanos;
        }
    }

    static synchronized void publish(String activation, String token, int port) {
        publish(activation, token, port, System.nanoTime());
    }

    static synchronized Pending consume(String activation) {
        return consume(activation, System.nanoTime());
    }

    static synchronized void publish(String activation, String token, int port, long nowNanos) {
        if (activation == null || !ACTIVATION.matcher(activation).matches())
            throw new IllegalArgumentException("ADB activation marker is invalid");
        if (token == null || !TOKEN.matcher(token).matches())
            throw new IllegalArgumentException("ADB session token is invalid");
        if (port < 49152 || port > 65535)
            throw new IllegalArgumentException("ADB session port is invalid");
        if (nowNanos < 0) throw new IllegalArgumentException("ADB publication time is invalid");
        pending = new Pending(activation, token, port, nowNanos);
    }

    static synchronized Pending consume(String activation, long nowNanos) {
        Pending candidate = pending;
        if (candidate == null || nowNanos < 0) return null;
        long age = nowNanos - candidate.publishedNanos;
        if (age < 0 || age > MAX_AGE_NANOS) {
            pending = null;
            return null;
        }
        if (activation == null || !candidate.activation.equals(activation)) return null;
        pending = null;
        return candidate;
    }

    static synchronized void expire(String activation) {
        if (pending != null && pending.activation.equals(activation)) pending = null;
    }

    static synchronized void clearForTests() { pending = null; }
}
