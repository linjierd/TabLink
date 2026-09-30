using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Windows;

internal sealed record UpdateChannelFile(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("manifestUrl")] string? ManifestUrl,
    [property: JsonPropertyName("checkIntervalMinutes")] int CheckIntervalMinutes = 360);

internal sealed record UpdateClientIdentity([property: JsonPropertyName("cohortId")] string CohortId);

internal sealed record UpdateChannelConfiguration(bool Enabled, Uri? ManifestUri, TimeSpan CheckInterval, string CohortId)
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static UpdateChannelConfiguration Load(string applicationDirectory, string localDataDirectory)
    {
        var channelPath = Path.Combine(applicationDirectory, "update-channel.json");
        if (!File.Exists(channelPath)) return new(false, null, TimeSpan.FromHours(6), LoadOrCreateCohort(localDataDirectory));
        var file = JsonSerializer.Deserialize<UpdateChannelFile>(File.ReadAllBytes(channelPath), Options)
            ?? throw new InvalidDataException("自动更新频道配置为空。");
        if (file.CheckIntervalMinutes is < 60 or > 10080) throw new InvalidDataException("自动更新检查间隔必须是 60 到 10080 分钟。");
        Uri? manifest = null;
        if (file.Enabled)
        {
            if (string.IsNullOrWhiteSpace(file.ManifestUrl)) throw new InvalidDataException("自动更新已启用，但没有正式版清单地址。");
            manifest = UpdateManifestVerifier.ValidateHttps(file.ManifestUrl, "更新清单");
        }
        return new(file.Enabled, manifest, TimeSpan.FromMinutes(file.CheckIntervalMinutes), LoadOrCreateCohort(localDataDirectory));
    }

    static string LoadOrCreateCohort(string localDataDirectory)
    {
        Directory.CreateDirectory(localDataDirectory);
        var path = Path.Combine(localDataDirectory, "update-client.json");
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<UpdateClientIdentity>(File.ReadAllBytes(path), Options);
                if (existing is not null && Guid.TryParseExact(existing.CohortId, "N", out _)) return existing.CohortId;
                throw new InvalidDataException("本机更新标识无效。");
            }
            var identity = new UpdateClientIdentity(Guid.NewGuid().ToString("N"));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { JsonSerializer.Serialize(stream, identity, Options); stream.Flush(true); }
                File.Move(temporary, path, false);
            }
            catch (IOException) when (File.Exists(path)) { }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<UpdateClientIdentity>(File.ReadAllBytes(path), Options);
                if (saved is not null && Guid.TryParseExact(saved.CohortId, "N", out _)) return saved.CohortId;
            }
            return identity.CohortId;
        }
        catch (UnauthorizedAccessException) { return MachineBoundFallback(); }
        catch (IOException) { return MachineBoundFallback(); }
    }

    static string MachineBoundFallback()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "\n" + Environment.UserName));
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }
}
