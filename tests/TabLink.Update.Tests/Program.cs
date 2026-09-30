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
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, false, false, false, false) == AutomaticUpdateApplyDisposition.ScheduleWhenIdle, "idle app schedules automatic install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, true, false, false, false) == AutomaticUpdateApplyDisposition.DeferredForActivity, "active display defers install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, false, true, false, false) == AutomaticUpdateApplyDisposition.DeferredForActivity, "in-progress operation defers install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, false, false, true, false) == AutomaticUpdateApplyDisposition.DeferredForActivity, "display cleanup defers install");
        Check(AutomaticUpdateApplyPolicy.Evaluate(false, false, false, false, false) == AutomaticUpdateApplyDisposition.None, "no update does nothing");
        Check(AutomaticUpdateApplyPolicy.Evaluate(true, false, false, false, true) == AutomaticUpdateApplyDisposition.None, "closing app does not schedule twice");
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
        var available = Sign(Payload([Artifact("windows-x64", "0.8.0", 17, "https://updates.example.test/windows.zip", package.Length, hash)]));
        var withdrawn = Sign(Payload([Artifact("android", "0.8.0", 17, "https://updates.example.test/android.apk", 7, new string('4', 64))], releaseId: "stable-0.8.0-withdrawn"));
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
        var handler = new FixtureHandler { Manifest = Sign(Payload([artifact], releaseId: "stable-pause-test")), Package = package };
        using var http = new HttpClient(handler);
        var cache = Path.Combine(root, "paused-cache");
        var client = new UpdateClient(http, publicKey, cache, StableSemanticVersion.Parse("0.7.3"), "0123456789abcdef0123456789abcdef");
        Check(await client.CheckAndDownloadAsync(new Uri("https://updates.example.test/stable.json"), CancellationToken.None) is not null, "active rollout downloads once");
        var requestsBeforePause = handler.PackageRequests;
        handler.Manifest = Sign(Payload([artifact], releaseId: "stable-pause-test", rolloutPercentage: 0));
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
