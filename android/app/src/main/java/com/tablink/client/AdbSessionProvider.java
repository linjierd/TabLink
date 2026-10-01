package com.tablink.client;

import android.Manifest;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;

import java.util.Arrays;
import java.util.HashSet;

/** ADB-shell-only writer for short-lived, one-shot USB session configuration. */
public final class AdbSessionProvider extends ContentProvider {
    static final String URI = "content://com.tablink.client.adb/session";
    private static final HashSet<String> KEYS = new HashSet<>(
            Arrays.asList("activation", "token", "port"));

    @Override public boolean onCreate() { return true; }

    @Override public Uri insert(Uri uri, ContentValues values) {
        if (getContext() == null) throw new IllegalStateException("Provider is not attached");
        getContext().enforceCallingPermission(Manifest.permission.DUMP,
                "ADB session configuration requires android.permission.DUMP");
        if (!URI.equals(uri.toString()) || values == null || !values.keySet().equals(KEYS))
            throw new IllegalArgumentException("Only exact ADB session configuration is supported");
        String activation = values.getAsString("activation");
        String token = values.getAsString("token");
        Integer port = values.getAsInteger("port");
        if (port == null) throw new IllegalArgumentException("ADB session port is missing");
        AdbSessionConfiguration.publish(activation, token, port);
        new Handler(Looper.getMainLooper()).postDelayed(
                () -> AdbSessionConfiguration.expire(activation),
                AdbSessionConfiguration.MAX_AGE_MILLIS);
        return Uri.parse(URI + "/pending");
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection,
                                  String[] selectionArgs, String sortOrder) {
        throw new UnsupportedOperationException("Write only");
    }
    @Override public String getType(Uri uri) { return null; }
    @Override public int delete(Uri uri, String selection, String[] args) {
        throw new UnsupportedOperationException("Write only");
    }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] args) {
        throw new UnsupportedOperationException("Write only");
    }
}
