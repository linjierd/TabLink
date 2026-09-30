using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TabLink.Windows;

internal sealed class DesktopCapture : IDisposable
{
    readonly string deviceName;
    readonly DisplayLease identity;
    Point inputLocation;
    readonly Bitmap source;
    readonly Bitmap scaled;
    readonly EncoderParameters quality;
    readonly ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
    readonly object inputLock = new();
    bool mouseDown;
    bool disposed;

    public DesktopCapture(Screen display, int maxWidth = 1600, int jpegQuality = 75)
        :this(new VirtualDisplayInfo(display.DeviceName,display.DeviceName,display.Primary,display.Bounds,!display.Primary),maxWidth,jpegQuality){}

    public DesktopCapture(VirtualDisplayInfo display, int maxWidth = 1600, int jpegQuality = 75,DisplayLease? identity=null)
    {
        if (display.IsPrimary||!display.IsTabLinkCompatible) throw new InvalidOperationException("请选择扩展副屏，不能将主屏当作扩展屏。");
        this.identity=identity??VirtualDisplayManager.CaptureLease(display);
        var bounds = VirtualDisplayManager.ResolveCurrent(this.identity).Bounds;
        deviceName = this.identity.DeviceName;inputLocation=bounds.Location;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("显示器尺寸无效");
        var scale = Math.Min(1, (double)maxWidth / Math.Max(bounds.Width, bounds.Height));
        source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        scaled = new Bitmap(Math.Max(1,(int)(bounds.Width * scale)), Math.Max(1,(int)(bounds.Height * scale)), PixelFormat.Format24bppRgb);
        quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 30, 95));
    }

    Rectangle ValidateDisplay()=>VirtualDisplayManager.ResolveCurrent(identity).Bounds;

    public byte[] Capture()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if(!InputDesktopAvailability.Query().IsAvailable)throw new IOException("普通桌面暂不可采集。");
        var bounds=ValidateDisplay();
        using (var g = Graphics.FromImage(source))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref cursor) && cursor.flags == 1 && bounds.Contains(cursor.ptScreenPos.X, cursor.ptScreenPos.Y))
            {
                var hdc = g.GetHdc();
                try { DrawIconEx(hdc, cursor.ptScreenPos.X-bounds.X, cursor.ptScreenPos.Y-bounds.Y, cursor.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
                finally { g.ReleaseHdc(hdc); }
            }
        }
        using (var g = Graphics.FromImage(scaled))
        { g.InterpolationMode = InterpolationMode.Bilinear; g.DrawImage(source, new Rectangle(Point.Empty,scaled.Size)); }
        using var output = new MemoryStream(); scaled.Save(output, codec, quality); return output.ToArray();
    }

    public void Input(InputMessage message)
    {
        lock (inputLock)
        {
            if (disposed) return;
            if(!InputDesktopAvailability.Query().IsAvailable){ReleaseInput();return;}
            Rectangle bounds;
            try{bounds=ValidateDisplay();}
            catch(Exception ex) when(ex is IOException or InvalidOperationException){ReleaseInput();return;}
            if(bounds.Location!=inputLocation){ReleaseInput();inputLocation=bounds.Location;}
            if(!VirtualDisplayManager.HasCurrentBounds(deviceName,bounds))return;
            var desktop = SystemInformation.VirtualScreen;
            var px = bounds.X + (int)Math.Round(message.X * (bounds.Width - 1));
            var py = bounds.Y + (int)Math.Round(message.Y * (bounds.Height - 1));
            var flags = 0x0001u | 0x8000u | 0x4000u; // absolute move on virtual desktop
            if (message.Kind == "down" && !mouseDown) { flags |= 0x0002; mouseDown = true; }
            if (message.Kind == "up" && mouseDown) { flags |= 0x0004; mouseDown = false; }
            uint data = 0;
            if (message.Kind == "scroll") { flags |= 0x0800; data = unchecked((uint)(int)Math.Clamp(message.Delta, -1200, 1200)); }
            Send(px,py,desktop,flags,data);
        }
    }

    static void Send(int x, int y, Rectangle desktop, uint flags, uint data)
    {
        var entry = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT {
            dx = (int)Math.Round((x-desktop.X)*65535.0 / Math.Max(1,desktop.Width-1)),
            dy = (int)Math.Round((y-desktop.Y)*65535.0 / Math.Max(1,desktop.Height-1)),
            dwFlags = flags, mouseData = data } } };
        _ = SendInput(1, [entry], Marshal.SizeOf<INPUT>());
    }
    public void ReleaseInput()
    {
        lock(inputLock) { if (!mouseDown) return; mouseDown=false;
            if(!InputDesktopAvailability.Query().IsAvailable)return;
            var entry = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } };
            _ = SendInput(1, [entry], Marshal.SizeOf<INPUT>());
        }
    }
    public void Dispose() { lock(inputLock) { ReleaseInput(); disposed=true; source.Dispose(); scaled.Dispose(); quality.Dispose(); } }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public uint flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public UIntPtr dwExtraInfo; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc,int x,int y,IntPtr icon,int width,int height,uint step,IntPtr brush,uint flags);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count,INPUT[] input,int size);
}
