package com.tablink.client;

/** Aspect-fit mapping; touches on letterboxing never become desktop clicks. */
public final class FrameGeometry {
    public final float left;
    public final float top;
    public final float width;
    public final float height;

    private FrameGeometry(float left, float top, float width, float height) {
        this.left = left;
        this.top = top;
        this.width = width;
        this.height = height;
    }

    public static FrameGeometry fit(int imageWidth, int imageHeight, int viewWidth, int viewHeight) {
        if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) {
            return new FrameGeometry(0, 0, 0, 0);
        }
        float scale = Math.min((float) viewWidth / imageWidth, (float) viewHeight / imageHeight);
        float width = imageWidth * scale;
        float height = imageHeight * scale;
        return new FrameGeometry((viewWidth - width) / 2, (viewHeight - height) / 2, width, height);
    }

    public boolean contains(float x, float y) {
        return width > 0 && height > 0 && !Float.isNaN(x) && !Float.isInfinite(x)
                && !Float.isNaN(y) && !Float.isInfinite(y)
                && x >= left && y >= top && x <= left + width && y <= top + height;
    }

    public float normalizedX(float x) {
        return width <= 0 ? 0 : Math.max(0, Math.min(1, (x - left) / width));
    }

    public float normalizedY(float y) {
        return height <= 0 ? 0 : Math.max(0, Math.min(1, (y - top) / height));
    }
}
