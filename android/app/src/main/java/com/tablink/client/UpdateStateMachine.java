package com.tablink.client;

/** Small synchronized policy machine that prevents automatic installs during a display session. */
public final class UpdateStateMachine {
    public enum State { IDLE, CHECKING, UP_TO_DATE, AVAILABLE, DOWNLOADING, READY_DEFERRED,
        READY_TO_INSTALL, AWAITING_PERMISSION, INSTALLING, FAILED, DISABLED }
    public enum Action { NONE, DOWNLOAD, PROMPT_INSTALL }
    public enum InstallResult { PENDING_USER_ACTION, SUCCESS, RETRY }

    private State state = State.IDLE;
    private boolean activeSession;
    private boolean autoDownload = true;
    private boolean updateAvailable;
    private boolean staged;

    public synchronized State state() { return state; }
    public synchronized boolean hasStagedUpdate() { return staged; }

    public synchronized void setAutoDownload(boolean enabled) {
        autoDownload = enabled;
        if (!enabled && !staged && state != State.CHECKING && state != State.DOWNLOADING) state = State.DISABLED;
        else if (enabled && state == State.DISABLED) state = State.IDLE;
    }

    public synchronized void beginCheck() { state = State.CHECKING; }

    public synchronized Action checked(boolean available) {
        updateAvailable = available;
        if (!available) { staged = false; state = State.UP_TO_DATE; return Action.NONE; }
        state = State.AVAILABLE;
        if (autoDownload && !activeSession) { state = State.DOWNLOADING; return Action.DOWNLOAD; }
        return Action.NONE;
    }

    public synchronized Action requestDownload() {
        if (!updateAvailable || state == State.DOWNLOADING) return Action.NONE;
        if (activeSession) { state = State.AVAILABLE; return Action.NONE; }
        state = State.DOWNLOADING;
        return Action.DOWNLOAD;
    }

    public synchronized Action downloaded() {
        staged = true;
        if (activeSession) { state = State.READY_DEFERRED; return Action.NONE; }
        state = State.READY_TO_INSTALL;
        return Action.PROMPT_INSTALL;
    }

    public synchronized void downloadDeferred(boolean active) {
        activeSession = active;
        if (!staged && updateAvailable) state = State.AVAILABLE;
    }

    public synchronized Action sessionChanged(boolean active) {
        activeSession = active;
        if (active) {
            if (state == State.READY_TO_INSTALL) state = State.READY_DEFERRED;
            return Action.NONE;
        }
        if (staged) { state = State.READY_TO_INSTALL; return Action.PROMPT_INSTALL; }
        if (updateAvailable && autoDownload && state != State.DOWNLOADING) {
            state = State.DOWNLOADING; return Action.DOWNLOAD;
        }
        return Action.NONE;
    }

    public synchronized boolean beginInstall(boolean explicit, boolean permissionGranted) {
        if (!staged || activeSession && !explicit) return false;
        state = permissionGranted ? State.INSTALLING : State.AWAITING_PERMISSION;
        return true;
    }

    public synchronized void installPermissionReturned(boolean granted) {
        state = granted ? State.READY_TO_INSTALL : State.AWAITING_PERMISSION;
    }

    public synchronized void installerReturned() {
        installResult(InstallResult.RETRY);
    }

    public synchronized void installResult(InstallResult result) {
        if (result == InstallResult.PENDING_USER_ACTION) {
            if (staged) state = State.INSTALLING;
        } else if (result == InstallResult.SUCCESS) reset();
        else {
            // The controller revalidates the signed envelope and APK before every retry.
            // Marking this ready also restores a callback delivered after process recreation.
            updateAvailable = true;
            staged = true;
            state = State.READY_TO_INSTALL;
        }
    }

    public synchronized void failed() { state = State.FAILED; }
    public synchronized void reset() {
        updateAvailable = false; staged = false; state = autoDownload ? State.IDLE : State.DISABLED;
    }
}
