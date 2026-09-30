package com.tablink.client;

/** Persisted HUD values, with validation independent of Android widgets. */
public final class HudStyle {
    public static final int DEFAULT_COLOR = 0xffffffff;
    public static final int DEFAULT_TRANSPARENCY = 30;
    public final int position, color, transparency;

    public HudStyle(int position, int color, int transparency) {
        this.position = position >= 0 && position <= 8 ? position : 0;
        this.color = 0xff000000 | color;
        this.transparency = Math.max(0, Math.min(100, transparency));
    }

    public float opacity() { return (100 - transparency) / 100f; }

    public static Integer parseColor(String text) {
        String value = text.trim();
        if (!value.matches("#[0-9a-fA-F]{6}")) return null;
        return 0xff000000 | Integer.parseInt(value.substring(1), 16);
    }
}
