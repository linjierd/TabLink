package com.tablink.client;

import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Pure-Java parser for the protocol-v1 AVC configuration carried by packet 0x20. */
final class AvcConfiguration {
    private static final int MAX_CONFIGURATION_BYTES = 131072;
    private static final int MAX_PARAMETER_SET_BYTES = 65536;
    private static final long MAX_PIXELS = 16000000L;

    final int width;
    final int height;
    final float fps;
    final byte[] sps;
    final byte[] pps;

    private AvcConfiguration(int width, int height, float fps, byte[] sps, byte[] pps) {
        this.width = width;
        this.height = height;
        this.fps = fps;
        this.sps = sps;
        this.pps = pps;
    }

    static AvcConfiguration parse(byte[] bytes) throws IOException {
        if (bytes == null || bytes.length > MAX_CONFIGURATION_BYTES)
            throw new IOException("H.264 configuration is too large");

        final Map<String, Object> json;
        try {
            json = new JsonParser(new String(bytes, StandardCharsets.UTF_8)).parseDocument();
        } catch (IllegalArgumentException problem) {
            throw new IOException("Invalid H.264 configuration", problem);
        }

        final String codec;
        final int width;
        final int height;
        final float fps;
        final byte[] decodedSps;
        final byte[] decodedPps;
        try {
            codec = requiredString(json, "codec");
            width = requiredInteger(json, "width");
            height = requiredInteger(json, "height");
            fps = requiredNumber(json, "fps").floatValue();
            decodedSps = decodeBase64(requiredString(json, "csd0"));
            decodedPps = decodeBase64(requiredString(json, "csd1"));
        } catch (IllegalArgumentException problem) {
            throw new IOException("Invalid H.264 configuration", problem);
        }

        if (!"video/avc".equals(codec)) throw new IOException("Unsupported video codec");
        if (width < 2 || height < 2 || width > 8192 || height > 8192
                || (width & 1) != 0 || (height & 1) != 0
                || (long) width * height > MAX_PIXELS
                || !Float.isFinite(fps) || fps < 1 || fps > 240)
            throw new IOException("H.264 dimensions or frame rate are invalid");

        byte[] sps = normalizeParameterSet(decodedSps, 7);
        byte[] pps = normalizeParameterSet(decodedPps, 8);
        return new AvcConfiguration(width, height, fps, sps, pps);
    }

    private static String requiredString(Map<String, Object> json, String name) {
        Object value = json.get(name);
        if (!(value instanceof String)) throw new IllegalArgumentException("Missing JSON string: " + name);
        return (String) value;
    }

    private static int requiredInteger(Map<String, Object> json, String name) {
        BigDecimal value = requiredNumber(json, name);
        try {
            return value.intValueExact();
        } catch (ArithmeticException problem) {
            throw new IllegalArgumentException("Invalid JSON integer: " + name, problem);
        }
    }

    private static BigDecimal requiredNumber(Map<String, Object> json, String name) {
        Object value = json.get(name);
        if (!(value instanceof JsonNumber)) throw new IllegalArgumentException("Missing JSON number: " + name);
        try {
            return new BigDecimal(((JsonNumber) value).text);
        } catch (NumberFormatException problem) {
            throw new IllegalArgumentException("Invalid JSON number: " + name, problem);
        }
    }

