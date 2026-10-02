package com.tablink.client;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageInstaller;
import android.os.Build;

/** PackageInstaller callback guarded by the persisted current session/token tuple. */
public final class UpdateInstallReceiver extends BroadcastReceiver {
    static final String ACTION = "com.tablink.client.UPDATE_INSTALL_STATUS";
    static final String PREFS = UpdateModePreference.PREFERENCES;
    static final String EXTRA_ATTEMPT_SESSION_ID = "com.tablink.client.extra.INSTALL_SESSION_ID";
    static final String EXTRA_ATTEMPT_TOKEN = "com.tablink.client.extra.INSTALL_ATTEMPT_TOKEN";

    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null || !ACTION.equals(intent.getAction())) return;
        UpdateInstallAttempt callback = UpdateInstallAttempt.validatedCallback(
                intent.getIntExtra(EXTRA_ATTEMPT_SESSION_ID, -1),
                intent.getStringExtra(EXTRA_ATTEMPT_TOKEN),
                intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1));
        if (callback == null) return;
        Context localized = AppLanguage.wrap(context);

        SharedPreferences preferences = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        UpdateInstallAttemptStore.Recovery recovery = UpdateInstallAttemptStore.recover(
                preferences, PackageInstaller.STATUS_PENDING_USER_ACTION);
        if (recovery.abandonSessionId >= 0) abandon(context, recovery.abandonSessionId);
        if (recovery.current == null || !recovery.current.matches(callback)) return;
        if (!installsAllowed(preferences)) {
            abandon(context, callback.sessionId);
            UpdateInstallAttemptStore.discardIfCurrent(preferences, callback, true);
            return;
        }

        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirmation;
            if (Build.VERSION.SDK_INT >= 33)
                confirmation = intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent.class);
            else {
                @SuppressWarnings("deprecation") Intent legacy = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                confirmation = legacy;
            }
            if (confirmation != null) {
                confirmation.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                try {
                    UpdateInstallerUiGate.LaunchResult launch =
                            UpdateInstallAttemptStore.launchConfirmation(preferences, callback, status,
                                    localized.getString(R.string.update_install_waiting_confirmation),
                                    () -> context.startActivity(confirmation));
                    if (launch == UpdateInstallerUiGate.LaunchResult.LAUNCHED
                            || launch == UpdateInstallerUiGate.LaunchResult.ALREADY_LAUNCHING
                            || launch == UpdateInstallerUiGate.LaunchResult.STALE_ATTEMPT) return;
                    abandon(context, callback.sessionId);
                    UpdateInstallAttemptStore.finish(preferences, callback,
                            PackageInstaller.STATUS_FAILURE_ABORTED,
                            localized.getString(R.string.update_install_deferred_for_display));
                    return;
                } catch (RuntimeException ignored) { }
            }
            abandon(context, callback.sessionId);
            UpdateInstallAttemptStore.finish(preferences, callback, PackageInstaller.STATUS_FAILURE,
                    localized.getString(R.string.update_confirmation_open_failed));
            return;
        }

        if (status == PackageInstaller.STATUS_SUCCESS) {
            UpdateInstallAttemptStore.finish(preferences, callback, status,
                    localized.getString(R.string.update_install_complete));
            return;
        }
        String detail = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        if (status == PackageInstaller.STATUS_FAILURE_ABORTED)
            detail = localized.getString(R.string.update_install_cancelled);
        else if (detail == null || detail.trim().isEmpty())
            detail = localized.getString(R.string.update_installer_incomplete);
        detail = detail.replaceAll("[\\p{Cntrl}]", " ").trim();
        if (AppLanguage.isChinese(context) != containsHan(detail))
            detail = localized.getString(R.string.update_installer_incomplete);
        if (detail.length() > 100) detail = detail.substring(0, 100);
        UpdateInstallAttemptStore.finish(preferences, callback, status, detail);
    }

    private static boolean installsAllowed(SharedPreferences preferences) {
        try {
            UpdateModePreference.Selection selected = UpdateModePreference.read(preferences.getAll());
            return !selected.damaged && UpdateModePreference.installsAllowed(
                    selected.mode.name(), null, false);
        } catch (RuntimeException invalid) {
            return false;
        }
    }

    private static void abandon(Context context, int sessionId) {
        if (sessionId < 0) return;
        try { context.getPackageManager().getPackageInstaller().abandonSession(sessionId); }
        catch (RuntimeException ignored) { }
    }

    private static boolean containsHan(String value) {
        for (int i = 0; i < value.length();) {
            int codePoint = value.codePointAt(i);
            if (codePoint >= 0x3400 && codePoint <= 0x4dbf || codePoint >= 0x4e00 && codePoint <= 0x9fff
                    || codePoint >= 0xf900 && codePoint <= 0xfaff || codePoint >= 0x20000 && codePoint <= 0x323af)
                return true;
            i += Character.charCount(codePoint);
        }
        return false;
    }
}
