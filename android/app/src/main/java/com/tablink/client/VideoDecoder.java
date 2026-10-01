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
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** Ranked AVC decoder failover with bounded pending input and render-confirmed telemetry. */
public final class VideoDecoder implements AutoCloseable {
    private static final AtomicLong DIAGNOSTIC_IDS = new AtomicLong();
    private static final DecoderCandidateSelector.FailureHistory DECODER_FAILURES =
            new DecoderCandidateSelector.FailureHistory();
    private static volatile long currentDiagnosticId;
    private static volatile String pacingDiagnostics = "{\"active\":false,\"enabled\":false}";
    public interface Listener {
        void onSubmitted(long ptsUs, long submittedNanos, int width, int height, String decoder);
        void onPresented(long ptsUs, long renderNanos, int width, int height, double fps, String decoder, long dropped);
        void onSizeChanged(int width, int height);
        /** Called only after a codec has configured and started successfully, including the first choice. */
        default void onDecoderSelected(String selectedDecoder, String tier, boolean fallback,
                String failedDecoder, String reason) { }
        void onError(String message);
    }

    private static final class CodecCandidate {
        final MediaCodecInfo info;
        final DecoderCandidateSelector.Candidate score;

        CodecCandidate(MediaCodecInfo info, DecoderCandidateSelector.Candidate score) {
            this.info = info;
            this.score = score;
        }
    }

