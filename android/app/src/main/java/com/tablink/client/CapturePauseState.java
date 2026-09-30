package com.tablink.client;

/** Status packets never stand in for a frame acknowledgement or alter a decoder lifecycle. */
public final class CapturePauseState {
    public final boolean paused;
    public final String message;

    public CapturePauseState() { this(false, ""); }
    private CapturePauseState(boolean paused, String message) { this.paused = paused; this.message = message; }

    public CapturePauseState update(Boolean explicitPaused, String newMessage) {
        if (explicitPaused == null) return this;
        if (!explicitPaused) return new CapturePauseState();
        String value = newMessage == null ? "" : newMessage.trim();
        return new CapturePauseState(true, value.isEmpty() ? "电脑暂时无法采集画面，等待恢复" : value);
    }
}
