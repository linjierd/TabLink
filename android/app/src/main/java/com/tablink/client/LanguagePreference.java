package com.tablink.client;

import java.util.Map;

/** Pure preference policy so language fallback can be tested without Android. */
public final class LanguagePreference {
    public enum Mode { SYSTEM, CHINESE_SIMPLIFIED, ENGLISH }
    public static final String KEY = "app-language";

    public static final class Selection {
        public final Mode mode;
        public final boolean damaged;
        Selection(Mode mode, boolean damaged) { this.mode = mode; this.damaged = damaged; }
    }

    private LanguagePreference() { }

    public static Selection read(Map<String, ?> values) {
        if (values == null || !values.containsKey(KEY)) return new Selection(Mode.SYSTEM, false);
        Object raw = values.get(KEY);
        if (!(raw instanceof String)) return new Selection(Mode.SYSTEM, true);
        try { return new Selection(Mode.valueOf((String) raw), false); }
        catch (IllegalArgumentException invalid) { return new Selection(Mode.SYSTEM, true); }
    }

    public static String resolvedLanguageTag(Mode mode, String systemLanguage) {
        if (mode == Mode.CHINESE_SIMPLIFIED) return "zh-CN";
        if (mode == Mode.ENGLISH) return "en";
        String system = systemLanguage == null ? "" : systemLanguage.trim().replace('_', '-');
        return "zh".equalsIgnoreCase(system) || system.toLowerCase(java.util.Locale.ROOT).startsWith("zh-")
                ? "zh-CN" : "en";
    }
}
