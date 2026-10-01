package com.tablink.client;

import java.nio.ByteBuffer;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/** Real producer/consumer waits exercise monitor release, wake-up, ordering and congestion bounds. */
public final class VideoFrameQueueTest {
    private static int assertions;
    private static final long WAIT = 25_000_000L;

    public static void main(String[] args) throws Exception {
        steady();
        for (int size = 7; size <= 12; size++) burst(size);
        sustainedCongestion();
        resetAndClose(false);
        resetAndClose(true);
        interruptedWait();
        reorderedAndExpired();
        duplicateDuringWait();
        recoveryEpochs();
        System.out.println("VideoFrameQueue: " + assertions + " assertions passed");
    }

    private static void steady() throws Exception {
        VideoFrameQueue queue = new VideoFrameQueue(6);
        long origin = 1_000_000_000L;
        for (int i = 0; i < 900; i++) {
            long pts = i * 1_000_000L / 90, now = origin + pts * 1000;
            check(queue.offerWithBackpressure(frame(pts, i == 0, now), WAIT), "steady 90 fps accepted");
            check(queue.poll(now).ptsUs == pts, "steady 90 fps ordering");
        }
        check(queue.droppedFrames() == 0 && queue.snapshot().backpressureWaits == 0,
                "steady consumer requires no waiting or dropped reference chain");
        check(queue.snapshot().highWaterMark == 1, "steady queue age and occupancy remain small");
    }

    private static void burst(int count) throws Exception {
        // Frame expiry is a separate behavior covered below. Use a synthetic
        // receive clock here so a cold or heavily loaded CI runner cannot turn
        // this backpressure/ordering test into a 150 ms wall-clock race.
        long frameClock = 1_000_000_000L;
        VideoFrameQueue queue = filled(frameClock);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        CountDownLatch[] accepted = new CountDownLatch[count - 6];
        for (int i = 0; i < accepted.length; i++) accepted[i] = new CountDownLatch(1);
        Thread producer = new Thread(() -> {
            try {
                for (int i = 7; i <= count; i++) {
                    if (!queue.offerWithBackpressure(frame(i, false, frameClock), WAIT))
                        throw new AssertionError("burst lost a frame while consumer made room");
                    accepted[i - 7].countDown();
                }
            } catch (Throwable problem) { failure.set(problem); }
        });
        producer.start();
        for (int i = 0; i < accepted.length; i++) {
            awaitWaiting(producer);
            VideoAccessUnit value = queue.poll(frameClock + 1);
            check(value != null && value.ptsUs == i + 1, "consumer may poll while producer waits");
            check(accepted[i].await(1, TimeUnit.SECONDS), "poll wakes producer");
        }
        producer.join(1000);
        check(!producer.isAlive() && failure.get() == null, "burst producer completes");
        for (int pts = count - 5; pts <= count; pts++)
            check(queue.poll(frameClock + 1).ptsUs == pts, "remaining burst frames preserve reference order");
        VideoFrameQueue.Snapshot state = queue.snapshot();
        check(queue.droppedFrames() == 0 && state.overflowEvents == 0, "7 to 12 frame bursts retain reference chain");
        check(state.highWaterMark == 6 && state.backpressureWaits >= 1, "burst remains at capacity six");
        check(state.backpressureTimeouts == 0, "available consumer avoids timeouts");
    }

    private static void sustainedCongestion() throws Exception {
        VideoFrameQueue queue = filled();
        long began = System.nanoTime();
        check(!queue.offerWithBackpressure(frame(7, false, began), Long.MAX_VALUE), "congestion discards dependent incoming frame");
        long elapsed = System.nanoTime() - began;
        check(elapsed >= 15_000_000L && elapsed < 500_000_000L, "arbitrary requested wait is capped near 25 ms, not unbounded");
        VideoFrameQueue.Snapshot state = queue.snapshot();
        check(state.backpressureTimeouts == 1 && state.overflowEvents == 1 && state.overflowDrops == 6,
                "timeout retains original whole-chain eviction policy");
        check(state.awaitingKeyFrameDrops == 1 && queue.droppedFrames() == 7 && queue.size() == 0,
                "drop counters classify each discarded input once");
        check(!queue.offer(frame(8, false, System.nanoTime())), "dependent frame waits for IDR after congestion");
        check(queue.offer(frame(9, true, System.nanoTime())), "fresh IDR recovers congested stream");
        check(queue.poll(System.nanoTime()).ptsUs == 9, "recovery starts only from fresh IDR");
    }

