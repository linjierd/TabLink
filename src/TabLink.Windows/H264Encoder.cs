using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabLink.Windows;

internal enum H264CapturePreference { GdiGrab, PreferDesktopDuplication }

internal sealed record H264AccessUnit(byte[] Data, long Sequence, long PtsUs,
    byte[] Sps, byte[] Pps, bool IsKeyFrame, bool ConfigurationChanged);

// A DDA startup failure may change only the capture path. The already selected
// encoder backend is immutable, and the retry is legal only before any frame
// has crossed the process boundary.
internal sealed class H264CaptureStartupFallback(VideoEncoderBackend selectedBackend)
{
    internal VideoEncoderBackend SelectedBackend { get; } = selectedBackend;
    internal bool Attempted { get; private set; }
    internal bool FrameObserved { get; private set; }

    internal bool TryBegin(bool usingDesktopDuplication, long emittedFrames)
    {
        if (emittedFrames < 0) throw new ArgumentOutOfRangeException(nameof(emittedFrames));
        if (emittedFrames > 0) FrameObserved = true;
        if (Attempted || FrameObserved || !usingDesktopDuplication) return false;
        Attempted = true;
        return true;
    }
}

// Owns exactly one FFmpeg process. There is no encoder fallback: the selected
// codec either starts and delivers H.264 or reports its bounded stderr tail.
internal sealed class H264Encoder : IAsyncDisposable
{
    readonly VirtualDisplayInfo? display;
    readonly DisplayLease? displayIdentity;
    readonly string ffmpegPath;
    readonly VideoEncoderBackend backend;
    readonly H264CapturePreference capturePreference;
    readonly int syntheticFrames;
    readonly bool browserCompatible;
    readonly VideoEncodingPlan? encodingPlan;
    readonly object lifecycle = new();
    readonly object errorLock = new();
    readonly StringBuilder errorTail = new();
    readonly CancellationTokenSource lifetime = new();
    readonly H264CaptureStartupFallback captureStartupFallback;
    Process? process;
    SafeFileHandle? job;
    Task? errorPump, disposeTask;
    int readerStarted;
    long lastFullDisplayCheck;
    DxgiOutputIdentity? dxgiTarget;
    internal int Width { get; }
    internal int Height { get; }
    internal int Fps { get; }
    internal VideoEncoderBackend Backend => backend;
    internal string CodecName => VideoEncoderBackendMetadata.CodecName(backend);
    internal string CaptureBackend => display is null ? "synthetic-lavfi" : dxgiTarget is null ? "gdigrab-selected-display" : "ddagrab-selected-display";
    internal string? CaptureFallbackReason { get; private set; }
    internal event Action<string>? Status;
    internal int? ProcessId => process?.Id;
    internal string LastError { get { lock (errorLock) return errorTail.ToString(); } }

