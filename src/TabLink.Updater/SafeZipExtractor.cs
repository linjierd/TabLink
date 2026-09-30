using System.IO.Compression;
using System.Security.Cryptography;
using TabLink.Windows;

namespace TabLink.Updater;

internal sealed record ExtractedUpdateFile(string RelativePath, long Length, string Sha256);
internal sealed record ExtractedUpdatePackage(IReadOnlyList<ExtractedUpdateFile> Files, IReadOnlyList<string> Directories);

internal static class SafeZipExtractor
{
    const int MaximumEntries = 20_000;
    const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;

    static readonly string[] RequiredFiles =
    [
        "TabLink.exe", "TabLink.dll", "TabLink.deps.json", "TabLink.runtimeconfig.json",
        "TabLink.Updater.exe", "TabLink.Updater.dll", "TabLink.Updater.deps.json", "TabLink.Updater.runtimeconfig.json",
        "update-channel.json"
    ];

    internal static ExtractedUpdatePackage Extract(Stream archiveStream, string destination, bool protectForElevatedExecution = false)
    {
        ValidateArchiveStream(archiveStream);
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(destinationRoot)?.FullName ?? throw new InvalidDataException("更新暂存目录不能是卷根目录。");
        if (Directory.Exists(destinationRoot) || File.Exists(destinationRoot)) throw new IOException("更新暂存目录已经存在。");
        if (protectForElevatedExecution) ProtectedUpdaterStager.EnsureProtectedDirectory(destinationRoot, parent);
        else Directory.CreateDirectory(destinationRoot);

        var destinationPrefix = destinationRoot + Path.DirectorySeparatorChar;
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateEntryCount(archive);
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ExtractedUpdateFile>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntry(entry, names);
            var output = ResolveOutput(destinationRoot, destinationPrefix, normalized.RelativePath);
            if (normalized.IsDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("更新包目录项无效。");
                EnsureDirectoryPath(output, destinationRoot, protectForElevatedExecution, directories);
                continue;
            }

            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("更新包解压后过大。");
            EnsureDirectoryPath(Path.GetDirectoryName(output)!, destinationRoot, protectForElevatedExecution, directories);
            using var input = entry.Open();
            using var target = protectForElevatedExecution
                ? ProtectedUpdaterStager.CreateProtectedFile(output, Path.GetDirectoryName(output)!)
                : new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
            var hash = CopyAndHash(input, target);
            target.Flush(true);
            if (target.Length != entry.Length) throw new InvalidDataException("更新包文件长度无效。");
            files.Add(new(normalized.RelativePath, entry.Length, Convert.ToHexString(hash)));
        }

