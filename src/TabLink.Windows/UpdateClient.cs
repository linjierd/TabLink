using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Windows;

internal sealed record PendingWindowsUpdate(
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("packagePath")] string PackagePath,
    [property: JsonPropertyName("manifestEnvelopePath")] string ManifestEnvelopePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("notes")] string? Notes);

internal sealed class UpdateClient(HttpClient http, ReadOnlyMemory<byte> signerSpki, string cacheDirectory, StableSemanticVersion currentVersion, string cohortId)
{
    const int MaximumRedirects = 3;
    const int MaximumManifestBytes = 384 * 1024;
    static readonly JsonSerializerOptions MetadataOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PendingWindowsUpdate?> CheckAndDownloadAsync(Uri manifestUri, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = await GetSmallHttpsAsync(manifestUri, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
            var manifest = UpdateManifestVerifier.VerifyAndParse(envelope, signerSpki.Span);
            var artifact = UpdateManifestVerifier.SelectWindowsUpdate(manifest, cohortId, currentVersion);
            if (artifact is null) { ClearPendingMetadata(); return null; }
            var cached = await TryLoadPendingAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.ReleaseId == manifest.ReleaseId && cached.Version == artifact.Version && cached.Build == artifact.Build && cached.Size == artifact.Size && cached.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                return cached;
            return await DownloadAsync(envelope, manifest, artifact, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<PendingWindowsUpdate?> TryLoadPendingAsync(CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(cacheDirectory, "pending-windows.json");
        if (!File.Exists(metadataPath)) return null;
        PendingWindowsUpdate? pending;
        try { pending = JsonSerializer.Deserialize<PendingWindowsUpdate>(await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false), MetadataOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        if (pending is null || pending.Size <= 0 || pending.Size > 8L * 1024 * 1024 * 1024 || string.IsNullOrWhiteSpace(pending.ReleaseId) || string.IsNullOrWhiteSpace(pending.Version) || string.IsNullOrWhiteSpace(pending.PackagePath) || string.IsNullOrWhiteSpace(pending.ManifestEnvelopePath) || pending.Sha256?.Length != 64) return null;
        try { _ = StableSemanticVersion.Parse(pending.Version); } catch (FormatException) { return null; }
        try
        {
            var fullCache = Path.GetFullPath(cacheDirectory) + Path.DirectorySeparatorChar;
            var fullPackage = Path.GetFullPath(pending.PackagePath);
            var fullEnvelope = Path.GetFullPath(pending.ManifestEnvelopePath);
            if (!fullPackage.StartsWith(fullCache, StringComparison.OrdinalIgnoreCase) || !fullEnvelope.StartsWith(fullCache, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPackage) || !File.Exists(fullEnvelope) || new FileInfo(fullEnvelope).Length is <= 0 or > 384 * 1024) return null;
            var envelope = await File.ReadAllBytesAsync(fullEnvelope, cancellationToken).ConfigureAwait(false);
            var manifest = UpdateManifestVerifier.VerifyAndParse(envelope, signerSpki.Span);
            var artifact = manifest.Artifacts.SingleOrDefault(a => a.Platform == "windows-x64");
            if (artifact is null || manifest.ReleaseId != pending.ReleaseId || artifact.Version != pending.Version || artifact.Build != pending.Build || artifact.Size != pending.Size || !artifact.Sha256.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            if (StableSemanticVersion.Parse(artifact.Version).CompareTo(currentVersion) <= 0 || !UpdateManifestVerifier.IsCohortIncluded(cohortId, manifest.ReleaseId, manifest.RolloutPercentage) || manifest.MinimumProtocolVersion > UpdateManifestVerifier.CurrentProtocolVersion) return null;
            return await ValidateFileAsync(fullPackage, artifact.Size, artifact.Sha256, cancellationToken).ConfigureAwait(false) ? pending : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException) { return null; }
    }

    async Task<PendingWindowsUpdate> DownloadAsync(byte[] envelope, UpdateManifestPayload manifest, UpdateArtifact artifact, CancellationToken cancellationToken)
    {
        var releaseKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.ReleaseId))).ToLowerInvariant()[..24];
        var releaseDirectory = Path.Combine(cacheDirectory, "packages", releaseKey);
        Directory.CreateDirectory(releaseDirectory);
        var destination = Path.Combine(releaseDirectory, "TabLink-windows-x64.zip");
        var envelopePath = Path.Combine(releaseDirectory, "signed-manifest.json");
        if (!await ValidateFileAsync(destination, artifact.Size, artifact.Sha256, cancellationToken).ConfigureAwait(false))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                var uri = UpdateManifestVerifier.ValidateHttps(artifact.Url, "Windows 更新包");
                using var response = await SendFollowingHttpsRedirectsAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long advertised && advertised != artifact.Size)
                    throw new InvalidDataException("更新包服务器返回的大小与签名清单不一致。");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > artifact.Size) throw new InvalidDataException("更新包超过签名清单声明的大小。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
                if (total != artifact.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
                    throw new InvalidDataException("更新包大小或 SHA-256 与签名清单不一致。");
                await output.DisposeAsync().ConfigureAwait(false);
                File.Move(temporary, destination, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        SaveBytes(envelopePath, envelope);
        var pending = new PendingWindowsUpdate(manifest.ReleaseId, artifact.Version, artifact.Build, destination, envelopePath, artifact.Size, artifact.Sha256.ToUpperInvariant(), artifact.Notes);
        SaveMetadata(pending);
        return pending;
    }

    async Task<byte[]> GetSmallHttpsAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendFollowingHttpsRedirectsAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("更新清单过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("更新清单过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    async Task<HttpResponseMessage> SendFollowingHttpsRedirectsAsync(Uri uri, HttpCompletionOption option, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var redirect = 0; ; redirect++)
        {
            UpdateManifestVerifier.ValidateHttps(current.AbsoluteUri, "更新地址");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TabLink", currentVersion.ToString()));
            var response = await http.SendAsync(request, option, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            if (redirect >= MaximumRedirects) { response.Dispose(); throw new HttpRequestException("更新地址重定向次数过多。"); }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("更新服务器返回了无地址的重定向。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    static async Task<bool> ValidateFileAsync(string path, long expectedSize, string expectedHash, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedSize) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
    }

    void SaveMetadata(PendingWindowsUpdate pending)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, "pending-windows.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, pending, MetadataOptions); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    void ClearPendingMetadata()
    {
        try { File.Delete(Path.Combine(cacheDirectory, "pending-windows.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void SaveBytes(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
}
