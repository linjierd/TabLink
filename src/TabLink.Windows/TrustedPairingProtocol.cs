using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabLink.Windows;

internal sealed record TrustedDeviceInfo(
    string DeviceId,
    string DisplayName,
    string PublicKeySpki,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastUsedUtc);

internal interface ITrustedDeviceRegistry
{
    string HostId { get; }
    int Count { get; }
    IReadOnlyList<TrustedDeviceInfo> Snapshot();
    TrustedDeviceInfo Register(string deviceId, string publicKeySpki, string? displayName);
    bool Contains(string deviceId);
    bool Verify(string deviceId, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature);
    bool Revoke(string deviceId);
}

internal sealed record InitialHello(
    int Protocol,
    string Token,
    string[] Features,
    string? DeviceId,
    string? DevicePublicKey,
    string? DeviceName);

internal sealed record TrustedHello(int Protocol, string DeviceId, string[] Features);
internal sealed record TrustedProof(string DeviceId, byte[] Signature);

/// <summary>
/// Strict parser and transcript builder for long-lived device authentication.
/// A discovery address is only a route hint; the persistent host certificate
/// and a fresh signed challenge remain the two authentication anchors.
/// </summary>
internal static class TrustedPairingProtocol
{
    internal const string Feature = "trusted-device-v1";
    internal const byte TrustedHelloPacket = 0x17;
    internal const byte TrustedChallengePacket = 0x18;
    internal const byte TrustedProofPacket = 0x19;
    internal const byte TrustEstablishedPacket = 0x1a;
    internal const int ChallengeLength = 32;
    internal const int MaximumDeviceNameLength = 64;
    internal const int MaximumFeatures = 16;
    static readonly byte[] Domain = Encoding.ASCII.GetBytes("TabLink trusted-device-v1 proof\0");

    internal static InitialHello ParseInitialHello(ReadOnlySpan<byte> payload)
    {
        using var document = ParseObject(payload, 8192);
        var properties = ReadProperties(document.RootElement,
            "protocol", "token", "features", "deviceId", "devicePublicKey", "deviceName");
        RequireExactProperties(properties, "protocol", "token");
        var protocol = ReadInt(properties["protocol"], "protocol");
        var token = ReadString(properties["token"], "token", 64);
        var features = properties.TryGetValue("features", out var featureValue)
            ? ReadFeatures(featureValue) : [];
        var deviceId = OptionalString(properties, "deviceId", 64);
        var publicKey = OptionalString(properties, "devicePublicKey", 256);
        var name = OptionalString(properties, "deviceName", MaximumDeviceNameLength);
        var hasAnyRegistration = deviceId is not null || publicKey is not null || name is not null;
        if (hasAnyRegistration && (deviceId is null || publicKey is null))
            throw new InvalidDataException("可信设备注册信息不完整。");
        if (deviceId is not null) ValidateDeviceId(deviceId);
        if (name is not null) _ = NormalizeDeviceName(name);
        return new(protocol, token, features, deviceId, publicKey, name);
    }

    internal static TrustedHello ParseTrustedHello(ReadOnlySpan<byte> payload)
    {
        using var document = ParseObject(payload, 4096);
        var properties = ReadProperties(document.RootElement, "protocol", "deviceId", "features");
        RequireExactProperties(properties, "protocol", "deviceId", "features");
        var deviceId = ReadString(properties["deviceId"], "deviceId", 64);
        ValidateDeviceId(deviceId);
        return new(ReadInt(properties["protocol"], "protocol"), deviceId, ReadFeatures(properties["features"]));
    }

    internal static TrustedProof ParseTrustedProof(ReadOnlySpan<byte> payload)
    {
        using var document = ParseObject(payload, 4096);
        var properties = ReadProperties(document.RootElement, "deviceId", "signature");
        RequireExactProperties(properties, "deviceId", "signature");
        var deviceId = ReadString(properties["deviceId"], "deviceId", 64);
        ValidateDeviceId(deviceId);
        var encoded = ReadString(properties["signature"], "signature", 192);
        byte[] signature;
        try { signature = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("可信设备签名格式无效。", error); }
        if (signature.Length is < 64 or > 80) throw new InvalidDataException("可信设备签名长度无效。");
        return new(deviceId, signature);
    }

    internal static byte[] CreateChallenge() => RandomNumberGenerator.GetBytes(ChallengeLength);

