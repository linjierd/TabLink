package com.tablink.client;

/** Deterministic timing cases: no Android runtime, clocks, sleeping, or external libraries. */
public final class RenderClockTest {
    private static int assertions;
    private static final long START = 1_000_000_000L;

    public static void main(String[] args) {
        RenderClock disabled = new RenderClock(90, false);
        for (int i = 0; i < 100; i++) {
            long pts = i * 1_000_000L / 90, now = START + i * 15_625_000L;
            RenderClock.Decision decision = disabled.plan(pts, pts + 100_000, now);
            check(decision.render && decision.renderNanos == now, "disabled pacing is immediate despite backlog");
        }
        check(disabled.immediateFrames() == 100 && disabled.scheduledFrames() == 0, "A/B counters distinguish immediate output");

        RenderClock steady = new RenderClock(90, true);
        long previous = Long.MIN_VALUE;
        for (int i = 0; i < 900; i++) {
            long pts = i * 1_000_000L / 90, now = START + pts * 1000;
            RenderClock.Decision decision = steady.plan(pts, pts, now);
            check(decision.render && decision.renderNanos == now + steady.leadNanos(), "steady 90 fps preserves media cadence");
            bounded(decision, now);
            check(decision.renderNanos > previous, "scheduled timestamps increase");
            previous = decision.renderNanos;
        }
        check(steady.reanchors() == 0 && steady.droppedFrames() == 0, "steady stream requires no recovery");

        RenderClock jitter = new RenderClock(90, true);
        for (int i = 0; i < 900; i++) {
            long pts = i * 1_000_000L / 90;
            long jitterNs = i == 0 ? 0 : ((i % 5) - 2) * 1_000_000L;
            long now = START + pts * 1000 + jitterNs;
            RenderClock.Decision decision = jitter.plan(pts, pts, now);
            check(decision.render && decision.renderNanos == START + pts * 1000 + jitter.leadNanos(),
                    "two millisecond decoder jitter does not change the planned cadence");
            bounded(decision, now);
        }

        RenderClock slowSource = new RenderClock(90, true);
        for (int i = 0; i < 600; i++) {
            long pts = i * 1_000_000L / 90, now = START + i * 15_625_000L;
            RenderClock.Decision decision = slowSource.plan(pts, pts, now);
            check(decision.render, "64 fps input declared as 90 never permanently starves newest output");
            bounded(decision, now);
        }
        check(slowSource.reanchors() > 0, "slower source resets media anchor instead of growing delay");

        RenderClock drifting = new RenderClock(90, true);
        for (int i = 0; i < 9000; i++) {
            long pts = i * 1_000_000L / 90, now = START + pts * 1000 + i * 2000L;
            RenderClock.Decision decision = drifting.plan(pts, pts, now);
            bounded(decision, now);
        }
        check(drifting.leadFloorReanchors() >= 2 && drifting.leadFloorReanchors() < 10,
                "100 seconds of slow drift restores lead with hysteresis rather than resetting every frame");
        check(drifting.droppedFrames() == 0, "minor clock drift does not discard decoded frames");

        RenderClock startupDelay = new RenderClock(90, true);
        for (int i = 0; i < 1800; i++) {
            long pts = i * 1_000_000L / 90, delay = i < 100 ? 0 : 14_000_000L;
            long now = START + pts * 1000 + delay;
            RenderClock.Decision decision = startupDelay.plan(pts, pts, now);
            bounded(decision, now);
            if (i > 100) check(decision.renderNanos - now >= startupDelay.minimumLeadNanos(),
                    "sustained pipeline delay restores compositor lead by the second low sample");
        }
        check(startupDelay.leadFloorReanchors() == 1,
                "persistent startup pipeline delay changes restore lead once instead of remaining at eight milliseconds");
        check(startupDelay.droppedFrames() == 0, "startup delay step does not damage the decoded stream");

        RenderClock alternatingJitter = new RenderClock(90, true);
        long previousJitterTarget = Long.MIN_VALUE;
        for (int i = 0; i < 1800; i++) {
            long pts = i * 1_000_000L / 90;
            long now = START + pts * 1000 + (i == 0 ? 0 : i % 2 == 0 ? 5_000_000 : -5_000_000);
            RenderClock.Decision decision = alternatingJitter.plan(pts, pts, now);
            if (decision.render) {
                bounded(decision, now);
                check(decision.renderNanos > previousJitterTarget, "large alternating jitter never reverses scheduled time");
                if (i >= 2) check(Math.abs((decision.renderNanos - previousJitterTarget) - 1_000_000_000L / 90) <= 1100,
                        "alternating five millisecond arrival jitter preserves even media cadence after initial clamp");
                previousJitterTarget = decision.renderNanos;
            }
        }
        check(alternatingJitter.scheduledFrames() > 1750, "large jitter cannot permanently starve the output clock");
        check(alternatingJitter.leadFloorReanchors() == 0 && alternatingJitter.aheadClamps() == 1,
                "isolated low-lead samples do not create a floor/ceiling re-anchor feedback loop");
        check(alternatingJitter.insufficientLeadFrames() > 100, "diagnostics expose ignored isolated low-lead samples");

        RenderClock pipelineLag = new RenderClock(90, true);
        int laggedRenders = 0, consecutiveDrops = 0;
        for (int i = 0; i < 900; i++) {
            long pts = i * 1_000_000L / 90, now = START + pts * 1000;
            RenderClock.Decision decision = pipelineLag.plan(pts, pts + 22_222, now);
            if (decision.render) { laggedRenders++; consecutiveDrops = 0; bounded(decision, now); }
            else consecutiveDrops++;
            check(consecutiveDrops <= 2, "permanent two-frame codec pipeline lag never starves rendering");
        }
        check(laggedRenders >= 898, "pipeline freshness hint does not cap stable 90 fps decoded output");
        for (int i = 0; i < 8; i++) {
            long pts = (900 + i) * 1_000_000L / 90, now = START + pts * 1000 + 1_000_000_000L;
            RenderClock.Decision decision = pipelineLag.plan(pts, pts + 33_333, now);
            if (decision.render) { consecutiveDrops = 0; bounded(decision, now); }
            else consecutiveDrops++;
            check(consecutiveDrops <= 2, "recovery remains live with persistent hardware lag after a pause");
        }

        RenderClock burst = new RenderClock(90, true);
        RenderClock.Decision first = burst.plan(0, 0, START);
        RenderClock.Decision second = burst.plan(11_111, 11_111, START);
        RenderClock.Decision third = burst.plan(22_222, 22_222, START);
        bounded(first, START); bounded(second, START);
        check(!third.render && burst.crowdedDrops() == 1, "a burst cannot enqueue identical or unbounded future deadlines");
        RenderClock.Decision resumed = burst.plan(33_333, 33_333, START + 11_111_000);
        check(resumed.render && resumed.renderNanos > second.renderNanos, "burst recovers without a long queued tail");
        bounded(resumed, START + 11_111_000);

        RenderClock stale = new RenderClock(90, true);
        stale.plan(0, 0, START);
        check(!stale.plan(11_111, 77_777, START + 80_000_000).render, "late decoded output with newer input is not shown");
        check(stale.plan(77_777, 77_777, START + 80_000_000).render, "current decoded output can recover after stale drop");
        check(!stale.plan(88_888, 555_555, START + 500_000_000).render, "old output after a long pause is dropped");
        RenderClock.Decision afterPause = stale.plan(555_555, 555_555, START + 500_000_000);
        check(afterPause.render, "input head reanchors after a long pause");
        bounded(afterPause, START + 500_000_000);
        check(!stale.plan(555_555, 555_555, START + 501_000_000).render, "duplicate output is not scheduled twice");
        check(!stale.plan(444_444, 555_555, START + 502_000_000).render, "out-of-order output is rejected");

        RenderClock freshSession = new RenderClock(90, true);
        check(freshSession.plan(0, 0, START + 800_000_000).render, "new decoder/connection accepts PTS zero again");
        check(freshSession.outputFrames() == 1 && freshSession.droppedFrames() == 0, "new epoch has fresh diagnostic counters");
        RenderClock jump = new RenderClock(90, true);
        jump.plan(0, 0, START);
        RenderClock.Decision hugePts = jump.plan(Long.MAX_VALUE - 1, Long.MAX_VALUE - 1, START + 11_111_111);
        check(hugePts.render, "huge media timestamp jump reanchors without multiplying an overflowing delta");
        bounded(hugePts, START + 11_111_111);
        RenderClock negativeOrigin = new RenderClock(90, true);
        bounded(negativeOrigin.plan(0, 0, -START), -START);
        check(negativeOrigin.plan(11_111, 11_111, -START - 100_000).render, "monotonic-origin discontinuity resets future bookkeeping");

        for (float fps : new float[] {1, 24, 60, 90, 120, 240}) {
            RenderClock varied = new RenderClock(fps, true);
            for (int i = 0; i < 80; i++) {
                long pts = (long) (i * 1_000_000.0 / fps), now = START + pts * 1000;
                bounded(varied.plan(pts, pts, now), now);
            }
            check(varied.maximumAheadNanos() <= RenderClock.MAX_AHEAD_NANOS, "lead capped for every supported frame rate");
        }
        System.out.println("PASS: " + assertions + " bounded render-clock assertions");
    }

    private static void bounded(RenderClock.Decision decision, long now) {
        check(decision.render && decision.renderNanos >= now
                        && decision.renderNanos - now <= RenderClock.MAX_AHEAD_NANOS,
                "render deadline remains within the 25 ms local scheduling budget");
    }
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        assertions++;
    }
}
