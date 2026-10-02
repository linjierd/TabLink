using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TabLink.Updater;
using TabLink.Windows;

var assertions = 0;
var scenarios = 0;
var root = Path.Combine(Path.GetTempPath(), "TabLink-Update-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = signer.ExportSubjectPublicKeyInfo();

try
{
    Run("pinned release public key matches the published trust file", () =>
    {
        var pinned = Convert.FromBase64String(UpdateTrust.ManifestSignerSpkiBase64);
        var published = Convert.FromBase64String(File.ReadAllText(Path.Combine("updates", "stable-public-key.spki.base64")).Trim());
        Check(CryptographicOperations.FixedTimeEquals(pinned, published), "compiled and published SPKI match");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine("updates", "stable-public-key.spki.base64")))) == "B275488873764E84A4FA3252D7EFAAFCBD13B97DB95A4D43C3F852748C12FECA", "published SPKI file hash matches audited value");
    });

    Run("Android and Windows accept the same pinned-key release envelope", () =>
    {
        var fixture = File.ReadAllBytes(Path.Combine("android", "tests", "fixtures", "stable-manifest-valid.json"));
        var pinned = Convert.FromBase64String(UpdateTrust.ManifestSignerSpkiBase64);
        var parsed = UpdateManifestVerifier.VerifyAndParse(fixture, pinned, new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero));
        Check(parsed.ReleaseId == "fixture-0.8.0" && parsed.Artifacts.Single().Platform == "android", "cross-platform release fixture verifies");
        var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(fixture) ?? throw new Exception("fixture envelope missing");
        var alteredPayload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload)).Replace("\"version\":\"0.8.0\"", "\"version\":\"0.8.1\"", StringComparison.Ordinal);
        var tampered = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope(Convert.ToBase64String(Encoding.UTF8.GetBytes(alteredPayload)), envelope.Signature));
        Reject<InvalidDataException>(() => UpdateManifestVerifier.VerifyAndParse(tampered, pinned, new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero)), "cross-platform fixture tampering rejected");
    });

    Run("stable SemVer parsing and ordering", () =>
    {
        Check(StableSemanticVersion.Parse("0.7.10").CompareTo(StableSemanticVersion.Parse("0.7.3")) > 0, "numeric patch ordering");
        Check(UpdateVersionPolicy.IsUpgrade("0.7.3", "0.8.0"), "newer formal version is an upgrade");
        Check(!UpdateVersionPolicy.IsUpgrade("0.7.3", "0.7.3"), "same version is not reinstalled");
        Check(!UpdateVersionPolicy.IsUpgrade("0.7.3", "0.7.2"), "older signed version cannot downgrade installation");
        Reject<FormatException>(() => StableSemanticVersion.Parse("0.8.0-beta.1"), "prerelease is rejected");
        Reject<FormatException>(() => StableSemanticVersion.Parse("01.2.3"), "leading zero is rejected");
    });

    Run("automatic apply waits for every active display session", () =>
    {
        Check(AutomaticUpdateApplyPolicy.ShouldAllowPackageDownload(false, false, false, false), "idle app permits package download");
        Check(!AutomaticUpdateApplyPolicy.ShouldAllowPackageDownload(true, false, false, false), "active display pauses package download");
        Check(!AutomaticUpdateApplyPolicy.ShouldAllowPackageDownload(false, true, false, false), "busy app pauses package download");
        Check(!AutomaticUpdateApplyPolicy.ShouldAllowPackageDownload(false, false, true, false), "display cleanup pauses package download");
        Check(!AutomaticUpdateApplyPolicy.ShouldAllowPackageDownload(false, false, false, true), "connection startup pauses package download");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, true, false, false, false, false) == AutomaticUpdateApplyDisposition.ScheduleWhenIdle, "idle app schedules automatic install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, true, true, false, false, false) == AutomaticUpdateApplyDisposition.DeferredForActivity, "active display defers install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, true, false, true, false, false) == AutomaticUpdateApplyDisposition.DeferredForActivity, "in-progress operation defers install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, true, false, false, true, false) == AutomaticUpdateApplyDisposition.DeferredForActivity, "display cleanup defers install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, false, false, false, false, false) == AutomaticUpdateApplyDisposition.None, "no update does nothing");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, true, false, false, false, true) == AutomaticUpdateApplyDisposition.None, "closing app does not schedule twice");
        Check(AutomaticUpdateApplyPolicy.Evaluate(false, true, false, false, false, false) == AutomaticUpdateApplyDisposition.None, "manual-install policy never schedules automatic install");
        Check(AutomaticUpdateApplyPolicy.ShouldLaunchOnNormalExit(true, true), "automatic policy installs a ready update during normal exit");
        Check(!AutomaticUpdateApplyPolicy.ShouldLaunchOnNormalExit(false, true), "manual-install policy does not install during normal exit");
        Check(!AutomaticUpdateApplyPolicy.ShouldLaunchOnNormalExit(true, false), "normal exit does not launch without a ready update");
    });

    Run("protected updater bundle identity requires the complete helper set", () =>
    {
        UpdaterHelperDigest[] complete =
        [
            new("TabLink.Updater.exe", 101, new string('1', 64)),
            new("TabLink.Updater.dll", 202, new string('2', 64)),
            new("TabLink.Updater.deps.json", 303, new string('3', 64)),
            new("TabLink.Updater.runtimeconfig.json", 404, new string('4', 64))
        ];
        var identity = ProtectedUpdaterStager.ComputeBundleSha256(complete);
        Check(identity.Length == 64 && identity.All(Uri.IsHexDigit), "bundle identity is a SHA-256 value");
        Check(identity == ProtectedUpdaterStager.ComputeBundleSha256(complete.Reverse()), "bundle identity is independent of enumeration order");
        var changed = complete.ToArray();
        changed[0] = changed[0] with { Sha256 = new string('A', 64) };
        Check(identity != ProtectedUpdaterStager.ComputeBundleSha256(changed), "helper content change selects another protected directory");
        Reject<InvalidDataException>(() => ProtectedUpdaterStager.ComputeBundleSha256(complete[..3]), "missing runtime companion rejected");
        Reject<InvalidDataException>(() => ProtectedUpdaterStager.ComputeBundleSha256([complete[0], complete[0], complete[2], complete[3]]), "duplicate helper rejected");
        Reject<InvalidDataException>(() => ProtectedUpdaterStager.ComputeBundleSha256([complete[0], complete[1], complete[2], new("unexpected.json", 1, new string('5', 64))]), "unknown helper rejected");

        var programData = Path.Combine(root, "ProgramData-policy-fixture");
        var protectedPath = ProtectedUpdaterStager.GetBundleDirectory(programData, identity);
        Check(Path.GetFileName(protectedPath) == "sha256-" + identity.ToLowerInvariant(), "protected leaf is content addressed");
        Check(Path.GetDirectoryName(protectedPath) == Path.Combine(Path.GetFullPath(programData), "TabLink", "Updater"), "protected helper stays under ProgramData TabLink updater root");
        Reject<ArgumentException>(() => ProtectedUpdaterStager.GetBundleDirectory(programData, "..\\outside"), "path-like bundle identity rejected");

        const string programFiles = @"C:\Program Files";
        const string formalProgramData = @"C:\ProgramData";
        const string formalInstall = @"C:\Program Files\TabLink";
        var formal = WindowsUpdatePathPolicy.Resolve(formalInstall, programFiles, formalProgramData);
        Check(formal.InstallDirectory == formalInstall && formal.ProgramDataTabLinkRoot == @"C:\ProgramData\TabLink", "formal install and protected data roots are fixed");
        Check(WindowsUpdatePathPolicy.GetExpectedInstallDirectory(programFiles) == formalInstall, "expected install is the exact Program Files TabLink child");
        Check(WindowsUpdatePathPolicy.EnumerateDirectoryChain(formalInstall).SequenceEqual(new[] { @"C:\", @"C:\Program Files", formalInstall }, StringComparer.OrdinalIgnoreCase), "formal install validation covers every directory from volume root to TabLink");
        Check(WindowsUpdatePathPolicy.EnumerateDirectoryChain(formalProgramData).SequenceEqual(new[] { @"C:\", formalProgramData }, StringComparer.OrdinalIgnoreCase), "ProgramData validation covers the volume root and leaf");
        Reject<InvalidDataException>(() => WindowsUpdatePathPolicy.Resolve(@"C:\Users\TestUser\Desktop\TabLink", programFiles, formalProgramData), "Desktop mirror cannot authorize automatic replacement");
        Reject<InvalidDataException>(() => WindowsUpdatePathPolicy.Resolve(@"C:\Program Files\TabLink\child", programFiles, formalProgramData), "install subdirectory cannot authorize automatic replacement");
        Reject<InvalidDataException>(() => WindowsUpdatePathPolicy.Resolve(formalInstall, programFiles, @"D:\ProgramData"), "cross-volume ProgramData transaction is rejected");

        var transaction = ProtectedUpdaterStager.GetTransactionPaths(formalInstall, programFiles, formalProgramData, new string('a', 32));
        Check(transaction.ProgramDataRoot == formalProgramData && transaction.SecurityRoot == @"C:\ProgramData\TabLink", "transaction security root is the protected ProgramData TabLink directory");
        Check(transaction.TransactionRoot == @"C:\ProgramData\TabLink\Transactions\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "transaction path is fixed below protected ProgramData");
        Check(new[] { transaction.Staging, transaction.Backup, transaction.Failed }.All(path => Path.GetPathRoot(path) == Path.GetPathRoot(formalInstall) && Path.GetDirectoryName(path) == transaction.TransactionRoot), "staging backup and failed remain on the formal install volume");
        Reject<ArgumentException>(() => ProtectedUpdaterStager.GetTransactionPaths(formalInstall, programFiles, formalProgramData, "../outside"), "transaction traversal identifier rejected");
        Reject<ArgumentException>(() => WindowsUpdatePathPolicy.Resolve("relative\\TabLink", programFiles, formalProgramData), "relative install directory rejected");
    });

    Run("protected updater ACL excludes the current user and requires trusted owner", () =>
    {
        var directoryAcl = ProtectedUpdaterStager.CreateDirectorySecurity();
        var fileAcl = ProtectedUpdaterStager.CreateFileSecurity();
        Check(ProtectedUpdaterStager.HasExactProtectedAcl(directoryAcl, true), "directory ACL has only protected SYSTEM and Administrators full control");
        Check(ProtectedUpdaterStager.HasExactProtectedAcl(fileAcl, false), "file ACL has only protected SYSTEM and Administrators full control");

        var directoryRules = directoryAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Check(directoryAcl.AreAccessRulesProtected && directoryRules.All(rule => !rule.IsInherited), "directory inheritance is disabled");
        Check(directoryRules.All(rule => rule.FileSystemRights == FileSystemRights.FullControl), "directory grants no reduced or extra access rule");

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new Exception("current Windows identity has no SID");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!currentUser.Equals(administrators) && !currentUser.Equals(system))
        {
            var userOwned = ProtectedUpdaterStager.CreateDirectorySecurity();
            userOwned.SetOwner(currentUser);
            Check(!ProtectedUpdaterStager.HasExactProtectedAcl(userOwned, true), "current user cannot own the privileged helper directory");

            var userWritable = ProtectedUpdaterStager.CreateDirectorySecurity();
            userWritable.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.Write,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Check(!ProtectedUpdaterStager.HasExactProtectedAcl(userWritable, true), "current user write access is rejected");

            var userOwnedFile = ProtectedUpdaterStager.CreateFileSecurity();
            userOwnedFile.SetOwner(currentUser);
            Check(!ProtectedUpdaterStager.HasExactProtectedAcl(userOwnedFile, false), "current user cannot own a privileged helper file");

            var userWritableFile = ProtectedUpdaterStager.CreateFileSecurity();
            userWritableFile.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.Write, AccessControlType.Allow));
            Check(!ProtectedUpdaterStager.HasExactProtectedAcl(userWritableFile, false), "current user cannot write a privileged helper file");
        }

        var inherited = ProtectedUpdaterStager.CreateDirectorySecurity();
        inherited.SetAccessRuleProtection(false, true);
        Check(!ProtectedUpdaterStager.HasExactProtectedAcl(inherited, true), "inheritable parent ACL is rejected");

        var installDirectoryAcl = ProtectedUpdaterStager.CreateInstallDirectorySecurity();
        var installFileAcl = ProtectedUpdaterStager.CreateInstallFileSecurity();
        Check(ProtectedUpdaterStager.HasExactInstallAcl(installDirectoryAcl, true), "install directory grants only trusted full control and Users read-execute");
        Check(ProtectedUpdaterStager.HasExactInstallAcl(installFileAcl, false), "install file grants only trusted full control and Users read-execute");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var usersDirectoryRule = installDirectoryAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(users));
        var dangerousUserRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
                                  FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
                                  FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        Check(usersDirectoryRule.FileSystemRights == (FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize) &&
              (usersDirectoryRule.FileSystemRights & dangerousUserRights) == 0,
            "ordinary Users receive RX without write delete or ACL ownership rights");
        var overPrivilegedInstall = ProtectedUpdaterStager.CreateInstallFileSecurity();
        overPrivilegedInstall.ResetAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        Check(!ProtectedUpdaterStager.HasExactInstallAcl(overPrivilegedInstall, false), "install ACL rejects Users modify permission");

        Check(WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(installDirectoryAcl), "formal directory policy accepts Administrators owner with Users RX only");
        var unsafeWrite = ProtectedUpdaterStager.CreateInstallDirectorySecurity();
        unsafeWrite.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(unsafeWrite), "formal directory policy rejects ordinary user write");
        var unsafeDelete = ProtectedUpdaterStager.CreateInstallDirectorySecurity();
        unsafeDelete.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(unsafeDelete), "formal directory policy rejects ordinary user DELETE and DELETE_CHILD");
        var unsafeAclWrite = ProtectedUpdaterStager.CreateInstallDirectorySecurity();
        unsafeAclWrite.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(unsafeAclWrite), "formal directory policy rejects ordinary user WRITE_DAC and WRITE_OWNER equivalents");
        var inheritableMutation = ProtectedUpdaterStager.CreateInstallDirectorySecurity();
        inheritableMutation.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(inheritableMutation), "formal directory policy rejects propagating InheritOnly mutation ACEs");
        var genericMutation = new DirectorySecurity();
        genericMutation.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICIIO;GA;;;BU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(genericMutation), "formal directory policy rejects generic-all mutation ACEs");
        var unsafeHelperFile = ProtectedUpdaterStager.CreateInstallFileSecurity();
        unsafeHelperFile.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write | FileSystemRights.Delete, AccessControlType.Allow));
        Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(unsafeHelperFile), "required helper source file policy rejects ordinary user write and delete");
        var writableApplicationDll = ProtectedUpdaterStager.CreateInstallFileSecurity();
        writableApplicationDll.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
        Check(!ProtectedUpdaterStager.HasExactInstallAcl(writableApplicationDll, false) &&
            !WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(writableApplicationDll),
            "strict full-install-tree policy rejects a writable non-helper application DLL");
        var standardProgramDataAcl = new DirectorySecurity();
        standardProgramDataAcl.SetAccessRuleProtection(true, false);
        standardProgramDataAcl.SetOwner(system);
        standardProgramDataAcl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        standardProgramDataAcl.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        standardProgramDataAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        Check(WindowsUpdatePathPolicy.HasSafeAncestorNamespaceAcl(standardProgramDataAcl), "ancestor policy permits standard ProgramData create and write rights that cannot replace an existing protected child");
        var unsafeAncestor = standardProgramDataAcl;
        unsafeAncestor.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        Check(!WindowsUpdatePathPolicy.HasSafeAncestorNamespaceAcl(unsafeAncestor), "ancestor policy rejects DELETE_CHILD that can replace an existing protected child");

        var actualProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var actualProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var actualVolumeRoot = Path.GetPathRoot(actualProgramFiles)!;
        Check(WindowsUpdatePathPolicy.HasSafeAncestorNamespaceAcl(new DirectoryInfo(actualVolumeRoot).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)), "standard Windows volume-root ACL is accepted as a safe namespace ancestor");
        Check(WindowsUpdatePathPolicy.HasSafeAncestorNamespaceAcl(new DirectoryInfo(actualProgramFiles).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)), "standard Program Files ACL including safe Creator Owner inheritance is accepted");
        Check(WindowsUpdatePathPolicy.HasSafeAncestorNamespaceAcl(new DirectoryInfo(actualProgramData).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)), "standard ProgramData Users write ACL is accepted while protected children remain strict");
        if (!currentUser.Equals(administrators) && !currentUser.Equals(system))
        {
            var unsafeOwner = ProtectedUpdaterStager.CreateInstallDirectorySecurity();
            unsafeOwner.SetOwner(currentUser);
            Check(!WindowsUpdatePathPolicy.HasNoOrdinaryUserMutationAccess(unsafeOwner), "formal directory policy rejects an ordinary user owner");
        }
    });

    Run("signed stable manifest is accepted", () =>
    {
        const string formalDownload = "https://linjie.space/download/api/download?path=TabLink%2Fstable%2F0.8.0%2FTabLink-windows-x64-0.8.0.zip";
        var artifact = Artifact("windows-x64", "0.8.0", 11, formalDownload, 123, new string('A', 64));
        var envelope = Sign(Payload([artifact]));
        var parsed = UpdateManifestVerifier.VerifyAndParse(envelope, publicKey);
        var selected = UpdateManifestVerifier.SelectWindowsUpdate(parsed, "0123456789abcdef0123456789abcdef", StableSemanticVersion.Parse("0.7.3"));
        Check(selected?.Version == "0.8.0", "newer Windows artifact selected");
        Check(selected?.Url == formalDownload, "formal DownloadSite query URL is accepted without normalization drift");
        Check(UpdateManifestVerifier.ValidateHttps(formalDownload, "fixture").Query.StartsWith("?path=", StringComparison.Ordinal), "HTTPS query is preserved");
        Reject<InvalidDataException>(() => UpdateManifestVerifier.ValidateHttps("https://user@example.test/a.zip?path=ok", "fixture"), "HTTPS user info remains rejected");
        Reject<InvalidDataException>(() => UpdateManifestVerifier.ValidateHttps("https://example.test/a.zip?path=ok#fragment", "fixture"), "HTTPS fragment remains rejected");
    });

    Run("zero-percent rollout is a valid signed pause and selects no update", () =>
    {
        var artifact = Artifact("windows-x64", "0.8.0", 11, "https://updates.example.test/windows.zip", 123, new string('A', 64));
        var parsed = UpdateManifestVerifier.VerifyAndParse(Sign(Payload([artifact], rolloutPercentage: 0)), publicKey);
        Check(parsed.RolloutPercentage == 0, "zero-percent rollout parses as a valid stable manifest");
        Check(UpdateManifestVerifier.SelectWindowsUpdate(parsed, "0123456789abcdef0123456789abcdef", StableSemanticVersion.Parse("0.7.3")) is null,
            "paused rollout does not select a download");
    });

    Run("signature tampering and duplicate fields are rejected", () =>
    {
        var payload = Payload([Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/windows.zip", 1, new string('1', 64))]);
        var envelope = Sign(payload);
        envelope[^8] ^= 1;
        Reject<InvalidDataException>(() => UpdateManifestVerifier.VerifyAndParse(envelope, publicKey), "tampered envelope rejected");
        var duplicate = Encoding.UTF8.GetBytes("{\"payload\":\"AA==\",\"payload\":\"AA==\",\"signature\":\"AA==\"}");
        Reject<InvalidDataException>(() => UpdateManifestVerifier.VerifyAndParse(duplicate, publicKey), "duplicate envelope property rejected");
    });

    Run("manifest policy rejects unsafe or ambiguous releases", () =>
    {
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0", 1, "http://updates.example.test/a.zip", 1, new string('2', 64))])), "HTTP artifact rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([
            Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/a.zip", 1, new string('2', 64)),
            Artifact("windows-x64", "0.8.1", 2, "https://updates.example.test/b.zip", 1, new string('3', 64))])), "duplicate platform rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0-beta", 1, "https://updates.example.test/a.zip", 1, new string('2', 64))])), "prerelease rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/a.zip", 1, new string('2', 64))], DateTimeOffset.UtcNow.AddHours(25))), "future manifest rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/a.zip", 1, new string('2', 64))], DateTimeOffset.UtcNow.AddDays(-367))), "stale manifest rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/a.zip", 1, new string('2', 64))], rolloutPercentage: -1)), "negative rollout rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/a.zip", 1, new string('2', 64))], rolloutPercentage: 101)), "rollout above 100 rejected");
        Reject<InvalidDataException>(() => VerifyPayload(Payload([Artifact("windows-x64", "0.8.0", 1, "https://updates.example.test/a.zip", 1, new string('2', 64))], publishedAtText: "2026-09-29T14:35:00.1Z")), "fractional-second timestamp rejected");
    });

    await RunAsync("download validates signed metadata and a withdrawal clears pending update", async () =>
    {
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("fixture")));
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var published = DateTimeOffset.UtcNow.AddMinutes(-2);
        var available = Sign(Payload([Artifact("windows-x64", "0.8.0", 17, "https://updates.example.test/windows.zip", package.Length, hash)], published));
        var withdrawn = Sign(Payload([Artifact("android", "0.8.0", 17, "https://updates.example.test/android.apk", 7, new string('4', 64))], published.AddMinutes(1), "stable-0.8.0-withdrawn"));
        var handler = new FixtureHandler { Manifest = available, Package = package };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "client-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        var ready = await client.CheckAndDownloadAsync(new Uri("https://updates.example.test/stable.json"), CancellationToken.None);
        Check(ready?.Version == "0.8.0", "signed package becomes ready");
        Check(await client.TryLoadPendingAsync(CancellationToken.None) is not null, "cached package is revalidated from signed envelope");
        handler.Manifest = withdrawn;
        Check(await client.CheckAndDownloadAsync(new Uri("https://updates.example.test/stable.json"), CancellationToken.None) is null, "withdrawn Windows artifact is not returned");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")), "withdrawal clears pending metadata");
    });

    await RunAsync("edited pending metadata cannot authorize another package", async () =>
    {
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("trusted")));
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var handler = new FixtureHandler { Manifest = Sign(Payload([Artifact("windows-x64", "0.8.1", 18, "https://updates.example.test/windows.zip", package.Length, hash)], releaseId: "stable-0.8.1")), Package = package };
        using var http = new HttpClient(handler);
        var cache = Path.Combine(root, "tamper-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        var ready = await client.CheckAndDownloadAsync(new Uri("https://updates.example.test/stable.json"), CancellationToken.None) ?? throw new Exception("fixture did not download");
        var metadata = await File.ReadAllTextAsync(Path.Combine(cache, "pending-windows.json"));
        await File.WriteAllTextAsync(Path.Combine(cache, "pending-windows.json"), metadata.Replace(ready.Sha256, new string('0', 64), StringComparison.Ordinal));
        Check(await client.TryLoadPendingAsync(CancellationToken.None) is null, "tampered hash rejected against signed manifest");
    });

    await RunAsync("zero-percent rollout clears pending metadata without downloading", async () =>
    {
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("trusted")));
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var artifact = Artifact("windows-x64", "0.8.0", 19, "https://updates.example.test/windows.zip", package.Length, hash);
        var published = DateTimeOffset.UtcNow.AddMinutes(-2);
        var handler = new FixtureHandler { Manifest = Sign(Payload([artifact], published, "stable-pause-test")), Package = package };
        using var http = new HttpClient(handler);
        var cache = Path.Combine(root, "paused-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        Check(await client.CheckAndDownloadAsync(new Uri("https://updates.example.test/stable.json"), CancellationToken.None) is not null, "active rollout downloads once");
        var requestsBeforePause = handler.PackageRequests;
        handler.Manifest = Sign(Payload([artifact], published.AddMinutes(1), "stable-pause-test", rolloutPercentage: 0));
        Check(await client.CheckAndDownloadAsync(new Uri("https://updates.example.test/stable.json"), CancellationToken.None) is null, "paused rollout returns no update");
        Check(handler.PackageRequests == requestsBeforePause, "paused rollout makes no package request");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")), "paused rollout clears pending metadata");
    });

    Run("safe ZIP extraction accepts normal package and rejects traversal and links", () =>
    {
        var normal = Path.Combine(root, "normal.zip");
        File.WriteAllBytes(normal, CreateZip(
            ("TabLink.exe", [1, 2, 3]),
            ("TabLink.dll", [3]),
            ("TabLink.deps.json", Encoding.UTF8.GetBytes("{}")),
            ("TabLink.runtimeconfig.json", Encoding.UTF8.GetBytes("{}")),
            ("TabLink.Updater.exe", [4, 5]),
            ("TabLink.Updater.dll", [6]),
            ("TabLink.Updater.deps.json", Encoding.UTF8.GetBytes("{}")),
            ("TabLink.Updater.runtimeconfig.json", Encoding.UTF8.GetBytes("{}")),
            ("update-channel.json", Encoding.UTF8.GetBytes("{}")),
            ("tools/helper.txt", [7])));
        var output = Path.Combine(root, "normal-output");
        ExtractedUpdatePackage extracted;
        using (var locked = new FileStream(normal, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Reject<IOException>(() => { using var mutation = new FileStream(normal, FileMode.Open, FileAccess.Write, FileShare.None); }, "locked verified package cannot be replaced before extraction");
            extracted = SafeZipExtractor.Extract(locked, output);
            SafeZipExtractor.VerifyExtractedFiles(locked, output, extracted, requireProtectedTree: false);
        }
        Check(File.ReadAllBytes(Path.Combine(output, "TabLink.exe")).SequenceEqual(new byte[] { 1, 2, 3 }), "normal root executable extracted");
        Check(File.ReadAllBytes(Path.Combine(output, "TabLink.Updater.exe")).SequenceEqual(new byte[] { 4, 5 }), "new updater helper is extracted as package data while old helper runs elsewhere");

        File.WriteAllBytes(Path.Combine(output, "TabLink.exe"), [9, 9, 9]);
        using (var signedZip = new FileStream(normal, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject<InvalidDataException>(() => SafeZipExtractor.VerifyExtractedFiles(signedZip, output, extracted, requireProtectedTree: false), "post-extraction in-place mutation is rejected against the locked ZIP");
        File.WriteAllBytes(Path.Combine(output, "TabLink.exe"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(output, "injected.exe"), "untrusted");
        using (var signedZip = new FileStream(normal, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject<InvalidDataException>(() => SafeZipExtractor.VerifyExtractedFiles(signedZip, output, extracted, requireProtectedTree: false), "post-extraction extra executable is rejected");

        var traversal = Path.Combine(root, "traversal.zip");
        File.WriteAllBytes(traversal, CreateZip(("TabLink.exe", [1]), ("../escape.txt", [9])));
        Reject<InvalidDataException>(() => Extract(traversal, Path.Combine(root, "traversal-output")), "parent traversal rejected");
        Check(!File.Exists(Path.Combine(root, "escape.txt")), "traversal wrote nothing outside destination");

        var link = Path.Combine(root, "link.zip");
        using (var file = new FileStream(link, FileMode.CreateNew))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var exe = zip.CreateEntry("TabLink.exe"); using (var stream = exe.Open()) stream.WriteByte(1);
            var symlink = zip.CreateEntry("bad-link"); symlink.ExternalAttributes = unchecked((int)0xA0000000); using var ignored = symlink.Open();
        }
        Reject<InvalidDataException>(() => Extract(link, Path.Combine(root, "link-output")), "symbolic link rejected");

        var emptySegment = Path.Combine(root, "empty-segment.zip");
        File.WriteAllBytes(emptySegment, CreateZip(("TabLink.exe", [1]), ("folder//file.txt", [2])));
        Reject<InvalidDataException>(() => Extract(emptySegment, Path.Combine(root, "empty-segment-output")), "empty path segment rejected");
    });

    Run("updater arguments reject unknown switches", () =>
    {
        Reject<ArgumentException>(() => UpdaterArguments.Parse(["--unknown", "value"]), "unknown updater argument rejected");
    });

    Run("update ready gate stays closed until every destructive-update preflight succeeds", () =>
    {
        var state = new UpdatePreflightState();
        Check(!state.CanSignalReady, "initial state cannot ask the old main process to exit");
        state = state with { SignedPackageValidated = true };
        Check(!state.CanSignalReady, "signature validation alone cannot signal ready");
        state = state with { ExtractedFilesVerified = true };
        Check(!state.CanSignalReady, "extraction validation alone cannot signal ready");
        state = state with { ProtectedStagingVerified = true };
        Check(!state.CanSignalReady, "protected staging without a rollback snapshot cannot signal ready");
        var damagedPackageState = state with { SignedPackageValidated = false, RollbackSnapshotCaptured = true };
        Check(!damagedPackageState.CanSignalReady, "a damaged or unverified package never reaches ready even if later state exists");
        Reject<InvalidOperationException>(() => damagedPackageState.DemandReadyAllowed(), "damaged package cannot trigger old-main exit");
        state = state with { RollbackSnapshotCaptured = true };
        Check(state.CanSignalReady, "ready opens only after signature extraction protected staging and rollback snapshot succeed");
        state.DemandReadyAllowed();
    });

    Run("update handshake and startup health bind every process identity field to a nonce", () =>
    {
        var nonce = new string('A', 64);
        var otherNonce = new string('B', 64);
        var handshake = JsonSerializer.SerializeToUtf8Bytes(new { waitPid = 101, waitStartUtcTicks = 202L, updaterPid = 303, updaterStartUtcTicks = 404L, nonce });
        Check(UpdateSignalSecurity.ValidateHandshake(handshake, 101, 202, 303, 404, nonce), "valid handshake binds owner pid start ticks updater pid updater start ticks and nonce");
        Check(!UpdateSignalSecurity.ValidateHandshake(handshake, 100, 202, 303, 404, nonce), "handshake rejects wrong owner pid");
        Check(!UpdateSignalSecurity.ValidateHandshake(handshake, 101, 203, 303, 404, nonce), "handshake rejects wrong owner start ticks");
        Check(!UpdateSignalSecurity.ValidateHandshake(handshake, 101, 202, 304, 404, nonce), "handshake rejects wrong updater pid");
        Check(!UpdateSignalSecurity.ValidateHandshake(handshake, 101, 202, 303, 405, nonce), "handshake rejects wrong updater start ticks");
        Check(!UpdateSignalSecurity.ValidateHandshake(handshake, 101, 202, 303, 404, otherNonce), "handshake rejects wrong random nonce");
        var duplicateHandshake = Encoding.UTF8.GetBytes($"{{\"waitPid\":101,\"waitStartUtcTicks\":202,\"updaterPid\":303,\"updaterStartUtcTicks\":404,\"nonce\":\"{nonce}\",\"nonce\":\"{nonce}\"}}");
        Check(!UpdateSignalSecurity.ValidateHandshake(duplicateHandshake, 101, 202, 303, 404, nonce), "handshake rejects duplicate security fields");

        const string executable = @"C:\Program Files\TabLink\TabLink.exe";
        var health = JsonSerializer.SerializeToUtf8Bytes(new { pid = 404, processStartUtcTicks = 505L, executablePath = executable, nonce });
        Check(UpdateSignalSecurity.ValidateHealth(health, 404, 505, executable, nonce), "valid health binds pid start ticks executable and nonce");
        Check(!UpdateSignalSecurity.ValidateHealth(health, 405, 505, executable, nonce), "health rejects wrong pid");
        Check(!UpdateSignalSecurity.ValidateHealth(health, 404, 506, executable, nonce), "health rejects wrong start ticks");
        Check(!UpdateSignalSecurity.ValidateHealth(health, 404, 505, @"C:\Program Files\TabLink\other.exe", nonce), "health rejects wrong executable");
        Check(!UpdateSignalSecurity.ValidateHealth(health, 404, 505, executable, otherNonce), "health rejects wrong nonce");
    });

    Run("update channel configuration keeps legacy manifestUrl and adds an ordered HTTPS fallback", () =>
    {
        var applicationDirectory = Path.Combine(root, "channel-configuration");
        var localDataDirectory = Path.Combine(root, "channel-configuration-data");
        Directory.CreateDirectory(applicationDirectory);
        var channelPath = Path.Combine(applicationDirectory, "update-channel.json");
        File.WriteAllText(channelPath, """
            {
              "enabled": true,
              "manifestUrl": "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json",
              "checkIntervalMinutes": 360
            }
            """);
        var legacy = UpdateChannelConfiguration.Load(applicationDirectory, localDataDirectory);
        Check(legacy.ManifestUri?.Host == "linjie.space", "legacy manifestUrl remains the primary source");
        Check(legacy.FallbackManifestUri is null && legacy.ManifestUris.Count == 1, "legacy configuration remains valid without a fallback");

        File.WriteAllText(channelPath, """
            {
              "enabled": true,
              "manifestUrl": "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json",
              "fallbackManifestUrl": "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json",
              "checkIntervalMinutes": 360
            }
            """);
        var dual = UpdateChannelConfiguration.Load(applicationDirectory, localDataDirectory);
        Check(dual.ManifestUris.Count == 2 && dual.ManifestUris[0].Host == "linjie.space" && dual.ManifestUris[1].Host == "github.com", "primary and fallback sources keep their configured order");

        File.WriteAllText(channelPath, """
            {
              "enabled": true,
              "manifestUrl": "https://updates.example.test/manifest.json",
              "fallbackManifestUrl": "https://updates.example.test/manifest.json",
              "checkIntervalMinutes": 360
            }
            """);
        Reject<InvalidDataException>(() => UpdateChannelConfiguration.Load(applicationDirectory, localDataDirectory), "duplicate sources are rejected");
    });

    await RunAsync("a bad primary signature or HTTP response falls back to a valid signed manifest", async () =>
    {
        const string primaryManifestUrl = "https://linjie.space/TabLink/stable/manifest.json";
        const string fallbackManifestUrl = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json";
        const string packageUrl = "https://github.com/linjierd/TabLink/releases/download/0.9.0/TabLink-windows-x64.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("signed fallback")));
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var payload = Payload([Artifact("windows-x64", "0.9.0", 21, packageUrl, package.Length, hash)], releaseId: "stable-0.9.0-signature-fallback");
        var validEnvelope = Sign(payload);
        var decoded = JsonSerializer.Deserialize<SignedUpdateEnvelope>(validEnvelope) ?? throw new Exception("signed fixture envelope missing");
        var alteredPayload = Encoding.UTF8.GetString(payload).Replace("\"build\":21", "\"build\":22", StringComparison.Ordinal);
        var invalidEnvelope = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope(Convert.ToBase64String(Encoding.UTF8.GetBytes(alteredPayload)), decoded.Signature));
        var handler = new MultiSourceFixtureHandler();
        handler.Add(primaryManifestUrl, invalidEnvelope);
        handler.Add(fallbackManifestUrl, validEnvelope);
        handler.Add(packageUrl, package);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new UpdateClient(http, publicKey, Path.Combine(root, "signature-fallback-cache"), StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        var ready = await client.CheckAndDownloadAsync([new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None);
        Check(ready?.Version == "0.9.0", "valid fallback source authorizes the package after primary signature failure");
        Check(handler.Requests.Contains(primaryManifestUrl) && handler.Requests.Contains(fallbackManifestUrl), "both manifest sources were attempted");

        var httpFailureHandler = new MultiSourceFixtureHandler();
        httpFailureHandler.Add(primaryManifestUrl, [], HttpStatusCode.ServiceUnavailable);
        httpFailureHandler.Add(fallbackManifestUrl, validEnvelope);
        httpFailureHandler.Add(packageUrl, package);
        using var httpFailureClient = new HttpClient(httpFailureHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var httpFallbackClient = new UpdateClient(httpFailureClient, publicKey, Path.Combine(root, "http-fallback-cache"), StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        Check(await httpFallbackClient.CheckAndDownloadAsync([new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None) is not null, "HTTP failure on the primary source advances to the valid fallback");
    });

    await RunAsync("a stalled primary manifest has an independent timeout and cannot starve the fallback", async () =>
    {
        const string primaryManifestUrl = "https://linjie.space/TabLink/stable/manifest.json";
        const string fallbackManifestUrl = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json";
        const string packageUrl = "https://github.com/linjierd/TabLink/releases/download/0.9.0/TabLink-windows-x64.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("bounded source timeout")));
        var envelope = Sign(Payload(
            [Artifact("windows-x64", "0.9.0", 21, packageUrl, package.Length, Convert.ToHexString(SHA256.HashData(package)))],
            releaseId: "stable-0.9.0-source-timeout"));
        var handler = new StalledPrimaryFixtureHandler(primaryManifestUrl, fallbackManifestUrl, envelope, packageUrl, package);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new UpdateClient(http, publicKey, Path.Combine(root, "source-timeout-cache"),
            StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef", TimeSpan.FromMilliseconds(50));
        var ready = await client.CheckAndDownloadAsync(
            [new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None);
        Check(ready?.Version == "0.9.0", "fallback remains usable after the primary source-specific timeout");
        Check(handler.Requests.SequenceEqual([primaryManifestUrl, fallbackManifestUrl, packageUrl]),
            "the stalled primary is cancelled before the fallback manifest and package are requested");
    });

    await RunAsync("cancelling an in-flight package before commit leaves no ready update", async () =>
    {
        const string manifestUrl = "https://updates.example.test/cancel/manifest.json";
        const string packageUrl = "https://updates.example.test/cancel/windows.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("must not commit after cancellation")));
        var envelope = Sign(Payload(
            [Artifact("windows-x64", "0.9.0", 21, packageUrl, package.Length, Convert.ToHexString(SHA256.HashData(package)))],
            releaseId: "stable-0.9.0-cancel-before-commit"));
        using var cancellation = new CancellationTokenSource();
        var handler = new CancelAtPackageEndFixtureHandler(manifestUrl, envelope, packageUrl, package, cancellation);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "cancel-before-commit-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"),
            "0123456789abcdef0123456789abcdef");
        await RejectAsync<OperationCanceledException>(() => client.CheckAndDownloadAsync(
            [new Uri(manifestUrl)], cancellation.Token), "policy cancellation must win before package commit");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")),
            "cancelled download cannot publish pending update metadata");
        Check(!Directory.Exists(cache) || !Directory.EnumerateFiles(cache, "TabLink-windows-x64.zip", SearchOption.AllDirectories).Any(),
            "cancelled download cannot publish a final package");
    });

    await RunAsync("display activity cancels an in-flight package without cancelling manifest policy", async () =>
    {
        const string manifestUrl = "https://updates.example.test/display-started/manifest.json";
        const string packageUrl = "https://updates.example.test/display-started/windows.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("display started during package transfer")));
        var envelope = Sign(Payload(
            [Artifact("windows-x64", "0.9.0", 21, packageUrl, package.Length, Convert.ToHexString(SHA256.HashData(package)))],
            releaseId: "stable-0.9.0-display-started"));
        using var packagePolicy = new CancellationTokenSource();
        var handler = new CancelAtPackageEndFixtureHandler(manifestUrl, envelope, packageUrl, package, packagePolicy);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "display-started-download-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"),
            "0123456789abcdef0123456789abcdef");
        await RejectAsync<OperationCanceledException>(() => client.CheckAndDownloadAsync(
            [new Uri(manifestUrl)], CancellationToken.None, packagePolicy.Token),
            "display policy cancellation stops the package before it becomes ready");
        Check(File.Exists(Path.Combine(cache, "manifest-floor.json")),
            "package-only cancellation preserves the verified anti-replay decision");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")),
            "package-only cancellation cannot publish pending metadata");
        Check(!Directory.EnumerateFiles(cache, "TabLink-windows-x64.zip", SearchOption.AllDirectories).Any(),
            "package-only cancellation cannot publish a final package");
    });

    await RunAsync("an active display can verify the manifest without starting the package download and resumes later", async () =>
    {
        const string manifestUrl = "https://updates.example.test/display-active/manifest.json";
        const string packageUrl = "https://updates.example.test/display-active/windows.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("deferred while display is active")));
        var envelope = Sign(Payload(
            [Artifact("windows-x64", "0.9.0", 21, packageUrl, package.Length, Convert.ToHexString(SHA256.HashData(package)))],
            releaseId: "stable-0.9.0-display-active"));
        var handler = new MultiSourceFixtureHandler();
        handler.Add(manifestUrl, envelope);
        handler.Add(packageUrl, package);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "display-active-download-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"),
            "0123456789abcdef0123456789abcdef");
        using var packagePolicy = new CancellationTokenSource();
        packagePolicy.Cancel();
        await RejectAsync<OperationCanceledException>(() => client.CheckAndDownloadAsync(
            [new Uri(manifestUrl)], CancellationToken.None, packagePolicy.Token),
            "active display policy defers package work after manifest verification");
        Check(handler.Requests.SequenceEqual([manifestUrl]), "active display makes no package HTTP request");
        Check(File.Exists(Path.Combine(cache, "manifest-floor.json")), "signed manifest still advances the anti-replay floor while download is deferred");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")), "deferred package cannot publish a ready update");

        var resumed = await client.CheckAndDownloadAsync([new Uri(manifestUrl)], CancellationToken.None,
            CancellationToken.None);
        Check(resumed?.Version == "0.9.0", "package download resumes when the display becomes idle");
        Check(handler.Requests.SequenceEqual([manifestUrl, manifestUrl, packageUrl]),
            "idle retry rechecks the signed manifest before requesting the package");
    });

    await RunAsync("coordinator drain waits for cancelled package temp cleanup before Never completes", async () =>
    {
        const string manifestUrl = "https://updates.example.test/never-drain/manifest.json";
        const string packageUrl = "https://updates.example.test/never-drain/windows.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes(new string('N', 512 * 1024))));
        var envelope = Sign(Payload(
            [Artifact("windows-x64", "0.9.0", 21, packageUrl, package.Length, Convert.ToHexString(SHA256.HashData(package)))],
            releaseId: "stable-0.9.0-never-drain"));
        var handler = new BlockingPackageFixtureHandler(manifestUrl, envelope, packageUrl, package);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "never-drain-cache");
        var configuration = new UpdateChannelConfiguration(true, new Uri(manifestUrl), null,
            TimeSpan.FromHours(6), "0123456789abcdef0123456789abcdef");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"),
            configuration.CohortId);
        using var lifetime = new CancellationTokenSource();
        using var coordinator = new WindowsUpdateCoordinator(configuration, client, http, root, cache, _ => { });
        coordinator.Start(lifetime.Token);
        await handler.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Directory.EnumerateFiles(cache, "*.download", SearchOption.AllDirectories).Any(),
            "fixture reaches an in-flight package temp file before Never cancellation");
        lifetime.Cancel();
        await coordinator.WaitForWorkerAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(handler.CancellationObserved.Task.IsCompleted,
            "worker observes cancellation before the drain task completes");
        Check(!Directory.EnumerateFiles(cache, "*.download", SearchOption.AllDirectories).Any(),
            "drained worker leaves no package download temp file");
        Check(!Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(),
            "drained worker leaves no metadata temp file");
        Check(!Directory.EnumerateFiles(cache, "TabLink-windows-x64.zip", SearchOption.AllDirectories).Any(),
            "drained worker cannot publish a final package");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")),
            "drained worker cannot publish pending metadata");
    });

    await RunAsync("the newest valid signed pause is authoritative over an older active source", async () =>
    {
        const string primaryManifestUrl = "https://linjie.space/TabLink/stable/manifest.json";
        const string fallbackManifestUrl = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json";
        const string packageUrl = "https://updates.example.test/paused.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("must not download")));
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var artifact = Artifact("windows-x64", "0.9.1", 22, packageUrl, package.Length, hash);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        var handler = new MultiSourceFixtureHandler();
        handler.Add(primaryManifestUrl, Sign(Payload([artifact], newer, "stable-0.9.1-paused", rolloutPercentage: 0)));
        handler.Add(fallbackManifestUrl, Sign(Payload([artifact], newer.AddMinutes(-1), "stable-0.9.1-paused")));
        handler.Add(packageUrl, package);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "authoritative-pause-cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "pending-windows.json"), "stale pending marker");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        Check(await client.CheckAndDownloadAsync([new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None) is null, "newest pause returns no update");
        Check(!handler.Requests.Contains(packageUrl), "older active source cannot trigger a package request");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")), "authoritative pause clears pending metadata");

        var replayHandler = new MultiSourceFixtureHandler();
        replayHandler.Add(fallbackManifestUrl,
            Sign(Payload([artifact], newer.AddMinutes(-1), "stable-0.9.1-paused")));
        replayHandler.Add(packageUrl, package);
        using var replayHttp = new HttpClient(replayHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var recreated = new UpdateClient(replayHttp, publicKey, cache,
            StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        await RejectAsync<UpdatePolicyBlockedException>(() => recreated.CheckAndDownloadAsync(
            [new Uri(fallbackManifestUrl)], CancellationToken.None),
            "a recreated client rejects an older signed replay after accepting a newer pause");
        Check(!replayHandler.Requests.Contains(packageUrl),
            "persisted manifest floor blocks package download from an older replay");
    });

    await RunAsync("failure to persist a newer signed decision is a fatal policy result", async () =>
    {
        const string oldManifestUrl = "https://updates.example.test/floor-write/old-manifest.json";
        const string oldPackageUrl = "https://updates.example.test/floor-write/old-windows.zip";
        const string newerManifestUrl = "https://updates.example.test/floor-write/new-manifest.json";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("floor persistence fixture")));
        var artifact = Artifact("windows-x64", "0.9.0", 21, oldPackageUrl, package.Length,
            Convert.ToHexString(SHA256.HashData(package)));
        var oldPublished = DateTimeOffset.UtcNow.AddMinutes(-3);
        var cache = Path.Combine(root, "floor-write-failure-cache");
        var initialHandler = new MultiSourceFixtureHandler();
        initialHandler.Add(oldManifestUrl, Sign(Payload([artifact], oldPublished, "stable-0.9.0-floor-write")));
        initialHandler.Add(oldPackageUrl, package);
        using (var initialHttp = new HttpClient(initialHandler) { Timeout = Timeout.InfiniteTimeSpan })
        {
            var initialClient = new UpdateClient(initialHttp, publicKey, cache,
                StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
            Check(await initialClient.CheckAndDownloadAsync([new Uri(oldManifestUrl)], CancellationToken.None) is not null,
                "fixture creates the accepted floor that will be locked");
        }

        var newerHandler = new MultiSourceFixtureHandler();
        newerHandler.Add(newerManifestUrl, Sign(Payload([artifact], oldPublished.AddMinutes(1),
            "stable-0.9.0-newer-pause", rolloutPercentage: 0)));
        using var newerHttp = new HttpClient(newerHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var newerClient = new UpdateClient(newerHttp, publicKey, cache,
            StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        var floorPath = Path.Combine(cache, "manifest-floor.json");
        using (var floorLock = new FileStream(floorPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await RejectAsync<UpdatePolicyBlockedException>(() => newerClient.CheckAndDownloadAsync(
                [new Uri(newerManifestUrl)], CancellationToken.None),
                "a verified decision that cannot advance the anti-replay floor cannot reuse an older pending update");
        }
    });

    await RunAsync("same-time conflicting signed manifests fail closed", async () =>
    {
        const string primaryManifestUrl = "https://linjie.space/TabLink/stable/manifest.json";
        const string fallbackManifestUrl = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json";
        var published = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = Artifact("windows-x64", "0.9.2", 23, "https://linjie.space/TabLink/stable/0.9.2.zip", 10, new string('A', 64));
        var second = Artifact("windows-x64", "0.9.2", 23, "https://github.com/linjierd/TabLink/releases/download/0.9.2/TabLink.zip", 10, new string('B', 64));
        var handler = new MultiSourceFixtureHandler();
        handler.Add(primaryManifestUrl, Sign(Payload([first], published, "stable-0.9.2-conflict")));
        handler.Add(fallbackManifestUrl, Sign(Payload([second], published, "stable-0.9.2-conflict")));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "conflict-cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "pending-windows.json"), "stale pending marker");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        await RejectAsync<UpdatePolicyBlockedException>(() => client.CheckAndDownloadAsync([new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None), "same-time hash conflict must not select either source");
        Check(handler.Requests.All(url => !url.EndsWith(".zip", StringComparison.Ordinal)), "conflicting manifests make no package request");
        Check(!File.Exists(Path.Combine(cache, "pending-windows.json")), "conflict clears cached authorization so callers cannot reuse an older pending update");
    });

    await RunAsync("a signed source conflict persists a blocking floor when old pending metadata cannot be deleted", async () =>
    {
        const string oldManifestUrl = "https://updates.example.test/conflict-floor/old-manifest.json";
        const string oldPackageUrl = "https://updates.example.test/conflict-floor/old-windows.zip";
        const string primaryManifestUrl = "https://linjie.space/TabLink/stable/manifest.json";
        const string fallbackManifestUrl = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json";
        var oldPackage = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("previously authorized update")));
        var oldPublished = DateTimeOffset.UtcNow.AddMinutes(-3);
        var oldArtifact = Artifact("windows-x64", "0.9.0", 21, oldPackageUrl, oldPackage.Length,
            Convert.ToHexString(SHA256.HashData(oldPackage)));
        var cache = Path.Combine(root, "persistent-conflict-floor-cache");
        var initialHandler = new MultiSourceFixtureHandler();
        initialHandler.Add(oldManifestUrl, Sign(Payload([oldArtifact], oldPublished, "stable-0.9.0-old-pending")));
        initialHandler.Add(oldPackageUrl, oldPackage);
        using (var initialHttp = new HttpClient(initialHandler) { Timeout = Timeout.InfiniteTimeSpan })
        {
            var initialClient = new UpdateClient(initialHttp, publicKey, cache,
                StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
            Check(await initialClient.CheckAndDownloadAsync([new Uri(oldManifestUrl)], CancellationToken.None) is not null,
                "fixture creates an older valid cached pending update");
        }

        var pendingPath = Path.Combine(cache, "pending-windows.json");
        File.SetAttributes(pendingPath, File.GetAttributes(pendingPath) | FileAttributes.ReadOnly);
        try
        {
            var conflictPublished = oldPublished.AddMinutes(1);
            var first = Artifact("windows-x64", "0.9.1", 22,
                "https://linjie.space/TabLink/stable/0.9.1.zip", 10, new string('A', 64));
            var second = Artifact("windows-x64", "0.9.1", 22,
                "https://github.com/linjierd/TabLink/releases/download/0.9.1/TabLink.zip", 10, new string('B', 64));
            var conflictHandler = new MultiSourceFixtureHandler();
            conflictHandler.Add(primaryManifestUrl, Sign(Payload([first], conflictPublished, "stable-0.9.1-conflict-floor")));
            conflictHandler.Add(fallbackManifestUrl, Sign(Payload([second], conflictPublished, "stable-0.9.1-conflict-floor")));
            using var conflictHttp = new HttpClient(conflictHandler) { Timeout = Timeout.InfiniteTimeSpan };
            var conflictClient = new UpdateClient(conflictHttp, publicKey, cache,
                StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
            await RejectAsync<UpdatePolicyBlockedException>(() => conflictClient.CheckAndDownloadAsync(
                [new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None),
                "same-time signed conflict is a fatal policy result");
            Check(File.Exists(pendingPath), "read-only fixture proves pending deletion was unavailable");
            Check(await conflictClient.TryLoadPendingAsync(CancellationToken.None) is null,
                "persisted conflict floor rejects the otherwise valid older pending update");
            var floor = JsonSerializer.Deserialize<AcceptedManifestFloor>(File.ReadAllBytes(Path.Combine(cache, "manifest-floor.json")))
                ?? throw new Exception("conflict floor was not persisted");
            Check(floor.PublishedAtUtc == conflictPublished.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                "conflict floor advances to the conflicting signed publication time");
        }
        finally
        {
            if (File.Exists(pendingPath)) File.SetAttributes(pendingPath, FileAttributes.Normal);
        }
    });

    await RunAsync("equivalent signed manifests provide hash-checked package mirrors and cached packages are revalidated", async () =>
    {
        const string primaryManifestUrl = "https://linjie.space/TabLink/stable/manifest.json";
        const string fallbackManifestUrl = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json";
        const string primaryPackageUrl = "https://linjie.space/TabLink/stable/0.9.3.zip";
        const string fallbackPackageUrl = "https://github.com/linjierd/TabLink/releases/download/0.9.3/TabLink-windows-x64.zip";
        var package = CreateZip(("TabLink.exe", Encoding.UTF8.GetBytes("verified mirror")));
        var corruptPackage = package.ToArray();
        corruptPackage[^1] ^= 0x5A;
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var published = DateTimeOffset.UtcNow.AddMinutes(-1);
        var primaryArtifact = Artifact("windows-x64", "0.9.3", 24, primaryPackageUrl, package.Length, hash) with { Notes = "same signed release" };
        var fallbackArtifact = Artifact("windows-x64", "0.9.3", 24, fallbackPackageUrl, package.Length, hash) with { Notes = "same signed release" };
        var handler = new MultiSourceFixtureHandler();
        handler.Add(primaryManifestUrl, Sign(Payload([primaryArtifact], published, "stable-0.9.3-mirrors")));
        handler.Add(fallbackManifestUrl, Sign(Payload([fallbackArtifact], published, "stable-0.9.3-mirrors")));
        handler.Add(primaryPackageUrl, corruptPackage);
        handler.Add(fallbackPackageUrl, package);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var cache = Path.Combine(root, "mirror-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        var ready = await client.CheckAndDownloadAsync([new Uri(primaryManifestUrl), new Uri(fallbackManifestUrl)], CancellationToken.None) ?? throw new Exception("mirror fixture did not download");
        Check(handler.Requests.Contains(primaryPackageUrl) && handler.Requests.Contains(fallbackPackageUrl), "hash failure on the primary package advances to the signed mirror");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ready.PackagePath))) == hash, "downloaded mirror still matches signed size and SHA-256");
        File.WriteAllBytes(ready.PackagePath, corruptPackage);
        Check(await client.TryLoadPendingAsync(CancellationToken.None) is null, "cached pending package is revalidated before reuse");
    });

    Console.WriteLine($"TabLink update tests passed: {scenarios} scenarios, {assertions} assertions.");
    return 0;
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

void VerifyPayload(byte[] payload) => UpdateManifestVerifier.VerifyAndParse(Sign(payload), publicKey);

UpdateArtifact Artifact(string platform, string version, int build, string url, long size, string hash) => new(platform, version, build, url, size, hash, null, null);

byte[] Payload(UpdateArtifact[] artifacts, DateTimeOffset? published = null, string releaseId = "stable-0.8.0", int rolloutPercentage = 100, string? publishedAtText = null)
{
    var timestamp = publishedAtText ?? (published ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    return JsonSerializer.SerializeToUtf8Bytes(new UpdateManifestPayload(1, "stable", releaseId, timestamp, rolloutPercentage, 1, artifacts));
}

byte[] Sign(byte[] payload)
{
    var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(signature)));
}

byte[] CreateZip(params (string Name, byte[] Content)[] entries)
{
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        foreach (var item in entries) { var entry = zip.CreateEntry(item.Name); using var stream = entry.Open(); stream.Write(item.Content); }
    return output.ToArray();
}

void Extract(string archive, string destination)
{
    using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None);
    SafeZipExtractor.Extract(stream, destination);
}

