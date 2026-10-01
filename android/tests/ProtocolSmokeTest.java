package com.tablink.client;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.nio.ByteBuffer;

/** Runs on the build JDK without an Android device or any external test library. */
public final class ProtocolSmokeTest {
    private static int assertions;

    public static void main(String[] args) throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        byte[] utf8 = "{\"message\":\"连接成功\"}".getBytes(StandardCharsets.UTF_8);
        WireProtocol.write(new DataOutputStream(bytes), WireProtocol.STATUS, utf8);
        byte[] encoded = bytes.toByteArray();
        check(encoded[0] == 2 && encoded[1] == 0 && encoded[2] == 0 && encoded[3] == 0
                && (encoded[4] & 255) == utf8.length, "big-endian header");
        WireProtocol.Packet packet = WireProtocol.read(input(encoded));
        check(packet.type == WireProtocol.STATUS && Arrays.equals(packet.payload, utf8), "UTF-8 packet round trip");

        // TCP may split payloads arbitrarily, so readFully must assemble them.
        DataInputStream fragmented = new DataInputStream(new ByteArrayInputStream(encoded) {
            @Override public synchronized int read(byte[] target, int offset, int length) {
                return super.read(target, offset, Math.min(2, length));
            }
        });
        check(Arrays.equals(WireProtocol.read(fragmented).payload, utf8), "fragmented TCP payload");
        expectFailure(new byte[] {1, 0, (byte) 0x80, 0, 1}, "greater than 8 MiB rejected");
        expectFailure(new byte[] {1, (byte) 255, (byte) 255, (byte) 255, (byte) 255}, "unsigned length rejected");
        expectFailure(new byte[] {1, 0, 0, 0, 4, 1, 2}, "truncated payload rejected");
        expectFailure(new byte[] {1, 0}, "truncated header rejected");
        check(WireProtocol.read(input(new byte[] {2, 0, 0, 0, 0})).payload.length == 0, "empty payload framed safely");

        FrameGeometry letterbox = FrameGeometry.fit(1920, 1080, 1200, 800);
        close(letterbox.left, 0, "letterbox x offset");
        close(letterbox.top, 62.5f, "letterbox y offset");
        check(!letterbox.contains(500, 30), "top black bar excluded");
        check(!letterbox.contains(500, 780), "bottom black bar excluded");
        check(letterbox.contains(600, 400), "visible image accepts touch");
        close(letterbox.normalizedX(600), 0.5f, "center x mapping");
        close(letterbox.normalizedY(400), 0.5f, "center y mapping");
        FrameGeometry pillarbox = FrameGeometry.fit(1080, 1920, 1200, 800);
        close(pillarbox.left, 375, "pillarbox x offset");
        check(!pillarbox.contains(200, 400), "side black bar excluded");
        close(pillarbox.normalizedX(825), 1, "right image edge mapping");
        close(pillarbox.normalizedY(0), 0, "top image edge mapping");
        check(!FrameGeometry.fit(0, 1080, 1200, 800).contains(0, 0), "missing frame rejects touch");
        check(!letterbox.contains(Float.NaN, 100), "NaN excluded");
        check(!letterbox.contains(Float.POSITIVE_INFINITY, 100), "infinity excluded");

        PresentationProgress progress = new PresentationProgress();
        PresentationProgress.Report first = progress.presented(1, 1280, 800, 100);
        check(first != null && first.sequence == 1 && first.width == 1280 && first.height == 800,
                "first actual presentation immediately reported");
        check(progress.presented(1, 1280, 800, 2000) == null, "stalled image redraw does not report liveness");
        check(progress.presented(2, 1280, 800, 300) == null, "presentation acknowledgements rate limited");
        PresentationProgress.Report later = progress.presented(3, 1600, 900, 1100);
        check(later != null && later.sequence == 3 && later.width == 1600 && later.height == 900,
                "report includes all successfully presented new frames and actual dimensions");
        check(progress.presented(2, 1280, 800, 2200) == null, "older retained image cannot count twice");
        check(progress.presented(4, 0, 800, 2200) == null, "invalid presentation cannot count");
        PresentationProgress.Report skipped = progress.presented(6, 1600, 900, 2300);
        check(skipped != null && skipped.sequence == 4, "frames never presented do not count");
        check(new PresentationProgress().presented(1, 1280, 800, 2500).sequence == 1,
                "new TCP connection starts a fresh sequence");