    internal H264Encoder(VirtualDisplayInfo display, int width, int height, int fps, string ffmpegPath,
        VideoEncoderBackend backend = VideoEncoderBackend.Nvenc,
        H264CapturePreference capturePreference = H264CapturePreference.GdiGrab,DisplayLease? identity=null,
        bool browserCompatible=false,VideoEncodingPlan? encodingPlan=null)
        : this(width, height, fps, ffmpegPath, backend, 0,browserCompatible,encodingPlan)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!display.IsTabLinkCompatible || display.IsPrimary || display.Bounds.Width < 1 || display.Bounds.Height < 1)
            throw new ArgumentException("H.264 只能采集已确认的独立虚拟副屏", nameof(display));
        this.display = display;
        displayIdentity=identity??VirtualDisplayManager.CaptureLease(display);
        this.capturePreference = capturePreference;
    }

    H264Encoder(int width, int height, int fps, string ffmpegPath, VideoEncoderBackend backend, int syntheticFrames,
        bool browserCompatible=false,VideoEncodingPlan? encodingPlan=null)
    {
        if (width is < 64 or > 8192 || height is < 64 or > 8192 || (width & 1) != 0 || (height & 1) != 0 ||
            (long)width * height > 16_000_000)
            throw new ArgumentOutOfRangeException(nameof(width), "H.264 尺寸必须是 64–8192 范围的偶数，且总像素不超过 1600 万");
        if (fps is < 1 or > 144) throw new ArgumentOutOfRangeException(nameof(fps));
        if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("找不到 FFmpeg 编码程序", ffmpegPath);
        Width = width; Height = height; Fps = fps;
        this.ffmpegPath = Path.GetFullPath(ffmpegPath);
        _=VideoEncoderBackendMetadata.CodecName(backend);
        this.backend = backend;
        captureStartupFallback = new H264CaptureStartupFallback(backend);
        this.syntheticFrames = syntheticFrames;
        this.browserCompatible=browserCompatible;
        this.encodingPlan=encodingPlan;
        if(encodingPlan is not null)
        {
            if(encodingPlan.Width!=width||encodingPlan.Height!=height||encodingPlan.Fps!=fps||
                encodingPlan.BitrateKbps is <1000 or >100000||encodingPlan.BufferKbits is <128 or >16000||
                encodingPlan.GopFrames is <1 or >1000||encodingPlan.Generation<1)
                throw new ArgumentOutOfRangeException(nameof(encodingPlan),"编码计划参数无效。");
            if(browserCompatible)throw new ArgumentException("浏览器编码暂不接受原生自适应计划。",nameof(encodingPlan));
        }
        if(browserCompatible&&(Math.Max(width,height)>1920||Math.Min(width,height)>1080||fps>60))
            throw new ArgumentOutOfRangeException(nameof(width),"浏览器编码上限为 1080p / 60 fps。");
    }

    // This finite source benchmarks the complete process/parser path without
    // reading any desktop pixel or changing display settings.
    internal static H264Encoder CreateSynthetic(int width, int height, int fps, string ffmpegPath,
        VideoEncoderBackend backend = VideoEncoderBackend.Nvenc, int frameCount = 300,bool browserCompatible=false,
        VideoEncodingPlan? encodingPlan=null) =>
        new(width, height, fps, ffmpegPath, backend, Math.Clamp(frameCount, 1, 10000),browserCompatible,encodingPlan);

    internal void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
            if (process is not null) throw new InvalidOperationException("H.264 编码器已经启动");
            EnsureDesktopAvailable();
            ValidateDisplay(true);
            SelectCaptureBackend();
            EnsureDesktopAvailable();
            ValidateDisplay(true);
            StartProcessCore();
        }
    }

    // lifecycle must be held by the caller, including a pre-first-frame retry.
    void StartProcessCore()
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in BuildArguments()) start.ArgumentList.Add(argument);
        var child = new Process { StartInfo = start };
        try
        {
            if (!child.Start()) throw new IOException("无法启动 H.264 编码器");
            process = child;
            // Closing this host's handle also terminates its encoder after a
            // crash; no global FFmpeg lookup or process-name termination.
            job = CreateKillOnCloseJob(child);
            if (backend == VideoEncoderBackend.LibX264)
            {
                try { child.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                { ReportStatus("软件编码器优先级无法降低：" + error.Message); }
            }
            child.StandardInput.Close();
            errorPump = DrainErrorsAsync(child.StandardError);
            ReportStatus($"H.264 捕获后端：{CaptureBackend}；编码器：{CodecName}" +
                (CaptureFallbackReason is null ? "" : "；" + CaptureFallbackReason));
        }
        catch
        {
            try { TryKill(child); }
            finally
            {
                try { child.Dispose(); }
                finally
                {
                    if (ReferenceEquals(process, child)) process = null;
                    var failedJob = job;
                    job = null; errorPump = null;
                    failedJob?.Dispose();
                }
            }
            throw;
        }
    }

    void SelectCaptureBackend()
    {
        if (display is null || capturePreference == H264CapturePreference.GdiGrab) return;
        if (backend != VideoEncoderBackend.Nvenc)
        { CaptureFallbackReason = VideoEncoderBackendMetadata.DisplayName(backend) + " 当前使用指定副屏 GDI 捕获"; return; }
        if (Width != display.Bounds.Width || Height != display.Bounds.Height)
        { CaptureFallbackReason = "输出尺寸需要缩放，使用指定副屏 GDI 捕获"; return; }
        try
        {
            dxgiTarget = DxgiCaptureTarget.SelectUnique(display, DxgiCaptureTarget.ReadOutputs(), out var reason);
            if (dxgiTarget is null) CaptureFallbackReason = reason + "，使用指定副屏 GDI 捕获";
        }
        catch (Exception ex) when (ex is IOException or COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            EnsureDesktopAvailable();
            if(IsDesktopAccessFailure(ex))
                throw new CaptureUnavailableException("Desktop duplication 暂时不可用："+ex.Message);
            CaptureFallbackReason = "无法验证 GPU 捕获输出：" + ex.Message + "；使用指定副屏 GDI 捕获";
        }
    }

    internal async IAsyncEnumerable<H264AccessUnit> ReadAccessUnitsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref readerStarted, 1) != 0)
            throw new InvalidOperationException("H.264 编码流只能读取一次");
        if (process is null) throw new InvalidOperationException("请先启动 H.264 编码器");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var input = new byte[65536];
        long sequence = 0;
        try
        {
            while (true)
            {
                var child = process ?? throw new InvalidOperationException("H.264 编码进程已结束");
                var attemptErrorPump = errorPump;
                var parser = new AnnexBParser();
                while (true)
                {
                var count = await child.StandardOutput.BaseStream.ReadAsync(input, linked.Token).ConfigureAwait(false);
                if (count == 0) break;
                foreach (var unit in parser.Append(input.AsSpan(0, count)))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    ValidateDisplay(sequence == 0);
                    ValidateAccessUnit(unit, sequence);
                    yield return Wrap(unit, ++sequence);
                }
                }
                await child.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await WaitForErrorPumpAsync(attemptErrorPump, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var exitCode = child.ExitCode;
                var attemptError = LastError;
                if (exitCode != 0)
                {
                    var failure = new IOException($"{CodecName} 编码失败（退出码 {exitCode}）：{attemptError}");
                    if (await TryRestartWithGdiBeforeFirstFrameAsync(child, attemptErrorPump, sequence, linked.Token)
                        .ConfigureAwait(false))
                        continue;
                    // Access loss after a delivered frame is a desktop-lifecycle
                    // event. It must rebuild the stream, never switch capture.
                    if(dxgiTarget is not null&&IsTransientDesktopFailure(attemptError))
                        throw new CaptureUnavailableException("Desktop duplication 需要重建："+attemptError);
                    throw failure;
                }
            if (parser.Complete() is { } final)
            {
                ValidateDisplay();
                ValidateAccessUnit(final, sequence);
                yield return Wrap(final, ++sequence);
            }
                if (sequence == 0)
                {
                    var failure = new IOException($"{CodecName} 没有输出画面：{attemptError}");
                    if (await TryRestartWithGdiBeforeFirstFrameAsync(child, attemptErrorPump, sequence, linked.Token)
                        .ConfigureAwait(false))
                        continue;
                    throw failure;
                }
            if (display is not null && !linked.IsCancellationRequested)
                throw new IOException($"{CodecName} 意外停止输出：{LastError}");
                break;
            }
        }
        finally { KillOwnedProcess(); }
    }

    async Task<bool> TryRestartWithGdiBeforeFirstFrameAsync(Process failedProcess, Task? failedErrorPump,
        long emittedFrames, CancellationToken cancellationToken)
    {
        var wasUsingDesktopDuplication = dxgiTarget is not null;
        if (!captureStartupFallback.TryBegin(wasUsingDesktopDuplication, emittedFrames)) return false;

        // A secure/unavailable desktop or changed display identity belongs to
        // CaptureRecovery. It must not be bypassed by opening GDI.
        EnsureDesktopAvailable();
        dxgiTarget = null;
        ValidateDisplay(true);
        cancellationToken.ThrowIfCancellationRequested();
        await ReleaseAttemptAsync(failedProcess, failedErrorPump).ConfigureAwait(false);

        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            CaptureFallbackReason = "Desktop duplication 首帧前初始化失败；已保持 " +
                VideoEncoderBackendMetadata.DisplayName(captureStartupFallback.SelectedBackend) +
                " 编码器，仅改用指定副屏 GDI 捕获重试一次";
            lock (errorLock) errorTail.Clear();
            EnsureDesktopAvailable();
            ValidateDisplay(true);
            StartProcessCore();
        }
        return true;
    }

    async Task ReleaseAttemptAsync(Process ownedProcess, Task? ownedErrorPump)
    {
        var ownedJob = job;
        job = null;
        try
        {
            TryKill(ownedProcess);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ownedProcess.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception) { }
            await WaitForErrorPumpAsync(ownedErrorPump, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(process, ownedProcess)) process = null;
            if (ReferenceEquals(errorPump, ownedErrorPump)) errorPump = null;
            try { ownedJob?.Dispose(); }
            finally
            {
                ownedProcess.Dispose();
            }
        }
    }

    H264AccessUnit Wrap(AnnexBAccessUnit unit, long sequence) =>
        new(unit.Data, sequence, checked((sequence - 1) * 1_000_000L / Fps),
            unit.Sps, unit.Pps, unit.IsKeyFrame, unit.ConfigurationChanged);

    static void ValidateAccessUnit(AnnexBAccessUnit unit, long emittedCount)
    {
        if (emittedCount == 0 && !unit.IsKeyFrame)
            throw new InvalidDataException("编码器第一帧不是 IDR，已停止不完整的视频链路。");
        if (unit.ConfigurationChanged && !unit.IsKeyFrame)
            throw new InvalidDataException("编码参数变化未与 IDR 同帧输出，已停止不安全的解码重建。");
    }

    IReadOnlyList<string> BuildArguments()
    {
        static string N(int number) => number.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        if (display is null)
            arguments.AddRange(["-f", "lavfi", "-i", $"testsrc2=size={N(Width)}x{N(Height)}:rate={N(Fps)}", "-frames:v", N(syntheticFrames)]);
        else if (dxgiTarget is not null)
        {
            arguments.AddRange(["-init_hw_device", $"d3d11va=tablink:{N(dxgiTarget.AdapterIndex)}",
                "-filter_hw_device", "tablink", "-filter_complex",
                $"ddagrab=output_idx={N(dxgiTarget.OutputIndex)}:framerate={N(Fps)}:video_size={N(Width)}x{N(Height)}:draw_mouse=1:output_fmt=bgra:dup_frames=1[capture]",
                "-map", "[capture]"]);
        }
        else
        {
            var bounds = display.Bounds;
            arguments.AddRange(["-thread_queue_size", "2", "-f", "gdigrab", "-framerate", N(Fps),
                "-draw_mouse", "1", "-offset_x", N(bounds.X), "-offset_y", N(bounds.Y),
                "-video_size", $"{N(bounds.Width)}x{N(bounds.Height)}", "-i", "desktop"]);
            if (Width != bounds.Width || Height != bounds.Height)
                arguments.AddRange(["-vf", $"scale={N(Width)}:{N(Height)}:flags=fast_bilinear"]);
        }
        arguments.AddRange(["-an", "-sn", "-dn", "-c:v", CodecName]);
        var bitrate=encodingPlan is null
            ?browserCompatible?(Width*Height>1280*720?"8M":"4M"):"30M"
            :N(encodingPlan.BitrateKbps)+"k";
        var buffer=encodingPlan is null?"1M":N(encodingPlan.BufferKbits)+"k";
        switch (backend)
        {
            case VideoEncoderBackend.Nvenc:
                arguments.AddRange(["-preset", "p1", "-tune", "ull", "-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate,
                    "-bufsize", buffer, "-rc-lookahead", "0", "-zerolatency", "1", "-delay", "0",
                    "-forced-idr", "1"]);
                break;
            case VideoEncoderBackend.Qsv:
                arguments.AddRange(["-preset", "veryfast", "-async_depth", "1", "-look_ahead", "0",
                    "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer,
                    "-repeat_pps", "1", "-forced_idr", "1"]);
                break;
            case VideoEncoderBackend.Amf:
                arguments.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr",
                    "-preencode", "0", "-preanalysis", "0", "-frame_skipping", "0",
                    "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer]);
                break;
            case VideoEncoderBackend.LibX264:
                arguments.AddRange(["-preset", "ultrafast", "-tune", "zerolatency"]);
                if (encodingPlan is null)
                    arguments.AddRange(["-crf", "20", "-maxrate", browserCompatible ? "8M" : "30M", "-bufsize", "2M"]);
                else
                    arguments.AddRange(["-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer]);
                arguments.AddRange(["-threads", Math.Max(1, Environment.ProcessorCount / 2).ToString(CultureInfo.InvariantCulture),
                    "-x264-params", "repeat-headers=1:scenecut=0:rc-lookahead=0:sync-lookahead=0"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "未知的视频编码后端。");
        }
        arguments.AddRange(["-profile:v", browserCompatible ? "baseline" : "high"]);
        if(browserCompatible)arguments.AddRange(["-level:v","4.2"]);
        if (dxgiTarget is null)
            arguments.AddRange(["-pix_fmt", backend is VideoEncoderBackend.Qsv or VideoEncoderBackend.Amf ? "nv12" : "yuv420p"]);
        arguments.AddRange(["-bf", "0", "-g", N(encodingPlan?.GopFrames??Fps), "-fps_mode", "passthrough",
            "-bsf:v", "h264_metadata=aud=insert", "-flush_packets", "1", "-f", "h264", "pipe:1"]);
        return arguments;
    }

    internal IReadOnlyList<string> BuildArgumentsForTesting()=>BuildArguments();
    internal static void ValidateAccessUnitForTesting(AnnexBAccessUnit unit,long emittedCount)=>
        ValidateAccessUnit(unit,emittedCount);

    void ValidateDisplay(bool force = false)
    {
        if (display is null) return;
        EnsureDesktopAvailable();
        if (!VirtualDisplayManager.HasCurrentBounds(display.DeviceName, display.Bounds))
        {
            var moved=VirtualDisplayManager.ResolveCurrent(displayIdentity!);
            if(moved.Bounds.Size==display.Bounds.Size)
                throw new CaptureUnavailableException("原虚拟副屏位置已移动，需要重建捕获。");
            throw new IOException("副屏尺寸发生变化，请重新连接。");
        }
        var now = Environment.TickCount64;
        if (!force && now - lastFullDisplayCheck < 250) return;
        var verified=VirtualDisplayManager.ResolveCurrent(displayIdentity!);
        if(verified.Bounds!=display.Bounds)
            throw new CaptureUnavailableException("原虚拟副屏位置已移动，需要重建捕获。");
        if (dxgiTarget is not null && !DxgiCaptureTarget.IsCurrent(display, dxgiTarget))
        {
            // Revalidate the original lease before discarding an obsolete DXGI mapping.
            // A fresh encoder must select its output again even when only the index changed.
            _=VirtualDisplayManager.ResolveCurrent(displayIdentity!);
            throw new CaptureUnavailableException("原副屏的 GPU 映射发生变化，需要重建捕获。");
        }
        lastFullDisplayCheck = now;
    }

    void EnsureDesktopAvailable()
    {
        if(display is not null&&!InputDesktopAvailability.Query().IsAvailable)
            throw new CaptureUnavailableException("普通 Windows 桌面暂不可采集。");
    }

    internal static bool IsDesktopAccessFailure(Exception error)
    {
        for(Exception? current=error;current is not null;current=current.InnerException)
            if(current.HResult==unchecked((int)0x887A0026)||current.HResult==unchecked((int)0x80070005))return true;
        return false;
    }

    internal static bool IsTransientDesktopFailure(string error)
    {
        // Match capture-specific diagnostics, not generic NVENC/configuration
        // failures such as unsupported parameters or an incompatible driver.
        return error.Contains("DXGI_ERROR_ACCESS_LOST",StringComparison.OrdinalIgnoreCase)||
            error.Contains("0x887a0026",StringComparison.OrdinalIgnoreCase)||
            error.Contains("Desktop duplication access denied",StringComparison.OrdinalIgnoreCase)||
            error.Contains("Desktop duplication lost",StringComparison.OrdinalIgnoreCase)||
            (error.Contains("ddagrab",StringComparison.OrdinalIgnoreCase)&&
             (error.Contains("access lost",StringComparison.OrdinalIgnoreCase)||
              error.Contains("access denied",StringComparison.OrdinalIgnoreCase)));
    }

    void ReportStatus(string message)
    {
        lock (errorLock)
        {
            errorTail.AppendLine("[TabLink] " + message);
            if (errorTail.Length > 16384) errorTail.Remove(0, errorTail.Length - 16384);
        }
        Trace.WriteLine(message);
        Status?.Invoke(message);
    }

    async Task DrainErrorsAsync(StreamReader stderr)
    {
        var chars = new char[2048];
        try
        {
            while (true)
            {
                var count = await stderr.ReadAsync(chars).ConfigureAwait(false);
                if (count == 0) return;
                lock (errorLock)
                {
                    errorTail.Append(chars, 0, count);
                    if (errorTail.Length > 16384) errorTail.Remove(0, errorTail.Length - 16384);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    static bool TryKill(Process ownedProcess) => TryExpectedKill(() =>
    {
        if (!ownedProcess.HasExited) ownedProcess.Kill(entireProcessTree: true);
    });

    static bool TryExpectedKill(Action kill)
    {
        try { kill(); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
    }

    void KillOwnedProcess()
    {
        if (process is { } ownedProcess) TryKill(ownedProcess);
    }

    static async Task<bool> WaitForErrorPumpAsync(Task? pump, TimeSpan timeout)
    {
        if (pump is null) return true;
        try
        {
            await pump.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            // stderr is diagnostic cleanup. A timeout or a fault here must not
            // replace the encoder/capture failure that caused process exit.
            Trace.WriteLine("H.264 stderr cleanup did not complete: " + error.Message);
            return false;
        }
    }

    internal static bool TryExpectedKillForTesting(Action kill) => TryExpectedKill(kill);
    internal static Task<bool> WaitForErrorPumpForTestingAsync(Task? pump, TimeSpan timeout) =>
        WaitForErrorPumpAsync(pump, timeout);

    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new ValueTask(disposeTask ??= DisposeCoreAsync());
    }

    async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        var ownedProcess = process;
        var ownedErrorPump = errorPump;
        var ownedJob = job;
        job = null;
        try
        {
            KillOwnedProcess();
            ownedJob?.Dispose();
            if (ownedProcess is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ownedProcess.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception) { }
            }
            await WaitForErrorPumpAsync(ownedErrorPump, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            try { ownedJob?.Dispose(); }
            finally
            {
                try { ownedProcess?.Dispose(); }
                finally
                {
                    process = null; errorPump = null;
                    lifetime.Dispose();
                }
            }
        }
    }

    static SafeFileHandle CreateKillOnCloseJob(Process child)
    {
        var handle = new SafeFileHandle(CreateJobObject(IntPtr.Zero, null), ownsHandle: true);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建编码器进程保护");
        try
        {
            var info = new JobExtendedLimit { BasicLimit = new JobBasicLimit { Flags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<JobExtendedLimit>()) ||
                !AssignProcessToJobObject(handle, child.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保护编码器进程，已取消启动");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)] struct JobBasicLimit
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct JobIoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimit;
        public JobIoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
