package com.tablink.client;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.MessageDigest;
import java.security.Signature;
import java.security.interfaces.ECPublicKey;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.X509EncodedKeySpec;
import java.util.Base64;
import java.util.Locale;

/** Plain-JVM security, parsing, integrity and lifecycle tests for stable updates. */
public final class StableUpdateSecurityTest {
    private static final long NOW = 1790668800000L; // 2026-09-29T08:00:00Z
    private static final String PRODUCTION_SPKI = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A==";
    private static int checks;

    public static void main(String[] args) throws Exception {
        KeyPairGenerator generator = KeyPairGenerator.getInstance("EC");
        generator.initialize(new ECGenParameterSpec("secp256r1"));
        KeyPair signing = generator.generateKeyPair();
        String spki = Base64.getEncoder().encodeToString(signing.getPublic().getEncoded());
        String downloadUrl = "https://linjie.space/download/api/download?path=TabLink%2Fstable%2F0.8.0%2FTabLink-android-0.8.0.apk";
        String android = "{\"platform\":\"android\",\"version\":\"0.8.0\",\"build\":11,"
                + "\"url\":\"" + downloadUrl + "\",\"size\":5,"
                + "\"sha256\":\"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824\","
                + "\"notes\":\"稳定版\"}";
        String payload = payload("stable", 1, 100, android);
        byte[] envelope = sign(payload, signing);
        ReleaseManifest parsed = ReleaseManifest.verify(envelope, spki, NOW);
        check(parsed.androidArtifact().build == 11 && parsed.androidArtifact().version.equals("0.8.0"),
                "signed Android artifact selected");
        check(parsed.androidArtifact().url.equals(downloadUrl), "HTTPS DownloadSite query URL accepted");
        check(parsed.rolloutPercentage == 100 && parsed.includesInstallation("test-installation"),
                "full rollout includes installation");
        String cohortId = "0123456789abcdef0123456789abcdef";
        ReleaseManifest rollout20 = ReleaseManifest.verify(sign(payload("stable", 1, 20, android), signing), spki, NOW);
        ReleaseManifest rollout21 = ReleaseManifest.verify(sign(payload("stable", 1, 21, android), signing), spki, NOW);
        check(!rollout20.includesInstallation(cohortId) && rollout21.includesInstallation(cohortId),
                "cohort uses installationId newline releaseId and matches cross-platform bucket 20");
        check(parsed.artifacts.size() == 1 && parsed.androidArtifact().installerUrl == null,
                "optional fields stay optional");
        check(parsed.androidArtifact().isNewerThan(10, "0.7.1")
                        && !parsed.androidArtifact().isNewerThan(11, "0.7.1")
                        && !parsed.androidArtifact().isNewerThan(10, "0.9.0"),
                "both monotonically increasing versionCode and stable SemVer are required");

        String mirrorUrl = "https://github.com/linjierd/TabLink/releases/download/0.8.0/TabLink-android.apk";
        ReleaseManifest mirror = ReleaseManifest.verify(sign(
                payload("stable", 1, 100, android.replace(downloadUrl, mirrorUrl)), signing), spki, NOW);
        check(UpdateDecisionPolicy.sameDecision(parsed, mirror)
                        && UpdateDecisionPolicy.fingerprint(parsed).equals(UpdateDecisionPolicy.fingerprint(mirror)),
                "URL-only mirrors retain one canonical signed decision");
        ReleaseManifest changedNotes = ReleaseManifest.verify(sign(
                payload("stable", 1, 100, android.replace("稳定版", "不同说明")), signing), spki, NOW);
        check(!UpdateDecisionPolicy.sameDecision(parsed, changedNotes),
                "same-time semantic changes are not treated as mirrors");
        String notes4096 = "n".repeat(4096);
        ReleaseManifest notesAtSchemaLimit = ReleaseManifest.verify(sign(
                payload("stable", 1, 100, android.replace("稳定版", notes4096)), signing), spki, NOW);
        check(notesAtSchemaLimit.androidArtifact().notes.length() == 4096,
                "Android accepts the shared 4096-character release-notes limit");
        reject(sign(payload("stable", 1, 100, android.replace("稳定版", notes4096 + "n")), signing), spki, NOW,
                "release notes above the shared schema limit are rejected");
        ReleaseManifest futureProtocol = ReleaseManifest.verify(sign(
                payload("stable", 1, 100, android)
                        .replace("2026-09-29T08:00:00Z", "2026-09-29T08:01:00Z")
                        .replace("\"minimumProtocolVersion\":1", "\"minimumProtocolVersion\":2"), signing), spki, NOW);
        check(futureProtocol.minimumProtocolVersion == 2,
                "a signed future-protocol decision remains valid and authoritative");
        check(UpdateDecisionPolicy.selectAuthoritative(java.util.Arrays.asList(parsed, futureProtocol)) == futureProtocol,
                "a newer future-protocol source wins over an older installable source");
        UpdateDecisionPolicy.ConflictException sourceConflict = null;
        try { UpdateDecisionPolicy.selectAuthoritative(java.util.Arrays.asList(parsed, changedNotes)); }
        catch (UpdateDecisionPolicy.ConflictException expected) { sourceConflict = expected; }
        check(sourceConflict != null && sourceConflict.publishedAtUtcMs == parsed.publishedAtUtcMs,
                "authoritative source selection identifies the conflicting signed timestamp");
        final long[] persistedFloorUtc = {Long.MIN_VALUE};
        final String[] persistedFloorDecision = {null};
        UpdateDecisionPolicy.FloorState conflictFloor = UpdateDecisionPolicy.FloorState.empty();
        conflictFloor.block(sourceConflict.publishedAtUtcMs, (utc, decision) -> {
            persistedFloorUtc[0] = utc;
            persistedFloorDecision[0] = decision;
            return true;
        });
        boolean loneConflictingSourceBlocked = false;
        try { conflictFloor.require(parsed); }
        catch (IOException expected) { loneConflictingSourceBlocked = true; }
        check(loneConflictingSourceBlocked && conflictFloor.blocked(),
                "after a conflict, one remaining source at the same timestamp stays blocked");
        UpdateDecisionPolicy.FloorState restartedFloor = UpdateDecisionPolicy.FloorState.restore(
                true, persistedFloorUtc[0], true, persistedFloorDecision[0]);
        boolean restartStillBlocked = false;
        try { restartedFloor.require(parsed); }
        catch (IOException expected) { restartStillBlocked = true; }
        check(restartStillBlocked && restartedFloor.blocked(),
                "persisted conflict floor remains blocked after process restart");
        UpdateDecisionPolicy.FloorState failedUnblock = UpdateDecisionPolicy.FloorState.restore(
                true, persistedFloorUtc[0], true, persistedFloorDecision[0]);
        boolean unblockWriteFailed = false;
        try { failedUnblock.accept(futureProtocol, (utc, decision) -> false); }
        catch (IOException expected) { unblockWriteFailed = true; }
        check(unblockWriteFailed && failedUnblock.blocked(),
                "failed atomic cleanup/floor write cannot unblock the current process");
        restartedFloor.accept(futureProtocol, (utc, decision) -> {
            persistedFloorUtc[0] = utc;
            persistedFloorDecision[0] = decision;
            return true;
        });
        check(!restartedFloor.blocked() && persistedFloorUtc[0] == futureProtocol.publishedAtUtcMs,
                "only a newer valid signed decision atomically clears the conflict floor");
        UpdateDecisionPolicy.FloorState failedConflictWrite = UpdateDecisionPolicy.FloorState.empty();
        boolean conflictWriteFailed = false;
        try { failedConflictWrite.block(parsed.publishedAtUtcMs, (utc, decision) -> false); }
        catch (IOException expected) { conflictWriteFailed = true; }
        boolean sameProcessStillBlocked = false;
        try { failedConflictWrite.require(parsed); }
        catch (IOException expected) { sameProcessStillBlocked = true; }
        check(conflictWriteFailed && sameProcessStillBlocked && failedConflictWrite.blocked(),
                "failed conflict-floor persistence remains blocked in the current process");
        ReleaseManifest paused = ReleaseManifest.verify(sign(payload("stable", 1, 0, android), signing), spki, NOW);
        ReleaseManifest replayedOlder = ReleaseManifest.verify(sign(
                payload("stable", 1, 100, android).replace("2026-09-29T08:00:00Z", "2026-09-29T07:59:00Z"), signing), spki, NOW);
        boolean olderAfterConflictRejected = false;
        try { conflictFloor.require(replayedOlder); }
        catch (IOException expected) { olderAfterConflictRejected = true; }
        check(olderAfterConflictRejected, "conflict floor rejects every ordinary signed decision at or below T");
        boolean replayRejected = false;
        try {
            UpdateDecisionPolicy.requireAtOrAbove(paused.publishedAtUtcMs,
                    UpdateDecisionPolicy.fingerprint(paused), replayedOlder);
        } catch (IOException expected) { replayRejected = true; }
        check(replayRejected, "persisted newer pause rejects an older signed replay");

        byte[] tampered = envelope.clone();
        for (int i = 0; i < tampered.length; i++) if (tampered[i] == 'c') { tampered[i] = 'd'; break; }
        reject(tampered, spki, NOW, "signed envelope tampering rejected");
        KeyPair wrong = generator.generateKeyPair();
        reject(envelope, Base64.getEncoder().encodeToString(wrong.getPublic().getEncoded()), NOW,
                "wrong pinned key rejected");
        reject(sign(payload("beta", 1, 100, android), signing), spki, NOW, "non-stable channel rejected");
        reject(sign(payload("stable", 2, 100, android), signing), spki, NOW, "unknown schema rejected");
        reject(sign(payload("stable", 1, 100, android.replace("0.8.0", "0.8.0-beta.1")), signing), spki, NOW,
                "prerelease version rejected");
        reject(sign(payload("stable", 1, 100, android.replace("https://", "http://")), signing), spki, NOW,
                "non-HTTPS artifact rejected");
        reject(sign(payload("stable", 1, 100, android.replace("https://linjie.space", "https://user@linjie.space")), signing), spki, NOW,
                "artifact URL userinfo rejected");
        reject(sign(payload("stable", 1, 100, android.replace(downloadUrl, downloadUrl + "#fragment")), signing), spki, NOW,
                "artifact URL fragment rejected");
        reject(sign(payload("stable", 1, 100, android + "," + android), signing), spki, NOW,
                "duplicate platform rejected");
        reject(sign(payload("stable", 1, 101, android), signing), spki, NOW, "rollout above 100 rejected");
        reject(sign(payload("stable", 1, 100, android).replace("\"schema\":1", "\"schema\":1,\"schema\":1"), signing),
                spki, NOW, "duplicate JSON property rejected");
        reject(sign(payload.replace("2026-09-29T08:00:00Z", "2026-10-01T08:00:00Z"), signing), spki, NOW,
                "implausible future publication rejected");
        String fractional = payload.replace("2026-09-29T08:00:00Z", "2026-09-29T08:00:00.1234567Z");
        reject(sign(fractional, signing), spki, NOW,
                "fractional UTC timestamp rejected so every platform uses exact signed seconds");

        ECPublicKey production = (ECPublicKey) KeyFactory.getInstance("EC").generatePublic(
                new X509EncodedKeySpec(Base64.getDecoder().decode(PRODUCTION_SPKI)));
        check(production.getParams().getCurve().getField().getFieldSize() == 256, "pinned production key is P-256");
        check(hex(MessageDigest.getInstance("SHA-256").digest(production.getEncoded())).equals(
                "016aab1632a4e78705d9726e0a4ac7452568b69e2a36fcbf4baeea3b23856534"),
                "pinned production SPKI exact checksum retained");
        if (args.length == 2) productionFixture(args[0], args[1]);

        File file = File.createTempFile("tablink-update-", ".apk");
        try {
            try (FileOutputStream output = new FileOutputStream(file)) { output.write("hello".getBytes(StandardCharsets.UTF_8)); }
            check(ArtifactIntegrity.verify(file, 5,
                    "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"),
                    "exact size and SHA-256 accepted");
            check(!ArtifactIntegrity.verify(file, 4,
                    "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"),
                    "size mismatch rejected");
            check(!ArtifactIntegrity.verify(file, 5,
                    "3cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"),
                    "hash mismatch rejected");
        } finally { file.delete(); }
        check(ArtifactIntegrity.safeApkName(11).equals("TabLink-android-stable-11.apk"), "safe path is build-derived");
        check(ArtifactIntegrity.isSafeApkName("TabLink-android-stable-11.apk")
                        && !ArtifactIntegrity.isSafeApkName("../TabLink-android-stable-11.apk")
                        && !ArtifactIntegrity.isSafeApkName("TabLink-last-working.apk"),
                "staged-package allowlist excludes traversal and rollback archive");

        stateMachine();
        System.out.println("PASS: " + checks + " stable-update security and state assertions");
    }

