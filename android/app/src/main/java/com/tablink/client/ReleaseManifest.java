package com.tablink.client;

import java.math.BigDecimal;
import java.net.URI;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.security.KeyFactory;
import java.security.MessageDigest;
import java.security.PublicKey;
import java.security.Signature;
import java.security.interfaces.ECPublicKey;
import java.security.spec.X509EncodedKeySpec;
import java.text.ParsePosition;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Date;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TimeZone;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Strict parser and signature verifier for the stable release manifest. */
public final class ReleaseManifest {
    public static final int SCHEMA = 1;
    public static final int UPDATE_PROTOCOL = 1;
    public static final long MAX_MANIFEST_BYTES = 256 * 1024;
    public static final long MAX_ARTIFACT_BYTES = 1024L * 1024 * 1024;
    private static final long MAX_FUTURE_MS = 24L * 60 * 60 * 1000;
    private static final long MAX_AGE_MS = 366L * 24 * 60 * 60 * 1000;
    private static final Pattern SEMVER = Pattern.compile("(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)");
    private static final Pattern SHA256 = Pattern.compile("[0-9a-fA-F]{64}");
    private static final Pattern IDENTIFIER = Pattern.compile("[A-Za-z0-9][A-Za-z0-9._-]{0,127}");
    private static final Pattern UTC = Pattern.compile("\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z");
    private static final Set<String> PLATFORMS;
    static {
        Set<String> platforms = new HashSet<>();
        Collections.addAll(platforms, "windows-x64", "android", "ios", "harmony");
        PLATFORMS = Collections.unmodifiableSet(platforms);
    }

    public final String releaseId;
    public final long publishedAtUtcMs;
    public final int rolloutPercentage;
    public final int minimumProtocolVersion;
    public final List<Artifact> artifacts;

    private ReleaseManifest(String releaseId, long publishedAtUtcMs, int rolloutPercentage,
                            int minimumProtocolVersion, List<Artifact> artifacts) {
        this.releaseId = releaseId;
        this.publishedAtUtcMs = publishedAtUtcMs;
        this.rolloutPercentage = rolloutPercentage;
        this.minimumProtocolVersion = minimumProtocolVersion;
        this.artifacts = Collections.unmodifiableList(artifacts);
    }

    public Artifact androidArtifact() {
        for (Artifact artifact : artifacts) if ("android".equals(artifact.platform)) return artifact;
        return null;
    }

