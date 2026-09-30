package com.tablink.client;

/** Essential persisted HUD boundaries and backwards-compatible pause state transitions. */
public final class DisplayUiPolicyTest {
    private static int assertions;
    public static void main(String[] args) {
        HudStyle defaults = new HudStyle(0, HudStyle.DEFAULT_COLOR, HudStyle.DEFAULT_TRANSPARENCY);
        check(defaults.color == 0xffffffff && Math.abs(defaults.opacity() - 0.7f) < 0.0001f,
                "30 percent transparent means white text at 70 percent opacity");
        check(new HudStyle(-1, 0, 30).position == 0 && new HudStyle(9, 0, 30).position == 0,
                "invalid persisted grid positions fall back safely");
        for (int i = 0; i < 9; i++) check(new HudStyle(i, 0, 30).position == i, "nine positions retained");
        check(new HudStyle(0, 0, -10).opacity() == 1 && new HudStyle(0, 0, 110).opacity() == 0,
                "persisted transparency is bounded");
        check(HudStyle.parseColor("#67e8f9") == 0xff67e8f9, "hex color parsed opaque independently from transparency");
        check(HudStyle.parseColor("#80FFFFFF") == null && HudStyle.parseColor("white") == null,
                "invalid alpha color text cannot silently alter transparency");
        CapturePauseState state = new CapturePauseState();
        check(!state.paused, "new connection begins unpaused");
        state = state.update(true, "电脑安全桌面正在使用");
        check(state.paused && state.message.equals("电脑安全桌面正在使用"), "explicit pause message retained");
        CapturePauseState retained = state.update(null, "普通心跳");
        check(retained == state && retained.paused, "legacy status or absent pause field cannot resume capture");
        state = state.update(false, "已恢复");
        check(!state.paused && state.message.isEmpty(), "explicit resume clears pause message");
        check(state.update(true, " ").message.length() > 0, "missing pause explanation gets a useful fallback");
        PresentationProgress progress = new PresentationProgress();
        PresentationProgress.Report first = progress.presented(1, 1200, 1920, 0);
        state.update(true, "暂停");
        state.update(false, "恢复");
        PresentationProgress.Report resumed = progress.presented(2, 1200, 1920, 20000);
        check(first.sequence == 1 && resumed.sequence == 2, "same connection presentation sequence survives pause");
        System.out.println("PASS: " + assertions + " HUD and capture pause assertions");
    }
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        assertions++;
    }
}
