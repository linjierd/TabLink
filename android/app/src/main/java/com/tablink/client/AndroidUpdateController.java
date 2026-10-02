package com.tablink.client;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.HttpsURLConnection;

/** Stable-channel checker/downloader. Installation always remains an Android-confirmed action. */
final class AndroidUpdateController implements AutoCloseable {
    interface Host {
        boolean isForeground();
        boolean hasActiveDisplaySession();
        void updateStatusChanged(String status);
        void offerInstall(ReleaseManifest.Artifact artifact, boolean activeSession);
        void offerDisconnectForDownload(ReleaseManifest.Artifact artifact);
    }

    private static final long PERIOD_MS = 6L * 60 * 60 * 1000;
    private static final String PREFS = UpdateModePreference.PREFERENCES;
    private static final String MODE = UpdateModePreference.MODE_KEY;
    private static final String LEGACY_AUTO = UpdateModePreference.LEGACY_AUTO_KEY;
    private static final String MANIFEST_FLOOR_UTC = "manifest-floor-utc";
    private static final String MANIFEST_FLOOR_DECISION = "manifest-floor-decision";
    private static final String INSTALLATION = "installation-id";
    private static final String MANIFEST_FILE = "pending-manifest.json";
    private static final String LAST_WORKING_FILE = "TabLink-last-working.apk";
    private final Activity activity;
    private final Host host;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor(r -> {
        Thread thread = new Thread(r, "TabLink-stable-update");
        thread.setDaemon(true);
        return thread;
    });
    private final AtomicBoolean operationRunning = new AtomicBoolean();
    private final Object policyLock = new Object();
    private final UpdateStateMachine machine = new UpdateStateMachine();
    private final SharedPreferences preferences;
    private final UpdateDecisionPolicy.FloorState manifestFloor;
    private final SharedPreferences.OnSharedPreferenceChangeListener installResultListener;
    private final File updateDirectory;
    private volatile boolean foreground;
    private volatile boolean closed;
    private volatile long policyGeneration;
    private volatile boolean refreshRequested;
    private volatile boolean refreshExplicit;
    private boolean installRequested;
    private boolean installRequestedExplicit;
    private Boolean permissionInstallExplicit;
    private volatile String status;
    private volatile ReleaseManifest.Artifact available;
    private final Runnable periodic = new Runnable() {
        @Override public void run() {
            if (!foreground || closed) return;
            queueRefresh(false);
            if (machine.checksEnabled()) ui.postDelayed(this, PERIOD_MS);
        }
    };

    AndroidUpdateController(Activity activity, Host host) {
        this.activity = activity;
        this.host = host;
        preferences = activity.getSharedPreferences(PREFS, Activity.MODE_PRIVATE);
        manifestFloor = loadManifestFloor();
        installResultListener = (ignored, key) -> {
            if (UpdateInstallAttempt.RESULT_STATUS.equals(key)) consumeInstallResult();
        };
        preferences.registerOnSharedPreferenceChangeListener(installResultListener);
        updateDirectory = new File(activity.getFilesDir(), "updates");
        UpdateStateMachine.Mode initialMode = loadUpdateMode();
        machine.setMode(initialMode);
        UpdateModePreference.setRuntimeMode(initialMode);
        boolean manifestConflictBlocked = manifestFloor.blocked();
        UpdateInstallAttemptStore.Recovery recovery = UpdateInstallAttemptStore.recover(
                preferences, PackageInstaller.STATUS_PENDING_USER_ACTION);
        if (recovery.abandonSessionId >= 0) abandonSession(recovery.abandonSessionId);
        if (manifestConflictBlocked) {
            // The conflict marker is persisted before an in-flight installer is
            // cancelled. Finish that transaction after a crash between those
            // writes, and never restore its display gate or staged package.
            UpdateInstallAttemptStore.Recovery blocked = UpdateInstallAttemptStore.clearAll(preferences);
            if (blocked.abandonSessionId >= 0) abandonSession(blocked.abandonSessionId);
            recovery = blocked;
            invalidateStaged();
        }
        if (initialMode == UpdateStateMachine.Mode.NEVER) {
            UpdateInstallAttemptStore.Recovery disabled = UpdateInstallAttemptStore.disableAndClear(preferences);
            if (disabled.abandonSessionId >= 0) abandonSession(disabled.abandonSessionId);
            recovery = disabled;
        } else if (!manifestConflictBlocked && recovery.current != null) {
            if (installerSessionExists(recovery.current)) {
                UpdateInstallerUiGate.restoreInstallerTransaction(recovery.current);
                machine.restoreInstalling();
            } else {
                UpdateInstallAttempt stale = recovery.current;
                boolean discarded = UpdateInstallAttemptStore.discardIfCurrent(
                        preferences, stale, true);
                abandonSession(stale.sessionId);
                recovery = new UpdateInstallAttemptStore.Recovery(
                        null, null, true, -1, !discarded);
                machine.installResult(UpdateStateMachine.InstallResult.RETRY);
            }
        } else if (!manifestConflictBlocked && recovery.retryRequired)
            machine.installResult(UpdateStateMachine.InstallResult.RETRY);
        status = manifestConflictBlocked
                ? "更新源冲突仍被阻断；等待发布时间更新的有效签名决定"
                : recovery.result != null ? recovery.result.message
                : recovery.retryRequired ? "Android 安装状态记录已损坏；已清理，可重试安装"
                : recovery.current != null ? "Android 安装仍在进行；正在等待系统结果"
                : configured() ? "尚未检查正式版更新" : "更新服务尚未配置";
        worker.execute(this::cleanInterruptedDownloads);
    }

    UpdateStateMachine.Mode updateMode() { return machine.mode(); }

    private UpdateDecisionPolicy.FloorState loadManifestFloor() {
        try {
            Map<String, ?> values = preferences.getAll();
            return UpdateDecisionPolicy.FloorState.restore(
                    values.containsKey(MANIFEST_FLOOR_UTC), values.get(MANIFEST_FLOOR_UTC),
                    values.containsKey(MANIFEST_FLOOR_DECISION), values.get(MANIFEST_FLOOR_DECISION));
        } catch (RuntimeException | IOException invalid) {
            return UpdateDecisionPolicy.FloorState.failClosed();
        }
    }

