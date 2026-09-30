using TabLink.Core;

// File-system fixtures only. This program never launches adb or changes process environment.
var root = Path.Combine(Path.GetTempPath(), "TabLink-AdbLocator-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
try
{
    var app = Path.Combine(root, "app with spaces");
    var sdkRoot = Path.Combine(root, "sdk-root");
    var androidHome = Path.Combine(root, "android-home");
    var local = Path.Combine(root, "local");
    var path1 = Path.Combine(root, "path-first");
    var path2 = Path.Combine(root, "path-second");
    var bundle = Complete(Path.Combine(app, "tools", "platform-tools"));
    var sdk = Complete(Path.Combine(sdkRoot, "platform-tools"));
    var home = Complete(Path.Combine(androidHome, "platform-tools"));
    var localSdk = Complete(Path.Combine(local, "Android", "Sdk", "platform-tools"));
    var firstPath = Complete(path1);
    var secondPath = Complete(path2);
    var environment = new AdbSearchEnvironment(app, sdkRoot, androidHome, local, [path1, path2]);
    Check(AdbLocator.FindAdbPath(null, environment) == bundle, "bundle precedes SDK and PATH");
    Check(AdbLocator.FindAdbPath(firstPath, environment) == firstPath, "valid explicit setting precedes bundle");
    Check(AdbLocator.FindAdbPath('"' + firstPath + '"', environment) == firstPath, "quoted explicit path normalized");
    Check(AdbLocator.FindAdbPath(Path.Combine(root, "missing", "adb.exe"), environment) is null,
        "invalid explicit setting does not silently fall back");
    Check(AdbLocator.FindAdbPath("bad\0path", environment) is null, "invalid configured path rejected safely");
    Check(AdbLocator.FindAdbPath(firstPath + " version", environment) is null, "command line is not a file path");
    Check(!AdbLocator.IsCompleteInstallation(Path.GetDirectoryName(bundle)), "directory is not executable");
    var wrongName = Path.Combine(path1, "different.exe");
    File.WriteAllBytes(wrongName, [1]);
    Check(!AdbLocator.IsCompleteInstallation(wrongName), "only adb.exe filename is accepted");
    File.WriteAllBytes(bundle, []);
    Check(!AdbLocator.IsCompleteInstallation(bundle), "empty executable rejected");
    Check(AdbLocator.FindAdbPath(null, environment) == sdk, "incomplete bundle falls back to SDK root");
    File.WriteAllBytes(bundle, [1]);
    var bundleDll = Path.Combine(Path.GetDirectoryName(bundle)!, "AdbWinApi.dll");
    File.WriteAllBytes(bundleDll, []);
    Check(!AdbLocator.IsCompleteInstallation(bundle), "empty DLL rejected");
    File.Delete(bundleDll);
    Directory.CreateDirectory(bundleDll);
    Check(!AdbLocator.IsCompleteInstallation(bundle), "directory named DLL rejected");
    File.Delete(Path.Combine(Path.GetDirectoryName(sdk)!, "AdbWinUsbApi.dll"));
    Check(AdbLocator.FindAdbPath(null, environment) == home, "SDK root missing dependency falls back to Android home");
    File.Delete(home);
    Check(AdbLocator.FindAdbPath(null, environment) == localSdk, "local SDK follows explicit SDK environment");
    File.Delete(localSdk);
    Check(AdbLocator.FindAdbPath(null, environment) == firstPath, "PATH preserves order");
    File.Delete(firstPath);
    Check(AdbLocator.FindAdbPath(null, environment) == secondPath, "missing PATH entry skipped");
    Check(AdbLocator.FindAdbPath(null, environment with { PathEntries = ['"' + path2 + '"'] }) == secondPath,
        "quoted PATH entry supported");
    Check(AdbLocator.FindAdbPath(null, new(".", null, null, null, [".", "", "bad\0path"])) is null,
        "relative and invalid discovery roots cannot search current working directory");
    Check(AdbLocator.FindAdbPath(null, new(app, null, null, null, [])) is null, "no complete installation returns null");
    Console.WriteLine($"PASS: {count} bundled ADB locator assertions; no adb process was executed.");
}
finally
{
    var checkedRoot = Path.GetFullPath(root);
    var intendedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (checkedRoot.StartsWith(intendedRoot, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(checkedRoot).StartsWith("TabLink-AdbLocator-", StringComparison.Ordinal))
        Directory.Delete(checkedRoot, recursive: true);
}

string Complete(string directory)
{
    Directory.CreateDirectory(directory);
    foreach (var name in new[] { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll" })
        File.WriteAllBytes(Path.Combine(directory, name), [1]);
    return Path.Combine(directory, "adb.exe");
}
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + label);
    count++;
}
