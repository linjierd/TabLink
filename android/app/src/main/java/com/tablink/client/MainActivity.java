package com.tablink.client;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.ActivityNotFoundException;
import android.content.ClipboardManager;
import android.content.ClipData;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.hardware.display.DisplayManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.net.Uri;
import android.os.Bundle;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.Gravity;
import android.view.Display;
import android.view.Surface;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowManager;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.text.Editable;
import android.text.TextWatcher;
import android.text.InputType;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;

import org.json.JSONException;
import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.atomic.AtomicLong;
import java.util.Locale;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLHandshakeException;

public final class MainActivity extends Activity {
    private enum ConfigurationDecision { APPLIED, CONFIRMATION_REQUIRED, IGNORED }
    private static final String ACTION_SET_RENDER_PACING = "com.tablink.client.SET_RENDER_PACING";
    private static final String ACTION_APPLY_ADB_SESSION = "com.tablink.client.APPLY_ADB_SESSION";
    private static final String EXTRA_ADB_ACTIVATION = "adbActivation";
    private static final int SCAN_REQUEST = 71;
    private static final long METRIC_FRESHNESS_MILLIS = 5000;
    private static final int BACKGROUND = Color.rgb(16, 25, 34);
    private static final int ACCENT = Color.rgb(110, 231, 183);
    private final Handler ui = new Handler(Looper.getMainLooper());
    private volatile Session session;
    private DesktopSurface desktop;
    private VideoSurface videoSurface;
    private FrameLayout content;
    private TextView status;
    private TextView help;
    private View connectionPanel;
    private Button reconnectPairing;
    private TextView performance;
    private TextView settingsStatus;
    private AlertDialog settingsDialog;
    private AlertDialog updateInstallDialog;
    private AlertDialog pairingReplacementDialog;
    private TextView updateStatus;
    private AndroidUpdateController updater;
    private SharedPreferences preferences;
    private HudStyle hudStyle;
    private String token;
    private PairingLink networkPairing;
    private PairingLink pendingExternalPairing;
    private TrustedComputer trustedComputer;
    private final Object trustedComputerLock = new Object();
    private int port = 27183;
    private String configurationError;
    private volatile boolean renderPacingEnabled = true;
    private boolean activityStarted;
    private Context localizedContext;
    private boolean brightnessOverrideActive;
    private float previousWindowBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE;
    private Session brightnessOwner;
    private final Runnable performanceTicker = new Runnable() {
        @Override public void run() {
            if (!activityStarted) return;
            Session current = session;
            if (current != null) {
                updatePerformance(current);
                current.sendReceiverFeedback();
            }
            ui.postDelayed(this, 1000);
        }
    };
    private final DisplayManager.DisplayListener displayListener = new DisplayManager.DisplayListener() {
        @Override public void onDisplayAdded(int id) { }
        @Override public void onDisplayRemoved(int id) { }
        @Override public void onDisplayChanged(int id) {
            Session current = session;
            if (current != null) {
                updateStreamingBrightness(current);
                current.sendDisplayProfile();
            }
        }
    };

