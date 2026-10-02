using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Globalization;
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

internal sealed record AcceptedManifestFloor(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("publishedAtUtc")] string PublishedAtUtc,
    [property: JsonPropertyName("decisionSha256")] string DecisionSha256);

/// <summary>
/// Marks a verified release-policy condition for which reusing an older cached
/// update would violate fail-closed behavior. The coordinator must clear its
/// ready state instead of treating this as an ordinary offline failure.
/// </summary>
internal sealed class UpdatePolicyBlockedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class UpdateClient(HttpClient http, ReadOnlyMemory<byte> signerSpki, string cacheDirectory, StableSemanticVersion currentVersion, string cohortId,
    TimeSpan? manifestSourceTimeout = null)
{
    const int MaximumRedirects = 3;
    const int MaximumManifestBytes = 384 * 1024;
    static readonly TimeSpan DefaultManifestSourceTimeout = TimeSpan.FromSeconds(30);
    static readonly JsonSerializerOptions MetadataOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object persistenceSync = new();
    readonly TimeSpan sourceTimeout = ValidateSourceTimeout(manifestSourceTimeout ?? DefaultManifestSourceTimeout);

    public Task<PendingWindowsUpdate?> CheckAndDownloadAsync(Uri manifestUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        return CheckAndDownloadAsync(new[] { manifestUri }, cancellationToken);
    }

    public Task<PendingWindowsUpdate?> CheckAndDownloadAsync(IReadOnlyList<Uri> manifestUris, CancellationToken cancellationToken)
        => CheckAndDownloadAsync(manifestUris, cancellationToken, cancellationToken);

    internal async Task<PendingWindowsUpdate?> CheckAndDownloadAsync(IReadOnlyList<Uri> manifestUris,
        CancellationToken operationCancellationToken, CancellationToken packageDownloadCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestUris);
        if (manifestUris.Count == 0) throw new ArgumentException("至少需要一个更新清单地址。", nameof(manifestUris));
        await gate.WaitAsync(operationCancellationToken).ConfigureAwait(false);
        try
        {
            using var cancellationBarrier = RegisterPersistenceCancellationBarrier(operationCancellationToken);
            operationCancellationToken.ThrowIfCancellationRequested();
            var sources = await GetVerifiedManifestsAsync(manifestUris, operationCancellationToken).ConfigureAwait(false);
            operationCancellationToken.ThrowIfCancellationRequested();
            var newestPublishedAt = sources.Max(source => ParsePublishedAt(source.Manifest.PublishedAtUtc));
            var newestSources = sources.Where(source => ParsePublishedAt(source.Manifest.PublishedAtUtc) == newestPublishedAt).ToArray();
            for (var index = 1; index < newestSources.Length; index++)
                if (!HasSameSignedDecision(newestSources[0].Manifest, newestSources[index].Manifest))
                {
                    try
                    {
                        PersistConflictFloor(newestSources, operationCancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                        JsonException or CryptographicException or FormatException or ArgumentException or System.Security.SecurityException)
                    {
                        ClearPendingMetadata(operationCancellationToken);
                        throw new UpdatePolicyBlockedException(
                            "多个有效更新清单具有相同发布时间但内容冲突，且冲突状态未能持久保存；已阻止复用旧更新。", ex);
                    }
                    ClearPendingMetadata(operationCancellationToken);
                    throw new UpdatePolicyBlockedException(
                        "多个有效更新清单具有相同发布时间，但发布内容冲突；为安全起见已停止更新并阻止复用旧更新。");
                }

            var authoritative = newestSources[0];
            var manifest = authoritative.Manifest;
            AcceptManifestFloor(manifest, operationCancellationToken);
            operationCancellationToken.ThrowIfCancellationRequested();
            var artifact = UpdateManifestVerifier.SelectWindowsUpdate(manifest, cohortId, currentVersion);
            if (artifact is null) { ClearPendingMetadata(operationCancellationToken); return null; }

            packageDownloadCancellationToken.ThrowIfCancellationRequested();
            using var packageCancellationBarrier = RegisterPersistenceCancellationBarrier(packageDownloadCancellationToken);
            var cached = await TryLoadPendingAsync(packageDownloadCancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.ReleaseId == manifest.ReleaseId && cached.Version == artifact.Version && cached.Build == artifact.Build && cached.Size == artifact.Size && cached.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                return cached;
            var mirrors = GetPackageMirrors(sources, manifest.ReleaseId, artifact);
            return await DownloadAsync(authoritative.Envelope, manifest, artifact, mirrors, packageDownloadCancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    async Task<IReadOnlyList<VerifiedManifestSource>> GetVerifiedManifestsAsync(IReadOnlyList<Uri> manifestUris, CancellationToken cancellationToken)
    {
        var sources = new List<VerifiedManifestSource>();
        var failures = new List<Exception>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifestUri in manifestUris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifestUri is null)
            {
                failures.Add(new ArgumentNullException(nameof(manifestUris), "更新清单地址不能为空。"));
                continue;
            }
            if (!seen.Add(manifestUri.AbsoluteUri)) continue;
            try
            {
                using var sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sourceLifetime.CancelAfter(sourceTimeout);
                var envelope = await GetSmallHttpsAsync(manifestUri, MaximumManifestBytes, sourceLifetime.Token).ConfigureAwait(false);
                sources.Add(new VerifiedManifestSource(envelope, UpdateManifestVerifier.VerifyAndParse(envelope, signerSpki.Span)));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { failures.Add(ex); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or FormatException or ArgumentException or NotSupportedException)
            { failures.Add(ex); }
        }
        if (sources.Count == 0)
            throw new InvalidDataException("所有更新清单来源均不可用或未通过签名验证。", new AggregateException(failures));
        cancellationToken.ThrowIfCancellationRequested();
        return sources;
    }

    static TimeSpan ValidateSourceTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(manifestSourceTimeout), value,
                "Manifest source timeout must be greater than zero and no more than ten minutes.");
        return value;
    }

    CancellationTokenRegistration RegisterPersistenceCancellationBarrier(CancellationToken cancellationToken)
        => cancellationToken.Register(static state =>
        {
            var client = (UpdateClient)state!;
            lock (client.persistenceSync) { }
        }, this);

    void CommitWhileActive(CancellationToken cancellationToken, Action commit)
    {
        lock (persistenceSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            commit();
        }
    }

    static DateTimeOffset ParsePublishedAt(string value)
        => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    static bool HasSameSignedDecision(UpdateManifestPayload left, UpdateManifestPayload right)
    {
        if (left.Schema != right.Schema || left.Channel != right.Channel || left.ReleaseId != right.ReleaseId || left.PublishedAtUtc != right.PublishedAtUtc || left.RolloutPercentage != right.RolloutPercentage || left.MinimumProtocolVersion != right.MinimumProtocolVersion || left.Artifacts.Length != right.Artifacts.Length)
            return false;
        var leftArtifacts = left.Artifacts.OrderBy(artifact => artifact.Platform, StringComparer.Ordinal).ToArray();
        var rightArtifacts = right.Artifacts.OrderBy(artifact => artifact.Platform, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < leftArtifacts.Length; index++)
        {
            var leftArtifact = leftArtifacts[index];
            var rightArtifact = rightArtifacts[index];
            if (!HasSameArtifactIdentity(leftArtifact, rightArtifact) ||
                !string.Equals(leftArtifact.InstallerUrl, rightArtifact.InstallerUrl, StringComparison.Ordinal) ||
                !string.Equals(leftArtifact.Notes, rightArtifact.Notes, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    static bool HasSameArtifactIdentity(UpdateArtifact left, UpdateArtifact right)
        => left.Platform == right.Platform && left.Version == right.Version && left.Build == right.Build && left.Size == right.Size && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);

    void PersistConflictFloor(IReadOnlyList<VerifiedManifestSource> conflictingSources, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (conflictingSources.Count < 2) throw new ArgumentException("至少需要两份冲突清单。", nameof(conflictingSources));
        var publishedAtUtc = conflictingSources[0].Manifest.PublishedAtUtc;
        if (conflictingSources.Any(source => source.Manifest.PublishedAtUtc != publishedAtUtc))
            throw new InvalidDataException("冲突清单的发布时间不一致。");

        var existing = LoadManifestFloor();
        if (existing is not null && ParsePublishedAt(existing.PublishedAtUtc) > ParsePublishedAt(publishedAtUtc)) return;
        var decisions = conflictingSources.Select(source => DecisionFingerprint(source.Manifest))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (decisions.Length < 2) throw new InvalidDataException("冲突清单没有不同的发布决定。");
        var markerText = "TabLink signed manifest conflict v1\n" + string.Join('\n', decisions);
        var marker = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(markerText)));
        if (existing is not null && existing.PublishedAtUtc == publishedAtUtc &&
            marker.Equals(existing.DecisionSha256, StringComparison.OrdinalIgnoreCase)) return;
        SaveManifestFloor(new(1, publishedAtUtc, marker), cancellationToken);
    }

    void AcceptManifestFloor(UpdateManifestPayload manifest, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = LoadManifestFloor();
            var published = ParsePublishedAt(manifest.PublishedAtUtc);
            var decision = DecisionFingerprint(manifest);
            if (existing is not null)
            {
                var floor = ParsePublishedAt(existing.PublishedAtUtc);
                if (published < floor)
                    throw new InvalidDataException("更新清单早于本机已接受的签名决定。");
                if (published == floor && !decision.Equals(existing.DecisionSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新清单与本机同一时间的签名决定冲突。");
                if (published == floor) return;
            }
            SaveManifestFloor(new(1, manifest.PublishedAtUtc, decision), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ClearPendingMetadata(cancellationToken);
            throw new UpdatePolicyBlockedException(
                "已验证的更新决定未能通过防回放检查或持久保存；已阻止复用旧更新。", ex);
        }
    }

    AcceptedManifestFloor? LoadManifestFloor()
    {
        var path = Path.Combine(cacheDirectory, "manifest-floor.json");
        if (!File.Exists(path)) return null;
        try
        {
            var floor = JsonSerializer.Deserialize<AcceptedManifestFloor>(File.ReadAllBytes(path), MetadataOptions)
                ?? throw new InvalidDataException("更新防回放记录为空。");
            if (floor.SchemaVersion != 1 || string.IsNullOrWhiteSpace(floor.PublishedAtUtc) ||
                floor.DecisionSha256?.Length != 64)
                throw new InvalidDataException("更新防回放记录无效。");
            _ = ParsePublishedAt(floor.PublishedAtUtc);
            _ = Convert.FromHexString(floor.DecisionSha256);
            return floor;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or FormatException or ArgumentException)
        { throw new InvalidDataException("更新防回放记录无法验证。", ex); }
    }

    void RequireManifestAtFloor(UpdateManifestPayload manifest)
    {
        var floor = LoadManifestFloor();
        if (floor is null) return;
        var published = ParsePublishedAt(manifest.PublishedAtUtc);
        var accepted = ParsePublishedAt(floor.PublishedAtUtc);
        var decision = DecisionFingerprint(manifest);
        if (published < accepted || published == accepted &&
            !decision.Equals(floor.DecisionSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("缓存更新不符合本机已接受的最新签名决定。");
    }

    void SaveManifestFloor(AcceptedManifestFloor floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, "manifest-floor.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, floor, MetadataOptions); stream.Flush(true); }
            CommitWhileActive(cancellationToken, () => File.Move(temporary, path, true));
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    static string DecisionFingerprint(UpdateManifestPayload manifest)
    {
        var canonical = new System.Text.StringBuilder();
        AppendDecision(canonical, manifest.Schema.ToString(CultureInfo.InvariantCulture));
        AppendDecision(canonical, manifest.Channel);
        AppendDecision(canonical, manifest.ReleaseId);
        AppendDecision(canonical, manifest.PublishedAtUtc);
        AppendDecision(canonical, manifest.RolloutPercentage.ToString(CultureInfo.InvariantCulture));
        AppendDecision(canonical, manifest.MinimumProtocolVersion.ToString(CultureInfo.InvariantCulture));
        foreach (var artifact in manifest.Artifacts.OrderBy(item => item.Platform, StringComparer.Ordinal))
        {
            AppendDecision(canonical, artifact.Platform);
            AppendDecision(canonical, artifact.Version);
            AppendDecision(canonical, artifact.Build.ToString(CultureInfo.InvariantCulture));
            AppendDecision(canonical, artifact.Size.ToString(CultureInfo.InvariantCulture));
            AppendDecision(canonical, artifact.Sha256.ToLowerInvariant());
            AppendDecision(canonical, artifact.InstallerUrl);
            AppendDecision(canonical, artifact.Notes);
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    static void AppendDecision(System.Text.StringBuilder target, string? value)
    {
        if (value is null) target.Append("-1:");
        else target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        target.Append(';');
    }

    static IReadOnlyList<UpdateArtifact> GetPackageMirrors(IReadOnlyList<VerifiedManifestSource> sources, string releaseId, UpdateArtifact authoritativeArtifact)
    {
        var mirrors = new List<UpdateArtifact>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Manifest.ReleaseId != releaseId) continue;
            var candidate = source.Manifest.Artifacts.SingleOrDefault(item => item.Platform == authoritativeArtifact.Platform);
            if (candidate is null || !HasSameArtifactIdentity(candidate, authoritativeArtifact) || !seenUrls.Add(candidate.Url)) continue;
            mirrors.Add(candidate);
        }
        if (mirrors.Count == 0) throw new InvalidDataException("有效更新清单中没有与发布内容匹配的下载地址。");
        return mirrors;
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
            RequireManifestAtFloor(manifest);
            var artifact = manifest.Artifacts.SingleOrDefault(a => a.Platform == "windows-x64");
            if (artifact is null || manifest.ReleaseId != pending.ReleaseId || artifact.Version != pending.Version || artifact.Build != pending.Build || artifact.Size != pending.Size || !artifact.Sha256.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            if (StableSemanticVersion.Parse(artifact.Version).CompareTo(currentVersion) <= 0 || !UpdateManifestVerifier.IsCohortIncluded(cohortId, manifest.ReleaseId, manifest.RolloutPercentage) || manifest.MinimumProtocolVersion > UpdateManifestVerifier.CurrentProtocolVersion) return null;
            return await ValidateFileAsync(fullPackage, artifact.Size, artifact.Sha256, cancellationToken).ConfigureAwait(false) ? pending : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException) { return null; }
    }

    async Task<PendingWindowsUpdate> DownloadAsync(byte[] envelope, UpdateManifestPayload manifest, UpdateArtifact artifact, IReadOnlyList<UpdateArtifact> mirrors, CancellationToken cancellationToken)
    {
        var releaseKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.ReleaseId))).ToLowerInvariant()[..24];
        var releaseDirectory = Path.Combine(cacheDirectory, "packages", releaseKey);
        Directory.CreateDirectory(releaseDirectory);
        var destination = Path.Combine(releaseDirectory, "TabLink-windows-x64.zip");
        var envelopePath = Path.Combine(releaseDirectory, "signed-manifest.json");
        if (!await ValidateFileAsync(destination, artifact.Size, artifact.Sha256, cancellationToken).ConfigureAwait(false))
        {
            var failures = new List<Exception>();
            var downloaded = false;
            foreach (var mirror in mirrors)
            {
                try
                {
                    await DownloadPackageAsync(mirror, destination, cancellationToken).ConfigureAwait(false);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { failures.Add(ex); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or FormatException or ArgumentException or NotSupportedException)
                { failures.Add(ex); }
            }
            if (!downloaded)
                throw new InvalidDataException("所有已签名更新包来源均下载失败或未通过完整性验证。", new AggregateException(failures));
        }
        cancellationToken.ThrowIfCancellationRequested();
        SaveBytes(envelopePath, envelope, cancellationToken);
        var pending = new PendingWindowsUpdate(manifest.ReleaseId, artifact.Version, artifact.Build, destination, envelopePath, artifact.Size, artifact.Sha256.ToUpperInvariant(), artifact.Notes);
        SaveMetadata(pending, cancellationToken);
        return pending;
    }

    async Task DownloadPackageAsync(UpdateArtifact artifact, string destination, CancellationToken cancellationToken)
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
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
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
            }
            CommitWhileActive(cancellationToken, () => File.Move(temporary, destination, true));
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
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

    void SaveMetadata(PendingWindowsUpdate pending, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, "pending-windows.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, pending, MetadataOptions); stream.Flush(true); }
            CommitWhileActive(cancellationToken, () => File.Move(temporary, path, true));
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    void ClearPendingMetadata(CancellationToken cancellationToken)
    {
        CommitWhileActive(cancellationToken, () =>
        {
            try { File.Delete(Path.Combine(cacheDirectory, "pending-windows.json")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    void SaveBytes(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            CommitWhileActive(cancellationToken, () => File.Move(temporary, path, true));
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    sealed record VerifiedManifestSource(byte[] Envelope, UpdateManifestPayload Manifest);
}