    internal static byte[] BuildProofTranscript(string hostId, string deviceId, ReadOnlySpan<byte> challenge)
    {
        ValidateHostId(hostId);
        ValidateDeviceId(deviceId);
        if (challenge.Length != ChallengeLength) throw new ArgumentException("可信设备挑战长度无效。", nameof(challenge));
        using var output = new MemoryStream(Domain.Length + 64 + 64 + ChallengeLength + 8);
        output.Write(Domain);
        WriteField(output, Encoding.ASCII.GetBytes(hostId));
        WriteField(output, Encoding.ASCII.GetBytes(deviceId));
        WriteField(output, challenge);
        return output.ToArray();
    }

    internal static string DeviceIdFromPublicKey(string publicKeySpki)
    {
        var bytes = DecodeAndValidatePublicKey(publicKeySpki);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static bool VerifySignature(string publicKeySpki, string hostId, string deviceId,
        ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
    {
        if (signature.Length is < 64 or > 80) return false;
        byte[] spki;
        try { spki = DecodeAndValidatePublicKey(publicKeySpki); }
        catch (Exception error) when (error is FormatException or CryptographicException or InvalidDataException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(spki), Convert.FromHexString(deviceId))) return false;
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.KeySize != 256) return false;
            return key.VerifyData(BuildProofTranscript(hostId, deviceId, challenge), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    internal static string NormalizeDeviceName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Android 设备" : value.Trim();
        if (name.Length > MaximumDeviceNameLength || name.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
            throw new InvalidDataException("可信设备名称无效。");
        return name;
    }

    internal static void ValidateHostId(string hostId)
    {
        if (!IsLowerHexSha256(hostId)) throw new InvalidDataException("可信电脑身份无效。");
    }

    internal static void ValidateDeviceId(string deviceId)
    {
        if (!IsLowerHexSha256(deviceId)) throw new InvalidDataException("可信设备身份无效。");
    }

    static byte[] DecodeAndValidatePublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new InvalidDataException("可信设备公钥无效。");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length is < 80 or > 160) throw new InvalidDataException("可信设备公钥长度无效。");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out var read);
        if (read != bytes.Length || key.KeySize != 256) throw new InvalidDataException("可信设备必须使用 P-256 公钥。");
        return bytes;
    }

    static JsonDocument ParseObject(ReadOnlySpan<byte> payload, int maximumBytes)
    {
        if (payload.IsEmpty || payload.Length > maximumBytes) throw new InvalidDataException("认证数据长度无效。");
        try
        {
            var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException("认证数据必须是 JSON 对象。");
            }
            return document;
        }
        catch (JsonException error) { throw new InvalidDataException("认证数据 JSON 无效。", error); }
    }

    static Dictionary<string, JsonElement> ReadProperties(JsonElement root, params string[] allowed)
    {
        var allow = allowed.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allow.Contains(property.Name)) throw new InvalidDataException("认证数据包含未知字段。");
            if (!result.TryAdd(property.Name, property.Value)) throw new InvalidDataException("认证数据包含重复字段。");
        }
        return result;
    }

    static void RequireExactProperties(IReadOnlyDictionary<string, JsonElement> values, params string[] required)
    {
        foreach (var name in required)
            if (!values.ContainsKey(name)) throw new InvalidDataException("认证数据缺少字段。");
    }

    static int ReadInt(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"认证字段 {name} 无效。");
        return result;
    }

    static string ReadString(JsonElement value, string name, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"认证字段 {name} 无效。");
        var result = value.GetString() ?? throw new InvalidDataException($"认证字段 {name} 无效。");
        if (result.Length is < 1 || result.Length > maximumLength || result.Any(char.IsControl))
            throw new InvalidDataException($"认证字段 {name} 无效。");
        return result;
    }

    static string? OptionalString(IReadOnlyDictionary<string, JsonElement> values, string name, int maximumLength) =>
        values.TryGetValue(name, out var value) ? ReadString(value, name, maximumLength) : null;

    static string[] ReadFeatures(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("认证功能列表无效。");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var feature = ReadString(item, "features", 64);
            if (!feature.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                throw new InvalidDataException("认证功能名称无效。");
            if (!seen.Add(feature)) throw new InvalidDataException("认证功能列表包含重复项。");
            result.Add(feature);
            if (result.Count > MaximumFeatures) throw new InvalidDataException("认证功能列表过长。");
        }
        return [.. result];
    }

    static bool IsLowerHexSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static void WriteField(Stream output, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        output.Write(length);
        output.Write(value);
    }
}
