package com.tablink.client;

/** Decides whether a trusted one-shot ADB configuration may replace a session. */
final class AdbSessionHandoffPolicy {
    enum Decision {
        IGNORE(false, false),
        APPLY(false, true),
        REPLACE_USB_SESSION(true, true);

        final boolean stopActiveSession;
        final boolean applyConfiguration;

        Decision(boolean stopActiveSession, boolean applyConfiguration) {
            this.stopActiveSession = stopActiveSession;
            this.applyConfiguration = applyConfiguration;
        }
    }

    private AdbSessionHandoffPolicy() { }

    static Decision decide(boolean hasTrustedConfiguration, boolean running,
                           boolean connected, boolean usbSession,
                           boolean sameUsbConfiguration) {
        if (!hasTrustedConfiguration) return Decision.IGNORE;
        if (!running) return Decision.APPLY;
        if (!usbSession) return Decision.IGNORE;
        // Replaying the exact configuration must not disturb a healthy socket.
        // A different DUMP-protected configuration is an explicit handoff, even
        // while the old socket is briefly still marked connected after its host
        // has gone away. This closes the stop/start race without trusting an
        // exported Activity extra.
        if (connected && sameUsbConfiguration) return Decision.IGNORE;
        return Decision.REPLACE_USB_SESSION;
    }
}
