namespace TabLink.Windows;

internal sealed partial class MainForm
{
    internal static readonly Size PreferredExpandedWindowSize=new(944,1038);
    internal static readonly Size PreferredCompactWindowSize=new(760,640);
    const int LogicalDpi=96;

    void ConfigureWindowSizing()
    {
        if(verificationMode)return;
        Load+=(_,_)=>ApplyInitialExpandedWindowBounds();
        DpiChanged+=(_,_)=>QueueWindowWorkingAreaClamp();
    }

    void ApplyInitialExpandedWindowBounds()
    {
        var work=Screen.FromHandle(Handle).WorkingArea;
        UpdateWindowMinimumSize(work);
        Bounds=CalculateInitialWindowBounds(work,DeviceDpi);
    }

    void QueueWindowWorkingAreaClamp()
    {
        if(IsDisposed||Disposing||!IsHandleCreated)return;
        try{BeginInvoke(ClampWindowToCurrentWorkingArea);}catch(InvalidOperationException)when(IsDisposed||Disposing){}
    }

    void ClampWindowToCurrentWorkingArea()
    {
        if(IsDisposed||Disposing)return;
        var intersects=Screen.AllScreens.Any(screen=>screen.WorkingArea.IntersectsWith(Bounds));
        var screen=intersects?Screen.FromRectangle(Bounds):Screen.FromPoint(Cursor.Position);
        var work=screen.WorkingArea;
        UpdateWindowMinimumSize(work);
        if(WindowState!=FormWindowState.Normal)return;
        var width=Math.Min(Width,work.Width);
        var height=Math.Min(Height,work.Height);
        var left=Math.Clamp(Left,work.Left,work.Right-width);
        var top=Math.Clamp(Top,work.Top,work.Bottom-height);
        Bounds=new Rectangle(left,top,width,height);
    }

    void UpdateWindowMinimumSize(Rectangle workingArea)
    {
        var preferred=ScaleLogicalSize(PreferredCompactWindowSize,DeviceDpi);
        var constrained=new Size(Math.Min(preferred.Width,workingArea.Width),Math.Min(preferred.Height,workingArea.Height));
        if(MinimumSize!=constrained)MinimumSize=constrained;
    }

    internal static Size ScaleLogicalSize(Size logical,int dpi)
    {
        var effectiveDpi=dpi>0?dpi:LogicalDpi;
        return new Size(
            Math.Max(1,(int)Math.Round(logical.Width*effectiveDpi/(double)LogicalDpi,MidpointRounding.AwayFromZero)),
            Math.Max(1,(int)Math.Round(logical.Height*effectiveDpi/(double)LogicalDpi,MidpointRounding.AwayFromZero)));
    }

    internal static Rectangle CalculateInitialWindowBounds(Rectangle workingArea,int dpi)
    {
        if(workingArea.Width<=0||workingArea.Height<=0)throw new ArgumentOutOfRangeException(nameof(workingArea));
        var preferred=ScaleLogicalSize(PreferredExpandedWindowSize,dpi);
        var size=new Size(Math.Min(preferred.Width,workingArea.Width),Math.Min(preferred.Height,workingArea.Height));
        return CentreInside(workingArea,size);
    }

    internal static Rectangle CentreInside(Rectangle workingArea,Size size)=>new(
        workingArea.Left+(workingArea.Width-size.Width)/2,
        workingArea.Top+(workingArea.Height-size.Height)/2,
        size.Width,size.Height);
}
