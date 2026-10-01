using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TabLink.Windows;

internal sealed record NativeTrustStorageBoundary(
    string ProgramDataRoot,
    string TabLinkRoot,
    string Directory,
    Action EnsureDirectory,
    Func<string, FileStream> CreateFile,
    Action<string> VerifyFile,
    Func<byte[], byte[]> Protect,
    Func<byte[], byte[]> Unprotect);

/// <summary>
/// Installation identity and enrolled native-device public keys. Production
/// files live under administrator-owned ProgramData and the PFX is additionally
/// protected with DPAPI. Discovery metadata and user settings never authorize
/// a trusted connection.
/// </summary>
internal sealed class NativeTrustRepository : ITrustedDeviceRegistry, IDisposable
{
    const int SchemaVersion = 1;
    const int MaximumDevices = 32;
    const int MaximumIdentityBytes = 65536;
    const int MaximumRegistryBytes = 262144;
    const string IdentityName = "host-identity.dpapi";
    const string DevicesName = "trusted-devices.json";
    const string TemporaryPrefix = ".trusted-devices.";
    const string TemporarySuffix = ".tmp";
    readonly object gate = new();
    readonly NativeTrustStorageBoundary storage;
    readonly string identityPath;
    readonly string devicesPath;
    readonly X509Certificate2 identity;
    List<TrustedDeviceInfo> devices;
    bool disposed;

    internal static NativeTrustStorageBoundary ProductionStorage()
    {
        var programData = UsbReverseProtectedStorage.ProgramDataRoot;
        var tabLink = UsbReverseProtectedStorage.TabLinkRoot;
        var directory = Path.Combine(tabLink, "NativeTrust");
        return new(programData, tabLink, directory,
            () =>
            {
                UsbReverseProtectedStorage.EnsureProtectedDirectoryChain(tabLink);
                ProtectedUpdaterStager.EnsureProtectedDirectory(directory, tabLink);
                WindowsUpdatePathPolicy.VerifySafeNamespaceChain(programData, "原生可信配对保护父路径");
                ProtectedUpdaterStager.VerifyProtectedDirectory(tabLink);
                ProtectedUpdaterStager.VerifyProtectedDirectory(directory);
            },
            path =>
            {
                VerifyDirectChild(directory, path);
                return ProtectedUpdaterStager.CreateProtectedFile(path, directory);
            },
            path =>
            {
                VerifyDirectChild(directory, path);
                ProtectedUpdaterStager.VerifyProtectedFile(path);
            },
            bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser),
            bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
    }

    internal NativeTrustRepository(NativeTrustStorageBoundary? storageBoundary = null)
    {
        storage = storageBoundary ?? ProductionStorage();
        ValidateBoundary(storage);
        identityPath = Path.Combine(storage.Directory, IdentityName);
        devicesPath = Path.Combine(storage.Directory, DevicesName);
        storage.EnsureDirectory();
        CleanupProtectedTemporaryFiles();
        if (!File.Exists(identityPath) && File.Exists(devicesPath))
            throw new InvalidDataException("可信设备记录存在，但主机身份缺失；已停止自动配对。请先检查受保护存储。");
        identity = File.Exists(identityPath) ? LoadIdentity() : CreateIdentity();
        HostId = Convert.ToHexString(SHA256.HashData(identity.RawData)).ToLowerInvariant();
        TrustedPairingProtocol.ValidateHostId(HostId);
        devices = File.Exists(devicesPath) ? LoadDevices() : [];
    }

    public string HostId { get; }
    public int Count { get { lock (gate) { ThrowIfDisposed(); return devices.Count; } } }