    private UpdateStateMachine.Mode loadUpdateMode() {
        UpdateModePreference.Selection selection;
        try {
            selection = UpdateModePreference.read(preferences.getAll());
        } catch (RuntimeException invalid) {
            selection = new UpdateModePreference.Selection(UpdateStateMachine.Mode.NEVER, true, false);
        }
        if (!selection.damaged && !selection.migrateLegacy) return selection.mode;
        UpdateStateMachine.Mode persisted = selection.damaged ? UpdateStateMachine.Mode.NEVER : selection.mode;
        try {
            if (preferences.edit().putString(MODE, persisted.name()).remove(LEGACY_AUTO).commit())
                return persisted;
        } catch (RuntimeException ignored) { }
        return UpdateStateMachine.Mode.NEVER;
    }

    UpdateStateMachine.Mode setUpdateMode(UpdateStateMachine.Mode mode) {
        UpdateStateMachine.Mode requested = mode == null ? UpdateStateMachine.Mode.NEVER : mode;
        // Stop the old generation before touching storage. No download can be
        // committed while a new policy is still uncertain.
        UpdateInstallAttemptStore.Recovery cleared = null;
        synchronized (policyLock) {
            policyGeneration++;
            machine.setMode(UpdateStateMachine.Mode.NEVER);
            UpdateModePreference.setRuntimeMode(UpdateStateMachine.Mode.NEVER);
            refreshRequested = false;
            refreshExplicit = false;
            installRequested = false;
            installRequestedExplicit = false;
            permissionInstallExplicit = null;
            if (requested == UpdateStateMachine.Mode.NEVER) {
                cleared = UpdateInstallAttemptStore.disableAndClear(preferences);
                if (cleared.abandonSessionId >= 0) abandonSession(cleared.abandonSessionId);
            }
        }
        boolean saved = requested == UpdateStateMachine.Mode.NEVER
                ? cleared != null && !cleared.storageFailure : false;
        if (requested != UpdateStateMachine.Mode.NEVER) try {
            saved = preferences.edit().putString(MODE, requested.name()).remove(LEGACY_AUTO).commit();
        } catch (RuntimeException ignored) { }
        UpdateStateMachine.Mode selected = saved ? requested : UpdateStateMachine.Mode.NEVER;
        if (!saved) {
            UpdateInstallAttemptStore.Recovery failedClosed = UpdateInstallAttemptStore.disableAndClear(preferences);
            if (failedClosed.abandonSessionId >= 0) abandonSession(failedClosed.abandonSessionId);
        }
        synchronized (policyLock) {
            machine.setMode(selected);
            UpdateModePreference.setRuntimeMode(selected);
            machine.sessionChanged(host.hasActiveDisplaySession());
        }
        ui.removeCallbacks(periodic);
        if (foreground && selected != UpdateStateMachine.Mode.NEVER) ui.postDelayed(periodic, PERIOD_MS);
        if (!saved) publish("无法保存更新设置；已安全切换为“从不更新”");
        else if (selected == UpdateStateMachine.Mode.AUTOMATIC) publish("已选择自动更新");
        else if (selected == UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK) publish("已选择自动下载后手动安装");
        else publish("已选择从不更新；不会检查或下载更新");
        if (foreground && selected != UpdateStateMachine.Mode.NEVER) queueRefresh(false);
        return selected;
    }

    String status() { return status; }

    void onForeground() {
        foreground = true;
        machine.sessionChanged(host.hasActiveDisplaySession());
        host.updateStatusChanged(status);
        ui.removeCallbacks(periodic);
        if (machine.checksEnabled()) ui.postDelayed(periodic, PERIOD_MS);
        boolean consumedInstallResult = consumeInstallResult();
        if (!consumedInstallResult && machine.state() == UpdateStateMachine.State.AWAITING_PERMISSION)
            publish("请允许 TabLink 安装未知应用，然后返回继续更新");
        else if (!consumedInstallResult && machine.state() == UpdateStateMachine.State.INSTALLING)
            publish("Android 安装仍在进行；正在等待系统结果");
        else if (!consumedInstallResult && machine.checksEnabled()) queueRefresh(false);
        else if (!consumedInstallResult) publish("已选择从不更新；不会检查或下载更新");
    }

    void onBackground() {
        foreground = false;
        ui.removeCallbacks(periodic);
    }

    void onSessionChanged() {
        UpdateStateMachine.Action action;
        synchronized (policyLock) {
            action = machine.sessionChanged(host.hasActiveDisplaySession());
        }
        perform(action);
    }

