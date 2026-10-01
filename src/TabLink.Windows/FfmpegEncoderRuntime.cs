using System.Diagnostics;
using System.Text;

namespace TabLink.Windows;

internal sealed record FfmpegEncoderPaths(string HardwarePath, string? SoftwarePath)
{
    internal string? PathFor(VideoEncoderBackend backend) => backend switch
    {
        VideoEncoderBackend.Nvenc or VideoEncoderBackend.Qsv or VideoEncoderBackend.Amf => HardwarePath,
        VideoEncoderBackend.LibX264 => SoftwarePath,
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "未知的视频编码后端。")
    };
}

internal readonly record struct FfmpegCatalogReadPlan(bool Hardware, bool Software);

internal static class FfmpegEncoderRuntime
{
    static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(2);

    internal static FfmpegEncoderPaths FindPaths()
    {
        var roots = new List<string> { AppContext.BaseDirectory };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            roots.Add(Path.Combine(directory.FullName, "third_party", "ffmpeg-tablink", "bin"));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hardware = Path.Combine(root, root.EndsWith(Path.Combine("ffmpeg-tablink", "bin"), StringComparison.OrdinalIgnoreCase)
                ? "ffmpeg.exe" : Path.Combine("tools", "ffmpeg", "ffmpeg.exe"));
            if (!File.Exists(hardware)) continue;
            var software = Path.Combine(Path.GetDirectoryName(hardware)!, "ffmpeg-x264.exe");
            return new(Path.GetFullPath(hardware), File.Exists(software) ? Path.GetFullPath(software) : null);
        }
        throw new FileNotFoundException("缺少已验证的 FFmpeg 硬件编码组件，请使用完整的 TabLink 交付目录。");
    }

    internal static async Task<FfmpegEncoderCatalog> ReadCatalogAsync(FfmpegEncoderPaths paths,
        VideoEncoderSelectionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        var plan = PlanCatalogRead(options);

        var hardwareTask = plan.Hardware
            ? TryReadEncodersAsync(paths.HardwarePath, cancellationToken)
            : Task.FromResult(CatalogFragment.NotRequested);
        var softwareTask = plan.Software
            ? TryReadEncodersAsync(paths.SoftwarePath, cancellationToken)
            : Task.FromResult(CatalogFragment.NotRequested);
        await Task.WhenAll(hardwareTask, softwareTask).ConfigureAwait(false);
        var hardware = await hardwareTask.ConfigureAwait(false);
        var software = await softwareTask.ConfigureAwait(false);
        var output = new StringBuilder();
        if (hardware.Output is { Length: > 0 }) output.AppendLine(hardware.Output);
        if (software.Output is { Length: > 0 }) output.AppendLine(software.Output);
        var unavailable = new Dictionary<VideoEncoderBackend, string>();
        if (hardware.Error is { } hardwareError)
            foreach (var backend in new[] { VideoEncoderBackend.Nvenc, VideoEncoderBackend.Qsv, VideoEncoderBackend.Amf })
                unavailable[backend] = hardwareError;
        if (software.Error is { } softwareError) unavailable[VideoEncoderBackend.LibX264] = softwareError;
        return FfmpegEncoderCatalog.Parse(output.ToString(), unavailable);
    }

    internal static FfmpegCatalogReadPlan PlanCatalogRead(VideoEncoderSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VideoEncoderPreferenceMetadata.TryGetForcedBackend(options.Preference, out var forcedBackend);
        var forced = options.Preference != VideoEncoderPreference.Automatic;
        var readHardware = !forced || forcedBackend != VideoEncoderBackend.LibX264;
        var readSoftware = options.AllowSoftwareFallback &&
            (!forced || forcedBackend == VideoEncoderBackend.LibX264);
        return new(readHardware, readSoftware);
    }

    static async Task<CatalogFragment> TryReadEncodersAsync(string? executable,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return new CatalogFragment(null, "编码器组件文件不存在。");
        try
        {
            return new CatalogFragment(await ReadEncodersAsync(executable, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException error) { return new CatalogFragment(null, error.Message); }
        catch (Exception error) when (error is IOException or InvalidDataException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new CatalogFragment(null, CompactCatalogError(error.Message));
        }
    }

    static string CompactCatalogError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "编码器组件能力检测失败。";
        var compact = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 400 ? compact : compact[..400] + "…";
    }

    static async Task<string> ReadEncodersAsync(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-encoders");
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CatalogTimeout);
        try
        {
            if (!process.Start()) throw new IOException("无法启动 FFmpeg 能力检测。");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var combined = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
            if (combined.Length > 1_000_000) throw new InvalidDataException("FFmpeg 能力列表超过安全上限。");
            if (process.ExitCode != 0) throw new IOException("FFmpeg 能力检测失败（退出码 " + process.ExitCode + "）。");
            return combined;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("FFmpeg 能力检测超过 2 秒。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    readonly record struct CatalogFragment(string? Output, string? Error)
    {
        internal static CatalogFragment NotRequested => new(null, null);
    }
}

internal sealed class FfmpegEncoderCapabilityProbe : IVideoEncoderCapabilityProbe
{
    readonly FfmpegEncoderPaths paths;
    readonly int width;
    readonly int height;
    readonly int requestedFps;
    readonly bool browserCompatible;

    internal FfmpegEncoderCapabilityProbe(FfmpegEncoderPaths paths, int width, int height, int requestedFps,
        bool browserCompatible)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (width is < 64 or > 8192 || height is < 64 or > 8192 || (width & 1) != 0 || (height & 1) != 0 ||
            (long)width * height > 16_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        if (requestedFps is < 1 or > 144) throw new ArgumentOutOfRangeException(nameof(requestedFps));
        this.paths = paths;
        this.width = width;
        this.height = height;
        this.requestedFps = requestedFps;
        this.browserCompatible = browserCompatible;
    }

    internal int EffectiveFps(VideoEncoderBackend backend) => backend == VideoEncoderBackend.LibX264
        ? Math.Min(requestedFps, 30)
        : requestedFps;

    public async Task<VideoEncoderProbeResult> ProbeAsync(VideoEncoderBackend backend,
        CancellationToken cancellationToken)
    {
        var executable = paths.PathFor(backend);
        if (executable is null || !File.Exists(executable))
            return VideoEncoderProbeResult.Failure(VideoEncoderBackendMetadata.DisplayName(backend) + " 组件未随程序提供。");
        var fps = EffectiveFps(backend);
        var frames = backend == VideoEncoderBackend.LibX264
            ? Math.Max(30, fps * 2)
            : Math.Max(30, Math.Min(fps, 90));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(backend == VideoEncoderBackend.LibX264
            ? TimeSpan.FromMilliseconds(3500)
            : TimeSpan.FromMilliseconds(2500));
        var timer = Stopwatch.StartNew();
        try
        {
            var count = 0;
            long priorPts = -1;
            await using var encoder = H264Encoder.CreateSynthetic(width, height, fps, executable, backend, frames,
                browserCompatible);
            encoder.Start();
            await foreach (var unit in encoder.ReadAccessUnitsAsync(timeout.Token).ConfigureAwait(false))
            {
                count++;
                if (count == 1 && (!unit.IsKeyFrame || !unit.ConfigurationChanged || unit.Sps.Length == 0 || unit.Pps.Length == 0))
                    return VideoEncoderProbeResult.Failure("首帧没有提供完整 SPS/PPS/IDR。");
                if (unit.PtsUs <= priorPts) return VideoEncoderProbeResult.Failure("探测帧时间戳没有严格递增。");
                priorPts = unit.PtsUs;
            }
            timer.Stop();
            if (count != frames) return VideoEncoderProbeResult.Failure($"只输出 {count}/{frames} 帧。");
            var throughput = frames / Math.Max(0.001, timer.Elapsed.TotalSeconds);
            if (backend == VideoEncoderBackend.LibX264 && throughput < fps * 1.10)
                return VideoEncoderProbeResult.Failure($"软件编码仅 {throughput:F1} fps，低于 {fps} fps 的实时余量要求。");
            if (backend != VideoEncoderBackend.LibX264 && throughput < fps * 0.90)
                return VideoEncoderProbeResult.Failure($"硬件编码仅 {throughput:F1} fps，无法稳定达到 {fps} fps 的目标。");
            return VideoEncoderProbeResult.Success($"{width} × {height} @ {fps} Hz 初始化成功；探测 {throughput:F1} fps。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return VideoEncoderProbeResult.Failure("真实初始化探测超时。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            return VideoEncoderProbeResult.Failure(Compact(error.Message));
        }
    }

    static string Compact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "编码器初始化失败。";
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 1200 ? compact : compact[..1200] + "…";
    }
}
