package com.tablink.client;

public final class LanguageSwitchPolicyTest {
    public static void main(String[] args) {
        if (LanguageSwitchPolicy.choose(true) != LanguageSwitchPolicy.Action.REFRESH_IN_PLACE)
            throw new AssertionError("a running or connected session language switch must not recreate the Activity");
        if (LanguageSwitchPolicy.choose(false) != LanguageSwitchPolicy.Action.RECREATE_ACTIVITY)
            throw new AssertionError("idle language switch should recreate the Activity");
        if (LanguageSwitchPolicy.sessionState(true, false, false)
                != LanguageSwitchPolicy.SessionState.CONNECTING)
            throw new AssertionError("running but not connected initial attempt must remain visibly connecting");
        if (LanguageSwitchPolicy.sessionState(true, false, true)
                != LanguageSwitchPolicy.SessionState.RECONNECTING)
            throw new AssertionError("running but not connected retry must remain visibly reconnecting");
        if (LanguageSwitchPolicy.sessionState(true, true, true)
                != LanguageSwitchPolicy.SessionState.CONNECTED)
            throw new AssertionError("connected state takes precedence over retry metadata");
        if (LanguageSwitchPolicy.sessionState(false, false, true)
                != LanguageSwitchPolicy.SessionState.IDLE)
            throw new AssertionError("stopped session is idle even if retry metadata was stale");
        System.out.println("Language switch policy: 6 assertions passed.");
    }
}