    void checkNow(boolean explicit) {
        if (closed) return;
        if (!machine.checksEnabled()) {
            if (explicit) publish("更新策略是“从不更新”；请先在设置中更改");
            return;
        }
        if (!configured()) {
            publish("更新服务尚未配置");
            return;
        }
        if (!operationRunning.compareAndSet(false, true)) {
            refreshRequested = true;
            if (explicit) refreshExplicit = true;
            if (explicit) publish("正在检查或下载正式版更新…");
            return;
        }
        final long generation = policyGeneration;
        machine.beginCheck();
        publish("正在检查正式版更新…");
        worker.execute(() -> {
            try {
                RemoteRelease release = fetchRelease(generation);
                ensurePolicyCurrent(generation);
                byte[] envelope = release.envelope;
                ReleaseManifest manifest = release.manifest;
                ReleaseManifest.Artifact artifact = release.artifact;
                if (manifest.minimumProtocolVersion > ReleaseManifest.UPDATE_PROTOCOL) {
                    clearInstallRequest();
                    invalidateStaged();
                    available = null;
                    machine.failed();
                    publish("最新正式版需要更新的升级协议；请从官网手动安装新版 TabLink");
                    return;
                }
                if (artifact == null || !artifact.isNewerThan(BuildConfig.VERSION_CODE, BuildConfig.VERSION_NAME)) {
                    clearInstallRequest();
                    invalidateStaged();
                    available = null;
                    machine.checked(false);
                    publish("当前已是最新正式版 " + BuildConfig.VERSION_NAME);
                    return;
                }
                if (!manifest.includesInstallation(installationId())) {
                    clearInstallRequest();
                    invalidateStaged();
                    available = null;
                    machine.checked(false);
                    publish("正式版 " + artifact.version + " 正在分批发布，暂未轮到此设备");
                    return;
                }
                invalidateIfDifferent(artifact);
                available = artifact;
                if (validateStaged(envelope, artifact, false)) {
                    UpdateStateMachine.Action action = machine.downloaded();
                    Boolean explicitInstall = takeInstallRequest();
                    if (explicitInstall != null) {
                        publish("已重新确认正式版 " + artifact.version + "，正在准备系统安装…");
                        installValidatedPending(generation, explicitInstall);
                        return;
                    }
                    publish(downloadedStatus(artifact, action));
                    if (explicit && action == UpdateStateMachine.Action.NONE && machine.hasStagedUpdate())
                        ui.post(() -> {
                            if (foreground && host.isForeground() && machine.checksEnabled())
                                host.offerInstall(artifact, host.hasActiveDisplaySession());
                        });
                    else perform(action);
                    return;
                }
                UpdateStateMachine.Action action = machine.checked(true);
                if (explicit && action == UpdateStateMachine.Action.NONE) action = machine.requestDownload();
                if (action == UpdateStateMachine.Action.DOWNLOAD) download(release, generation);
                else {
                    publish("发现正式版 " + artifact.version + "；副屏断开后自动下载");
                    if (explicit && host.hasActiveDisplaySession())
                        ui.post(() -> host.offerDisconnectForDownload(artifact));
                }
            } catch (PolicyChangedException ignored) {
                if (!machine.checksEnabled()) publish("已选择从不更新；不会检查或下载更新");
            } catch (Exception failure) {
                clearInstallRequest();
                if (machine.checksEnabled()) {
                    machine.failed();
                    publish("检查更新失败：" + visibleFailure(failure));
                }
            } finally {
                operationRunning.set(false);
                if (refreshRequested && foreground && machine.checksEnabled()) ui.post(this::drainRefreshRequest);
            }
        });
    }

    void requestDownloadOrInstall() {
        if (!machine.checksEnabled()) {
            publish("更新策略是“从不更新”；请先在设置中更改");
            return;
        }
        queueRefresh(true);
    }

    void continueAfterExplicitDisconnect() {
        if (host.hasActiveDisplaySession()) return;
        queueRefresh(true);
    }

    void installPending(boolean explicit) {
        if (closed || !foreground) return;
        if (!machine.checksEnabled()) {
            publish("更新策略是“从不更新”；请先在设置中更改");
            return;
        }
        if (host.hasActiveDisplaySession() && !explicit) {
            publish("更新已就绪，副屏断开后再安装");
            return;
        }
        requestInstall(explicit);
        publish("正在重新确认正式版仍可安装…");
        queueRefresh(true);
    }

    private void installValidatedPending(long generation, boolean explicit) throws Exception {
        ensurePolicyCurrent(generation);
        if (!explicit && host.hasActiveDisplaySession()) {
            machine.sessionChanged(true);
            publish("更新已就绪，副屏断开后再安装");
            return;
        }
        Verified staged = readAndValidateStaged();
        boolean permission = canInstallPackages();
        if (!machine.beginInstall(explicit, permission)) {
            if (!explicit) {
                publish("更新已就绪，副屏断开后再安装");
                return;
            }
            throw new IOException("更新当前不能安装");
        }
        if (!permission) {
            synchronized (policyLock) {
                ensurePolicyCurrent(generation);
                permissionInstallExplicit = explicit;
            }
        }
        archiveCurrentPackage();
        ensurePolicyCurrent(generation);
        if (!permission) ui.post(() -> openUnknownSourcesSettings(generation));
        else commitPackageInstaller(staged, generation);
    }

    void onResume() {
        if (!foreground || closed) return;
        if (consumeInstallResult()) return;
        if (machine.state() == UpdateStateMachine.State.INSTALLING) {
            if (UpdateInstallAttemptStore.current(preferences) != null) {
                publish("Android 安装仍在进行；正在等待系统结果");
                return;
            }
            machine.installerReturned();
            publish("安装未完成；可在设置中点击“检查 / 继续正式版更新”重试");
            return;
        }
        if (machine.state() != UpdateStateMachine.State.AWAITING_PERMISSION) return;
        UpdateInstallerUiGate.permissionUiFinished(this);
        boolean granted = canInstallPackages();
        machine.installPermissionReturned(granted);
        if (granted) {
            boolean explicit;
            synchronized (policyLock) {
                explicit = Boolean.TRUE.equals(permissionInstallExplicit);
                permissionInstallExplicit = null;
            }
            installPending(explicit);
        }
        else publish("请允许 TabLink 安装未知应用，然后返回继续更新");
    }

    private void download(RemoteRelease release, long generation) throws Exception {
        byte[] envelope = release.envelope;
        ReleaseManifest.Artifact artifact = release.artifact;
        if (host.hasActiveDisplaySession()) {
            machine.sessionChanged(true);
            publish("发现正式版 " + artifact.version + "；副屏断开后自动下载");
            return;
        }
        publish("正在后台下载正式版 " + artifact.version + "…");
        ensureDirectory();
        File temporary = new File(updateDirectory, ArtifactIntegrity.safeApkName(artifact.build) + ".download");
        File destination = new File(updateDirectory, ArtifactIntegrity.safeApkName(artifact.build));
        deleteQuietly(temporary);
        Exception lastFailure = null;
        try {
            for (RemoteCandidate candidate : release.candidates) {
                ensurePolicyCurrent(generation);
                deleteQuietly(temporary);
                try {
                    downloadCandidate(candidate.artifact.url, temporary, artifact, generation);
                    envelope = candidate.envelope;
                    lastFailure = null;
                    break;
                } catch (DeferredException | PolicyChangedException stop) { throw stop; }
                catch (Exception failure) { lastFailure = failure; }
            }
            if (lastFailure != null) throw lastFailure;
        } catch (DeferredException deferred) {
            deleteQuietly(temporary);
            machine.downloadDeferred(true);
            publish("副屏正在使用；已暂停更新下载，断开后会重试");
            return;
        } catch (PolicyChangedException changed) {
            deleteQuietly(temporary);
            throw changed;
        }
        try {
            ensurePolicyCurrent(generation);
            if (!ArtifactIntegrity.verify(temporary, artifact.size, artifact.sha256))
                throw new IOException("更新包 SHA-256 或大小不匹配");
            verifyApk(temporary, artifact);
            ensurePolicyCurrent(generation);
            synchronized (policyLock) {
                ensurePolicyCurrent(generation);
                atomicReplace(temporary, destination);
                writeAtomic(new File(updateDirectory, MANIFEST_FILE), envelope);
                cleanOtherApks(destination);
            }
            ensurePolicyCurrent(generation);
            UpdateStateMachine.Action action = machine.downloaded();
            ensurePolicyCurrent(generation);
            Boolean explicitInstall = takeInstallRequest();
            if (explicitInstall != null) {
                publish("正式版 " + artifact.version + " 已下载并重新确认，正在准备系统安装…");
                installValidatedPending(generation, explicitInstall);
                return;
            }
            publish(downloadedStatus(artifact, action));
            perform(action);
        } finally { deleteQuietly(temporary); }
    }

