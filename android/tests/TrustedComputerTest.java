package com.tablink.client;

public final class TrustedComputerTest {
    public static void main(String[] args) {
        String identity = repeat("ab", 32);
        TrustedComputer saved = new TrustedComputer(identity, identity, "192.168.50.10", 27184);
        check(saved.hostId.equals(identity) && saved.certificateSha256.equals(identity),
                "trusted host identity is preserved");
        check(saved.lastHost.equals("192.168.50.10") && saved.port == 27184,
                "trusted endpoint is preserved");

        TrustedComputer moved = saved.withEndpoint("10.20.30.40", 27192);
        check(moved.hostId.equals(identity) && moved.certificateSha256.equals(identity),
                "endpoint changes retain the pinned identity");
        check(moved.lastHost.equals("10.20.30.40") && moved.port == 27192,
                "endpoint changes accept another valid native endpoint");

        PairingLink sameHost = PairingLink.parse("tablink://connect?host=10.20.30.41&port=27191&token="
                + identity + "&cert=" + identity);
        check(saved.matchesCertificate(sameHost),
                "an enrolled certificate turns a stale one-time link into a route hint");
        PairingLink otherHost = PairingLink.parse("tablink://connect?host=10.20.30.41&port=27191&token="
                + identity + "&cert=" + repeat("02", 32));
        check(!saved.matchesCertificate(otherHost),
                "another certificate still requires fresh enrollment");

        expectReject(() -> new TrustedComputer(identity, repeat("02", 32), "192.168.1.2", 27184),
                "mismatched host and certificate fingerprints");
        expectReject(() -> new TrustedComputer(identity.toUpperCase(java.util.Locale.ROOT), identity,
                "192.168.1.2", 27184), "noncanonical uppercase host identity");
        expectReject(() -> new TrustedComputer(identity, identity, "127.0.0.1", 27184),
                "loopback endpoint");
        expectReject(() -> new TrustedComputer(identity, identity, "224.0.0.1", 27184),
                "multicast endpoint");
        expectReject(() -> new TrustedComputer(identity, identity, "192.168.001.2", 27184),
                "noncanonical IPv4 endpoint");
        expectReject(() -> new TrustedComputer(identity, identity, "192.168.1.2", 27185),
                "browser-only port");
        expectReject(() -> new TrustedComputer(identity, identity, "192.168.1.2", 27193),
                "discovery-only port");
        expectReject(() -> new TrustedComputer(null, identity, "192.168.1.2", 27184),
                "missing host identity");
        expectReject(() -> new TrustedComputer(identity, "gg" + identity.substring(2),
                "192.168.1.2", 27184), "non-hex certificate fingerprint");

        int[] nativePorts = {27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192};
        for (int port : nativePorts)
            check(new TrustedComputer(identity, identity, "192.168.1.2", port).port == port,
                    "native port " + port + " remains accepted");
        System.out.println("PASS trusted computer identity, endpoint migration and malformed state rejection");
    }

    private static String repeat(String value, int count) {
        StringBuilder result = new StringBuilder(value.length() * count);
        for (int index = 0; index < count; index++) result.append(value);
        return result.toString();
    }

    private static void expectReject(Runnable action, String message) {
        try {
            action.run();
            throw new AssertionError("Expected rejection: " + message);
        } catch (IllegalArgumentException expected) { }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
