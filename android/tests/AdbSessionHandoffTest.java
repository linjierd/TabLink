package com.tablink.client;

public final class AdbSessionHandoffTest {
    public static void main(String[] args) {
        AdbSessionHandoffPolicy.Decision noConfiguration =
                AdbSessionHandoffPolicy.decide(false, true, true, true, true);
        check(noConfiguration == AdbSessionHandoffPolicy.Decision.IGNORE,
                "a launcher or forged intent without trusted configuration cannot stop a healthy session");

        AdbSessionHandoffPolicy.Decision healthy =
                AdbSessionHandoffPolicy.decide(true, true, true, true, true);
        check(healthy == AdbSessionHandoffPolicy.Decision.IGNORE
                        && !healthy.stopActiveSession && !healthy.applyConfiguration,
                "even trusted configuration does not replace a currently connected session");

        AdbSessionHandoffPolicy.Decision staleUsb =
                AdbSessionHandoffPolicy.decide(true, true, false, true, true);
        check(staleUsb == AdbSessionHandoffPolicy.Decision.REPLACE_USB_SESSION
                        && staleUsb.stopActiveSession && staleUsb.applyConfiguration,
                "a disconnected USB retry session is stopped before trusted replacement is applied");

        AdbSessionHandoffPolicy.Decision staleNetwork =
                AdbSessionHandoffPolicy.decide(true, true, false, false, false);
        check(staleNetwork == AdbSessionHandoffPolicy.Decision.IGNORE,
                "ADB configuration cannot silently replace a network trust session");

        AdbSessionHandoffPolicy.Decision racedOldSocket =
                AdbSessionHandoffPolicy.decide(true, true, true, true, false);
        check(racedOldSocket == AdbSessionHandoffPolicy.Decision.REPLACE_USB_SESSION
                        && racedOldSocket.stopActiveSession && racedOldSocket.applyConfiguration,
                "a different trusted USB generation replaces an old socket before disconnect visibility catches up");

        String marker = "0123456789abcdef0123456789abcdef";
        String token = "session_token_0123456789abcdef";
        AdbSessionConfiguration.clearForTests();
        AdbSessionConfiguration.publish(marker, token, 54321, 100);
        check(AdbSessionConfiguration.consume("ffffffffffffffffffffffffffffffff", 101) == null,
                "an unguessable activation marker is required and a mismatch does not consume the pending value");
        AdbSessionConfiguration.Pending accepted = AdbSessionConfiguration.consume(marker, 102);
        check(accepted != null && token.equals(accepted.token) && accepted.port == 54321,
                "matching one-shot configuration preserves the exact token and port");
        check(AdbSessionConfiguration.consume(marker, 103) == null,
                "configuration cannot be replayed");

        AdbSessionConfiguration.publish(marker, token, 54321, 200);
        check(AdbSessionConfiguration.consume(marker, 30_000_000_201L) == null,
                "configuration expires after the bounded activation window");
        AdbSessionConfiguration.publish(marker, token, 54321, 300);
        AdbSessionConfiguration.expire(marker);
        check(AdbSessionConfiguration.consume(marker, 301) == null,
                "scheduled expiration clears a never-consumed bearer from process memory");
        expectReject(() -> AdbSessionConfiguration.publish(marker, token, 27183, 1));
        expectReject(() -> AdbSessionConfiguration.publish("short", token, 54321, 1));
        expectReject(() -> AdbSessionConfiguration.publish(marker, "token with spaces", 54321, 1));

        System.out.println("PASS protected ADB handoff preserves healthy sessions and replaces disconnected USB retries");
    }

    private static void expectReject(Runnable action) {
        try {
            action.run();
            throw new AssertionError("Expected invalid ADB configuration rejection");
        } catch (IllegalArgumentException expected) { }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