    private Verified readAndValidateStaged() throws Exception {
        File manifestFile = new File(updateDirectory, MANIFEST_FILE);
        byte[] envelope = readLimited(manifestFile, ReleaseManifest.MAX_MANIFEST_BYTES);
        ReleaseManifest manifest = ReleaseManifest.verify(envelope, BuildConfig.UPDATE_PUBLIC_KEY_SPKI, System.currentTimeMillis());
        requireManifestAtFloor(manifest);
        if (manifest.minimumProtocolVersion > ReleaseManifest.UPDATE_PROTOCOL)
            throw new IOException("暂存更新需要新版升级协议");
        ReleaseManifest.Artifact artifact = manifest.androidArtifact();
        if (artifact == null || !artifact.isNewerThan(BuildConfig.VERSION_CODE, BuildConfig.VERSION_NAME)
                || !manifest.includesInstallation(installationId()))
            throw new IOException("暂存更新已失效");
        File apk = new File(updateDirectory, ArtifactIntegrity.safeApkName(artifact.build));
        if (!ArtifactIntegrity.verify(apk, artifact.size, artifact.sha256)) throw new IOException("暂存更新校验失败");
        verifyApk(apk, artifact);
        return new Verified(artifact, apk);
    }

    private boolean validateStaged(byte[] freshEnvelope, ReleaseManifest.Artifact artifact, boolean install) {
        try {
            Verified existing = readAndValidateStaged();
            if (!same(existing.artifact, artifact)) return false;
            if (install) ReleaseManifest.verify(freshEnvelope, BuildConfig.UPDATE_PUBLIC_KEY_SPKI, System.currentTimeMillis());
            return true;
        } catch (Exception failure) { return false; }
    }

    private void verifyApk(File apk, ReleaseManifest.Artifact artifact) throws Exception {
        PackageManager packages = activity.getPackageManager();
        int flags = Build.VERSION.SDK_INT >= 28 ? PackageManager.GET_SIGNING_CERTIFICATES : PackageManager.GET_SIGNATURES;
        PackageInfo candidate = packages.getPackageArchiveInfo(apk.getAbsolutePath(), flags);
        PackageInfo installed = packages.getPackageInfo(activity.getPackageName(), flags);
        if (candidate == null || !activity.getPackageName().equals(candidate.packageName)
                || packageVersion(candidate) != artifact.build || !artifact.version.equals(candidate.versionName))
            throw new IOException("APK 包名或版本与签名清单不一致");
        Set<String> candidateSigners = signerDigests(candidate);
        Set<String> installedSigners = signerDigests(installed);
        if (candidateSigners.isEmpty() || !candidateSigners.equals(installedSigners))
            throw new IOException("APK 安装签名与当前应用不一致");
    }

    @SuppressWarnings("deprecation")
    private static long packageVersion(PackageInfo info) {
        return Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode;
    }

    @SuppressWarnings("deprecation")
    private static Set<String> signerDigests(PackageInfo info) throws Exception {
        Signature[] signatures;
        if (Build.VERSION.SDK_INT >= 28) {
            if (info.signingInfo == null) return new HashSet<>();
            signatures = info.signingInfo.hasMultipleSigners()
                    ? info.signingInfo.getApkContentsSigners() : info.signingInfo.getSigningCertificateHistory();
        } else signatures = info.signatures;
        HashSet<String> result = new HashSet<>();
        if (signatures != null) for (Signature signature : signatures)
            result.add(hex(MessageDigest.getInstance("SHA-256").digest(signature.toByteArray())));
        return result;
    }

    private void openUnknownSourcesSettings(long generation) {
        try {
            ensurePolicyCurrent(generation);
            if (deferPermissionUiForActiveSession(generation)) return;
            Intent permission = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:" + activity.getPackageName()));
            synchronized (policyLock) {
                ensurePolicyCurrent(generation);
                if (host.hasActiveDisplaySession()) {
                    permissionInstallExplicit = null;
                    machine.installResult(UpdateStateMachine.InstallResult.RETRY);
                    machine.sessionChanged(true);
                    publish("更新已就绪，副屏断开后再安装");
                    return;
                }
                UpdateInstallerUiGate.LaunchResult launch =
                        UpdateInstallerUiGate.tryLaunchPermissionUi(
                                this, () -> activity.startActivity(permission));
                if (launch == UpdateInstallerUiGate.LaunchResult.BLOCKED_BY_DISPLAY) {
                    permissionInstallExplicit = null;
                    machine.installResult(UpdateStateMachine.InstallResult.RETRY);
                    machine.sessionChanged(host.hasActiveDisplaySession());
                    publish("更新已就绪，副屏断开后再安装");
                    return;
                }
                if (launch == UpdateInstallerUiGate.LaunchResult.ALREADY_LAUNCHING) return;
            }
            publish("请允许 TabLink 安装未知应用，然后返回继续更新");
        } catch (PolicyChangedException ignored) {
            // The selected policy no longer permits this stale permission request.
        } catch (ActivityNotFoundException | SecurityException failure) {
            machine.installerReturned();
            publish("无法打开“安装未知应用”设置");
        }
    }

    private boolean deferPermissionUiForActiveSession(long generation) throws PolicyChangedException {
        synchronized (policyLock) {
            ensurePolicyCurrent(generation);
            if (!host.hasActiveDisplaySession()) return false;
            permissionInstallExplicit = null;
            machine.installResult(UpdateStateMachine.InstallResult.RETRY);
            machine.sessionChanged(true);
        }
        publish("更新已就绪，副屏断开后再安装");
        return true;
    }

