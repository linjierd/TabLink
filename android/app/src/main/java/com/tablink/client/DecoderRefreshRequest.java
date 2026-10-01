package com.tablink.client;

import java.io.IOException;
import java.nio.ByteBuffer;

/** Fixed-width recovery request. Codec names and exception text never cross the wire. */
final class DecoderRefreshRequest {
    static final int PAYLOAD_BYTES = Long.BYTES;

    static byte[] encode(long generation) {
        if (generation <= 0) throw new IllegalArgumentException("Refresh generation must be positive");
        return ByteBuffer.allocate(PAYLOAD_BYTES).putLong(generation).array();
    }

    static long decode(byte[] payload) throws IOException {
        if (payload == null || payload.length != PAYLOAD_BYTES)
            throw new IOException("Invalid decoder refresh request");
        long generation = ByteBuffer.wrap(payload).getLong();
        if (generation <= 0) throw new IOException("Invalid decoder refresh generation");
        return generation;
    }

    private DecoderRefreshRequest() { }
}
