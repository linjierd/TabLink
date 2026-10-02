package com.tablink.client;

/** Serialises display activation with update-related system UI launches in this app process. */
final class UpdateInstallerUiGate {
    enum LaunchResult { LAUNCHED, BLOCKED_BY_DISPLAY, ALREADY_LAUNCHING, STALE_ATTEMPT }
    private enum UpdateOperation { NONE, PERMISSION_UI, PACKAGE_INSTALLER }

    private static final Object LOCK = new Object();
    private static Object activeDisplayOwner;
    private static UpdateOperation operation = UpdateOperation.NONE;
    private static boolean updateUiStarted;
    private static Object permissionOwner;
    private static UpdateInstallAttempt installerAttempt;

    private UpdateInstallerUiGate() { }

    static boolean tryActivateDisplay(Object owner) {
        if (owner == null) throw new IllegalArgumentException("owner");
        synchronized (LOCK) {
            if (operation != UpdateOperation.NONE || activeDisplayOwner != null && activeDisplayOwner != owner)
                return false;
            activeDisplayOwner = owner;
            return true;
        }
    }

    static void deactivateDisplay(Object owner) {
        synchronized (LOCK) {
            if (activeDisplayOwner == owner) activeDisplayOwner = null;
        }
    }

    static boolean tryBeginInstallerTransaction(UpdateInstallAttempt attempt) {
        if (attempt == null) throw new IllegalArgumentException("attempt");
        synchronized (LOCK) {
            if (activeDisplayOwner != null || operation != UpdateOperation.NONE) return false;
            operation = UpdateOperation.PACKAGE_INSTALLER;
            installerAttempt = attempt;
            return true;
        }
    }

    static LaunchResult tryLaunchPermissionUi(Object owner, Runnable launch) {
        if (owner == null || launch == null) throw new IllegalArgumentException("owner/launch");
        synchronized (LOCK) {
            if (activeDisplayOwner != null) return LaunchResult.BLOCKED_BY_DISPLAY;
            if (operation != UpdateOperation.NONE) return LaunchResult.ALREADY_LAUNCHING;
            operation = UpdateOperation.PERMISSION_UI;
            permissionOwner = owner;
            updateUiStarted = true;
            try {
                launch.run();
                return LaunchResult.LAUNCHED;
            } catch (RuntimeException failure) {
                operation = UpdateOperation.NONE;
                permissionOwner = null;
                updateUiStarted = false;
                throw failure;
            }
        }
    }

    static LaunchResult tryLaunchInstallerConfirmation(UpdateInstallAttempt attempt, Runnable launch) {
        if (attempt == null || launch == null) throw new IllegalArgumentException("attempt/launch");
        synchronized (LOCK) {
            if (activeDisplayOwner != null) return LaunchResult.BLOCKED_BY_DISPLAY;
            if (operation == UpdateOperation.PERMISSION_UI) return LaunchResult.ALREADY_LAUNCHING;
            if (operation == UpdateOperation.PACKAGE_INSTALLER
                    && (installerAttempt == null || !installerAttempt.matches(attempt)))
                return LaunchResult.STALE_ATTEMPT;
            if (updateUiStarted)
                return LaunchResult.ALREADY_LAUNCHING;
            operation = UpdateOperation.PACKAGE_INSTALLER;
            installerAttempt = attempt;
            updateUiStarted = true;
            try {
                launch.run();
                return LaunchResult.LAUNCHED;
            } catch (RuntimeException failure) {
                operation = UpdateOperation.NONE;
                installerAttempt = null;
                updateUiStarted = false;
                throw failure;
            }
        }
    }

    static boolean restoreInstallerTransaction(UpdateInstallAttempt attempt) {
        if (attempt == null) return false;
        synchronized (LOCK) {
            if (activeDisplayOwner == null && operation == UpdateOperation.NONE) {
                operation = UpdateOperation.PACKAGE_INSTALLER;
                installerAttempt = attempt;
                return true;
            }
            return operation == UpdateOperation.PACKAGE_INSTALLER
                    && installerAttempt != null && installerAttempt.matches(attempt);
        }
    }

    static void permissionUiFinished(Object owner) {
        synchronized (LOCK) {
            if (operation == UpdateOperation.PERMISSION_UI && permissionOwner == owner) {
                operation = UpdateOperation.NONE;
                permissionOwner = null;
                updateUiStarted = false;
            }
        }
    }

    static void installerTransactionFinished(UpdateInstallAttempt attempt) {
        synchronized (LOCK) {
            if (operation == UpdateOperation.PACKAGE_INSTALLER && installerAttempt != null
                    && installerAttempt.matches(attempt)) {
                operation = UpdateOperation.NONE;
                installerAttempt = null;
                updateUiStarted = false;
            }
        }
    }

    static void clearInstallerTransactionForRecovery() {
        synchronized (LOCK) {
            if (operation == UpdateOperation.PACKAGE_INSTALLER) {
                operation = UpdateOperation.NONE;
                installerAttempt = null;
                updateUiStarted = false;
            }
        }
    }

    static void resetForTests() {
        synchronized (LOCK) {
            activeDisplayOwner = null;
            operation = UpdateOperation.NONE;
            permissionOwner = null;
            updateUiStarted = false;
            installerAttempt = null;
        }
    }
}
