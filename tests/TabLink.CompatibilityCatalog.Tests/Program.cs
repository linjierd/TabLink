using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TabLink.CompatibilityCatalog;

var assertions = 0;
var scenarios = 0;
var testParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-artifacts"));
var root = Path.Combine(testParent, "compatibility-catalog-" + Guid.NewGuid().ToString("N"));
const string sourceCommit = "7c20a72c5d77cd454a4115d4a763d668f002f329";
Directory.CreateDirectory(Path.Combine(root, "compatibility"));
Directory.CreateDirectory(Path.Combine(root, "docs"));
File.WriteAllText(Path.Combine(root, "docs", "VERIFICATION-0.8.8.md"),
    "# Public verification\n\nSource commit: `" + sourceCommit + "`\n", new UTF8Encoding(false));

try
{
    Run("valid curated record", () =>
    {
        var catalog = Validate(CreateCatalogBytes());
        Check(catalog.SchemaVersion == 1, "schema version parsed");
        Check(catalog.Reports.Count == 1, "one report parsed");
        var report = catalog.Reports[0];
        Check(report.Id == "tlc-000001", "public record id parsed");
        Check(report.Receiver.Model == "W202DS", "public model parsed");
        Check(report.Display.SupportedRefreshHz.SequenceEqual([60, 90]), "refresh rates parsed");
        Check(report.Receiver.Client == "android-native", "receiver client parsed");
        Check(report.Video.PresentationCallbackFps == 90.0m, "presentation callback frame rate parsed");
        Check(report.Video.PhysicalPresentationFps is null && report.Video.PhysicalPresentationMethod is null,
            "unmeasured physical presentation remains absent");
        Check(report.Evidence.SourceCommit == sourceCommit, "evidence commit parsed");
    });

    Run("unknown and duplicate JSON properties are rejected without reflection", () =>
    {
        var unknown = CreateCatalogNode();
        unknown["privateSerial_000000000000"] = "secret";
        var unknownError = Reject<InvalidDataException>(() => Validate(ToBytes(unknown)), "unknown property rejected");
        Check(!unknownError.Message.Contains("privateSerial", StringComparison.Ordinal),
            "unknown property name is not reflected");

        var json = Encoding.UTF8.GetString(CreateCatalogBytes());
        var duplicateRoot = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Reject<InvalidDataException>(() => Validate(Encoding.UTF8.GetBytes(duplicateRoot)), "duplicate root property rejected");
        var duplicateNested = json.Replace("\"model\":\"W202DS\"", "\"model\":\"W202DS\",\"model\":\"Other\"", StringComparison.Ordinal);
        Reject<InvalidDataException>(() => Validate(Encoding.UTF8.GetBytes(duplicateNested)), "duplicate nested property rejected");
    });

    Run("private identifiers and transport data are rejected", () =>
    {
        foreach (var privateValue in new[]
                 {
                     "000000000000",
                     "192.168.1.2",
                     "192.168.001.002",
                     "999.999.999.999",
                     "AA:BB:CC:DD:EE:FF",
                     "AA-BB-CC-DD-EE-FF",
                     "AA.BB.CC.DD.EE.FF",
                     "AA BB CC DD EE FF",
                     "aabb.ccdd.eeff",
                     "AABBCCDDEEFF",
                     "VID_ABCD",
                     "VID ABCD",
                     "VID-ABCD",
                     "PID_1234",
                     @"C:\Users\Private",
                     "https://private.example/device",
                     "abcdef0123456789",
                     "abcdefghijklmnopqrstuvwx"
                 })
        {
            var catalog = CreateCatalogNode();
            Receiver(catalog)["model"] = privateValue;
            var error = Reject<InvalidDataException>(() => Validate(ToBytes(catalog)), "private value rejected");
            Check(!error.Message.Contains(privateValue, StringComparison.Ordinal), "private value is not reflected");
        }

        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(CreateCatalogBytes()).ToArray();
        Reject<InvalidDataException>(() => Validate(bom), "UTF-8 BOM rejected");
    });

    Run("controlled values and public labels are strict", () =>
    {
        var badEnum = CreateCatalogNode();
        Report(badEnum)["connection"] = "serial-port";
        Reject<InvalidDataException>(() => Validate(ToBytes(badEnum)), "unknown enum rejected");

        var badMarkup = CreateCatalogNode();
        Receiver(badMarkup)["manufacturer"] = "<ZTE>";
        Reject<InvalidDataException>(() => Validate(ToBytes(badMarkup)), "markup label rejected");

        var longLabel = CreateCatalogNode();
        Host(longLabel)["gpuModel"] = string.Join(' ', Enumerable.Repeat("GPU", 28));
        Reject<InvalidDataException>(() => Validate(ToBytes(longLabel)), "overlong public label rejected");

        var nonCanonicalVersion = CreateCatalogNode();
        TabLink(nonCanonicalVersion)["version"] = "00.8.8";
        Reject<InvalidDataException>(() => Validate(ToBytes(nonCanonicalVersion)), "non-canonical version rejected");

        var deviceNamedId = CreateCatalogNode();
        Report(deviceNamedId)["id"] = "zte-w202ds";
        Reject<InvalidDataException>(() => Validate(ToBytes(deviceNamedId)), "device-derived record id rejected");

        var longDevelopmentTag = CreateCatalogNode();
        TabLink(longDevelopmentTag)["channel"] = "development";
        TabLink(longDevelopmentTag)["releaseTag"] = new string('m', 85);
        Reject<InvalidDataException>(() => Validate(ToBytes(longDevelopmentTag)),
            "overlong development release tag rejected");

        var longPreviewTag = CreateCatalogNode();
        TabLink(longPreviewTag)["releaseTag"] = "v0.8.8-preview." + new string('1', 70);
        Reject<InvalidDataException>(() => Validate(ToBytes(longPreviewTag)),
            "overlong preview release tag rejected");
    });

    Run("records and arrays require deterministic sorting", () =>
    {
        var reportsOutOfOrder = CreateCatalogNode();
        var first = Report(reportsOutOfOrder).DeepClone();
        first["id"] = "tlc-000002";
        var second = Report(reportsOutOfOrder).DeepClone();
        second["id"] = "tlc-000001";
        reportsOutOfOrder["reports"] = new JsonArray(first, second);
        Reject<InvalidDataException>(() => Validate(ToBytes(reportsOutOfOrder)), "report order rejected");

        var duplicateReports = CreateCatalogNode();
        var duplicated = Report(duplicateReports).DeepClone();
        duplicateReports["reports"] = new JsonArray(duplicated, duplicated.DeepClone());
        Reject<InvalidDataException>(() => Validate(ToBytes(duplicateReports)), "duplicate report id rejected");

        var featureOrder = CreateCatalogNode();
        Report(featureOrder)["verifiedFeatures"] = new JsonArray("trusted-registration", "extended-desktop");
        Reject<InvalidDataException>(() => Validate(ToBytes(featureOrder)), "feature order rejected");

        var duplicateLimitation = CreateCatalogNode();
        Report(duplicateLimitation)["limitations"] = new JsonArray(
            "route-migration-not-tested", "route-migration-not-tested");
        Reject<InvalidDataException>(() => Validate(ToBytes(duplicateLimitation)), "duplicate limitation rejected");

        var refreshOrder = CreateCatalogNode();
        Display(refreshOrder)["supportedRefreshHz"] = new JsonArray(90, 60);
        Reject<InvalidDataException>(() => Validate(ToBytes(refreshOrder)), "refresh order rejected");
    });

    Run("evidence stays at an approved repository path and closes the commit loop", () =>
    {
        var traversal = CreateCatalogNode();
        Evidence(traversal)["document"] = "../VERIFICATION-0.8.8.md";
        Reject<InvalidDataException>(() => Validate(ToBytes(traversal)), "evidence traversal rejected");

        var arbitraryDirectory = CreateCatalogNode();
        Evidence(arbitraryDirectory)["document"] = "archive/VERIFICATION-0.8.8.md";
        Reject<InvalidDataException>(() => Validate(ToBytes(arbitraryDirectory)),
            "unapproved evidence directory rejected");

        var missing = CreateCatalogNode();
        TabLink(missing)["version"] = "0.8.9";
        TabLink(missing)["releaseTag"] = "v0.8.9-preview.1";
        Evidence(missing)["document"] = "VERIFICATION-0.8.9.md";
        Reject<InvalidDataException>(() => Validate(ToBytes(missing)), "missing root evidence rejected");

        var uppercaseCommit = CreateCatalogNode();
        Evidence(uppercaseCommit)["sourceCommit"] = sourceCommit.ToUpperInvariant();
        Reject<InvalidDataException>(() => Validate(ToBytes(uppercaseCommit)), "uppercase commit rejected");

        var unreferencedCommit = CreateCatalogNode();
        Evidence(unreferencedCommit)["sourceCommit"] = new string('b', 40);
        Reject<InvalidDataException>(() => Validate(ToBytes(unreferencedCommit)),
            "commit absent from public evidence rejected");
    });

    Run("cross-field display and release rules are enforced", () =>
    {
        var unsupportedActive = CreateCatalogNode();
        Display(unsupportedActive)["activeRefreshHz"] = 120;
        Reject<InvalidDataException>(() => Validate(ToBytes(unsupportedActive)),
            "active rate absent from supported list rejected");

        var mismatchedTag = CreateCatalogNode();
        TabLink(mismatchedTag)["releaseTag"] = "v0.8.7-preview.1";
        Reject<InvalidDataException>(() => Validate(ToBytes(mismatchedTag)), "release tag mismatch rejected");

        var exponentJson = Encoding.UTF8.GetString(CreateCatalogBytes())
            .Replace("\"presentationCallbackFps\":90", "\"presentationCallbackFps\":9e1", StringComparison.Ordinal);
        Reject<InvalidDataException>(() => Validate(Encoding.UTF8.GetBytes(exponentJson)),
            "non-canonical exponent frame rate rejected");
    });

    Run("physical presentation evidence is an explicit optional pair", () =>
    {
        var fpsOnly = CreateCatalogNode();
        Video(fpsOnly)["physicalPresentationFps"] = 89.7;
        Reject<InvalidDataException>(() => Validate(ToBytes(fpsOnly)), "physical FPS without method rejected");

        var methodOnly = CreateCatalogNode();
        Video(methodOnly)["physicalPresentationMethod"] = "surfaceflinger";
        Reject<InvalidDataException>(() => Validate(ToBytes(methodOnly)), "physical method without FPS rejected");

        var invalidMethod = CreateCatalogNode();
        Video(invalidMethod)["physicalPresentationFps"] = 89.7;
        Video(invalidMethod)["physicalPresentationMethod"] = "estimated";
        Reject<InvalidDataException>(() => Validate(ToBytes(invalidMethod)), "uncontrolled physical method rejected");

        var measured = CreateCatalogNode();
        Video(measured)["physicalPresentationFps"] = 89.7;
        Video(measured)["physicalPresentationMethod"] = "external-camera";
        var report = Validate(ToBytes(measured)).Reports.Single();
        Check(report.Video.PhysicalPresentationFps == 89.7m, "physical presentation FPS parsed");
        Check(report.Video.PhysicalPresentationMethod == "external-camera", "physical method parsed");
        var catalog = new CompatibilityCatalogDocument(1, [report]);
        var readme = CompatibilityCatalogGenerator.GenerateReadme(catalog);
        var readmeZhCn = CompatibilityCatalogGenerator.GenerateReadmeZhCn(catalog);
        Check(readme.Contains("physical presentation 89.7 fps (`external-camera`)", StringComparison.Ordinal),
            "English physical measurement is labelled with its method");
        Check(readmeZhCn.Contains("物理呈现 89.7 fps (`external-camera`)", StringComparison.Ordinal),
            "Chinese physical measurement is labelled with its method");
    });

    Run("feature and limitation claims match the measured session", () =>
    {
        var hardwareFeatureMissing = CreateCatalogNode();
        SetFeatures(hardwareFeatureMissing,
            "app-process-restart-reconnect", "extended-desktop", "native-orientation", "ninety-hz",
            "single-display-cleanup", "trusted-reconnect", "trusted-registration");
        Reject<InvalidDataException>(() => Validate(ToBytes(hardwareFeatureMissing)),
            "hardware decoder requires hardware-decoding feature");

        var hardwareFeatureOnSoftware = CreateCatalogNode();
        Video(hardwareFeatureOnSoftware)["decoder"] = "software";
        Reject<InvalidDataException>(() => Validate(ToBytes(hardwareFeatureOnSoftware)),
            "hardware-decoding feature rejects software decoder");

        var softwareDecoderNotExclusive = CreateCatalogNode();
        Video(softwareDecoderNotExclusive)["decoder"] = "software";
        SetFeatures(softwareDecoderNotExclusive,
            "app-process-restart-reconnect", "extended-desktop", "native-orientation", "ninety-hz",
            "single-display-cleanup", "trusted-reconnect", "trusted-registration");
        Validate(ToBytes(softwareDecoderNotExclusive));
        assertions++;

        var falseSoftwareOnly = CreateCatalogNode();
        SetLimitations(falseSoftwareOnly,
            "active-revocation-not-tested", "route-migration-not-tested", "software-decode-only",
            "system-restart-not-tested", "token-replay-not-tested");
        Reject<InvalidDataException>(() => Validate(ToBytes(falseSoftwareOnly)),
            "software-decode-only rejects hardware decoder");

        var ninetyFeatureMissing = CreateCatalogNode();
        SetFeatures(ninetyFeatureMissing,
            "app-process-restart-reconnect", "extended-desktop", "hardware-decoding", "native-orientation",
            "single-display-cleanup", "trusted-reconnect", "trusted-registration");
        Reject<InvalidDataException>(() => Validate(ToBytes(ninetyFeatureMissing)),
            "90 Hz session requires ninety-hz feature");

        var falseNinetyFeature = CreateCatalogNode();
        Display(falseNinetyFeature)["activeRefreshHz"] = 60;
        Display(falseNinetyFeature)["requestedRefreshHz"] = 60;
        Reject<InvalidDataException>(() => Validate(ToBytes(falseNinetyFeature)),
            "ninety-hz feature rejects non-90 Hz session");

        var usbNoDebugWrongTransport = CreateCatalogNode();
        SetFeatures(usbNoDebugWrongTransport,
            "app-process-restart-reconnect", "extended-desktop", "hardware-decoding", "native-orientation",
            "ninety-hz", "single-display-cleanup", "trusted-reconnect", "trusted-registration", "usb-no-debug");
        Reject<InvalidDataException>(() => Validate(ToBytes(usbNoDebugWrongTransport)),
            "usb-no-debug requires USB tethering");

        var contradictoryUsbClaims = CreateCatalogNode();
        Report(contradictoryUsbClaims)["connection"] = "usb-tethering";
        SetFeatures(contradictoryUsbClaims,
            "app-process-restart-reconnect", "extended-desktop", "hardware-decoding", "native-orientation",
            "ninety-hz", "single-display-cleanup", "trusted-reconnect", "trusted-registration", "usb-no-debug");
        SetLimitations(contradictoryUsbClaims,
            "active-revocation-not-tested", "route-migration-not-tested", "system-restart-not-tested",
            "token-replay-not-tested", "usb-debug-required");
        Reject<InvalidDataException>(() => Validate(ToBytes(contradictoryUsbClaims)),
            "USB debug claims cannot coexist");

        var debugRequiredWrongTransport = CreateCatalogNode();
        SetLimitations(debugRequiredWrongTransport,
            "active-revocation-not-tested", "route-migration-not-tested", "system-restart-not-tested",
            "token-replay-not-tested", "usb-debug-required");
        Reject<InvalidDataException>(() => Validate(ToBytes(debugRequiredWrongTransport)),
            "usb-debug-required requires ADB");

        var debugRequiredAdb = CreateCatalogNode();
        Report(debugRequiredAdb)["connection"] = "adb";
        SetLimitations(debugRequiredAdb,
            "active-revocation-not-tested", "route-migration-not-tested", "system-restart-not-tested",
            "token-replay-not-tested", "usb-debug-required");
        Validate(ToBytes(debugRequiredAdb));
        assertions++;
    });

    Run("browser and trust claims form complete dependency chains", () =>
    {
        var browserClientOnly = CreateCatalogNode();
        Receiver(browserClientOnly)["platform"] = "ipados";
        Receiver(browserClientOnly)["client"] = "browser";
        Reject<InvalidDataException>(() => Validate(ToBytes(browserClientOnly)),
            "browser client without browser transport rejected");

        var browserFeatureOnly = CreateCatalogNode();
        SetFeatures(browserFeatureOnly,
            "app-process-restart-reconnect", "browser-mode", "extended-desktop", "hardware-decoding",
            "native-orientation", "ninety-hz", "single-display-cleanup", "trusted-reconnect",
            "trusted-registration");
        Reject<InvalidDataException>(() => Validate(ToBytes(browserFeatureOnly)),
            "browser feature without browser client rejected");

        var validBrowser = CreateCatalogNode();
        Receiver(validBrowser)["platform"] = "ipados";
        Receiver(validBrowser)["client"] = "browser";
        Report(validBrowser)["connection"] = "browser";
        SetFeatures(validBrowser,
            "app-process-restart-reconnect", "browser-mode", "extended-desktop", "hardware-decoding",
            "native-orientation", "ninety-hz", "single-display-cleanup", "trusted-reconnect",
            "trusted-registration");
        Validate(ToBytes(validBrowser));
        assertions++;

        var nativeOnIos = CreateCatalogNode();
        Receiver(nativeOnIos)["platform"] = "ios";
        Reject<InvalidDataException>(() => Validate(ToBytes(nativeOnIos)),
            "Android native client on iOS rejected");

        var adbBrowser = CreateCatalogNode();
        Receiver(adbBrowser)["platform"] = "android";
        Receiver(adbBrowser)["client"] = "browser";
        Report(adbBrowser)["connection"] = "adb";
        SetFeatures(adbBrowser,
            "app-process-restart-reconnect", "browser-mode", "extended-desktop", "hardware-decoding",
            "native-orientation", "ninety-hz", "single-display-cleanup", "trusted-reconnect",
            "trusted-registration");
        Reject<InvalidDataException>(() => Validate(ToBytes(adbBrowser)), "ADB browser client rejected");

        var reconnectWithoutRegistration = CreateCatalogNode();
        SetFeatures(reconnectWithoutRegistration,
            "extended-desktop", "hardware-decoding", "native-orientation", "ninety-hz",
            "single-display-cleanup", "trusted-reconnect");
        SetLimitations(reconnectWithoutRegistration, "route-migration-not-tested", "system-restart-not-tested");
        Reject<InvalidDataException>(() => Validate(ToBytes(reconnectWithoutRegistration)),
            "trusted reconnect requires registration");

        var processRestartWithoutReconnect = CreateCatalogNode();
        SetFeatures(processRestartWithoutReconnect,
            "app-process-restart-reconnect", "extended-desktop", "hardware-decoding", "native-orientation",
            "ninety-hz", "single-display-cleanup", "trusted-registration");
        Reject<InvalidDataException>(() => Validate(ToBytes(processRestartWithoutReconnect)),
            "process restart reconnect requires trusted reconnect");

        var activeLimitationWithoutRegistration = CreateCatalogNode();
        SetFeatures(activeLimitationWithoutRegistration,
            "extended-desktop", "hardware-decoding", "native-orientation", "ninety-hz",
            "single-display-cleanup");
        SetLimitations(activeLimitationWithoutRegistration,
            "active-revocation-not-tested", "route-migration-not-tested", "system-restart-not-tested");
        Reject<InvalidDataException>(() => Validate(ToBytes(activeLimitationWithoutRegistration)),
            "active revocation limitation requires registration");

        var tokenLimitationWithoutRegistration = CreateCatalogNode();
        SetFeatures(tokenLimitationWithoutRegistration,
            "extended-desktop", "hardware-decoding", "native-orientation", "ninety-hz",
            "single-display-cleanup");
        SetLimitations(tokenLimitationWithoutRegistration,
            "route-migration-not-tested", "system-restart-not-tested", "token-replay-not-tested");
        Reject<InvalidDataException>(() => Validate(ToBytes(tokenLimitationWithoutRegistration)),
            "token replay limitation requires registration");
    });

    Run("both README languages and JSON Schema generation are deterministic", () =>
    {
        var catalog = Validate(CreateCatalogBytes());
        var readmeOne = CompatibilityCatalogGenerator.GenerateReadme(catalog);
        var readmeTwo = CompatibilityCatalogGenerator.GenerateReadme(catalog);
        var readmeZhCnOne = CompatibilityCatalogGenerator.GenerateReadmeZhCn(catalog);
        var readmeZhCnTwo = CompatibilityCatalogGenerator.GenerateReadmeZhCn(catalog);
        Check(readmeOne == readmeTwo, "English README output repeats byte-for-byte");
        Check(readmeZhCnOne == readmeZhCnTwo, "Chinese README output repeats byte-for-byte");
        Check(readmeOne.EndsWith('\n') && !readmeOne.Contains('\r'), "English README uses final LF and no CR");
        Check(readmeZhCnOne.EndsWith('\n') && !readmeZhCnOne.Contains('\r'),
            "Chinese README uses final LF and no CR");
        Check(readmeOne.Contains("W202DS", StringComparison.Ordinal) &&
              readmeZhCnOne.Contains("W202DS", StringComparison.Ordinal),
            "both README languages contain the reviewed public model");
        Check(readmeOne.Contains("Each record demonstrates only the exact", StringComparison.Ordinal),
            "English README states evidence scope");
        Check(readmeZhCnOne.Contains("每条记录只证明", StringComparison.Ordinal),
            "Chinese README states evidence scope");
        Check(readmeOne.Contains("Schema v1; 1 reviewed record", StringComparison.Ordinal),
            "English README reports its schema version and record count");
        Check(readmeZhCnOne.Contains("Schema v1，当前 1 条记录", StringComparison.Ordinal),
            "Chinese README reports its schema version and record count");
        Check(readmeOne.Contains("A requested refresh rate is distinct", StringComparison.Ordinal),
            "English README separates requested refresh from measured rates");
        Check(readmeZhCnOne.Contains("请求刷新率不等于", StringComparison.Ordinal),
            "Chinese README separates requested refresh from measured rates");
        Check(readmeOne.Contains("presentation callback 90 fps", StringComparison.Ordinal),
            "English README names the callback measurement");
        Check(readmeZhCnOne.Contains("呈现回调 90 fps", StringComparison.Ordinal),
            "Chinese README names the callback measurement");
        Check(readmeOne.Contains("physical presentation —", StringComparison.Ordinal),
            "English README does not imply an unmeasured physical frame rate");
        Check(readmeZhCnOne.Contains("物理呈现 —", StringComparison.Ordinal),
            "Chinese README does not imply an unmeasured physical frame rate");
        Check(readmeOne.Contains("**English (Singapore)** | [简体中文](README.zh-CN.md)", StringComparison.Ordinal) &&
              readmeZhCnOne.Contains("[English (Singapore)](README.md) | **简体中文**", StringComparison.Ordinal),
            "both README languages provide a reciprocal language switch");
        Check(readmeOne.Contains("device serial numbers, network addresses, USB identifiers or pairing credentials",
                  StringComparison.Ordinal) &&
              readmeZhCnOne.Contains("设备序列号、网络地址、USB 标识符或配对凭据", StringComparison.Ordinal),
            "both README languages preserve the private-identifier exclusion");

        var schemaOne = CompatibilityCatalogGenerator.GenerateSchemaJson();
        var schemaTwo = CompatibilityCatalogGenerator.GenerateSchemaJson();
        Check(schemaOne == schemaTwo, "schema output repeats byte-for-byte");
        Check(schemaOne.EndsWith('\n') && !schemaOne.Contains('\r'), "schema uses final LF and no CR");
        using var schemaDocument = JsonDocument.Parse(Encoding.UTF8.GetBytes(schemaOne));
        Check(schemaDocument.RootElement.GetProperty("$defs").GetProperty("report")
            .GetProperty("additionalProperties").GetBoolean() == false, "schema rejects extra report fields");
        var videoSchema = schemaDocument.RootElement.GetProperty("$defs").GetProperty("video");
        Check(videoSchema.GetProperty("dependentRequired").GetProperty("physicalPresentationFps")[0]
            .GetString() == "physicalPresentationMethod", "schema pairs physical measurement fields");
        Check(schemaDocument.RootElement.GetProperty("$defs").GetProperty("receiver")
            .GetProperty("properties").TryGetProperty("client", out _), "schema requires an explicit receiver client");
    });

    Run("workspace check detects drift and write atomically converges", () =>
    {
        var catalogPath = Path.Combine(root, "compatibility", "catalog.json");
        var schemaPath = Path.Combine(root, "compatibility", "catalog.schema.json");
        var readmePath = Path.Combine(root, "compatibility", "README.md");
        var readmeZhCnPath = Path.Combine(root, "compatibility", "README.zh-CN.md");
        File.WriteAllBytes(catalogPath, CreateCatalogBytes());
        var workspace = new CompatibilityCatalogWorkspace(root);
        Reject<InvalidDataException>(workspace.CheckGeneratedFiles, "missing generated files rejected");

        File.WriteAllText(schemaPath, "old schema", new UTF8Encoding(false));
        File.WriteAllText(readmePath, "old readme", new UTF8Encoding(false));
        File.WriteAllText(readmeZhCnPath, "old Chinese readme", new UTF8Encoding(false));
        workspace.WriteGeneratedFiles();
        workspace.CheckGeneratedFiles();
        Check(!File.ReadAllBytes(schemaPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
            "schema is UTF-8 without BOM");
        Check(!File.ReadAllBytes(readmePath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
            "English README is UTF-8 without BOM");
        Check(!File.ReadAllBytes(readmeZhCnPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
            "Chinese README is UTF-8 without BOM");
        Check(!Directory.EnumerateFiles(Path.Combine(root, "compatibility"), ".*.tmp").Any(),
            "atomic write leaves no controlled temporary files");

        File.AppendAllText(schemaPath, "drift", new UTF8Encoding(false));
        Reject<InvalidDataException>(workspace.CheckGeneratedFiles, "schema drift rejected");
        workspace.WriteGeneratedFiles();
        File.AppendAllText(readmePath, "drift", new UTF8Encoding(false));
        Reject<InvalidDataException>(workspace.CheckGeneratedFiles, "English README drift rejected");
        workspace.WriteGeneratedFiles();
        File.AppendAllText(readmeZhCnPath, "drift", new UTF8Encoding(false));
        Reject<InvalidDataException>(workspace.CheckGeneratedFiles, "Chinese README drift rejected");
        workspace.WriteGeneratedFiles();
        workspace.CheckGeneratedFiles();

        File.WriteAllText(schemaPath, "preserve schema", new UTF8Encoding(false));
        File.WriteAllText(readmePath, "preserve readme", new UTF8Encoding(false));
        File.WriteAllText(readmeZhCnPath, "preserve Chinese readme", new UTF8Encoding(false));
        var validCatalog = File.ReadAllBytes(catalogPath);
        File.WriteAllText(catalogPath, "{}", new UTF8Encoding(false));
        Reject<InvalidDataException>(workspace.WriteGeneratedFiles, "invalid source prevents generation");
        Check(File.ReadAllText(schemaPath) == "preserve schema", "schema remains unchanged after validation failure");
        Check(File.ReadAllText(readmePath) == "preserve readme",
            "English README remains unchanged after validation failure");
        Check(File.ReadAllText(readmeZhCnPath) == "preserve Chinese readme",
            "Chinese README remains unchanged after validation failure");
        File.WriteAllBytes(catalogPath, validCatalog);
        workspace.WriteGeneratedFiles();
        workspace.CheckGeneratedFiles();
    });

    Run("workspace rejects reparse boundaries and keeps every path inside the root", () =>
    {
        var catalogPath = Path.Combine(root, "compatibility", "catalog.json");
        var schemaPath = Path.Combine(root, "compatibility", "catalog.schema.json");
        var readmePath = Path.Combine(root, "compatibility", "README.md");
        var readmeZhCnPath = Path.Combine(root, "compatibility", "README.zh-CN.md");
        File.WriteAllBytes(catalogPath, CreateCatalogBytes());
        new CompatibilityCatalogWorkspace(root).WriteGeneratedFiles();

        Reject<InvalidDataException>(
            () => _ = new CompatibilityCatalogWorkspace(root,
                new RecordingPathGuard(rejectedDirectory: root)),
            "repository root reparse boundary rejected");
        Reject<InvalidDataException>(
            () => _ = new CompatibilityCatalogWorkspace(root,
                new RecordingPathGuard(rejectedDirectory: Path.Combine(root, "compatibility"))),
            "compatibility directory reparse boundary rejected");

        var sourceGuard = new RecordingPathGuard(rejectedFile: catalogPath);
        var sourceWorkspace = new CompatibilityCatalogWorkspace(root, sourceGuard);
        Reject<InvalidDataException>(() => _ = sourceWorkspace.LoadAndValidate(),
            "catalog source reparse boundary rejected");

        var schemaBefore = File.ReadAllBytes(schemaPath);
        var schemaGuard = new RecordingPathGuard(rejectedFile: schemaPath);
        var schemaWorkspace = new CompatibilityCatalogWorkspace(root, schemaGuard);
        Reject<InvalidDataException>(schemaWorkspace.WriteGeneratedFiles, "schema target reparse boundary rejected");
        Check(File.ReadAllBytes(schemaPath).SequenceEqual(schemaBefore),
            "schema target remains unchanged after path rejection");

        var readmeBefore = File.ReadAllBytes(readmePath);
        var readmeGuard = new RecordingPathGuard(rejectedFile: readmePath);
        var readmeWorkspace = new CompatibilityCatalogWorkspace(root, readmeGuard);
        Reject<InvalidDataException>(readmeWorkspace.WriteGeneratedFiles, "README target reparse boundary rejected");
        Check(File.ReadAllBytes(readmePath).SequenceEqual(readmeBefore),
            "English README target remains unchanged after path rejection");

        var schemaBeforeChineseRejection = File.ReadAllBytes(schemaPath);
        var readmeBeforeChineseRejection = File.ReadAllBytes(readmePath);
        var readmeZhCnBefore = File.ReadAllBytes(readmeZhCnPath);
        var readmeZhCnGuard = new RecordingPathGuard(rejectedFile: readmeZhCnPath);
        var readmeZhCnWorkspace = new CompatibilityCatalogWorkspace(root, readmeZhCnGuard);
        Reject<InvalidDataException>(readmeZhCnWorkspace.WriteGeneratedFiles,
            "Chinese README target reparse boundary rejected");
        Check(File.ReadAllBytes(schemaPath).SequenceEqual(schemaBeforeChineseRejection) &&
              File.ReadAllBytes(readmePath).SequenceEqual(readmeBeforeChineseRejection) &&
              File.ReadAllBytes(readmeZhCnPath).SequenceEqual(readmeZhCnBefore),
            "all generated targets remain unchanged when the Chinese README fails preflight");

        var temporaryGuard = new RecordingPathGuard(rejectTemporaryFile: true);
        var temporaryWorkspace = new CompatibilityCatalogWorkspace(root, temporaryGuard);
        Reject<InvalidDataException>(temporaryWorkspace.WriteGeneratedFiles,
            "generated temporary reparse boundary rejected");
        Check(!Directory.EnumerateFiles(Path.Combine(root, "compatibility"), ".*.tmp").Any(),
            "rejected temporary file is cleaned up");

        var recordingGuard = new RecordingPathGuard();
        var recordingWorkspace = new CompatibilityCatalogWorkspace(root, recordingGuard);
        recordingWorkspace.WriteGeneratedFiles();
        var normalizedRootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                                   Path.DirectorySeparatorChar;
        Check(recordingGuard.ObservedPaths.All(path =>
                string.Equals(path, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFullPath(path).StartsWith(normalizedRootPrefix, StringComparison.OrdinalIgnoreCase)),
            "all inspected source, target and temporary paths stay inside the repository root");

        TryAssertRealDirectoryReparseRejected();
        TryAssertRealEvidenceDirectoryReparseRejected();
    });

    Console.WriteLine($"PASS: {scenarios} compatibility catalog scenarios, {assertions} assertions. Build-output test-artifacts fixtures only; no device, network or display operations were performed.");
}
finally
{
    var fullRoot = Path.GetFullPath(root);
    var expectedPrefix = testParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(fullRoot).StartsWith("compatibility-catalog-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing unsafe compatibility test cleanup.");
    if (Directory.Exists(fullRoot))
        Directory.Delete(fullRoot, recursive: true);
}

void Run(string name, Action action)
{
    scenarios++;
    try
    {
        action();
    }
    catch (Exception exception)
    {
        throw new InvalidOperationException("Scenario failed: " + name, exception);
    }
}

void Check(bool condition, string message)
{
    assertions++;
    if (!condition)
        throw new InvalidOperationException("Assertion failed: " + message);
}

T Reject<T>(Action action, string message) where T : Exception
{
    assertions++;
    try
    {
        action();
    }
    catch (T exception)
    {
        return exception;
    }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
}

CompatibilityCatalogDocument Validate(byte[] bytes) =>
    CompatibilityCatalogValidator.ParseAndValidate(bytes, root);

byte[] CreateCatalogBytes() => ToBytes(CreateCatalogNode());

byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = false
}));

void TryAssertRealDirectoryReparseRejected()
{
    var suffix = Guid.NewGuid().ToString("N");
    var fixtureRoot = Path.Combine(testParent, "compatibility-reparse-root-" + suffix);
    var outsideDirectory = Path.Combine(testParent, "compatibility-reparse-outside-" + suffix);
    var compatibilityLink = Path.Combine(fixtureRoot, "compatibility");
    try
    {
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(outsideDirectory);
        Directory.CreateSymbolicLink(compatibilityLink, outsideDirectory);
        Reject<InvalidDataException>(() => _ = new CompatibilityCatalogWorkspace(fixtureRoot),
            "real compatibility directory reparse point rejected");
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                      PlatformNotSupportedException or NotSupportedException)
    {
        // The injected guard assertions above cover hosts where creating a reparse point is unavailable.
    }
    finally
    {
        DeleteDirectoryLinkIfPresent(compatibilityLink);
        DeleteSafeFixtureDirectory(fixtureRoot, "compatibility-reparse-root-");
        DeleteSafeFixtureDirectory(outsideDirectory, "compatibility-reparse-outside-");
    }

    var rootLink = Path.Combine(testParent, "compatibility-root-link-" + suffix);
    try
    {
        Directory.CreateSymbolicLink(rootLink, root);
        Reject<InvalidDataException>(() => _ = new CompatibilityCatalogWorkspace(rootLink),
            "real repository root reparse point rejected");
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                      PlatformNotSupportedException or NotSupportedException)
    {
        // The injected root guard assertion remains deterministic on restricted Windows hosts.
    }
    finally
    {
        DeleteDirectoryLinkIfPresent(rootLink);
    }
}

void TryAssertRealEvidenceDirectoryReparseRejected()
{
    var suffix = Guid.NewGuid().ToString("N");
    var fixtureRoot = Path.Combine(testParent, "compatibility-evidence-root-" + suffix);
    var outsideDirectory = Path.Combine(testParent, "compatibility-evidence-outside-" + suffix);
    var docsLink = Path.Combine(fixtureRoot, "docs");
    try
    {
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(Path.Combine(outsideDirectory, "VERIFICATION-0.8.8.md"),
            "# Public verification\n\nSource commit: `" + sourceCommit + "`\n", new UTF8Encoding(false));
        Directory.CreateSymbolicLink(docsLink, outsideDirectory);
        Reject<InvalidDataException>(
            () => _ = CompatibilityCatalogValidator.ParseAndValidate(CreateCatalogBytes(), fixtureRoot),
            "real docs evidence directory reparse point rejected");
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                      PlatformNotSupportedException or NotSupportedException)
    {
        // The validator's explicit FileAttributes guard remains active on hosts that cannot create links.
    }
    finally
    {
        DeleteDirectoryLinkIfPresent(docsLink);
        DeleteSafeFixtureDirectory(fixtureRoot, "compatibility-evidence-root-");
        DeleteSafeFixtureDirectory(outsideDirectory, "compatibility-evidence-outside-");
    }
}

void DeleteDirectoryLinkIfPresent(string path)
{
    try
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("Refusing to delete a non-reparse fixture as a link.");
        Directory.Delete(path);
    }
    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
    }
}

