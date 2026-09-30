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
import java.util.HashSet;
import java.util.Locale;
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
    private static final String PREFS = "stable-updates";
    private static final String AUTO = "auto-download";
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
    private final UpdateStateMachine machine = new UpdateStateMachine();
    private final SharedPreferences preferences;
    private final SharedPreferences.OnSharedPreferenceChangeListener installResultListener;
    private final File updateDirectory;
    private volatile boolean foreground;
    private volatile boolean closed;
    private volatile String status;
    private volatile ReleaseManifest.Artifact available;
    private final Runnable periodic = new Runnable() {
        @Override public void run() {
            if (!foreground || closed) return;
            checkNow(false);
            ui.postDelayed(this, PERIOD_MS);
        }
    };

    AndroidUpdateController(Activity activity, Host host) {
        this.activity = activity;
        this.host = host;
        preferences = activity.getSharedPreferences(PREFS, Activity.MODE_PRIVATE);
        installResultListener = (ignored, key) -> {
            if (UpdateInstallReceiver.RESULT_STATUS.equals(key)) consumeInstallResult();
        };
        preferences.registerOnSharedPreferenceChangeListener(installResultListener);
        updateDirectory = new File(activity.getFilesDir(), "updates");
        machine.setAutoDownload(autoDownloadEnabled());
        String previousInstall = preferences.getString(UpdateInstallReceiver.RESULT_MESSAGE, null);
        status = previousInstall != null ? previousInstall
                : configured() ? "尚未检查正式版更新" : "更新服务尚未配置";
        worker.execute(this::cleanInterruptedDownloads);
    }

    boolean autoDownloadEnabled() { return preferences.getBoolean(AUTO, true); }

    void setAutoDownloadEnabled(boolean enabled) {
        preferences.edit().putBoolean(AUTO, enabled).apply();
        UpdateStateMachine.Action action = machineAction(() -> { machine.setAutoDownload(enabled); return machine.sessionChanged(host.hasActiveDisplaySession()); });
        publish(enabled ? "已开启正式版自动下载" : "已关闭正式版自动下载");
        perform(action);
    }

    String status() { return status; }

    void onForeground() {
        foreground = true;
        machine.sessionChanged(host.hasActiveDisplaySession());
        host.updateStatusChanged(status);
        ui.removeCallbacks(periodic);
        ui.postDelayed(periodic, PERIOD_MS);
        if (preferences.contains(UpdateInstallReceiver.RESULT_STATUS))
            publish(preferences.getString(UpdateInstallReceiver.RESULT_MESSAGE, "Android 安装状态已更新"));
        else if (machine.state() == UpdateStateMachine.State.AWAITING_PERMISSION)
            publish("请允许 TabLink 安装未知应用，然后返回继续更新");
        else if (machine.state() == UpdateStateMachine.State.INSTALLING)
            publish("Android 安装未完成时，可在设置中继续更新");
        else inspectStagedThenCheck();
    }

    void onBackground() {
        foreground = false;
        ui.removeCallbacks(periodic);
    }

    void onSessionChanged() {
        UpdateStateMachine.Action action = machine.sessionChanged(host.hasActiveDisplaySession());
        perform(action);
    }

    void checkNow(boolean explicit) {
        if (closed) return;
        if (!configured()) {
            publish("更新服务尚未配置");
            return;
        }
        if (!operationRunning.compareAndSet(false, true)) {
            if (explicit) publish("正在检查或下载正式版更新…");
            return;
        }
        machine.beginCheck();
        publish("正在检查正式版更新…");
        worker.execute(() -> {
            try {
                byte[] envelope = fetch(BuildConfig.UPDATE_MANIFEST_URL, ReleaseManifest.MAX_MANIFEST_BYTES);
                ReleaseManifest manifest = ReleaseManifest.verify(envelope, BuildConfig.UPDATE_PUBLIC_KEY_SPKI, System.currentTimeMillis());
                ReleaseManifest.Artifact artifact = manifest.androidArtifact();
                if (artifact == null || !artifact.isNewerThan(BuildConfig.VERSION_CODE, BuildConfig.VERSION_NAME)) {
                    invalidateStaged();
                    available = null;
                    machine.checked(false);
                    publish("当前已是最新正式版 " + BuildConfig.VERSION_NAME);
                    return;
                }
                if (!manifest.includesInstallation(installationId())) {
                    invalidateStaged();
                    available = artifact;
                    machine.checked(false);
                    publish("正式版 " + artifact.version + " 正在分批发布，暂未轮到此设备");
                    return;
                }
                invalidateIfDifferent(artifact);
                available = artifact;
                if (validateStaged(envelope, artifact, false)) {
                    UpdateStateMachine.Action action = machine.downloaded();
                    publish(host.hasActiveDisplaySession()
                            ? "正式版 " + artifact.version + " 已下载，将在副屏断开后安装"
                            : "正式版 " + artifact.version + " 已下载，等待安装确认");
                    perform(action);
                    return;
                }
                UpdateStateMachine.Action action = machine.checked(true);
                if (explicit && action == UpdateStateMachine.Action.NONE) action = machine.requestDownload();
                if (action == UpdateStateMachine.Action.DOWNLOAD) download(envelope, artifact);
                else {
                    publish("发现正式版 " + artifact.version + "；副屏断开后自动下载");
                    if (explicit && host.hasActiveDisplaySession())
                        ui.post(() -> host.offerDisconnectForDownload(artifact));
                }
            } catch (Exception failure) {
                machine.failed();
                publish("检查更新失败：" + visibleFailure(failure));
            } finally { operationRunning.set(false); }
        });
    }

    void requestDownloadOrInstall() {
        if (available != null && machine.hasStagedUpdate()) {
            host.offerInstall(available, host.hasActiveDisplaySession());
            return;
        }
        UpdateStateMachine.Action action = machine.requestDownload();
        if (action == UpdateStateMachine.Action.DOWNLOAD) checkNow(true);
        else if (host.hasActiveDisplaySession() && available != null) {
            publish("已发现正式版 " + available.version + "；请断开副屏后下载，或选择断开并更新");
            host.offerDisconnectForDownload(available);
        } else checkNow(true);
    }

    void continueAfterExplicitDisconnect() {
        if (host.hasActiveDisplaySession()) return;
        if (operationRunning.get()) {
            ui.postDelayed(this::continueAfterExplicitDisconnect, 250);
            return;
        }
        checkNow(true);
    }

    void installPending(boolean explicit) {
        if (closed || !foreground) return;
        if (host.hasActiveDisplaySession() && !explicit) {
            publish("更新已就绪，副屏断开后再安装");
            return;
        }
        if (!operationRunning.compareAndSet(false, true)) return;
        worker.execute(() -> {
            try {
                Verified staged = readAndValidateStaged();
                boolean permission = canInstallPackages();
                if (!machine.beginInstall(explicit, permission)) return;
                archiveCurrentPackage();
                if (!permission) ui.post(this::openUnknownSourcesSettings);
                else commitPackageInstaller(staged);
            } catch (Exception failure) {
                machine.failed();
                publish("无法安装更新：" + visibleFailure(failure));
            } finally { operationRunning.set(false); }
        });
    }

    void onResume() {
        if (!foreground || closed) return;
        if (consumeInstallResult()) return;
        if (machine.state() == UpdateStateMachine.State.INSTALLING) {
            machine.installerReturned();
            publish("安装未完成；可在设置中点击“检查 / 继续正式版更新”重试");
            return;
        }
        if (machine.state() != UpdateStateMachine.State.AWAITING_PERMISSION) return;
        boolean granted = canInstallPackages();
        machine.installPermissionReturned(granted);
        if (granted) installPending(true);
        else publish("请允许 TabLink 安装未知应用，然后返回继续更新");
    }

    private void inspectStagedThenCheck() {
        if (!configured()) { publish("更新服务尚未配置"); return; }
        worker.execute(() -> {
            try {
                Verified verified = readAndValidateStaged();
                available = verified.artifact;
                UpdateStateMachine.Action action = machine.downloaded();
                publish(host.hasActiveDisplaySession()
                        ? "正式版 " + verified.artifact.version + " 已下载，将在副屏断开后安装"
                        : "正式版 " + verified.artifact.version + " 已下载，等待安装确认");
                perform(action);
            } catch (Exception ignored) {
                invalidateStaged();
                checkNow(false);
            }
        });
    }

    private void download(byte[] envelope, ReleaseManifest.Artifact artifact) throws Exception {
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
        HttpsURLConnection connection = openHttps(artifact.url);
        try {
            int code = connection.getResponseCode();
            if (code != HttpURLConnection.HTTP_OK) throw new IOException("下载服务器返回 HTTP " + code);
            long declared = connection.getContentLength();
            if (declared >= 0 && declared != artifact.size) throw new IOException("更新包大小与清单不一致");
            try (InputStream input = connection.getInputStream(); FileOutputStream output = new FileOutputStream(temporary)) {
                byte[] buffer = new byte[64 * 1024]; long total = 0;
                while (true) {
                    if (closed || host.hasActiveDisplaySession()) throw new DeferredException();
                    int count = input.read(buffer); if (count < 0) break; if (count == 0) continue;
                    total += count; if (total > artifact.size) throw new IOException("更新包超过清单大小");
                    output.write(buffer, 0, count);
                }
                output.getFD().sync();
            }
        } catch (DeferredException deferred) {
            deleteQuietly(temporary);
            machine.downloadDeferred(true);
            publish("副屏正在使用；已暂停更新下载，断开后会重试");
            return;
        } finally { connection.disconnect(); }
        if (!ArtifactIntegrity.verify(temporary, artifact.size, artifact.sha256)) {
            deleteQuietly(temporary); throw new IOException("更新包 SHA-256 或大小不匹配");
        }
        verifyApk(temporary, artifact);
        atomicReplace(temporary, destination);
        writeAtomic(new File(updateDirectory, MANIFEST_FILE), envelope);
        cleanOtherApks(destination);
        UpdateStateMachine.Action action = machine.downloaded();
        publish(host.hasActiveDisplaySession()
                ? "正式版 " + artifact.version + " 已下载，将在副屏断开后安装"
                : "正式版 " + artifact.version + " 已下载，等待安装确认");
        perform(action);
    }

    private Verified readAndValidateStaged() throws Exception {
        File manifestFile = new File(updateDirectory, MANIFEST_FILE);
        byte[] envelope = readLimited(manifestFile, ReleaseManifest.MAX_MANIFEST_BYTES);
        ReleaseManifest manifest = ReleaseManifest.verify(envelope, BuildConfig.UPDATE_PUBLIC_KEY_SPKI, System.currentTimeMillis());
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

    private void openUnknownSourcesSettings() {
        try {
            Intent permission = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:" + activity.getPackageName()));
            activity.startActivity(permission);
            publish("请允许 TabLink 安装未知应用，然后返回继续更新");
        } catch (ActivityNotFoundException | SecurityException failure) {
            machine.installerReturned();
            publish("无法打开“安装未知应用”设置");
        }
    }

    private void commitPackageInstaller(Verified staged) throws Exception {
        PackageInstaller installer = activity.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams parameters = new PackageInstaller.SessionParams(
                PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        parameters.setAppPackageName(activity.getPackageName());
        parameters.setSize(staged.artifact.size);
        if (Build.VERSION.SDK_INT >= 31)
            parameters.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        int sessionId = installer.createSession(parameters);
        boolean committed = false;
        try (PackageInstaller.Session installSession = installer.openSession(sessionId);
             InputStream input = new FileInputStream(staged.apk);
             OutputStream output = installSession.openWrite("base.apk", 0, staged.artifact.size)) {
            byte[] buffer = new byte[64 * 1024]; long total = 0;
            while (true) {
                int count = input.read(buffer); if (count < 0) break; if (count == 0) continue;
                total += count; if (total > staged.artifact.size) throw new IOException("APK 在提交时大小发生变化");
                output.write(buffer, 0, count);
            }
            if (total != staged.artifact.size) throw new IOException("APK 在提交时大小发生变化");
            installSession.fsync(output);
            Intent result = new Intent(activity, UpdateInstallReceiver.class).setAction(UpdateInstallReceiver.ACTION);
            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= 31) flags |= PendingIntent.FLAG_MUTABLE;
            PendingIntent callback = PendingIntent.getBroadcast(activity, staged.artifact.build, result, flags);
            installSession.commit(callback.getIntentSender());
            committed = true;
            publish("已提交正式版 " + staged.artifact.version + "；等待 Android 安装结果");
        } catch (Exception sessionFailure) {
            try { installer.abandonSession(sessionId); } catch (RuntimeException ignored) { }
            if (!foreground) throw sessionFailure;
            ui.post(() -> launchFileProviderFallback(staged.artifact));
        }
        if (!committed) publish("系统安装会话不可用，正在打开兼容安装界面…");
    }

    private void launchFileProviderFallback(ReleaseManifest.Artifact artifact) {
        try {
            File apk = new File(updateDirectory, ArtifactIntegrity.safeApkName(artifact.build));
            Uri uri = new Uri.Builder().scheme("content").authority(UpdateApkProvider.AUTHORITY)
                    .appendPath("apk").appendPath(apk.getName()).build();
            Intent install = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            activity.startActivity(install);
            publish("已打开 Android 兼容安装界面；请确认安装正式版 " + artifact.version);
        } catch (ActivityNotFoundException | SecurityException failure) {
            machine.installerReturned();
            publish("无法打开 Android 安装程序");
        }
    }

    private boolean consumeInstallResult() {
        int installerStatus = preferences.getInt(UpdateInstallReceiver.RESULT_STATUS, Integer.MIN_VALUE);
        if (installerStatus == Integer.MIN_VALUE) return false;
        String message = preferences.getString(UpdateInstallReceiver.RESULT_MESSAGE, "Android 安装状态已更新");
        preferences.edit().remove(UpdateInstallReceiver.RESULT_STATUS)
                .remove(UpdateInstallReceiver.RESULT_MESSAGE).apply();
        if (installerStatus == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            machine.installResult(UpdateStateMachine.InstallResult.PENDING_USER_ACTION);
            publish(message);
            return true;
        }
        if (installerStatus == PackageInstaller.STATUS_SUCCESS) {
            machine.installResult(UpdateStateMachine.InstallResult.SUCCESS);
            publish(message);
            return true;
        }
        machine.installResult(UpdateStateMachine.InstallResult.RETRY);
        publish(message + "；可在设置中重试");
        return true;
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
        return a != null && b != null && a.build == b.build && a.size == b.size && a.version.equals(b.version)
                && a.sha256.equals(b.sha256) && a.url.equals(b.url);
    }
    private void invalidateStaged() {
        if (!updateDirectory.isDirectory()) return;
        File[] files = updateDirectory.listFiles();
        if (files != null) for (File file : files)
            if (MANIFEST_FILE.equals(file.getName()) || ArtifactIntegrity.isSafeApkName(file.getName())) deleteQuietly(file);
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

    private byte[] fetch(String url, long limit) throws IOException {
        HttpsURLConnection connection = openHttps(url);
        try {
            int code = connection.getResponseCode();
            if (code != HttpURLConnection.HTTP_OK) throw new IOException("更新服务器返回 HTTP " + code);
            long declared = connection.getContentLength();
            if (declared > limit) throw new IOException("更新清单过大");
            try (InputStream input = connection.getInputStream()) { return readLimited(input, limit); }
        } finally { connection.disconnect(); }
    }
    private static HttpsURLConnection openHttps(String address) throws IOException {
        URL url = new URL(address);
        if (!"https".equalsIgnoreCase(url.getProtocol())) throw new IOException("更新地址必须使用 HTTPS");
        HttpsURLConnection connection = (HttpsURLConnection) url.openConnection();
        connection.setConnectTimeout(12_000); connection.setReadTimeout(30_000);
        connection.setInstanceFollowRedirects(false); connection.setUseCaches(false);
        connection.setRequestProperty("Accept", "application/json, application/vnd.android.package-archive");
        connection.setRequestProperty("User-Agent", "TabLink-Android/" + BuildConfig.VERSION_NAME);
        return connection;
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
    private boolean configured() { return BuildConfig.UPDATE_MANIFEST_URL != null && !BuildConfig.UPDATE_MANIFEST_URL.isEmpty(); }
    private void perform(UpdateStateMachine.Action action) {
        if (action == UpdateStateMachine.Action.DOWNLOAD && foreground) checkNow(false);
        else if (action == UpdateStateMachine.Action.PROMPT_INSTALL && available != null && foreground)
            ui.post(() -> { if (foreground && host.isForeground()) host.offerInstall(available, host.hasActiveDisplaySession()); });
    }
    private UpdateStateMachine.Action machineAction(ActionCall call) {
        try { return call.run(); } catch (Exception impossible) { return UpdateStateMachine.Action.NONE; }
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
        closed = true; foreground = false; ui.removeCallbacks(periodic);
        preferences.unregisterOnSharedPreferenceChangeListener(installResultListener);
        worker.shutdownNow();
    }
    private interface ActionCall { UpdateStateMachine.Action run(); }
    private static final class DeferredException extends Exception { }
    private static final class Verified {
        final ReleaseManifest.Artifact artifact; final File apk;
        Verified(ReleaseManifest.Artifact artifact, File apk) { this.artifact = artifact; this.apk = apk; }
    }
}
