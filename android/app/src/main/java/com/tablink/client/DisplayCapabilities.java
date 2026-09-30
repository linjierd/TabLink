package com.tablink.client;

import android.content.Context;
import android.graphics.Point;
import android.hardware.display.DisplayManager;
import android.view.Display;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/** Reads the actual built-in display; mode dimensions stay in hardware orientation. */
public final class DisplayCapabilities {
    private static volatile int requestedModeId;
    private static volatile float requestedRefreshRate;
    private static volatile boolean brightnessWorkaroundActive;
    private DisplayCapabilities() {}

    public static void recordRequestedMode(int modeId, float rate) {
        requestedModeId = modeId;
        requestedRefreshRate = rate;
    }

    public static void recordBrightnessWorkaround(boolean active) { brightnessWorkaroundActive = active; }

    public static Display.Mode preferredMode(Display display) {
        Display.Mode preferred = display.getMode();
        for (Display.Mode mode : display.getSupportedModes()) {
            if (mode.getPhysicalWidth() == preferred.getPhysicalWidth()
                    && mode.getPhysicalHeight() == preferred.getPhysicalHeight()
                    && mode.getRefreshRate() > preferred.getRefreshRate()) preferred = mode;
        }
        return preferred;
    }

    @SuppressWarnings("deprecation")
    public static JSONObject read(Context context) {
        DisplayManager manager = (DisplayManager) context.getSystemService(Context.DISPLAY_SERVICE);
        Display display = manager == null ? null : manager.getDisplay(Display.DEFAULT_DISPLAY);
        if (display == null) throw new IllegalStateException("Built-in display is unavailable");
        Point real = new Point();
        display.getRealSize(real);
        Display.Mode active = display.getMode();
        try {
            JSONObject profile = new JSONObject();
            profile.put("width", real.x);
            profile.put("height", real.y);
            profile.put("rotation", display.getRotation());
            profile.put("activeModeId", active.getModeId());
            profile.put("refreshRate", (double) display.getRefreshRate());
            profile.put("nativeWidth", active.getPhysicalWidth());
            profile.put("nativeHeight", active.getPhysicalHeight());
            Display.Mode preferred = preferredMode(display);
            profile.put("preferredModeId", preferred.getModeId());
            profile.put("maxRefreshRate", (double) preferred.getRefreshRate());
            profile.put("requestedModeId", requestedModeId);
            profile.put("requestedRefreshRate", (double) requestedRefreshRate);
            profile.put("brightnessWorkaroundActive", brightnessWorkaroundActive);
            JSONArray modes = new JSONArray();
            for (Display.Mode mode : display.getSupportedModes()) {
                JSONObject item = new JSONObject();
                item.put("width", mode.getPhysicalWidth());
                item.put("height", mode.getPhysicalHeight());
                item.put("refreshRate", (double) mode.getRefreshRate());
                item.put("modeId", mode.getModeId());
                modes.put(item);
            }
            profile.put("supportedModes", modes);
            return profile;
        } catch (JSONException invalidHardwareValue) {
            throw new IllegalStateException("Display returned invalid metrics", invalidHardwareValue);
        }
    }
}