    private void commitPackageInstaller(Verified staged, long generation) throws Exception {
        ensurePolicyCurrent(generation);
        PackageInstaller installer = activity.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams parameters = new PackageInstaller.SessionParams(
                PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        parameters.setAppPackageName(activity.getPackageName());
        parameters.setSize(staged.artifact.size);
        if (Build.VERSION.SDK_INT >= 31)
            parameters.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        int sessionId = installer.createSession(parameters);
        UpdateInstallAttempt attempt = UpdateInstallAttempt.create(sessionId);
        boolean committed = false;
        boolean tracked = false;
        try (PackageInstaller.Session installSession = installer.openSession(sessionId);
             InputStream input = new FileInputStream(staged.apk);
             OutputStream output = installSession.openWrite("base.apk", 0, staged.artifact.size)) {
            byte[] buffer = new byte[64 * 1024]; long total = 0;
            while (true) {
                ensurePolicyCurrent(generation);
                int count = input.read(buffer); if (count < 0) break; if (count == 0) continue;
                total += count; if (total > staged.artifact.size) throw new IOException("APK 在提交时大小发生变化");
                ensurePolicyCurrent(generation);
                output.write(buffer, 0, count);
            }
            if (total != staged.artifact.size) throw new IOException("APK 在提交时大小发生变化");
            ensurePolicyCurrent(generation);
            installSession.fsync(output);
            Intent result = new Intent(activity, UpdateInstallReceiver.class)
                    .setAction(UpdateInstallReceiver.ACTION)
                    .setData(Uri.parse(attempt.callbackIdentity()))
                    .putExtra(UpdateInstallReceiver.EXTRA_ATTEMPT_SESSION_ID, attempt.sessionId)
                    .putExtra(UpdateInstallReceiver.EXTRA_ATTEMPT_TOKEN, attempt.token);
            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= 31) flags |= PendingIntent.FLAG_MUTABLE;
            PendingIntent callback = PendingIntent.getBroadcast(activity, sessionId, result, flags);
            synchronized (policyLock) {
                ensurePolicyCurrent(generation);
                if (host.hasActiveDisplaySession()) {
                    machine.installResult(UpdateStateMachine.InstallResult.RETRY);
                    machine.sessionChanged(true);
                    publish("更新已就绪，副屏断开后再安装");
                    throw new PolicyChangedException();
                }
                if (!UpdateInstallAttemptStore.begin(preferences, attempt)) {
                    machine.installResult(UpdateStateMachine.InstallResult.RETRY);
                    machine.sessionChanged(host.hasActiveDisplaySession());
                    abandonSession(sessionId);
                    publish("无法建立可跟踪的 Android 安装会话；已保留更新包，可在设置中重试");
                    return;
                }
                tracked = true;
                installSession.commit(callback.getIntentSender());
                committed = true;
            }
            publish("已提交正式版 " + staged.artifact.version + "；等待 Android 安装结果");
        } catch (PolicyChangedException changed) {
            abandonSession(sessionId);
            if (tracked) UpdateInstallAttemptStore.discardIfCurrent(preferences, attempt, true);
            throw changed;
        } catch (Exception sessionFailure) {
            if (committed) {
                publish("已提交正式版 " + staged.artifact.version + "；等待 Android 安装结果");
                return;
            }
            abandonSession(sessionId);
            if (tracked) UpdateInstallAttemptStore.discardIfCurrent(preferences, attempt, true);
            ensurePolicyCurrent(generation);
            machine.installResult(UpdateStateMachine.InstallResult.RETRY);
            publish("Android 安装会话失败：" + visibleFailure(sessionFailure)
                    + "；已保留更新包，可在设置中重试");
        }
    }

    private boolean consumeInstallResult() {
        UpdateInstallAttemptStore.Recovery recovery = UpdateInstallAttemptStore.recover(
                preferences, PackageInstaller.STATUS_PENDING_USER_ACTION);
        if (recovery.abandonSessionId >= 0) abandonSession(recovery.abandonSessionId);
        if (recovery.current != null) {
            UpdateInstallerUiGate.restoreInstallerTransaction(recovery.current);
            if (machine.state() != UpdateStateMachine.State.INSTALLING) machine.restoreInstalling();
        }
        if (recovery.retryRequired && recovery.current == null) {
            machine.installResult(UpdateStateMachine.InstallResult.RETRY);
            publish(recovery.storageFailure
                    ? "无法清理损坏的 Android 安装状态；已安全停止自动安装，请重试"
                    : "Android 安装状态记录已损坏；已清理，可在设置中重试");
            return true;
        }
        UpdateInstallAttempt.Result result = recovery.result;
        if (result == null) return false;
        int installerStatus = result.status;
        String message = result.message;
        if (installerStatus == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            machine.installResult(UpdateStateMachine.InstallResult.PENDING_USER_ACTION);
            publish(message);
            return true;
        }
        UpdateInstallAttemptStore.clearResult(preferences, result);
        if (installerStatus == PackageInstaller.STATUS_SUCCESS) {
            machine.installResult(UpdateStateMachine.InstallResult.SUCCESS);
            publish(message);
            return true;
        }
        machine.installResult(UpdateStateMachine.InstallResult.RETRY);
        publish(message + "；可在设置中重试");
        return true;
    }

    private void abandonSession(int sessionId) {
        if (sessionId < 0) return;
        try { activity.getPackageManager().getPackageInstaller().abandonSession(sessionId); }
        catch (RuntimeException ignored) { }
    }

    private boolean installerSessionExists(UpdateInstallAttempt attempt) {
        try {
            // API 23-25 cannot distinguish a committed session from the crash
            // window after begin() persisted the tuple but before commit().
            if (Build.VERSION.SDK_INT < 26) return false;
            java.util.HashMap<Integer, Boolean> sessions = new java.util.HashMap<>();
            for (PackageInstaller.SessionInfo session : activity.getPackageManager()
                    .getPackageInstaller().getMySessions())
                sessions.put(session.getSessionId(), session.isSealed());
            return UpdateInstallAttempt.isSessionRestorable(attempt, sessions, true);
        } catch (RuntimeException unavailable) {
            // An uncertain session cannot retain a process-global display gate.
            return false;
        }
    }

