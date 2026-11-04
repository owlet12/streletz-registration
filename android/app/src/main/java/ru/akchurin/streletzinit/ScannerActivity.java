// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;
import android.widget.Toast;
import android.widget.LinearLayout;
import android.widget.SeekBar;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;

import java.io.IOException;
import java.util.Collections;
import java.util.EnumMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

@SuppressWarnings("deprecation")
public class ScannerActivity extends Activity implements SurfaceHolder.Callback, Camera.PreviewCallback {
    private static final int CAMERA_PERMISSION = 301;
    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private final MultiFormatReader reader = new MultiFormatReader();
    private SurfaceView preview;
    private FrameLayout root;
    private TextView scanFrame;
    private TextView guide;
    private LinearLayout controls;
    private TextView zoomLabel;
    private SeekBar zoom;
    private Button torch;
    private boolean torchOn;
    private boolean resumed;
    private int cameraId;
    private boolean mirrored;
    private int orientation;
    private int cameraGeneration;
    private Camera camera;
    private boolean surfaceReady;
    private boolean decoding;
    private final QrFramePool frames = new QrFramePool();
    private byte[] cameraBuffer;
    private boolean bufferQueued;
    private boolean finished;
    private int previewWidth;
    private int previewHeight;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        hints.put(DecodeHintType.CHARACTER_SET, "UTF-8");
        reader.setHints(hints);
        buildScreen();
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, CAMERA_PERMISSION);
        }
    }

    private void buildScreen() {
        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        preview = new SurfaceView(this);
        preview.getHolder().addCallback(this);
        root.addView(preview, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        guide = new TextView(this);
        guide.setText("Наведите камеру на QR-код наклейки\nID и key будут заполнены автоматически");
        guide.setTextColor(Color.WHITE);
        guide.setTextSize(17);
        guide.setGravity(Gravity.CENTER);
        guide.setBackgroundColor(Color.argb(165, 0, 0, 0));
        guide.setPadding(dp(18), dp(14), dp(18), dp(14));
        FrameLayout.LayoutParams guideLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.TOP);
        guideLp.setMargins(dp(12), dp(20), dp(12), 0);
        root.addView(guide, guideLp);

        scanFrame = new TextView(this);
        scanFrame.setBackgroundResource(resourceDrawable("scanner_frame"));
        FrameLayout.LayoutParams frameLp = new FrameLayout.LayoutParams(dp(270), dp(270), Gravity.CENTER);
        root.addView(scanFrame, frameLp);

        controls = new LinearLayout(this);
        controls.setOrientation(LinearLayout.VERTICAL);
        controls.setPadding(dp(12), dp(6), dp(12), dp(6));
        controls.setBackgroundColor(Color.argb(185, 0, 0, 0));
        zoomLabel = new TextView(this);
        zoomLabel.setTextColor(Color.WHITE);
        zoomLabel.setGravity(Gravity.CENTER);
        zoomLabel.setText("Увеличение 1,0×");
        controls.addView(zoomLabel);
        zoom = new SeekBar(this);
        zoom.setEnabled(false);
        controls.addView(zoom, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(36)));
        zoom.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar bar, int value, boolean fromUser) { if (fromUser) changeZoom(value); }
            public void onStartTrackingTouch(SeekBar bar) { }
            public void onStopTrackingTouch(SeekBar bar) { }
        });
        LinearLayout buttons = new LinearLayout(this);
        torch = new Button(this);
        torch.setText("Фонарик");
        torch.setAllCaps(false);
        torch.setEnabled(false);
        torch.setOnClickListener(v -> toggleTorch());
        buttons.addView(torch, new LinearLayout.LayoutParams(0, dp(50), 1));
        Button cancel = new Button(this);
        cancel.setText("Отмена");
        cancel.setAllCaps(false);
        cancel.setOnClickListener(v -> finish());
        buttons.addView(cancel, new LinearLayout.LayoutParams(0, dp(50), 1));
        controls.addView(buttons);
        FrameLayout.LayoutParams controlsLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM);
        controlsLp.setMargins(dp(12), 0, dp(12), dp(20));
        root.addView(controls, controlsLp);
        root.addOnLayoutChangeListener((view, l, t, r, b, oldL, oldT, oldR, oldB) -> layoutPreview());
        setContentView(root);
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) openCamera();
    }

    @Override
    protected void onPause() {
        resumed = false;
        closeCamera();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        decoder.shutdownNow();
        super.onDestroy();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode != CAMERA_PERMISSION) return;
        if (results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) openCamera();
        else {
            Toast.makeText(this, "Для сканирования нужен доступ к камере", Toast.LENGTH_LONG).show();
            finish();
        }
    }

    private void openCamera() {
        if (camera != null || !surfaceReady || finished || !resumed
                || checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        try {
            cameraId = 0;
            Camera.CameraInfo info = new Camera.CameraInfo();
            for (int i = 0; i < Camera.getNumberOfCameras(); i++) {
                Camera.getCameraInfo(i, info);
                if (info.facing == Camera.CameraInfo.CAMERA_FACING_BACK) { cameraId = i; break; }
            }
            Camera.getCameraInfo(cameraId, info);
            mirrored = info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT;
            camera = Camera.open(cameraId);
            cameraGeneration++;
            Camera.Parameters parameters = camera.getParameters();
            Camera.Size size = choosePreviewSize(parameters.getSupportedPreviewSizes());
            parameters.setPreviewSize(size.width, size.height);
            previewWidth = size.width;
            previewHeight = size.height;
            List<String> focusModes = parameters.getSupportedFocusModes();
            if (focusModes != null && focusModes.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE))
                parameters.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            parameters.setPreviewFormat(ImageFormat.NV21);
            camera.setParameters(parameters);
            orientation = displayOrientation();
            camera.setDisplayOrientation(orientation);
            configureControls();
            layoutPreview();
            camera.setPreviewDisplay(preview.getHolder());
            cameraBuffer = new byte[Math.multiplyExact(previewWidth, previewHeight) * 3 / 2];
            bufferQueued = false;
            camera.setPreviewCallbackWithBuffer(this);
            camera.startPreview();
            requestFrame();
        } catch (RuntimeException | IOException error) {
            closeCamera();
            Toast.makeText(this, "Не удалось открыть камеру: " + error.getMessage(), Toast.LENGTH_LONG).show();
            finish();
        }
    }

    private Camera.Size choosePreviewSize(List<Camera.Size> sizes) {
        Camera.Size best = sizes.get(0);
        long target = 1280L * 720L;
        for (Camera.Size size : sizes) {
            long pixels = (long) size.width * size.height;
            if (Math.abs(pixels - target) < Math.abs((long) best.width * best.height - target)) best = size;
        }
        return best;
    }

    private int displayOrientation() {
        Camera.CameraInfo info = new Camera.CameraInfo();
        Camera.getCameraInfo(cameraId, info);
        int rotation = getWindowManager().getDefaultDisplay().getRotation();
        int degrees = rotation == Surface.ROTATION_90 ? 90 : rotation == Surface.ROTATION_180 ? 180
                : rotation == Surface.ROTATION_270 ? 270 : 0;
        return mirrored ? (360 - (info.orientation + degrees) % 360) % 360
                : (info.orientation - degrees + 360) % 360;
    }

    private void closeCamera() {
        Camera current = camera;
        camera = null;
        cameraGeneration++;
        decoding = false;
        cameraBuffer = null;
        bufferQueued = false;
        torchOn = false;
        if (torch != null) { torch.setEnabled(false); torch.setText("Фонарик"); }
        if (zoom != null) zoom.setEnabled(false);
        if (current != null) {
            try {
                current.setPreviewCallback(null);
                Camera.Parameters parameters = current.getParameters();
                if (parameters.getSupportedFlashModes() != null && parameters.getSupportedFlashModes().contains(Camera.Parameters.FLASH_MODE_OFF)) {
                    parameters.setFlashMode(Camera.Parameters.FLASH_MODE_OFF);
                    current.setParameters(parameters);
                }
            } catch (RuntimeException ignored) { }
            try { current.stopPreview(); } catch (RuntimeException ignored) { }
            try { current.release(); } catch (RuntimeException ignored) { }
        }
    }

    @Override public void surfaceCreated(SurfaceHolder holder) { surfaceReady = true; openCamera(); }
    @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { requestFrame(); }
    @Override public void surfaceDestroyed(SurfaceHolder holder) { surfaceReady = false; closeCamera(); }

    private void configureControls() {
        Camera.Parameters parameters = camera.getParameters();
        List<String> flashModes = parameters.getSupportedFlashModes();
        boolean hasTorch = flashModes != null && flashModes.contains(Camera.Parameters.FLASH_MODE_TORCH)
                && flashModes.contains(Camera.Parameters.FLASH_MODE_OFF);
        torch.setEnabled(hasTorch);
        torch.setText(hasTorch ? "Фонарик" : "Нет фонарика");
        boolean hasZoom = parameters.isZoomSupported() && parameters.getMaxZoom() > 0;
        zoom.setEnabled(hasZoom);
        zoom.setMax(hasZoom ? parameters.getMaxZoom() : 0);
        zoom.setProgress(hasZoom ? parameters.getZoom() : 0);
        zoomLabel.setText(hasZoom ? zoomText(parameters) : "Увеличение недоступно");
    }

    private String zoomText(Camera.Parameters parameters) {
        List<Integer> ratios = parameters.getZoomRatios();
        return ratios == null ? "Увеличение" : String.format(java.util.Locale.getDefault(),
                "Увеличение %.1f×", ratios.get(parameters.getZoom()) / 100.0);
    }

    private void changeZoom(int value) {
        if (camera == null) return;
        try {
            Camera.Parameters parameters = camera.getParameters();
            if (!parameters.isZoomSupported()) return;
            parameters.setZoom(Math.max(0, Math.min(parameters.getMaxZoom(), value)));
            camera.setParameters(parameters);
            zoomLabel.setText(zoomText(camera.getParameters()));
        } catch (RuntimeException error) { Toast.makeText(this, "Камера не смогла изменить увеличение", Toast.LENGTH_SHORT).show(); }
    }

    private void toggleTorch() {
        if (camera == null) return;
        try {
            Camera.Parameters parameters = camera.getParameters();
            parameters.setFlashMode(torchOn ? Camera.Parameters.FLASH_MODE_OFF : Camera.Parameters.FLASH_MODE_TORCH);
            camera.setParameters(parameters);
            torchOn = !torchOn;
            torch.setText(torchOn ? "Выключить свет" : "Фонарик");
        } catch (RuntimeException error) { Toast.makeText(this, "Фонарик недоступен", Toast.LENGTH_SHORT).show(); }
    }

    private void layoutPreview() {
        if (previewWidth <= 0 || previewHeight <= 0 || root.getWidth() <= 0 || root.getHeight() <= 0) return;
        int[] fit = ScannerGeometry.fit(previewWidth, previewHeight, orientation, root.getWidth(), root.getHeight());
        FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) preview.getLayoutParams();
        if (lp.width != fit[0] || lp.height != fit[1]) {
            lp.width = fit[0]; lp.height = fit[1]; lp.gravity = Gravity.CENTER;
            preview.setLayoutParams(lp);
        }
        int previewTop = (root.getHeight() - fit[1]) / 2;
        int top = Math.max(previewTop, guide.getBottom() + dp(8));
        int bottom = Math.min(previewTop + fit[1], controls.getTop() > 0 ? controls.getTop() - dp(8) : root.getHeight() - dp(150));
        int side = Math.min(dp(270), Math.min((int) (fit[0] * 0.72), Math.max(dp(48), bottom - top)));
        int left = (root.getWidth() - side) / 2;
        int frameTop = top + Math.max(0, (bottom - top - side) / 2);
        FrameLayout.LayoutParams frame = (FrameLayout.LayoutParams) scanFrame.getLayoutParams();
        if (frame.width != side || frame.height != side || frame.leftMargin != left || frame.topMargin != frameTop
                || frame.gravity != (Gravity.TOP | Gravity.LEFT)) {
            frame.width = side; frame.height = side; frame.gravity = Gravity.TOP | Gravity.LEFT;
            frame.leftMargin = left; frame.topMargin = frameTop; scanFrame.setLayoutParams(frame);
        }
    }

    private void requestFrame() {
        if (camera == null || !resumed || finished || decoding || bufferQueued || cameraBuffer == null) return;
        try { camera.addCallbackBuffer(cameraBuffer); bufferQueued = true; } catch (RuntimeException ignored) { }
    }

    @Override
    public void onPreviewFrame(byte[] data, Camera source) {
        if (source != camera) return;
        bufferQueued = false;
        if (!resumed || decoding || finished || data == null || previewWidth <= 0 || previewHeight <= 0) return;
        if (preview.getWidth() <= 0 || preview.getHeight() <= 0 || scanFrame.getWidth() <= 0) { requestFrame(); return; }
        final ScannerGeometry.Crop crop;
        try {
            // Frame and preview share the same parent; account for letterboxing and rotation.
            crop = ScannerGeometry.crop(previewWidth, previewHeight, preview.getWidth(), preview.getHeight(),
                    scanFrame.getLeft() - preview.getLeft(), scanFrame.getTop() - preview.getTop(),
                    scanFrame.getRight() - preview.getLeft(), scanFrame.getBottom() - preview.getTop(), orientation, mirrored);
        } catch (IllegalArgumentException invalidLayout) { requestFrame(); return; }
        QrFramePool.Frame frame;
        try { frame = frames.crop(data, previewWidth, previewHeight, crop); }
        catch (IllegalArgumentException invalidFrame) { requestFrame(); return; }
        if (frame == null) { requestFrame(); return; }
        int generation = cameraGeneration;
        decoding = true;
        try { decoder.execute(() -> decode(frame, generation)); }
        catch (java.util.concurrent.RejectedExecutionException stopped) { frames.release(frame); decoding = false; }
    }

    private void decode(QrFramePool.Frame frame, int generation) {
        long started = System.nanoTime();
        String value = null;
        try {
            PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                    frame.data, frame.width, frame.height, 0, 0, frame.width, frame.height, false);
            Result result = reader.decodeWithState(new BinaryBitmap(new HybridBinarizer(source)));
            value = result.getText();
        } catch (Exception ignored) { }
        finally { reader.reset(); frames.release(frame); }
        PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.QR_DECODE, System.nanoTime() - started, 1, value != null);
        String decoded = value;
        runOnUiThread(() -> {
            if (generation != cameraGeneration || finished || camera == null || !resumed) return;
            decoding = false;
            if (decoded != null && !decoded.trim().isEmpty()) {
                finished = true;
                Intent result = new Intent();
                result.putExtra("qr_text", decoded.trim());
                setResult(RESULT_OK, result);
                finish();
            } else requestFrame();
        });
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private int resourceDrawable(String name) { return getResources().getIdentifier(name, "drawable", getPackageName()); }
}
