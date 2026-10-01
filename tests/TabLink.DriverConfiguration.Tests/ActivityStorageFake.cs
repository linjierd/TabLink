namespace TabLink.DriverSetup;

internal static partial class DriverInstaller
{
    internal static string DisplayLeaseRoot => Path.Combine(AppContext.BaseDirectory, "display-lease-test-root");
    internal static IDisposable AcquireDisplayLeaseMutationLock() =>
        throw new InvalidOperationException("Configuration XML tests must not acquire the production display lease lock.");
    internal static void VerifyDisplayLeaseProtectedNamespace() =>
        throw new InvalidOperationException("Configuration XML tests call the isolated directory scanner directly.");
    internal static void VerifyDisplayLeaseStateFile(string path) =>
        throw new InvalidOperationException("Configuration XML tests inject an isolated state-file verifier.");
}