void Run(string name, Action action) { scenarios++; action(); Console.WriteLine("PASS " + name); }
async Task RunAsync(string name, Func<Task> action) { scenarios++; await action(); Console.WriteLine("PASS " + name); }
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception("Assertion failed: " + message); }
void Reject<T>(Action action, string message) where T : Exception { assertions++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }
async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception { assertions++; try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }

sealed class FixtureHandler : HttpMessageHandler
{
    public required byte[] Manifest { get; set; }
    public required byte[] Package { get; init; }
    public int PackageRequests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var packageRequest = request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal);
        if (packageRequest) PackageRequests++;
        var body = packageRequest ? Package : Manifest;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request };
        response.Content.Headers.ContentLength = body.Length;
        return Task.FromResult(response);
    }
}

sealed class MultiSourceFixtureHandler : HttpMessageHandler
{
    readonly Dictionary<string, (HttpStatusCode StatusCode, byte[] Body)> responses = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = [];

    public void Add(string url, byte[] body, HttpStatusCode statusCode = HttpStatusCode.OK)
        => responses.Add(url, (statusCode, body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        if (!responses.TryGetValue(url, out var fixture)) fixture = (HttpStatusCode.NotFound, []);
        var response = new HttpResponseMessage(fixture.StatusCode) { Content = new ByteArrayContent(fixture.Body), RequestMessage = request };
        response.Content.Headers.ContentLength = fixture.Body.Length;
        return Task.FromResult(response);
    }
}

sealed class StalledPrimaryFixtureHandler(
    string primaryManifestUrl,
    string fallbackManifestUrl,
    byte[] fallbackManifest,
    string packageUrl,
    byte[] package) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        if (url == primaryManifestUrl)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The stalled source unexpectedly completed.");
        }
        var body = url == fallbackManifestUrl ? fallbackManifest : url == packageUrl ? package : [];
        var status = url == fallbackManifestUrl || url == packageUrl ? HttpStatusCode.OK : HttpStatusCode.NotFound;
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body), RequestMessage = request };
        response.Content.Headers.ContentLength = body.Length;
        return response;
    }
}

