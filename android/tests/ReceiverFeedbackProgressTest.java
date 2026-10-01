package com.tablink.client;

public final class ReceiverFeedbackProgressTest {
    private static int assertions;

    public static void main(String[] args) {
        ReceiverFeedbackProgress selectedOnly = new ReceiverFeedbackProgress();
        selectedOnly.decoderSelected(false);
        check(selectedOnly.report(0, false) == null,
                "selected codec waits for a valid queue snapshot before reporting");

        ReceiverFeedbackProgress feedback = new ReceiverFeedbackProgress();
        check(feedback.report(0, false) == null,
                "no receiver feedback is emitted before a decoder exists");

        feedback.receivedVideo(1200);
        feedback.receivedVideo(800);
        feedback.submitted();
        feedback.presented();
        feedback.observeDecoder(metrics(10, 2, 6, 4, false, 7, 1, 6, 2, 3, 1, 5));
        check(feedback.report(0, false) == null,
                "decoder object without a successfully selected codec does not report epoch zero");
        feedback.decoderSelected(false);
        ReceiverFeedbackProgress.Report first = feedback.report(0, false);
        check(first != null && first.sequence == 1 && first.receivedVideoFrames == 2
                        && first.receivedVideoBytes == 2000 && first.submittedFrames == 1
                        && first.presentedFrames == 1,
                "report contains connection-cumulative receive, submit and present counts");
        check(first.decoderEpoch == 1 && first.decoderFallbacks == 0 && first.queueDepth == 2
                        && first.queueCapacity == 6 && first.queueHighWaterMark == 4,
                "report includes decoder epoch and current bounded queue");
        check(first.inputDroppedFrames == 7 && first.overflowEvents == 1 && first.overflowDrops == 6
                        && first.expiredDrops == 2 && first.awaitingKeyFrameDrops == 3
                        && first.backpressureTimeouts == 1 && first.renderDrops == 5,
                "report includes cumulative classified drops");
        check(feedback.report(999, false) == null, "feedback is limited to once per second");

        feedback.observeDecoder(metrics(10, 0, 6, 5, true, 9, 2, 12, 2, 4, 2, 8));
        long recovery = feedback.recoveryStarted();
        ReceiverFeedbackProgress.Report waiting = feedback.report(1000, true);
        check(recovery == 1 && waiting.recoveryEpoch == 1 && waiting.awaitingKeyFrame,
                "logical recovery epoch and waiting state are explicit");
        check(waiting.inputDroppedFrames == 9 && waiting.overflowEvents == 2
                        && waiting.overflowDrops == 12 && waiting.awaitingKeyFrameDrops == 4
                        && waiting.backpressureTimeouts == 2 && waiting.renderDrops == 8,
                "same decoder contributes only counter deltas");

        feedback.observeDecoder(metrics(11, 1, 6, 2, false, 3, 1, 2, 1, 1, 0, 2));
        feedback.decoderSelected(true);
        ReceiverFeedbackProgress.Report replacement = feedback.report(2000, false);
        check(replacement.inputDroppedFrames == 12 && replacement.overflowEvents == 3
                        && replacement.renderDrops == 10,
                "new decoder counters add without making the TCP totals regress");
        check(replacement.decoderEpoch == 2 && replacement.decoderFallbacks == 1
                        && replacement.queueHighWaterMark == 5,
                "fallback advances connection-scoped decoder state and retains high water mark");

        feedback.observeDecoder(metrics(11, 0, 6, 1, false, 2, 0, 1, 0, 0, 0, 1));
        ReceiverFeedbackProgress.Report stalled = feedback.report(3000, false);
        check(stalled != null && stalled.sequence == 4 && stalled.receivedVideoFrames == 2
                        && stalled.inputDroppedFrames == 12 && stalled.renderDrops == 10,
                "feedback continues once per second while stalled without recounting old samples");
        String json = stalled.toJson();
        check(json.startsWith("{\"kind\":\"receiver-feedback\"")
                        && json.contains("\"sequence\":4")
                        && json.contains("\"awaitingKeyFrame\":false")
                        && json.length() < 4096,
                "feedback encoding is fixed, bounded JSON");
        feedback.decoderStopped();
        feedback.observeDecoder(metrics(12, 0, 6, 0, true, 0, 0, 0, 0, 0, 0, 0));
        feedback.observeDecoder(metrics(11, 0, 6, 6, false, 100, 100, 100, 100, 100, 100, 100));
        check(feedback.report(4000, false) == null,
                "dynamic decoder reconfiguration waits for successful codec selection");
        feedback.decoderSelected(false);
        ReceiverFeedbackProgress.Report restarted = feedback.report(4000, false);
        check(restarted != null && restarted.inputDroppedFrames == 12 && restarted.renderDrops == 10,
                "replacement decoder reports after selection and ignores late old-decoder metrics");
        try { feedback.receivedVideo(-1); throw new AssertionError("negative bytes accepted"); }
        catch (IllegalArgumentException expected) { assertions++; }
        System.out.println("ReceiverFeedbackProgress: " + assertions + " assertions passed");
    }

    private static ReceiverFeedbackProgress.DecoderMetrics metrics(long source, int depth, int capacity,
            int highWater, boolean waiting, long dropped, long overflows, long overflowDrops,
            long expired, long awaitingDrops, long timeouts, long renderDrops) {
        return new ReceiverFeedbackProgress.DecoderMetrics(source, depth, capacity, highWater, waiting,
                -1, dropped, overflows, overflowDrops, expired, awaitingDrops, timeouts, renderDrops);
    }

    private static void check(boolean value, String message) {
        assertions++;
        if (!value) throw new AssertionError(message);
    }
}