    public boolean includesInstallation(String installationId) {
        if (rolloutPercentage >= 100) return true;
        if (rolloutPercentage <= 0) return false;
        if (installationId == null || !IDENTIFIER.matcher(installationId).matches()) return false;
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(
                    (installationId + "\n" + releaseId).getBytes(StandardCharsets.UTF_8));
            long sample = ((digest[0] & 255L) << 24) | ((digest[1] & 255L) << 16)
                    | ((digest[2] & 255L) << 8) | (digest[3] & 255L);
            return sample % 100 < rolloutPercentage;
        } catch (Exception impossible) { return false; }
    }

    public static ReleaseManifest verify(byte[] envelopeUtf8, String publicKeySpkiBase64,
                                         long nowUtcMs) throws ValidationException {
        if (envelopeUtf8 == null || envelopeUtf8.length == 0 || envelopeUtf8.length > MAX_MANIFEST_BYTES)
            throw invalid("更新清单大小无效");
        if (publicKeySpkiBase64 == null || publicKeySpkiBase64.length() > 256)
            throw invalid("更新公钥配置无效");
        String envelopeText = decodeUtf8(envelopeUtf8, "更新清单不是有效 UTF-8");
        Map<String, Object> envelope = object(new Json(envelopeText).parse(), "更新清单外层");
        exactKeys(envelope, set("payload", "signature"), Collections.emptySet(), "更新清单外层");
        byte[] payload = base64(string(envelope, "payload", 1, (int) MAX_MANIFEST_BYTES), "payload");
        byte[] signature = base64(string(envelope, "signature", 1, 256), "signature");
        verifySignature(payload, signature, publicKeySpkiBase64);
        String payloadText = decodeUtf8(payload, "更新清单正文不是有效 UTF-8");
        Map<String, Object> root = object(new Json(payloadText).parse(), "更新清单正文");
        exactKeys(root, set("schema", "channel", "releaseId", "publishedAtUtc", "rolloutPercentage",
                "minimumProtocolVersion", "artifacts"), Collections.emptySet(), "更新清单正文");
        if (integer(root, "schema", 1, 1) != SCHEMA) throw invalid("不支持的更新清单版本");
        if (!"stable".equals(string(root, "channel", 1, 16))) throw invalid("更新清单不是正式渠道");
        String releaseId = string(root, "releaseId", 1, 128);
        if (!IDENTIFIER.matcher(releaseId).matches()) throw invalid("发布编号无效");
        long published = parseUtc(string(root, "publishedAtUtc", 20, 32));
        if (published > nowUtcMs + MAX_FUTURE_MS || published < nowUtcMs - MAX_AGE_MS)
            throw invalid("更新清单发布时间异常");
        int rollout = integer(root, "rolloutPercentage", 0, 100);
        int protocol = integer(root, "minimumProtocolVersion", 1, Integer.MAX_VALUE);
        List<Object> values = array(root.get("artifacts"), "artifacts");
        if (values.isEmpty() || values.size() > PLATFORMS.size()) throw invalid("更新包列表数量无效");
        ArrayList<Artifact> artifacts = new ArrayList<>();
        HashSet<String> seenPlatforms = new HashSet<>();
        for (Object value : values) {
            Map<String, Object> item = object(value, "更新包");
            exactKeys(item, set("platform", "version", "build", "url", "size", "sha256"),
                    set("installerUrl", "notes"), "更新包");
            String platform = string(item, "platform", 1, 32);
            if (!PLATFORMS.contains(platform) || !seenPlatforms.add(platform))
                throw invalid("更新包平台重复或无效");
            String version = string(item, "version", 5, 64);
            if (!SEMVER.matcher(version).matches()) throw invalid("正式版版本号必须是稳定 SemVer");
            int build = integer(item, "build", 1, Integer.MAX_VALUE);
            String url = https(string(item, "url", 8, 2048), "更新包地址");
            long size = longInteger(item, "size", 1, MAX_ARTIFACT_BYTES);
            String sha = string(item, "sha256", 64, 64).toLowerCase(Locale.ROOT);
            if (!SHA256.matcher(sha).matches()) throw invalid("更新包 SHA-256 无效");
            String installerUrl = null;
            if (item.containsKey("installerUrl")) installerUrl = https(string(item, "installerUrl", 8, 2048), "安装地址");
            String notes = item.containsKey("notes") ? string(item, "notes", 0, 4096) : "";
            artifacts.add(new Artifact(platform, version, build, url, size, sha, installerUrl, notes));
        }
        return new ReleaseManifest(releaseId, published, rollout, protocol, artifacts);
    }

    private static void verifySignature(byte[] payload, byte[] signatureBytes, String spkiBase64)
            throws ValidationException {
        try {
            byte[] spki = base64(spkiBase64, "更新公钥");
            PublicKey publicKey = KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(spki));
            if (!(publicKey instanceof ECPublicKey)
                    || ((ECPublicKey) publicKey).getParams().getCurve().getField().getFieldSize() != 256)
                throw invalid("更新公钥不是 P-256 公钥");
            Signature verifier = Signature.getInstance("SHA256withECDSA");
            verifier.initVerify(publicKey);
            verifier.update(payload);
            if (!verifier.verify(signatureBytes)) throw invalid("更新清单签名无效");
        } catch (ValidationException expected) { throw expected; }
        catch (Exception failure) { throw invalid("无法验证更新清单签名"); }
    }

    private static String https(String value, String label) throws ValidationException {
        try {
            URI uri = new URI(value);
            if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null
                    || uri.getHost().isEmpty() || uri.getUserInfo() != null || uri.getFragment() != null)
                throw invalid(label + "必须是完整 HTTPS 地址");
            return uri.toASCIIString();
        } catch (ValidationException expected) { throw expected; }
        catch (Exception failure) { throw invalid(label + "无效"); }
    }

    private static long parseUtc(String value) throws ValidationException {
        Matcher match = UTC.matcher(value);
        if (!match.matches()) throw invalid("发布时间必须使用整秒 UTC 格式");
        SimpleDateFormat format = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.ROOT);
        format.setLenient(false);
        format.setTimeZone(TimeZone.getTimeZone("UTC"));
        ParsePosition position = new ParsePosition(0);
        Date parsed = format.parse(value, position);
        if (parsed == null || position.getIndex() != value.length()) throw invalid("发布时间无效");
        return parsed.getTime();
    }

    private static byte[] base64(String value, String label) throws ValidationException {
        try {
            if (value.length() == 0 || value.length() % 4 != 0) throw new IllegalArgumentException();
            int padding = value.endsWith("==") ? 2 : value.endsWith("=") ? 1 : 0;
            byte[] result = new byte[value.length() / 4 * 3 - padding];
            int output = 0;
            for (int offset = 0; offset < value.length(); offset += 4) {
                int a = base64Digit(value.charAt(offset));
                int b = base64Digit(value.charAt(offset + 1));
                int c = value.charAt(offset + 2) == '=' ? 0 : base64Digit(value.charAt(offset + 2));
                int d = value.charAt(offset + 3) == '=' ? 0 : base64Digit(value.charAt(offset + 3));
                boolean last = offset + 4 == value.length();
                if (a < 0 || b < 0 || c < 0 || d < 0 || (!last && (value.charAt(offset + 2) == '=' || value.charAt(offset + 3) == '='))
                        || value.charAt(offset + 2) == '=' && value.charAt(offset + 3) != '=')
                    throw new IllegalArgumentException();
                int packed = a << 18 | b << 12 | c << 6 | d;
                if (output < result.length) result[output++] = (byte) (packed >>> 16);
                if (output < result.length) result[output++] = (byte) (packed >>> 8);
                if (output < result.length) result[output++] = (byte) packed;
            }
            // RFC 4648 padding bits must be zero; accepting aliases weakens exact signed-envelope parsing.
            if (padding == 2 && (base64Digit(value.charAt(value.length() - 3)) & 15) != 0
                    || padding == 1 && (base64Digit(value.charAt(value.length() - 2)) & 3) != 0)
                throw new IllegalArgumentException();
            return result;
        } catch (IllegalArgumentException failure) { throw invalid(label + "不是规范 Base64"); }
    }

    private static int base64Digit(char value) {
        if (value >= 'A' && value <= 'Z') return value - 'A';
        if (value >= 'a' && value <= 'z') return value - 'a' + 26;
        if (value >= '0' && value <= '9') return value - '0' + 52;
        if (value == '+') return 62;
        if (value == '/') return 63;
        return -1;
    }

    private static String decodeUtf8(byte[] bytes, String error) throws ValidationException {
        try {
            CharBuffer decoded = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes));
            return decoded.toString();
        } catch (CharacterCodingException failure) { throw invalid(error); }
    }

    private static Map<String, Object> object(Object value, String label) throws ValidationException {
        if (!(value instanceof Map)) throw invalid(label + "必须是对象");
        @SuppressWarnings("unchecked") Map<String, Object> result = (Map<String, Object>) value;
        return result;
    }
    private static List<Object> array(Object value, String label) throws ValidationException {
        if (!(value instanceof List)) throw invalid(label + "必须是数组");
        @SuppressWarnings("unchecked") List<Object> result = (List<Object>) value;
        return result;
    }
    private static String string(Map<String, Object> map, String key, int min, int max) throws ValidationException {
        Object value = map.get(key);
        if (!(value instanceof String)) throw invalid(key + "必须是文字");
        String result = (String) value;
        if (result.length() < min || result.length() > max) throw invalid(key + "长度无效");
        return result;
    }
    private static int integer(Map<String, Object> map, String key, int min, int max) throws ValidationException {
        long value = longInteger(map, key, min, max);
        return (int) value;
    }
    private static long longInteger(Map<String, Object> map, String key, long min, long max) throws ValidationException {
        Object value = map.get(key);
        if (!(value instanceof BigDecimal)) throw invalid(key + "必须是整数");
        try {
            long result = ((BigDecimal) value).longValueExact();
            if (result < min || result > max) throw invalid(key + "超出范围");
            return result;
        } catch (ArithmeticException failure) { throw invalid(key + "必须是整数"); }
    }
    private static void exactKeys(Map<String, Object> value, Set<String> required, Set<String> optional, String label)
            throws ValidationException {
        if (!value.keySet().containsAll(required)) throw invalid(label + "缺少必要字段");
        for (String key : value.keySet()) if (!required.contains(key) && !optional.contains(key))
            throw invalid(label + "包含未知字段");
    }
    private static Set<String> set(String... values) {
        HashSet<String> result = new HashSet<>();
        Collections.addAll(result, values);
        return result;
    }
    private static ValidationException invalid(String message) { return new ValidationException(message); }

    public static final class Artifact {
        public final String platform, version, url, sha256, installerUrl, notes;
        public final int build;
        public final long size;
        Artifact(String platform, String version, int build, String url, long size, String sha256,
                 String installerUrl, String notes) {
            this.platform = platform; this.version = version; this.build = build; this.url = url;
            this.size = size; this.sha256 = sha256; this.installerUrl = installerUrl; this.notes = notes;
        }

        public boolean isNewerThan(int installedBuild, String installedVersion) {
            if (build <= installedBuild || installedVersion == null) return false;
            Matcher candidate = SEMVER.matcher(version);
            Matcher installed = SEMVER.matcher(installedVersion);
            if (!candidate.matches() || !installed.matches()) return false;
            for (int i = 1; i <= 3; i++) {
                String left = candidate.group(i), right = installed.group(i);
                if (left.length() != right.length()) return left.length() > right.length();
                int comparison = left.compareTo(right);
                if (comparison != 0) return comparison > 0;
            }
            return false;
        }
    }

    public static final class ValidationException extends Exception {
        ValidationException(String message) { super(message); }
    }

    /** Small strict JSON reader so manifest security tests run on a plain JVM. */
    private static final class Json {
        private final String text; private int offset;
        Json(String text) { this.text = text; }
        Object parse() throws ValidationException {
            Object value = value(); whitespace();
            if (offset != text.length()) throw invalid("JSON 末尾包含多余内容");
            return value;
        }
        private Object value() throws ValidationException {
            whitespace();
            if (offset >= text.length()) throw invalid("JSON 提前结束");
            char c = text.charAt(offset);
            if (c == '{') return object(); if (c == '[') return array(); if (c == '"') return string();
            if (c == 't') return literal("true", Boolean.TRUE); if (c == 'f') return literal("false", Boolean.FALSE);
            if (c == 'n') return literal("null", null); if (c == '-' || c >= '0' && c <= '9') return number();
            throw invalid("JSON 值无效");
        }
        private Map<String, Object> object() throws ValidationException {
            offset++; whitespace(); Map<String, Object> map = new HashMap<>();
            if (take('}')) return map;
            while (true) {
                whitespace(); if (!peek('"')) throw invalid("JSON 对象键无效");
                String key = string(); whitespace(); require(':'); Object value = value();
                if (map.containsKey(key)) throw invalid("JSON 对象包含重复字段");
                map.put(key, value);
                whitespace(); if (take('}')) return map; require(',');
            }
        }
        private List<Object> array() throws ValidationException {
            offset++; whitespace(); ArrayList<Object> list = new ArrayList<>();
            if (take(']')) return list;
            while (true) { list.add(value()); whitespace(); if (take(']')) return list; require(','); }
        }
        private String string() throws ValidationException {
            require('"'); StringBuilder out = new StringBuilder();
            while (offset < text.length()) {
                char c = text.charAt(offset++);
                if (c == '"') return out.toString();
                if (c < 0x20) throw invalid("JSON 字符串包含控制字符");
                if (c == '\\') {
                    if (offset >= text.length()) throw invalid("JSON 转义提前结束");
                    char escape = text.charAt(offset++);
                    if (escape == '"' || escape == '\\' || escape == '/') out.append(escape);
                    else if (escape == 'b') out.append('\b'); else if (escape == 'f') out.append('\f');
                    else if (escape == 'n') out.append('\n'); else if (escape == 'r') out.append('\r');
                    else if (escape == 't') out.append('\t');
                    else if (escape == 'u') {
                        char decoded = unicode();
                        if (Character.isHighSurrogate(decoded)) {
                            if (offset + 1 >= text.length() || text.charAt(offset++) != '\\' || text.charAt(offset++) != 'u')
                                throw invalid("JSON Unicode 代理项无效");
                            char low = unicode(); if (!Character.isLowSurrogate(low)) throw invalid("JSON Unicode 代理项无效");
                            out.append(decoded).append(low);
                        } else if (Character.isLowSurrogate(decoded)) throw invalid("JSON Unicode 代理项无效");
                        else out.append(decoded);
                    } else throw invalid("JSON 转义无效");
                } else {
                    if (Character.isSurrogate(c)) throw invalid("JSON Unicode 代理项必须转义成一对");
                    out.append(c);
                }
            }
            throw invalid("JSON 字符串提前结束");
        }
        private char unicode() throws ValidationException {
            if (offset + 4 > text.length()) throw invalid("JSON Unicode 转义提前结束");
            int value = 0;
            for (int i = 0; i < 4; i++) {
                int digit = Character.digit(text.charAt(offset++), 16);
                if (digit < 0) throw invalid("JSON Unicode 转义无效"); value = value * 16 + digit;
            }
            return (char) value;
        }
        private BigDecimal number() throws ValidationException {
            int start = offset; if (take('-') && offset >= text.length()) throw invalid("JSON 数字无效");
            if (take('0')) { if (offset < text.length() && Character.isDigit(text.charAt(offset))) throw invalid("JSON 数字前导零无效"); }
            else { if (offset >= text.length() || text.charAt(offset) < '1' || text.charAt(offset) > '9') throw invalid("JSON 数字无效"); while (offset < text.length() && Character.isDigit(text.charAt(offset))) offset++; }
            if (take('.')) { int begin = offset; while (offset < text.length() && Character.isDigit(text.charAt(offset))) offset++; if (begin == offset) throw invalid("JSON 小数无效"); }
            if (take('e') || take('E')) { take('+'); take('-'); int begin = offset; while (offset < text.length() && Character.isDigit(text.charAt(offset))) offset++; if (begin == offset) throw invalid("JSON 指数无效"); }
            try { return new BigDecimal(text.substring(start, offset)); }
            catch (NumberFormatException failure) { throw invalid("JSON 数字无效"); }
        }
        private Object literal(String expected, Object value) throws ValidationException {
            if (!text.regionMatches(offset, expected, 0, expected.length())) throw invalid("JSON 字面量无效");
            offset += expected.length(); return value;
        }
        private void whitespace() { while (offset < text.length() && " \t\r\n".indexOf(text.charAt(offset)) >= 0) offset++; }
        private boolean peek(char c) { return offset < text.length() && text.charAt(offset) == c; }
        private boolean take(char c) { if (peek(c)) { offset++; return true; } return false; }
        private void require(char c) throws ValidationException { if (!take(c)) throw invalid("JSON 缺少 " + c); }
    }
}
