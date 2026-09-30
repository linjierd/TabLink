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
        foreach(var view in views)RenderView(directory,view.Name,view.Tab,view.Mode,new Size(1100,900));
        foreach(var view in views)RenderView(directory,"compact-"+view.Name,view.Tab,view.Mode,new Size(760,640));
    }
    static void RenderView(string directory,string name,int tab,int mode,Size size)
    {
        using var form=new MainForm(verification:true);
        try
        {
            form.activationTimer.Stop();form.monitor.Stop();form.tray.Visible=false;
            form.Size=size;form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
            form.mainTabs.SelectedIndex=tab;
            if(mode>=0)form.connectionMode.SelectedIndex=mode;
            PrepareView(form);Save(form,directory,name);
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
