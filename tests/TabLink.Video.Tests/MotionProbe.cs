using System.Diagnostics;
using System.Runtime.InteropServices;
using TabLink.Windows;

internal sealed record MotionProbeReport(bool Success, string? Error, string DeviceName, Rectangle Bounds,
    int RequestedFps, int RequestedSeconds, long ScheduledFrame, int UniquePaints, double PaintedFps,
    double Seconds, double MedianPaintGapMs, double P90PaintGapMs, uint TimerBeginResult, uint TimerEndResult,
    object Barcode);

// Draws only its own short-lived diagnostic window. It never changes a display
// mode, captures the desktop, starts ADB, or controls another application's UI.
internal sealed class MotionProbe : Form
{
    const int RequestedFps = 90;
    readonly VirtualDisplayInfo display;
    readonly int seconds;
    readonly Stopwatch elapsed = new();
    readonly List<double> paintTimes = [];
    readonly Font title = new("Segoe UI", 27, FontStyle.Bold);
    readonly Font text = new("Microsoft YaHei UI", 16);
    readonly Font counter = new("Consolas", 30, FontStyle.Bold);
    readonly SolidBrush background = new(Color.FromArgb(12, 23, 40));
    readonly SolidBrush moving = new(Color.FromArgb(50, 215, 165));
    long scheduledFrame, paintedFrame = -1, lastIdentityCheck;
    bool activated, closing, timerRequested, resourcesDisposed;
    uint timerBegin, timerEnd;
    string? error;

    MotionProbe(VirtualDisplayInfo display, int seconds)
    {
        this.display = display; this.seconds = seconds;
        Text = "TabLink · 90 FPS motion test";
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = display.Bounds;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation => true;

    internal static Task<MotionProbeReport> RunAsync(string deviceName, int seconds)
    {
        seconds = Math.Clamp(seconds, 2, 30);
        var result = new TaskCompletionSource<MotionProbeReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MotionProbe? form = null;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var matches = VirtualDisplayManager.GetDisplays().Where(d => d.IsTabLinkCompatible && !d.IsPrimary &&
                    d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new IOException("Motion probe 只能显示在当前唯一匹配的活动 TabLink 虚拟副屏，未打开窗口。");
                form = new MotionProbe(matches[0], seconds);
                form.ValidateTarget(true);
                Application.Run(form);
                result.SetResult(form.MakeReport());
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { form?.Dispose(); }
        }) { IsBackground = false, Name = "TabLink motion probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            ValidateTarget(true);
            timerBegin = timeBeginPeriod(1);
            if (timerBegin != 0) throw new IOException("Motion probe 无法请求自身进程的 1ms 计时精度");
            timerRequested = true;
            elapsed.Start(); activated = true;
            Application.Idle += DrawWhileIdle;
        }
        catch (Exception ex) { error = ex.Message; Close(); }
    }

    void DrawWhileIdle(object? sender, EventArgs e)
    {
        try
        {
            while (!closing && !PeekMessage(out _, IntPtr.Zero, 0, 0, 0))
            {
                if (elapsed.Elapsed.TotalSeconds >= seconds) { Close(); return; }
                ValidateTarget();
                var target = (long)(elapsed.Elapsed.TotalSeconds * RequestedFps);
                if (target != paintedFrame)
                {
                    scheduledFrame = target;
                    Invalidate();
                    Update();
                }
                else Thread.Sleep(1);
            }
        }
        catch (Exception ex) { error = ex.Message; Close(); }
    }

