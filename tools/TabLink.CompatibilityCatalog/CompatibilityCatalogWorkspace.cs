using System.Text;

namespace TabLink.CompatibilityCatalog;

public interface ICompatibilityCatalogPathGuard
{
    void RequireSafeDirectory(string path, bool mustExist);
    void RequireSafeRegularFile(string path, bool mustExist);
}

public sealed class CompatibilityCatalogWorkspace
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private readonly string repositoryRoot;
    private readonly string compatibilityDirectory;
    private readonly string catalogPath;
    private readonly string schemaPath;
    private readonly string readmePath;
    private readonly ICompatibilityCatalogPathGuard pathGuard;

    public CompatibilityCatalogWorkspace(string repositoryRoot)
        : this(repositoryRoot, SystemCompatibilityCatalogPathGuard.Instance)
    {
    }

    public CompatibilityCatalogWorkspace(string repositoryRoot, ICompatibilityCatalogPathGuard pathGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
        try
        {
            this.repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("Repository root is unavailable.", exception);
        }

        this.pathGuard.RequireSafeDirectory(this.repositoryRoot, mustExist: true);
        compatibilityDirectory = GetContainedPath(this.repositoryRoot, "compatibility");
        catalogPath = GetContainedPath(this.repositoryRoot, "compatibility", "catalog.json");
        schemaPath = GetContainedPath(this.repositoryRoot, "compatibility", "catalog.schema.json");
        readmePath = GetContainedPath(this.repositoryRoot, "compatibility", "README.md");
        this.pathGuard.RequireSafeDirectory(compatibilityDirectory, mustExist: false);
    }

    public CompatibilityCatalogDocument LoadAndValidate()
    {
        RequireSafeBase(requireCatalog: true);
        var bytes = ReadBounded(catalogPath, CompatibilityCatalogContract.MaximumCatalogBytes,
            "compatibility/catalog.json is missing or inaccessible.");
        RequireSafeBase(requireCatalog: true);
        return CompatibilityCatalogValidator.ParseAndValidate(bytes, repositoryRoot);
    }

    public void CheckGeneratedFiles()
    {
        var catalog = LoadAndValidate();
        RequireExact(schemaPath, CompatibilityCatalogGenerator.GenerateSchemaJson(),
            "compatibility/catalog.schema.json is missing or differs from the generated schema.");
        RequireExact(readmePath, CompatibilityCatalogGenerator.GenerateReadme(catalog),
            "compatibility/README.md is missing or differs from the generated catalog.");
        RequireSafeBase(requireCatalog: true);
    }

    public void WriteGeneratedFiles()
    {
        var catalog = LoadAndValidate();
        var schema = CompatibilityCatalogGenerator.GenerateSchemaJson();
        var readme = CompatibilityCatalogGenerator.GenerateReadme(catalog);
        RequireSafeBase(requireCatalog: true);
        pathGuard.RequireSafeRegularFile(schemaPath, mustExist: false);
        pathGuard.RequireSafeRegularFile(readmePath, mustExist: false);
        AtomicTextFile.Write(schemaPath, schema, compatibilityDirectory, pathGuard);
        RequireSafeBase(requireCatalog: true);
        AtomicTextFile.Write(readmePath, readme, compatibilityDirectory, pathGuard);
        RequireSafeBase(requireCatalog: true);
        pathGuard.RequireSafeRegularFile(schemaPath, mustExist: true);
        pathGuard.RequireSafeRegularFile(readmePath, mustExist: true);
    }

    private void RequireSafeBase(bool requireCatalog)
    {
        pathGuard.RequireSafeDirectory(repositoryRoot, mustExist: true);
        pathGuard.RequireSafeDirectory(compatibilityDirectory, mustExist: true);
        pathGuard.RequireSafeRegularFile(catalogPath, mustExist: requireCatalog);
    }

    private void RequireExact(string path, string expected, string message)
    {
        RequireSafeBase(requireCatalog: true);
        pathGuard.RequireSafeRegularFile(path, mustExist: true);
        byte[] actual;
        try
        {
            actual = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(message, exception);
        }
        pathGuard.RequireSafeRegularFile(path, mustExist: true);
        RequireSafeBase(requireCatalog: true);
        var expectedBytes = Utf8NoBom.GetBytes(expected);
        if (!actual.AsSpan().SequenceEqual(expectedBytes))
            throw new InvalidDataException(message);
    }

    private static byte[] ReadBounded(string path, int maximumBytes, string message)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
                throw new InvalidDataException("compatibility/catalog.json is empty or exceeds the size limit.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new InvalidDataException("compatibility/catalog.json changed while it was being read.");
            return bytes;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static string GetContainedPath(string root, params string[] relativeSegments)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine([root, .. relativeSegments]));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("Compatibility catalog path is unavailable.", exception);
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException("Compatibility catalog path must remain inside the repository root.");
        return candidate;
    }

    private static class AtomicTextFile
    {
        public static void Write(
            string destinationPath,
            string content,
            string expectedDirectory,
            ICompatibilityCatalogPathGuard pathGuard)
        {
            var destination = Path.GetFullPath(destinationPath);
            var destinationDirectory = Path.GetDirectoryName(destination) ??
                                       throw new InvalidDataException("Generated output directory is unavailable.");
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(destinationDirectory, expectedDirectory, comparison))
                throw new InvalidDataException("Generated output must remain inside the compatibility directory.");

            pathGuard.RequireSafeDirectory(expectedDirectory, mustExist: true);
            pathGuard.RequireSafeRegularFile(destination, mustExist: false);
            var destinationName = Path.GetFileName(destination);
            var temporaryPath = Path.GetFullPath(Path.Combine(expectedDirectory,
                "." + destinationName + "." + Guid.NewGuid().ToString("N") + ".tmp"));
            if (!string.Equals(Path.GetDirectoryName(temporaryPath), expectedDirectory, comparison))
                throw new InvalidDataException("Generated temporary output must remain inside the compatibility directory.");

            var bytes = Utf8NoBom.GetBytes(content);
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 16 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                pathGuard.RequireSafeDirectory(expectedDirectory, mustExist: true);
                pathGuard.RequireSafeRegularFile(temporaryPath, mustExist: true);
                pathGuard.RequireSafeRegularFile(destination, mustExist: false);
                File.Move(temporaryPath, destination, overwrite: true);
                pathGuard.RequireSafeDirectory(expectedDirectory, mustExist: true);
                pathGuard.RequireSafeRegularFile(destination, mustExist: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the primary failure. A later --write can safely ignore controlled .tmp files.
                }
            }
        }
    }
}

