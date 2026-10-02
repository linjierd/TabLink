using TabLink.Core;
using TabLink.Windows;
using System.Security.Cryptography;

// File-system fixtures only. This program never launches adb or changes process environment.
var testParent = Path.Combine(AppContext.BaseDirectory, "test-artifacts");
var root = Path.Combine(testParent, "TabLink-AdbLocator-" + Guid.NewGuid().ToString("N"));
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
    VerifyUsbDeviceAutoSelection();
    VerifyProtectedStagingRecovery(root);
    Console.WriteLine($"PASS: {count} bundled ADB locator assertions; no adb process was executed.");
}

finally
{
    var checkedRoot = Path.GetFullPath(root);
    var intendedParent = Path.GetFullPath(testParent).TrimEnd(Path.DirectorySeparatorChar);
    if (!string.Equals(Path.GetDirectoryName(checkedRoot), intendedParent, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(checkedRoot).StartsWith("TabLink-AdbLocator-", StringComparison.Ordinal))
        throw new IOException("Refusing to delete an unexpected ADB locator test directory");
    Directory.Delete(checkedRoot, recursive: true);
    if (Directory.Exists(intendedParent) && !Directory.EnumerateFileSystemEntries(intendedParent).Any())
        Directory.Delete(intendedParent);
}

void VerifyUsbDeviceAutoSelection()
{
    Check(UsbDeviceAutoSelection.FindPreferredIndex([], null) == -1,
        "empty refresh has no automatic USB selection");
    Check(UsbDeviceAutoSelection.FindPreferredIndex([("blocked", false), ("ready", true)], null) == 1,
        "the only allowed USB device is selected even when it is not first");
    Check(UsbDeviceAutoSelection.FindPreferredIndex([("first", true), ("second", true)], null) == -1,
        "multiple allowed USB devices require an explicit selection");
    Check(UsbDeviceAutoSelection.FindPreferredIndex([("first", true), ("second", true)], "second") == 1,
        "an explicitly selected allowed USB device is preserved");
    Check(UsbDeviceAutoSelection.FindPreferredIndex([("old", false), ("ready", true)], "old") == 1,
        "a no-longer-allowed old selection falls back to the only allowed USB device");
    Check(UsbDeviceAutoSelection.FindPreferredIndex([("old", false), ("other", false)], "old") == -1,
        "blocked USB devices are never selected automatically");
}

void VerifyProtectedStagingRecovery(string fixtureRoot)
{
    var source = Path.Combine(fixtureRoot, "trusted-source");
    Directory.CreateDirectory(source);
    var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["adb.exe"] = "offline-adb-r37-fixture"u8.ToArray(),
        ["AdbWinApi.dll"] = "offline-api-r37-fixture"u8.ToArray(),
        ["AdbWinUsbApi.dll"] = "offline-usb-r37-fixture"u8.ToArray()
    };
    foreach (var payload in payloads)
        File.WriteAllBytes(Path.Combine(source, payload.Key), payload.Value);
    var hashes = payloads.ToDictionary(x => x.Key,
        x => Convert.ToHexString(SHA256.HashData(x.Value)), StringComparer.Ordinal);

    var protectedRoot = Path.Combine(fixtureRoot, "protected", "TabLink");
    var unsafePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var storage = new TrustedAdbStorageBoundary(
        protectedRoot,
        VerifyDirectory,
        adbRoot =>
        {
            Directory.CreateDirectory(protectedRoot);
            Directory.CreateDirectory(adbRoot);
            VerifyDirectory(protectedRoot);
            VerifyDirectory(adbRoot);
        },
        (path, parent) =>
        {
            RequireDirectChild(path, parent);
            VerifyDirectory(parent);
            Directory.CreateDirectory(path);
            VerifyDirectory(path);
        },
        (path, parent) =>
        {
            RequireDirectChild(path, parent);
            VerifyDirectory(parent);
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.WriteThrough);
        },
        VerifyDirectory,
        path =>
        {
            var full = Path.GetFullPath(path);
            if (unsafePaths.Contains(full))
                throw new UnauthorizedAccessException("injected unsafe protected ADB file");
            if (!File.Exists(full)) throw new FileNotFoundException("fixture file missing", full);
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("fixture is not an ordinary protected file");
        });

    var adbRoot = Path.Combine(protectedRoot, "Adb");
    Directory.CreateDirectory(adbRoot);
    var target = TrustedBundledAdb.ProtectedTargetDirectory(protectedRoot, hashes);
    Directory.CreateDirectory(target);
    File.WriteAllBytes(Path.Combine(target, "adb.exe"), payloads["adb.exe"]);
    File.WriteAllBytes(Path.Combine(target, "AdbWinApi.dll"), "crash-cut-truncated"u8.ToArray());
    // AdbWinUsbApi.dll is intentionally absent: this models a crash after the
    // target directory and only part of the protected triplet were committed.
    var staged = TrustedBundledAdb.StageAndGetVerifiedPath(
        Path.Combine(source, "adb.exe"), hashes, storage);
    Check(staged == Path.Combine(target, "adb.exe") && payloads.All(payload =>
            File.ReadAllBytes(Path.Combine(target, payload.Key)).SequenceEqual(payload.Value)),
        "a fixed-hash source safely repairs missing and truncated protected staging members");
    Check(TrustedBundledAdb.IsTrustStorageFailure(new InvalidDataException("fixture")),
        "InvalidDataException is classified as a protected ADB staging or trust-store failure");

    var protectedMember = Path.Combine(target, "AdbWinApi.dll");
    File.WriteAllBytes(protectedMember, "unsafe-content"u8.ToArray());
    unsafePaths.Add(Path.GetFullPath(protectedMember));
    Check(Throws<UnauthorizedAccessException>(() => TrustedBundledAdb.StageAndGetVerifiedPath(
              Path.Combine(source, "adb.exe"), hashes, storage)) &&
          File.ReadAllBytes(protectedMember).SequenceEqual("unsafe-content"u8.ToArray()),
        "staging repair refuses to delete a member that fails the protected-file predicate");
}

void VerifyDirectory(string path)
{
    var full = Path.GetFullPath(path);
    if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("fixture directory is a reparse point");
}

void RequireDirectChild(string path, string parent)
{
    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("fixture path escaped its expected parent");
}

bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
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
