package com.tablink.client;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;

/** ADB/system-only display and pacing diagnostics guarded by android.permission.DUMP. */
public final class DisplayCapabilitiesProvider extends ContentProvider {
    private static final String URI = "content://com.tablink.client.display/capabilities";
    private static final String PACING_URI = "content://com.tablink.client.display/pacing";
    @Override public boolean onCreate() { return true; }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        boolean pacing = PACING_URI.equals(uri.toString());
        if ((!URI.equals(uri.toString()) && !pacing) || selection != null || selectionArgs != null || sortOrder != null)
            throw new IllegalArgumentException("Only read-only display and pacing rows are supported");
        if (projection != null && (projection.length != 1 || !"json".equals(projection[0])))
            throw new IllegalArgumentException("Only the json column is available");
        MatrixCursor cursor = new MatrixCursor(new String[] { "json" }, 1);
        cursor.addRow(new Object[] { pacing ? VideoDecoder.readPacingDiagnostics() : DisplayCapabilities.read(getContext()).toString() });
        return cursor;
    }
    @Override public String getType(Uri uri) { return "vnd.android.cursor.item/vnd.tablink.display-capabilities"; }
    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException("Read only"); }
    @Override public int delete(Uri uri, String selection, String[] args) { throw new UnsupportedOperationException("Read only"); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] args) { throw new UnsupportedOperationException("Read only"); }
}
