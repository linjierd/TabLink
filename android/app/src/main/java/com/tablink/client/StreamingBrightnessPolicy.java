package com.tablink.client;

/** Window-local workaround; unknown brightness scales and higher brightness fail closed. */
public final class StreamingBrightnessPolicy {
    public static final float FLOOR = 0.08f;
    private StreamingBrightnessPolicy() {}

    public static boolean mayApply(boolean streaming, int setting, int settingMaximum,
            float actualRefreshRate, float requestedRefreshRate, float currentWindowBrightness) {
        return keepWhileStreaming(streaming, setting, settingMaximum, requestedRefreshRate)
                && !Float.isNaN(actualRefreshRate) && !Float.isInfinite(actualRefreshRate)
                && actualRefreshRate > 0 && actualRefreshRate + 0.5f < requestedRefreshRate
                && (currentWindowBrightness < 0 || currentWindowBrightness < FLOOR);
    }

    public static boolean keepWhileStreaming(boolean streaming, int setting, int settingMaximum,
            float requestedRefreshRate) {
        return streaming && settingMaximum == 255 && setting >= 0 && setting <= 16
                && !Float.isNaN(requestedRefreshRate) && !Float.isInfinite(requestedRefreshRate)
                && requestedRefreshRate > 60.5f;
    }
}
