using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TabLink.Windows;

internal sealed record SignedUpdateEnvelope(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature);

internal sealed record UpdateManifestPayload(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("publishedAtUtc")] string PublishedAtUtc,
    [property: JsonPropertyName("rolloutPercentage")] int RolloutPercentage,
    [property: JsonPropertyName("minimumProtocolVersion")] int MinimumProtocolVersion,
    [property: JsonPropertyName("artifacts")] UpdateArtifact[] Artifacts);

internal sealed record UpdateArtifact(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("installerUrl")] string? InstallerUrl,
    [property: JsonPropertyName("notes")] string? Notes);

internal static partial class UpdateManifestVerifier
{
    public const int CurrentProtocolVersion = 1;
    const int MaximumEnvelopeBytes = 384 * 1024;
    const int MaximumPayloadBytes = 256 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseIdPattern();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339UtcPattern();

    public static UpdateManifestPayload VerifyAndParse(ReadOnlySpan<byte> envelopeBytes, ReadOnlySpan<byte> signerSpki)
        => VerifyAndParse(envelopeBytes, signerSpki, DateTimeOffset.UtcNow);

    internal static UpdateManifestPayload VerifyAndParse(ReadOnlySpan<byte> envelopeBytes, ReadOnlySpan<byte> signerSpki, DateTimeOffset nowUtc)
    {
        if (envelopeBytes.Length is 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("更新清单大小无效。");
        ValidateUniqueProperties(envelopeBytes);
        var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelopeBytes, JsonOptions)
            ?? throw new InvalidDataException("更新清单为空。");
        byte[] payload;
        byte[] signature;
        try
        {
            payload = DecodeCanonicalBase64(envelope.Payload, "payload");
            signature = DecodeCanonicalBase64(envelope.Signature, "signature");
        }
        catch (FormatException ex) { throw new InvalidDataException("更新清单签名编码无效。", ex); }
        if (payload.Length is 0 or > MaximumPayloadBytes)
            throw new InvalidDataException("更新负载大小无效。");
        using (var signer = ECDsa.Create())
        {
            try { signer.ImportSubjectPublicKeyInfo(signerSpki, out var read); if (read != signerSpki.Length) throw new CryptographicException("公钥包含尾随数据。"); }
            catch (CryptographicException ex) { throw new InvalidDataException("内置更新签名公钥无效。", ex); }
            if (!signer.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new InvalidDataException("更新清单签名验证失败。");
        }
        ValidateUniqueProperties(payload);
        var manifest = JsonSerializer.Deserialize<UpdateManifestPayload>(payload, JsonOptions)
            ?? throw new InvalidDataException("更新负载为空。");
        Validate(manifest, nowUtc);
        return manifest;
    }

    public static UpdateArtifact? SelectWindowsUpdate(UpdateManifestPayload manifest, string cohortId, StableSemanticVersion current)
    {
        if (manifest.MinimumProtocolVersion > CurrentProtocolVersion || !IsCohortIncluded(cohortId, manifest.ReleaseId, manifest.RolloutPercentage))
            return null;
        return manifest.Artifacts
            .Where(a => a.Platform == "windows-x64")
            .Select(a => (Artifact: a, Version: StableSemanticVersion.Parse(a.Version)))
            .Where(x => x.Version.CompareTo(current) > 0)
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Artifact.Build)
            .Select(x => x.Artifact)
            .FirstOrDefault();
    }

    internal static bool IsCohortIncluded(string cohortId, string releaseId, int percentage)
    {
        if (percentage >= 100) return true;
        if (percentage <= 0) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cohortId + "\n" + releaseId));
        return BinaryPrimitives.ReadUInt32BigEndian(hash) % 100 < percentage;
    }

    static void Validate(UpdateManifestPayload manifest, DateTimeOffset nowUtc)
    {
        if (manifest.Schema != 1) throw new InvalidDataException("不支持的更新清单版本。");
        if (manifest.Channel != "stable") throw new InvalidDataException("只接受 stable 正式版更新。");
        if (string.IsNullOrEmpty(manifest.ReleaseId) || !ReleaseIdPattern().IsMatch(manifest.ReleaseId)) throw new InvalidDataException("更新发布编号无效。");
        if (string.IsNullOrEmpty(manifest.PublishedAtUtc) || !Rfc3339UtcPattern().IsMatch(manifest.PublishedAtUtc) || !DateTimeOffset.TryParseExact(manifest.PublishedAtUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var published))
            throw new InvalidDataException("更新发布时间必须是整秒 UTC 格式 yyyy-MM-dd'T'HH:mm:ss'Z'。");
        var now = nowUtc.ToUniversalTime();
        if (published > now.AddHours(24) || published < now.AddDays(-366)) throw new InvalidDataException("更新清单发布时间异常或已经过期。");
        if (manifest.RolloutPercentage is < 0 or > 100) throw new InvalidDataException("更新发布比例无效。");
        if (manifest.MinimumProtocolVersion < 1) throw new InvalidDataException("最低协议版本无效。");
        if (manifest.Artifacts is null || manifest.Artifacts.Length is 0 || manifest.Artifacts.Length > 32)
            throw new InvalidDataException("更新清单没有有效安装包。");
        var platforms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.Platform is not ("windows-x64" or "android" or "ios" or "harmony")) throw new InvalidDataException("更新平台无效。");
            try { _ = StableSemanticVersion.Parse(artifact.Version); }
            catch (FormatException ex) { throw new InvalidDataException("更新包版本号不是正式 SemVer。", ex); }
            if (artifact.Build < 1 || artifact.Size <= 0 || artifact.Size > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包大小或构建号无效。");
            ValidateHttps(artifact.Url, "更新包");
            if (artifact.InstallerUrl is not null) ValidateHttps(artifact.InstallerUrl, "安装地址");
            if (string.IsNullOrEmpty(artifact.Sha256) || !Sha256Pattern().IsMatch(artifact.Sha256)) throw new InvalidDataException("更新包 SHA-256 无效。");
            if (artifact.Notes?.Length > 4096) throw new InvalidDataException("更新说明过长。");
            if (!platforms.Add(artifact.Platform)) throw new InvalidDataException("更新清单中每个平台只能包含一个安装包。");
        }
    }

    internal static Uri ValidateHttps(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException(name + "必须使用不含用户信息或片段的 HTTPS 地址；允许下载服务所需的查询参数。");
        return uri;
    }

    static byte[] DecodeCanonicalBase64(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) throw new InvalidDataException(name + " 必须是无空白的标准 Base64。");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (!Convert.ToBase64String(bytes).Equals(value, StringComparison.Ordinal)) throw new InvalidDataException(name + " 必须使用规范 Base64 编码。");
            return bytes;
        }
        catch (FormatException ex) { throw new InvalidDataException(name + " Base64 编码无效。", ex); }
    }

    static void ValidateUniqueProperties(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            CheckElement(document.RootElement);
        }
        catch (JsonException ex) { throw new InvalidDataException("更新清单 JSON 无效。", ex); }
    }

    static void CheckElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("更新清单包含重复字段。");
                CheckElement(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckElement(item);
    }
}
