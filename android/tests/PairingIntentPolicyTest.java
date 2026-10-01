package com.tablink.client;

public final class PairingIntentPolicyTest {
    public static void main(String[] args) {
        String first = repeat("11", 32);
        String second = repeat("22", 32);
        TrustedComputer saved = new TrustedComputer(first, first, "192.168.8.10", 27184);
        PairingLink same = PairingLink.parse("tablink://connect?host=192.168.8.11&port=27186&token="
                + second + "&cert=" + first);
        PairingLink different = PairingLink.parse("tablink://connect?host=192.168.8.12&port=27186&token="
                + first + "&cert=" + second);

        check(!PairingIntentPolicy.requiresReplacementConfirmation(true, saved, same),
                "same pinned certificate remains an authenticated route hint");
        check(PairingIntentPolicy.requiresReplacementConfirmation(true, saved, different),
                "any pairing URI delivered through the exported activity requires confirmation");
        check(!PairingIntentPolicy.requiresReplacementConfirmation(false, saved, different),
                "scanner and paste flows remain explicit in-app authorization");
        check(PairingIntentPolicy.requiresReplacementConfirmation(true, null, different),
                "first external deep link also requires an in-app user decision");
        expectReject(() -> PairingIntentPolicy.requiresReplacementConfirmation(true, saved, null));
        System.out.println("PASS external pairing replacement requires an in-app confirmation");
    }

    private static String repeat(String value, int count) {
        StringBuilder result = new StringBuilder(value.length() * count);
        for (int index = 0; index < count; index++) result.append(value);
        return result.toString();
    }

    private static void expectReject(Runnable action) {
        try {
            action.run();
            throw new AssertionError("Expected missing pairing rejection");
        } catch (IllegalArgumentException expected) { }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
