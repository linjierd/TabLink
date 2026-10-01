using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Windows;

internal enum SupportConnectionPath { None, NativeNetwork, AdbCompatibility, Browser }
internal enum SupportQualityPreset { Automatic, LowLatency, Balanced, HighQuality }
internal enum SupportEncoderPreference { Automatic, Nvenc, Qsv, Amf, LibX264 }
internal enum SupportEncoderBackend { None, Nvenc, Qsv, Amf, LibX264 }
internal enum SupportHealthStage
{
    RouteAndListener,
    AuthenticationAndDisplayProfile,
    SingleVirtualDisplay,
    CaptureEncodeSend,
    ClientDecodeSubmission,
    ClientPresentationCallback
}
internal enum SupportHealthState { Waiting, Working, Healthy, Paused, Attention }
internal enum SupportHealthReason
{
    Idle,
    Starting,
    Ready,
    Authenticating,
    DisplayProfileReceived,
    DisplayPreparing,
    DisplayReady,
    PipelineStarting,
    FrameSent,
    CapturePaused,
    DecodeSubmitted,
    FramePresented,
    Reconnecting,
    NeedsAttention,
    Stopped
}
internal enum SupportHealthRecovery
{
    None,
    RefreshRoute,
    RecreatePairing,
    ConfigureDisplayMode,
    ReclaimOwnedDisplay,
    RestartVideo,
    OpenLogs
}

internal sealed record SupportDisplayProfile(
    int Width,
    int Height,
    int NativeWidth,
    int NativeHeight,
    int Rotation,
    int RequestedRefreshRate);

internal sealed record SupportVideoStatus(
    SupportEncoderBackend Encoder,
    bool HardwareEncoder,
    int Width,
    int Height,
    int RequestedFps,
    int EffectiveFps,
    bool CapturePaused,
    long FramesSent,
    long DecoderSubmittedFrames,
    long PresentationCallbacks,
    double? DecoderSubmittedFps,
    double? PresentationCallbackFps);

internal sealed record SupportHealthStep(
    SupportHealthStage Stage,
    SupportHealthState State,
    SupportHealthReason Reason,
    SupportHealthRecovery Recovery);

internal sealed record SupportBundleSnapshot(
    DateTimeOffset CreatedUtc,
    Version ApplicationVersion,
    Version WindowsVersion,
    Version FrameworkVersion,
    Architecture OsArchitecture,
    Architecture ProcessArchitecture,
    SupportQualityPreset QualityPreset,
    SupportEncoderPreference EncoderPreference,
    bool SoftwareFallbackEnabled,
    bool ClientConnected,
    SupportConnectionPath HealthPath,
    SupportDisplayProfile? Display,
    SupportVideoStatus? Video,
    IReadOnlyList<SupportHealthStep> Health);

internal sealed record PreparedSupportBundle(
    IReadOnlyDictionary<string, byte[]> Entries,
    string PreviewText);

internal readonly record struct SupportNativeCandidate(bool ClientConnected, SupportDisplayProfile? Display);
internal readonly record struct SupportCurrentConnection(bool ClientConnected, SupportDisplayProfile? Display, int NativeSourceIndex);

/// <summary>
/// Produces a deliberately narrow support artifact. It never scans or copies
/// the diagnostics, logs, settings, trust, USB cleanup or display lease folders.
/// Every exported field is represented by a typed allow-list DTO above.
/// </summary>
internal static class SupportBundleExporter
{
    internal const int SchemaVersion = 1;
    internal const int MaximumBundleBytes = 256 * 1024;
    static readonly DateTimeOffset FixedEntryTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    static readonly string[] FixedEntryOrder =
    [
        "manifest.json",
        "compatibility.json",
        "diagnostics.json",
        "issue-summary.txt",
        "README.txt"
    ];

