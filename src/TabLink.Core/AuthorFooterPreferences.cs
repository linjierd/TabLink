using System.Security;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Core;

/// <summary>User-editable attribution displayed in the Windows footer.</summary>
public sealed class AuthorFooterPreferences
{
    public const int MaximumAuthorTextLength = 160;
    public const int MaximumLinkLabelLength = 64;
    public const int MaximumUrlLength = 2048;

    public bool Enabled { get; set; } = true;
    public string AuthorText { get; set; } = "作者：张林杰（Jey / @linjierd）";
    public string GitHubLabel { get; set; } = "GitHub";
    public string GitHubUrl { get; set; } = "https://github.com/linjierd";
    public string BlogLabel { get; set; } = "博客：linjie.space";
    public string BlogUrl { get; set; } = "https://linjie.space/";

    public static AuthorFooterPreferences CreateDefault() => new();

    public AuthorFooterPreferences NormalizeAndValidate()
    {
        var github = NormalizeLinkPair(GitHubLabel, GitHubUrl, nameof(GitHubLabel), nameof(GitHubUrl));
        var blog = NormalizeLinkPair(BlogLabel, BlogUrl, nameof(BlogLabel), nameof(BlogUrl));
        return new AuthorFooterPreferences
        {
            Enabled = Enabled,
            AuthorText = NormalizeText(AuthorText, nameof(AuthorText), MaximumAuthorTextLength, allowEmpty: false),
            GitHubLabel = github.Label,
            GitHubUrl = github.Url,
            BlogLabel = blog.Label,
            BlogUrl = blog.Url
        };
    }

    private static (string Label, string Url) NormalizeLinkPair(
        string? label, string? url, string labelPropertyName, string urlPropertyName)
    {
        var normalizedLabel = NormalizeText(label, labelPropertyName, MaximumLinkLabelLength, allowEmpty: true);
        var normalizedUrl = NormalizeText(url, urlPropertyName, MaximumUrlLength, allowEmpty: true);
        if (normalizedLabel.Length == 0 && normalizedUrl.Length == 0) return ("", "");
        if (normalizedLabel.Length == 0 || normalizedUrl.Length == 0)
            throw new ArgumentException($"{labelPropertyName} and {urlPropertyName} must both be set or both be empty.");
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.IdnHost) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException($"{urlPropertyName} must be an absolute HTTPS URL without embedded credentials.", urlPropertyName);
        var absolute = uri.AbsoluteUri;
        if (absolute.Length > MaximumUrlLength)
            throw new ArgumentException($"{urlPropertyName} is too long.", urlPropertyName);
        return (normalizedLabel, absolute);
    }

    private static string NormalizeText(
        string? value, string propertyName, int maximumLength, bool allowEmpty)
    {
        if (value is null || ContainsUnsafeDisplayCharacter(value))
            throw new ArgumentException($"{propertyName} must contain safe display text.", propertyName);
        var normalized = value.Trim();
        if ((!allowEmpty && normalized.Length == 0) || normalized.Length > maximumLength)
            throw new ArgumentException($"{propertyName} has an invalid length.", propertyName);
        return normalized;
    }

    private static bool ContainsUnsafeDisplayCharacter(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            Rune rune;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length ||
                    !Rune.TryCreate(character, value[index + 1], out rune))
                    return true;
                index++;
            }
            else if (char.IsLowSurrogate(character)) return true;
            else rune = new Rune(character);

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Stores the footer as a non-security UI preference. A missing, damaged or
/// future-version file safely falls back to defaults and never affects USB policy.
/// </summary>
public sealed class AuthorFooterPreferencesStore(string path)
{
    private const int CurrentSchema = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public AuthorFooterPreferences Load()
    {
        try
        {
            if (!File.Exists(Path)) return AuthorFooterPreferences.CreateDefault();
            var content = File.ReadAllText(Path);
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !HasCompleteUniqueShape(document.RootElement))
                return AuthorFooterPreferences.CreateDefault();
            var stored = JsonSerializer.Deserialize<StoredPreferences>(content, Options);
            if (stored is null || stored.SchemaVersion != CurrentSchema)
                return AuthorFooterPreferences.CreateDefault();
            return new AuthorFooterPreferences
            {
                Enabled = stored.Enabled,
                AuthorText = stored.AuthorText ?? "",
                GitHubLabel = stored.GitHubLabel ?? "",
                GitHubUrl = stored.GitHubUrl ?? "",
                BlogLabel = stored.BlogLabel ?? "",
                BlogUrl = stored.BlogUrl ?? ""
            }.NormalizeAndValidate();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            SecurityException or JsonException or ArgumentException)
        {
            return AuthorFooterPreferences.CreateDefault();
        }
    }

    public void Save(AuthorFooterPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.NormalizeAndValidate();
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stored = new StoredPreferences
            {
                SchemaVersion = CurrentSchema,
                Enabled = normalized.Enabled,
                AuthorText = normalized.AuthorText,
                GitHubLabel = normalized.GitHubLabel,
                GitHubUrl = normalized.GitHubUrl,
                BlogLabel = normalized.BlogLabel,
                BlogUrl = normalized.BlogUrl
            };
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, stored, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool HasCompleteUniqueShape(JsonElement element)
    {
        if (!HasUniquePropertiesRecursively(element)) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            names.Add(property.Name);
        string[] required =
        [
            nameof(StoredPreferences.SchemaVersion), nameof(StoredPreferences.Enabled),
            nameof(StoredPreferences.AuthorText), nameof(StoredPreferences.GitHubLabel),
            nameof(StoredPreferences.GitHubUrl), nameof(StoredPreferences.BlogLabel),
            nameof(StoredPreferences.BlogUrl)
        ];
        return required.All(names.Contains);
    }

    private static bool HasUniquePropertiesRecursively(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniquePropertiesRecursively(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (!HasUniquePropertiesRecursively(item)) return false;
        }
        return true;
    }

    private sealed class StoredPreferences
    {
        public int SchemaVersion { get; set; }
        public bool Enabled { get; set; }
        public string? AuthorText { get; set; }
        public string? GitHubLabel { get; set; }
        public string? GitHubUrl { get; set; }
        public string? BlogLabel { get; set; }
        public string? BlogUrl { get; set; }
    }
}
