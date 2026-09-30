package com.tablink.client;

import android.app.Activity;
import android.app.AlertDialog;
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
import android.widget.Switch;
import android.widget.TextView;

import org.json.JSONException;
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
    private static final String ACTION_SET_RENDER_PACING = "com.tablink.client.SET_RENDER_PACING";
    private static final int SCAN_REQUEST = 71;
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
    private TextView updateStatus;
    private AndroidUpdateController updater;
    private SharedPreferences preferences;
    private HudStyle hudStyle;
    private String token;
    private PairingLink networkPairing;
    private int port = 27183;
    private String configurationError;
    private volatile boolean renderPacingEnabled = true;
    private boolean activityStarted;
    private boolean brightnessOverrideActive;
    private float previousWindowBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE;
    private Session brightnessOwner;
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

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        preferences = getSharedPreferences("display", MODE_PRIVATE);
        hudStyle = new HudStyle(preferences.getInt("hudPosition", 0),
                preferences.getInt("hudColor", HudStyle.DEFAULT_COLOR),
                preferences.getInt("hudTransparency", HudStyle.DEFAULT_TRANSPARENCY));
        createUi();
        updater = new AndroidUpdateController(this, new AndroidUpdateController.Host() {
            @Override public boolean isForeground() { return activityStarted; }
            @Override public boolean hasActiveDisplaySession() { return session != null; }
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
        readConfiguration(getIntent());
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
        connect();
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
        setIntent(intent);
        readConfiguration(intent);
        if (activityStarted) connect();
    }

    @Override protected void onStop() {
        activityStarted = false;
        updater.onBackground();
        ((DisplayManager) getSystemService(DISPLAY_SERVICE)).unregisterDisplayListener(displayListener);
        DisplayCapabilities.recordRequestedMode(0, 0);
        disconnect();
        super.onStop();
    }

    @Override protected void onDestroy() {
        if (settingsDialog != null) settingsDialog.dismiss();
        if (updateInstallDialog != null) updateInstallDialog.dismiss();
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

    private void readConfiguration(Intent intent) {
        configurationError = null;
        networkPairing = null;
        renderPacingEnabled = intent.getBooleanExtra("renderPacing", true);
        String pairing = intent.getDataString();
        if (pairing != null) {
            token = null;
            try {
                networkPairing = PairingLink.parse(pairing);
                token = networkPairing.token();
                port = networkPairing.port;
            } catch (IllegalArgumentException invalid) {
                configurationError = invalid.getMessage();
            }
            return;
        }
        token = intent.getBooleanExtra("profileOnly", false) ? null : intent.getStringExtra("token");
        port = intent.getIntExtra("port", 27183);
        String host = intent.getStringExtra("host");
        if (host != null && !"127.0.0.1".equals(host)) {
            configurationError = "ADB 通道仅允许 127.0.0.1；网络连接请扫描电脑端二维码";
        } else if (port < 1024 || port > 65535) {
            configurationError = "电脑端提供的端口无效，请重新连接";
        } else if (token != null && (token.isEmpty() || token.length() > 512)) {
            configurationError = "电脑端提供的连接凭证无效，请重新连接";
        }
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
        performance = text("TabLink · 等待连接", 12, Color.WHITE);
        performance.setBackgroundColor(Color.TRANSPARENT);
        performance.setShadowLayer(dp(1), 0, 0, Color.BLACK);
        performance.setPadding(dp(8), dp(8), dp(8), dp(8));
        performance.setMinHeight(dp(44));
        performance.setGravity(Gravity.CENTER_VERTICAL);
        performance.setContentDescription("统计信息；长按打开显示设置");
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
        TextView title = text("TabLink · 平板副屏", 28, Color.WHITE);
        title.setTypeface(null, android.graphics.Typeface.BOLD);
        panel.addView(title);
        help = text("无线或 USB 数据线，把平板变成电脑的第二块屏幕。", 16, Color.rgb(190, 204, 219));
        help.setPadding(0, dp(12), 0, dp(22));
        panel.addView(help);
        Button scan = button("扫描电脑二维码");
        scan.setBackgroundTintList(android.content.res.ColorStateList.valueOf(ACCENT));
        scan.setTextColor(BACKGROUND);
        scan.setOnClickListener(v -> startScanner());
        panel.addView(scan, new LinearLayout.LayoutParams(-1, dp(56)));
        Button paste = button("粘贴连接链接");
        paste.setOnClickListener(v -> showPasteDialog());
        panel.addView(paste, new LinearLayout.LayoutParams(-1, dp(56)));
        reconnectPairing = button("重连上次配对的电脑");
        reconnectPairing.setOnClickListener(v -> {
            String saved = getSharedPreferences("pairing", MODE_PRIVATE).getString("lastLink", null);
            if (saved != null) acceptPairing(saved);
        });
        panel.addView(reconnectPairing, new LinearLayout.LayoutParams(-1, dp(56)));
        refreshSavedPairing();
        TextView instructions = text("Wi-Fi：平板和电脑连接同一局域网，再扫描电脑端显示的二维码。\n\n"
                + "USB：连接数据线，在系统设置中开启“USB 网络共享”，然后扫描电脑端的 USB 网络二维码。无需 USB 调试。", 14, Color.LTGRAY);
        instructions.setPadding(0, dp(22), 0, dp(10));
        panel.addView(instructions);
        Button tether = button("打开 USB 网络共享设置");
        tether.setOnClickListener(v -> openTetherSettings());
        panel.addView(tether, new LinearLayout.LayoutParams(-1, dp(52)));
        TextView legacy = text("已配置 ADB 的设备仍可由电脑端启动。播放时长按统计文字，或使用返回手势打开设置。", 12, Color.GRAY);
        legacy.setPadding(0, dp(14), 0, dp(12));
        panel.addView(legacy);
        status = text("请选择连接方式", 14, ACCENT);
        status.setPadding(0, dp(8), 0, dp(8));
        panel.addView(status);
        scroll.addView(panel, new ScrollView.LayoutParams(-1, -1));
        connectionPanel = scroll;
        content.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
    }

    private void refreshSavedPairing() {
        String saved = getSharedPreferences("pairing", MODE_PRIVATE).getString("lastLink", null);
        try {
            PairingLink previous = PairingLink.parse(saved);
            reconnectPairing.setText("重连上次电脑 · " + previous.host);
            reconnectPairing.setVisibility(View.VISIBLE);
        } catch (IllegalArgumentException missing) {
            reconnectPairing.setVisibility(View.GONE);
        }
    }

    private void changeConnection() {
        disconnect();
        token = null;
        networkPairing = null;
        configurationError = null;
        setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_MAIN));
        if (settingsDialog != null) settingsDialog.dismiss();
        refreshSavedPairing();
        status.setText("请选择连接方式；电脑重新开始会话后，请扫描新的二维码");
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
            networkPairing = paired;
            token = paired.token();
            port = paired.port;
            configurationError = null;
            setIntent(new Intent(this, MainActivity.class).setAction(Intent.ACTION_VIEW)
                    .setData(android.net.Uri.parse(paired.toPrivateUri())));
            if (activityStarted) connect();
            return true;
        } catch (IllegalArgumentException invalid) {
            status.setText(invalid.getMessage());
            return false;
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
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("粘贴电脑端连接链接")
                .setMessage("在电脑端复制连接链接后粘贴到这里。链接只用于当前电脑会话。")
                .setView(input).setPositiveButton("连接", null).setNegativeButton("取消", null).create();
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
                status.setText("请手动打开系统设置 → 热点与网络共享 → USB 网络共享");
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
        TextView instruction = text("长按统计文字或返回手势打开此面板。设置即时保存，关闭后继续全屏观看。", 13, Color.LTGRAY);
        instruction.setPadding(0, dp(10), 0, dp(14));
        options.addView(instruction);
        Button change = button("更换连接 / 扫描二维码");
        change.setOnClickListener(v -> changeConnection());
        options.addView(change, new LinearLayout.LayoutParams(-1, dp(48)));
        Button forget = button("忘记上次配对的电脑");
        forget.setOnClickListener(v -> {
            getSharedPreferences("pairing", MODE_PRIVATE).edit().remove("lastLink").apply();
            refreshSavedPairing();
            forget.setText("已清除保存的配对");
        });
        options.addView(forget, new LinearLayout.LayoutParams(-1, dp(48)));
        options.addView(text("正式版更新", 15, Color.WHITE));
        @SuppressWarnings("deprecation") Switch automaticUpdates = new Switch(this);
        automaticUpdates.setText("自动下载正式版更新");
        automaticUpdates.setTextColor(Color.WHITE);
        automaticUpdates.setChecked(updater.autoDownloadEnabled());
        automaticUpdates.setOnCheckedChangeListener((button, checked) -> updater.setAutoDownloadEnabled(checked));
        options.addView(automaticUpdates, new LinearLayout.LayoutParams(-1, dp(48)));
        updateStatus = text(updater.status(), 13, ACCENT);
        updateStatus.setPadding(0, dp(4), 0, dp(6));
        options.addView(updateStatus);
        Button checkUpdate = button("检查 / 继续正式版更新");
        checkUpdate.setOnClickListener(v -> updater.requestDownloadOrInstall());
        options.addView(checkUpdate, new LinearLayout.LayoutParams(-1, dp(48)));
        TextView updateExplanation = text("更新包会在后台下载并验证签名、大小和 SHA-256。副屏使用中不会自动更新；安装仍需在 Android 系统界面确认。", 12, Color.LTGRAY);
        updateExplanation.setPadding(0, 0, 0, dp(12));
        options.addView(updateExplanation);
        options.addView(text("统计位置", 15, Color.WHITE));
        Spinner position = new Spinner(this);
        String[] positions = { "左上", "上中", "右上", "左中", "正中", "右中", "左下", "下中", "右下" };
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
        options.addView(text("文字颜色（#RRGGBB）", 15, Color.WHITE));
        EditText color = new EditText(this);
        color.setSingleLine(true);
        color.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        color.setText(String.format(Locale.ROOT, "#%06X", hudStyle.color & 0xffffff));
        color.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                Integer parsed = HudStyle.parseColor(s.toString());
                color.setError(parsed == null ? "请输入 # 和六位颜色，例如 #FFFFFF" : null);
                if (parsed != null) {
                    hudStyle = new HudStyle(hudStyle.position, parsed, hudStyle.transparency);
                    applyHudStyle(true);
                }
            }
            @Override public void afterTextChanged(Editable value) { }
        });
        options.addView(color, new LinearLayout.LayoutParams(-1, -2));
        LinearLayout palette = new LinearLayout(this);
        String[] colorNames = { "白", "绿", "青", "黄", "黑" };
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
        options.addView(text("0% 完全不透明；100% 隐藏文字。隐藏后仍可用返回手势进入设置。", 12, Color.LTGRAY));
        ScrollView scroll = new ScrollView(this);
        scroll.addView(options);
        settingsDialog = new AlertDialog.Builder(this).setTitle("TabLink · 设置").setView(scroll)
                .setPositiveButton("完成", (dialog, which) -> { })
                .setNeutralButton("重连", (dialog, which) -> connect())
                .setNegativeButton("退出", (dialog, which) -> { disconnect(); finishAndRemoveTask(); })
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

    private static String transparencyLabel(int value) {
        return "文字透明度 " + value + "%（不透明度 " + (100 - value) + "%）";
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
            status.setText(configurationError);
            return;
        }
        if (token == null) {
            status.setText("扫描电脑端二维码连接；Wi-Fi 或 USB 网络共享均无需 USB 调试");
            return;
        }
        if (!activityStarted) return;
        Session next = new Session(token, port, networkPairing);
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
        performance.setText("TabLink · 等待连接");
        desktop.clearFrame();
        connectionPanel.setVisibility(View.VISIBLE);
        refreshSavedPairing();
        status.setVisibility(View.VISIBLE);
        if (updater != null) ui.post(updater::onSessionChanged);
    }

    private void showUpdateInstallPrompt(ReleaseManifest.Artifact artifact, boolean activeSession) {
        if (!activityStarted || isFinishing() || updateInstallDialog != null && updateInstallDialog.isShowing()) return;
        if (settingsDialog != null) settingsDialog.dismiss();
        String message = "正式版 " + artifact.version + " 已下载并完成安全校验。"
                + (activeSession ? "\n\n当前副屏正在使用；继续会先断开连接。" : "")
                + "\n\nAndroid 会打开系统安装界面，需要你确认安装。";
        updateInstallDialog = new AlertDialog.Builder(this).setTitle("安装 TabLink 正式版")
                .setMessage(message)
                .setPositiveButton(activeSession ? "断开并安装" : "安装", (dialog, which) -> {
                    if (activeSession) changeConnection();
                    updater.installPending(true);
                })
                .setNegativeButton("稍后", null).create();
        updateInstallDialog.setOnDismissListener(dialog -> updateInstallDialog = null);
        updateInstallDialog.show();
    }

    private void showUpdateDownloadPrompt(ReleaseManifest.Artifact artifact) {
        if (!activityStarted || isFinishing() || updateInstallDialog != null && updateInstallDialog.isShowing()) return;
        if (settingsDialog != null) settingsDialog.dismiss();
        updateInstallDialog = new AlertDialog.Builder(this).setTitle("下载 TabLink 正式版")
                .setMessage("发现正式版 " + artifact.version + "。当前副屏正在使用；继续会先断开连接，然后下载并校验更新包。")
                .setPositiveButton("断开并下载", (dialog, which) -> {
                    changeConnection();
                    updater.continueAfterExplicitDisconnect();
                }).setNegativeButton("稍后", null).create();
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
            performance.setText("采集暂停 · " + capture.message + "\n长按或返回打开设置");
        } else if (source.hasPresentedFrame) {
            JSONObject profile = DisplayCapabilities.read(this);
            performance.setText(String.format(Locale.ROOT, "%s · 解码 %.1f fps · 屏幕 %.0f Hz / 请求 %.0f Hz",
                    source.streamCodec, source.actualFps, profile.optDouble("refreshRate"), profile.optDouble("requestedRefreshRate"))
                    + (brightnessOverrideActive ? " · 高刷最低亮度" : ""));
        } else performance.setText("TabLink · " + status.getText());
    }

    private static String visibleMessage(String value) {
        String trimmed = value.replaceAll("[\\p{Cntrl}&&[^\\n]]", " ").trim();
        return trimmed.length() <= 180 ? trimmed : trimmed.substring(0, 180);
    }

    private final class Session {
        final String sessionToken;
        final int sessionPort;
        final PairingLink paired;
        final ArrayBlockingQueue<WireProtocol.Packet> outgoing = new ArrayBlockingQueue<>(64);
        final Thread reader;
        volatile boolean running = true;
        volatile boolean connected;
        volatile Socket socket;
        volatile Thread writer;
        volatile PresentationProgress presentation;
        volatile boolean hasPresentedFrame;
        private String lastDisplayProfile;
        private final AtomicLong frameIds = new AtomicLong();
        private FrameRateMeter jpegRate = new FrameRateMeter();
        private long lastJpegFrameId;
        volatile VideoDecoder video;
        volatile long videoGeneration;
        volatile double actualFps;
        volatile String streamCodec = "JPEG";
        volatile String decoderName = "";
        volatile long droppedFrames;
        volatile CapturePauseState captureState = new CapturePauseState();

        Session(String token, int port, PairingLink paired) {
            sessionToken = token;
            sessionPort = port;
            this.paired = paired;
            reader = new Thread(this::run, "TabLink-receiver");
        }

        String connectionLabel() { return paired == null ? "USB 调试通道" : "加密网络 · " + paired.host; }

        void stop() {
            running = false;
            connected = false;
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
                setStatus(this, connectionLabel() + " 已连接 · " + width + " × " + height + " · 触摸控制", true);
                if (paired != null) getSharedPreferences("pairing", MODE_PRIVATE).edit()
                        .putString("lastLink", paired.toPrivateUri()).apply();
            }
            final String telemetry = String.format(Locale.ROOT, "%s · 解码 %.1f fps", streamCodec, actualFps);
            ui.post(() -> {
                if (session != this || !activityStarted) return;
                updateStreamingBrightness(this);
                updatePerformance(this);
                String detail = captureState.paused ? captureState.message
                        : connectionLabel() + " 已连接 · " + width + " × " + height + " · " + telemetry;
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
            if (previous != null) previous.close();
            ui.post(() -> {
                if (session == this || session == null) {
                    restoreStreamingBrightness(this);
                    videoSurface.hide();
                    desktop.setVisibility(View.VISIBLE);
                }
            });
        }

        void configureVideo(byte[] bytes, PresentationProgress progress) throws IOException {
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
            droppedFrames = 0;
            VideoDecoder next = new VideoDecoder(configuration, new VideoDecoder.Listener() {
                @Override public void onPresented(long ptsUs, long renderNanos, int width, int height,
                        double fps, String decoder, long dropped) {
                    if (!running || !connected || session != Session.this || generation != videoGeneration || presentation != progress) return;
                    actualFps = fps;
                    decoderName = decoder;
                    droppedFrames = dropped;
                    framePresented(progress, frameIds.incrementAndGet(), width, height);
                }
                @Override public void onSizeChanged(int width, int height) {
                    ui.post(() -> {
                        if (session == Session.this && generation == videoGeneration) videoSurface.setImageSize(width, height);
                    });
                }
                @Override public void onError(String message) {
                    if (generation != videoGeneration) return;
                    setStatus(Session.this, message, false);
                    closeSocket();
                }
            }, renderPacingEnabled);
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

        void run() {
            int retry = 0;
            while (running) {
                PresentationProgress progress = new PresentationProgress();
                presentation = progress;
                hasPresentedFrame = false;
                captureState = new CapturePauseState();
                lastDisplayProfile = null;
                frameIds.set(0);
                lastJpegFrameId = 0;
                jpegRate = new FrameRateMeter();
                boolean serverRejected = false;
                try {
                    setStatus(this, (retry == 0 ? "正在连接 " : "正在重新连接 ") + connectionLabel() + "…", false);
                    Socket local = paired == null ? new Socket() : PinnedTls.createSocket(paired.certificateSha256);
                    socket = local;
                    if (!running) { local.close(); break; }
                    local.connect(new InetSocketAddress(paired == null ? "127.0.0.1" : paired.host, sessionPort), 5000);
                    local.setTcpNoDelay(true);
                    local.setSoTimeout(8000);
                    if (local instanceof SSLSocket) ((SSLSocket) local).startHandshake();
                    local.setSoTimeout(15000);
                    DataOutputStream output = new DataOutputStream(new BufferedOutputStream(local.getOutputStream()));
                    JSONObject hello = new JSONObject();
                    hello.put("protocol", 1);
                    hello.put("token", sessionToken);
                    WireProtocol.write(output, WireProtocol.HELLO, hello.toString().getBytes(StandardCharsets.UTF_8));
                    // Send the initial native/rotation/requested-Hz profile synchronously,
                    // before any decoder is created or incoming video packet is consumed.
                    lastDisplayProfile = DisplayCapabilities.read(MainActivity.this).toString();
                    WireProtocol.write(output, WireProtocol.DISPLAY_PROFILE, lastDisplayProfile.getBytes(StandardCharsets.UTF_8));
                    outgoing.clear();
                    connected = true;
                    writer = new Thread(() -> writeLoop(local, output), "TabLink-input");
                    writer.start();
                    DataInputStream input = new DataInputStream(new BufferedInputStream(local.getInputStream()));
                    setStatus(this, connectionLabel() + " 已连接，等待桌面画面…", false);
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
                            configureVideo(packet.payload, progress);
                        } else if (packet.type == WireProtocol.VIDEO_FRAME) {
                            VideoDecoder currentVideo = video;
                            if (currentVideo == null) throw new IOException("H.264 configuration must precede video frames");
                            currentVideo.offer(packet.payload);
                            retry = 0;
                        } else if (packet.type == WireProtocol.STATUS) {
                            if (packet.payload.length > 16384) throw new IOException("状态数据过长");
                            JSONObject state = new JSONObject(new String(packet.payload, StandardCharsets.UTF_8));
                            String message = state.optString("message", connectionLabel() + " 已连接");
                            Object paused = state.opt("capturePaused");
                            captureState = captureState.update(paused instanceof Boolean ? (Boolean) paused : null,
                                    visibleMessage(message));
                            // Keep the same decoder, Surface, last frame and presentation sequence.
                            // Only actual JPEG/codec rendering may advance an acknowledgement.
                            setStatus(this, visibleMessage(message), hasPresentedFrame);
                        } else if (packet.type == WireProtocol.ERROR) {
                            serverRejected = true;
                            String message = new String(packet.payload, 0, Math.min(packet.payload.length, 4096), StandardCharsets.UTF_8);
                            throw new IOException(visibleMessage(message));
                        } else {
                            throw new IOException("电脑端协议不兼容，请更新两端 TabLink");
                        }
                    }
                } catch (IOException | JSONException | IllegalArgumentException problem) {
                    if (running) {
                        boolean certificateFailure = paired != null && problem instanceof SSLHandshakeException;
                        if (certificateFailure) serverRejected = true;
                        String message = certificateFailure ? "无法验证电脑证书。请检查平板日期，重新扫描电脑当前二维码；不会使用未验证的连接。"
                                : serverRejected ? "电脑端提示：" + problem.getMessage()
                                : paired == null ? "连接中断，请检查 USB 调试通道和电脑端；将自动重连"
                                : "尚未连通电脑，将自动重试。请确认同一 Wi-Fi 或已开启 USB 网络共享，电脑网络会话仍在运行；会话重开后需重新扫码。";
                        hasPresentedFrame = false;
                        setStatus(this, message, false);
                    }
                } finally {
                    connected = false;
                    stopVideo();
                    closeSocket();
                    Thread currentWriter = writer;
                    if (currentWriter != null) {
                        currentWriter.interrupt();
                        try { currentWriter.join(1500); } catch (InterruptedException ignored) { }
                    }
                    outgoing.clear();
                    desktop.clearFrame(this);
                }
                if (serverRejected) { running = false; break; }
                if (!running) break;
                long delay = Math.min(10000, 1000L << Math.min(retry++, 4));
                try { Thread.sleep(delay); } catch (InterruptedException interrupted) { break; }
            }
        }

        void writeLoop(Socket local, DataOutputStream output) {
            try {
                while (running && !local.isClosed()) {
                    WireProtocol.Packet packet = outgoing.take();
                    WireProtocol.write(output, packet.type, packet.payload);
                }
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            } catch (IOException ignored) {
                try { local.close(); } catch (IOException alsoIgnored) { }
            }
        }
    }

    private static Bitmap decodeFrame(byte[] bytes) throws IOException {
        if (bytes.length < 3 || bytes[0] != (byte) 0xff || bytes[1] != (byte) 0xd8) {
            throw new IOException("画面不是 JPEG 数据");
        }
        BitmapFactory.Options size = new BitmapFactory.Options();
        size.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(bytes, 0, bytes.length, size);
        if (size.outWidth <= 0 || size.outHeight <= 0 || size.outWidth > 8192 || size.outHeight > 8192
                || (long) size.outWidth * size.outHeight > 16000000L) {
            throw new IOException("画面尺寸无效或过大");
        }
        BitmapFactory.Options decode = new BitmapFactory.Options();
        decode.inPreferredConfig = Bitmap.Config.RGB_565;
        Bitmap bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, decode);
        if (bitmap == null) throw new IOException("无法解码桌面画面");
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
            setContentDescription("电脑扩展桌面，H.264 硬件解码");
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
            setContentDescription("电脑扩展桌面，单指点击和拖动模拟鼠标");
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