        SubmissionProgress submissions = new SubmissionProgress();
        SubmissionProgress.Report firstSubmission = submissions.submitted(0, 1200, 1920, 1, 100, "decoder-a");
        check(firstSubmission != null && firstSubmission.frames == 1 && firstSubmission.ptsUs == 0
                        && firstSubmission.width == 1200 && firstSubmission.height == 1920
                        && firstSubmission.fps == 0 && firstSubmission.decoder.equals("decoder-a"),
                "first decoder submission immediately reports cumulative TCP progress");
        check(submissions.submitted(1, 1200, 1920, 500_000_001L, 1099, "decoder-a") == null,
                "decoder submission acknowledgements are limited to one per second");
        SubmissionProgress.Report laterSubmission = submissions.submitted(2, 1200, 1920,
                1_000_000_001L, 1100, "decoder-a");
        check(laterSubmission != null && laterSubmission.frames == 3 && laterSubmission.ptsUs == 2
                        && Math.abs(laterSubmission.fps - 2) < 0.001,
                "rate-limited report retains every successful decoder submission");
        check(submissions.submitted(3, 0, 1920, 1_100_000_001L, 2200, "decoder-a") == null,
                "invalid decoder submission does not advance progress");
        check(submissions.submitted(4, 1200, 1920, 900_000_001L, 2200, "decoder-a") == null,
                "stale decoder callback does not advance progress");
        SubmissionProgress.Report reconfigured = submissions.submitted(0, 1200, 1920,
                2_100_000_001L, 2200, "decoder-b");
        check(reconfigured != null && reconfigured.frames == 4 && reconfigured.ptsUs == 0
                        && reconfigured.decoder.equals("decoder-b"),
                "decoder reconfiguration may reset PTS without resetting TCP submission count");
        String longDecoder = "x".repeat(200);
        SubmissionProgress.Report boundedDecoder = submissions.submitted(1, 1200, 1920,
                3_200_000_001L, 3300, longDecoder);
        check(boundedDecoder != null && boundedDecoder.frames == 5 && boundedDecoder.decoder.length() == 160,
                "submission decoder metadata is bounded");
        check(new SubmissionProgress().submitted(0, 1200, 1920, 1, 1, "decoder").frames == 1,
                "new TCP connection starts fresh decoder submission progress");
        check(new SubmissionProgress().submitted(0, 1200, 1920, -5, 1, "decoder").frames == 1,
                "monotonic submission time may use an arbitrary nanoTime origin");
        ByteArrayOutputStream submissionBytes = new ByteArrayOutputStream();
        check(WireProtocol.RENDER_SUBMITTED == 0x14, "render-submitted packet keeps its protocol type");
        WireProtocol.write(new DataOutputStream(submissionBytes), WireProtocol.RENDER_SUBMITTED,
                "{\"evidence\":\"render-submitted\"}".getBytes(StandardCharsets.UTF_8));
        check(WireProtocol.read(input(submissionBytes.toByteArray())).type == WireProtocol.RENDER_SUBMITTED,
                "render-submitted packet type round trips");