    private static byte[] decodeBase64(String encoded) {
        StringBuilder compact = new StringBuilder(encoded.length());
        for (int i = 0; i < encoded.length(); i++) {
            char character = encoded.charAt(i);
            if (character != ' ' && character != '\t' && character != '\r'
                    && character != '\n' && character != '\f') compact.append(character);
        }
        int length = compact.length();
        int padding = length > 0 && compact.charAt(length - 1) == '=' ? 1 : 0;
        if (length > 1 && compact.charAt(length - 2) == '=') padding++;
        int dataLength = length - padding;
        if ((padding != 0 && (length & 3) != 0)
                || (padding == 1 && (dataLength & 3) != 3)
                || (padding == 2 && (dataLength & 3) != 2)
                || padding > 2 || (dataLength & 3) == 1)
            throw new IllegalArgumentException("Invalid Base64 length or padding");

        byte[] decoded = new byte[dataLength * 6 / 8];
        int buffer = 0;
        int bits = 0;
        int output = 0;
        for (int i = 0; i < dataLength; i++) {
            int value = base64Value(compact.charAt(i));
            if (value < 0) throw new IllegalArgumentException("Invalid Base64 character");
            buffer = (buffer << 6) | value;
            bits += 6;
            if (bits >= 8) {
                bits -= 8;
                decoded[output++] = (byte) (buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }
        for (int i = dataLength; i < length; i++) {
            if (compact.charAt(i) != '=') throw new IllegalArgumentException("Invalid Base64 padding");
        }
        if (buffer != 0) throw new IllegalArgumentException("Non-zero Base64 padding bits");
        return decoded;
    }

    private static int base64Value(char value) {
        if (value >= 'A' && value <= 'Z') return value - 'A';
        if (value >= 'a' && value <= 'z') return value - 'a' + 26;
        if (value >= '0' && value <= '9') return value - '0' + 52;
        if (value == '+') return 62;
        if (value == '/') return 63;
        return -1;
    }

    private static byte[] normalizeParameterSet(byte[] data, int type) throws IOException {
        if (data.length < 4 || data.length > MAX_PARAMETER_SET_BYTES
                || !VideoAccessUnit.containsNal(data, type))
            throw new IOException("H.264 parameter set is missing");
        if (data[0] == 0 && data[1] == 0 && data[2] == 1) {
            byte[] fourByteStart = new byte[data.length + 1];
            System.arraycopy(data, 0, fourByteStart, 1, data.length);
            return fourByteStart;
        }
        if (data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 1)
            throw new IOException("H.264 parameter set must use Annex-B");
        return data;
    }

    private static final class JsonNumber {
        final String text;
        JsonNumber(String text) { this.text = text; }
    }

    /** Small strict JSON reader used so the protocol parser remains runnable on a plain JDK. */
    private static final class JsonParser {
        private static final int MAX_DEPTH = 64;
        private final String text;
        private int index;

        JsonParser(String text) { this.text = text; }

        Map<String, Object> parseDocument() {
            skipWhitespace();
            Map<String, Object> value = parseObject(0);
            skipWhitespace();
            if (index != text.length()) fail("Trailing JSON content");
            return value;
        }

        private Map<String, Object> parseObject(int depth) {
            checkDepth(depth);
            expect('{');
            Map<String, Object> result = new HashMap<>();
            skipWhitespace();
            if (take('}')) return result;
            while (true) {
                skipWhitespace();
                String name = parseString();
                skipWhitespace();
                expect(':');
                skipWhitespace();
                result.put(name, parseValue(depth + 1));
                skipWhitespace();
                if (take('}')) return result;
                expect(',');
            }
        }

        private List<Object> parseArray(int depth) {
            checkDepth(depth);
            expect('[');
            List<Object> result = new ArrayList<>();
            skipWhitespace();
            if (take(']')) return result;
            while (true) {
                skipWhitespace();
                result.add(parseValue(depth + 1));
                skipWhitespace();
                if (take(']')) return result;
                expect(',');
            }
        }

        private Object parseValue(int depth) {
            checkDepth(depth);
            if (index >= text.length()) fail("Missing JSON value");
            char character = text.charAt(index);
            if (character == '"') return parseString();
            if (character == '{') return parseObject(depth);
            if (character == '[') return parseArray(depth);
            if (character == 't') { literal("true"); return Boolean.TRUE; }
            if (character == 'f') { literal("false"); return Boolean.FALSE; }
            if (character == 'n') { literal("null"); return null; }
            return parseNumber();
        }

        private JsonNumber parseNumber() {
            int start = index;
            take('-');
            if (take('0')) {
                if (index < text.length() && isDigit(text.charAt(index))) fail("Leading zero in JSON number");
            } else {
                requireDigit();
                while (index < text.length() && isDigit(text.charAt(index))) index++;
            }
            if (take('.')) {
                requireDigit();
                while (index < text.length() && isDigit(text.charAt(index))) index++;
            }
            if (take('e') || take('E')) {
                if (!take('+')) take('-');
                requireDigit();
                while (index < text.length() && isDigit(text.charAt(index))) index++;
            }
            return new JsonNumber(text.substring(start, index));
        }

        private String parseString() {
            expect('"');
            StringBuilder result = new StringBuilder();
            while (index < text.length()) {
                char character = text.charAt(index++);
                if (character == '"') return result.toString();
                if (character == '\\') {
                    if (index >= text.length()) fail("Incomplete JSON escape");
                    char escaped = text.charAt(index++);
                    switch (escaped) {
                        case '"': result.append('"'); break;
                        case '\\': result.append('\\'); break;
                        case '/': result.append('/'); break;
                        case 'b': result.append('\b'); break;
                        case 'f': result.append('\f'); break;
                        case 'n': result.append('\n'); break;
                        case 'r': result.append('\r'); break;
                        case 't': result.append('\t'); break;
                        case 'u': result.append(parseUnicodeEscape()); break;
                        default: fail("Invalid JSON escape");
                    }
                } else {
                    if (character < 0x20) fail("Control character in JSON string");
                    result.append(character);
                }
            }
            fail("Unterminated JSON string");
            return "";
        }

        private char parseUnicodeEscape() {
            if (index + 4 > text.length()) fail("Incomplete JSON unicode escape");
            int value = 0;
            for (int i = 0; i < 4; i++) {
                int digit = Character.digit(text.charAt(index++), 16);
                if (digit < 0) fail("Invalid JSON unicode escape");
                value = (value << 4) | digit;
            }
            return (char) value;
        }

        private void literal(String expected) {
            if (!text.regionMatches(index, expected, 0, expected.length())) fail("Invalid JSON literal");
            index += expected.length();
        }

        private void requireDigit() {
            if (index >= text.length() || !isDigit(text.charAt(index))) fail("Invalid JSON number");
        }

        private static boolean isDigit(char value) { return value >= '0' && value <= '9'; }

        private void skipWhitespace() {
            while (index < text.length()) {
                char character = text.charAt(index);
                if (character != ' ' && character != '\t' && character != '\r' && character != '\n') return;
                index++;
            }
        }

        private boolean take(char expected) {
            if (index < text.length() && text.charAt(index) == expected) {
                index++;
                return true;
            }
            return false;
        }

        private void expect(char expected) {
            if (!take(expected)) fail("Expected '" + expected + "'");
        }

        private void checkDepth(int depth) {
            if (depth > MAX_DEPTH) fail("JSON nesting is too deep");
        }

        private void fail(String message) {
            throw new IllegalArgumentException(message + " at character " + index);
        }
    }
}
