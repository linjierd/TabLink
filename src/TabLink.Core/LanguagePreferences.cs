using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;

namespace TabLink.Core;

public enum ProductLanguage
{
    SimplifiedChinese,
    English
}

public enum ProductLanguageMode
{
    System,
    SimplifiedChinese,
    English
}

public enum LanguagePreferencesLoadStatus
{
    Loaded,
    MissingSystemDefault,
    InvalidSystemDefault
}

public sealed record LanguagePreferencesLoadResult(
    ProductLanguageMode Mode,
    ProductLanguage EffectiveLanguage,
    LanguagePreferencesLoadStatus Status);

/// <summary>
/// Stores an explicit product language independently from security-sensitive
/// device settings. A missing or damaged file follows the current UI culture:
/// every zh-* culture uses Simplified Chinese; all other cultures use English.
/// </summary>
public sealed class LanguagePreferencesStore(string path)
{
    private const int CurrentSchema = 1;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public LanguagePreferencesLoadResult Load(CultureInfo? uiCulture = null)
    {
        var systemDefault = FromCulture(uiCulture ?? CultureInfo.CurrentUICulture);
        try
        {
            var content = File.ReadAllText(Path, StrictUtf8);
            using var document = JsonDocument.Parse(content);
            if (!TryRead(document.RootElement, out var mode))
                return new(ProductLanguageMode.System, systemDefault, LanguagePreferencesLoadStatus.InvalidSystemDefault);
            return new(mode, Resolve(mode, uiCulture ?? CultureInfo.CurrentUICulture), LanguagePreferencesLoadStatus.Loaded);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(ProductLanguageMode.System, systemDefault, LanguagePreferencesLoadStatus.MissingSystemDefault);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
            JsonException or DecoderFallbackException)
        {
            return new(ProductLanguageMode.System, systemDefault, LanguagePreferencesLoadStatus.InvalidSystemDefault);
        }
    }

    public void Save(ProductLanguageMode mode)
    {
        var storedLanguage = mode switch
        {
            ProductLanguageMode.System => "system",
            ProductLanguageMode.SimplifiedChinese => "zh-CN",
            ProductLanguageMode.English => "en",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown product language mode.")
        };
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new StoredPreferences(CurrentSchema, storedLanguage), WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static ProductLanguage FromCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? ProductLanguage.SimplifiedChinese
            : ProductLanguage.English;
    }

    public static ProductLanguage Resolve(ProductLanguageMode mode, CultureInfo culture) => mode switch
    {
        ProductLanguageMode.System => FromCulture(culture),
        ProductLanguageMode.SimplifiedChinese => ProductLanguage.SimplifiedChinese,
        ProductLanguageMode.English => ProductLanguage.English,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown product language mode.")
    };

    private static bool TryRead(JsonElement root, out ProductLanguageMode mode)
    {
        mode = ProductLanguageMode.System;
        if (root.ValueKind != JsonValueKind.Object) return false;
        JsonElement schema = default;
        JsonElement storedLanguage = default;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            if (property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)) schema = property.Value;
            else if (property.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) storedLanguage = property.Value;
            else return false;
        }
        if (names.Count != 2 || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != CurrentSchema ||
            storedLanguage.ValueKind != JsonValueKind.String) return false;
        var parsed = storedLanguage.GetString() switch
        {
            "system" => ProductLanguageMode.System,
            "zh-CN" => ProductLanguageMode.SimplifiedChinese,
            "en" => ProductLanguageMode.English,
            _ => (ProductLanguageMode?)null
        };
        if (parsed is null) return false;
        mode = parsed.Value;
        return true;
    }

    private sealed record StoredPreferences(int SchemaVersion, string Language);
}
