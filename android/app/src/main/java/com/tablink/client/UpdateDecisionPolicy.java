package com.tablink.client;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;

/** Pure policy for comparing signed mirror decisions and preventing manifest replay. */
final class UpdateDecisionPolicy {
    static final String BLOCKED_CONFLICT = "blocked-source-conflict-v1";

    static final class ConflictException extends IOException {
        final long publishedAtUtcMs;
        ConflictException(long publishedAtUtcMs) {
            super("两个更新源对同一发布时间给出了冲突的签名清单");
            this.publishedAtUtcMs = publishedAtUtcMs;
        }
    }

    interface FloorWriter {
        boolean write(long publishedAtUtcMs, String decision) throws IOException;
    }

    /** In-memory floor stays blocked if persistence fails, preventing same-process fallback. */
    static final class FloorState {
        private long publishedAtUtcMs;
        private String decision;

        private FloorState(long publishedAtUtcMs, String decision) {
            this.publishedAtUtcMs = publishedAtUtcMs;
            this.decision = decision;
        }

        static FloorState empty() { return new FloorState(Long.MIN_VALUE, null); }

        static FloorState restore(boolean hasUtc, Object rawUtc,
                boolean hasDecision, Object rawDecision) throws IOException {
            if (!hasUtc && !hasDecision) return empty();
            if (hasUtc != hasDecision || !(rawUtc instanceof Long) || !(rawDecision instanceof String))
                throw new IOException("更新防回放记录无效");
            long utc = (Long) rawUtc;
            String marker = (String) rawDecision;
            if (utc < 0 || !validDecisionMarker(marker)) throw new IOException("更新防回放记录无效");
            return new FloorState(utc, marker);
        }

        static FloorState failClosed() { return new FloorState(Long.MAX_VALUE, BLOCKED_CONFLICT); }

        synchronized void require(ReleaseManifest manifest) throws IOException {
            if (decision != null) requireAtOrAbove(publishedAtUtcMs, decision, manifest);
        }

        synchronized void block(long conflictUtc, FloorWriter writer) throws IOException {
            if (conflictUtc < 0) throw new IOException("冲突清单时间无效");
            if (publishedAtUtcMs > conflictUtc) return;
            publishedAtUtcMs = conflictUtc;
            decision = BLOCKED_CONFLICT;
            boolean saved;
            try { saved = writer.write(publishedAtUtcMs, decision); }
            catch (IOException failure) { throw failure; }
            catch (RuntimeException failure) { throw new IOException("无法保存更新冲突阻断记录", failure); }
            if (!saved) throw new IOException("无法保存更新冲突阻断记录");
        }

        synchronized void accept(ReleaseManifest manifest, FloorWriter writer) throws IOException {
            require(manifest);
            String accepted = fingerprint(manifest);
            if (manifest.publishedAtUtcMs == publishedAtUtcMs && accepted.equals(decision)) return;
            boolean saved;
            try { saved = writer.write(manifest.publishedAtUtcMs, accepted); }
            catch (IOException failure) { throw failure; }
            catch (RuntimeException failure) { throw new IOException("无法保存更新防回放记录", failure); }
            if (!saved) throw new IOException("无法保存更新防回放记录");
            publishedAtUtcMs = manifest.publishedAtUtcMs;
            decision = accepted;
        }

        synchronized long publishedAtUtcMs() { return publishedAtUtcMs; }
        synchronized String decision() { return decision; }
        synchronized boolean blocked() { return BLOCKED_CONFLICT.equals(decision); }
    }

    private UpdateDecisionPolicy() { }

    static ReleaseManifest selectAuthoritative(List<ReleaseManifest> manifests) throws IOException {
        if (manifests == null || manifests.isEmpty()) throw new IOException("没有可用的已签名更新决定");
        ReleaseManifest selected = null;
        for (ReleaseManifest manifest : manifests) {
            if (manifest == null) throw new IOException("更新决定为空");
            if (selected == null || manifest.publishedAtUtcMs > selected.publishedAtUtcMs) {
                selected = manifest;
            } else if (manifest.publishedAtUtcMs == selected.publishedAtUtcMs
                    && !sameDecision(selected, manifest)) {
                throw new ConflictException(manifest.publishedAtUtcMs);
            }
        }
        // Recheck every newest mirror because a later manifest can replace the
        // provisional selection made earlier in the first pass.
        for (ReleaseManifest manifest : manifests) {
            if (manifest.publishedAtUtcMs == selected.publishedAtUtcMs
                    && !sameDecision(selected, manifest)) {
                throw new ConflictException(manifest.publishedAtUtcMs);
            }
        }
        return selected;
    }

