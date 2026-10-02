package com.tablink.client;

import java.util.HashMap;
import java.util.Map;

public final class LanguagePreferenceTest {
    private static int checks;
    private static void check(boolean value, String label) {
        checks++; if (!value) throw new AssertionError(label);
    }
    public static void main(String[] args) {
        check(LanguagePreference.Mode.SYSTEM.ordinal() == 0
                        && LanguagePreference.Mode.CHINESE_SIMPLIFIED.ordinal() == 1
                        && LanguagePreference.Mode.ENGLISH.ordinal() == 2,
                "language picker order is system, simplified Chinese, English");
        check(LanguagePreference.read(new HashMap<>()).mode == LanguagePreference.Mode.SYSTEM,
                "missing preference follows system");
        for (LanguagePreference.Mode mode : LanguagePreference.Mode.values()) {
            Map<String, Object> values = new HashMap<>();
            values.put(LanguagePreference.KEY, mode.name());
            LanguagePreference.Selection selected = LanguagePreference.read(values);
            check(!selected.damaged && selected.mode == mode, "round trip " + mode);
        }
        Map<String, Object> wrongType = new HashMap<>(); wrongType.put(LanguagePreference.KEY, 3);
        check(LanguagePreference.read(wrongType).damaged, "wrong type is damaged");
        Map<String, Object> unknown = new HashMap<>(); unknown.put(LanguagePreference.KEY, "KLINGON");
        check(LanguagePreference.read(unknown).damaged, "unknown value is damaged");
        check("zh-CN".equals(LanguagePreference.resolvedLanguageTag(
                LanguagePreference.Mode.SYSTEM, "zh")), "system zh-CN resolves to simplified Chinese");
        check("zh-CN".equals(LanguagePreference.resolvedLanguageTag(
                LanguagePreference.Mode.SYSTEM, "zh-HK")), "system zh-HK resolves to simplified Chinese");
        check("en".equals(LanguagePreference.resolvedLanguageTag(
                LanguagePreference.Mode.SYSTEM, "de")), "non-Chinese system locale resolves to English");
        check("en".equals(LanguagePreference.resolvedLanguageTag(
                LanguagePreference.Mode.ENGLISH, "zh")), "explicit English overrides Chinese system locale");
        check("zh-CN".equals(LanguagePreference.resolvedLanguageTag(
                LanguagePreference.Mode.CHINESE_SIMPLIFIED, "en")), "explicit Chinese overrides English system locale");
        System.out.println("Language preference: " + checks + " assertions passed.");
    }
}