    private static void stateMachine() {
        UpdateStateMachine state = new UpdateStateMachine();
        check(UpdateModePreference.parse(null, null, false) == UpdateStateMachine.Mode.AUTOMATIC,
                "missing update preference defaults to automatic");
        check(UpdateModePreference.parse(UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK.name(), null, false)
                        == UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK,
                "canonical download-then-ask preference is accepted");
        check(UpdateModePreference.parse(null, true, false) == UpdateStateMachine.Mode.AUTOMATIC
                        && UpdateModePreference.parse(null, false, false)
                                == UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK,
                "legacy auto-download migrates true to automatic and false to download-then-ask");
        check(UpdateModePreference.parse("damaged", null, false) == UpdateStateMachine.Mode.NEVER
                        && UpdateModePreference.parse(null, null, true) == UpdateStateMachine.Mode.NEVER
                        && !UpdateModePreference.installsAllowed(UpdateStateMachine.Mode.NEVER.name(), null, false),
                "damaged or never preferences fail closed before installer callbacks");
        UpdateModePreference.setRuntimeMode(UpdateStateMachine.Mode.NEVER);
        check(!UpdateModePreference.installsAllowed(UpdateStateMachine.Mode.AUTOMATIC.name(), null, false),
                "runtime never mode blocks a pending installer callback even if preference persistence failed");
        UpdateModePreference.setRuntimeMode(UpdateStateMachine.Mode.AUTOMATIC);
        UpdateInstallerUiGate.resetForTests();
        Object displayOwner = new Object();
        Object permissionOwner = new Object();
        Object stalePermissionOwner = new Object();
        check(UpdateInstallerUiGate.tryActivateDisplay(displayOwner)
                        && UpdateInstallerUiGate.tryLaunchPermissionUi(permissionOwner, () -> { })
                                == UpdateInstallerUiGate.LaunchResult.BLOCKED_BY_DISPLAY,
                "an active display blocks Android update system UI");
        UpdateInstallerUiGate.deactivateDisplay(displayOwner);
        check(UpdateInstallerUiGate.tryLaunchPermissionUi(permissionOwner, () -> { })
                                == UpdateInstallerUiGate.LaunchResult.LAUNCHED
                        && !UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "a launched update system UI blocks a newly connected display");
        UpdateInstallerUiGate.permissionUiFinished(stalePermissionOwner);
        check(!UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "a stale controller cannot release another controller's permission UI gate");
        UpdateInstallerUiGate.permissionUiFinished(permissionOwner);
        check(UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "returning or destroying the owner releases its permission UI gate");
        UpdateInstallerUiGate.deactivateDisplay(displayOwner);
        UpdateInstallAttempt firstAttempt = UpdateInstallAttempt.validated(41, "a".repeat(64));
        UpdateInstallAttempt staleAttempt = UpdateInstallAttempt.validated(40, "b".repeat(64));
        check(UpdateInstallerUiGate.tryBeginInstallerTransaction(firstAttempt)
                        && !UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "a committed installer transaction blocks display activation");
        UpdateInstallerUiGate.installerTransactionFinished(staleAttempt);
        check(!UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "a stale callback cannot clear the current installer gate");
        check(UpdateInstallerUiGate.tryLaunchInstallerConfirmation(firstAttempt, () -> { })
                                == UpdateInstallerUiGate.LaunchResult.LAUNCHED,
                "only the current attempt can launch installer confirmation");
        UpdateInstallerUiGate.installerTransactionFinished(firstAttempt);
        check(UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "never-mode cancellation clears the matching installer gate before returning");
        UpdateInstallerUiGate.deactivateDisplay(displayOwner);
        UpdateInstallerUiGate.resetForTests();
        check(UpdateInstallerUiGate.restoreInstallerTransaction(firstAttempt)
                        && !UpdateInstallerUiGate.tryActivateDisplay(displayOwner),
                "a persisted pending installer confirmation restores the display gate after process recreation");
        UpdateInstallerUiGate.installerTransactionFinished(firstAttempt);
        check(state.sessionChanged(true) == UpdateStateMachine.Action.NONE, "active display registered");
        state.beginCheck();
        check(state.checked(true) == UpdateStateMachine.Action.NONE
                && state.state() == UpdateStateMachine.State.AVAILABLE, "active display defers automatic download");
        check(state.sessionChanged(false) == UpdateStateMachine.Action.DOWNLOAD
                && state.state() == UpdateStateMachine.State.DOWNLOADING, "disconnect starts deferred download");
        state.downloadDeferred(true);
        check(state.state() == UpdateStateMachine.State.AVAILABLE
                        && state.sessionChanged(false) == UpdateStateMachine.Action.DOWNLOAD,
                "session arriving mid-download causes a retry after disconnect");
        check(state.downloaded() == UpdateStateMachine.Action.START_INSTALL,
                "completed automatic download starts the Android installer while idle");
        check(state.sessionChanged(true) == UpdateStateMachine.Action.NONE
                && state.state() == UpdateStateMachine.State.READY_DEFERRED, "new display session defers staged install");
        check(!state.beginInstall(false, true), "automatic install cannot interrupt display session");
        check(state.beginInstall(true, false) && state.state() == UpdateStateMachine.State.AWAITING_PERMISSION,
                "explicit install waits for unknown-sources permission");
        state.installPermissionReturned(true);
        check(state.state() == UpdateStateMachine.State.READY_TO_INSTALL, "permission return makes update installable");
        check(state.beginInstall(true, true), "explicit retry invokes the system installer");
        state.installResult(UpdateStateMachine.InstallResult.PENDING_USER_ACTION);
        check(state.state() == UpdateStateMachine.State.INSTALLING,
                "PackageInstaller pending-user-action remains in progress");
        state.installResult(UpdateStateMachine.InstallResult.RETRY);
        check(state.state() == UpdateStateMachine.State.READY_TO_INSTALL,
                "cancelled system installer remains retryable without an automatic prompt loop");
        check(state.beginInstall(true, true), "retry can resubmit the verified staged package");
        state.installResult(UpdateStateMachine.InstallResult.SUCCESS);
        check(state.state() == UpdateStateMachine.State.IDLE && !state.hasStagedUpdate(),
                "PackageInstaller success clears the pending state");
        state.restoreInstalling();
        check(state.state() == UpdateStateMachine.State.INSTALLING && state.hasStagedUpdate(),
                "a persisted current attempt restores installer state after process recreation");
        state.installResult(UpdateStateMachine.InstallResult.RETRY);
        UpdateStateMachine automaticInstall = new UpdateStateMachine();
        automaticInstall.beginCheck();
        check(automaticInstall.checked(true) == UpdateStateMachine.Action.DOWNLOAD
                        && automaticInstall.downloaded() == UpdateStateMachine.Action.START_INSTALL
                        && automaticInstall.beginInstall(false, true),
                "automatic mode can submit a staged idle package without an extra in-app confirmation");
        state.reset();
        state.setMode(UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK);
        state.sessionChanged(false);
        state.beginCheck();
        check(state.checked(true) == UpdateStateMachine.Action.DOWNLOAD,
                "download-then-ask still downloads while idle");
        check(state.downloaded() == UpdateStateMachine.Action.NONE
                        && state.state() == UpdateStateMachine.State.READY_TO_INSTALL,
                "download-then-ask stages without prompting the installer");
        check(state.beginInstall(true, true), "download-then-ask permits an explicit install");
        state.reset();
        state.setMode(UpdateStateMachine.Mode.NEVER);
        state.beginCheck();
        check(!state.checksEnabled() && state.state() == UpdateStateMachine.State.DISABLED,
                "never mode disables update checks");
        check(state.checked(true) == UpdateStateMachine.Action.NONE
                        && state.requestDownload() == UpdateStateMachine.Action.NONE
                        && !state.beginInstall(true, true),
                "never mode blocks download and install actions");
        check(state.downloaded() == UpdateStateMachine.Action.NONE
                        && state.sessionChanged(false) == UpdateStateMachine.Action.NONE
                        && state.state() == UpdateStateMachine.State.DISABLED,
                "never mode stays disabled across download and session callbacks");
        state.installPermissionReturned(true);
        state.installerReturned();
        state.installResult(UpdateStateMachine.InstallResult.RETRY);
        state.failed();
        check(state.state() == UpdateStateMachine.State.DISABLED,
                "never mode stays disabled across installer and failure callbacks");
        state.installResult(UpdateStateMachine.InstallResult.SUCCESS);
        check(state.state() == UpdateStateMachine.State.DISABLED && !state.hasStagedUpdate(),
                "installer success clears staged state without leaving never mode");
        state.setMode(UpdateStateMachine.Mode.AUTOMATIC);
        check(state.checksEnabled() && state.state() == UpdateStateMachine.State.IDLE,
                "automatic mode can be restored after never mode");
    }

