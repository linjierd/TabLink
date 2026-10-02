using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    // Developer-only rendering of this app's own controls, without showing the
    // form or invoking connection, display, driver or certificate operations.
    internal static void RenderUi(string directory)
    {
        Directory.CreateDirectory(directory);
        var views=new (string Name,int Tab,int Mode)[]
        {
            ("connection-client",0,0),("connection-browser",0,1),("connection-usb-debug",0,2),
            ("device-settings",1,-1),("diagnostics-log",2,-1)
        };
        VerifyWindowSizingPolicy();
        foreach(var view in views)RenderView(directory,view.Name,view.Tab,view.Mode,PreferredExpandedWindowSize,
            requireNoInternalScroll:view.Tab==0&&view.Mode==0);
        foreach(var view in views)RenderView(directory,"compact-"+view.Name,view.Tab,view.Mode,PreferredCompactWindowSize);
        RenderView(directory,"compact-settings-author-custom",1,-1,new Size(760,640),form=>
        {
            var custom=new AuthorFooterPreferences
            {
                Enabled=true,
                AuthorText="作者：这是一段用于验证窄窗口与高 DPI 自动换行的较长自定义显示名称（团队 / 社区维护者）",
                GitHubLabel="项目主页、源代码和问题反馈",
                GitHubUrl="https://github.com/example/example-project",
                BlogLabel="博客、教程与完整使用说明",
                BlogUrl="https://example.com/tablink/guide"
            };
            form.PopulateAuthorFooterEditors(custom);form.ApplyAuthorFooterPreferences(custom);
        });
        RenderView(directory,"compact-footer-hidden",0,0,new Size(760,640),form=>
        {
            var hidden=AuthorFooterPreferences.CreateDefault();hidden.Enabled=false;
            form.PopulateAuthorFooterEditors(hidden);form.ApplyAuthorFooterPreferences(hidden);
        });
        RenderView(directory,"compact-footer-maximum",0,0,new Size(760,640),form=>
        {
            var maximum=new AuthorFooterPreferences
            {
                Enabled=true,
                AuthorText=new string('作',AuthorFooterPreferences.MaximumAuthorTextLength),
                GitHubLabel=new string('G',AuthorFooterPreferences.MaximumLinkLabelLength),
                GitHubUrl="https://github.com/example/example-project",
                BlogLabel=new string('博',AuthorFooterPreferences.MaximumLinkLabelLength),
                BlogUrl="https://example.com/tablink/guide"
            };
            form.PopulateAuthorFooterEditors(maximum);form.ApplyAuthorFooterPreferences(maximum);
        });
    }
    static void RenderView(string directory,string name,int tab,int mode,Size size,Action<MainForm>? configure=null,
        bool requireNoInternalScroll=false)
    {
        using var form=new MainForm(verification:true);
        try
        {
            form.activationTimer.Stop();form.monitor.Stop();form.tray.Visible=false;
            form.Size=size;form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
            form.mainTabs.SelectedIndex=tab;
            if(mode>=0)form.connectionMode.SelectedIndex=mode;
            configure?.Invoke(form);
            PrepareView(form);
            if(requireNoInternalScroll&&form.clientConnectionPanel is ScrollableControl client&&
                (client.VerticalScroll.Visible||client.HorizontalScroll.Visible))
                throw new InvalidOperationException($"默认展开窗口仍需滚动才能查看连接副屏内容：client={client.ClientSize}，display={client.DisplayRectangle.Size}，vertical={client.VerticalScroll.Visible}，horizontal={client.HorizontalScroll.Visible}。");
            Save(form,directory,name);
        }
        finally{form.closing=true;form.Close();}
    }
    static void PrepareView(MainForm form)
    {
        form.mainTabs.SelectedTab!.CreateControl();form.ShowConnectionMode();
        var original=form.Size;
        form.Size=new Size(original.Width+1,original.Height+1);Application.DoEvents();
        form.Size=original;
        for(var pass=0;pass<3;pass++)
        {
            form.connectionHost.PerformLayout();form.mainTabs.PerformLayout();form.PerformLayout();
            form.Refresh();Application.DoEvents();
        }
        using var warmup=new Bitmap(form.Width,form.Height);
        form.DrawToBitmap(warmup,new Rectangle(Point.Empty,form.Size));
        form.Refresh();Application.DoEvents();
    }

    static void VerifyWindowSizingPolicy()
    {
        var large=CalculateInitialWindowBounds(new Rectangle(0,0,2048,1232),96);
        if(large!=new Rectangle(552,97,944,1038))
            throw new InvalidOperationException("大工作区默认窗口居中验证失败。");
        var standard=CalculateInitialWindowBounds(new Rectangle(0,0,1920,1040),96);
        if(standard!=new Rectangle(488,1,944,1038))
            throw new InvalidOperationException("1080p 工作区默认窗口验证失败。");
        var shortWork=CalculateInitialWindowBounds(new Rectangle(0,0,1366,728),96);
        if(shortWork!=new Rectangle(211,0,944,728))
            throw new InvalidOperationException("低高度工作区约束验证失败。");
        var smallWork=new Rectangle(-800,40,800,600);
        var small=CalculateInitialWindowBounds(smallWork,144);
        if(small!=smallWork)
            throw new InvalidOperationException("小屏工作区约束验证失败。");
        if(ScaleLogicalSize(PreferredExpandedWindowSize,144)!=new Size(1416,1557))
            throw new InvalidOperationException("逻辑窗口尺寸的 DPI 换算验证失败。");
    }
    static void Save(Form form,string directory,string name)
    {
        using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
        using(var graphics=Graphics.FromImage(bitmap))
        {
            var root=(TableLayoutPanel)form.Controls[0];
            var clientLeft=(form.Width-form.ClientSize.Width)/2;
            var clientTop=form.Height-form.ClientSize.Height-clientLeft;
            foreach(Control control in root.Controls)
            {
                if(!control.Visible||control.Width<=0||control.Height<=0)continue;
                using var layer=new Bitmap(control.Width,control.Height);
                control.DrawToBitmap(layer,new Rectangle(Point.Empty,control.Size));
                using var encoded=new MemoryStream();
                layer.Save(encoded,System.Drawing.Imaging.ImageFormat.Png);encoded.Position=0;
                using var rendered=new Bitmap(encoded);
                graphics.DrawImageUnscaled(rendered,clientLeft+root.Left+control.Left,clientTop+root.Top+control.Top);
            }
        }
        using var flattened=new Bitmap(bitmap.Width,bitmap.Height,System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(flattened))graphics.DrawImageUnscaled(bitmap,Point.Empty);
        flattened.Save(Path.Combine(directory,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
    }
}
