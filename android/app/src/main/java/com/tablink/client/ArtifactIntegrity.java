package com.tablink.client;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.Locale;

/** Exact size and SHA-256 verification shared by download code and plain-JVM tests. */
public final class ArtifactIntegrity {
    private ArtifactIntegrity() { }

    public static boolean verify(File file, long expectedSize, String expectedSha256) throws IOException {
        if (file == null || !file.isFile() || expectedSize < 1 || file.length() != expectedSize
                || expectedSha256 == null || !expectedSha256.matches("[0-9a-fA-F]{64}")) return false;
        MessageDigest digest;
        try { digest = MessageDigest.getInstance("SHA-256"); }
        catch (Exception impossible) { throw new IOException("SHA-256 不可用", impossible); }
        try (InputStream input = new FileInputStream(file)) {
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            while (true) {
                int count = input.read(buffer);
                if (count < 0) break;
                if (count == 0) continue;
                total += count;
                if (total > expectedSize) return false;
                digest.update(buffer, 0, count);
            }
            return total == expectedSize && hex(digest.digest()).equals(expectedSha256.toLowerCase(Locale.ROOT));
        }
    }

    public static String safeApkName(int build) {
        if (build < 1) throw new IllegalArgumentException("build must be positive");
        return "TabLink-android-stable-" + build + ".apk";
    }

    public static boolean isSafeApkName(String name) {
        return name != null && name.matches("TabLink-android-stable-[1-9][0-9]{0,9}\\.apk");
    }

    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 255));
        return result.toString();
    }
}
