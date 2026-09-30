package com.tablink.client;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageInstaller;
import android.os.Build;

/** PackageInstaller callback, including the mandatory user-action handoff when Android requests it. */
public final class UpdateInstallReceiver extends BroadcastReceiver {
    static final String ACTION = "com.tablink.client.UPDATE_INSTALL_STATUS";
    static final String PREFS = "stable-updates";
    static final String RESULT_STATUS = "install-result-status";
    static final String RESULT_MESSAGE = "install-result-message";

    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null || !ACTION.equals(intent.getAction())) return;
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
                save(context, status, "Android 正在等待安装确认");
                try { context.startActivity(confirmation); return; }
                catch (RuntimeException ignored) { }
            }
            save(context, PackageInstaller.STATUS_FAILURE,
                    "无法打开 Android 安装确认页，请在 TabLink 设置中重试");
            return;
        }
        if (status == PackageInstaller.STATUS_SUCCESS) {
            save(context, status, "正式版更新安装完成");
            return;
        }
        String detail = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        if (status == PackageInstaller.STATUS_FAILURE_ABORTED) detail = "安装已取消，可在 TabLink 设置中重试";
        else if (detail == null || detail.trim().isEmpty()) detail = "Android 安装程序未完成更新";
        detail = detail.replaceAll("[\\p{Cntrl}]", " ").trim();
        if (detail.length() > 100) detail = detail.substring(0, 100);
        save(context, status, detail);
    }

    private static void save(Context context, int status, String message) {
        SharedPreferences preferences = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        preferences.edit().putString(RESULT_MESSAGE, message).putInt(RESULT_STATUS, status).apply();
    }
}