    private boolean canInstallPackages() {
        return Build.VERSION.SDK_INT < 26 || activity.getPackageManager().canRequestPackageInstalls();
    }

    private void archiveCurrentPackage() throws IOException {
        ensureDirectory();
        File source = new File(activity.getApplicationInfo().sourceDir);
        File target = new File(updateDirectory, LAST_WORKING_FILE);
        File temporary = new File(updateDirectory, LAST_WORKING_FILE + ".download");
        copy(source, temporary);
        atomicReplace(temporary, target);
    }

    private void invalidateIfDifferent(ReleaseManifest.Artifact artifact) {
        try { if (!same(readAndValidateStaged().artifact, artifact)) invalidateStaged(); }
        catch (Exception failure) { invalidateStaged(); }
    }
    private static boolean same(ReleaseManifest.Artifact a, ReleaseManifest.Artifact b) {
        return a != null && b != null && a.platform.equals(b.platform)
                && a.build == b.build && a.size == b.size && a.version.equals(b.version)
                && a.sha256.equals(b.sha256);
    }
    private void invalidateStaged() {
        if (updateDirectory.isDirectory()) {
            File[] files = updateDirectory.listFiles();
            if (files != null) for (File file : files)
                if (MANIFEST_FILE.equals(file.getName()) || ArtifactIntegrity.isSafeApkName(file.getName()))
                    deleteQuietly(file);
        }
        machine.reset();
    }
    private void cleanInterruptedDownloads() {
        if (!updateDirectory.isDirectory()) return;
        File[] files = updateDirectory.listFiles();
        if (files != null) for (File file : files) if (file.getName().endsWith(".download")) deleteQuietly(file);
    }
    private void cleanOtherApks(File keep) {
        File[] files = updateDirectory.listFiles();
        if (files != null) for (File file : files)
            if (!file.equals(keep) && ArtifactIntegrity.isSafeApkName(file.getName())) deleteQuietly(file);
    }

    private RemoteRelease fetchRelease(long generation) throws Exception {
        ArrayList<RemoteCandidate> valid = new ArrayList<>();
        ArrayList<String> failures = new ArrayList<>();
        String[] addresses = { BuildConfig.UPDATE_MANIFEST_URL, BuildConfig.UPDATE_MANIFEST_FALLBACK_URL };
        String[] labels = { "个人博客", "GitHub" };
        for (int index = 0; index < addresses.length; index++) {
            String address = addresses[index];
            if (address == null || address.trim().isEmpty()) continue;
            ensurePolicyCurrent(generation);
            try {
                byte[] envelope = fetch(address, ReleaseManifest.MAX_MANIFEST_BYTES, generation);
                ReleaseManifest manifest = ReleaseManifest.verify(
                        envelope, BuildConfig.UPDATE_PUBLIC_KEY_SPKI, System.currentTimeMillis());
                valid.add(new RemoteCandidate(envelope, manifest, manifest.androidArtifact()));
            } catch (PolicyChangedException changed) { throw changed; }
            catch (Exception failure) { failures.add(labels[index] + "：" + visibleFailure(failure)); }
        }
        ensurePolicyCurrent(generation);
        if (valid.isEmpty()) {
            String detail = failures.isEmpty() ? "没有配置可用更新源" : String.join("；", failures);
            throw new IOException(detail);
        }
        ArrayList<ReleaseManifest> manifests = new ArrayList<>();
        for (RemoteCandidate candidate : valid) manifests.add(candidate.manifest);
        ReleaseManifest authoritative;
        try { authoritative = UpdateDecisionPolicy.selectAuthoritative(manifests); }
        catch (UpdateDecisionPolicy.ConflictException conflict) {
            recordManifestConflict(conflict, generation);
            throw conflict;
        }
        long newest = authoritative.publishedAtUtcMs;
        ArrayList<RemoteCandidate> selected = new ArrayList<>();
        for (RemoteCandidate candidate : valid)
            if (candidate.manifest.publishedAtUtcMs == newest) selected.add(candidate);
        RemoteCandidate decision = selected.get(0);
        try { acceptManifestFloor(decision.manifest, generation); }
        catch (IOException replayOrStorageFailure) {
            invalidateStaged();
            available = null;
            throw replayOrStorageFailure;
        }
        return new RemoteRelease(decision.envelope, decision.manifest, decision.artifact, selected);
    }

    private void acceptManifestFloor(ReleaseManifest manifest, long generation)
            throws IOException, PolicyChangedException {
        synchronized (policyLock) {
            ensurePolicyCurrent(generation);
            if (manifestFloor.blocked()) {
                final UpdateInstallAttemptStore.Recovery[] cleanup = { null };
                try {
                    manifestFloor.accept(manifest, (utc, decision) -> {
                        cleanup[0] = UpdateInstallAttemptStore.replaceBlockedFloorAndClear(
                                preferences, MANIFEST_FLOOR_UTC, MANIFEST_FLOOR_DECISION,
                                manifestFloor.publishedAtUtcMs(), manifestFloor.decision(),
                                utc, decision);
                        return !cleanup[0].storageFailure;
                    });
                } finally {
                    if (cleanup[0] != null && cleanup[0].abandonSessionId >= 0)
                        abandonSession(cleanup[0].abandonSessionId);
                }
                return;
            }
            manifestFloor.accept(manifest, (utc, decision) -> preferences.edit()
                    .putLong(MANIFEST_FLOOR_UTC, utc)
                    .putString(MANIFEST_FLOOR_DECISION, decision).commit());
        }
    }

    private void requireManifestAtFloor(ReleaseManifest manifest) throws IOException {
        manifestFloor.require(manifest);
    }

