package com.tablink.client;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;

/** Boundary tests for protocol-v1 packet 0x20, runnable on a plain JDK. */
public final class AvcConfigurationTest {
    private static final byte[] SPS = {0, 0, 0, 1, 0x67, 0x42, 0, 0x1f};
    private static final byte[] PPS = {0, 0, 0, 1, 0x68, (byte) 0xce, 6, (byte) 0xe2};
    private static int assertions;

    public static void main(String[] args) throws Exception {
        AvcConfiguration normal = parse("1920", "1200", "90", SPS, PPS);
        check(normal.width == 1920 && normal.height == 1200 && normal.fps == 90,
                "normal protocol-v1 AVC configuration");
        check(Arrays.equals(normal.sps, SPS) && Arrays.equals(normal.pps, PPS),
                "four-byte Annex-B parameter sets are preserved");

        AvcConfiguration repeated = AvcConfiguration.parse(configuration(
                "1920", "1200", "90", "video/avc", encode(SPS), encode(PPS)));
        check(repeated.width == normal.width && repeated.height == normal.height
                        && repeated.fps == normal.fps
                        && Arrays.equals(repeated.sps, normal.sps)
                        && Arrays.equals(repeated.pps, normal.pps),
                "the same 0x20 configuration can be parsed again for decoder replacement");

        AvcConfiguration minimum = parse("2", "2", "1", SPS, PPS);
        check(minimum.width == 2 && minimum.height == 2 && minimum.fps == 1,
                "minimum even dimensions and frame rate are accepted");
        check(parse("8192", "2", "240", SPS, PPS).width == 8192,
                "maximum width is accepted when the area is bounded");
        check(parse("2", "8192", "240", SPS, PPS).height == 8192,
                "maximum height is accepted when the area is bounded");
        check(parse("4000", "4000", "240", SPS, PPS).width == 4000,
                "exactly sixteen million pixels and 240 fps are accepted");

        byte[] threeByteSps = {0, 0, 1, 0x67};
        byte[] threeBytePps = {0, 0, 1, 0x68};
        AvcConfiguration normalized = parse("1280", "720", "59.94", threeByteSps, threeBytePps);
        check(Arrays.equals(normalized.sps, new byte[] {0, 0, 0, 1, 0x67})
                        && Arrays.equals(normalized.pps, new byte[] {0, 0, 0, 1, 0x68}),
                "three-byte Annex-B start codes are normalized to four bytes");

        String wrappedSps = encode(SPS).substring(0, 4) + "\\n" + encode(SPS).substring(4);
        AvcConfiguration whitespace = AvcConfiguration.parse(configuration(
                "1280", "720", "60", "video/avc", wrappedSps, encode(PPS)));
        check(Arrays.equals(whitespace.sps, SPS), "Android-compatible Base64 whitespace is accepted");

        byte[] maximumSps = parameterSet(65536, 7);
        check(parse("1280", "720", "60", maximumSps, PPS).sps.length == 65536,
                "a 64 KiB parameter set is accepted");
        byte[] maximumPps = parameterSet(65536, 8);
        check(parse("1280", "720", "60", SPS, maximumPps).pps.length == 65536,
                "the PPS shares the same 64 KiB boundary");

        reject(configuration("1", "2", "60", "video/avc", encode(SPS), encode(PPS)),
                "width below two");
        reject(configuration("2", "1", "60", "video/avc", encode(SPS), encode(PPS)),
                "height below two");
        reject(configuration("3", "2", "60", "video/avc", encode(SPS), encode(PPS)),
                "odd width");
        reject(configuration("2", "3", "60", "video/avc", encode(SPS), encode(PPS)),
                "odd height");
        reject(configuration("8194", "2", "60", "video/avc", encode(SPS), encode(PPS)),
                "width above 8192");
        reject(configuration("2", "8194", "60", "video/avc", encode(SPS), encode(PPS)),
                "height above 8192");
        reject(configuration("4000", "4002", "60", "video/avc", encode(SPS), encode(PPS)),
                "area above sixteen million pixels");
        reject(configuration("2.5", "2", "60", "video/avc", encode(SPS), encode(PPS)),
                "fractional width");

        reject(configuration("1280", "720", "0.999", "video/avc", encode(SPS), encode(PPS)),
                "fps below one");
        reject(configuration("1280", "720", "240.001", "video/avc", encode(SPS), encode(PPS)),
                "fps above 240");
        reject(configuration("1280", "720", "1e400", "video/avc", encode(SPS), encode(PPS)),
                "non-finite float fps");
        reject(configuration("1280", "720", "60", "Video/AVC", encode(SPS), encode(PPS)),
                "codec remains exactly video/avc");

        reject(configuration("1280", "720", "60", "video/avc", encode(PPS), encode(PPS)),
                "csd0 must contain an SPS NAL type 7");
        reject(configuration("1280", "720", "60", "video/avc", encode(SPS), encode(SPS)),
                "csd1 must contain a PPS NAL type 8");
        reject(configuration("1280", "720", "60", "video/avc",
                        encode(new byte[] {0, 0, 2, 0, 0, 1, 0x67}), encode(PPS)),
                "parameter set must begin with Annex-B");
        reject(configuration("1280", "720", "60", "video/avc", "%%%", encode(PPS)),
                "invalid SPS Base64");
        reject(configuration("1280", "720", "60", "video/avc",
                        encode(parameterSet(65537, 7)), encode(PPS)),
                "SPS above 64 KiB");
        reject(configuration("1280", "720", "60", "video/avc",
                        encode(SPS), encode(parameterSet(65537, 8))),
                "PPS above 64 KiB");

        reject("{\"codec\":\"video/avc\"}".getBytes(StandardCharsets.UTF_8),
                "missing required fields");
        reject("{\"codec\":\"video/avc\",}".getBytes(StandardCharsets.UTF_8),
                "malformed JSON");
        reject(new byte[131073], "configuration above 128 KiB");
        reject(null, "null configuration");

        String withUnknownData = new String(configuration(
                "1280", "720", "60", "video/avc", encode(SPS), encode(PPS)), StandardCharsets.UTF_8);
        withUnknownData = withUnknownData.substring(0, withUnknownData.length() - 1)
                + ",\"future\":{\"items\":[true,false,null,1]}}";
        check(AvcConfiguration.parse(withUnknownData.getBytes(StandardCharsets.UTF_8)).width == 1280,
                "unknown future fields remain compatible with protocol v1");

        System.out.println("PASS: " + assertions + " AVC configuration assertions");
    }

    private static AvcConfiguration parse(String width, String height, String fps,
            byte[] sps, byte[] pps) throws IOException {
        return AvcConfiguration.parse(configuration(
                width, height, fps, "video/avc", encode(sps), encode(pps)));
    }

    private static byte[] configuration(String width, String height, String fps,
            String codec, String sps, String pps) {
        String json = "{\"codec\":\"" + codec + "\",\"width\":" + width
                + ",\"height\":" + height + ",\"fps\":" + fps
                + ",\"csd0\":\"" + sps + "\",\"csd1\":\"" + pps + "\"}";
        return json.getBytes(StandardCharsets.UTF_8);
    }

    private static String encode(byte[] bytes) { return Base64.getEncoder().encodeToString(bytes); }

    private static byte[] parameterSet(int length, int nalType) {
        byte[] bytes = new byte[length];
        bytes[3] = 1;
        bytes[4] = (byte) (0x60 | nalType);
        return bytes;
    }

    private static void reject(byte[] bytes, String message) throws Exception {
        try {
            AvcConfiguration.parse(bytes);
            throw new AssertionError("Accepted invalid configuration: " + message);
        } catch (IOException expected) {
            assertions++;
        }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        assertions++;
    }
}