    void ValidateTarget(bool full = false)
    {
        if (!VirtualDisplayManager.HasCurrentBounds(display.DeviceName, display.Bounds))
            throw new IOException("Motion probe 的虚拟屏已断开或边界改变，测试窗口已关闭。");
        var now = Environment.TickCount64;
        if (!full && now - lastIdentityCheck < 250) return;
        if (!VirtualDisplayManager.GetDisplays().Any(d => d.IsTabLinkCompatible && !d.IsPrimary &&
            d.DeviceName.Equals(display.DeviceName, StringComparison.OrdinalIgnoreCase) && d.Bounds == display.Bounds))
            throw new IOException("Motion probe 无法重新确认同一虚拟副屏，测试窗口已关闭。");
        lastIdentityCheck = now;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (closing) return;
        var g = e.Graphics;
        g.PageUnit = GraphicsUnit.Pixel;
        g.FillRectangle(background, ClientRectangle);
        g.DrawString("TabLink  ·  90 FPS motion probe", title, Brushes.White, 48, 48);
        g.DrawString($"独立虚拟副屏动态测试 · {seconds} 秒后自动结束", text, Brushes.LightSteelBlue, 48, 110);
        g.DrawString($"Frame {scheduledFrame:00000}", counter, Brushes.White, 48, 160);
        // A decoder can count genuinely different source pictures from these
        // pixels rather than mistaking duplicated video packets for new motion.
        for (var bit = 0; bit < 16; bit++)
            g.FillRectangle((scheduledFrame & (1L << bit)) != 0 ? Brushes.White : Brushes.Black,
                48 + bit * 28, 220, 20, 20);
        var travel = Math.Max(1, ClientSize.Width - 180);
        var phase = elapsed.Elapsed.TotalSeconds * 270 % travel;
        g.FillRectangle(moving, 48 + (float)phase, 320, 84, Math.Max(180, ClientSize.Height - 430));
        g.DrawString($"Target 90 Hz   Time {elapsed.Elapsed.TotalSeconds:F2}s", text, Brushes.LightSteelBlue, 48, 270);
        if (activated && paintedFrame != scheduledFrame)
        {
            paintedFrame = scheduledFrame;
            paintTimes.Add(elapsed.Elapsed.TotalMilliseconds);
        }
        base.OnPaint(e);
    }

    protected override void WndProc(ref Message message)
    {
        // If Windows tries to relocate this window after unplug/removal, hide it
        // before the proposed position can put the diagnostic on the main screen.
        if (activated && !closing && message.Msg == 0x46 && message.LParam != IntPtr.Zero)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
            var x = (position.Flags & 2) != 0 ? Left : position.X;
            var y = (position.Flags & 2) != 0 ? Top : position.Y;
            if (x != display.Bounds.X || y != display.Bounds.Y)
            {
                error = "Windows 尝试移动动态测试窗口，测试已终止以避开主屏。";
                closing = true;
                ShowWindow(Handle, 0);
                BeginInvoke(Close);
            }
        }
        base.WndProc(ref message);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing = true;
        elapsed.Stop();
        Application.Idle -= DrawWhileIdle;
        if (timerRequested) { timerEnd = timeEndPeriod(1); timerRequested = false; }
        base.OnFormClosed(e);
    }

    MotionProbeReport MakeReport()
    {
        var gaps = paintTimes.Zip(paintTimes.Skip(1), (a,b) => b-a).OrderBy(x => x).ToArray();
        var fps = paintTimes.Count > 1 ? (paintTimes.Count - 1) * 1000 / (paintTimes[^1] - paintTimes[0]) : 0;
        return new(error is null && paintTimes.Count > 0, error, display.DeviceName, display.Bounds,
            RequestedFps, seconds, scheduledFrame, paintTimes.Count, fps, elapsed.Elapsed.TotalSeconds,
            gaps.Length == 0 ? 0 : gaps[gaps.Length/2], gaps.Length == 0 ? 0 : gaps[(int)(gaps.Length*.9)],
            timerBegin, timerEnd, new { X=48,Y=220,CellSize=20,CellStep=28,Bits=16,LeastSignificantBitFirst=true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            Application.Idle -= DrawWhileIdle;
            if (timerRequested) { timerEnd=timeEndPeriod(1);timerRequested=false; }
            title.Dispose();text.Dispose();counter.Dispose();background.Dispose();moving.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeMessage
    { public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X,Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] struct WindowPosition
    { public IntPtr Hwnd, InsertAfter; public int X,Y,Width,Height; public uint Flags; }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
}