    private static void productionFixture(String payloadPath, String envelopePath) throws Exception {
        byte[] expectedPayload = Files.readAllBytes(Paths.get(payloadPath));
        byte[] envelope = Files.readAllBytes(Paths.get(envelopePath));
        ReleaseManifest release = ReleaseManifest.verify(envelope, PRODUCTION_SPKI, 1790692500000L);
        check(release.androidArtifact() != null && release.androidArtifact().build == 11,
                "offline production key validates cross-platform fixture");
        String text = new String(envelope, StandardCharsets.UTF_8);
        java.util.regex.Matcher payload = java.util.regex.Pattern.compile(
                "\\\"payload\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"").matcher(text);
        check(payload.find() && java.util.Arrays.equals(Base64.getDecoder().decode(payload.group(1)
                        .replace("\\u002B", "+").replace("\\u002F", "/").replace("\\u003D", "=")), expectedPayload),
                "production fixture signs the exact payload bytes");
        byte[] modified = envelope.clone(); modified[modified.length / 2] ^= 1;
        reject(modified, PRODUCTION_SPKI, 1790692500000L, "production fixture tampering rejected");
    }

    private static String payload(String channel, int schema, int rollout, String artifacts) {
        return "{\"schema\":" + schema + ",\"channel\":\"" + channel + "\","
                + "\"releaseId\":\"stable-0.8.0\",\"publishedAtUtc\":\"2026-09-29T08:00:00Z\","
                + "\"rolloutPercentage\":" + rollout + ",\"minimumProtocolVersion\":1,"
                + "\"artifacts\":[" + artifacts + "]}";
    }

    private static byte[] sign(String payload, KeyPair key) throws Exception {
        byte[] body = payload.getBytes(StandardCharsets.UTF_8);
        Signature signer = Signature.getInstance("SHA256withECDSA"); signer.initSign(key.getPrivate()); signer.update(body);
        return ("{\"payload\":\"" + Base64.getEncoder().encodeToString(body) + "\",\"signature\":\""
                + Base64.getEncoder().encodeToString(signer.sign()) + "\"}").getBytes(StandardCharsets.UTF_8);
    }
    private static void reject(byte[] envelope, String spki, long now, String message) throws Exception {
        try { ReleaseManifest.verify(envelope, spki, now); throw new AssertionError(message); }
        catch (ReleaseManifest.ValidationException expected) { checks++; }
    }
    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder();
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 255));
        return result.toString();
    }
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message); checks++;
    }
}
