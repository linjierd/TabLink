package com.tablink.client;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/** An Annex-B access unit; each packet has one nonnegative presentation timestamp. */
public final class VideoAccessUnit {
    public final long ptsUs;
    public final byte[] bytes;
    public final boolean keyFrame;
    public final long receivedNanos;

    private VideoAccessUnit(long ptsUs, byte[] bytes, boolean keyFrame, long receivedNanos) {
        this.ptsUs = ptsUs;
        this.bytes = bytes;
        this.keyFrame = keyFrame;
        this.receivedNanos = receivedNanos;
    }

    public static VideoAccessUnit parse(byte[] payload, long receivedNanos) throws IOException {
        if (payload.length < 13 || payload.length > WireProtocol.MAX_PAYLOAD)
            throw new IOException("H.264 packet size is invalid");
        long pts = ByteBuffer.wrap(payload, 0, 8).order(ByteOrder.BIG_ENDIAN).getLong();
        if (pts < 0) throw new IOException("H.264 timestamp is invalid");
        byte[] encoded = new byte[payload.length - 8];
        System.arraycopy(payload, 8, encoded, 0, encoded.length);
        if (startCodeLength(encoded, 0) == 0) throw new IOException("H.264 frame must use Annex-B start codes");
        if (!containsNal(encoded, 1) && !containsNal(encoded, 5))
            throw new IOException("H.264 access unit has no picture");
        return new VideoAccessUnit(pts, encoded, containsNal(encoded, 5), receivedNanos);
    }

    public static boolean containsNal(byte[] encoded, int type) {
        for (int i = 0; i < encoded.length - 3; i++) {
            int start = startCodeLength(encoded, i);
            if (start != 0 && i + start < encoded.length && (encoded[i + start] & 0x80) == 0
                    && (encoded[i + start] & 31) == type) return true;
        }
        return false;
    }

    private static int startCodeLength(byte[] data, int offset) {
        if (offset + 3 < data.length && data[offset] == 0 && data[offset + 1] == 0) {
            if (data[offset + 2] == 1) return 3;
            if (data[offset + 2] == 0 && data[offset + 3] == 1) return 4;
        }
        return 0;
    }
}
