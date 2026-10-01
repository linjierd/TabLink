using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TabLink.CompatibilityCatalog;

public static partial class CompatibilityCatalogValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ResultValues = ToSet(CompatibilityCatalogContract.Results);
    private static readonly HashSet<string> SourceValues = ToSet(CompatibilityCatalogContract.Sources);
    private static readonly HashSet<string> ChannelValues = ToSet(CompatibilityCatalogContract.Channels);
    private static readonly HashSet<string> ArchitectureValues = ToSet(CompatibilityCatalogContract.Architectures);
    private static readonly HashSet<string> PlatformValues = ToSet(CompatibilityCatalogContract.ReceiverPlatforms);
    private static readonly HashSet<string> ClientValues = ToSet(CompatibilityCatalogContract.ReceiverClients);
    private static readonly HashSet<string> ConnectionValues = ToSet(CompatibilityCatalogContract.Connections);
    private static readonly HashSet<string> CodecValues = ToSet(CompatibilityCatalogContract.Codecs);
    private static readonly HashSet<string> EncoderValues = ToSet(CompatibilityCatalogContract.Encoders);
    private static readonly HashSet<string> DecoderKindValues = ToSet(CompatibilityCatalogContract.DecoderKinds);
    private static readonly HashSet<string> PhysicalPresentationMethodValues =
        ToSet(CompatibilityCatalogContract.PhysicalPresentationMethods);
    private static readonly HashSet<string> FeatureValues = ToSet(CompatibilityCatalogContract.VerifiedFeatures);
    private static readonly HashSet<string> LimitationValues = ToSet(CompatibilityCatalogContract.Limitations);

    public static CompatibilityCatalogDocument ParseAndValidate(byte[] utf8Json, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (utf8Json.Length == 0 || utf8Json.Length > CompatibilityCatalogContract.MaximumCatalogBytes)
            throw Rule("$", "must be a non-empty UTF-8 document within the size limit");
        if (utf8Json.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw Rule("$", "must use UTF-8 without a byte-order mark");
        try
        {
            _ = StrictUtf8.GetString(utf8Json);
        }
        catch (DecoderFallbackException exception)
        {
            throw Rule("$", "must contain valid UTF-8", exception);
        }

        string root;
        try
        {
            root = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("Repository root is unavailable.", exception);
        }
        if (!Directory.Exists(root))
            throw new InvalidDataException("Repository root is unavailable.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException exception)
        {
            throw Rule("$", "must contain one valid strict JSON document", exception);
        }

        using (document)
        {
            var rootObject = ReadObject(document.RootElement, "$", ["schemaVersion", "reports"]);
            var schemaVersion = ReadInteger(rootObject, "schemaVersion", "$", 1, 1);
            var reportsElement = rootObject["reports"];
            if (reportsElement.ValueKind != JsonValueKind.Array || reportsElement.GetArrayLength() == 0)
                throw Rule("$.reports", "must be a non-empty array");
            if (reportsElement.GetArrayLength() > 1000)
                throw Rule("$.reports", "must contain no more than 1000 records");

            var reports = new List<CompatibilityReport>(reportsElement.GetArrayLength());
            string? previousId = null;
            var index = 0;
            foreach (var reportElement in reportsElement.EnumerateArray())
            {
                var path = $"$.reports[{index}]";
                var report = ReadReport(reportElement, path, root);
                if (previousId is not null && string.CompareOrdinal(previousId, report.Id) >= 0)
                    throw Rule(path + ".id", "must be unique and records must be sorted by id using ordinal order");
                previousId = report.Id;
                reports.Add(report);
                index++;
            }

            return new CompatibilityCatalogDocument(schemaVersion, reports.AsReadOnly());
        }
    }

    private static CompatibilityReport ReadReport(JsonElement element, string path, string repositoryRoot)
    {
        var value = ReadObject(element, path,
        [
            "id", "result", "verifiedOn", "source", "tabLink", "host", "receiver", "connection",
            "display", "video", "verifiedFeatures", "limitations", "evidence"
        ]);

        var id = ReadString(value, "id", path);
        if (!IdRegex().IsMatch(id))
            throw Rule(path + ".id", "must be a neutral six-digit catalog id");

        var result = ReadEnum(value, "result", path, ResultValues);
        var verifiedOnText = ReadString(value, "verifiedOn", path);
        if (!DateOnly.TryParseExact(verifiedOnText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var verifiedOn))
            throw Rule(path + ".verifiedOn", "must use yyyy-MM-dd");
        var source = ReadEnum(value, "source", path, SourceValues);
        var tabLink = ReadTabLink(value["tabLink"], path + ".tabLink");
        var host = ReadHost(value["host"], path + ".host");
        var receiver = ReadReceiver(value["receiver"], path + ".receiver");
        var connection = ReadEnum(value, "connection", path, ConnectionValues);
        var display = ReadDisplay(value["display"], path + ".display");
        var video = ReadVideo(value["video"], path + ".video");
        var verifiedFeatures = ReadSortedEnumArray(
            value["verifiedFeatures"], path + ".verifiedFeatures", FeatureValues, requireNonEmpty: true);
        var limitations = ReadSortedEnumArray(
            value["limitations"], path + ".limitations", LimitationValues, requireNonEmpty: false);
        var evidence = ReadEvidence(value["evidence"], path + ".evidence", tabLink.Version, repositoryRoot);

        var report = new CompatibilityReport(id, result, verifiedOn, source, tabLink, host, receiver, connection,
            display, video, verifiedFeatures, limitations, evidence);
        ValidateCrossFields(report, path);
        return report;
    }

    private static TabLinkRelease ReadTabLink(JsonElement element, string path)
    {
        var value = ReadObject(element, path, ["version", "channel", "releaseTag"]);
        var version = ReadString(value, "version", path);
        if (!VersionRegex().IsMatch(version))
            throw Rule(path + ".version", "must be a canonical three-part numeric version");
        var channel = ReadEnum(value, "channel", path, ChannelValues);
        var releaseTag = ReadString(value, "releaseTag", path);
        if (releaseTag.Length > 64)
            throw Rule(path + ".releaseTag", "must be no longer than 64 characters");
        var validTag = channel switch
        {
            "stable" => string.Equals(releaseTag, "v" + version, StringComparison.Ordinal),
            "preview" => PreviewTagRegex().IsMatch(releaseTag) &&
                         releaseTag.StartsWith("v" + version + "-preview.", StringComparison.Ordinal),
            "development" => string.Equals(releaseTag, "main", StringComparison.Ordinal),
            _ => false
        };
        if (!validTag)
            throw Rule(path + ".releaseTag", "must match the selected channel and version");
        return new TabLinkRelease(version, channel, releaseTag);
    }

    private static HostConfiguration ReadHost(JsonElement element, string path)
    {
        var value = ReadObject(element, path,
            ["operatingSystem", "architecture", "gpuVendor", "gpuModel"]);
        return new HostConfiguration(
            ReadPublicLabel(value, "operatingSystem", path),
            ReadEnum(value, "architecture", path, ArchitectureValues),
            ReadPublicLabel(value, "gpuVendor", path),
            ReadPublicLabel(value, "gpuModel", path));
    }

    private static ReceiverConfiguration ReadReceiver(JsonElement element, string path)
    {
        var value = ReadObject(element, path,
            ["platform", "client", "manufacturer", "model", "operatingSystem", "decoder"]);
        return new ReceiverConfiguration(
            ReadEnum(value, "platform", path, PlatformValues),
            ReadEnum(value, "client", path, ClientValues),
            ReadPublicLabel(value, "manufacturer", path),
            ReadPublicLabel(value, "model", path),
            ReadPublicLabel(value, "operatingSystem", path),
            ReadPublicLabel(value, "decoder", path));
    }

    private static DisplayConfiguration ReadDisplay(JsonElement element, string path)
    {
        var value = ReadObject(element, path,
        [
            "logicalWidth", "logicalHeight", "nativeWidth", "nativeHeight", "rotationQuarterTurns",
            "activeRefreshHz", "requestedRefreshHz", "supportedRefreshHz"
        ]);
        var active = ReadInteger(value, "activeRefreshHz", path, 1, 480);
        var requested = ReadInteger(value, "requestedRefreshHz", path, 1, 480);
        var supported = ReadSortedIntegerArray(value["supportedRefreshHz"], path + ".supportedRefreshHz", 1, 480);
        if (!supported.Contains(active) || !supported.Contains(requested))
            throw Rule(path, "active and requested refresh rates must appear in supportedRefreshHz");
        return new DisplayConfiguration(
            ReadInteger(value, "logicalWidth", path, 320, 16384),
            ReadInteger(value, "logicalHeight", path, 320, 16384),
            ReadInteger(value, "nativeWidth", path, 320, 16384),
            ReadInteger(value, "nativeHeight", path, 320, 16384),
            ReadInteger(value, "rotationQuarterTurns", path, 0, 3),
            active,
            requested,
            supported);
    }

    private static VideoConfiguration ReadVideo(JsonElement element, string path)
    {
        var value = ReadObject(element, path,
        [
            "codec", "encoder", "decoder", "requestedFps", "effectiveFps", "submittedFps",
            "presentationCallbackFps"
        ], ["physicalPresentationFps", "physicalPresentationMethod"]);
        var hasPhysicalFps = value.ContainsKey("physicalPresentationFps");
        var hasPhysicalMethod = value.ContainsKey("physicalPresentationMethod");
        if (hasPhysicalFps != hasPhysicalMethod)
            throw Rule(path, "must specify physicalPresentationFps and physicalPresentationMethod together");

        return new VideoConfiguration(
            ReadEnum(value, "codec", path, CodecValues),
            ReadEnum(value, "encoder", path, EncoderValues),
            ReadEnum(value, "decoder", path, DecoderKindValues),
            ReadDecimal(value, "requestedFps", path, 0, 480),
            ReadDecimal(value, "effectiveFps", path, 0, 480),
            ReadDecimal(value, "submittedFps", path, 0, 480),
            ReadDecimal(value, "presentationCallbackFps", path, 0, 480),
            hasPhysicalFps ? ReadDecimal(value, "physicalPresentationFps", path, 0, 480) : null,
            hasPhysicalMethod
                ? ReadEnum(value, "physicalPresentationMethod", path, PhysicalPresentationMethodValues)
                : null);
    }

    private static void ValidateCrossFields(CompatibilityReport report, string path)
    {
        var features = new HashSet<string>(report.VerifiedFeatures, StringComparer.Ordinal);
        var limitations = new HashSet<string>(report.Limitations, StringComparer.Ordinal);
        var hardwareDecoder = string.Equals(report.Video.Decoder, "hardware", StringComparison.Ordinal);
        if (features.Contains("hardware-decoding") != hardwareDecoder)
            throw Rule(path + ".verifiedFeatures",
                "must include hardware-decoding exactly when the current session used a hardware decoder");

        if (limitations.Contains("software-decode-only") &&
            !string.Equals(report.Video.Decoder, "software", StringComparison.Ordinal))
            throw Rule(path + ".limitations",
                "may include software-decode-only only when the current session used a software decoder");

        var ninetyHzSession = report.Display.ActiveRefreshHz == 90 && report.Display.RequestedRefreshHz == 90;
        if (features.Contains("ninety-hz") != ninetyHzSession)
            throw Rule(path + ".verifiedFeatures",
                "must include ninety-hz exactly when active and requested refresh rates are both 90 Hz");

        if (features.Contains("usb-no-debug") &&
            (!string.Equals(report.Connection, "usb-tethering", StringComparison.Ordinal) ||
             limitations.Contains("usb-debug-required")))
            throw Rule(path, "usb-no-debug requires USB tethering and cannot coexist with usb-debug-required");

        if (limitations.Contains("usb-debug-required") &&
            !string.Equals(report.Connection, "adb", StringComparison.Ordinal))
            throw Rule(path + ".limitations", "may include usb-debug-required only for an ADB connection");

        var browserClient = string.Equals(report.Receiver.Client, "browser", StringComparison.Ordinal);
        var browserConnection = string.Equals(report.Connection, "browser", StringComparison.Ordinal);
        var browserFeature = features.Contains("browser-mode");
        if (browserClient != browserConnection || browserClient != browserFeature)
            throw Rule(path,
                "must keep the browser client, browser connection and browser-mode feature consistent");

        if (string.Equals(report.Receiver.Client, "android-native", StringComparison.Ordinal) &&
            !string.Equals(report.Receiver.Platform, "android", StringComparison.Ordinal))
            throw Rule(path + ".receiver", "the Android native client requires the Android platform");

        if (string.Equals(report.Connection, "adb", StringComparison.Ordinal) &&
            !string.Equals(report.Receiver.Client, "android-native", StringComparison.Ordinal))
            throw Rule(path, "an ADB connection requires the Android native client");

        if (features.Contains("trusted-reconnect") && !features.Contains("trusted-registration"))
            throw Rule(path + ".verifiedFeatures", "trusted-reconnect requires trusted-registration");

        if (features.Contains("app-process-restart-reconnect") && !features.Contains("trusted-reconnect"))
            throw Rule(path + ".verifiedFeatures",
                "app-process-restart-reconnect requires trusted-reconnect");

        if ((limitations.Contains("token-replay-not-tested") ||
             limitations.Contains("active-revocation-not-tested")) &&
            !features.Contains("trusted-registration"))
            throw Rule(path + ".limitations",
                "token and revocation test limitations require trusted-registration evidence");
    }

    private static EvidenceReference ReadEvidence(
        JsonElement element,
        string path,
        string tabLinkVersion,
        string repositoryRoot)
    {
        var value = ReadObject(element, path, ["document", "sourceCommit"]);
        var document = ReadString(value, "document", path);
        if (!EvidenceDocumentRegex().IsMatch(document) ||
            !string.Equals(document, $"VERIFICATION-{tabLinkVersion}.md", StringComparison.Ordinal))
            throw Rule(path + ".document", "must name the matching root VERIFICATION-x.y.z.md file");
        var sourceCommit = ReadString(value, "sourceCommit", path);
        if (!LowerCommitRegex().IsMatch(sourceCommit))
            throw Rule(path + ".sourceCommit", "must be exactly 40 lowercase hexadecimal characters");

        var evidencePath = Path.Combine(repositoryRoot, document);
        if (!File.Exists(evidencePath))
            throw Rule(path + ".document", "must name an existing regular repository-root evidence file");
        try
        {
            if ((File.GetAttributes(evidencePath) & FileAttributes.ReparsePoint) != 0)
                throw Rule(path + ".document", "must name an existing regular repository-root evidence file");

            using var stream = new FileStream(evidencePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > 2 * 1024 * 1024)
                throw Rule(path + ".document", "must be a non-empty bounded evidence document");
            var evidenceBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(evidenceBytes);
            if (stream.ReadByte() != -1)
                throw Rule(path + ".document", "must remain stable while it is being verified");
            string evidenceText;
            try
            {
                evidenceText = StrictUtf8.GetString(evidenceBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw Rule(path + ".document", "must contain valid UTF-8", exception);
            }
            if (!evidenceText.Contains(sourceCommit, StringComparison.Ordinal))
                throw Rule(path + ".sourceCommit", "must be declared by the referenced public evidence document");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Rule(path + ".document", "must name an accessible regular repository-root evidence file", exception);
        }

        return new EvidenceReference(document, sourceCommit);
    }

    private static Dictionary<string, JsonElement> ReadObject(
        JsonElement element,
        string path,
        IReadOnlyList<string> required,
        IReadOnlyList<string>? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Rule(path, "must be a JSON object");
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        if (optional is not null)
            allowed.UnionWith(optional);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Rule(path, "contains an unsupported property");
            if (!properties.TryAdd(property.Name, property.Value))
                throw Rule(path, "contains a duplicate JSON property");
        }
        foreach (var property in required)
        {
            if (!properties.ContainsKey(property))
                throw Rule(path, $"is missing the required {property} property");
        }
        return properties;
    }

    private static string ReadString(Dictionary<string, JsonElement> value, string name, string parentPath)
    {
        var element = value[name];
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw Rule(parentPath + "." + name, "must be a non-empty string");
        return element.GetString()!;
    }

    private static string ReadPublicLabel(Dictionary<string, JsonElement> value, string name, string parentPath)
    {
        var path = parentPath + "." + name;
        var text = ReadString(value, name, parentPath);
        if (text.Length > 80 || !text.IsNormalized(NormalizationForm.FormC) ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal) || text.Contains("  ", StringComparison.Ordinal) ||
            text.Contains("..", StringComparison.Ordinal) || !text.All(IsPublicLabelCharacter) ||
            !char.IsLetterOrDigit(text[0]) || !(char.IsLetterOrDigit(text[^1]) || text[^1] == ')') ||
            HasSensitiveIdentifier(text))
            throw Rule(path, "must be a short normalized public label without private identifiers or markup");
        return text;
    }

    private static bool IsPublicLabelCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is ' ' or '.' or '_' or '+' or '(' or ')' or '-';

    private static string ReadEnum(
        Dictionary<string, JsonElement> value,
        string name,
        string parentPath,
        HashSet<string> allowed)
    {
        var result = ReadString(value, name, parentPath);
        if (!allowed.Contains(result))
            throw Rule(parentPath + "." + name, "must use a supported controlled value");
        return result;
    }

    private static int ReadInteger(
        Dictionary<string, JsonElement> value,
        string name,
        string parentPath,
        int minimum,
        int maximum)
    {
        var element = value[name];
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var result) ||
            result < minimum || result > maximum)
            throw Rule(parentPath + "." + name, "must be an integer within the allowed range");
        return result;
    }

    private static decimal ReadDecimal(
        Dictionary<string, JsonElement> value,
        string name,
        string parentPath,
        decimal minimum,
        decimal maximum)
    {
        var element = value[name];
        if (element.ValueKind != JsonValueKind.Number ||
            !DecimalNumberRegex().IsMatch(element.GetRawText()) ||
            !element.TryGetDecimal(out var result) || result < minimum || result > maximum)
            throw Rule(parentPath + "." + name, "must be a non-negative decimal with at most three fractional digits in range");
        return result;
    }

    private static IReadOnlyList<int> ReadSortedIntegerArray(
        JsonElement element,
        string path,
        int minimum,
        int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0 || element.GetArrayLength() > 16)
            throw Rule(path, "must be a non-empty bounded array");
        var result = new List<int>(element.GetArrayLength());
        int? previous = null;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) ||
                number < minimum || number > maximum)
                throw Rule(path, "must contain only integers within the allowed range");
            if (previous is not null && previous.Value >= number)
                throw Rule(path, "must be unique and sorted in ascending numeric order");
            previous = number;
            result.Add(number);
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> ReadSortedEnumArray(
        JsonElement element,
        string path,
        HashSet<string> allowed,
        bool requireNonEmpty)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > allowed.Count ||
            (requireNonEmpty && element.GetArrayLength() == 0))
            throw Rule(path, requireNonEmpty ? "must be a non-empty bounded array" : "must be a bounded array");
        var result = new List<string>(element.GetArrayLength());
        string? previous = null;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !allowed.Contains(item.GetString()!))
                throw Rule(path, "must contain only supported controlled values");
            var text = item.GetString()!;
            if (previous is not null && string.CompareOrdinal(previous, text) >= 0)
                throw Rule(path, "must be unique and sorted using ordinal order");
            previous = text;
            result.Add(text);
        }
        return result.AsReadOnly();
    }

    private static bool HasSensitiveIdentifier(string value) =>
        UrlRegex().IsMatch(value) || Ipv4Regex().IsMatch(value) || DottedDecimalRegex().IsMatch(value) ||
        MacRegex().IsMatch(value) || SeparatedEui48Regex().IsMatch(value) ||
        CiscoMacRegex().IsMatch(value) || Eui48Regex().IsMatch(value) ||
        UsbIdentifierRegex().IsMatch(value) || UuidRegex().IsMatch(value) || LongDigitsRegex().IsMatch(value) ||
        LongHexRegex().IsMatch(value) || TokenLikeRegex().IsMatch(value) || value.Contains('@');

    private static HashSet<string> ToSet(IEnumerable<string> values) => new(values, StringComparer.Ordinal);

    private static InvalidDataException Rule(string path, string rule, Exception? inner = null) =>
        new($"Compatibility catalog field {path} {rule}.", inner);

    [GeneratedRegex("^tlc-[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)-preview\\.[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PreviewTagRegex();

    [GeneratedRegex("^VERIFICATION-(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceDocumentRegex();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerCommitRegex();

    [GeneratedRegex("^(0|[1-9][0-9]*)(?:\\.[0-9]{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalNumberRegex();

    [GeneratedRegex("(?:https?://|www\\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("(?<![0-9])(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})(?:\\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})){3}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex("(?<![0-9])(?:[0-9]{1,3}\\.){3}[0-9]{1,3}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedDecimalRegex();

    [GeneratedRegex("(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex MacRegex();

    [GeneratedRegex("(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[.\\s]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedEui48Regex();

    [GeneratedRegex("(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{4}\\.){2}[0-9A-Fa-f]{4}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex CiscoMacRegex();

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Eui48Regex();

    [GeneratedRegex("(?<![A-Za-z0-9])(?:vid|pid)[\\s_:-]*[0-9a-f]{4}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdentifierRegex();

    [GeneratedRegex("(?i)(?<![0-9a-f])[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex UuidRegex();

    [GeneratedRegex("(?<![0-9])[0-9]{8,}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigitsRegex();

    [GeneratedRegex("(?i)(?<![0-9a-f])[0-9a-f]{16,}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex LongHexRegex();

    [GeneratedRegex("(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{24,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex TokenLikeRegex();
}