    private static void resetAndClose(boolean close) throws Exception {
        VideoFrameQueue queue = filled();
        AtomicReference<Boolean> accepted = new AtomicReference<>();
        AtomicReference<Throwable> failure = new AtomicReference<>();
        Thread producer = new Thread(() -> {
            try { accepted.set(queue.offerWithBackpressure(frame(7, false, System.nanoTime()), WAIT)); }
            catch (Throwable problem) { failure.set(problem); }
        });
        producer.start();
        awaitWaiting(producer);
        if (close) queue.close(); else queue.clear();
        producer.join(1000);
        check(!producer.isAlive() && failure.get() == null && Boolean.FALSE.equals(accepted.get()), "clear/close cancels and wakes waiting producer");
        check(queue.size() == 0 && queue.droppedFrames() == 0, "intentional stop does not report performance drops");
        check(queue.snapshot().backpressureTimeouts == 0, "stop wake-up does not use timeout fallback");
        check(queue.offer(frame(0, true, System.nanoTime())) != close, "reset accepts new PTS epoch; closed queue stays closed");
    }

    private static void interruptedWait() throws Exception {
        VideoFrameQueue queue = filled();
        AtomicReference<Throwable> result = new AtomicReference<>();
        Thread producer = new Thread(() -> {
            try { queue.offerWithBackpressure(frame(7, false, System.nanoTime()), WAIT); }
            catch (Throwable interrupted) { result.set(interrupted); }
        });
        producer.start();
        awaitWaiting(producer);
        producer.interrupt();
        producer.join(1000);
        check(result.get() instanceof InterruptedException, "interruption promptly exits producer wait");
        check(queue.size() == 6 && queue.droppedFrames() == 0, "interrupted offer does not alter reference chain");
        queue.poll(System.nanoTime());
        check(queue.offer(frame(7, false, System.nanoTime())), "interruption does not prematurely advance last PTS");
    }

    private static void reorderedAndExpired() throws Exception {
        VideoFrameQueue queue = new VideoFrameQueue(6);
        check(queue.offer(frame(10, true, 1)), "initial IDR accepted");
        check(!queue.offer(frame(10, true, 2)), "duplicate rejected");
        check(!queue.offer(frame(9, true, 3)), "out-of-order rejected");
        check(queue.offer(frame(11, false, 100_000_000L)), "later P frame queued");
        check(queue.poll(150_000_002L) == null, "expired IDR invalidates later dependent frame");
        VideoFrameQueue.Snapshot state = queue.snapshot();
        check(state.reorderedDrops == 2 && state.expiredDrops == 1 && state.awaitingKeyFrameDrops == 1,
                "reordered, expired and waiting-IDR reasons are distinct");
        check(queue.droppedFrames() == 4 && state.overflowEvents == 0, "reason sum equals total drops");
    }

    private static void duplicateDuringWait() throws Exception {
        VideoFrameQueue queue = filled();
        AtomicReference<Boolean> accepted = new AtomicReference<>();
        AtomicReference<Throwable> failure = new AtomicReference<>();
        Thread producer = new Thread(() -> {
            try { accepted.set(queue.offerWithBackpressure(frame(7, false, System.nanoTime()), WAIT)); }
            catch (Throwable problem) { failure.set(problem); }
        });
        producer.start();
        awaitWaiting(producer);
        synchronized (queue) {
            queue.poll(System.nanoTime());
            check(queue.offer(frame(7, false, System.nanoTime())), "another producer may win the PTS while first waits");
        }
        producer.join(1000);
        check(failure.get() == null && Boolean.FALSE.equals(accepted.get()), "waiter rechecks PTS ordering before commit");
        check(queue.snapshot().reorderedDrops == 1 && queue.snapshot().overflowEvents == 0,
                "duplicate wake-up does not evict full but valid reference chain");
    }

