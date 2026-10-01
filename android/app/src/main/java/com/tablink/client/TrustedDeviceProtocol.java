package com.tablink.client;

import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.PublicKey;
import java.security.Signature;
import java.util.Locale;

/** Pure-Java transcript shared by Android Keystore signing and JVM tests. */
public final class TrustedDeviceProtocol {
    public static final String FEATURE = "trusted-device-v1";
    public static final int TRUSTED_HELLO = 0x17;
    public static final int TRUSTED_CHALLENGE = 0x18;
    public static final int TRUSTED_PROOF = 0x19;
    public static final int TRUST_ESTABLISHED = 0x1a;
    public static final int DISCOVERY_PORT = 27193;
    public static final int CHALLENGE_BYTES = 32;
    private static final byte[] DOMAIN = "TabLink trusted-device-v1 proof\0".getBytes(StandardCharsets.US_ASCII);

    private TrustedDeviceProtocol() {}

    public static String deviceId(PublicKey publicKey) throws GeneralSecurityException {
        byte[] encoded = publicKey.getEncoded();
        if (encoded == null || encoded.length < 80 || encoded.length > 160)
            throw new GeneralSecurityException("可信设备公钥长度无效");
        return hex(MessageDigest.getInstance("SHA-256").digest(encoded));
    }

    public static byte[] transcript(String hostId, String deviceId, byte[] challenge) {
        validateSha256(hostId, "电脑身份");
        validateSha256(deviceId, "设备身份");
        if (challenge == null || challenge.length != CHALLENGE_BYTES)
            throw new IllegalArgumentException("可信设备挑战长度无效");
        try {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream(256);
            DataOutputStream output = new DataOutputStream(bytes);
            output.write(DOMAIN);
            writeField(output, hostId.getBytes(StandardCharsets.US_ASCII));
            writeField(output, deviceId.getBytes(StandardCharsets.US_ASCII));
            writeField(output, challenge);
            output.flush();
            return bytes.toByteArray();
        } catch (IOException impossible) {
            throw new IllegalStateException("无法生成可信设备签名内容", impossible);
        }
    }

    public static boolean verify(PublicKey publicKey, String hostId, String deviceId,
            byte[] challenge, byte[] signature) throws GeneralSecurityException {
        if (!MessageDigest.isEqual(hexToBytes(deviceId),
                MessageDigest.getInstance("SHA-256").digest(publicKey.getEncoded()))) return false;
        if (signature == null || signature.length < 64 || signature.length > 80) return false;
        Signature verifier = Signature.getInstance("SHA256withECDSA");
        verifier.initVerify(publicKey);
        verifier.update(transcript(hostId, deviceId, challenge));
        return verifier.verify(signature);
    }

    public static void validateSha256(String value, String description) {
        if (value == null || !value.matches("[0-9a-f]{64}"))
            throw new IllegalArgumentException(description + "无效");
    }

    public static byte[] hexToBytes(String value) {
        validateSha256(value, "SHA-256");
        byte[] result = new byte[32];
        for (int index = 0; index < result.length; index++)
            result[index] = (byte) Integer.parseInt(value.substring(index * 2, index * 2 + 2), 16);
        return result;
    }

    private static String hex(byte[] value) {
        StringBuilder result = new StringBuilder(value.length * 2);
        for (byte item : value) result.append(String.format(Locale.ROOT, "%02x", item & 0xff));
        return result.toString();
    }

    private static void writeField(DataOutputStream output, byte[] value) throws IOException {
        output.writeInt(value.length);
        output.write(value);
    }
}