internal sealed class SystemCompatibilityCatalogPathGuard : ICompatibilityCatalogPathGuard
{
    public static SystemCompatibilityCatalogPathGuard Instance { get; } = new();

    public void RequireSafeDirectory(string path, bool mustExist) =>
        Require(path, mustExist, expectDirectory: true);

    public void RequireSafeRegularFile(string path, bool mustExist) =>
        Require(path, mustExist, expectDirectory: false);

    private static void Require(string path, bool mustExist, bool expectDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (!mustExist &&
                                           exception is FileNotFoundException or DirectoryNotFoundException)
        {
            RequireNoReparseDirectoryChain(expectDirectory
                ? Directory.GetParent(Path.GetFullPath(path))?.FullName
                : Path.GetDirectoryName(Path.GetFullPath(path)));
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Compatibility catalog path is missing or inaccessible.", exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Compatibility catalog paths must not use reparse points or junctions.");
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
            throw new InvalidDataException(expectDirectory
                ? "Compatibility catalog directory is not a regular directory."
                : "Compatibility catalog file is not a regular file.");
        RequireNoReparseDirectoryChain(expectDirectory
            ? Path.GetFullPath(path)
            : Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    private static void RequireNoReparseDirectoryChain(string? startingDirectory)
    {
        var current = startingDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("Compatibility catalog path is missing or inaccessible.", exception);
            }
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Compatibility catalog directory chain must use regular directories only.");

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }
}