void DeleteSafeFixtureDirectory(string path, string requiredPrefix)
{
    var fullPath = Path.GetFullPath(path);
    var expectedParent = testParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(fullPath).StartsWith(requiredPrefix, StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing unsafe reparse fixture cleanup.");
    if (Directory.Exists(fullPath))
        Directory.Delete(fullPath, recursive: true);
}

JsonObject CreateCatalogNode() => new()
{
    ["schemaVersion"] = 1,
    ["reports"] = new JsonArray
    {
        new JsonObject
        {
            ["id"] = "tlc-000001",
            ["result"] = "verified",
            ["verifiedOn"] = "2026-10-02",
            ["source"] = "maintainer-verification",
            ["tabLink"] = new JsonObject
            {
                ["version"] = "0.8.8",
                ["channel"] = "preview",
                ["releaseTag"] = "v0.8.8-preview.1"
            },
            ["host"] = new JsonObject
            {
                ["operatingSystem"] = "Windows 11",
                ["architecture"] = "x64",
                ["gpuVendor"] = "NVIDIA",
                ["gpuModel"] = "RTX 4060 Laptop GPU"
            },
            ["receiver"] = new JsonObject
            {
                ["platform"] = "android",
                ["client"] = "android-native",
                ["manufacturer"] = "ZTE",
                ["model"] = "W202DS",
                ["operatingSystem"] = "Android 13",
                ["decoder"] = "c2.unisoc.avc.decoder"
            },
            ["connection"] = "native-network",
            ["display"] = new JsonObject
            {
                ["logicalWidth"] = 1920,
                ["logicalHeight"] = 1200,
                ["nativeWidth"] = 1200,
                ["nativeHeight"] = 1920,
                ["rotationQuarterTurns"] = 1,
                ["activeRefreshHz"] = 90,
                ["requestedRefreshHz"] = 90,
                ["supportedRefreshHz"] = new JsonArray(60, 90)
            },
            ["video"] = new JsonObject
            {
                ["codec"] = "h264",
                ["encoder"] = "nvenc",
                ["decoder"] = "hardware",
                ["requestedFps"] = 90,
                ["effectiveFps"] = 90,
                ["submittedFps"] = 90.1,
                ["presentationCallbackFps"] = 90.0
            },
            ["verifiedFeatures"] = new JsonArray(
                "app-process-restart-reconnect",
                "extended-desktop",
                "hardware-decoding",
                "native-orientation",
                "ninety-hz",
                "single-display-cleanup",
                "trusted-reconnect",
                "trusted-registration"),
            ["limitations"] = new JsonArray(
                "active-revocation-not-tested",
                "route-migration-not-tested",
                "system-restart-not-tested",
                "token-replay-not-tested"),
            ["evidence"] = new JsonObject
            {
                ["document"] = "docs/VERIFICATION-0.8.8.md",
                ["sourceCommit"] = sourceCommit
            }
        }
    }
};

JsonObject Report(JsonObject rootNode) => rootNode["reports"]!.AsArray()[0]!.AsObject();
JsonObject TabLink(JsonObject rootNode) => Report(rootNode)["tabLink"]!.AsObject();
JsonObject Host(JsonObject rootNode) => Report(rootNode)["host"]!.AsObject();
JsonObject Receiver(JsonObject rootNode) => Report(rootNode)["receiver"]!.AsObject();
JsonObject Display(JsonObject rootNode) => Report(rootNode)["display"]!.AsObject();
JsonObject Video(JsonObject rootNode) => Report(rootNode)["video"]!.AsObject();
JsonObject Evidence(JsonObject rootNode) => Report(rootNode)["evidence"]!.AsObject();

void SetFeatures(JsonObject rootNode, params string[] values) =>
    Report(rootNode)["verifiedFeatures"] = ToJsonArray(values);

void SetLimitations(JsonObject rootNode, params string[] values) =>
    Report(rootNode)["limitations"] = ToJsonArray(values);

JsonArray ToJsonArray(IEnumerable<string> values)
{
    var result = new JsonArray();
    foreach (var value in values)
        result.Add(value);
    return result;
}

sealed class RecordingPathGuard(
    string? rejectedDirectory = null,
    string? rejectedFile = null,
    bool rejectTemporaryFile = false) : ICompatibilityCatalogPathGuard
{
    public List<string> ObservedPaths { get; } = [];

    public void RequireSafeDirectory(string path, bool mustExist)
    {
        var fullPath = Path.GetFullPath(path);
        ObservedPaths.Add(fullPath);
        if (SamePath(fullPath, rejectedDirectory))
            throw new InvalidDataException("Injected directory reparse boundary.");
    }

    public void RequireSafeRegularFile(string path, bool mustExist)
    {
        var fullPath = Path.GetFullPath(path);
        ObservedPaths.Add(fullPath);
        if (SamePath(fullPath, rejectedFile) ||
            (rejectTemporaryFile && Path.GetFileName(fullPath).EndsWith(".tmp", StringComparison.Ordinal)))
            throw new InvalidDataException("Injected file reparse boundary.");
    }

    private static bool SamePath(string path, string? expected) => expected is not null &&
        string.Equals(path, Path.GetFullPath(expected),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