        var result = NormalizeManifest(files, directories);
        ValidateRequiredFiles(result);
        ValidateExtractedTree(destinationRoot, result, protectForElevatedExecution, verifyContent: false);
        return result;
    }

    internal static void VerifyExtractedFiles(Stream archiveStream, string destination, ExtractedUpdatePackage expected, bool requireProtectedTree)
    {
        ValidateArchiveStream(archiveStream);
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedExpected = NormalizeManifest(expected.Files, expected.Directories);
        ValidateRequiredFiles(normalizedExpected);
        if (requireProtectedTree) ProtectedUpdaterStager.VerifyProtectedTree(destinationRoot);

        var archiveManifest = ReadArchiveManifest(archiveStream);
        if (!ManifestEquals(normalizedExpected, archiveManifest)) throw new InvalidDataException("暂存文件清单与已锁定签名 ZIP 不一致。");
        ValidateExtractedTree(destinationRoot, normalizedExpected, requireProtectedTree, verifyContent: true);
        if (requireProtectedTree) ProtectedUpdaterStager.VerifyProtectedTree(destinationRoot);
    }

    static ExtractedUpdatePackage ReadArchiveManifest(Stream archiveStream)
    {
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateEntryCount(archive);
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ExtractedUpdateFile>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntry(entry, names);
            AddParentDirectories(normalized.RelativePath, normalized.IsDirectory, directories);
            if (normalized.IsDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("更新包目录项无效。");
                directories.Add(normalized.RelativePath);
                continue;
            }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("更新包解压后过大。");
            using var input = entry.Open();
            var hash = HashStream(input);
            files.Add(new(normalized.RelativePath, entry.Length, Convert.ToHexString(hash)));
        }
        var result = NormalizeManifest(files, directories);
        ValidateRequiredFiles(result);
        return result;
    }

    static void ValidateExtractedTree(string destinationRoot, ExtractedUpdatePackage expected, bool requireProtectedTree, bool verifyContent)
    {
        if (!Directory.Exists(destinationRoot) || (File.GetAttributes(destinationRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新暂存目录不存在或是重解析点。");
        var actualFiles = Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories)
            .Select(path => ToRelative(destinationRoot, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var actualDirectories = Directory.EnumerateDirectories(destinationRoot, "*", SearchOption.AllDirectories)
            .Select(path => ToRelative(destinationRoot, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var expectedFiles = expected.Files.Select(file => file.RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var expectedDirectories = expected.Directories.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!actualFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal) || !actualDirectories.SequenceEqual(expectedDirectories, StringComparer.Ordinal))
            throw new InvalidDataException("更新暂存目录包含缺失或额外的文件系统项。");

        foreach (var path in Directory.EnumerateFileSystemEntries(destinationRoot, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新暂存目录包含重解析点。");
        if (!verifyContent) return;

        var locked = new List<(ExtractedUpdateFile Expected, FileStream Stream)>();
        try
        {
            foreach (var expectedFile in expected.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
            {
                var path = ResolveOutput(destinationRoot, destinationRoot + Path.DirectorySeparatorChar, expectedFile.RelativePath);
                if (requireProtectedTree) ProtectedUpdaterStager.VerifyProtectedFile(path);
                locked.Add((expectedFile, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.SequentialScan)));
            }
            foreach (var item in locked)
            {
                if (item.Stream.Length != item.Expected.Length ||
                    !CryptographicOperations.FixedTimeEquals(HashAndRewind(item.Stream), Convert.FromHexString(item.Expected.Sha256)))
                    throw new InvalidDataException("更新暂存文件与已锁定签名 ZIP 不一致：" + item.Expected.RelativePath);
            }
        }
        finally
        {
            for (var index = locked.Count - 1; index >= 0; index--) locked[index].Stream.Dispose();
        }
    }

    static (string RelativePath, bool IsDirectory) NormalizeEntry(ZipArchiveEntry entry, HashSet<string> names)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is 0xA000 or 0x6000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新包不能包含链接或重解析点。");
        var name = entry.FullName.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':')) throw new InvalidDataException("更新包包含无效路径。");
        var directoryEntry = name.EndsWith('/');
        var pathName = directoryEntry ? name[..^1] : name;
        var parts = pathName.Split('/');
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("更新包包含越界或含空段的路径。");
        var relative = string.Join('/', parts);
        if (!names.Add(relative)) throw new InvalidDataException("更新包包含重复路径。");
        return (relative, directoryEntry);
    }

    static void EnsureDirectoryPath(string directory, string root, bool protect, HashSet<string> directories)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullDirectory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
        var relative = Path.GetRelativePath(fullRoot, fullDirectory);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative))
            throw new InvalidDataException("更新包目录越过暂存根目录。");
        var current = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = current;
            current = Path.Combine(current, part);
            if (protect) ProtectedUpdaterStager.EnsureProtectedDirectory(current, parent);
            else Directory.CreateDirectory(current);
            directories.Add(ToRelative(fullRoot, current));
        }
    }

    static void AddParentDirectories(string relativePath, bool isDirectory, HashSet<string> directories)
    {
        var parts = relativePath.Split('/');
        var count = isDirectory ? parts.Length : parts.Length - 1;
        for (var index = 1; index <= count; index++) directories.Add(string.Join('/', parts[..index]));
    }

    static ExtractedUpdatePackage NormalizeManifest(IEnumerable<ExtractedUpdateFile> files, IEnumerable<string> directories)
    {
        var normalizedFiles = files.Select(file => new ExtractedUpdateFile(
                NormalizeRelative(file.RelativePath), file.Length, NormalizeSha256(file.Sha256)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        if (normalizedFiles.Length == 0 || normalizedFiles.Length > MaximumEntries || normalizedFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedFiles.Length)
            throw new InvalidDataException("更新文件清单数量或名称无效。");
        var normalizedDirectories = directories.Select(NormalizeRelative).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (normalizedDirectories.Intersect(normalizedFiles.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("更新清单中的文件和目录冲突。");
        return new(normalizedFiles, normalizedDirectories);
    }

    static bool ManifestEquals(ExtractedUpdatePackage left, ExtractedUpdatePackage right)
    {
        if (!left.Directories.SequenceEqual(right.Directories, StringComparer.Ordinal) || left.Files.Count != right.Files.Count) return false;
        for (var index = 0; index < left.Files.Count; index++)
        {
            var a = left.Files[index];
            var b = right.Files[index];
            if (a.RelativePath != b.RelativePath || a.Length != b.Length || !a.Sha256.Equals(b.Sha256, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    static void ValidateRequiredFiles(ExtractedUpdatePackage package)
    {
        var names = new HashSet<string>(package.Files.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var file in RequiredFiles)
            if (!names.Contains(file)) throw new InvalidDataException("完整更新包根目录缺少 " + file + "。");
    }

    static string ResolveOutput(string root, string prefix, string relative)
    {
        var output = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包试图写出暂存目录。");
        return output;
    }

    static string ToRelative(string root, string path) => NormalizeRelative(Path.GetRelativePath(root, path).Replace('\\', '/'));

    static string NormalizeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains(':')) throw new InvalidDataException("更新相对路径无效。");
        var parts = value.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("更新相对路径越界或含空段。");
        return string.Join('/', parts);
    }

    static string NormalizeSha256(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("更新文件 SHA-256 无效。");
        return value.ToUpperInvariant();
    }

    static byte[] CopyAndHash(Stream input, Stream output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    static byte[] HashStream(Stream input)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }

    static byte[] HashAndRewind(FileStream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return hash;
    }

    static void ValidateArchiveStream(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("更新包流必须可读取并可定位。", nameof(stream));
    }

    static void ValidateEntryCount(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("更新包文件数量无效。");
    }
}
