package com.tablink.client;

public final class PendingDecoderRefreshTest {
    private static int assertions;

    public static void main(String[] args) {
        PendingDecoderRefresh pending = new PendingDecoderRefresh();
        pending.set(1);
        check(pending.claim(1) && pending.get() == 0,
                "enqueue claims the matching generation before publishing it");
        pending.defer(1);
        check(pending.get() == 1,
                "writer defer after claim cannot be cleared by enqueue completion");
        pending.set(2);
        pending.defer(1);
        check(pending.get() == 2, "late older writer cannot replace a newer recovery");
        pending.defer(3);
        check(pending.get() == 3, "latest deferred writer generation wins");
        check(!pending.claim(2) && pending.get() == 3,
                "stale enqueue claim cannot clear a newer generation");
        check(pending.claim(3) && pending.get() == 0,
                "resumed writer consumes the restored generation once");
        pending.set(4);
        check(pending.claim(4), "writer claims the generation before recovery completes");
        pending.cancelThrough(4);
        check(pending.isCancelled(4), "claimed generation is visibly cancelled to the writer");
        pending.defer(4);
        check(pending.get() == 0,
                "writer cannot resurrect a claimed generation after presentation recovery cancels it");
        pending.set(5);
        check(pending.get() == 5,
                "a newer recovery remains eligible after the prior generation was cancelled");
        check(!pending.isCancelled(5), "newer generation is outside the cancellation floor");
        pending.defer(0);
        check(pending.get() == 5, "invalid deferred generation is ignored");
        pending.reset();
        check(pending.get() == 0, "TCP reset removes stale pending recovery and cancellation floor");
        pending.set(1);
        check(pending.get() == 1, "new TCP connection accepts its first generation");
        pending.set(3);
        pending.set(2);
        check(pending.get() == 3, "late older producer cannot overwrite a newer pending generation");
        try { pending.set(0); throw new AssertionError("zero generation accepted"); }
        catch (IllegalArgumentException expected) { assertions++; }

        DecoderRecoveryState recovery = new DecoderRecoveryState();
        check(recovery.start(1, "decoder"), "first logical recovery becomes active");
        long firstWireGeneration = recovery.issue(1);
        DecoderRecoveryState.Snapshot oldPresentation = recovery.snapshot();
        check(recovery.start(2, "network"), "reader may begin a newer recovery");
        long secondWireGeneration = recovery.issue(2);
        check(firstWireGeneration == 1 && secondWireGeneration == 2,
                "wire generations remain ordered across logical recoveries");
        check(recovery.complete(oldPresentation) == null && recovery.isWaiting(),
                "old presentation callback cannot complete the newer recovery");
        DecoderRecoveryState.Snapshot currentPresentation = recovery.snapshot();
        check(currentPresentation != null && currentPresentation.epoch == 2
                        && "network".equals(currentPresentation.kind),
                "new recovery epoch and kind remain intact after stale completion");

        recovery.reset();
        pending.reset();
        check(recovery.start(1, "decoder"), "writer race starts with an active recovery");
        long claimedOld = recovery.issue(1);
        pending.set(claimedOld);
        check(pending.claim(claimedOld), "writer may claim the old packet before presentation");
        DecoderRecoveryState.Completion completed = recovery.complete(recovery.snapshot());
        check(completed != null && completed.wireGenerationCutoff == claimedOld,
                "completion captures only its own wire cutoff");
        check(recovery.start(2, "network"), "new recovery may start before old cancellation is applied");
        long newWire = recovery.issue(2);
        pending.set(newWire);
        pending.cancelThrough(completed.wireGenerationCutoff);
        check(pending.isCancelled(claimedOld) && !pending.isCancelled(newWire),
                "writer drops the claimed old packet without cancelling the new packet");
        pending.defer(claimedOld);
        check(pending.get() == newWire, "cancelled writer cannot replace the new pending generation");
        System.out.println("PendingDecoderRefresh: " + assertions + " assertions passed");
    }

    private static void check(boolean value, String message) {
        assertions++;
        if (!value) throw new AssertionError(message);
    }
}
