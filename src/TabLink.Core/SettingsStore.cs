using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Core;

public sealed class SettingsStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public DevicePolicySettings Load()
    {
        try
        {
            string content = File.ReadAllText(Path);
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Settings must be an object.");
            ValidateUniqueProperties(document.RootElement);
            var names = document.RootElement.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(nameof(DevicePolicySettings.SchemaVersion)) || !names.Contains(nameof(DevicePolicySettings.ExcludedDevices)))
                throw new JsonException("Missing settings version or exclusion rules.");
            var settings = JsonSerializer.Deserialize<DevicePolicySettings>(content, Options) ?? throw new JsonException("Empty settings.");
            DevicePolicy.ValidateSettings(settings);
            return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Never silently replace a damaged exclusion list with defaults.
            throw new SettingsLoadException("Cannot load settings. Device actions must remain disabled until this file is recovered: " + Path, ex);
        }
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate settings property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateUniqueProperties(item);
    }

    public void Save(DevicePolicySettings settings)
    {
        DevicePolicy.ValidateSettings(settings);
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class SettingsLoadException(string message, Exception innerException) : IOException(message, innerException);
