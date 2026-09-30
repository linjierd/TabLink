package com.tablink.client;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.graphics.SurfaceTexture;
import android.hardware.Camera;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.google.zxing.ReaderException;
import java.io.IOException;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;

/** Short-lived native camera screen. Images stay in memory and are never uploaded or saved. */
@SuppressWarnings("deprecation")
public final class QrScannerActivity extends Activity implements TextureView.SurfaceTextureListener {
    public static final int RESULT_PASTE = RESULT_FIRST_USER;
    private static final int CAMERA_PERMISSION = 72;
    private final AtomicInteger generation = new AtomicInteger();
    private HandlerThread cameraThread;
    private Handler cameraWork;
    private TextureView preview;
    private FrameLayout root;
    private TextView instruction;
    private volatile boolean resumed;
    private volatile boolean completed;
    private volatile int displayRotation;
    private volatile SurfaceTexture previewTexture;
    // Accessed on the camera thread only.
    private Camera camera;
    private int cameraId;
    private int previewWidth;
    private int previewHeight;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON | WindowManager.LayoutParams.FLAG_SECURE);
        cameraThread = new HandlerThread("TabLink-QR-camera");
        cameraThread.start();
        cameraWork = new Handler(cameraThread.getLooper());
        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        preview = new TextureView(this);
        preview.setSurfaceTextureListener(this);
        root.addView(preview, new FrameLayout.LayoutParams(-1, -1, Gravity.CENTER));
        LinearLayout controls = new LinearLayout(this);
        controls.setOrientation(LinearLayout.VERTICAL);
        controls.setPadding(dp(20), dp(16), dp(20), dp(20));
        controls.setBackgroundColor(0xdd101922);
        instruction = new TextView(this);
        instruction.setText("对准电脑端 TabLink 的连接二维码\n请让整个二维码出现在取景框内");
        instruction.setTextSize(17);
        instruction.setTextColor(Color.WHITE);
        instruction.setGravity(Gravity.CENTER);
        instruction.setPadding(0, 0, 0, dp(12));
        controls.addView(instruction);
        Button paste = new Button(this);
        paste.setText("改为粘贴连接链接");
        paste.setOnClickListener(v -> { setResult(RESULT_PASTE); finish(); });
        controls.addView(paste, new LinearLayout.LayoutParams(-1, dp(52)));
        Button cancel = new Button(this);
        cancel.setText("返回");
        cancel.setOnClickListener(v -> finish());
        controls.addView(cancel, new LinearLayout.LayoutParams(-1, dp(48)));
        root.addView(controls, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM));
        setContentView(root);
        displayRotation = getWindowManager().getDefaultDisplay().getRotation();
    }

    @Override protected void onResume() {
        super.onResume();
        resumed = true;
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) scheduleOpen();
        else requestPermissions(new String[] {Manifest.permission.CAMERA}, CAMERA_PERMISSION);
    }

    @Override protected void onPause() {
        resumed = false;
        stopCamera(null);
        super.onPause();
    }

    @Override protected void onDestroy() {
        stopCamera(null);
        cameraThread.quitSafely();
        super.onDestroy();
    }

    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        displayRotation = getWindowManager().getDefaultDisplay().getRotation();
        cameraWork.post(() -> { if (camera != null) updateOrientation(camera); });
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request != CAMERA_PERMISSION) return;
        if (results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) scheduleOpen();
        else showMessage("未授予相机权限。可返回后重试，或使用下方的粘贴连接链接。它不需要相机权限。");
    }

    @Override public void onSurfaceTextureAvailable(SurfaceTexture texture, int width, int height) {
        previewTexture = texture;
        scheduleOpen();
    }

    @Override public void onSurfaceTextureSizeChanged(SurfaceTexture texture, int width, int height) { }
    @Override public void onSurfaceTextureUpdated(SurfaceTexture texture) { }

    @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture texture) {
        previewTexture = null;
        stopCamera(texture);
        // Release only after the camera has relinquished this texture on its thread.
        return false;
    }

    private void scheduleOpen() {
        if (!resumed || previewTexture == null || completed
                || checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        int expected = generation.get();
        cameraWork.post(() -> openCamera(expected));
    }

    private void openCamera(int expected) {
        if (!resumed || completed || generation.get() != expected || previewTexture == null || camera != null) return;
        try {
            int count = Camera.getNumberOfCameras();
            if (count == 0) throw new IOException("No camera");
            cameraId = 0;
            Camera.CameraInfo info = new Camera.CameraInfo();
            for (int id = 0; id < count; id++) {
                Camera.getCameraInfo(id, info);
                if (info.facing == Camera.CameraInfo.CAMERA_FACING_BACK) { cameraId = id; break; }
            }
            camera = Camera.open(cameraId);
            if (!resumed || generation.get() != expected) { closeCamera(); return; }
            Camera.Parameters parameters = camera.getParameters();
            Camera.Size selected = parameters.getPreviewSize();
            long bestArea = 0;
            for (Camera.Size size : parameters.getSupportedPreviewSizes()) {
                long area = (long) size.width * size.height;
                if (area <= 1280L * 720 && area > bestArea) { selected = size; bestArea = area; }
            }
            previewWidth = selected.width;
            previewHeight = selected.height;
            parameters.setPreviewSize(previewWidth, previewHeight);
            parameters.setPreviewFormat(ImageFormat.NV21);
            List<String> focus = parameters.getSupportedFocusModes();
            if (focus != null && focus.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE))
                parameters.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            camera.setParameters(parameters);
            camera.setPreviewTexture(previewTexture);
            updateOrientation(camera);
            camera.startPreview();
            requestFrame(camera, expected);
        } catch (IOException | RuntimeException unavailable) {
            closeCamera();
            showMessage("无法使用相机，可能正被其他应用占用。关闭占用相机的应用后重试，或使用粘贴连接链接。");
        }
    }

    private void updateOrientation(Camera active) {
        Camera.CameraInfo info = new Camera.CameraInfo();
        Camera.getCameraInfo(cameraId, info);
        int degrees = displayRotation == Surface.ROTATION_90 ? 90 : displayRotation == Surface.ROTATION_180 ? 180
                : displayRotation == Surface.ROTATION_270 ? 270 : 0;
        int rotation = info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT
                ? (360 - (info.orientation + degrees) % 360) % 360 : (info.orientation - degrees + 360) % 360;
        active.setDisplayOrientation(rotation);
        int imageWidth = rotation % 180 == 0 ? previewWidth : previewHeight;
        int imageHeight = rotation % 180 == 0 ? previewHeight : previewWidth;
        runOnUiThread(() -> {
            if (root.getWidth() <= 0 || root.getHeight() <= 0 || imageWidth <= 0 || imageHeight <= 0) return;
            float scale = Math.min((float) root.getWidth() / imageWidth, (float) root.getHeight() / imageHeight);
            preview.setLayoutParams(new FrameLayout.LayoutParams(Math.round(imageWidth * scale),
                    Math.round(imageHeight * scale), Gravity.CENTER));
        });
    }

    private void requestFrame(Camera active, int expected) {
        if (!resumed || completed || generation.get() != expected || active != camera) return;
        active.setOneShotPreviewCallback((bytes, source) -> {
            if (!resumed || completed || source != camera || generation.get() != expected) return;
            try {
                String text = QrCodeDecoder.decode(bytes, previewWidth, previewHeight);
                PairingLink paired = PairingLink.parse(text);
                completed = true;
                closeCamera();
                runOnUiThread(() -> {
                    if (isFinishing()) return;
                    setResult(RESULT_OK, new Intent().putExtra("pairingLink", paired.toPrivateUri()));
                    finish();
                });
                return;
            } catch (ReaderException noQrInThisFrame) {
                // A camera frame without a readable QR is normal.
            } catch (IllegalArgumentException invalidLink) {
                showMessage("这个二维码不是有效的 TabLink 连接码。请扫描电脑端当前会话的二维码。");
            }
            cameraWork.postDelayed(() -> {
                try { requestFrame(active, expected); }
                catch (RuntimeException cameraLost) { closeCamera(); showMessage("相机连接中断，可返回重试或粘贴连接链接。"); }
            }, 120);
        });
    }

    private void stopCamera(SurfaceTexture releaseAfterStop) {
        generation.incrementAndGet();
        boolean posted = cameraWork.post(() -> {
            closeCamera();
            if (releaseAfterStop != null) releaseAfterStop.release();
        });
        if (!posted && releaseAfterStop != null) releaseAfterStop.release();
    }

    private void closeCamera() {
        Camera previous = camera;
        camera = null;
        if (previous == null) return;
        try { previous.setPreviewCallback(null); previous.stopPreview(); }
        catch (RuntimeException ignored) { }
        finally { previous.release(); }
    }

    private void showMessage(String message) {
        runOnUiThread(() -> { if (!isFinishing() && !completed) instruction.setText(message); });
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
