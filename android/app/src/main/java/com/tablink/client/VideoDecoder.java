package com.tablink.client;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Base64;
import android.view.Surface;
import org.json.JSONException;
import org.json.JSONObject;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** Hardware AVC decoder with bounded pending input and render-confirmed telemetry. */
public final class VideoDecoder implements AutoCloseable {
    private static final AtomicLong DIAGNOSTIC_IDS = new AtomicLong();
    private static volatile long currentDiagnosticId;
    private static volatile String pacingDiagnostics = "{\"active\":false,\"enabled\":false}";
    public interface Listener {
        void onSubmitted(long ptsUs, long submittedNanos, int width, int height, String decoder);
        void onPresented(long ptsUs, long renderNanos, int width, int height, double fps, String decoder, long dropped);
        void onSizeChanged(int width, int height);
        void onError(String message);
    }

    public static final class Configuration {
        public final int width, height;
        public final float fps;
        final byte[] sps, pps;
        private Configuration(int width, int height, float fps, byte[] sps, byte[] pps) {
            this.width = width; this.height = height; this.fps = fps; this.sps = sps; this.pps = pps;
        }
        public static Configuration parse(byte[] bytes) throws IOException {
            if (bytes.length > 131072) throw new IOException("H.264 configuration is too large");
            try {
                JSONObject json = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
                if (!"video/avc".equals(json.getString("codec"))) throw new IOException("Unsupported video codec");
                int width = json.getInt("width"), height = json.getInt("height");
                float fps = (float) json.getDouble("fps");
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192
                        || (long) width * height > 16000000 || !Float.isFinite(fps) || fps < 1 || fps > 240)
                    throw new IOException("H.264 dimensions or frame rate are invalid");
                byte[] sps = normalizeCsd(Base64.decode(json.getString("csd0"), Base64.DEFAULT), 7);
                byte[] pps = normalizeCsd(Base64.decode(json.getString("csd1"), Base64.DEFAULT), 8);
                return new Configuration(width, height, fps, sps, pps);
            } catch (JSONException | IllegalArgumentException problem) {
                throw new IOException("Invalid H.264 configuration", problem);
            }
        }
        private static byte[] normalizeCsd(byte[] data, int type) throws IOException {
            if (data.length < 4 || data.length > 65536 || !VideoAccessUnit.containsNal(data, type))
                throw new IOException("H.264 parameter set is missing");
            if (data[0] == 0 && data[1] == 0 && data[2] == 1) {
                byte[] fourByteStart = new byte[data.length + 1];
                System.arraycopy(data, 0, fourByteStart, 1, data.length);
                return fourByteStart;
            }
            if (data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 1)
                throw new IOException("H.264 parameter set must use Annex-B");
            return data;
        }
    }

    public final Configuration configuration;
    private final Listener listener;
    private final HandlerThread thread = new HandlerThread("TabLink-AVC-decoder");
    private final Handler handler;
    private final VideoFrameQueue pending = new VideoFrameQueue(6);
    private final ArrayDeque<Integer> inputSlots = new ArrayDeque<>();
    private final AtomicBoolean pumpPosted = new AtomicBoolean();
    private final CountDownLatch released = new CountDownLatch(1);
    private final FrameRateMeter rate = new FrameRateMeter();
    private volatile boolean closed;
    private boolean startScheduled;
    private MediaCodec codec;
    private String decoderName = "";
    private int outputWidth, outputHeight;
    private long lastRenderedPts = -1;
    private final long diagnosticId = DIAGNOSTIC_IDS.incrementAndGet();
    private RenderClock renderClock;
    private volatile long latestInputPtsUs = -1;
    private long pacingEpoch, renderCallbacks, lastDiagnosticNanos;
    private double actualCallbackFps;
    private final long inputBackpressureNanos;
    private volatile long receivedFrames, longestArrivalGapNanos;
    private long lastArrivalNanos;
    private long submittedFrames, lastSubmitNanos, longestSubmitGapNanos;
    private long totalInputAgeNanos, longestInputAgeNanos;

    public VideoDecoder(Configuration configuration, Listener listener, boolean renderPacing) {
        this.configuration = configuration;
        this.listener = listener;
        inputBackpressureNanos = Math.min(25_000_000L, Math.round(2_000_000_000.0 / configuration.fps));
        renderClock = new RenderClock(configuration.fps, renderPacing);
        outputWidth = configuration.width;
        outputHeight = configuration.height;
        currentDiagnosticId = diagnosticId;
        publishPacingDiagnostics(true);
        thread.start();
        handler = new Handler(thread.getLooper());
    }

    public static String readPacingDiagnostics() { return pacingDiagnostics; }

    /** Changes only this decoder's in-memory scheduling; does not reconnect or alter authentication. */
    public void setRenderPacing(boolean enabled) {
        if (closed) return;
        handler.post(() -> {
            if (closed || renderClock.enabled() == enabled) return;
            renderClock = new RenderClock(configuration.fps, enabled);
            pacingEpoch++;
            publishPacingDiagnostics(true);
        });
    }

    public synchronized void start(Surface surface) {
        if (closed || startScheduled) return;
        startScheduled = true;
        handler.post(() -> initialize(surface));
    }

    public void offer(byte[] packet) throws IOException {
        if (closed) return;
        long arrived = System.nanoTime();
        VideoAccessUnit frame = VideoAccessUnit.parse(packet, arrived);
        receivedFrames++;
        if (lastArrivalNanos != 0)
            longestArrivalGapNanos = Math.max(longestArrivalGapNanos, arrived - lastArrivalNanos);
        lastArrivalNanos = arrived;
        final boolean accepted;
        try { accepted = pending.offerWithBackpressure(frame, inputBackpressureNanos); }
        catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
            throw new IOException("Video input interrupted", interrupted);
        }
        if (accepted) {
            latestInputPtsUs = frame.ptsUs;
            if (pumpPosted.compareAndSet(false, true))
                handler.post(() -> { pumpPosted.set(false); pumpInputs(); });
        }
    }

    private void initialize(Surface surface) {
        if (closed) return;
        try {
            if (!surface.isValid()) throw new IOException("Video surface is unavailable");
            MediaCodecInfo selected = null;
            for (MediaCodecInfo info : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
                if (info.isEncoder() || !isHardware(info)) continue;
                for (String type : info.getSupportedTypes()) {
                    if (!"video/avc".equalsIgnoreCase(type)) continue;
                    MediaCodecInfo.VideoCapabilities video = info.getCapabilitiesForType(type).getVideoCapabilities();
                    if (video != null && video.isSizeSupported(configuration.width, configuration.height)) {
                        selected = info;
                        break;
                    }
                }
                if (selected != null) break;
            }
            if (selected == null) throw new IOException("No hardware H.264 decoder supports this resolution");
            decoderName = selected.getName();
            codec = MediaCodec.createByCodecName(decoderName);
            codec.setCallback(new MediaCodec.Callback() {
                @Override public void onInputBufferAvailable(MediaCodec source, int index) {
                    if (closed || source != codec) return;
                    inputSlots.add(index);
                    pumpInputs();
                }
                @Override public void onOutputBufferAvailable(MediaCodec source, int index, MediaCodec.BufferInfo info) {
                    if (closed || source != codec) return;
                    try {
                        if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0)
                            source.releaseOutputBuffer(index, false);
                        else {
                            RenderClock.Decision decision = renderClock.plan(info.presentationTimeUs,
                                    latestInputPtsUs, System.nanoTime());
                            if (decision.render) source.releaseOutputBuffer(index, decision.renderNanos);
                            else source.releaseOutputBuffer(index, false);
                            publishPacingDiagnostics(false);
                        }
                    } catch (IllegalStateException problem) { fail(problem); }
                }
                @Override public void onOutputFormatChanged(MediaCodec source, MediaFormat format) {
                    if (closed || source != codec) return;
                    int width = format.getInteger(MediaFormat.KEY_WIDTH), height = format.getInteger(MediaFormat.KEY_HEIGHT);
                    if (format.containsKey("crop-right") && format.containsKey("crop-left"))
                        width = format.getInteger("crop-right") - format.getInteger("crop-left") + 1;
                    if (format.containsKey("crop-bottom") && format.containsKey("crop-top"))
                        height = format.getInteger("crop-bottom") - format.getInteger("crop-top") + 1;
                    outputWidth = width; outputHeight = height;
                    listener.onSizeChanged(width, height);
                }
                @Override public void onError(MediaCodec source, MediaCodec.CodecException error) { fail(error); }
            }, handler);
            codec.setOnFrameRenderedListener((source, ptsUs, nanoTime) -> {
                if (closed || source != codec || ptsUs < 0 || ptsUs == Long.MAX_VALUE || ptsUs <= lastRenderedPts) return;
                lastRenderedPts = ptsUs;
                renderCallbacks++;
                actualCallbackFps = rate.presented(nanoTime);
                listener.onPresented(ptsUs, nanoTime, outputWidth, outputHeight, actualCallbackFps, decoderName,
                        pending.droppedFrames() + renderClock.droppedFrames());
                publishPacingDiagnostics(false);
            }, handler);
            MediaFormat format = MediaFormat.createVideoFormat("video/avc", configuration.width, configuration.height);
            format.setByteBuffer("csd-0", ByteBuffer.wrap(configuration.sps));
            format.setByteBuffer("csd-1", ByteBuffer.wrap(configuration.pps));
            format.setFloat(MediaFormat.KEY_FRAME_RATE, configuration.fps);
            format.setFloat(MediaFormat.KEY_OPERATING_RATE, configuration.fps);
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE,
                    (int) Math.min(WireProtocol.MAX_PAYLOAD, (long) configuration.width * configuration.height * 3 / 2));
            if (Build.VERSION.SDK_INT >= 30 && selected.getCapabilitiesForType("video/avc")
                    .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency))
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            codec.configure(format, surface, null, 0);
            codec.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT);
            codec.start();
        } catch (IOException | RuntimeException problem) { fail(problem); }
    }

    private static boolean isHardware(MediaCodecInfo info) {
        if (Build.VERSION.SDK_INT >= 29) return info.isHardwareAccelerated() && !info.isSoftwareOnly();
        String name = info.getName().toLowerCase(Locale.ROOT);
        return !name.startsWith("omx.google.") && !name.startsWith("c2.android.") && !name.contains("software");
    }

    private void pumpInputs() {
        if (closed || codec == null) return;
        try {
            while (!inputSlots.isEmpty()) {
                VideoAccessUnit frame = pending.poll(System.nanoTime());
                if (frame == null) return;
                int index = inputSlots.remove();
                ByteBuffer target = codec.getInputBuffer(index);
                if (target == null || target.capacity() < frame.bytes.length)
                    throw new IOException("H.264 access unit exceeds decoder input capacity");
                target.clear(); target.put(frame.bytes);
                codec.queueInputBuffer(index, 0, frame.bytes.length, frame.ptsUs, 0);
                long submitted = System.nanoTime();
                submittedFrames++;
                listener.onSubmitted(frame.ptsUs, submitted, configuration.width, configuration.height, decoderName);
                long inputAge = Math.max(0, submitted - frame.receivedNanos);
                totalInputAgeNanos += inputAge;
                longestInputAgeNanos = Math.max(longestInputAgeNanos, inputAge);
                if (lastSubmitNanos != 0)
                    longestSubmitGapNanos = Math.max(longestSubmitGapNanos, submitted - lastSubmitNanos);
                lastSubmitNanos = submitted;
            }
        } catch (IOException | RuntimeException problem) { fail(problem); }
    }

    private void fail(Exception problem) {
        if (closed) return;
        listener.onError("H.264 解码中断：" + problem.getMessage());
        close();
    }

    private void publishPacingDiagnostics(boolean force) {
        long now = System.nanoTime();
        if (currentDiagnosticId != diagnosticId || (!force && now - lastDiagnosticNanos < 500_000_000L)) return;
        lastDiagnosticNanos = now;
        try {
            JSONObject json = new JSONObject();
            json.put("active", !closed);
            json.put("decoderId", diagnosticId);
            json.put("epoch", pacingEpoch);
            json.put("enabled", renderClock.enabled());
            json.put("configuredFps", configuration.fps);
            json.put("leadMs", renderClock.leadNanos() / 1_000_000.0);
            json.put("minimumLeadMs", renderClock.minimumLeadNanos() / 1_000_000.0);
            json.put("maxAheadMs", RenderClock.MAX_AHEAD_NANOS / 1_000_000.0);
            json.put("outputFrames", renderClock.outputFrames());
            json.put("scheduledFrames", renderClock.scheduledFrames());
            json.put("immediateFrames", renderClock.immediateFrames());
            json.put("lateDrops", renderClock.lateDrops());
            json.put("crowdedDrops", renderClock.crowdedDrops());
            json.put("outOfOrderDrops", renderClock.orderDrops());
            json.put("reanchors", renderClock.reanchors());
            json.put("aheadClamps", renderClock.aheadClamps());
            json.put("leadFloorReanchors", renderClock.leadFloorReanchors());
            json.put("insufficientLeadFrames", renderClock.insufficientLeadFrames());
            json.put("lastAheadMs", renderClock.lastAheadNanos() / 1_000_000.0);
            json.put("maximumAheadMs", renderClock.maximumAheadNanos() / 1_000_000.0);
            json.put("minimumObservedAheadMs", renderClock.minimumObservedAheadNanos() / 1_000_000.0);
            json.put("inputDroppedFrames", pending.droppedFrames());
            VideoFrameQueue.Snapshot queue = pending.snapshot();
            json.put("receivedFrames", receivedFrames);
            json.put("submittedFrames", submittedFrames);
            json.put("longestArrivalGapMs", longestArrivalGapNanos / 1_000_000.0);
            json.put("longestSubmitGapMs", longestSubmitGapNanos / 1_000_000.0);
            json.put("averageInputQueueAgeMs", submittedFrames == 0 ? 0 : totalInputAgeNanos / 1_000_000.0 / submittedFrames);
            json.put("longestInputQueueAgeMs", longestInputAgeNanos / 1_000_000.0);
            json.put("inputQueueSize", queue.size);
            json.put("inputQueueHighWaterMark", queue.highWaterMark);
            json.put("inputOverflowEvents", queue.overflowEvents);
            json.put("inputOverflowDrops", queue.overflowDrops);
            json.put("inputAwaitingKeyFrameDrops", queue.awaitingKeyFrameDrops);
            json.put("inputExpiredDrops", queue.expiredDrops);
            json.put("inputReorderedDrops", queue.reorderedDrops);
            json.put("inputBackpressureBudgetMs", inputBackpressureNanos / 1_000_000.0);
            json.put("inputBackpressureWaits", queue.backpressureWaits);
            json.put("inputBackpressureTimeouts", queue.backpressureTimeouts);
            json.put("inputBackpressureWaitMs", queue.backpressureWaitNanos / 1_000_000.0);
            json.put("longestInputBackpressureWaitMs", queue.longestBackpressureWaitNanos / 1_000_000.0);
            json.put("decoderRenderCallbacks", renderCallbacks);
            json.put("actualCallbackFps", actualCallbackFps);
            json.put("decoder", decoderName);
            json.put("updatedMonotonicNs", now);
            if (currentDiagnosticId == diagnosticId) pacingDiagnostics = json.toString();
        } catch (JSONException ignored) { }
    }

    @Override public void close() {
        if (closed) return;
        closed = true;
        pending.close();
        handler.post(() -> {
            MediaCodec previous = codec;
            codec = null;
            inputSlots.clear();
            if (previous != null) {
                try { previous.stop(); } catch (RuntimeException ignored) { }
                try { previous.release(); } catch (RuntimeException ignored) { }
            }
            publishPacingDiagnostics(true);
            released.countDown();
            thread.quitSafely();
        });
    }

    public boolean awaitReleased(long timeoutMillis) throws InterruptedException {
        return released.await(timeoutMillis, TimeUnit.MILLISECONDS);
    }
}
