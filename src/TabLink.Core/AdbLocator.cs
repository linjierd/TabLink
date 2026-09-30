namespace TabLink.Core;

/// <summary>Explicit search inputs make discovery testable without changing process environment or executing adb.</summary>
public sealed record AdbSearchEnvironment(
    string ApplicationDirectory,
    string? AndroidSdkRoot,
    string? AndroidHome,
    string? LocalApplicationData,
    IReadOnlyList<string> PathEntries);

public static class AdbLocator
{
    private static readonly string[] RequiredLibraries = ["AdbWinApi.dll", "AdbWinUsbApi.dll"];

    public static string? FindAdbPath(string? configuredPath = null) => FindAdbPath(configuredPath, new(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
        Environment.GetEnvironmentVariable("ANDROID_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>
    /// Prefer an explicit valid installation, then the app's complete bundled tools,
    /// then Android SDK locations and PATH. Invalid explicit settings fail closed;
    /// callers can diagnose/clear them rather than silently switching the user's tool.
    /// </summary>
    public static string? FindAdbPath(string? configuredPath, AdbSearchEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.IsNullOrWhiteSpace(configuredPath)) return NormalizeCompleteInstallation(configuredPath);

        var candidates = new List<string?>
        {
            Candidate(environment.ApplicationDirectory, "tools", "platform-tools", "adb.exe"),
            Candidate(environment.AndroidSdkRoot, "platform-tools", "adb.exe"),
            Candidate(environment.AndroidHome, "platform-tools", "adb.exe"),
            Candidate(environment.LocalApplicationData, "Android", "Sdk", "platform-tools", "adb.exe")
        };
        foreach (var entry in environment.PathEntries)
            candidates.Add(Candidate(entry, "adb.exe"));
        foreach (var candidate in candidates)
            if (candidate is not null && NormalizeCompleteInstallation(candidate) is { } found) return found;
        return null;
    }

    /// <summary>Checks file layout only; packaging separately verifies binary hashes and provenance.</summary>
    public static bool IsCompleteInstallation(string? executablePath) => NormalizeCompleteInstallation(executablePath) is not null;

    private static string? Candidate(string? root, params string[] suffix)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(root.Trim().Trim('"'));
            // Relative/empty discovery entries must not turn the working directory into a tool source.
            if (!Path.IsPathFullyQualified(expanded)) return null;
            return Path.Combine([expanded, .. suffix]);
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string? NormalizeCompleteInstallation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!string.Equals(Path.GetFileName(fullPath), "adb.exe", StringComparison.OrdinalIgnoreCase)
                || !IsNonEmptyFile(fullPath)) return null;
            var directory = Path.GetDirectoryName(fullPath)!;
            return RequiredLibraries.All(name => IsNonEmptyFile(Path.Combine(directory, name))) ? fullPath : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
    }

    private static bool IsNonEmptyFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & FileAttributes.Directory) == 0 && file.Length > 0;
    }
}