    private void recordManifestConflict(UpdateDecisionPolicy.ConflictException conflict,
            long generation) throws IOException, PolicyChangedException {
        IOException storageFailure = null;
        synchronized (policyLock) {
            ensurePolicyCurrent(generation);
            final UpdateInstallAttemptStore.Recovery[] cleanup = { null };
            try {
                manifestFloor.block(conflict.publishedAtUtcMs, (utc, decision) -> {
                    cleanup[0] = UpdateInstallAttemptStore.blockConflictFloorAndClear(
                            preferences, MANIFEST_FLOOR_UTC, MANIFEST_FLOOR_DECISION,
                            utc, decision);
                    return !cleanup[0].storageFailure;
                });
            } catch (IOException failure) {
                storageFailure = failure;
            }
            // A floor already newer than this conflict does not invoke the
            // writer, but the conflicting candidate must still cancel an old
            // staged/install transaction.
            UpdateInstallAttemptStore.Recovery cleared = cleanup[0] != null
                    ? cleanup[0] : UpdateInstallAttemptStore.clearAll(preferences);
            if (cleared.abandonSessionId >= 0) abandonSession(cleared.abandonSessionId);
            if (cleared.storageFailure) {
                IOException clearFailure = new IOException("无法清理冲突清单对应的安装状态");
                if (storageFailure == null) storageFailure = clearFailure;
                else storageFailure.addSuppressed(clearFailure);
            }
            clearInstallRequest();
            permissionInstallExplicit = null;
            available = null;
        }
        invalidateStaged();
        if (storageFailure != null) throw storageFailure;
    }

    private byte[] fetch(String url, long limit, long generation) throws Exception {
        ensurePolicyCurrent(generation);
        HttpsURLConnection connection = openFollowingRedirects(url, generation);
        try {
            int code = connection.getResponseCode();
            if (code != HttpURLConnection.HTTP_OK) throw new IOException("更新服务器返回 HTTP " + code);
            long declared = connection.getContentLength();
            if (declared > limit) throw new IOException("更新清单过大");
            try (InputStream input = connection.getInputStream()) {
                ByteArrayOutputStream output = new ByteArrayOutputStream();
                byte[] buffer = new byte[8192]; long total = 0;
                while (true) {
                    ensurePolicyCurrent(generation);
                    int count = input.read(buffer); if (count < 0) break; if (count == 0) continue;
                    total += count; if (total > limit) throw new IOException("下载内容超过限制");
                    output.write(buffer, 0, count);
                }
                if (total == 0) throw new IOException("下载内容为空");
                return output.toByteArray();
            }
        } finally { connection.disconnect(); }
    }

    private void downloadCandidate(String address, File temporary, ReleaseManifest.Artifact artifact,
                                   long generation) throws Exception {
        HttpsURLConnection connection = openFollowingRedirects(address, generation);
        try {
            int code = connection.getResponseCode();
            if (code != HttpURLConnection.HTTP_OK) throw new IOException("下载服务器返回 HTTP " + code);
            long declared = connection.getContentLength();
            if (declared >= 0 && declared != artifact.size) throw new IOException("更新包大小与清单不一致");
            try (InputStream input = connection.getInputStream(); FileOutputStream output = new FileOutputStream(temporary)) {
                byte[] buffer = new byte[64 * 1024]; long total = 0;
                while (true) {
                    ensurePolicyCurrent(generation);
                    if (host.hasActiveDisplaySession()) throw new DeferredException();
                    int count = input.read(buffer); if (count < 0) break; if (count == 0) continue;
                    total += count; if (total > artifact.size) throw new IOException("更新包超过清单大小");
                    output.write(buffer, 0, count);
                }
                if (total != artifact.size) throw new IOException("更新包大小与清单不一致");
                output.getFD().sync();
            }
        } finally { connection.disconnect(); }
        if (!ArtifactIntegrity.verify(temporary, artifact.size, artifact.sha256)) {
            deleteQuietly(temporary);
            throw new IOException("更新包 SHA-256 或大小不匹配");
        }
    }

    private HttpsURLConnection openFollowingRedirects(String address, long generation)
            throws IOException, PolicyChangedException {
        String current = address;
        for (int redirects = 0; redirects <= 5; redirects++) {
            ensurePolicyCurrent(generation);
            HttpsURLConnection connection = openHttps(current);
            boolean handedOff = false;
            try {
                int code = connection.getResponseCode();
                ensurePolicyCurrent(generation);
                if (code != HttpURLConnection.HTTP_MOVED_PERM && code != HttpURLConnection.HTTP_MOVED_TEMP
                        && code != HttpURLConnection.HTTP_SEE_OTHER && code != 307 && code != 308) {
                    handedOff = true;
                    return connection;
                }
                String location = connection.getHeaderField("Location");
                URL base = connection.getURL();
                ensurePolicyCurrent(generation);
                if (location == null || location.trim().isEmpty())
                    throw new IOException("更新服务器重定向缺少地址");
                URL next = new URL(base, location);
                validateHttps(next);
                ensurePolicyCurrent(generation);
                current = next.toExternalForm();
            } finally {
                if (!handedOff) connection.disconnect();
            }
        }
        throw new IOException("更新地址重定向次数过多");
    }