sealed class CancelAtPackageEndFixtureHandler(
    string manifestUrl,
    byte[] manifest,
    string packageUrl,
    byte[] package,
    CancellationTokenSource cancellation) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        HttpContent content = url == manifestUrl
            ? new ByteArrayContent(manifest)
            : url == packageUrl
                ? new StreamContent(new CancelAtEndStream(package, cancellation))
                : new ByteArrayContent([]);
        content.Headers.ContentLength = url == manifestUrl ? manifest.Length : url == packageUrl ? package.Length : 0;
        return Task.FromResult(new HttpResponseMessage(url == manifestUrl || url == packageUrl ? HttpStatusCode.OK : HttpStatusCode.NotFound)
        { Content = content, RequestMessage = request });
    }
}

sealed class CancelAtEndStream(byte[] content, CancellationTokenSource cancellation) : Stream
{
    int offset;
    bool cancellationSent;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => offset; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        if (offset < content.Length)
        {
            var copied = Math.Min(count, content.Length - offset);
            Array.Copy(content, offset, buffer, bufferOffset, copied);
            offset += copied;
            return copied;
        }
        CancelOnce();
        return 0;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset < content.Length)
        {
            var copied = Math.Min(buffer.Length, content.Length - offset);
            content.AsMemory(offset, copied).CopyTo(buffer);
            offset += copied;
            return ValueTask.FromResult(copied);
        }
        CancelOnce();
        return ValueTask.FromResult(0);
    }

    void CancelOnce()
    {
        if (cancellationSent) return;
        cancellationSent = true;
        cancellation.Cancel();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class BlockingPackageFixtureHandler(
    string manifestUrl,
    byte[] manifest,
    string packageUrl,
    byte[] package) : HttpMessageHandler
{
    public TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        HttpContent content = url == manifestUrl
            ? new ByteArrayContent(manifest)
            : url == packageUrl
                ? new StreamContent(new BlockAfterFirstReadStream(package, SecondReadStarted, CancellationObserved))
                : new ByteArrayContent([]);
        content.Headers.ContentLength = url == manifestUrl ? manifest.Length : url == packageUrl ? package.Length : 0;
        return Task.FromResult(new HttpResponseMessage(url == manifestUrl || url == packageUrl ? HttpStatusCode.OK : HttpStatusCode.NotFound)
        { Content = content, RequestMessage = request });
    }
}

sealed class BlockAfterFirstReadStream(
    byte[] content,
    TaskCompletionSource secondReadStarted,
    TaskCompletionSource cancellationObserved) : Stream
{
    int offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => offset; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int bufferOffset, int count) =>
        throw new NotSupportedException("The fixture requires asynchronous reads.");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset == 0)
        {
            var copied = Math.Min(buffer.Length, Math.Min(content.Length, 128 * 1024));
            content.AsMemory(0, copied).CopyTo(buffer);
            offset = copied;
            return copied;
        }
        secondReadStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            cancellationObserved.TrySetResult();
            throw;
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
