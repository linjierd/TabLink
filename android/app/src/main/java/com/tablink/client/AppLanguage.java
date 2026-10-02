package com.tablink.client;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.content.res.Resources;
import android.os.Build;
import android.os.LocaleList;

import java.util.Locale;

/** Applies the saved in-app language before an Activity creates any UI. */
public final class AppLanguage {
    private static final String PREFERENCES = "display";

    private AppLanguage() { }

    public static LanguagePreference.Mode read(Context context) {
        try {
            LanguagePreference.Selection selection = LanguagePreference.read(
                    context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).getAll());
            return selection.mode;
        } catch (RuntimeException invalid) {
            return LanguagePreference.Mode.SYSTEM;
        }
    }

    public static boolean save(Context context, LanguagePreference.Mode mode) {
        if (mode == null) return false;
        SharedPreferences.Editor editor = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).edit();
        if (mode == LanguagePreference.Mode.SYSTEM) editor.remove(LanguagePreference.KEY);
        else editor.putString(LanguagePreference.KEY, mode.name());
        try { return editor.commit(); }
        catch (RuntimeException failure) { return false; }
    }

    public static Context wrap(Context base) {
        LanguagePreference.Mode mode = read(base);
        Configuration systemConfiguration = Resources.getSystem().getConfiguration();
        Locale systemLocale = Build.VERSION.SDK_INT >= 24
                ? systemConfiguration.getLocales().get(0) : systemConfiguration.locale;
        String systemLanguage = systemLocale == null ? "" : systemLocale.getLanguage();
        Locale locale = Locale.forLanguageTag(
                LanguagePreference.resolvedLanguageTag(mode, systemLanguage));
        Configuration configuration = new Configuration(base.getResources().getConfiguration());
        if (Build.VERSION.SDK_INT >= 24) configuration.setLocales(new LocaleList(locale));
        else configuration.setLocale(locale);
        configuration.setLayoutDirection(locale);
        return base.createConfigurationContext(configuration);
    }

    public static boolean isChinese(Context context) {
        Configuration configuration = wrap(context).getResources().getConfiguration();
        Locale locale = Build.VERSION.SDK_INT >= 24 ? configuration.getLocales().get(0) : configuration.locale;
        return locale != null && "zh".equalsIgnoreCase(locale.getLanguage());
    }
}
