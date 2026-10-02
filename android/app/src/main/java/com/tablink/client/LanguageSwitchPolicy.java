package com.tablink.client;

/** Prevents a language-only change from tearing down an active display transport. */
public final class LanguageSwitchPolicy {
    public enum Action { REFRESH_IN_PLACE, RECREATE_ACTIVITY }
    public enum SessionState { IDLE, CONNECTING, RECONNECTING, CONNECTED }
    private LanguageSwitchPolicy() { }
    public static Action choose(boolean sessionInProgress) {
        return sessionInProgress ? Action.REFRESH_IN_PLACE : Action.RECREATE_ACTIVITY;
    }
    public static SessionState sessionState(boolean running, boolean connected, boolean reconnecting) {
        if (!running) return SessionState.IDLE;
        if (connected) return SessionState.CONNECTED;
        return reconnecting ? SessionState.RECONNECTING : SessionState.CONNECTING;
    }
}
