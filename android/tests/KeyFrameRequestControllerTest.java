package com.tablink.client;

public final class KeyFrameRequestControllerTest {
    private static int assertions;

    public static void main(String[] args) {
        KeyFrameRequestController controller = new KeyFrameRequestController();
        check(controller.request(1, 0, true) == 1, "first recovery uses first burst token");
        check(controller.request(1, 0, true) == 0, "same recovery epoch is idempotent");
        check(controller.request(2, 0, true) == 2, "second recovery uses bounded burst token");
        check(controller.request(3, 0, true) == 0 && controller.pendingEpoch() == 3,
                "third immediate recovery is coalesced while rate limited");
        check(controller.retry(999, true) == 0, "token does not refill early");
        check(controller.retry(1000, true) == 3 && controller.pendingEpoch() == 0,
                "one token refills after one second");

        check(controller.request(4, 1000, false) == 0 && controller.pendingEpoch() == 4,
                "paused or unnegotiated recovery does not send");
        check(controller.retry(2000, false) == 0, "ineligible retry remains silent");
        check(controller.retry(2000, true) == 4, "latest eligible recovery may send after state resumes");
        check(controller.request(3, 3000, true) == 0, "older epoch never reopens a request");

        check(controller.request(5, 3000, false) == 0, "disconnected request remains pending, not sent");
        controller.cancelPending();
        check(controller.retry(5000, true) == 0, "recovered stream cancels stale pending request");

        KeyFrameRequestController completionRace = new KeyFrameRequestController();
        check(completionRace.request(1, 0, false) == 0, "old recovery waits while ineligible");
        check(completionRace.request(2, 0, false) == 0 && completionRace.pendingEpoch() == 2,
                "new recovery replaces the old pending epoch");
        completionRace.cancelPendingThrough(1);
        check(completionRace.pendingEpoch() == 2,
                "old presentation completion cannot cancel a newer pending recovery");
        completionRace.cancelPendingThrough(2);
        check(completionRace.pendingEpoch() == 0, "matching completion cancels its pending recovery");

        KeyFrameRequestController reconnect = new KeyFrameRequestController();
        check(reconnect.request(1, 0, true) == 1, "new TCP connection owns a fresh limiter");
        try { reconnect.request(0, 0, true); throw new AssertionError("zero epoch accepted"); }
        catch (IllegalArgumentException expected) { assertions++; }
        try { reconnect.retry(-1, true); throw new AssertionError("negative clock accepted"); }
        catch (IllegalArgumentException expected) { assertions++; }
        System.out.println("KeyFrameRequestController: " + assertions + " assertions passed");
    }

    private static void check(boolean value, String message) {
        assertions++;
        if (!value) throw new AssertionError(message);
    }
}
