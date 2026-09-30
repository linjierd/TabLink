namespace TabLink.Windows;

internal static class UpdateVersionPolicy
{
    internal static bool IsUpgrade(string currentVersion, string candidateVersion) =>
        StableSemanticVersion.Parse(candidateVersion).CompareTo(StableSemanticVersion.Parse(currentVersion)) > 0;
}
