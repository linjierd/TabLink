package com.tablink.client;

/** Small synchronized policy machine that prevents automatic installs during a display session. */
public final class UpdateStateMachine {
    public enum Mode { AUTOMATIC, DOWNLOAD_THEN_ASK, NEVER }
    public enum State { IDLE, CHECKING, UP_TO_DATE, AVAILABLE, DOWNLOADING, READY_DEFERRED,
        READY_TO_INSTALL, AWAITING_PERMISSION, INSTALLING, FAILED, DISABLED }
    public enum Action { NONE, DOWNLOAD, START_INSTALL }
    public enum InstallResult { PENDING_USER_ACTION, SUCCESS, RETRY }

    private State state = State.IDLE;
    private boolean activeSession;
    private Mode mode = Mode.AUTOMATIC;
    private boolean updateAvailable;
    private boolean staged;

    public synchronized State state() { return state; }
    public synchronized boolean hasStagedUpdate() { return staged; }

    public synchronized void setMode(Mode value) {
        if (value == null) throw new IllegalArgumentException("mode");
        mode = value;
        if (mode == Mode.NEVER) state = State.DISABLED;
        else if (state == State.DISABLED)
            state = staged ? activeSession ? State.READY_DEFERRED : State.READY_TO_INSTALL : State.IDLE;
    }

    public synchronized Mode mode() { return mode; }
    public synchronized boolean checksEnabled() { return mode != Mode.NEVER; }

    public synchronized void beginCheck() { state = mode == Mode.NEVER ? State.DISABLED : State.CHECKING; }

    public synchronized Action checked(boolean available) {
        if (mode == Mode.NEVER) { state = State.DISABLED; return Action.NONE; }
        updateAvailable = available;
        if (!available) { staged = false; state = State.UP_TO_DATE; return Action.NONE; }
        state = State.AVAILABLE;
        if (!activeSession) { state = State.DOWNLOADING; return Action.DOWNLOAD; }
        return Action.NONE;
    }

    public synchronized Action requestDownload() {
        if (mode == Mode.NEVER || !updateAvailable || state == State.DOWNLOADING) return Action.NONE;
        if (activeSession) { state = State.AVAILABLE; return Action.NONE; }
        state = State.DOWNLOADING;
        return Action.DOWNLOAD;
    }

    public synchronized Action downloaded() {
        if (mode == Mode.NEVER) { state = State.DISABLED; return Action.NONE; }
        staged = true;
        if (activeSession) { state = State.READY_DEFERRED; return Action.NONE; }
        state = State.READY_TO_INSTALL;
        return mode == Mode.AUTOMATIC ? Action.START_INSTALL : Action.NONE;
    }

    public synchronized void downloadDeferred(boolean active) {
        activeSession = active;
        if (mode == Mode.NEVER) { state = State.DISABLED; return; }
        if (!staged && updateAvailable) state = State.AVAILABLE;
    }

    public synchronized Action sessionChanged(boolean active) {
        activeSession = active;
        if (mode == Mode.NEVER) { state = State.DISABLED; return Action.NONE; }
        if (active) {
            if (state == State.READY_TO_INSTALL) state = State.READY_DEFERRED;
            return Action.NONE;
        }
        if (staged) {
            state = State.READY_TO_INSTALL;
            return mode == Mode.AUTOMATIC ? Action.START_INSTALL : Action.NONE;
        }
        if (mode != Mode.NEVER && updateAvailable && state != State.DOWNLOADING) {
            state = State.DOWNLOADING; return Action.DOWNLOAD;
        }
        return Action.NONE;
    }

    public synchronized boolean beginInstall(boolean explicit, boolean permissionGranted) {
        if (mode == Mode.NEVER || !staged || activeSession && !explicit) return false;
        state = permissionGranted ? State.INSTALLING : State.AWAITING_PERMISSION;
        return true;
    }

    public synchronized void installPermissionReturned(boolean granted) {
        if (mode == Mode.NEVER) { state = State.DISABLED; return; }
        state = granted ? State.READY_TO_INSTALL : State.AWAITING_PERMISSION;
    }

    public synchronized void installerReturned() {
        if (mode == Mode.NEVER) { state = State.DISABLED; return; }
        installResult(InstallResult.RETRY);
    }

    public synchronized void restoreInstalling() {
        if (mode == Mode.NEVER) { state = State.DISABLED; return; }
        updateAvailable = true;
        staged = true;
        state = State.INSTALLING;
    }

    public synchronized void installResult(InstallResult result) {
        if (mode == Mode.NEVER) {
            if (result == InstallResult.SUCCESS) { staged = false; updateAvailable = false; }
            state = State.DISABLED;
            return;
        }
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

    public synchronized void failed() { state = mode == Mode.NEVER ? State.DISABLED : State.FAILED; }
    public synchronized void reset() {
        updateAvailable = false; staged = false; state = mode == Mode.NEVER ? State.DISABLED : State.IDLE;
    }
}
