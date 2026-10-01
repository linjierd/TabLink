package com.tablink.client;

/** Coordinates preference removal with Android Keystore deletion and rollback. */
public final class TrustedComputerForgetCoordinator {
    public enum Outcome {
        SUCCESS,
        METADATA_CLEAR_FAILED,
        IDENTITY_DELETE_FAILED,
        IDENTITY_STATE_UNKNOWN,
        ROLLBACK_FAILED
    }

    public enum IdentityRemoval {
        REMOVED,
        PRESERVED,
        UNKNOWN
    }

    public interface Actions {
        boolean clearMetadata();
        boolean restoreMetadata(TrustedComputer previous);
        IdentityRemoval deleteIdentity();
    }

    private TrustedComputerForgetCoordinator() { }

    public static Outcome forget(TrustedComputer previous, Actions actions) {
        if (actions == null) throw new IllegalArgumentException("缺少可信电脑存储操作");
        if (!actions.clearMetadata()) {
            return restore(previous, actions) ? Outcome.METADATA_CLEAR_FAILED : Outcome.ROLLBACK_FAILED;
        }
        IdentityRemoval removal;
        try { removal = actions.deleteIdentity(); }
        catch (RuntimeException failure) { removal = IdentityRemoval.UNKNOWN; }
        if (removal == IdentityRemoval.REMOVED) return Outcome.SUCCESS;
        if (removal == IdentityRemoval.PRESERVED) {
            return restore(previous, actions) ? Outcome.IDENTITY_DELETE_FAILED : Outcome.ROLLBACK_FAILED;
        }
        // An unknown Keystore state cannot safely be paired with old metadata:
        // the private key may already be gone. Keep metadata cleared and require
        // explicit re-enrollment instead of claiming that rollback succeeded.
        return Outcome.IDENTITY_STATE_UNKNOWN;
    }

    private static boolean restore(TrustedComputer previous, Actions actions) {
        return previous == null || actions.restoreMetadata(previous);
    }
}
