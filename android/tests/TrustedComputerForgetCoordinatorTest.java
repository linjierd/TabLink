package com.tablink.client;

public final class TrustedComputerForgetCoordinatorTest {
    public static void main(String[] args) {
        String identity = repeat("33", 32);
        TrustedComputer saved = new TrustedComputer(identity, identity, "192.168.9.10", 27184);

        FakeActions success = new FakeActions();
        check(TrustedComputerForgetCoordinator.forget(saved, success)
                        == TrustedComputerForgetCoordinator.Outcome.SUCCESS,
                "successful removal clears metadata before deleting identity");
        check(success.clearCalls == 1 && success.deleteCalls == 1 && success.restoreCalls == 0,
                "successful removal performs each destructive step once");

        FakeActions clearFailure = new FakeActions();
        clearFailure.clearResult = false;
        check(TrustedComputerForgetCoordinator.forget(saved, clearFailure)
                        == TrustedComputerForgetCoordinator.Outcome.METADATA_CLEAR_FAILED,
                "metadata failure is reported without deleting the identity");
        check(clearFailure.deleteCalls == 0 && clearFailure.restoreCalls == 1,
                "metadata failure restores the visible trust record before returning");

        FakeActions identityFailure = new FakeActions();
        identityFailure.removal = TrustedComputerForgetCoordinator.IdentityRemoval.PRESERVED;
        check(TrustedComputerForgetCoordinator.forget(saved, identityFailure)
                        == TrustedComputerForgetCoordinator.Outcome.IDENTITY_DELETE_FAILED,
                "keystore failure is reported after restoring the trust record");
        check(identityFailure.restoreCalls == 1,
                "keystore failure keeps the saved computer available for a retry");

        FakeActions rollbackFailure = new FakeActions();
        rollbackFailure.removal = TrustedComputerForgetCoordinator.IdentityRemoval.PRESERVED;
        rollbackFailure.restoreResult = false;
        check(TrustedComputerForgetCoordinator.forget(saved, rollbackFailure)
                        == TrustedComputerForgetCoordinator.Outcome.ROLLBACK_FAILED,
                "failed rollback is never presented as successful removal");

        FakeActions unknownIdentity = new FakeActions();
        unknownIdentity.removal = TrustedComputerForgetCoordinator.IdentityRemoval.UNKNOWN;
        check(TrustedComputerForgetCoordinator.forget(saved, unknownIdentity)
                        == TrustedComputerForgetCoordinator.Outcome.IDENTITY_STATE_UNKNOWN,
                "unknown keystore outcome cannot restore metadata that may reference a missing key");
        check(unknownIdentity.restoreCalls == 0,
                "unknown keystore outcome keeps trust metadata cleared and requires re-enrollment");
        System.out.println("PASS trusted computer removal reports failure and rolls back metadata");
    }

    private static final class FakeActions implements TrustedComputerForgetCoordinator.Actions {
        boolean clearResult = true;
        boolean restoreResult = true;
        TrustedComputerForgetCoordinator.IdentityRemoval removal =
                TrustedComputerForgetCoordinator.IdentityRemoval.REMOVED;
        int clearCalls;
        int restoreCalls;
        int deleteCalls;

        @Override public boolean clearMetadata() { clearCalls++; return clearResult; }
        @Override public boolean restoreMetadata(TrustedComputer previous) {
            restoreCalls++;
            return restoreResult && previous != null;
        }
        @Override public TrustedComputerForgetCoordinator.IdentityRemoval deleteIdentity() {
            deleteCalls++;
            return removal;
        }
    }

    private static String repeat(String value, int count) {
        StringBuilder result = new StringBuilder(value.length() * count);
        for (int index = 0; index < count; index++) result.append(value);
        return result.toString();
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