    private static void recoveryEpochs() throws Exception {
        VideoFrameQueue initial = new VideoFrameQueue(2);
        check(initial.snapshot().awaitingKeyFrame && initial.snapshot().recoveryEpoch == 0,
                "initial IDR wait is not a recovery request");
        check(!initial.offer(frame(1, false, System.nanoTime()))
                        && initial.snapshot().recoveryEpoch == 0,
                "initial dependent frames do not manufacture a recovery epoch");
        check(initial.offer(frame(2, true, System.nanoTime()))
                        && !initial.snapshot().awaitingKeyFrame,
                "initial IDR establishes a healthy dependency chain");
        check(initial.offer(frame(3, false, System.nanoTime())), "healthy dependent frame fills queue");
        check(!initial.offer(frame(4, false, System.nanoTime())), "overflowing P frame invalidates chain");
        VideoFrameQueue.Snapshot overflow = initial.snapshot();
        check(overflow.awaitingKeyFrame && overflow.recoveryEpoch == 1
                        && overflow.recoveryReason.equals("queue-overflow"),
                "overflow advances exactly one recovery epoch");
        check(!initial.offer(frame(5, false, System.nanoTime()))
                        && initial.snapshot().recoveryEpoch == 1,
                "additional P frames in the same broken chain are coalesced");
        check(initial.offer(frame(6, true, System.nanoTime()))
                        && !initial.snapshot().awaitingKeyFrame
                        && initial.snapshot().recoveryKeyFramePtsUs == 6,
                "fresh IDR closes the queue recovery epoch");
        long beforeClear = initial.snapshot().recoveryEpoch;
        initial.clear();
        check(initial.snapshot().recoveryEpoch == beforeClear,
                "intentional clear does not create a recovery epoch");

        VideoFrameQueue expired = new VideoFrameQueue(6);
        check(expired.offer(frame(10, true, 0)) && expired.offer(frame(11, false, 1)),
                "expired fixture starts with a valid chain");
        check(expired.poll(200_000_000L) == null, "expired chain produces no late frame");
        VideoFrameQueue.Snapshot stale = expired.snapshot();
        check(stale.awaitingKeyFrame && stale.recoveryEpoch == 1
                        && stale.recoveryReason.equals("expired-chain"),
                "expired healthy chain advances one recovery epoch");

        VideoFrameQueue alreadyRecovered = new VideoFrameQueue(6);
        check(alreadyRecovered.offer(frame(20, true, 0))
                        && alreadyRecovered.offer(frame(21, false, 1))
                        && alreadyRecovered.offer(frame(22, true, 190_000_000L)),
                "fresh IDR may already be queued behind stale frames");
        check(alreadyRecovered.poll(200_000_000L).ptsUs == 22
                        && alreadyRecovered.snapshot().recoveryEpoch == 0,
                "queued fresh IDR avoids an unnecessary recovery request");

        VideoFrameQueue idrOverflow = new VideoFrameQueue(2);
        check(idrOverflow.offer(frame(30, true, 0)) && idrOverflow.offer(frame(31, false, 1)),
                "IDR overflow fixture is full");
        check(idrOverflow.offer(frame(32, true, 2))
                        && idrOverflow.snapshot().recoveryEpoch == 0
                        && !idrOverflow.snapshot().awaitingKeyFrame,
                "overflowing IDR immediately rebuilds the chain without a request");
    }

    private static VideoFrameQueue filled() throws Exception {
        return filled(System.nanoTime());
    }
    private static VideoFrameQueue filled(long receivedNanos) throws Exception {
        VideoFrameQueue queue = new VideoFrameQueue(6);
        for (int i = 1; i <= 6; i++) queue.offer(frame(i, i == 1, receivedNanos));
        return queue;
    }
    private static VideoAccessUnit frame(long pts, boolean idr, long now) throws Exception {
        byte[] packet = new byte[14];
        ByteBuffer.wrap(packet).putLong(pts);
        packet[11] = 1; packet[12] = (byte) (idr ? 0x65 : 0x41); packet[13] = 1;
        return VideoAccessUnit.parse(packet, now);
    }
    private static void awaitWaiting(Thread producer) throws Exception {
        long deadline = System.nanoTime() + 1_000_000_000L;
        while (producer.isAlive() && producer.getState() != Thread.State.TIMED_WAITING && System.nanoTime() < deadline)
            Thread.yield();
        check(producer.getState() == Thread.State.TIMED_WAITING, "producer reaches monitor wait");
    }
    private static void check(boolean value, String message) {
        assertions++;
        if (!value) throw new AssertionError(message);
    }
}
