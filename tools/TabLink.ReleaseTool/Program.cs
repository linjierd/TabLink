using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

try
{
    return args switch
    {
        ["init-key", var privatePath, var publicPath] => InitializeKey(privatePath, publicPath),
        ["sign", var privatePath, var payloadPath, var envelopePath] => Sign(privatePath, payloadPath, envelopePath),
        ["verify", var publicPath, var envelopePath] => Verify(publicPath, envelopePath),
        _ => Usage()
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int InitializeKey(string privatePath, string publicPath)
{
    privatePath = Path.GetFullPath(privatePath);
    publicPath = Path.GetFullPath(publicPath);
    if (File.Exists(privatePath) || File.Exists(publicPath))
        throw new IOException("Refusing to replace an existing stable release key.");

    Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    WriteNew(privatePath, key.ExportPkcs8PrivateKeyPem());
    WriteNew(publicPath, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) + Environment.NewLine);
    Console.WriteLine("Created a P-256 stable release key. Back up the private key securely; never package or commit it.");
    return 0;
}

static int Sign(string privatePath, string payloadPath, string envelopePath)
{
    var payload = File.ReadAllBytes(Path.GetFullPath(payloadPath));
    using var document = ParseAndValidatePayload(payload);
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(Path.GetFullPath(privatePath)));
    EnsureP256(key);

    var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    var envelope = JsonSerializer.SerializeToUtf8Bytes(
        new SignedEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(signature)),
        new JsonSerializerOptions { WriteIndented = true });
    WriteNewBytes(Path.GetFullPath(envelopePath), envelope);
    Console.WriteLine("Signed stable manifest envelope: " + Path.GetFullPath(envelopePath));
    return 0;
}

static int Verify(string publicPath, string envelopePath)
{
    var envelopeBytes = File.ReadAllBytes(Path.GetFullPath(envelopePath));
    using var envelopeDocument = ParseJsonObject(envelopeBytes, "Signed envelope");
    RequireExactProperties(envelopeDocument.RootElement, ["payload", "signature"]);
    var payload = DecodeCanonicalBase64(RequireString(envelopeDocument.RootElement, "payload"), "payload");
    var signature = DecodeCanonicalBase64(RequireString(envelopeDocument.RootElement, "signature"), "signature");

    var publicKeyText = File.ReadAllText(Path.GetFullPath(publicPath)).Trim();
    var publicKey = DecodeCanonicalBase64(publicKeyText, "public key");
    using var key = ECDsa.Create();
    key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
    if (bytesRead != publicKey.Length)
        throw new CryptographicException("The public key contains trailing data.");
    EnsureP256(key);
    if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        throw new CryptographicException("Stable manifest signature is invalid.");

    // Do not parse fields from the untrusted payload until its signature succeeds.
    using var payloadDocument = ParseAndValidatePayload(payload);
    Console.WriteLine(Encoding.UTF8.GetString(payload));
    return 0;
}