        VideoAccessUnit idr = video(1, true, 0);
        check(idr.ptsUs == 1 && idr.keyFrame, "video PTS and Annex-B IDR parsed");
        check(!video(2, false, 0).keyFrame, "inter frame is not IDR");
        try { VideoAccessUnit.parse(new byte[12], 0); throw new AssertionError("short video header"); }
        catch (IOException expected) { assertions++; }
        byte[] negativePts = ByteBuffer.allocate(14).putLong(-1).put(new byte[] {0, 0, 0, 1, 0x65, 1}).array();
        try { VideoAccessUnit.parse(negativePts, 0); throw new AssertionError("negative video PTS"); }
        catch (IOException expected) { assertions++; }
        VideoFrameQueue queue = new VideoFrameQueue(2);
        check(!queue.offer(video(1, false, 0)), "initial stream waits for IDR");
        check(queue.offer(video(2, true, 0)) && queue.offer(video(3, false, 0)), "bounded queue accepts valid dependency chain");
        check(!queue.offer(video(4, false, 0)) && queue.size() == 0, "overflow discards chain instead of growing latency");
        check(!queue.offer(video(5, false, 0)), "overflow waits for another IDR");
        check(queue.offer(video(6, true, 0)), "fresh IDR resumes stream");
        check(queue.poll(200_000_000) == null, "stale input is never rendered late");
        check(!queue.offer(video(7, false, 200_000_000)), "stale chain waits for IDR");
        check(queue.offer(video(8, true, 200_000_000)) && queue.poll(210_000_000).ptsUs == 8, "fresh chain resumes after stale drop");
        check(!queue.offer(video(8, true, 210_000_000)), "repeated presentation timestamp rejected");
        FrameRateMeter meter = new FrameRateMeter();
        check(meter.presented(0) == 0, "target frame rate never substituted for measurement");
        for (int i = 1; i <= 60; i++) meter.presented(Math.round(i * 1_000_000_000.0 / 60));
        check(Math.abs(meter.presented(1_000_000_000) - 60) < 0.01, "actual callback timestamps measure 60 fps");
        check(StreamingBrightnessPolicy.mayApply(true, 1, 255, 60, 90, -1), "active low brightness high refresh stream may request window floor");
        check(!StreamingBrightnessPolicy.mayApply(false, 1, 255, 60, 90, -1), "idle app never raises brightness");
        check(!StreamingBrightnessPolicy.mayApply(true, 17, 255, 60, 90, -1), "higher system brightness is preserved");
        check(!StreamingBrightnessPolicy.mayApply(true, 1, 4095, 60, 90, -1), "unknown OEM brightness scale fails closed");
        check(!StreamingBrightnessPolicy.mayApply(true, -1, 255, 60, 90, -1), "missing brightness setting fails closed");
        check(!StreamingBrightnessPolicy.mayApply(true, 1, 255, 90, 90, -1), "already active target rate does not need brightness override");
        check(!StreamingBrightnessPolicy.mayApply(true, 1, 255, 60, 60, -1), "60 Hz native panel is not overridden");
        check(!StreamingBrightnessPolicy.mayApply(true, 1, 255, 60, 90, 0.6f), "higher existing window brightness is never lowered");
        check(StreamingBrightnessPolicy.keepWhileStreaming(true, 1, 255, 90), "successful mode switch keeps floor until stream ends to avoid oscillation");
        check(!StreamingBrightnessPolicy.keepWhileStreaming(false, 1, 255, 90), "ending stream restores original window brightness");
        System.out.println("PASS: " + assertions + " protocol and aspect-fit assertions");
    }

    private static DataInputStream input(byte[] bytes) { return new DataInputStream(new ByteArrayInputStream(bytes)); }
    private static VideoAccessUnit video(long pts, boolean key, long received) throws IOException {
        byte[] payload = ByteBuffer.allocate(14).putLong(pts)
                .put(new byte[] {0, 0, 0, 1, (byte) (key ? 0x65 : 0x41), 1}).array();
        return VideoAccessUnit.parse(payload, received);
    }
    private static void expectFailure(byte[] bytes, String message) throws Exception {
        try { WireProtocol.read(input(bytes)); throw new AssertionError(message); }
        catch (IOException expected) { assertions++; }
    }
    private static void close(float actual, float expected, String message) {
        check(Math.abs(actual - expected) < 0.001f, message + ": " + actual);
    }
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        assertions++;
    }
}