    static boolean sameDecision(ReleaseManifest first, ReleaseManifest second) {
        if (first == null || second == null || !first.releaseId.equals(second.releaseId)
                || first.publishedAtUtcMs != second.publishedAtUtcMs
                || first.rolloutPercentage != second.rolloutPercentage
                || first.minimumProtocolVersion != second.minimumProtocolVersion
                || first.artifacts.size() != second.artifacts.size()) return false;
        ArrayList<ReleaseManifest.Artifact> left = sorted(first);
        ArrayList<ReleaseManifest.Artifact> right = sorted(second);
        for (int index = 0; index < left.size(); index++) {
            ReleaseManifest.Artifact a = left.get(index), b = right.get(index);
            if (!sameIdentity(a, b) || !sameNullable(a.installerUrl, b.installerUrl)
                    || !a.notes.equals(b.notes)) return false;
        }
        return true;
    }

    static String fingerprint(ReleaseManifest manifest) throws IOException {
        if (manifest == null) throw new IOException("更新决定为空");
        try {
            StringBuilder canonical = new StringBuilder();
            append(canonical, manifest.releaseId);
            append(canonical, Long.toString(manifest.publishedAtUtcMs));
            append(canonical, Integer.toString(manifest.rolloutPercentage));
            append(canonical, Integer.toString(manifest.minimumProtocolVersion));
            for (ReleaseManifest.Artifact artifact : sorted(manifest)) {
                append(canonical, artifact.platform);
                append(canonical, artifact.version);
                append(canonical, Integer.toString(artifact.build));
                append(canonical, Long.toString(artifact.size));
                append(canonical, artifact.sha256);
                append(canonical, artifact.installerUrl);
                append(canonical, artifact.notes);
            }
            return hex(MessageDigest.getInstance("SHA-256").digest(
                    canonical.toString().getBytes(StandardCharsets.UTF_8)));
        } catch (Exception failure) { throw new IOException("无法计算更新决定摘要", failure); }
    }

    static void requireAtOrAbove(long floorUtc, String floorDecision,
                                 ReleaseManifest candidate) throws IOException {
        if (floorDecision == null || floorDecision.isEmpty()) throw new IOException("更新防回放记录不完整");
        if (BLOCKED_CONFLICT.equals(floorDecision)) {
            if (candidate.publishedAtUtcMs <= floorUtc)
                throw new IOException("更新源冲突仍被阻断；需要发布时间更新的有效签名决定");
            return;
        }
        if (candidate.publishedAtUtcMs < floorUtc)
            throw new IOException("更新清单早于本机已接受的签名决定");
        if (candidate.publishedAtUtcMs == floorUtc && !fingerprint(candidate).equals(floorDecision))
            throw new IOException("更新清单与本机同一时间的签名决定冲突");
    }

    private static boolean validDecisionMarker(String marker) {
        if (BLOCKED_CONFLICT.equals(marker)) return true;
        if (marker == null || marker.length() != 64) return false;
        for (int i = 0; i < marker.length(); i++) {
            char value = marker.charAt(i);
            if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) return false;
        }
        return true;
    }

    private static ArrayList<ReleaseManifest.Artifact> sorted(ReleaseManifest manifest) {
        ArrayList<ReleaseManifest.Artifact> result = new ArrayList<>(manifest.artifacts);
        Collections.sort(result, (left, right) -> left.platform.compareTo(right.platform));
        return result;
    }

    private static boolean sameIdentity(ReleaseManifest.Artifact first, ReleaseManifest.Artifact second) {
        return first.platform.equals(second.platform) && first.version.equals(second.version)
                && first.build == second.build && first.size == second.size
                && first.sha256.equals(second.sha256);
    }

    private static boolean sameNullable(String first, String second) {
        return first == null ? second == null : first.equals(second);
    }

    private static void append(StringBuilder target, String value) {
        if (value == null) target.append("-1:");
        else target.append(value.length()).append(':').append(value);
        target.append(';');
    }

    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 255));
        return result.toString();
    }
}
