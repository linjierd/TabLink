namespace TabLink.Updater;

internal readonly record struct UpdatePreflightState(
    bool SignedPackageValidated,
    bool ExtractedFilesVerified,
    bool ProtectedStagingVerified,
    bool RollbackSnapshotCaptured)
{
    internal bool CanSignalReady => SignedPackageValidated && ExtractedFilesVerified && ProtectedStagingVerified && RollbackSnapshotCaptured;

    internal void DemandReadyAllowed()
    {
        if (!CanSignalReady)
            throw new InvalidOperationException("更新预检尚未完整通过，禁止发送退出握手。");
    }
}
