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
                                    "Android 正在等待安装确认", () -> context.startActivity(confirmation));
                    if (launch == UpdateInstallerUiGate.LaunchResult.LAUNCHED
                            || launch == UpdateInstallerUiGate.LaunchResult.ALREADY_LAUNCHING
                            || launch == UpdateInstallerUiGate.LaunchResult.STALE_ATTEMPT) return;
                    abandon(context, callback.sessionId);
                    UpdateInstallAttemptStore.finish(preferences, callback,
                            PackageInstaller.STATUS_FAILURE_ABORTED,
                            "副屏正在使用；断开后将重新确认并安装更新");
                    return;
                } catch (RuntimeException ignored) { }
            }
            abandon(context, callback.sessionId);
            UpdateInstallAttemptStore.finish(preferences, callback, PackageInstaller.STATUS_FAILURE,
                    "无法打开 Android 安装确认页，请在 TabLink 设置中重试");
            return;
        }

        if (status == PackageInstaller.STATUS_SUCCESS) {
            UpdateInstallAttemptStore.finish(preferences, callback, status, "正式版更新安装完成");
            return;
        }
        String detail = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        if (status == PackageInstaller.STATUS_FAILURE_ABORTED) detail = "安装已取消，可在 TabLink 设置中重试";
        else if (detail == null || detail.trim().isEmpty()) detail = "Android 安装程序未完成更新";
        detail = detail.replaceAll("[\\p{Cntrl}]", " ").trim();
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
}
