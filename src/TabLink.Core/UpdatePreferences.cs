using System.Security;
using System.Text;
using System.Text.Json;

namespace TabLink.Core;

/// <summary>Controls how TabLink checks, downloads and applies Windows updates.</summary>
public enum UpdateMode
{
    Automatic,
    DownloadThenAsk,
    Never
}

/// <summary>A non-security UI preference kept separate from device policy settings.</summary>
public sealed record UpdatePreferences(UpdateMode Mode)
{
    public static UpdatePreferences Default { get; } = new(UpdateMode.Automatic);
    public static UpdatePreferences FailClosed { get; } = new(UpdateMode.Never);
}

public enum UpdatePreferencesLoadStatus
{
    Loaded,
    MissingDefault,
    InvalidFailClosed
}

public sealed record UpdatePreferencesLoadResult(
    UpdatePreferences Preferences,
    UpdatePreferencesLoadStatus Status)
{
    public bool HasError => Status == UpdatePreferencesLoadStatus.InvalidFailClosed;
}

/// <summary>
/// Stores update behavior independently of the fail-closed USB device policy.
/// Missing preferences retain the historical automatic behavior; an existing
/// file that cannot be trusted disables updates without rewriting that file.
/// </summary>
public sealed class UpdatePreferencesStore(string path)
{
    private const int CurrentSchema = 1;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public UpdatePreferences Load() => LoadWithStatus().Preferences;

    public UpdatePreferencesLoadResult LoadWithStatus()
    {
        try
        {
            var content = File.ReadAllText(Path, StrictUtf8);
            using var document = JsonDocument.Parse(content);
            if (!TryRead(document.RootElement, out var preferences))
                return InvalidResult();
            return new(preferences, UpdatePreferencesLoadStatus.Loaded);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(UpdatePreferences.Default, UpdatePreferencesLoadStatus.MissingDefault);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            SecurityException or JsonException or DecoderFallbackException)
        {
            return InvalidResult();
        }
    }

    public void Save(UpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var mode = ToStoredMode(preferences.Mode);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stored = new StoredPreferences(CurrentSchema, mode);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, stored, WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool TryRead(JsonElement root, out UpdatePreferences preferences)
    {
        preferences = UpdatePreferences.FailClosed;
        if (root.ValueKind != JsonValueKind.Object) return false;

        JsonElement schemaElement = default;
        JsonElement modeElement = default;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            if (property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
                schemaElement = property.Value;
            else if (property.Name.Equals("mode", StringComparison.OrdinalIgnoreCase))
                modeElement = property.Value;
            else return false;
        }

        if (names.Count != 2 || schemaElement.ValueKind != JsonValueKind.Number ||
            !schemaElement.TryGetInt32(out var schema) || schema != CurrentSchema ||
            modeElement.ValueKind != JsonValueKind.String)
            return false;

        var mode = modeElement.GetString() switch
        {
            "automatic" => UpdateMode.Automatic,
            "downloadThenAsk" => UpdateMode.DownloadThenAsk,
            "never" => UpdateMode.Never,
            _ => (UpdateMode?)null
        };
        if (mode is null) return false;
        preferences = new UpdatePreferences(mode.Value);
        return true;
    }

    private static string ToStoredMode(UpdateMode mode) => mode switch
    {
        UpdateMode.Automatic => "automatic",
        UpdateMode.DownloadThenAsk => "downloadThenAsk",
        UpdateMode.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown update mode.")
    };

    private static UpdatePreferencesLoadResult InvalidResult() =>
        new(UpdatePreferences.FailClosed, UpdatePreferencesLoadStatus.InvalidFailClosed);

    private sealed record StoredPreferences(int SchemaVersion, string Mode);
}