    private static final class CodecStartException extends IOException {
        final String stage;
        CodecStartException(String stage, Exception cause) {
            super("codec-" + stage, cause);
            this.stage = stage;
        }
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
    private FrameRateMeter rate = new FrameRateMeter();
    private volatile boolean closed;
    private boolean startScheduled;
    private MediaCodec codec;
    private Surface decoderSurface;
    private List<CodecCandidate> candidates = Collections.emptyList();
    private int nextCandidate;
    private CodecCandidate activeCandidate;
    private boolean fallbackScheduled;
    private long codecGeneration;
    private String decoderName = "";
    private String lastDecoderFailure = "";
    private long decoderFallbacks;
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
            decoderSurface = surface;
            candidates = discoverCandidates();
            startNextCandidate(null);
        } catch (IOException | RuntimeException problem) { fail(problem); }
    }

    private List<CodecCandidate> discoverCandidates() throws IOException {
        ArrayList<CodecCandidate> discovered = new ArrayList<>();
        int order = 0;
        for (MediaCodecInfo info : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
            if (info.isEncoder() || (Build.VERSION.SDK_INT >= 29 && info.isAlias())) continue;
            String avcType = null;
            for (String type : info.getSupportedTypes()) {
                if ("video/avc".equalsIgnoreCase(type)) { avcType = type; break; }
            }
            if (avcType == null) continue;
            try {
                MediaCodecInfo.CodecCapabilities capabilities = info.getCapabilitiesForType(avcType);
                MediaCodecInfo.VideoCapabilities video = capabilities.getVideoCapabilities();
                boolean sizeSupported = video != null && video.isSizeSupported(
                        configuration.width, configuration.height);
                boolean frameRateSupported = sizeSupported && video.areSizeAndRateSupported(
                        configuration.width, configuration.height, configuration.fps);
                boolean pointsDeclared = false, pointCovers = false;
                if (sizeSupported && Build.VERSION.SDK_INT >= 29) {
                    List<MediaCodecInfo.VideoCapabilities.PerformancePoint> points =
                            video.getSupportedPerformancePoints();
                    pointsDeclared = points != null && !points.isEmpty();
                    if (pointsDeclared) {
                        MediaCodecInfo.VideoCapabilities.PerformancePoint requested =
                                new MediaCodecInfo.VideoCapabilities.PerformancePoint(
                                        configuration.width, configuration.height,
                                        Math.max(1, (int) Math.ceil(configuration.fps)));
                        for (MediaCodecInfo.VideoCapabilities.PerformancePoint point : points) {
                            if (point != null && point.covers(requested)) { pointCovers = true; break; }
                        }
                    }
                }
                boolean lowLatency = Build.VERSION.SDK_INT >= 30 && capabilities.isFeatureSupported(
                        MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency);
                DecoderCandidateSelector.Candidate score = new DecoderCandidateSelector.Candidate(
                        diagnosticDecoderName(info.getName()), isHardware(info), isSoftware(info), sizeSupported,
                        frameRateSupported, pointsDeclared, pointCovers, lowLatency,
                        DECODER_FAILURES.count(info.getName()), order++);
                discovered.add(new CodecCandidate(info, score));
            } catch (IllegalArgumentException | IllegalStateException ignored) {
                // A malformed capability record is not allowed to hide other decoders.
            }
        }
        ArrayList<DecoderCandidateSelector.Candidate> scores = new ArrayList<>();
        for (CodecCandidate candidate : discovered) scores.add(candidate.score);
        List<DecoderCandidateSelector.Candidate> ranked = DecoderCandidateSelector.rank(scores);
        ArrayList<CodecCandidate> result = new ArrayList<>();
        for (DecoderCandidateSelector.Candidate score : ranked) {
            for (CodecCandidate candidate : discovered) {
                if (candidate.score == score) { result.add(candidate); break; }
            }
        }
        if (result.isEmpty()) throw new IOException("No H.264 decoder supports this resolution");
        return Collections.unmodifiableList(result);
    }

    private void startNextCandidate(Exception trigger) {
        if (closed) return;
        fallbackScheduled = false;
        String failedDecoder = "";
        StringBuilder failures = new StringBuilder();
        boolean fallback = trigger != null;
        if (trigger != null) {
            failedDecoder = decoderName;
            appendFailure(failures, failedDecoder, failureStage(trigger));
            if (activeCandidate != null) DECODER_FAILURES.record(activeCandidate.info.getName());
            prepareFallback();
            releaseCodec();
        }
        if (decoderSurface == null || !decoderSurface.isValid()) {
            fail(new IOException("surface-unavailable"));
            return;
        }
        while (!closed && nextCandidate < candidates.size()) {
            CodecCandidate candidate = candidates.get(nextCandidate++);
            try {
                startCodec(candidate);
            } catch (IOException | RuntimeException problem) {
                DECODER_FAILURES.record(candidate.info.getName());
                if (failedDecoder.isEmpty()) failedDecoder = candidate.score.name;
                appendFailure(failures, candidate.score.name, failureStage(problem));
                if (!fallback) { fallback = true; prepareFallback(); }
                releaseCodec();
                continue;
            }
            if (closed) {
                releaseCodec();
                return;
            }
            if (fallback) decoderFallbacks++;
            lastDecoderFailure = fallback ? failures.toString() : "";
            try {
                listener.onDecoderSelected(decoderName, decoderTier(activeCandidate), fallback, failedDecoder,
                        fallback ? lastDecoderFailure : "initial");
            } catch (RuntimeException ignored) { }
            publishPacingDiagnostics(true);
            return;
        }
        String detail = failures.length() == 0 ? "no eligible candidates" : failures.toString();
        lastDecoderFailure = detail;
        fail(new IOException("All H.264 decoders failed: " + detail));
    }

    private void startCodec(CodecCandidate candidate) throws IOException {
        Surface surface = decoderSurface;
        if (surface == null || !surface.isValid()) throw new IOException("Video surface is unavailable");
        MediaCodec created;
        try { created = MediaCodec.createByCodecName(candidate.info.getName()); }
        catch (IOException | RuntimeException problem) { throw new CodecStartException("create", problem); }
        codec = created;
        final long generation = ++codecGeneration;
        try { created.setCallback(new MediaCodec.Callback() {
            @Override public void onInputBufferAvailable(MediaCodec source, int index) {
                if (closed || fallbackScheduled || source != codec || generation != codecGeneration) return;
                inputSlots.add(index);
                pumpInputs();
            }
            @Override public void onOutputBufferAvailable(MediaCodec source, int index, MediaCodec.BufferInfo info) {
                if (closed || fallbackScheduled || source != codec || generation != codecGeneration) return;
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
                } catch (IllegalStateException problem) { scheduleFallback(source, generation, problem); }
            }
            @Override public void onOutputFormatChanged(MediaCodec source, MediaFormat format) {
                if (closed || fallbackScheduled || source != codec || generation != codecGeneration) return;
                try {
                    int width = format.getInteger(MediaFormat.KEY_WIDTH);
                    int height = format.getInteger(MediaFormat.KEY_HEIGHT);
                    if (format.containsKey("crop-right") && format.containsKey("crop-left"))
                        width = format.getInteger("crop-right") - format.getInteger("crop-left") + 1;
                    if (format.containsKey("crop-bottom") && format.containsKey("crop-top"))
                        height = format.getInteger("crop-bottom") - format.getInteger("crop-top") + 1;
                    outputWidth = width; outputHeight = height;
                    try { listener.onSizeChanged(width, height); }
                    catch (RuntimeException ignored) { }
                } catch (RuntimeException problem) { scheduleFallback(source, generation, problem); }
            }
            @Override public void onError(MediaCodec source, MediaCodec.CodecException error) {
                scheduleFallback(source, generation, error);
            }
        }, handler); }
        catch (RuntimeException problem) { throw new CodecStartException("callback", problem); }
        try { created.setOnFrameRenderedListener((source, ptsUs, nanoTime) -> {
            if (closed || fallbackScheduled || source != codec || generation != codecGeneration
                    || ptsUs < 0 || ptsUs == Long.MAX_VALUE || ptsUs <= lastRenderedPts) return;
            lastRenderedPts = ptsUs;
            renderCallbacks++;
            actualCallbackFps = rate.presented(nanoTime);
            try {
                listener.onPresented(ptsUs, nanoTime, outputWidth, outputHeight, actualCallbackFps, decoderName,
                        pending.droppedFrames() + renderClock.droppedFrames());
            } catch (RuntimeException ignored) { }
            publishPacingDiagnostics(false);
        }, handler); }
        catch (RuntimeException problem) { throw new CodecStartException("callback", problem); }
        MediaFormat format = MediaFormat.createVideoFormat("video/avc", configuration.width, configuration.height);
        format.setByteBuffer("csd-0", ByteBuffer.wrap(configuration.sps));
        format.setByteBuffer("csd-1", ByteBuffer.wrap(configuration.pps));
        format.setFloat(MediaFormat.KEY_FRAME_RATE, configuration.fps);
        format.setFloat(MediaFormat.KEY_OPERATING_RATE, configuration.fps);
        format.setInteger(MediaFormat.KEY_PRIORITY, 0);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE,
                (int) Math.min(WireProtocol.MAX_PAYLOAD, (long) configuration.width * configuration.height * 3 / 2));
        if (candidate.score.lowLatency) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
        try {
            created.configure(format, surface, null, 0);
            created.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT);
        } catch (RuntimeException problem) { throw new CodecStartException("configure", problem); }
        try { created.start(); }
        catch (RuntimeException problem) { throw new CodecStartException("start", problem); }
        activeCandidate = candidate;
        decoderName = candidate.score.name;
    }

    private void prepareFallback() {
        pending.clear();
        inputSlots.clear();
        decoderName = "";
        latestInputPtsUs = -1;
        lastRenderedPts = -1;
        outputWidth = configuration.width;
        outputHeight = configuration.height;
        actualCallbackFps = 0;
        rate = new FrameRateMeter();
        renderClock = new RenderClock(configuration.fps, renderClock.enabled());
        pacingEpoch++;
    }

    private void scheduleFallback(MediaCodec failedCodec, long generation, Exception problem) {
        if (closed || fallbackScheduled || failedCodec != codec || generation != codecGeneration) return;
        fallbackScheduled = true;
        handler.post(() -> {
            if (closed) return;
            if (failedCodec != codec || generation != codecGeneration) {
                fallbackScheduled = false;
                return;
            }
            startNextCandidate(problem);
        });
    }

    private void releaseCodec() {
        MediaCodec previous = codec;
        codec = null;
        codecGeneration++;
        fallbackScheduled = false;
        activeCandidate = null;
        inputSlots.clear();
        if (previous == null) return;
        try { previous.stop(); } catch (RuntimeException ignored) { }
        try { previous.release(); } catch (RuntimeException ignored) { }
    }

    private static String failureStage(Exception problem) {
        return problem instanceof CodecStartException ? ((CodecStartException) problem).stage : "runtime";
    }

    private static void appendFailure(StringBuilder target, String decoder, String stage) {
        if (target.length() > 0) target.append("; ");
        target.append(decoder == null || decoder.isEmpty() ? "decoder" : decoder)
                .append(':').append(stage);
        if (target.length() > 480) target.setLength(480);
    }

    private static String diagnosticDecoderName(String value) {
        if (value == null || value.isEmpty()) return "unknown";
        StringBuilder safe = new StringBuilder(Math.min(value.length(), 160));
        for (int index = 0; index < value.length() && safe.length() < 160; index++) {
            char current = value.charAt(index);
            safe.append(current >= 0x20 && current <= 0x7e ? current : '?');
        }
        return safe.toString();
    }

    private static String decoderTier(CodecCandidate candidate) {
        if (candidate == null) return "unknown";
        if (candidate.score.hardwareAccelerated && !candidate.score.softwareOnly) return "hardware";
        return candidate.score.softwareOnly ? "software" : "unknown";
    }

    private static boolean isHardware(MediaCodecInfo info) {
        if (Build.VERSION.SDK_INT >= 29) return info.isHardwareAccelerated() && !info.isSoftwareOnly();
        String name = info.getName().toLowerCase(Locale.ROOT);
        return !name.startsWith("omx.google.") && !name.startsWith("c2.android.") && !name.contains("software");
    }

    private static boolean isSoftware(MediaCodecInfo info) {
        if (Build.VERSION.SDK_INT >= 29) return info.isSoftwareOnly();
        String name = info.getName().toLowerCase(Locale.ROOT);
        return name.startsWith("omx.google.") || name.startsWith("c2.android.")
                || name.contains("software") || name.contains(".sw.");
    }

    private void pumpInputs() {
        if (closed || fallbackScheduled || codec == null) return;
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
                try { listener.onSubmitted(frame.ptsUs, submitted, configuration.width, configuration.height, decoderName); }
                catch (RuntimeException ignored) { }
                long inputAge = Math.max(0, submitted - frame.receivedNanos);
                totalInputAgeNanos += inputAge;
                longestInputAgeNanos = Math.max(longestInputAgeNanos, inputAge);
                if (lastSubmitNanos != 0)
                    longestSubmitGapNanos = Math.max(longestSubmitGapNanos, submitted - lastSubmitNanos);
                lastSubmitNanos = submitted;
            }
        } catch (IOException | RuntimeException problem) {
            MediaCodec failed = codec;
            long generation = codecGeneration;
            if (failed != null) scheduleFallback(failed, generation, problem);
        }
    }

    private void fail(Exception problem) {
        if (closed) return;
        String reason = decoderSurface == null || !decoderSurface.isValid() ? "surface-unavailable"
                : candidates.isEmpty() ? "no-compatible-decoder"
                : nextCandidate >= candidates.size() ? "candidates-exhausted" : "initialization-failed";
        try { listener.onError("H.264 解码中断（" + reason + "）"); }
        catch (RuntimeException ignored) { }
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
            json.put("decoderCandidates", candidates.size());
            json.put("decoderFallbacks", decoderFallbacks);
            json.put("decoderFailure", lastDecoderFailure);
            if (activeCandidate != null) {
                json.put("decoderScore", activeCandidate.score.score());
                json.put("decoderKnownFailures", activeCandidate.score.knownFailures);
                json.put("decoderTier", activeCandidate.score.hardwareAccelerated ? "hardware"
                        : activeCandidate.score.softwareOnly ? "software" : "unknown");
                json.put("decoderLowLatency", activeCandidate.score.lowLatency);
                json.put("decoderFrameRateSupported", activeCandidate.score.frameRateSupported);
                json.put("decoderPerformancePointCovered", activeCandidate.score.performancePointCovers);
            }
            json.put("updatedMonotonicNs", now);
            if (currentDiagnosticId == diagnosticId) pacingDiagnostics = json.toString();
        } catch (JSONException ignored) { }
    }

    @Override public void close() {
        if (closed) return;
        closed = true;
        pending.close();
        handler.post(() -> {
            releaseCodec();
            publishPacingDiagnostics(true);
            released.countDown();
            thread.quitSafely();
        });
    }

    public boolean awaitReleased(long timeoutMillis) throws InterruptedException {
        return released.await(timeoutMillis, TimeUnit.MILLISECONDS);
    }
}
