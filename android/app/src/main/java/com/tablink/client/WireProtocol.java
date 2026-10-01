package com.tablink.client;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;

/** Dependency-free framing, shared by the Android transport and the JVM smoke tests. */
public final class WireProtocol {
    public static final int MAX_PAYLOAD = 8 * 1024 * 1024;
    public static final int FRAME = 0x01;
    public static final int STATUS = 0x02;
    public static final int ERROR = 0x03;
    public static final int HELLO = 0x10;
    public static final int INPUT = 0x11;
    public static final int PRESENTED = 0x12;
    public static final int DISPLAY_PROFILE = 0x13;
    public static final int RENDER_SUBMITTED = 0x14;
    public static final int VIDEO_CONFIG = 0x20;
    public static final int VIDEO_FRAME = 0x21;

    private WireProtocol() {}

    public static final class Packet {
        public final int type;
        public final byte[] payload;
        public Packet(int type, byte[] payload) {
            this.type = type;
            this.payload = payload;
        }
    }

    public static Packet read(DataInputStream input) throws IOException {
        int type = input.readUnsignedByte();
        long length = ((long) input.readInt()) & 0xffffffffL;
        if (length > MAX_PAYLOAD) {
            throw new IOException("数据包超过 8 MiB 限制");
        }
        byte[] payload = new byte[(int) length];
        input.readFully(payload);
        return new Packet(type, payload);
    }

    public static void write(DataOutputStream output, int type, byte[] payload)
            throws IOException {
        if (type < 0 || type > 255 || payload.length > MAX_PAYLOAD) {
            throw new IOException("无效的数据包");
        }
        output.writeByte(type);
        output.writeInt(payload.length);
        output.write(payload);
        output.flush();
    }
}
