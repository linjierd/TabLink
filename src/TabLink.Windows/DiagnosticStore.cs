using System.Text.Json;

namespace TabLink.Windows;

/// <summary>
/// Best-effort observations only. Display leases and other recovery state must
/// continue using their own durable persistence and error handling.
/// </summary>
internal sealed class DiagnosticStore
{
    const int MaximumTrackedFailures = 64;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    readonly object gate = new();
    readonly HashSet<string> reportedFailures = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<Stream, byte[]> writePayload;
    bool overflowReported;

    internal DiagnosticStore(string folder, Action<Stream, byte[]>? writePayload = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Folder = folder;
        this.writePayload = writePayload ?? ((stream, payload) => stream.Write(payload));
    }

    internal string Folder { get; }

    internal bool TrySave(string name, Func<object?> snapshot, Action<string>? reportFailure = null)
    {
        string? warning = null;
        bool saved;
        // One writer per store also keeps failure/recovery episodes consistent.
        // Different processes can still write complete snapshots atomically.
        lock (gate)
        {
            string? temporaryPath = null;
            var failureKey = IsSafeName(name) ? name : "<invalid-name>";
            try
            {
                if (!IsSafeName(name)) throw new ArgumentException("诊断文件名必须是有效的 JSON 文件名。", nameof(name));
                // If Windows cannot resolve LocalApplicationData, do not fall
                // back to a relative directory beside the executable.
                if (!Path.IsPathFullyQualified(Folder)) throw new ArgumentException("诊断目录必须是绝对路径。", nameof(Folder));
                ArgumentNullException.ThrowIfNull(snapshot);
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot(), JsonOptions);
                Directory.CreateDirectory(Folder);
                var targetPath = Path.Combine(Folder, name);
                temporaryPath = Path.Combine(Folder, $".{name}.{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    writePayload(stream, payload);
                    stream.Flush();
                }
                // A same-directory rename replaces the complete file. A failed
                // capture, serialization or staged write never touches the last
                // good snapshot; a failed rename leaves it in place as well.
                File.Move(temporaryPath, targetPath, overwrite: true);
                temporaryPath = null;
                reportedFailures.Remove(failureKey);
                if (reportedFailures.Count == 0) overflowReported = false;
                saved = true;
            }
            catch (Exception error)
            {
                saved = false;
                if (reportFailure is not null && ShouldReportFailure(failureKey))
                {
                    // Reporting must be optional even for an exception whose
                    // custom Message implementation itself fails.
                    try { warning = $"诊断记录暂时无法保存（{name}）：{error.Message}。副屏连接将继续运行。"; }
                    catch { warning = "诊断记录暂时无法保存，副屏连接将继续运行。"; }
                }
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch { /* Cleanup failure cannot stop a display session. */ }
                }
            }
        }
        if (warning is not null)
        {
            try { reportFailure?.Invoke(warning); }
            catch { /* A logging/UI callback is also best effort. */ }
        }
        return saved;
    }

    bool ShouldReportFailure(string name)
    {
        if (reportedFailures.Contains(name)) return false;
        if (reportedFailures.Count < MaximumTrackedFailures)
        {
            reportedFailures.Add(name);
            return true;
        }
        // Production uses a small fixed set of internal names. Keep memory and
        // warnings bounded even if a future caller supplies arbitrary names.
        if (overflowReported) return false;
        overflowReported = true;
        return true;
    }

    static bool IsSafeName(string? name)
    {
        if (name is null || name.Length is < 6 or > 100 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name.AsSpan(0, name.Length - 5);
        foreach (var character in stem)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-' and not '_') return false;
        }
        var device = stem.ToString().ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL") &&
               !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] is >= '1' and <= '9');
    }
}