    @Override protected void attachBaseContext(Context base) {
        super.attachBaseContext(AppLanguage.wrap(base));
    }

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        localizedContext = this;
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        preferences = getSharedPreferences("display", MODE_PRIVATE);
        hudStyle = new HudStyle(preferences.getInt("hudPosition", 0),
                preferences.getInt("hudColor", HudStyle.DEFAULT_COLOR),
                preferences.getInt("hudTransparency", HudStyle.DEFAULT_TRANSPARENCY));
        createUi();
        // Versions before 0.8.8 stored a reusable QR bearer token. It is not a
        // long-term trust record and must never be promoted silently.
        getSharedPreferences("pairing", MODE_PRIVATE).edit().remove("lastLink").apply();
        updater = new AndroidUpdateController(this, new AndroidUpdateController.Host() {
            @Override public boolean isForeground() { return activityStarted; }
            @Override public boolean hasActiveDisplaySession() {
                Session current = session;
                return current != null && current.connected;
            }
            @Override public void updateStatusChanged(String value) {
                if (updateStatus != null) updateStatus.setText(value);
            }
            @Override public void offerInstall(ReleaseManifest.Artifact artifact, boolean activeSession) {
                showUpdateInstallPrompt(artifact, activeSession);
            }
            @Override public void offerDisconnectForDownload(ReleaseManifest.Artifact artifact) {
                showUpdateDownloadPrompt(artifact);
            }
        });
        readConfiguration(getIntent(), false);
        enterImmersive(getWindow());
        if (Build.VERSION.SDK_INT >= 33)
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::showSettings);
    }

    @Override protected void onStart() {
        super.onStart();
        activityStarted = true;
        ((DisplayManager) getSystemService(DISPLAY_SERVICE)).registerDisplayListener(displayListener, ui);
        requestPreferredDisplayMode();
        if (pendingExternalPairing != null) showPairingReplacementConfirmation();
        else connect();
        ui.removeCallbacks(performanceTicker);
        ui.post(performanceTicker);
        updater.onForeground();
    }

    @Override protected void onResume() {
        super.onResume();
        if (updater != null) updater.onResume();
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        if (ACTION_SET_RENDER_PACING.equals(intent.getAction())) {
            if (intent.hasExtra("renderPacing")) {
                renderPacingEnabled = intent.getBooleanExtra("renderPacing", false);
                Session current = session;
                VideoDecoder decoder = current == null ? null : current.video;
                if (decoder != null) decoder.setRenderPacing(renderPacingEnabled);
            }
            // Keep the original authenticated launch intent and the live TCP session.
            return;
        }
        ConfigurationDecision decision = readConfiguration(intent, true);
        if (activityStarted) {
            if (decision == ConfigurationDecision.APPLIED) connect();
            else if (decision == ConfigurationDecision.CONFIRMATION_REQUIRED)
                showPairingReplacementConfirmation();
        }
    }

    @Override protected void onStop() {
        activityStarted = false;
        ui.removeCallbacks(performanceTicker);
        updater.onBackground();
        ((DisplayManager) getSystemService(DISPLAY_SERVICE)).unregisterDisplayListener(displayListener);
        DisplayCapabilities.recordRequestedMode(0, 0);
        disconnect();
        super.onStop();
    }

    @Override protected void onDestroy() {
        if (settingsDialog != null) settingsDialog.dismiss();
        if (updateInstallDialog != null) updateInstallDialog.dismiss();
        if (pairingReplacementDialog != null) pairingReplacementDialog.dismiss();
        disconnect();
        updater.close();
        ui.removeCallbacksAndMessages(null);
        super.onDestroy();
    }

    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        requestPreferredDisplayMode();
        Session current = session;
        if (current != null) current.sendDisplayProfile();
        applyHudStyle(false);
        enterImmersive(getWindow());
    }

    @SuppressWarnings("deprecation")
    @Override public void onBackPressed() { showSettings(); }

    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused) enterImmersive(getWindow());
    }

    private ConfigurationDecision readConfiguration(Intent intent, boolean reentry) {
        Session activeSession = session;
        boolean hasLiveSession = activeSession != null && activeSession.running;
        AdbSessionConfiguration.Pending adbConfiguration = null;
        if (ACTION_APPLY_ADB_SESSION.equals(intent.getAction()))
            adbConfiguration = AdbSessionConfiguration.consume(intent.getStringExtra(EXTRA_ADB_ACTIVATION));
        boolean sameUsbConfiguration = adbConfiguration != null && activeSession != null
                && adbConfiguration.port == activeSession.sessionPort
                && adbConfiguration.token.equals(activeSession.sessionToken);
        AdbSessionHandoffPolicy.Decision adbHandoff = AdbSessionHandoffPolicy.decide(
                adbConfiguration != null, hasLiveSession,
                activeSession != null && activeSession.connected,
                activeSession != null && activeSession.identity == null,
                sameUsbConfiguration);
        if (adbHandoff.applyConfiguration) {
            if (adbHandoff.stopActiveSession) disconnect();
            pendingExternalPairing = null;
            configurationError = null;
            networkPairing = null;
            trustedComputer = null;
            token = adbConfiguration.token;
            port = adbConfiguration.port;
            setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
            return ConfigurationDecision.APPLIED;
        }
        if (adbConfiguration != null || ACTION_APPLY_ADB_SESSION.equals(intent.getAction()) && hasLiveSession) {
            // The one-shot value has already been consumed. Never retain it in
            // the exported Activity Intent or let a forged marker alter state.
            return ConfigurationDecision.IGNORED;
        }

        String pairing = intent.getDataString();
        PairingLink parsedPairing = null;
        if (pairing != null) {
            try {
                parsedPairing = PairingLink.parse(pairing);
                TrustedComputer saved = loadTrustedComputer();
                if (reentry && hasLiveSession) {
                    String activeCertificate = trustedComputer != null
                            ? trustedComputer.certificateSha256
                            : networkPairing != null ? networkPairing.certificateSha256 : null;
                    if (parsedPairing.certificateSha256.equals(activeCertificate)) {
                        android.widget.Toast.makeText(this, tr(R.string.toast_current_trusted_computer),
                                android.widget.Toast.LENGTH_SHORT).show();
                        return ConfigurationDecision.IGNORED;
                    }
                    pendingExternalPairing = parsedPairing;
                    configurationError = null;
                    return ConfigurationDecision.CONFIRMATION_REQUIRED;
                }
                // MainActivity is exported. Another app may choose any action string,
                // so every pairing URI delivered through an Activity Intent is external.
                // Scanner and paste flows call acceptPairing() directly instead.
                if (PairingIntentPolicy.requiresReplacementConfirmation(true, saved, parsedPairing)) {
                    pendingExternalPairing = parsedPairing;
                    configurationError = null;
                    return ConfigurationDecision.CONFIRMATION_REQUIRED;
                }
            } catch (IllegalArgumentException invalid) {
                if (reentry && hasLiveSession) {
                    android.widget.Toast.makeText(this, tr(R.string.toast_invalid_connection_ignored),
                            android.widget.Toast.LENGTH_SHORT).show();
                    return ConfigurationDecision.IGNORED;
                }
                // Invalid initial links follow the normal configuration-error path below.
            }
        } else if (reentry && hasLiveSession) {
            // Tapping the launcher or another app explicitly starting the exported
            // activity must not tear down a healthy display session. ADB launches
            // remain accepted while idle, when there is no session to disrupt.
            return ConfigurationDecision.IGNORED;
        }
        pendingExternalPairing = null;
        configurationError = null;
        networkPairing = null;
        trustedComputer = null;
        renderPacingEnabled = intent.getBooleanExtra("renderPacing", true);
        if (pairing != null) {
            token = null;
            try {
                PairingLink parsed = parsedPairing != null ? parsedPairing : PairingLink.parse(pairing);
                TrustedComputer saved = loadTrustedComputer();
                if (saved != null && saved.matchesCertificate(parsed)) {
                    // Android may recreate a singleTask activity with its original VIEW
                    // intent after the one-time bearer was consumed. Once this certificate
                    // has been enrolled, treat that URI only as an authenticated route hint
                    // and reconnect with a fresh signed challenge instead of replaying it.
                    trustedComputer = saved.withEndpoint(parsed.host, parsed.port);
                    port = trustedComputer.port;
                    setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
                } else {
                    networkPairing = parsed;
                    token = parsed.token();
                    port = parsed.port;
                    setIntent(intent);
                }
            } catch (IllegalArgumentException invalid) {
                configurationError = invalid.getMessage();
                setIntent(intent);
            }
            return ConfigurationDecision.APPLIED;
        }
        // MainActivity is exported for launcher and tablink:// links. Raw ADB
        // credentials in extras are therefore never trusted here; only the
        // DUMP-protected provider can publish a matching one-shot marker.
        token = null;
        port = 27183;
        if (!intent.getBooleanExtra("profileOnly", false)) {
            trustedComputer = loadTrustedComputer();
            if (trustedComputer != null) port = trustedComputer.port;
        }
        setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
        return ConfigurationDecision.APPLIED;
    }

    private void createUi() {
        content = new FrameLayout(this);
        content.setBackgroundColor(Color.BLACK);
        videoSurface = new VideoSurface();
        videoSurface.setVisibility(View.GONE);
        content.addView(videoSurface, new FrameLayout.LayoutParams(-1, -1, Gravity.CENTER));
        desktop = new DesktopSurface();
        content.addView(desktop, new FrameLayout.LayoutParams(-1, -1));
        content.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> {
            videoSurface.updateLayout();
            performance.setMaxWidth(Math.max(dp(100), r - l - dp(24)));
        });
        createConnectionPanel();
        performance = text(tr(R.string.status_waiting_connection), 12, Color.WHITE);
        performance.setBackgroundColor(Color.TRANSPARENT);
        performance.setShadowLayer(dp(1), 0, 0, Color.BLACK);
        performance.setPadding(dp(8), dp(8), dp(8), dp(8));
        performance.setMinHeight(dp(44));
        performance.setGravity(Gravity.CENTER_VERTICAL);
        performance.setContentDescription(tr(R.string.hud_content_description));
        performance.setOnLongClickListener(v -> { showSettings(); return true; });
        content.addView(performance, new FrameLayout.LayoutParams(-2, -2));
        applyHudStyle(false);
        setContentView(content);
    }

    private void createConnectionPanel() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BACKGROUND);
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setGravity(Gravity.CENTER_VERTICAL);
        panel.setPadding(dp(28), dp(60), dp(28), dp(32));
        TextView title = text(tr(R.string.home_title), 28, Color.WHITE);
        title.setTypeface(null, android.graphics.Typeface.BOLD);
        panel.addView(title);
        help = text(tr(R.string.home_subtitle), 16, Color.rgb(190, 204, 219));
        help.setPadding(0, dp(12), 0, dp(22));
        panel.addView(help);
        Button scan = button(tr(R.string.scan_computer_qr));
        scan.setBackgroundTintList(android.content.res.ColorStateList.valueOf(ACCENT));
        scan.setTextColor(BACKGROUND);
        scan.setOnClickListener(v -> startScanner());
        panel.addView(scan, new LinearLayout.LayoutParams(-1, dp(56)));
        Button paste = button(tr(R.string.paste_connection_link));
        paste.setOnClickListener(v -> showPasteDialog());
        panel.addView(paste, new LinearLayout.LayoutParams(-1, dp(56)));
        reconnectPairing = button(tr(R.string.reconnect_last_computer));
        reconnectPairing.setOnClickListener(v -> {
            TrustedComputer saved = loadTrustedComputer();
            if (saved != null) {
                disconnect();token = null;networkPairing = null;trustedComputer = saved;port = saved.port;
                configurationError = null;
                setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
                if (activityStarted) connect();
            }
        });
        panel.addView(reconnectPairing, new LinearLayout.LayoutParams(-1, dp(56)));
        refreshSavedPairing();
        TextView instructions = text(tr(R.string.connection_instructions), 14, Color.LTGRAY);
        instructions.setPadding(0, dp(22), 0, dp(10));
        panel.addView(instructions);
        Button tether = button(tr(R.string.open_usb_tethering_settings));
        tether.setOnClickListener(v -> openTetherSettings());
        panel.addView(tether, new LinearLayout.LayoutParams(-1, dp(52)));
        TextView legacy = text(tr(R.string.legacy_adb_hint), 12, Color.GRAY);
        legacy.setPadding(0, dp(14), 0, dp(12));
        panel.addView(legacy);
        status = text(tr(R.string.status_choose_connection), 14, ACCENT);
        status.setPadding(0, dp(8), 0, dp(8));
        panel.addView(status);
        scroll.addView(panel, new ScrollView.LayoutParams(-1, -1));
        connectionPanel = scroll;
        content.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
    }

    private void refreshSavedPairing() {
        TrustedComputer previous = loadTrustedComputer();
        try {
            if (previous == null) throw new IllegalArgumentException("missing");
            reconnectPairing.setText(tr(R.string.reconnect_trusted_computer, previous.lastHost));
            reconnectPairing.setVisibility(View.VISIBLE);
        } catch (IllegalArgumentException missing) {
            reconnectPairing.setVisibility(View.GONE);
        }
    }

    private TrustedComputer loadTrustedComputer() {
        SharedPreferences saved = getSharedPreferences("trustedComputer", MODE_PRIVATE);
        try {
            String hostId = saved.getString("hostId", null);
            String certificate = saved.getString("certificateSha256", null);
            String host = saved.getString("lastHost", null);
            int savedPort = saved.getInt("port", 0);
            if (hostId == null || certificate == null || host == null) return null;
            return new TrustedComputer(hostId, certificate, host, savedPort);
        } catch (IllegalArgumentException | ClassCastException invalid) {
            // apply() removes the malformed record from this process immediately; disk
            // cleanup can stay asynchronous because no key material lives in preferences.
            saved.edit().clear().apply();
            return null;
        }
    }

    private static boolean writeTrustedComputer(SharedPreferences target, TrustedComputer computer) {
        SharedPreferences.Editor edit = target.edit().clear();
        if (computer != null) {
            edit.putString("hostId", computer.hostId)
                    .putString("certificateSha256", computer.certificateSha256)
                    .putString("lastHost", computer.lastHost)
                    .putInt("port", computer.port);
        }
        return edit.commit();
    }

    private void persistTrustedComputerFromSession(Session source, TrustedComputer computer) throws IOException {
        synchronized (trustedComputerLock) {
            if (session != source || !source.running)
                throw new IOException("trusted-record-stale-session");
            boolean saved = getSharedPreferences("trustedComputer", MODE_PRIVATE).edit()
                    .putString("hostId", computer.hostId)
                    .putString("certificateSha256", computer.certificateSha256)
                    .putString("lastHost", computer.lastHost)
                    .putInt("port", computer.port).commit();
            if (!saved) throw new IOException("trusted-record-save-failed");
        }
        ui.post(() -> {
            if (session != source) return;
            trustedComputer = computer;
            token = null;
            networkPairing = null;
            port = computer.port;
            setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
            refreshSavedPairing();
        });
    }

    private TrustedComputerForgetCoordinator.Outcome forgetTrustedComputer() {
        synchronized (trustedComputerLock) {
            TrustedComputer previous = loadTrustedComputer();
            SharedPreferences metadata = getSharedPreferences("trustedComputer", MODE_PRIVATE);
            TrustedComputerForgetCoordinator.Outcome outcome = TrustedComputerForgetCoordinator.forget(previous,
                    new TrustedComputerForgetCoordinator.Actions() {
                        @Override public boolean clearMetadata() {
                            return writeTrustedComputer(metadata, null);
                        }
                        @Override public boolean restoreMetadata(TrustedComputer value) {
                            return writeTrustedComputer(metadata, value);
                        }
                        @Override public TrustedComputerForgetCoordinator.IdentityRemoval deleteIdentity() {
                            return TrustedDeviceIdentity.delete();
                        }
                    });
            boolean mustDiscardRuntimeTrust = outcome == TrustedComputerForgetCoordinator.Outcome.SUCCESS
                    || outcome == TrustedComputerForgetCoordinator.Outcome.IDENTITY_STATE_UNKNOWN
                    || outcome == TrustedComputerForgetCoordinator.Outcome.ROLLBACK_FAILED;
            if (mustDiscardRuntimeTrust) {
                // No supported code reads this pre-0.8.8 bearer cache. Remove it
                // synchronously as hygiene; also discard the current Activity URI so
                // lifecycle restart cannot recreate a key and replay a pending token.
                getSharedPreferences("pairing", MODE_PRIVATE).edit().remove("lastLink").commit();
                trustedComputer = null;
                pendingExternalPairing = null;
                token = null;
                networkPairing = null;
                configurationError = null;
                setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
            }
            return outcome;
        }
    }

    private void changeConnection() {
        disconnect();
        token = null;
        networkPairing = null;
        trustedComputer = null;
        configurationError = null;
        setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
        if (settingsDialog != null) settingsDialog.dismiss();
        refreshSavedPairing();
        status.setText(tr(R.string.status_choose_connection_registered));
    }

    @SuppressWarnings("deprecation")
    private void startScanner() {
        changeConnection();
        startActivityForResult(new Intent(this, QrScannerActivity.class), SCAN_REQUEST);
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != SCAN_REQUEST) return;
        if (result == RESULT_OK && data != null) acceptPairing(data.getStringExtra("pairingLink"));
        else if (result == QrScannerActivity.RESULT_PASTE) ui.post(this::showPasteDialog);
    }

    private boolean acceptPairing(String link) {
        try {
            PairingLink paired = PairingLink.parse(link);
            pendingExternalPairing = null;
            networkPairing = paired;
            trustedComputer = null;
            token = paired.token();
            port = paired.port;
            configurationError = null;
            setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_VIEW)
                    .setData(android.net.Uri.parse(paired.toPrivateUri())));
            if (activityStarted) connect();
            return true;
        } catch (IllegalArgumentException invalid) {
            status.setText(tr(R.string.pairing_invalid));
            return false;
        }
    }

    private void showPairingReplacementConfirmation() {
        PairingLink pending = pendingExternalPairing;
        if (pending == null || isFinishing()) return;
        if (pairingReplacementDialog != null && pairingReplacementDialog.isShowing()) return;
        String fingerprint = pending.certificateSha256.substring(0, 12).toUpperCase(Locale.ROOT);
        Session activeSession = session;
        boolean replacing = loadTrustedComputer() != null || trustedComputer != null
                || activeSession != null && activeSession.running;
        AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle(tr(replacing ? R.string.pairing_replace_title : R.string.pairing_connect_title))
                .setMessage(tr(replacing ? R.string.pairing_replace_message : R.string.pairing_connect_message,
                        pending.host, fingerprint))
                .setPositiveButton(tr(R.string.action_allow_connect), (ignored, which) -> {
                    if (pendingExternalPairing != pending) return;
                    pendingExternalPairing = null;
                    acceptPairing(pending.toPrivateUri());
                })
                .setNegativeButton(tr(replacing ? R.string.action_keep_current_computer : R.string.action_cancel),
                        (ignored, which) -> rejectPairingReplacement(pending))
                .create();
        pairingReplacementDialog = dialog;
        dialog.setOnDismissListener(ignored -> {
            if (pairingReplacementDialog == dialog) pairingReplacementDialog = null;
            if (pendingExternalPairing == pending) rejectPairingReplacement(pending);
            else if (pendingExternalPairing != null && activityStarted)
                ui.post(this::showPairingReplacementConfirmation);
        });
        dialog.show();
        enterImmersive(dialog.getWindow());
    }

    private void rejectPairingReplacement(PairingLink rejected) {
        if (pendingExternalPairing != rejected) return;
        pendingExternalPairing = null;
        setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
        if (session == null) {
            token = null;
            networkPairing = null;
            trustedComputer = loadTrustedComputer();
            if (trustedComputer != null) port = trustedComputer.port;
            configurationError = null;
            status.setText(tr(trustedComputer == null ? R.string.pairing_cancelled : R.string.pairing_kept_original));
            if (activityStarted) connect();
        }
    }

    private void showPasteDialog() {
        EditText input = new EditText(this);
        input.setHint("tablink://connect?host=…");
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
                | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        input.setSaveEnabled(false);
        input.setMaxLines(5);
        input.setPadding(dp(18), dp(14), dp(18), dp(14));
        if (Build.VERSION.SDK_INT >= 26) input.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
        ClipboardManager clipboard = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
        ClipData clip = clipboard == null ? null : clipboard.getPrimaryClip();
        if (clip != null && clip.getItemCount() > 0) {
            CharSequence copied = clip.getItemAt(0).getText();
            if (copied != null && copied.length() <= 512 && copied.toString().startsWith("tablink://")) input.setText(copied);
        }
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle(tr(R.string.paste_dialog_title))
                .setMessage(tr(R.string.paste_dialog_message))
                .setView(input).setPositiveButton(tr(R.string.action_connect), null)
                .setNegativeButton(tr(R.string.action_cancel), null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            if (acceptPairing(input.getText().toString().trim())) dialog.dismiss();
            else input.setError(status.getText());
        }));
        dialog.show();
    }

    private void openTetherSettings() {
        try { startActivity(new Intent("android.settings.TETHER_SETTINGS")); }
        catch (ActivityNotFoundException | SecurityException unavailable) {
            try { startActivity(new Intent(Settings.ACTION_WIRELESS_SETTINGS)); }
            catch (ActivityNotFoundException | SecurityException alsoUnavailable) {
                status.setText(tr(R.string.manual_tether_settings));
            }
        }
    }

    @SuppressWarnings("deprecation")
    private void enterImmersive(Window window) {
        if (window == null) return;
        // Dialog.getWindow() can exist before PhoneWindow has created its decor.
        // Obtain/create it explicitly and tolerate a not-yet-attached window.
        View decor = window.getDecorView();
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false);
            WindowInsetsController controller = decor.getWindowInsetsController();
            if (controller != null) {
                controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                controller.hide(WindowInsets.Type.systemBars());
            }
        } else {
            decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
        }
    }

    private void applyHudStyle(boolean save) {
        int[] vertical = { Gravity.TOP, Gravity.CENTER_VERTICAL, Gravity.BOTTOM };
        int[] horizontal = { Gravity.LEFT, Gravity.CENTER_HORIZONTAL, Gravity.RIGHT };
        FrameLayout.LayoutParams layout = new FrameLayout.LayoutParams(-2, -2,
                vertical[hudStyle.position / 3] | horizontal[hudStyle.position % 3]);
        layout.setMargins(dp(12), dp(12), dp(12), dp(12));
        performance.setLayoutParams(layout);
        performance.setTextColor(hudStyle.color);
        performance.setAlpha(hudStyle.opacity());
        if (save) preferences.edit().putInt("hudPosition", hudStyle.position)
                .putInt("hudColor", hudStyle.color).putInt("hudTransparency", hudStyle.transparency).apply();
    }

    private void showSettings() {
        if (isFinishing() || settingsDialog != null && settingsDialog.isShowing()) return;
        desktop.releaseTouch();
        LinearLayout options = new LinearLayout(this);
        options.setOrientation(LinearLayout.VERTICAL);
        options.setPadding(dp(22), dp(6), dp(22), dp(12));
        settingsStatus = text(status.getText().toString(), 13, ACCENT);
        options.addView(settingsStatus);
        TextView instruction = text(tr(R.string.settings_instruction), 13, Color.LTGRAY);
        instruction.setPadding(0, dp(10), 0, dp(14));
        options.addView(instruction);
        options.addView(text(tr(R.string.language_title), 15, Color.WHITE));
        Spinner language = new Spinner(this);
        LanguagePreference.Mode[] languageModes = LanguagePreference.Mode.values();
        ArrayAdapter<String> languageAdapter = new ArrayAdapter<>(this,
                android.R.layout.simple_spinner_item, trArray(R.array.language_modes));
        languageAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        language.setAdapter(languageAdapter);
        LanguagePreference.Mode initialLanguage = AppLanguage.read(this);
        language.setSelection(initialLanguage.ordinal());
        language.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int index, long id) {
                LanguagePreference.Mode selected = languageModes[index];
                if (selected == AppLanguage.read(MainActivity.this)) return;
                if (!AppLanguage.save(MainActivity.this, selected)) {
                    settingsStatus.setText(tr(R.string.language_save_failed));
                    language.post(() -> language.setSelection(AppLanguage.read(MainActivity.this).ordinal()));
                    return;
                }
                localizedContext = AppLanguage.wrap(MainActivity.this);
                updater.onLanguageChanged();
                Session current = session;
                boolean activeDisplay = current != null && current.running;
                if (settingsDialog != null) settingsDialog.dismiss();
                if (LanguageSwitchPolicy.choose(activeDisplay)
                        == LanguageSwitchPolicy.Action.REFRESH_IN_PLACE) {
                    rebuildConnectionPanelForLanguage(current);
                    performance.setContentDescription(tr(R.string.hud_content_description));
                    videoSurface.setContentDescription(tr(R.string.desktop_h264_description));
                    desktop.setContentDescription(tr(R.string.desktop_touch_description));
                    updatePerformance(current);
                    android.widget.Toast.makeText(MainActivity.this, tr(R.string.language_applied_live),
                            android.widget.Toast.LENGTH_SHORT).show();
                } else {
                    recreate();
                }
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        options.addView(language, new LinearLayout.LayoutParams(-1, dp(48)));

        Button change = button(tr(R.string.settings_change_connection));
        change.setOnClickListener(v -> changeConnection());
        options.addView(change, new LinearLayout.LayoutParams(-1, dp(48)));
        Button forget = button(tr(R.string.settings_forget_computer));
        forget.setOnClickListener(v -> {
            disconnect();
            TrustedComputerForgetCoordinator.Outcome outcome = forgetTrustedComputer();
            refreshSavedPairing();
            if (outcome == TrustedComputerForgetCoordinator.Outcome.SUCCESS) {
                forget.setText(tr(R.string.forget_complete_button));
                settingsStatus.setText(tr(R.string.forget_complete_status));
                forget.setEnabled(false);
            } else if (outcome == TrustedComputerForgetCoordinator.Outcome.METADATA_CLEAR_FAILED) {
                forget.setText(tr(R.string.forget_retry_button));
                settingsStatus.setText(tr(R.string.forget_metadata_failed));
            } else if (outcome == TrustedComputerForgetCoordinator.Outcome.IDENTITY_DELETE_FAILED) {
                forget.setText(tr(R.string.forget_retry_button));
                settingsStatus.setText(tr(R.string.forget_identity_failed));
            } else if (outcome == TrustedComputerForgetCoordinator.Outcome.IDENTITY_STATE_UNKNOWN) {
                forget.setText(tr(R.string.forget_identity_unknown_button));
                settingsStatus.setText(tr(R.string.forget_identity_unknown));
            } else {
                forget.setText(tr(R.string.forget_rollback_failed_button));
                settingsStatus.setText(tr(R.string.forget_rollback_failed));
            }
        });
        options.addView(forget, new LinearLayout.LayoutParams(-1, dp(48)));
        options.addView(text(tr(R.string.update_settings_title), 15, Color.WHITE));
        Spinner updateMode = new Spinner(this);
        String[] updateModeNames = trArray(R.array.update_modes);
        UpdateStateMachine.Mode[] updateModes = {
                UpdateStateMachine.Mode.AUTOMATIC,
                UpdateStateMachine.Mode.DOWNLOAD_THEN_ASK,
                UpdateStateMachine.Mode.NEVER
        };
        ArrayAdapter<String> updateModeAdapter = new ArrayAdapter<>(this,
                android.R.layout.simple_spinner_item, updateModeNames);
        updateModeAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        updateMode.setAdapter(updateModeAdapter);
        UpdateStateMachine.Mode initialUpdateMode = updater.updateMode();
        updateMode.setSelection(initialUpdateMode.ordinal());
        options.addView(updateMode, new LinearLayout.LayoutParams(-1, dp(48)));
        updateStatus = text(updater.status(), 13, ACCENT);
        updateStatus.setPadding(0, dp(4), 0, dp(6));
        options.addView(updateStatus);
        Button checkUpdate = button(tr(R.string.update_check_continue));
        checkUpdate.setEnabled(initialUpdateMode != UpdateStateMachine.Mode.NEVER);
        checkUpdate.setOnClickListener(v -> updater.requestDownloadOrInstall());
        options.addView(checkUpdate, new LinearLayout.LayoutParams(-1, dp(48)));
        updateMode.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int index, long id) {
                UpdateStateMachine.Mode selected = updateModes[index];
                UpdateStateMachine.Mode applied = updater.updateMode() == selected
                        ? selected : updater.setUpdateMode(selected);
                checkUpdate.setEnabled(applied != UpdateStateMachine.Mode.NEVER);
                if (applied != selected)
                    updateMode.post(() -> updateMode.setSelection(applied.ordinal()));
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        TextView updateExplanation = text(tr(R.string.update_explanation), 12, Color.LTGRAY);
        updateExplanation.setPadding(0, 0, 0, dp(12));
        options.addView(updateExplanation);
        options.addView(text(tr(R.string.hud_position_title), 15, Color.WHITE));
        Spinner position = new Spinner(this);
        String[] positions = trArray(R.array.hud_positions);
        ArrayAdapter<String> positionAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, positions);
        positionAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        position.setAdapter(positionAdapter);
        position.setSelection(hudStyle.position);
        position.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int index, long id) {
                if (hudStyle.position == index) return;
                hudStyle = new HudStyle(index, hudStyle.color, hudStyle.transparency);
                applyHudStyle(true);
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        options.addView(position, new LinearLayout.LayoutParams(-1, dp(48)));
        options.addView(text(tr(R.string.hud_colour_title), 15, Color.WHITE));
        EditText color = new EditText(this);
        color.setSingleLine(true);
        color.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        color.setText(String.format(Locale.ROOT, "#%06X", hudStyle.color & 0xffffff));
        color.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                Integer parsed = HudStyle.parseColor(s.toString());
                color.setError(parsed == null ? tr(R.string.hud_colour_invalid) : null);
                if (parsed != null) {
                    hudStyle = new HudStyle(hudStyle.position, parsed, hudStyle.transparency);
                    applyHudStyle(true);
                }
            }
            @Override public void afterTextChanged(Editable value) { }
        });
        options.addView(color, new LinearLayout.LayoutParams(-1, -2));
        LinearLayout palette = new LinearLayout(this);
        String[] colorNames = trArray(R.array.hud_colours);
        String[] colorValues = { "#FFFFFF", "#6EE7B7", "#67E8F9", "#FDE047", "#000000" };
        for (int i = 0; i < colorNames.length; i++) {
            final String value = colorValues[i];
            Button swatch = button(colorNames[i]);
            swatch.setMinWidth(0);
            swatch.setOnClickListener(v -> color.setText(value));
            palette.addView(swatch, new LinearLayout.LayoutParams(0, dp(48), 1));
        }
        options.addView(palette);
        TextView transparency = text("", 15, Color.WHITE);
        options.addView(transparency);
        SeekBar opacity = new SeekBar(this);
        opacity.setMax(100);
        opacity.setProgress(hudStyle.transparency);
        transparency.setText(transparencyLabel(hudStyle.transparency));
        opacity.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar bar, int progress, boolean fromUser) {
                transparency.setText(transparencyLabel(progress));
                if (!fromUser) return;
                hudStyle = new HudStyle(hudStyle.position, hudStyle.color, progress);
                applyHudStyle(true);
            }
            @Override public void onStartTrackingTouch(SeekBar bar) { }
            @Override public void onStopTrackingTouch(SeekBar bar) { }
        });
        options.addView(opacity, new LinearLayout.LayoutParams(-1, dp(48)));
        options.addView(text(tr(R.string.hud_transparency_help), 12, Color.LTGRAY));
        TextView aboutTitle = text(tr(R.string.about_title), 15, Color.WHITE);
        aboutTitle.setPadding(0, dp(18), 0, dp(4));
        options.addView(aboutTitle);
        addAboutLink(options, R.string.about_author, R.string.about_github_url);
        addAboutLink(options, R.string.about_github, R.string.about_github_url);
        addAboutLink(options, R.string.about_blog, R.string.about_blog_url);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(options);
        settingsDialog = new AlertDialog.Builder(this).setTitle(tr(R.string.settings_title)).setView(scroll)
                .setPositiveButton(tr(R.string.action_done), (dialog, which) -> { })
                .setNeutralButton(tr(R.string.action_reconnect), (dialog, which) -> connect())
                .setNegativeButton(tr(R.string.action_exit), (dialog, which) -> { disconnect(); finishAndRemoveTask(); })
                .create();
        settingsDialog.setOnDismissListener(dialog -> {
            settingsDialog = null;
            settingsStatus = null;
            updateStatus = null;
            enterImmersive(getWindow());
        });
        Window dialogWindow = settingsDialog.getWindow();
        if (dialogWindow != null) {
            dialogWindow.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN);
        }
        settingsDialog.show();
        enterImmersive(settingsDialog.getWindow());
    }

    private String transparencyLabel(int value) {
        return tr(R.string.hud_transparency, value, 100 - value);
    }

    private void addAboutLink(LinearLayout parent, int labelResource, int urlResource) {
        TextView link = text(tr(labelResource), 13, ACCENT);
        link.setGravity(Gravity.CENTER_VERTICAL);
        link.setPaintFlags(link.getPaintFlags() | Paint.UNDERLINE_TEXT_FLAG);
        link.setClickable(true);
        link.setFocusable(true);
        link.setContentDescription(tr(R.string.link_open_browser, tr(labelResource)));
        link.setOnClickListener(v -> openTrustedExternalUrl(tr(urlResource)));
        parent.addView(link, new LinearLayout.LayoutParams(-1, dp(44)));
    }

    private void openTrustedExternalUrl(String value) {
        Uri uri = Uri.parse(value);
        String host = uri.getHost();
        boolean trustedHost = "github.com".equalsIgnoreCase(host) || "linjie.space".equalsIgnoreCase(host);
        if (!"https".equalsIgnoreCase(uri.getScheme()) || !trustedHost
                || uri.getUserInfo() != null || uri.getPort() != -1) {
            if (settingsStatus != null) settingsStatus.setText(tr(R.string.link_invalid));
            return;
        }
        Intent browser = new Intent(Intent.ACTION_VIEW, uri);
        browser.addCategory(Intent.CATEGORY_BROWSABLE);
        try {
            startActivity(browser);
        } catch (ActivityNotFoundException noBrowser) {
            if (settingsStatus != null) settingsStatus.setText(tr(R.string.link_no_browser));
        }
    }

    private String tr(int resource, Object... arguments) {
        Context context = localizedContext == null ? this : localizedContext;
        return arguments.length == 0 ? context.getString(resource) : context.getString(resource, arguments);
    }

    private String[] trArray(int resource) {
        Context context = localizedContext == null ? this : localizedContext;
        return context.getResources().getStringArray(resource);
    }

    private void rebuildConnectionPanelForLanguage(Session current) {
        int visibility = connectionPanel == null ? View.VISIBLE : connectionPanel.getVisibility();
        if (connectionPanel != null) content.removeView(connectionPanel);
        createConnectionPanel();
        connectionPanel.setVisibility(visibility);
        LanguageSwitchPolicy.SessionState sessionState = current == null
                ? LanguageSwitchPolicy.SessionState.IDLE
                : LanguageSwitchPolicy.sessionState(current.running, current.connected, current.reconnecting);
        if (sessionState == LanguageSwitchPolicy.SessionState.CONNECTED) {
            String value = current.captureState.paused
                    ? localizeProtocolMessage(current.captureState.message)
                    : tr(R.string.status_connected_waiting_desktop, current.connectionLabel());
            status.setText(value);
            status.setVisibility(visibility);
        } else if (sessionState == LanguageSwitchPolicy.SessionState.CONNECTING
                || sessionState == LanguageSwitchPolicy.SessionState.RECONNECTING) {
            status.setText(tr(sessionState == LanguageSwitchPolicy.SessionState.RECONNECTING
                    ? R.string.status_reconnecting : R.string.status_connecting, current.connectionLabel()));
            status.setVisibility(visibility);
        }
        performance.bringToFront();
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    private void requestPreferredDisplayMode() {
        Display display = ((DisplayManager) getSystemService(DISPLAY_SERVICE)).getDisplay(Display.DEFAULT_DISPLAY);
        if (display == null) return;
        Display.Mode preferred = DisplayCapabilities.preferredMode(display);
        WindowManager.LayoutParams attributes = getWindow().getAttributes();
        attributes.preferredDisplayModeId = preferred.getModeId();
        attributes.preferredRefreshRate = preferred.getRefreshRate();
        getWindow().setAttributes(attributes);
        DisplayCapabilities.recordRequestedMode(preferred.getModeId(), preferred.getRefreshRate());
        if (videoSurface != null) videoSurface.requestRate(preferred.getRefreshRate());
    }

    private void updateStreamingBrightness(Session source) {
        boolean streaming = activityStarted && session == source && source.running && source.connected && source.hasPresentedFrame;
        Display display = ((DisplayManager) getSystemService(DISPLAY_SERVICE)).getDisplay(Display.DEFAULT_DISPLAY);
        if (display == null) { restoreStreamingBrightness(source); return; }
        float requestedRate = DisplayCapabilities.preferredMode(display).getRefreshRate();
        int setting = Settings.System.getInt(getContentResolver(), Settings.System.SCREEN_BRIGHTNESS, -1);
        int maximum = legacyBrightnessMaximum();
        WindowManager.LayoutParams attributes = getWindow().getAttributes();
        if (brightnessOverrideActive) {
            // Once the panel switches, retain the temporary floor until the stream
            // ends; immediately restoring it would oscillate between 60 and 90 Hz.
            if (brightnessOwner != source || !StreamingBrightnessPolicy.keepWhileStreaming(streaming, setting, maximum, requestedRate))
                restoreStreamingBrightness(null);
            else if (Math.abs(attributes.screenBrightness - StreamingBrightnessPolicy.FLOOR) > 0.0001f) {
                // A newer window-level adjustment belongs to its caller; preserve it.
                brightnessOverrideActive = false;
                brightnessOwner = null;
                DisplayCapabilities.recordBrightnessWorkaround(false);
            }
            return;
        }
        if (!StreamingBrightnessPolicy.mayApply(streaming, setting, maximum, display.getRefreshRate(),
                requestedRate, attributes.screenBrightness)) return;
        previousWindowBrightness = attributes.screenBrightness;
        attributes.screenBrightness = StreamingBrightnessPolicy.FLOOR;
        brightnessOwner = source;
        brightnessOverrideActive = true;
        getWindow().setAttributes(attributes);
        DisplayCapabilities.recordBrightnessWorkaround(true);
        source.sendDisplayProfile();
    }

    @SuppressWarnings("DiscouragedApi")
    private int legacyBrightnessMaximum() {
        // This device's resource was independently checked as 255. Unknown OEM
        // scales (such as 4095) intentionally do not activate this workaround.
        int resource = getResources().getIdentifier("config_screenBrightnessSettingMaximum", "integer", "android");
        if (resource == 0) return -1;
        try { return getResources().getInteger(resource); }
        catch (android.content.res.Resources.NotFoundException unknownScale) { return -1; }
    }

    private void restoreStreamingBrightness(Session expectedOwner) {
        if (!brightnessOverrideActive || expectedOwner != null && brightnessOwner != expectedOwner) return;
        WindowManager.LayoutParams attributes = getWindow().getAttributes();
        if (Math.abs(attributes.screenBrightness - StreamingBrightnessPolicy.FLOOR) <= 0.0001f) {
            attributes.screenBrightness = previousWindowBrightness;
            getWindow().setAttributes(attributes);
        }
        brightnessOverrideActive = false;
        brightnessOwner = null;
        DisplayCapabilities.recordBrightnessWorkaround(false);
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        return view;
    }

    private Button button(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(14);
        button.setTextColor(Color.WHITE);
        button.setAllCaps(false);
        button.setMinWidth(dp(72));
        return button;
    }

    private void connect() {
        disconnect();
        requestPreferredDisplayMode();
        if (configurationError != null) {
            status.setText(tr(R.string.pairing_invalid));
            return;
        }
        if (token == null && trustedComputer == null) {
            status.setText(tr(R.string.status_scan_to_connect));
            return;
        }
        if (!activityStarted) return;
        TrustedDeviceIdentity identity = null;
        if (networkPairing != null || trustedComputer != null) {
            try { identity = TrustedDeviceIdentity.loadOrCreate(); }
            catch (Exception unavailable) {
                status.setText(tr(R.string.status_keystore_unavailable));
                return;
            }
        }
        Session next = new Session(token, port, networkPairing, trustedComputer, identity);
        session = next;
        next.reader.start();
        if (updater != null) updater.onSessionChanged();
    }

    private void disconnect() {
        restoreStreamingBrightness(null);
        desktop.releaseTouch();
        Session previous = session;
        session = null;
        if (previous != null) previous.stop();
        videoSurface.hide();
        desktop.setVisibility(View.VISIBLE);
        performance.setText(tr(R.string.status_waiting_connection));
        desktop.clearFrame();
        connectionPanel.setVisibility(View.VISIBLE);
        refreshSavedPairing();
        status.setVisibility(View.VISIBLE);
        if (updater != null) ui.post(updater::onSessionChanged);
    }

    private void showUpdateInstallPrompt(ReleaseManifest.Artifact artifact, boolean activeSession) {
        if (!activityStarted || isFinishing() || updateInstallDialog != null && updateInstallDialog.isShowing()) return;
        if (settingsDialog != null) settingsDialog.dismiss();
        String message = tr(R.string.update_install_prompt, artifact.version,
                activeSession ? tr(R.string.update_active_disconnect_notice) : "");
        updateInstallDialog = new AlertDialog.Builder(this).setTitle(tr(R.string.update_install_prompt_title))
                .setMessage(message)
                .setPositiveButton(tr(activeSession ? R.string.update_disconnect_install : R.string.action_install), (dialog, which) -> {
                    if (activeSession) changeConnection();
                    updater.installPending(true);
                })
                .setNegativeButton(tr(R.string.action_later), null).create();
        updateInstallDialog.setOnDismissListener(dialog -> updateInstallDialog = null);
        updateInstallDialog.show();
    }

    private void showUpdateDownloadPrompt(ReleaseManifest.Artifact artifact) {
        if (!activityStarted || isFinishing() || updateInstallDialog != null && updateInstallDialog.isShowing()) return;
        if (settingsDialog != null) settingsDialog.dismiss();
        updateInstallDialog = new AlertDialog.Builder(this).setTitle(tr(R.string.update_download_prompt_title))
                .setMessage(tr(R.string.update_download_prompt, artifact.version))
                .setPositiveButton(tr(R.string.update_disconnect_download), (dialog, which) -> {
                    changeConnection();
                    updater.continueAfterExplicitDisconnect();
                }).setNegativeButton(tr(R.string.action_later), null).create();
        updateInstallDialog.setOnDismissListener(dialog -> updateInstallDialog = null);
        updateInstallDialog.show();
    }

    private void setStatus(Session source, String message, boolean hasFrame) {
        ui.post(() -> {
            if (session != source || !activityStarted) return;
            status.setText(message);
            if (settingsStatus != null) settingsStatus.setText(message);
            // An already queued status/heartbeat cannot cover a newly presented frame.
            boolean displayed = hasFrame || source.hasPresentedFrame && source.connected;
            connectionPanel.setVisibility(displayed ? View.GONE : View.VISIBLE);
            status.setVisibility(displayed ? View.GONE : View.VISIBLE);
            updatePerformance(source);
            if (!displayed) {
                restoreStreamingBrightness(source);
                desktop.resetTouch();
            }
        });
    }

    private void updatePerformance(Session source) {
        if (session != source || !activityStarted) return;
        CapturePauseState capture = source.captureState;
        if (capture.paused) {
            performance.setText(tr(R.string.hud_capture_paused, localizeProtocolMessage(capture.message)));
        } else if ("H.264".equals(source.streamCodec)
                && (source.hasPresentedFrame || !source.decoderName.isEmpty())) {
            JSONObject profile = DisplayCapabilities.read(this);
            long now = SystemClock.elapsedRealtime();
            String panel = formatHz(profile.optDouble("refreshRate", Double.NaN));
            String requested = formatHz(profile.optDouble("requestedRefreshRate", Double.NaN));
            String submitted = formatFps(source.submittedFps, source.lastSubmittedMillis, now);
            String presented = formatFps(source.actualFps, source.lastPresentedMillis, now);
            String decoder = source.decoderName.isEmpty() ? tr(R.string.hud_selecting_decoder) : source.decoderName;
            DecoderRecoveryState.Snapshot recovery = source.decoderRecovery.snapshot();
            String state = recovery != null
                    ? ("network".equals(recovery.kind) ? tr(R.string.hud_network_recovery) : tr(R.string.hud_decoder_recovery))
                    : source.decoderSelection.isEmpty() ? tr(R.string.hud_waiting_first_frame)
                    : source.decoderSelectionText();
            performance.setText(tr(R.string.hud_h264_metrics, panel, requested, submitted, presented,
                    decoder, state, brightnessOverrideActive ? tr(R.string.hud_high_refresh_brightness) : ""));
        } else if (source.hasPresentedFrame) {
            performance.setText(tr(R.string.hud_presented_fps, source.streamCodec, source.actualFps));
        } else performance.setText(tr(R.string.hud_tablink_status, status.getText()));
    }

    private static String formatHz(double value) {
        return Double.isFinite(value) && value > 0 ? String.format(Locale.ROOT, "%.0f Hz", value) : "—";
    }

    private static String formatFps(double value, long updatedMillis, long nowMillis) {
        return Double.isFinite(value) && value > 0 && updatedMillis > 0
                && nowMillis - updatedMillis <= METRIC_FRESHNESS_MILLIS
                ? String.format(Locale.ROOT, "%.1f fps", value) : "—";
    }

    private static String visibleMessage(String value) {
        String trimmed = value.replaceAll("[\\p{Cntrl}&&[^\\n]]", " ").trim();
        return trimmed.length() <= 180 ? trimmed : trimmed.substring(0, 180);
    }

    private String localizeProtocolMessage(String value) {
        String safe = value == null ? "" : visibleMessage(value);
        if (safe.isEmpty() || "capture-paused".equals(safe)) return tr(R.string.capture_paused_default);
        if (safe.startsWith("h264-decoder-interrupted:"))
            return tr(R.string.decoder_interrupted, safe.substring("h264-decoder-interrupted:".length()));
        if (isChineseUi() != containsHan(safe)) return tr(R.string.status_untranslated_from_computer);
        return safe;
    }

    private String localizeServerError(String value) {
        String safe = value == null ? "" : visibleMessage(value);
        if (safe.isEmpty() || !isChineseUi() && containsHan(safe))
            return tr(R.string.status_computer_reported_untranslated);
        return tr(R.string.status_computer_reported, safe);
    }

    private boolean isChineseUi() {
        Configuration configuration = (localizedContext == null ? this : localizedContext)
                .getResources().getConfiguration();
        Locale locale = Build.VERSION.SDK_INT >= 24 ? configuration.getLocales().get(0) : configuration.locale;
        return locale != null && "zh".equalsIgnoreCase(locale.getLanguage());
    }

    private static boolean containsHan(String value) {
        for (int i = 0; i < value.length();) {
            int codePoint = value.codePointAt(i);
            if (codePoint >= 0x3400 && codePoint <= 0x4dbf || codePoint >= 0x4e00 && codePoint <= 0x9fff
                    || codePoint >= 0xf900 && codePoint <= 0xfaff || codePoint >= 0x20000 && codePoint <= 0x323af)
                return true;
            i += Character.charCount(codePoint);
        }
        return false;
    }

    private final class Session {
        final String sessionToken;
        final int sessionPort;
        final PairingLink paired;
        final TrustedDeviceIdentity identity;
        volatile TrustedComputer trusted;
        volatile String activeHost;
        volatile int activePort;
        final ArrayBlockingQueue<WireProtocol.Packet> outgoing = new ArrayBlockingQueue<>(64);
        final Thread reader;
        volatile boolean running = true;
        volatile boolean connected;
        volatile boolean reconnecting;
        volatile Socket socket;
        volatile Thread writer;
        volatile PresentationProgress presentation;
        volatile SubmissionProgress submission;
        volatile boolean submissionAckNegotiated;
        volatile boolean decoderRefreshNegotiated;
        volatile boolean receiverFeedbackNegotiated;
        volatile boolean adaptiveVideoNegotiated;
        volatile long transportGeneration;
        private final PendingDecoderRefresh pendingDecoderRefresh = new PendingDecoderRefresh();
        private final DecoderRecoveryState decoderRecovery = new DecoderRecoveryState();
        volatile KeyFrameRequestController keyFrameRequests = new KeyFrameRequestController();
        volatile ReceiverFeedbackProgress receiverFeedback = new ReceiverFeedbackProgress();
        private long lastQueueRecoveryVideoGeneration = -1;
        private long lastQueueRecoveryEpoch;
        volatile boolean hasPresentedFrame;
        private String lastDisplayProfile;
        private final AtomicLong frameIds = new AtomicLong();
        private FrameRateMeter jpegRate = new FrameRateMeter();
        private long lastJpegFrameId;
        volatile VideoDecoder video;
        volatile long videoGeneration;
        volatile double actualFps;
        volatile double submittedFps;
        volatile long lastSubmittedMillis;
        volatile long lastPresentedMillis;
        volatile String streamCodec = "JPEG";
        volatile String decoderName = "";
        volatile String decoderSelection = "";
        volatile boolean decoderFallback;
        volatile boolean decoderRecovered;
        volatile long droppedFrames;
        volatile CapturePauseState captureState = new CapturePauseState();

        Session(String token, int port, PairingLink paired, TrustedComputer trusted,
                TrustedDeviceIdentity identity) {
            sessionToken = token;
            sessionPort = port;
            this.paired = paired;
            this.trusted = trusted;
            this.identity = identity;
            activeHost = trusted != null ? trusted.lastHost : paired != null ? paired.host : "127.0.0.1";
            activePort = trusted != null ? trusted.port : port;
            reader = new Thread(this::run, "TabLink-receiver");
        }

        String connectionLabel() {
            if (identity == null) return tr(R.string.connection_usb_debug);
            return tr(trusted != null ? R.string.connection_trusted : R.string.connection_encrypted, activeHost);
        }

        String decoderSelectionText() {
            String tierLabel = "hardware".equals(decoderSelection) ? tr(R.string.decoder_tier_hardware)
                    : "software".equals(decoderSelection) ? tr(R.string.decoder_tier_software)
                    : tr(R.string.decoder_tier_generic);
            String value = decoderFallback && "hardware".equals(decoderSelection)
                    ? tr(R.string.decoder_selected_fallback_hardware)
                    : decoderFallback ? tr(R.string.decoder_selected_fallback, tierLabel)
                    : tr(R.string.decoder_selected_preferred, tierLabel);
            return decoderRecovered ? value + tr(R.string.decoder_recovered_suffix) : value;
        }

        void stop() {
            running = false;
            connected = false;
            reconnecting = false;
            UpdateInstallerUiGate.deactivateDisplay(this);
            closeSocket();
            reader.interrupt();
            Thread activeWriter = writer;
            if (activeWriter != null) activeWriter.interrupt();
            outgoing.clear();
            stopVideo();
        }

        void closeSocket() {
            Socket current = socket;
            if (current != null) {
                try { current.close(); } catch (IOException ignored) { }
            }
        }

        void sendInput(String kind, float x, float y, int delta) {
            if (!running || !connected) return;
            try {
                JSONObject event = new JSONObject();
                event.put("kind", kind);
                event.put("x", (double) x);
                event.put("y", (double) y);
                if ("scroll".equals(kind)) event.put("delta", delta);
                WireProtocol.Packet packet = new WireProtocol.Packet(WireProtocol.INPUT,
                        event.toString().getBytes(StandardCharsets.UTF_8));
                if ("move".equals(kind) && outgoing.remainingCapacity() < 3) return;
                if (!outgoing.offer(packet) && !"move".equals(kind)) {
                    // Reconnect releases host mouse state rather than losing an up event.
                    closeSocket();
                }
            } catch (JSONException ignored) { }
        }

        void framePresented(PresentationProgress progress, long frameId, int width, int height) {
            if (!running || !connected || session != this || presentation != progress) return;
            PresentationProgress.Report report = progress.presented(frameId, width, height, SystemClock.elapsedRealtime());
            if (report == null) return;
            if (!hasPresentedFrame) {
                hasPresentedFrame = true;
                setStatus(this, tr(R.string.status_connected_touch, connectionLabel(), width, height), true);
            }
            final String telemetry = tr(R.string.telemetry_decode_fps, streamCodec, actualFps);
            ui.post(() -> {
                if (session != this || !activityStarted) return;
                updateStreamingBrightness(this);
                updatePerformance(this);
                String detail = captureState.paused ? localizeProtocolMessage(captureState.message)
                        : tr(R.string.status_connected_telemetry, connectionLabel(), width, height, telemetry);
                status.setText(detail);
                if (settingsStatus != null) settingsStatus.setText(detail);
            });
            try {
                JSONObject acknowledgement = new JSONObject();
                acknowledgement.put("kind", "frame-presented");
                acknowledgement.put("sequence", report.sequence);
                acknowledgement.put("width", report.width);
                acknowledgement.put("height", report.height);
                acknowledgement.put("fps", actualFps);
                acknowledgement.put("codec", streamCodec);
                acknowledgement.put("decoder", decoderName);
                acknowledgement.put("droppedFrames", droppedFrames);
                // An acknowledgement may be skipped when input is busy; never block rendering.
                if (outgoing.remainingCapacity() > 1)
                    outgoing.offer(new WireProtocol.Packet(WireProtocol.PRESENTED,
                            acknowledgement.toString().getBytes(StandardCharsets.UTF_8)));
            } catch (JSONException ignored) { }
        }

        synchronized void jpegPresented(PresentationProgress progress, long frameId, int width, int height) {
            if (presentation != progress || frameId <= lastJpegFrameId) return;
            lastJpegFrameId = frameId;
            actualFps = jpegRate.presented(System.nanoTime());
            framePresented(progress, frameId, width, height);
        }

        void frameSubmitted(SubmissionProgress progress, long ptsUs, long submittedNanos,
                int width, int height, String decoder) {
            if (!running || !connected || session != this || submission != progress) return;
            receiverFeedback.submitted();
            lastSubmittedMillis = SystemClock.elapsedRealtime();
            SubmissionProgress.Report report = progress.submitted(ptsUs, width, height, submittedNanos,
                    lastSubmittedMillis, decoder);
            if (report == null) return;
            submittedFps = report.fps;
            ui.post(() -> {
                if (session == this && activityStarted) updatePerformance(this);
            });
            if (!submissionAckNegotiated || captureState.paused || outgoing.remainingCapacity() < 2) return;
            try {
                JSONObject acknowledgement = new JSONObject();
                acknowledgement.put("evidence", "render-submitted");
                acknowledgement.put("frames", report.frames);
                acknowledgement.put("ptsUs", report.ptsUs);
                acknowledgement.put("width", report.width);
                acknowledgement.put("height", report.height);
                acknowledgement.put("fps", report.fps);
                acknowledgement.put("decoder", report.decoder);
                // This cumulative telemetry may be dropped when control input is busy.
                // Never block the MediaCodec callback; a later report supersedes it.
                outgoing.offer(new WireProtocol.Packet(WireProtocol.RENDER_SUBMITTED,
                        acknowledgement.toString().getBytes(StandardCharsets.UTF_8)));
            } catch (JSONException ignored) { }
        }

        void acceptHostFeatures(JSONObject state) {
            if (state.optInt("protocol", -1) != 1) return;
            JSONArray features = state.optJSONArray("features");
            if (features == null || features.length() > 64) return;
            for (int index = 0; index < features.length(); index++) {
                String feature = features.optString(index, null);
                if (WireProtocol.FEATURE_RENDER_SUBMITTED.equals(feature)) submissionAckNegotiated = true;
                else if (WireProtocol.FEATURE_DECODER_REFRESH.equals(feature)) decoderRefreshNegotiated = true;
                else if (WireProtocol.FEATURE_RECEIVER_FEEDBACK.equals(feature)) receiverFeedbackNegotiated = true;
                else if (WireProtocol.FEATURE_ADAPTIVE_VIDEO.equals(feature)) adaptiveVideoNegotiated = true;
            }
        }

        void requestDecoderRefresh(String kind) {
            if (!running || session != this) return;
            long recoveryEpoch = receiverFeedback.recoveryStarted();
            if (!decoderRecovery.start(recoveryEpoch, kind)) return;
            long allowed = keyFrameRequests.request(recoveryEpoch, SystemClock.elapsedRealtime(),
                    maySendKeyFrameRequest());
            if (allowed > 0) issueDecoderRefresh(allowed);
        }

        void retryKeyFrameRequest() {
            long pendingWireGeneration = pendingDecoderRefresh.get();
            if (pendingWireGeneration > 0 && maySendKeyFrameRequest()) {
                enqueueDecoderRefresh(pendingWireGeneration, false, transportGeneration);
                if (pendingDecoderRefresh.get() == 0) return;
            }
            long allowed = keyFrameRequests.retry(SystemClock.elapsedRealtime(), maySendKeyFrameRequest());
            if (allowed > 0) issueDecoderRefresh(allowed);
        }

        boolean maySendKeyFrameRequest() {
            return shouldRetainKeyFrameRequest() && !captureState.paused;
        }

        boolean shouldRetainKeyFrameRequest() {
            return running && connected && session == this && decoderRefreshNegotiated
                    && decoderRecovery.isWaiting();
        }

        void issueDecoderRefresh(long recoveryEpoch) {
            long generation = decoderRecovery.issue(recoveryEpoch);
            if (generation <= 0) return;
            pendingDecoderRefresh.set(generation);
            enqueueDecoderRefresh(generation, true, transportGeneration);
        }

        void enqueueDecoderRefresh(long generation, boolean retry, long connectionGeneration) {
            if (!running || !connected || session != this || !decoderRefreshNegotiated
                    || captureState.paused || connectionGeneration != transportGeneration
                    || generation <= 0 || pendingDecoderRefresh.get() != generation) return;
            // Claim before publishing to outgoing. A writer that immediately
            // defers this packet on a pause must not be cleared afterwards.
            if (!pendingDecoderRefresh.claim(generation)) return;
            WireProtocol.Packet control = new WireProtocol.Packet(WireProtocol.DECODER_REFRESH,
                    DecoderRefreshRequest.encode(generation));
            boolean queued = outgoing.offer(control);
            if (!queued) {
                for (WireProtocol.Packet candidate : outgoing) {
                    if (candidate.type != WireProtocol.RENDER_SUBMITTED && candidate.type != WireProtocol.PRESENTED
                            && candidate.type != WireProtocol.RECEIVER_FEEDBACK)
                        continue;
                    if (outgoing.remove(candidate) && outgoing.offer(control)) { queued = true; break; }
                }
            }
            if (!queued) {
                pendingDecoderRefresh.defer(generation);
            }
            if (!queued && retry) {
                ui.postDelayed(() -> enqueueDecoderRefresh(generation, false, connectionGeneration), 100);
            }
        }

        void deferDecoderRefresh(long generation) {
            pendingDecoderRefresh.defer(generation);
        }

        void sendReceiverFeedback() {
            if (!running || !connected || session != this) return;
            retryKeyFrameRequest();
            if (!receiverFeedbackNegotiated) return;
            VideoDecoder currentVideo = video;
            if (currentVideo == null) return;
            receiverFeedback.observeDecoder(currentVideo.feedbackMetrics());
            ReceiverFeedbackProgress.Report report = receiverFeedback.report(SystemClock.elapsedRealtime(),
                    decoderRecovery.isWaiting());
            if (report == null) return;
            WireProtocol.Packet packet = new WireProtocol.Packet(WireProtocol.RECEIVER_FEEDBACK,
                    report.toJson().getBytes(StandardCharsets.UTF_8));
            for (WireProtocol.Packet candidate : outgoing) {
                if (candidate.type == WireProtocol.RECEIVER_FEEDBACK) outgoing.remove(candidate);
            }
            outgoing.offer(packet);
        }

        synchronized void sendDisplayProfile() {
            if (!running || !connected || session != this) return;
            String profile = DisplayCapabilities.read(MainActivity.this).toString();
            if (profile.equals(lastDisplayProfile)) return;
            if (outgoing.offer(new WireProtocol.Packet(WireProtocol.DISPLAY_PROFILE, profile.getBytes(StandardCharsets.UTF_8))))
                lastDisplayProfile = profile;
        }

        void stopVideo() {
            videoGeneration++;
            VideoDecoder previous = video;
            video = null;
            if (previous != null) receiverFeedback.observeDecoder(previous.feedbackMetrics());
            receiverFeedback.decoderStopped();
            DecoderRecoveryState.Completion cancelled = decoderRecovery.cancel();
            keyFrameRequests.cancelPendingThrough(cancelled.epoch);
            pendingDecoderRefresh.cancelThrough(cancelled.wireGenerationCutoff);
            removeDecoderRefreshThrough(cancelled.wireGenerationCutoff);
            decoderName = "";
            decoderSelection = "";
            decoderFallback = false;
            decoderRecovered = false;
            actualFps = submittedFps = 0;
            lastPresentedMillis = lastSubmittedMillis = 0;
            if (previous != null) previous.close();
            ui.post(() -> {
                if (session == this || session == null) {
                    restoreStreamingBrightness(this);
                    videoSurface.hide();
                    desktop.setVisibility(View.VISIBLE);
                }
            });
        }

        void configureVideo(byte[] bytes, PresentationProgress progress, SubmissionProgress submittedProgress) throws IOException {
            VideoDecoder.Configuration configuration = VideoDecoder.Configuration.parse(bytes);
            VideoDecoder previous = video;
            stopVideo();
            if (previous != null) {
                try {
                    if (!previous.awaitReleased(1500)) throw new IOException("Previous decoder did not stop in time");
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                    throw new IOException("Video reconfiguration interrupted", interrupted);
                }
            }
            long generation = ++videoGeneration;
            streamCodec = "H.264";
            actualFps = 0;
            submittedFps = 0;
            lastPresentedMillis = lastSubmittedMillis = 0;
            decoderName = "";
            decoderSelection = "";
            decoderFallback = false;
            decoderRecovered = false;
            droppedFrames = 0;
            final VideoDecoder[] holder = new VideoDecoder[1];
            VideoDecoder next = new VideoDecoder(configuration, new VideoDecoder.Listener() {
                @Override public void onSubmitted(long ptsUs, long submittedNanos, int width, int height, String decoder) {
                    if (!running || !connected || session != Session.this || generation != videoGeneration
                            || video != holder[0] || presentation != progress || submission != submittedProgress) return;
                    frameSubmitted(submittedProgress, ptsUs, submittedNanos, width, height, decoder);
                }
                @Override public void onPresented(long ptsUs, long renderNanos, int width, int height,
                        double fps, String decoder, long dropped) {
                    if (!running || !connected || session != Session.this || generation != videoGeneration
                            || video != holder[0] || presentation != progress) return;
                    receiverFeedback.presented();
                    ReceiverFeedbackProgress.DecoderMetrics metrics = holder[0].feedbackMetrics();
                    receiverFeedback.observeDecoder(metrics);
                    actualFps = fps;
                    lastPresentedMillis = SystemClock.elapsedRealtime();
                    decoderName = decoder;
                    DecoderRecoveryState.Snapshot recovery = decoderRecovery.snapshot();
                    boolean recoveryFrame = recovery != null && (!"network".equals(recovery.kind)
                            || !metrics.awaitingKeyFrame && metrics.recoveryKeyFramePtsUs >= 0
                            && ptsUs >= metrics.recoveryKeyFramePtsUs);
                    DecoderRecoveryState.Completion completed = recoveryFrame
                            ? decoderRecovery.complete(recovery) : null;
                    boolean recovered = completed != null;
                    if (completed != null) {
                        keyFrameRequests.cancelPendingThrough(completed.epoch);
                        pendingDecoderRefresh.cancelThrough(completed.wireGenerationCutoff);
                        removeDecoderRefreshThrough(completed.wireGenerationCutoff);
                    }
                    if (recovered) decoderRecovered = true;
                    droppedFrames = dropped;
                    framePresented(progress, frameIds.incrementAndGet(), width, height);
                }
                @Override public void onSizeChanged(int width, int height) {
                    ui.post(() -> {
                        if (session == Session.this && generation == videoGeneration) videoSurface.setImageSize(width, height);
                    });
                }
                @Override public void onDecoderSelected(String selectedDecoder, String tier, boolean fallback,
                        String failedDecoder, String reason) {
                    if (!running || !connected || session != Session.this || generation != videoGeneration
                            || video != holder[0] || presentation != progress || submission != submittedProgress) return;
                    decoderName = selectedDecoder;
                    receiverFeedback.decoderSelected(fallback);
                    decoderSelection = tier;
                    decoderFallback = fallback;
                    decoderRecovered = false;
                    if (fallback) {
                        actualFps = submittedFps = 0;
                        lastPresentedMillis = lastSubmittedMillis = 0;
                        requestDecoderRefresh("decoder");
                    }
                    ui.post(() -> {
                        if (session != Session.this || generation != videoGeneration || video != holder[0]
                                || !activityStarted) return;
                        if (fallback) {
                            String message = tr(R.string.decoder_switch_wait_keyframe);
                            status.setText(message);
                            if (settingsStatus != null) settingsStatus.setText(message);
                        }
                        updatePerformance(Session.this);
                    });
                }
                @Override public void onKeyFrameRequired(long queueRecoveryEpoch, String reason) {
                    if (!running || !connected || session != Session.this || generation != videoGeneration
                            || video != holder[0] || presentation != progress || submission != submittedProgress) return;
                    synchronized (Session.this) {
                        if (lastQueueRecoveryVideoGeneration == generation
                                && queueRecoveryEpoch <= lastQueueRecoveryEpoch) return;
                        lastQueueRecoveryVideoGeneration = generation;
                        lastQueueRecoveryEpoch = queueRecoveryEpoch;
                    }
                    requestDecoderRefresh("network");
                    ui.post(() -> {
                        if (session != Session.this || generation != videoGeneration || video != holder[0]
                                || !activityStarted) return;
                        String message = tr(R.string.video_queue_wait_keyframe);
                        status.setText(message);
                        if (settingsStatus != null) settingsStatus.setText(message);
                        updatePerformance(Session.this);
                    });
                }
                @Override public void onError(String message) {
                    if (generation != videoGeneration || video != holder[0]) return;
                    setStatus(Session.this, localizeProtocolMessage(message), false);
                    closeSocket();
                }
            }, renderPacingEnabled);
            holder[0] = next;
            video = next;
            ui.post(() -> {
                if (session != this || video != next || !running) return;
                next.setRenderPacing(renderPacingEnabled);
                desktop.releaseTouch();
                desktop.clearFrame(this);
                desktop.setVisibility(View.GONE);
                videoSurface.show(next, this);
            });
        }

        Socket openAttemptSocket() throws IOException {
            if (identity == null) {
                Socket local = new Socket();
                local.connect(new InetSocketAddress("127.0.0.1", sessionPort), 5000);
                configureSocket(local);
                return local;
            }
            TrustedComputer current = trusted;
            if (current == null) return connectPinned(paired.certificateSha256, paired.host, paired.port);
            IOException firstFailure;
            try { return connectPinned(current.certificateSha256, current.lastHost, current.port); }
            catch (IOException failure) { firstFailure = failure; }
            IOException lastFailure = firstFailure;
            for (TrustedDiscovery.Endpoint discovered : TrustedDiscovery.discover(current.hostId, 1800)) {
                if (!running) throw new IOException("connection-stopped");
                if (discovered.host.equals(current.lastHost) && discovered.port == current.port) continue;
                try {
                    // Discovery is only a route hint. Every candidate still has to
                    // complete TLS against the saved exact certificate; the caller
                    // then performs the signed trusted-device challenge on that socket.
                    return connectPinned(current.certificateSha256, discovered.host, discovered.port,
                            1000, 1500);
                } catch (IOException failure) {
                    lastFailure = failure;
                }
            }
            throw lastFailure;
        }

        Socket connectPinned(String certificate, String host, int targetPort) throws IOException {
            return connectPinned(certificate, host, targetPort, 5000, 8000);
        }

        Socket connectPinned(String certificate, String host, int targetPort,
                int connectTimeoutMillis, int handshakeTimeoutMillis) throws IOException {
            Socket local = PinnedTls.createSocket(certificate);
            try {
                local.connect(new InetSocketAddress(host, targetPort), connectTimeoutMillis);
                local.setTcpNoDelay(true);
                local.setSoTimeout(handshakeTimeoutMillis);
                ((SSLSocket) local).startHandshake();
                configureSocket(local);
                activeHost = host;activePort = targetPort;
                return local;
            } catch (IOException failure) {
                try { local.close(); } catch (IOException ignored) { }
                throw failure;
            }
        }

        void configureSocket(Socket local) throws IOException {
            local.setTcpNoDelay(true);
            local.setSoTimeout(8000);
        }

        JSONArray requestedFeatures() {
            return new JSONArray().put(WireProtocol.FEATURE_RENDER_SUBMITTED)
                    .put(WireProtocol.FEATURE_DECODER_REFRESH)
                    .put(WireProtocol.FEATURE_RECEIVER_FEEDBACK)
                    .put(WireProtocol.FEATURE_ADAPTIVE_VIDEO)
                    .put(WireProtocol.FEATURE_TRUSTED_DEVICE);
        }

        void sendTrustedAuthentication(DataInputStream input, DataOutputStream output) throws IOException, JSONException {
            TrustedComputer computer = trusted;
            if (computer == null || identity == null) throw new IOException("trusted-identity-not-ready");
            JSONObject hello = new JSONObject();
            hello.put("protocol", 1);hello.put("deviceId", identity.deviceId);hello.put("features", requestedFeatures());
            WireProtocol.write(output, WireProtocol.TRUSTED_HELLO, hello.toString().getBytes(StandardCharsets.UTF_8));
            WireProtocol.Packet packet = WireProtocol.read(input);
            if (packet.type != WireProtocol.TRUSTED_CHALLENGE || packet.payload.length > 4096)
                throw new IOException("trusted-challenge-missing");
            JSONObject challenge = new JSONObject(new String(packet.payload, StandardCharsets.UTF_8));
            requireKeys(challenge, "protocol", "feature", "hostId", "deviceId", "challenge");
            if (challenge.getInt("protocol") != 1 || !WireProtocol.FEATURE_TRUSTED_DEVICE.equals(challenge.getString("feature")) ||
                    !computer.hostId.equals(challenge.getString("hostId")) || !identity.deviceId.equals(challenge.getString("deviceId")))
                throw new IOException("trusted-challenge-identity-mismatch");
            byte[] nonce;
            try { nonce = android.util.Base64.decode(challenge.getString("challenge"), android.util.Base64.DEFAULT); }
            catch (IllegalArgumentException invalid) { throw new IOException("trusted-challenge-invalid", invalid); }
            if (nonce.length != TrustedDeviceProtocol.CHALLENGE_BYTES) throw new IOException("trusted-challenge-length-invalid");
            byte[] signature;
            try { signature = identity.sign(computer.hostId, nonce); }
            catch (java.security.GeneralSecurityException failure) { throw new IOException("trusted-challenge-signing-failed", failure); }
            JSONObject proof = new JSONObject();
            proof.put("deviceId", identity.deviceId);
            proof.put("signature", android.util.Base64.encodeToString(signature, android.util.Base64.NO_WRAP));
            WireProtocol.write(output, WireProtocol.TRUSTED_PROOF, proof.toString().getBytes(StandardCharsets.UTF_8));
        }

        void acceptTrustEstablished(byte[] payload) throws IOException, JSONException {
            if (identity == null || paired == null || payload.length > 4096) throw new IOException("unexpected-trust-confirmation");
            JSONObject state = new JSONObject(new String(payload, StandardCharsets.UTF_8));
            requireKeys(state, "protocol", "feature", "hostId", "deviceId");
            String hostId = state.getString("hostId");
            if (state.getInt("protocol") != 1 || !WireProtocol.FEATURE_TRUSTED_DEVICE.equals(state.getString("feature")) ||
                    !identity.deviceId.equals(state.getString("deviceId")) || !paired.certificateSha256.equals(hostId))
                throw new IOException("trust-confirmation-pairing-mismatch");
            TrustedComputer enrolled = new TrustedComputer(hostId, paired.certificateSha256, activeHost, activePort);
            persistTrustedComputerFromSession(this, enrolled);
            trusted = enrolled;
        }

        void persistTrustedEndpoint() {
            TrustedComputer current = trusted;
            if (current == null) return;
            if (current.lastHost.equals(activeHost) && current.port == activePort) return;
            TrustedComputer updated = current.withEndpoint(activeHost, activePort);
            try {
                persistTrustedComputerFromSession(this, updated);
                trusted = updated;
            }
            catch (IOException ignored) { }
        }

        void requireKeys(JSONObject value, String... names) throws IOException {
            java.util.HashSet<String> expected = new java.util.HashSet<>(java.util.Arrays.asList(names));
            java.util.HashSet<String> actual = new java.util.HashSet<>();
            for (java.util.Iterator<String> iterator = value.keys(); iterator.hasNext();) actual.add(iterator.next());
            if (!actual.equals(expected)) throw new IOException("trusted-authentication-fields-invalid");
        }

        void run() {
            int retry = 0;
            while (running) {
                transportGeneration++;
                if (transportGeneration <= 0) transportGeneration = 1;
                reconnecting = transportGeneration > 1;
                PresentationProgress progress = new PresentationProgress();
                SubmissionProgress submittedProgress = new SubmissionProgress();
                presentation = progress;
                submission = submittedProgress;
                submissionAckNegotiated = false;
                decoderRefreshNegotiated = false;
                receiverFeedbackNegotiated = false;
                adaptiveVideoNegotiated = false;
                pendingDecoderRefresh.reset();
                decoderRecovery.reset();
                keyFrameRequests = new KeyFrameRequestController();
                receiverFeedback = new ReceiverFeedbackProgress();
                lastQueueRecoveryVideoGeneration = -1;
                lastQueueRecoveryEpoch = 0;
                hasPresentedFrame = false;
                decoderName = "";
                decoderSelection = "";
                decoderFallback = false;
                decoderRecovered = false;
                actualFps = submittedFps = 0;
                lastPresentedMillis = lastSubmittedMillis = 0;
                captureState = new CapturePauseState();
                lastDisplayProfile = null;
                frameIds.set(0);
                lastJpegFrameId = 0;
                jpegRate = new FrameRateMeter();
                boolean serverRejected = false;
                try {
                    setStatus(this, tr(reconnecting ? R.string.status_reconnecting : R.string.status_connecting,
                            connectionLabel()), false);
                    boolean trustedAttempt = trusted != null;
                    Socket local = openAttemptSocket();
                    socket = local;
                    if (!running) { local.close(); break; }
                    local.setSoTimeout(15000);
                    DataOutputStream output = new DataOutputStream(new BufferedOutputStream(local.getOutputStream()));
                    DataInputStream input = new DataInputStream(new BufferedInputStream(local.getInputStream()));
                    if (trustedAttempt) sendTrustedAuthentication(input, output);
                    else {
                        JSONObject hello = new JSONObject();
                        hello.put("protocol", 1);
                        hello.put("token", sessionToken);
                        hello.put("features", identity == null ? new JSONArray()
                                .put(WireProtocol.FEATURE_RENDER_SUBMITTED)
                                .put(WireProtocol.FEATURE_DECODER_REFRESH)
                                .put(WireProtocol.FEATURE_RECEIVER_FEEDBACK)
                                .put(WireProtocol.FEATURE_ADAPTIVE_VIDEO) : requestedFeatures());
                        if (identity != null) {
                            hello.put("deviceId", identity.deviceId);
                            hello.put("devicePublicKey", identity.publicKeySpkiBase64());
                            String deviceName = (Build.MANUFACTURER + " " + Build.MODEL)
                                    .replaceAll("[\\p{Cntrl}]", " ").trim();
                            if (deviceName.length() > 64) deviceName = deviceName.substring(0, 64);
                            hello.put("deviceName", deviceName.isEmpty() ? tr(R.string.android_device_name) : deviceName);
                        }
                        WireProtocol.write(output, WireProtocol.HELLO,
                                hello.toString().getBytes(StandardCharsets.UTF_8));
                    }
                    // Send the initial native/rotation/requested-Hz profile synchronously,
                    // before any decoder is created or incoming video packet is consumed.
                    lastDisplayProfile = DisplayCapabilities.read(MainActivity.this).toString();
                    WireProtocol.write(output, WireProtocol.DISPLAY_PROFILE, lastDisplayProfile.getBytes(StandardCharsets.UTF_8));
                    outgoing.clear();
                    if (!UpdateInstallerUiGate.tryActivateDisplay(this))
                        throw new IOException("installer-ui-busy");
                    connected = true;
                    reconnecting = false;
                    if (updater != null) updater.onSessionChanged();
                    writer = new Thread(() -> writeLoop(local, output), "TabLink-input");
                    writer.start();
                    setStatus(this, tr(R.string.status_connected_waiting_desktop, connectionLabel()), false);
                    while (running) {
                        WireProtocol.Packet packet = WireProtocol.read(input);
                        if (packet.type == WireProtocol.FRAME) {
                            if (video != null) stopVideo();
                            streamCodec = "JPEG";
                            decoderName = "BitmapFactory";
                            Bitmap bitmap = decodeFrame(packet.payload);
                            desktop.showFrame(bitmap, this, progress, frameIds.incrementAndGet());
                            retry = 0;
                        } else if (packet.type == WireProtocol.VIDEO_CONFIG) {
                            configureVideo(packet.payload, progress, submittedProgress);
                        } else if (packet.type == WireProtocol.VIDEO_FRAME) {
                            VideoDecoder currentVideo = video;
                            if (currentVideo == null) throw new IOException("H.264 configuration must precede video frames");
                            receiverFeedback.receivedVideo(packet.payload.length);
                            currentVideo.offer(packet.payload);
                            receiverFeedback.observeDecoder(currentVideo.feedbackMetrics());
                            retry = 0;
                        } else if (packet.type == WireProtocol.TRUST_ESTABLISHED) {
                            acceptTrustEstablished(packet.payload);
                        } else if (packet.type == WireProtocol.STATUS) {
                            if (packet.payload.length > 16384) throw new IOException("status-packet-too-long");
                            JSONObject state = new JSONObject(new String(packet.payload, StandardCharsets.UTF_8));
                            acceptHostFeatures(state);
                            if (trustedAttempt) persistTrustedEndpoint();
                            String message = state.optString("message", tr(R.string.status_connected, connectionLabel()));
                            Object paused = state.opt("capturePaused");
                            captureState = captureState.update(paused instanceof Boolean ? (Boolean) paused : null,
                                    localizeProtocolMessage(message));
                            retryKeyFrameRequest();
                            // Keep the same decoder, Surface, last frame and presentation sequence.
                            // Only actual JPEG/codec rendering may advance an acknowledgement.
                            setStatus(this, localizeProtocolMessage(message), hasPresentedFrame);
                        } else if (packet.type == WireProtocol.ERROR) {
                            serverRejected = true;
                            String message = new String(packet.payload, 0, Math.min(packet.payload.length, 4096), StandardCharsets.UTF_8);
                            throw new IOException(localizeProtocolMessage(message));
                        } else {
                            throw new IOException("protocol-incompatible");
                        }
                    }
                } catch (IOException | JSONException | IllegalArgumentException problem) {
                    if (running) {
                        reconnecting = true;
                        boolean certificateFailure = identity != null && problem instanceof SSLHandshakeException;
                        if (certificateFailure && trusted == null) serverRejected = true;
                        String message = certificateFailure && trusted != null
                                ? tr(R.string.status_saved_certificate_failed)
                                : certificateFailure ? tr(R.string.status_certificate_failed)
                                : serverRejected ? localizeServerError(problem.getMessage())
                                : "installer-ui-busy".equals(problem.getMessage()) ? tr(R.string.installer_busy_reconnect)
                                : "protocol-incompatible".equals(problem.getMessage()) ? tr(R.string.status_protocol_incompatible)
                                : identity == null ? tr(R.string.status_adb_disconnected)
                                : trusted != null ? tr(R.string.status_trusted_retry)
                                : tr(R.string.status_network_retry);
                        hasPresentedFrame = false;
                        setStatus(this, message, false);
                    }
                } finally {
                    connected = false;
                    UpdateInstallerUiGate.deactivateDisplay(this);
                    if (updater != null) updater.onSessionChanged();
                    submissionAckNegotiated = false;
                    decoderRefreshNegotiated = false;
                    receiverFeedbackNegotiated = false;
                    adaptiveVideoNegotiated = false;
                    submission = null;
                    stopVideo();
                    closeSocket();
                    Thread currentWriter = writer;
                    if (currentWriter != null) {
                        currentWriter.interrupt();
                        try { currentWriter.join(1500); } catch (InterruptedException ignored) { }
                    }
                    outgoing.clear();
                    pendingDecoderRefresh.reset();
                    desktop.clearFrame(this);
                }
                if (serverRejected) { running = false; break; }
                if (!running) break;
                long delay = Math.min(10000, 1000L << Math.min(retry++, 4));
                try { Thread.sleep(delay); } catch (InterruptedException interrupted) { break; }
            }
            reconnecting = false;
        }

        void writeLoop(Socket local, DataOutputStream output) {
            try {
                while (running && !local.isClosed()) {
                    WireProtocol.Packet packet = outgoing.take();
                    if (packet.type == WireProtocol.DECODER_REFRESH) {
                        long generation = DecoderRefreshRequest.decode(packet.payload);
                        if (pendingDecoderRefresh.isCancelled(generation)) continue;
                        if (!maySendKeyFrameRequest()) {
                            if (!pendingDecoderRefresh.isCancelled(generation)
                                    && shouldRetainKeyFrameRequest()) deferDecoderRefresh(generation);
                            continue;
                        }
                        if (pendingDecoderRefresh.isCancelled(generation)) continue;
                    }
                    if (packet.type == WireProtocol.RECEIVER_FEEDBACK && !receiverFeedbackNegotiated) continue;
                    WireProtocol.write(output, packet.type, packet.payload);
                }
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            } catch (IOException ignored) {
                try { local.close(); } catch (IOException alsoIgnored) { }
            }
        }

        void removeDecoderRefreshThrough(long cutoff) {
            if (cutoff <= 0) return;
            for (WireProtocol.Packet candidate : outgoing) {
                if (candidate.type != WireProtocol.DECODER_REFRESH) continue;
                try {
                    if (DecoderRefreshRequest.decode(candidate.payload) <= cutoff) outgoing.remove(candidate);
                } catch (IOException invalidInternalPacket) {
                    outgoing.remove(candidate);
                }
            }
        }
    }

    private static Bitmap decodeFrame(byte[] bytes) throws IOException {
        if (bytes.length < 3 || bytes[0] != (byte) 0xff || bytes[1] != (byte) 0xd8) {
            throw new IOException("frame-not-jpeg");
        }
        BitmapFactory.Options size = new BitmapFactory.Options();
        size.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(bytes, 0, bytes.length, size);
        if (size.outWidth <= 0 || size.outHeight <= 0 || size.outWidth > 8192 || size.outHeight > 8192
                || (long) size.outWidth * size.outHeight > 16000000L) {
            throw new IOException("frame-dimensions-invalid");
        }
        BitmapFactory.Options decode = new BitmapFactory.Options();
        decode.inPreferredConfig = Bitmap.Config.RGB_565;
        Bitmap bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, decode);
        if (bitmap == null) throw new IOException("frame-decode-failed");
        return bitmap;
    }

    // A dedicated codec surface avoids connecting MediaCodec to the Canvas
    // producer used by JPEG. Both paths share the same aspect-fit touch mapping.
    private final class VideoSurface extends SurfaceView implements SurfaceHolder.Callback {
        private VideoDecoder active;
        private Session owner;
        private int imageWidth, imageHeight;
        private float requestedRate;

        VideoSurface() {
            super(MainActivity.this);
            getHolder().addCallback(this);
            setFocusable(true);
            setContentDescription(tr(R.string.desktop_h264_description));
        }

        void show(VideoDecoder decoder, Session source) {
            active = decoder;
            owner = source;
            setImageSize(decoder.configuration.width, decoder.configuration.height);
            setVisibility(View.VISIBLE);
            if (getHolder().getSurface().isValid()) {
                requestRate(requestedRate);
                decoder.start(getHolder().getSurface());
            }
        }

        void hide() {
            active = null;
            owner = null;
            setVisibility(View.GONE);
        }

        void setImageSize(int width, int height) {
            imageWidth = width; imageHeight = height;
            updateLayout();
        }

        void updateLayout() {
            if (content == null || imageWidth <= 0 || imageHeight <= 0 || content.getWidth() <= 0 || content.getHeight() <= 0) return;
            FrameGeometry fit = FrameGeometry.fit(imageWidth, imageHeight, content.getWidth(), content.getHeight());
            int width = Math.max(1, Math.round(fit.width)), height = Math.max(1, Math.round(fit.height));
            FrameLayout.LayoutParams current = (FrameLayout.LayoutParams) getLayoutParams();
            if (current.width != width || current.height != height) {
                current.width = width; current.height = height; current.gravity = Gravity.CENTER;
                setLayoutParams(current);
            }
            updateTouchMapping();
        }

        void updateTouchMapping() {
            if (desktop == null || active == null) return;
            synchronized (desktop.frameLock) {
                desktop.geometry = FrameGeometry.fit(imageWidth, imageHeight, getWidth(), getHeight());
            }
        }

        void requestRate(float rate) {
            requestedRate = rate;
            if (Build.VERSION.SDK_INT >= 30 && getHolder().getSurface().isValid()) {
                try {
                    if (Build.VERSION.SDK_INT >= 31)
                        getHolder().getSurface().setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE,
                                Surface.CHANGE_FRAME_RATE_ALWAYS);
                    else getHolder().getSurface().setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
                }
                catch (IllegalArgumentException | IllegalStateException ignored) { }
            }
        }

        @Override public void surfaceCreated(SurfaceHolder holder) {
            requestRate(requestedRate);
            if (active != null) active.start(holder.getSurface());
        }
        @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
            desktop.releaseTouch();
            updateTouchMapping();
        }
        @Override public void surfaceDestroyed(SurfaceHolder holder) {
            desktop.releaseTouch();
            VideoDecoder previous = active;
            if (previous != null) {
                previous.close();
                if (owner != null && owner.video == previous) owner.closeSocket();
            }
        }
        @Override public boolean onTouchEvent(MotionEvent event) { return desktop.onTouchEvent(event); }
        @Override public boolean onGenericMotionEvent(MotionEvent event) { return desktop.onGenericMotionEvent(event); }
        @Override public boolean performClick() { super.performClick(); return true; }
    }

    private final class DesktopSurface extends SurfaceView implements SurfaceHolder.Callback {
        private final Object frameLock = new Object();
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG | Paint.FILTER_BITMAP_FLAG);
        private Bitmap frame;
        private Session frameOwner;
        private PresentationProgress frameProgress;
        private long frameId;
        private FrameGeometry geometry = FrameGeometry.fit(0, 0, 0, 0);
        private boolean surfaceReady;
        private int pointerId = -1;
        private float lastX;
        private float lastY;
        private long lastMove;

        DesktopSurface() {
            super(MainActivity.this);
            getHolder().addCallback(this);
            setFocusable(true);
            setContentDescription(tr(R.string.desktop_touch_description));
        }

        void showFrame(Bitmap bitmap, Session source, PresentationProgress progress, long id) {
            synchronized (frameLock) {
                if (session != source || !source.running || source.presentation != progress) { bitmap.recycle(); return; }
                Bitmap previous = frame;
                frame = bitmap;
                frameOwner = source;
                frameProgress = progress;
                frameId = id;
                try { drawFrame(); }
                finally { if (previous != null) previous.recycle(); }
            }
        }

        void clearFrame() {
            clearFrame(null);
        }

        void clearFrame(Session expectedOwner) {
            synchronized (frameLock) {
                // A replaced connection must never clear its successor's screen.
                if (expectedOwner != null && session != expectedOwner) return;
                if (frame != null) frame.recycle();
                frame = null;
                frameOwner = null;
                frameProgress = null;
                geometry = FrameGeometry.fit(0, 0, 0, 0);
                drawFrame();
            }
        }

        private void drawFrame() {
            if (!surfaceReady || !getHolder().getSurface().isValid()) return;
            Canvas canvas = null;
            boolean drawn = false;
            boolean posted = false;
            try {
                canvas = getHolder().lockCanvas();
                if (canvas == null) return;
                canvas.drawColor(Color.BLACK);
                if (frame != null && !frame.isRecycled()) {
                    geometry = FrameGeometry.fit(frame.getWidth(), frame.getHeight(), canvas.getWidth(), canvas.getHeight());
                    canvas.drawBitmap(frame, null, new RectF(geometry.left, geometry.top,
                            geometry.left + geometry.width, geometry.top + geometry.height), paint);
                    drawn = true;
                }
            } catch (IllegalArgumentException | IllegalStateException surfaceUnavailable) {
                // The next surface callback redraws the retained frame after rotation/resizing.
            } finally {
                if (canvas != null) {
                    try {
                        getHolder().unlockCanvasAndPost(canvas);
                        posted = true;
                    } catch (IllegalArgumentException | IllegalStateException surfaceUnavailable) {
                        // A destroyed Surface is not evidence that a frame was presented.
                    }
                }
            }
            if (drawn && posted && frameOwner != null && frameProgress != null) {
                frameOwner.jpegPresented(frameProgress, frameId, frame.getWidth(), frame.getHeight());
            }
        }

        @Override public void surfaceCreated(SurfaceHolder holder) {
            synchronized (frameLock) { surfaceReady = true; drawFrame(); }
        }

        @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
            releaseTouch();
            synchronized (frameLock) { drawFrame(); }
        }

        @Override public void surfaceDestroyed(SurfaceHolder holder) {
            releaseTouch();
            synchronized (frameLock) { surfaceReady = false; }
        }

        void resetTouch() { pointerId = -1; }

        void releaseTouch() {
            if (pointerId != -1) send("up", lastX, lastY, 0);
            pointerId = -1;
        }

        private void send(String kind, float x, float y, int delta) {
            Session current = session;
            if (current != null) current.sendInput(kind, x, y, delta);
        }

        @Override public boolean onTouchEvent(MotionEvent event) {
            FrameGeometry mapping;
            synchronized (frameLock) { mapping = geometry; }
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN) {
                releaseTouch();
                if (!mapping.contains(event.getX(), event.getY())) return false;
                Session active = session;
                if (active == null || !active.connected) return false;
                pointerId = event.getPointerId(0);
                lastX = mapping.normalizedX(event.getX());
                lastY = mapping.normalizedY(event.getY());
                lastMove = SystemClock.uptimeMillis();
                send("down", lastX, lastY, 0);
                return true;
            }
            if (pointerId == -1) return false;
            int index = event.findPointerIndex(pointerId);
            if (action == MotionEvent.ACTION_CANCEL || index < 0) {
                releaseTouch();
                return true;
            }
            if (action == MotionEvent.ACTION_MOVE) {
                // Finish the drag at its last valid position when it leaves the image.
                if (!mapping.contains(event.getX(index), event.getY(index))) {
                    releaseTouch();
                    return true;
                }
                lastX = mapping.normalizedX(event.getX(index));
                lastY = mapping.normalizedY(event.getY(index));
                long now = SystemClock.uptimeMillis();
                if (now - lastMove >= 16) {
                    send("move", lastX, lastY, 0);
                    lastMove = now;
                }
            } else if (action == MotionEvent.ACTION_UP
                    || action == MotionEvent.ACTION_POINTER_UP && event.getPointerId(event.getActionIndex()) == pointerId) {
                if (mapping.contains(event.getX(index), event.getY(index))) {
                    lastX = mapping.normalizedX(event.getX(index));
                    lastY = mapping.normalizedY(event.getY(index));
                }
                releaseTouch();
                performClick();
            }
            return true;
        }

        @Override public boolean performClick() { super.performClick(); return true; }

        @Override public boolean onGenericMotionEvent(MotionEvent event) {
            if (event.getActionMasked() == MotionEvent.ACTION_SCROLL) {
                FrameGeometry mapping;
                synchronized (frameLock) { mapping = geometry; }
                if (!mapping.contains(event.getX(), event.getY())) return false;
                int delta = Math.round(event.getAxisValue(MotionEvent.AXIS_VSCROLL) * 120);
                if (delta != 0) send("scroll", mapping.normalizedX(event.getX()), mapping.normalizedY(event.getY()), delta);
                return true;
            }
            return super.onGenericMotionEvent(event);
        }
    }
}