    internal X509Certificate2 CreateServerCertificate()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var pfx = identity.Export(X509ContentType.Pfx);
            // Windows Schannel cannot use an RSA private key loaded only with
            // EphemeralKeySet for server authentication (SEC_E_NO_CREDENTIALS).
            // UserKeySet without PersistKeySet gives SslStream a usable CNG key
            // and removes the imported key when this certificate is disposed.
            try { return X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    public IReadOnlyList<TrustedDeviceInfo> Snapshot()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return Array.AsReadOnly(devices.OrderByDescending(item => item.LastUsedUtc).ToArray());
        }
    }

    public bool Contains(string deviceId)
    {
        try { TrustedPairingProtocol.ValidateDeviceId(deviceId); }
        catch (InvalidDataException) { return false; }
        lock (gate) { ThrowIfDisposed(); return devices.Any(item => item.DeviceId == deviceId); }
    }

    public TrustedDeviceInfo Register(string deviceId, string publicKeySpki, string? displayName)
    {
        TrustedPairingProtocol.ValidateDeviceId(deviceId);
        var derived = TrustedPairingProtocol.DeviceIdFromPublicKey(publicKeySpki);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(deviceId), Convert.FromHexString(derived)))
            throw new InvalidDataException("可信设备身份与公钥不匹配。");
        var normalizedName = TrustedPairingProtocol.NormalizeDeviceName(displayName);
        var normalizedKey = Convert.ToBase64String(Convert.FromBase64String(publicKeySpki));
        lock (gate)
        {
            ThrowIfDisposed();
            var now = DateTimeOffset.UtcNow;
            var next = devices.ToList();
            var existing = next.SingleOrDefault(item => item.DeviceId == deviceId);
            TrustedDeviceInfo result;
            if (existing is null)
            {
                if (next.Count >= MaximumDevices) throw new InvalidOperationException("可信设备已达到 32 台上限，请先移除不用的设备。");
                result = new(deviceId, normalizedName, normalizedKey, now, now);
                next.Add(result);
            }
            else
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(existing.PublicKeySpki), Convert.FromBase64String(normalizedKey)))
                    throw new InvalidDataException("同一可信设备身份出现了不同公钥。");
                result = existing with { DisplayName = normalizedName, LastUsedUtc = now };
                next[next.IndexOf(existing)] = result;
            }
            devices = SaveDevices(next);
            return devices.Single(item => item.DeviceId == deviceId);
        }
    }

    public bool Verify(string deviceId, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
    {
        if (challenge.Length != TrustedPairingProtocol.ChallengeLength) return false;
        try { TrustedPairingProtocol.ValidateDeviceId(deviceId); }
        catch (InvalidDataException) { return false; }
        lock (gate)
        {
            ThrowIfDisposed();
            var existing = devices.SingleOrDefault(item => item.DeviceId == deviceId);
            if (existing is null || !TrustedPairingProtocol.VerifySignature(existing.PublicKeySpki, HostId,
                    deviceId, challenge, signature)) return false;
            var next = devices.ToList();
            var updated = existing with { LastUsedUtc = DateTimeOffset.UtcNow };
            next[next.IndexOf(existing)] = updated;
            devices = SaveDevices(next);
            return true;
        }
    }

    public bool Revoke(string deviceId)
    {
        TrustedPairingProtocol.ValidateDeviceId(deviceId);
        lock (gate)
        {
            ThrowIfDisposed();
            if (!devices.Any(item => item.DeviceId == deviceId)) return false;
            var next = devices.Where(item => item.DeviceId != deviceId).ToList();
            devices = SaveDevices(next);
            return true;
        }
    }

    X509Certificate2 LoadIdentity()
    {
        storage.VerifyFile(identityPath);
        var info = new FileInfo(identityPath);
        if (info.Length is < 32 or > MaximumIdentityBytes) throw new InvalidDataException("原生主机身份文件大小无效。");
        var encrypted = File.ReadAllBytes(identityPath);
        byte[] pfx;
        try { pfx = storage.Unprotect(encrypted); }
        catch (CryptographicException error) { throw new InvalidDataException("原生主机身份无法解密。", error); }
        try
        {
            return LoadValidatedIdentity(pfx);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    X509Certificate2 CreateIdentity()
    {
        var ownsIdentityFile = false;
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=TabLink Native Host " + Guid.NewGuid().ToString("N")[..8],
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = generated.Export(X509ContentType.Pfx);
        try
        {
            var encrypted = storage.Protect(pfx);
            try
            {
                using var file = storage.CreateFile(identityPath);
                ownsIdentityFile = true;
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
            storage.VerifyFile(identityPath);
            return LoadValidatedIdentity(pfx);
        }
        catch
        {
            try { if (ownsIdentityFile && File.Exists(identityPath)) { storage.VerifyFile(identityPath); File.Delete(identityPath); } }
            catch { }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    List<TrustedDeviceInfo> LoadDevices()
    {
        try { return LoadDevicesCore(); }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException)
        {
            throw new InvalidDataException("可信设备记录格式或公钥无效。", error);
        }
    }

    List<TrustedDeviceInfo> LoadDevicesCore()
    {
        storage.VerifyFile(devicesPath);
        var info = new FileInfo(devicesPath);
        if (info.Length is < 2 or > MaximumRegistryBytes) throw new InvalidDataException("可信设备记录大小无效。");
        var payload = File.ReadAllBytes(devicesPath);
        if (payload.Length is < 2 or > MaximumRegistryBytes) throw new InvalidDataException("可信设备记录大小无效。");
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 5
        });
        var root = ReadUniqueObject(document.RootElement, "schemaVersion", "devices");
        if (root.Count != 2 || root["schemaVersion"].ValueKind != JsonValueKind.Number ||
            !root["schemaVersion"].TryGetInt32(out var schema) || schema != SchemaVersion ||
            root["devices"].ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("可信设备记录架构无效。");
        var result = new List<TrustedDeviceInfo>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in root["devices"].EnumerateArray())
        {
            if (result.Count >= MaximumDevices) throw new InvalidDataException("可信设备记录超过上限。");
            var item = ReadUniqueObject(element, "deviceId", "displayName", "publicKeySpki", "createdUtc", "lastUsedUtc");
            if (item.Count != 5) throw new InvalidDataException("可信设备记录字段不完整。");
            var id = StrictString(item["deviceId"], 64);
            TrustedPairingProtocol.ValidateDeviceId(id);
            var name = TrustedPairingProtocol.NormalizeDeviceName(StrictString(item["displayName"], TrustedPairingProtocol.MaximumDeviceNameLength));
            var key = StrictString(item["publicKeySpki"], 256);
            if (TrustedPairingProtocol.DeviceIdFromPublicKey(key) != id || !ids.Add(id))
                throw new InvalidDataException("可信设备记录身份重复或与公钥不匹配。");
            var created = StrictTimestamp(item["createdUtc"]);
            var used = StrictTimestamp(item["lastUsedUtc"]);
            var maximumTimestamp = DateTimeOffset.UtcNow.AddMinutes(5);
            if (used < created || created > maximumTimestamp || used > maximumTimestamp)
                throw new InvalidDataException("可信设备记录时间无效。");
            result.Add(new(id, name, Convert.ToBase64String(Convert.FromBase64String(key)), created, used));
        }
        return result;
    }

    List<TrustedDeviceInfo> SaveDevices(IReadOnlyList<TrustedDeviceInfo> nextDevices)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            devices = nextDevices.OrderBy(item => item.DeviceId, StringComparer.Ordinal).Select(item => new
            {
                deviceId = item.DeviceId,
                displayName = item.DisplayName,
                publicKeySpki = item.PublicKeySpki,
                createdUtc = item.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                lastUsedUtc = item.LastUsedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            })
        }, new JsonSerializerOptions { WriteIndented = true });
        if (payload.Length > MaximumRegistryBytes) throw new InvalidDataException("可信设备记录超过大小上限。");
        var temporary = Path.Combine(storage.Directory, TemporaryPrefix + Guid.NewGuid().ToString("N") + TemporarySuffix);
        try
        {
            using (var file = storage.CreateFile(temporary))
            {
                file.Write(payload);
                file.Flush(flushToDisk: true);
            }
            storage.VerifyFile(temporary);
            if (File.Exists(devicesPath)) storage.VerifyFile(devicesPath);
            File.Move(temporary, devicesPath, overwrite: true);
            storage.VerifyFile(devicesPath);
            return LoadDevices();
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { storage.VerifyFile(temporary); File.Delete(temporary); }
                catch { }
            }
        }
    }

    void CleanupProtectedTemporaryFiles()
    {
        foreach (var path in Directory.EnumerateFiles(storage.Directory, TemporaryPrefix + "*" + TemporarySuffix))
        {
            var name = Path.GetFileName(path);
            var middle = name[TemporaryPrefix.Length..^TemporarySuffix.Length];
            if (middle.Length != 32 || !middle.All(Uri.IsHexDigit))
                throw new InvalidDataException("可信设备目录包含未知临时文件。");
            storage.VerifyFile(path);
            File.Delete(path);
        }
        foreach (var path in Directory.EnumerateFiles(storage.Directory))
        {
            var name = Path.GetFileName(path);
            if (name != IdentityName && name != DevicesName)
                throw new InvalidDataException("可信设备目录包含未知文件。");
        }
        if (Directory.EnumerateDirectories(storage.Directory).Any())
            throw new InvalidDataException("可信设备目录包含未知子目录。");
    }

    static Dictionary<string, JsonElement> ReadUniqueObject(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("可信设备记录对象无效。");
        var allow = allowed.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allow.Contains(property.Name) || !result.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("可信设备记录包含未知或重复字段。");
        }
        foreach (var name in allowed)
            if (!result.ContainsKey(name)) throw new InvalidDataException("可信设备记录缺少字段。");
        return result;
    }

    static string StrictString(JsonElement element, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("可信设备记录字符串无效。");
        var value = element.GetString() ?? throw new InvalidDataException("可信设备记录字符串无效。");
        if (value.Length is < 1 || value.Length > maximumLength || value.Any(char.IsControl))
            throw new InvalidDataException("可信设备记录字符串无效。");
        return value;
    }

    static DateTimeOffset StrictTimestamp(JsonElement element)
    {
        var value = StrictString(element, 40);
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw new InvalidDataException("可信设备记录时间无效。");
        return timestamp;
    }

    static void ValidateIdentity(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30) ||
            certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5) ||
            !certificate.Subject.StartsWith("CN=TabLink Native Host ", StringComparison.Ordinal) ||
            rsa is not { KeySize: >= 3072 })
            throw new InvalidDataException("原生主机身份无效或即将到期。");
    }

    static X509Certificate2 LoadValidatedIdentity(ReadOnlySpan<byte> pfx)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        try
        {
            ValidateIdentity(certificate);
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    static void ValidateBoundary(NativeTrustStorageBoundary value)
    {
        var programData = Path.GetFullPath(value.ProgramDataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tabLink = Path.GetFullPath(value.TabLinkRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetFullPath(value.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(programData) || !tabLink.StartsWith(programData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !directory.StartsWith(tabLink + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("原生可信配对存储边界无效。", nameof(value));
    }

    static void VerifyDirectChild(string directory, string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原生可信配对文件越过受保护目录。");
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            identity.Dispose();
            devices.Clear();
        }
    }
}
