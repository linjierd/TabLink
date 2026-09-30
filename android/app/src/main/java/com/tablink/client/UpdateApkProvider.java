package com.tablink.client;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;

/** Read-only, grant-only provider exposing one verified APK to Android's package installer. */
public final class UpdateApkProvider extends ContentProvider {
    public static final String AUTHORITY = "com.tablink.client.updates";

    @Override public boolean onCreate() { return true; }

    @Override public String getType(Uri uri) {
        return resolve(uri) == null ? null : "application/vnd.android.package-archive";
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection,
                                  String[] selectionArgs, String sortOrder) {
        File file = resolve(uri);
        if (file == null) return null;
        String[] columns = projection == null ? new String[] {OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE} : projection;
        MatrixCursor cursor = new MatrixCursor(columns, 1);
        MatrixCursor.RowBuilder row = cursor.newRow();
        for (String column : columns) {
            if (OpenableColumns.DISPLAY_NAME.equals(column)) row.add(file.getName());
            else if (OpenableColumns.SIZE.equals(column)) row.add(file.length());
            else row.add(null);
        }
        return cursor;
    }

    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException("只允许读取更新包");
        File file = resolve(uri);
        if (file == null) throw new FileNotFoundException("更新包不存在");
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
    }

    private File resolve(Uri uri) {
        if (getContext() == null || uri == null || !"content".equals(uri.getScheme())
                || !AUTHORITY.equals(uri.getAuthority()) || uri.getPathSegments().size() != 2
                || !"apk".equals(uri.getPathSegments().get(0))) return null;
        String name = uri.getPathSegments().get(1);
        if (!ArtifactIntegrity.isSafeApkName(name)) return null;
        File directory = new File(getContext().getFilesDir(), "updates");
        File candidate = new File(directory, name);
        try {
            if (!candidate.getCanonicalFile().getParentFile().equals(directory.getCanonicalFile())
                    || !candidate.isFile()) return null;
        } catch (Exception failure) { return null; }
        return candidate;
    }

    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException("只读"); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        throw new UnsupportedOperationException("只读");
    }
}