static JsonDocument ParseAndValidatePayload(byte[] payload)
{
    var document = ParseJsonObject(payload, "Release payload");
    try
    {
        var root = document.RootElement;
        RequireExactProperties(root,
        [
            "schema", "channel", "releaseId", "publishedAtUtc", "rolloutPercentage",
            "minimumProtocolVersion", "artifacts"
        ]);

        if (RequireInt32(root, "schema") != 1)
            throw new InvalidDataException("Release payload schema must be 1.");
        if (!string.Equals(RequireString(root, "channel"), "stable", StringComparison.Ordinal))
            throw new InvalidDataException("Release payload channel must be stable.");

        var releaseId = RequireString(root, "releaseId");
        if (!Regex.IsMatch(releaseId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("releaseId contains unsupported characters or is too long.");

        var publishedAt = RequireString(root, "publishedAtUtc");
        if (!Regex.IsMatch(publishedAt, "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParseExact(publishedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
            throw new InvalidDataException("publishedAtUtc must use yyyy-MM-dd'T'HH:mm:ss'Z'.");

        var rollout = RequireInt32(root, "rolloutPercentage");
        if (rollout is < 0 or > 100)
            throw new InvalidDataException("rolloutPercentage must be between 0 and 100.");
        if (RequireInt32(root, "minimumProtocolVersion") < 1)
            throw new InvalidDataException("minimumProtocolVersion must be positive.");

        var artifacts = root.GetProperty("artifacts");
        if (artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() == 0)
            throw new InvalidDataException("artifacts must be a non-empty array.");

        var platforms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts.EnumerateArray())
        {
            if (artifact.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each artifact must be a JSON object.");
            RequireKnownProperties(
                artifact,
                ["platform", "version", "build", "url", "size", "sha256"],
                ["installerUrl", "notes"]);

            var platform = RequireString(artifact, "platform");
            if (platform is not ("windows-x64" or "android" or "ios" or "harmony"))
                throw new InvalidDataException($"Unsupported artifact platform: {platform}");
            if (!platforms.Add(platform))
                throw new InvalidDataException($"Duplicate artifact platform: {platform}");

            var version = RequireString(artifact, "version");
            if (!Regex.IsMatch(version, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant))
                throw new InvalidDataException($"Artifact version is not a stable SemVer: {version}");
            if (RequireInt64(artifact, "build") < 1)
                throw new InvalidDataException("Artifact build must be positive.");
            ValidateHttpsUrl(RequireString(artifact, "url"), "artifact url");
            if (RequireInt64(artifact, "size") < 1)
                throw new InvalidDataException("Artifact size must be positive.");
            if (!Regex.IsMatch(RequireString(artifact, "sha256"), "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Artifact sha256 must contain exactly 64 hexadecimal characters.");

            if (artifact.TryGetProperty("installerUrl", out var installerUrl))
            {
                if (installerUrl.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("installerUrl must be a string.");
                ValidateHttpsUrl(installerUrl.GetString()!, "installerUrl");
            }
            if (artifact.TryGetProperty("notes", out var notes) &&
                (notes.ValueKind != JsonValueKind.String || notes.GetString()!.Length > 4096))
                throw new InvalidDataException("notes must be a string no longer than 4096 characters.");
        }

        return document;
    }
    catch
    {
        document.Dispose();
        throw;
    }
}

static JsonDocument ParseJsonObject(byte[] bytes, string description)
{
    try
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException($"{description} must be one JSON object.");
        }
        return document;
    }
    catch (JsonException exception)
    {
        throw new InvalidDataException($"{description} is not valid JSON: {exception.Message}", exception);
    }
}

static void RequireExactProperties(JsonElement value, string[] required) =>
    RequireKnownProperties(value, required, []);

static void RequireKnownProperties(JsonElement value, string[] required, string[] optional)
{
    var allowed = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var property in value.EnumerateObject())
    {
        if (!allowed.Contains(property.Name))
            throw new InvalidDataException($"Unexpected JSON property: {property.Name}");
        if (!seen.Add(property.Name))
            throw new InvalidDataException($"Duplicate JSON property: {property.Name}");
    }
    foreach (var property in required)
    {
        if (!seen.Contains(property))
            throw new InvalidDataException($"Missing JSON property: {property}");
    }
}

static string RequireString(JsonElement value, string propertyName)
{
    var property = value.GetProperty(propertyName);
    if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        throw new InvalidDataException($"{propertyName} must be a non-empty string.");
    return property.GetString()!;
}

static int RequireInt32(JsonElement value, string propertyName)
{
    var property = value.GetProperty(propertyName);
    if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result))
        throw new InvalidDataException($"{propertyName} must be a 32-bit integer.");
    return result;
}

static long RequireInt64(JsonElement value, string propertyName)
{
    var property = value.GetProperty(propertyName);
    if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result))
        throw new InvalidDataException($"{propertyName} must be a 64-bit integer.");
    return result;
}

static void ValidateHttpsUrl(string value, string description)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(uri.Host) ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Fragment))
        throw new InvalidDataException($"{description} must be an absolute HTTPS URL without credentials or fragment.");
}

static byte[] DecodeCanonicalBase64(string value, string description)
{
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        throw new InvalidDataException($"{description} must be canonical Base64 without whitespace.");
    try
    {
        var bytes = Convert.FromBase64String(value);
        if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
            throw new InvalidDataException($"{description} must use canonical standard Base64.");
        return bytes;
    }
    catch (FormatException exception)
    {
        throw new InvalidDataException($"{description} is not valid standard Base64.", exception);
    }
}

static void EnsureP256(ECDsa key)
{
    var parameters = key.ExportParameters(false);
    if (key.KeySize != 256 ||
        !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
        throw new CryptographicException("The stable release key must use ECDSA P-256.");
}

static void WriteNew(string path, string text) => WriteNewBytes(path, Encoding.UTF8.GetBytes(text));

static void WriteNewBytes(string path, byte[] bytes)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    stream.Write(bytes);
    stream.Flush(true);
}

static int Usage()
{
    Console.Error.WriteLine("TabLink.ReleaseTool init-key <private.pem> <public.spki.base64>");
    Console.Error.WriteLine("TabLink.ReleaseTool sign <private.pem> <payload.json> <manifest.json>");
    Console.Error.WriteLine("TabLink.ReleaseTool verify <public.spki.base64> <manifest.json>");
    return 2;
}

internal sealed record SignedEnvelope(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature);