    private static HttpsURLConnection openHttps(String address) throws IOException {
        URL url = new URL(address);
        validateHttps(url);
        HttpsURLConnection connection = (HttpsURLConnection) url.openConnection();
        connection.setConnectTimeout(12_000); connection.setReadTimeout(30_000);
        connection.setInstanceFollowRedirects(false); connection.setUseCaches(false);
        connection.setRequestProperty("Accept", "application/json, application/vnd.android.package-archive");
        connection.setRequestProperty("User-Agent", "TabLink-Android/" + BuildConfig.VERSION_NAME);
        return connection;
    }
    private static void validateHttps(URL url) throws IOException {
        if (!"https".equalsIgnoreCase(url.getProtocol()) || url.getHost() == null || url.getHost().isEmpty()
                || url.getUserInfo() != null || url.getRef() != null)
            throw new IOException("更新地址必须使用完整 HTTPS 地址");
    }
    private static byte[] readLimited(File file, long limit) throws IOException {
        if (!file.isFile() || file.length() < 1 || file.length() > limit) throw new IOException("暂存更新清单不存在");
        try (InputStream input = new FileInputStream(file)) { return readLimited(input, limit); }
    }
    private static byte[] readLimited(InputStream input, long limit) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream(); byte[] buffer = new byte[8192]; long total = 0;
        while (true) { int count = input.read(buffer); if (count < 0) break; if (count == 0) continue;
            total += count; if (total > limit) throw new IOException("下载内容超过限制"); output.write(buffer, 0, count); }
        if (total == 0) throw new IOException("下载内容为空"); return output.toByteArray();
    }
    private static void copy(File source, File destination) throws IOException {
        deleteQuietly(destination);
        try (InputStream input = new FileInputStream(source); FileOutputStream output = new FileOutputStream(destination)) {
            byte[] buffer = new byte[64 * 1024]; int count;
            while ((count = input.read(buffer)) >= 0) { if (count > 0) output.write(buffer, 0, count); }
            output.getFD().sync();
        }
    }
    private static void writeAtomic(File destination, byte[] bytes) throws IOException {
        File temporary = new File(destination.getParentFile(), destination.getName() + ".download");
        deleteQuietly(temporary);
        try (FileOutputStream output = new FileOutputStream(temporary)) { output.write(bytes); output.getFD().sync(); }
        atomicReplace(temporary, destination);
    }
    private static void atomicReplace(File source, File destination) throws IOException {
        if (destination.exists() && !destination.delete()) throw new IOException("无法替换旧更新文件");
        if (!source.renameTo(destination)) { deleteQuietly(source); throw new IOException("无法保存更新文件"); }
    }
    private void ensureDirectory() throws IOException {
        if (!updateDirectory.isDirectory() && !updateDirectory.mkdirs()) throw new IOException("无法创建更新目录");
    }
    private String installationId() {
        String existing = preferences.getString(INSTALLATION, null);
        if (existing != null) return existing;
        String created = UUID.randomUUID().toString(); preferences.edit().putString(INSTALLATION, created).apply(); return created;
    }
    private boolean configured() {
        return BuildConfig.UPDATE_MANIFEST_URL != null && !BuildConfig.UPDATE_MANIFEST_URL.isEmpty()
                || BuildConfig.UPDATE_MANIFEST_FALLBACK_URL != null && !BuildConfig.UPDATE_MANIFEST_FALLBACK_URL.isEmpty();
    }
    private void ensurePolicyCurrent(long generation) throws PolicyChangedException {
        if (closed || generation != policyGeneration || !machine.checksEnabled()) throw new PolicyChangedException();
    }
    private void queueRefresh(boolean explicit) {
        if (closed || !machine.checksEnabled()) return;
        refreshRequested = true;
        if (explicit) refreshExplicit = true;
        if (foreground) ui.post(this::drainRefreshRequest);
    }
    private void drainRefreshRequest() {
        if (closed || !foreground || !machine.checksEnabled() || !refreshRequested) return;
        if (operationRunning.get()) return;
        boolean explicit = refreshExplicit;
        refreshRequested = false;
        refreshExplicit = false;
        checkNow(explicit);
    }
    private void perform(UpdateStateMachine.Action action) {
        if (action == UpdateStateMachine.Action.DOWNLOAD) queueRefresh(false);
        else if (action == UpdateStateMachine.Action.START_INSTALL && foreground)
            ui.post(() -> {
                if (foreground && host.isForeground() && machine.mode() == UpdateStateMachine.Mode.AUTOMATIC)
                    installPending(false);
            });
    }
    private String downloadedStatus(ReleaseManifest.Artifact artifact, UpdateStateMachine.Action action) {
        if (host.hasActiveDisplaySession())
            return "正式版 " + artifact.version + " 已下载，将在副屏断开后安装";
        return action == UpdateStateMachine.Action.START_INSTALL
                ? "正式版 " + artifact.version + " 已下载，正在准备系统安装…"
                : "正式版 " + artifact.version + " 已下载，等待手动安装";
    }
    private void requestInstall(boolean explicit) {
        synchronized (policyLock) {
            installRequestedExplicit = explicit;
            installRequested = true;
        }
    }
    private Boolean takeInstallRequest() {
        synchronized (policyLock) {
            if (!installRequested) return null;
            boolean explicit = installRequestedExplicit;
            installRequested = false;
            installRequestedExplicit = false;
            return explicit;
        }
    }
    private void clearInstallRequest() {
        synchronized (policyLock) {
            installRequested = false;
            installRequestedExplicit = false;
        }
    }
    private void publish(String value) {
        status = value; ui.post(() -> { if (!closed) host.updateStatusChanged(value); });
    }
    private static String visibleFailure(Exception failure) {
        String message = failure.getMessage();
        if (message == null || message.trim().isEmpty()) return "网络或校验失败，请稍后重试";
        message = message.replaceAll("[\\p{Cntrl}]", " ").trim();
        return message.length() <= 120 ? message : message.substring(0, 120);
    }
    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 255));
        return result.toString();
    }
    private static void deleteQuietly(File file) { if (file != null && file.exists()) file.delete(); }

    @Override public void close() {
        synchronized (policyLock) {
            closed = true; foreground = false; policyGeneration++;
            refreshRequested = false; refreshExplicit = false;
        }
        ui.removeCallbacks(periodic);
        // Activity recreation must not strand the process-global permission UI
        // gate. Tuple-bound PackageInstaller transactions remain untouched.
        UpdateInstallerUiGate.permissionUiFinished(this);
        preferences.unregisterOnSharedPreferenceChangeListener(installResultListener);
        worker.shutdownNow();
    }
    private static final class DeferredException extends Exception { }
    private static final class PolicyChangedException extends Exception { }
    private static final class RemoteCandidate {
        final byte[] envelope;
        final ReleaseManifest manifest;
        final ReleaseManifest.Artifact artifact;
        RemoteCandidate(byte[] envelope, ReleaseManifest manifest, ReleaseManifest.Artifact artifact) {
            this.envelope = envelope; this.manifest = manifest; this.artifact = artifact;
        }
    }
    private static final class RemoteRelease {
        final byte[] envelope;
        final ReleaseManifest manifest;
        final ReleaseManifest.Artifact artifact;
        final List<RemoteCandidate> candidates;
        RemoteRelease(byte[] envelope, ReleaseManifest manifest, ReleaseManifest.Artifact artifact,
                      List<RemoteCandidate> candidates) {
            this.envelope = envelope; this.manifest = manifest; this.artifact = artifact;
            this.candidates = candidates;
        }
    }
    private static final class Verified {
        final ReleaseManifest.Artifact artifact; final File apk;
        Verified(ReleaseManifest.Artifact artifact, File apk) { this.artifact = artifact; this.apk = apk; }
    }
}