    internal static PreparedSupportBundle Prepare(SupportBundleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        var createdUtc = snapshot.CreatedUtc.ToUniversalTime();
        var compatibility = new
        {
            schemaVersion = SchemaVersion,
            createdUtc,
            tabLinkVersion = snapshot.ApplicationVersion.ToString(),
            windowsVersion = snapshot.WindowsVersion.ToString(),
            frameworkVersion = snapshot.FrameworkVersion.ToString(),
            osArchitecture = snapshot.OsArchitecture,
            processArchitecture = snapshot.ProcessArchitecture,
            qualityPreset = snapshot.QualityPreset,
            encoderPreference = snapshot.EncoderPreference,
            snapshot.SoftwareFallbackEnabled,
            snapshot.Display,
            encoder = snapshot.Video is null ? null : new
            {
                snapshot.Video.Encoder,
                snapshot.Video.HardwareEncoder,
                snapshot.Video.Width,
                snapshot.Video.Height,
                snapshot.Video.RequestedFps,
                snapshot.Video.EffectiveFps
            }
        };
        var diagnostics = new
        {
            schemaVersion = SchemaVersion,
            createdUtc,
            snapshot.ClientConnected,
            snapshot.HealthPath,
            video = snapshot.Video,
            health = snapshot.Health
        };

        var compatibilityBytes = Serialize(compatibility);
        var diagnosticsBytes = Serialize(diagnostics);
        var issueSummary = BuildIssueSummary(snapshot);
        var privacy = BuildPrivacyNotice();
        var bodyEntries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["compatibility.json"] = compatibilityBytes,
            ["diagnostics.json"] = diagnosticsBytes,
            ["issue-summary.txt"] = Utf8.GetBytes(issueSummary),
            ["README.txt"] = Utf8.GetBytes(privacy)
        };
        var manifest = new
        {
            schemaVersion = SchemaVersion,
            createdUtc,
            entries = bodyEntries.Select(item => new
            {
                name = item.Key,
                size = item.Value.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(item.Value))
            }).ToArray(),
            deliberatelyExcluded = new[]
            {
                "raw logs and exception text",
                "raw diagnostics files",
                "user names and absolute paths",
                "IP, MAC and network interface identities",
                "USB, ADB, PnP, host and device identities",
                "pairing links, tokens, certificates, public keys and private keys",
                "settings, trust stores, display leases and USB cleanup receipts",
                "desktop captures and screenshots"
            }
        };
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Serialize(manifest)
        };
        foreach (var item in bodyEntries) entries.Add(item.Key, item.Value);
        if (!entries.Keys.SequenceEqual(FixedEntryOrder, StringComparer.Ordinal))
            throw new InvalidOperationException("支持包条目清单发生意外变化。");
        if (entries.Sum(item => item.Value.Length) > MaximumBundleBytes)
            throw new InvalidDataException("脱敏支持包内容超过安全大小限制。");

        var preview = string.Join("\r\n\r\n", FixedEntryOrder.Select(name =>
            $"===== {name} =====\r\n{Utf8.GetString(entries[name])}"));
        return new(entries, preview);
    }

    internal static void WriteAtomic(string destinationPath, PreparedSupportBundle prepared,
        Action<Stream, PreparedSupportBundle>? archiveWriter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("支持包目标必须是绝对路径。", nameof(destinationPath));
        var requestedLeaf = Path.GetFileName(destinationPath);
        ValidateDestinationLeaf(requestedLeaf);
        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(requestedLeaf, Path.GetFileName(destination), StringComparison.Ordinal))
            throw new ArgumentException("支持包文件名规范化后发生变化。", nameof(destinationPath));
        if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("支持包目标必须使用 .zip 扩展名。", nameof(destinationPath));
        ValidatePrepared(prepared);
        var folder = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("支持包目标目录无效。", nameof(destinationPath));
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("支持包目标目录不存在：" + folder);

        var temporary = Path.Combine(folder, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough))
            {
                (archiveWriter ?? WriteArchive)(stream, prepared);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                try { File.Delete(temporary); }
                catch { /* Never replace the destination with a partial archive. */ }
            }
        }
    }

    static void WriteArchive(Stream destination, PreparedSupportBundle prepared)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Utf8);
        foreach (var name in FixedEntryOrder)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedEntryTimestamp;
            entry.ExternalAttributes = 0;
            using var output = entry.Open();
            output.Write(prepared.Entries[name]);
        }
    }

    static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    static string BuildIssueSummary(SupportBundleSnapshot value)
    {
        var lines = new List<string>
        {
            "TabLink 脱敏支持摘要",
            $"Schema: {SchemaVersion}",
            $"Generated UTC: {value.CreatedUtc.ToUniversalTime():O}",
            $"TabLink: {value.ApplicationVersion}",
            $"Windows: {value.WindowsVersion} ({value.OsArchitecture}, process {value.ProcessArchitecture})",
            $".NET: {value.FrameworkVersion}",
            $"Client connected: {value.ClientConnected}; health path={value.HealthPath}",
            $"Quality: {value.QualityPreset}; requested encoder={value.EncoderPreference}; software fallback={value.SoftwareFallbackEnabled}"
        };
        if (value.Display is { } display)
            lines.Add($"Display: {display.Width}x{display.Height}; native={display.NativeWidth}x{display.NativeHeight}; rotation={display.Rotation}; requested={display.RequestedRefreshRate} Hz");
        if (value.Video is { } video)
        {
            lines.Add($"Video: {video.Encoder}; hardware={video.HardwareEncoder}; {video.Width}x{video.Height}; requested/effective={video.RequestedFps}/{video.EffectiveFps} fps; capturePaused={video.CapturePaused}");
            lines.Add($"Progress: sent={video.FramesSent}; submitted={video.DecoderSubmittedFrames} ({FormatRate(video.DecoderSubmittedFps)} fps); presentationCallbacks={video.PresentationCallbacks} ({FormatRate(video.PresentationCallbackFps)} fps)");
        }
        lines.Add("Health stages:");
        foreach (var step in value.Health)
            lines.Add($"- {step.Stage}: {step.State}; reason={step.Reason}; recovery={step.Recovery}");
        lines.Add(string.Empty);
        lines.Add("This summary contains no raw log text or stable device/network identity. Review the ZIP preview before attaching it to a public issue.");
        return string.Join("\r\n", lines) + "\r\n";
    }

    static string BuildPrivacyNotice() =>
        "TabLink 脱敏支持包 / Redacted support bundle\r\n\r\n" +
        "本 ZIP 只包含导出前预览过的结构化版本、显示参数、数值性能指标和枚举状态。\r\n" +
        "它不会读取或复制原始日志、原始诊断、设置文件、信任库、USB 清理记录、显示租约、截图或桌面画面。\r\n" +
        "它不会包含用户名、绝对路径、IP/MAC、网卡身份、USB/ADB/PnP 序列、host/device ID、配对链接、令牌、证书、公钥或私钥。\r\n" +
        "GPU/设备型号等自由文本也未自动收集；如 Issue 需要，请在理解公开范围后自行填写非唯一型号。\r\n\r\n" +
        "The ZIP contains only the structured fields shown in the in-app preview. Raw logs, free-form errors and stable identities are deliberately excluded.\r\n";

    static void Validate(SupportBundleSnapshot value)
    {
        ValidateVersion(value.ApplicationVersion, nameof(value.ApplicationVersion));
        ValidateVersion(value.WindowsVersion, nameof(value.WindowsVersion));
        ValidateVersion(value.FrameworkVersion, nameof(value.FrameworkVersion));
        ValidateEnum(value.OsArchitecture, nameof(value.OsArchitecture));
        ValidateEnum(value.ProcessArchitecture, nameof(value.ProcessArchitecture));
        ValidateEnum(value.QualityPreset, nameof(value.QualityPreset));
        ValidateEnum(value.EncoderPreference, nameof(value.EncoderPreference));
        ValidateEnum(value.HealthPath, nameof(value.HealthPath));
        if (value.CreatedUtc == default) throw new ArgumentException("支持包生成时间无效。", nameof(value));
        if (value.Display is { } display)
        {
            ValidateSize(display.Width, nameof(display.Width));
            ValidateSize(display.Height, nameof(display.Height));
            ValidateSize(display.NativeWidth, nameof(display.NativeWidth));
            ValidateSize(display.NativeHeight, nameof(display.NativeHeight));
            if (display.Rotation is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(display.Rotation));
            if (display.RequestedRefreshRate is < 24 or > 240) throw new ArgumentOutOfRangeException(nameof(display.RequestedRefreshRate));
        }
        if (value.Video is { } video)
        {
            ValidateEnum(video.Encoder, nameof(video.Encoder));
            ValidateSize(video.Width, nameof(video.Width));
            ValidateSize(video.Height, nameof(video.Height));
            if (video.RequestedFps is < 1 or > 240 || video.EffectiveFps is < 1 or > 240)
                throw new ArgumentOutOfRangeException(nameof(value.Video));
            if (video.FramesSent < 0 || video.DecoderSubmittedFrames < 0 || video.PresentationCallbacks < 0)
                throw new ArgumentOutOfRangeException(nameof(value.Video));
            if (video.DecoderSubmittedFps is { } submitted) ValidateRate(submitted, nameof(video.DecoderSubmittedFps));
            if (video.PresentationCallbackFps is { } presented) ValidateRate(presented, nameof(video.PresentationCallbackFps));
        }
        if (value.Health is null || value.Health.Count != Enum.GetValues<SupportHealthStage>().Length)
            throw new ArgumentException("支持包必须包含每个连接阶段且只能包含一次。", nameof(value.Health));
        var stages = new HashSet<SupportHealthStage>();
        foreach (var step in value.Health)
        {
            ArgumentNullException.ThrowIfNull(step);
            ValidateEnum(step.Stage, nameof(step.Stage));
            ValidateEnum(step.State, nameof(step.State));
            ValidateEnum(step.Reason, nameof(step.Reason));
            ValidateEnum(step.Recovery, nameof(step.Recovery));
            if (!stages.Add(step.Stage)) throw new ArgumentException("支持包连接阶段重复。", nameof(value.Health));
        }
        if (!value.Health.Select(step => step.Stage).SequenceEqual(Enum.GetValues<SupportHealthStage>()))
            throw new ArgumentException("支持包连接阶段顺序无效。", nameof(value.Health));
    }

    static void ValidatePrepared(PreparedSupportBundle prepared)
    {
        if (!prepared.Entries.Keys.SequenceEqual(FixedEntryOrder, StringComparer.Ordinal))
            throw new InvalidDataException("支持包条目不符合固定清单。");
        if (prepared.Entries.Any(item => item.Value is null) || prepared.Entries.Sum(item => item.Value.Length) > MaximumBundleBytes)
            throw new InvalidDataException("支持包内容无效或超过安全大小限制。");
        var expectedPreview = string.Join("\r\n\r\n", FixedEntryOrder.Select(name =>
            $"===== {name} =====\r\n{Utf8.GetString(prepared.Entries[name])}"));
        if (!string.Equals(prepared.PreviewText, expectedPreview, StringComparison.Ordinal))
            throw new InvalidDataException("支持包预览与待保存内容不一致。");
        using var manifest = JsonDocument.Parse(prepared.Entries["manifest.json"]);
        var declared = manifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        if (declared.Length != FixedEntryOrder.Length - 1)
            throw new InvalidDataException("支持包清单条目数量无效。");
        for (var index = 1; index < FixedEntryOrder.Length; index++)
        {
            var name = FixedEntryOrder[index];
            var item = declared[index - 1];
            if (item.GetProperty("name").GetString() != name ||
                item.GetProperty("size").GetInt32() != prepared.Entries[name].Length ||
                item.GetProperty("sha256").GetString() != Convert.ToHexString(SHA256.HashData(prepared.Entries[name])))
                throw new InvalidDataException("支持包清单与待保存内容不一致。");
        }
    }

    static void ValidateDestinationLeaf(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf) || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            leaf.Contains(':') || leaf.EndsWith(' ') || leaf.EndsWith('.') ||
            string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(leaf)))
            throw new ArgumentException("支持包文件名无效。", nameof(leaf));
        var deviceStem=leaf.Split('.')[0].TrimEnd(' ','.').ToUpperInvariant();
        if (deviceStem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            deviceStem.Length==4&&(deviceStem.StartsWith("COM",StringComparison.Ordinal)||deviceStem.StartsWith("LPT",StringComparison.Ordinal))&&
            deviceStem[3] is >= '1' and <= '9')
            throw new ArgumentException("支持包文件名不能使用 Windows 保留设备名。",nameof(leaf));
    }

    static void ValidateVersion(Version? value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        foreach (var component in new[] { value.Major, value.Minor, value.Build, value.Revision })
            if (component > 999_999) throw new ArgumentOutOfRangeException(name, "支持包版本组件超过允许范围。");
    }

    static void ValidateSize(int value, string name)
    {
        if (value is < 1 or > 8192) throw new ArgumentOutOfRangeException(name);
    }

    static void ValidateRate(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1000) throw new ArgumentOutOfRangeException(name);
    }

    static void ValidateEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name);
    }

    static string FormatRate(double? value) => value is { } known ? known.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "unknown";

    internal static double? NormalizeRate(double value) => double.IsFinite(value) && value is >= 0 and <= 1000 ? value : null;

    internal static SupportNativeCandidate CreateNativeCandidate(bool clientConnected, SupportDisplayProfile? clientReported,
        SupportDisplayProfile? preparedDisplay) => new(clientConnected,clientConnected?clientReported??preparedDisplay:null);

    internal static SupportEncoderBackend MapEncoderBackend(string? value)=>value switch
    {
        null or ""=>SupportEncoderBackend.None,
        "Nvenc"=>SupportEncoderBackend.Nvenc,
        "Qsv"=>SupportEncoderBackend.Qsv,
        "Amf"=>SupportEncoderBackend.Amf,
        "LibX264"=>SupportEncoderBackend.LibX264,
        _=>throw new ArgumentOutOfRangeException(nameof(value),"出现不支持的编码器状态。")
    };

    internal static string FailureSummary(Exception error)=>error switch
    {
        UnauthorizedAccessException=>"所选位置不允许写入，请改选有写入权限的文件夹。",
        DirectoryNotFoundException=>"所选文件夹已不存在，请重新选择保存位置。",
        InvalidDataException or ArgumentException=>"待保存内容未通过脱敏支持包的安全校验。",
        IOException=>"无法完成文件写入；原有目标文件已保留，请改名或改选位置后重试。",
        _=>"导出没有完成；没有上传任何内容。"
    };

    internal static SupportCurrentConnection SelectCurrentConnection(IReadOnlyList<SupportNativeCandidate> nativeCandidates,
        bool browserStreaming, SupportDisplayProfile? browserDisplay)
    {
        ArgumentNullException.ThrowIfNull(nativeCandidates);
        for(var index=0;index<nativeCandidates.Count;index++)
            if(nativeCandidates[index].ClientConnected)
                return new(true,nativeCandidates[index].Display,index);
        return browserStreaming?new(true,browserDisplay,-1):new(false,null,-1);
    }
}
