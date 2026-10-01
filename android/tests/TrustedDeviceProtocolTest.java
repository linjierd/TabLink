package com.tablink.client;

import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.Signature;
import java.security.spec.ECGenParameterSpec;
import java.util.Arrays;

public final class TrustedDeviceProtocolTest {
    public static void main(String[] args) throws Exception {
        KeyPairGenerator generator = KeyPairGenerator.getInstance("EC");
        generator.initialize(new ECGenParameterSpec("secp256r1"));
        KeyPair device = generator.generateKeyPair();
        KeyPair other = generator.generateKeyPair();
        String deviceId = TrustedDeviceProtocol.deviceId(device.getPublic());
        String hostId = repeat("01", 32);
        byte[] challenge = new byte[TrustedDeviceProtocol.CHALLENGE_BYTES];
        for (int index = 0; index < challenge.length; index++) challenge[index] = (byte) index;
        byte[] transcript = TrustedDeviceProtocol.transcript(hostId, deviceId, challenge);
        check(Arrays.equals(transcript, TrustedDeviceProtocol.transcript(hostId, deviceId, challenge)),
                "canonical transcript is stable");
        Signature signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(device.getPrivate());signer.update(transcript);
        byte[] signature = signer.sign();
        check(TrustedDeviceProtocol.verify(device.getPublic(), hostId, deviceId, challenge, signature),
                "fresh challenge signature verifies");
        byte[] changed = challenge.clone();changed[0] ^= 1;
        check(!TrustedDeviceProtocol.verify(device.getPublic(), hostId, deviceId, changed, signature),
                "changed challenge rejects replay");
        check(!TrustedDeviceProtocol.verify(other.getPublic(), hostId,
                TrustedDeviceProtocol.deviceId(other.getPublic()), challenge, signature),
                "different device key is rejected");
        check(WireProtocol.TRUSTED_HELLO == 0x17 && WireProtocol.TRUSTED_CHALLENGE == 0x18 &&
                WireProtocol.TRUSTED_PROOF == 0x19 && WireProtocol.TRUST_ESTABLISHED == 0x1a,
                "trusted packet numbers stay fixed");
        byte[] vectorChallenge = new byte[32];
        for (int index = 0; index < vectorChallenge.length; index++) vectorChallenge[index] = (byte) index;
        check(hex(java.security.MessageDigest.getInstance("SHA-256").digest(
                TrustedDeviceProtocol.transcript(repeat("01", 32), repeat("02", 32), vectorChallenge)))
                        .equals("1F57A15130EE260C4242840D79E543CFA0843976E42B0989C628D250E305AFB9"),
                "cross-platform trusted transcript vector stays fixed");
        System.out.println("PASS trusted device challenge transcript, identity binding, replay rejection and packet constants");
    }

    private static String repeat(String value, int count) {
        StringBuilder result = new StringBuilder(value.length() * count);
        for (int index = 0; index < count; index++) result.append(value);
        return result.toString();
    }

    private static String hex(byte[] value) {
        StringBuilder result = new StringBuilder(value.length * 2);
        for (byte item : value) result.append(String.format(java.util.Locale.ROOT, "%02X", item & 0xff));
        return result.toString();
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
