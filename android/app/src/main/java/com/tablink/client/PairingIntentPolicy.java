package com.tablink.client;

/** Security policy for pairing links delivered through the exported VIEW activity. */
public final class PairingIntentPolicy {
    private PairingIntentPolicy() { }

    /**
     * Scanner and paste flows are explicit in-app actions. An external deep link that
     * would replace an existing certificate requires a second, in-app decision.
     */
    public static boolean requiresReplacementConfirmation(boolean activityIntentData,
                                                           TrustedComputer saved,
                                                           PairingLink incoming) {
        if (incoming == null) throw new IllegalArgumentException("缺少配对链接");
        return activityIntentData && (saved == null || !saved.matchesCertificate(incoming));
    }
}
