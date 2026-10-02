using System.Diagnostics;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    void OpenSettingsPage()
    {
        mainTabs.SelectedIndex=1;
        if(mainTabs.SelectedTab is { } settingsPage)
        {
            settingsPage.AutoScrollPosition=Point.Empty;
            settingsPage.Select();
        }
    }

    void ShowHelpWindow()
    {
        using var dialog=CreateHelpWindow();
        dialog.ShowDialog(this);
    }

    Form CreateHelpWindow()
    {
        var dialog=new Form
        {
            Text=Ui("TabLink 使用帮助","TabLink Help"),StartPosition=FormStartPosition.CenterParent,
            Size=new Size(760,620),MinimumSize=new Size(620,480),ShowInTaskbar=false,
            AutoScaleMode=AutoScaleMode.Dpi,Font=Font,BackColor=Color.FromArgb(244,247,251),ForeColor=ink
        };
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=4};
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text=Ui("TabLink 使用帮助","TabLink Help"),AutoSize=true,
            Font=new Font("Microsoft YaHei UI",16,FontStyle.Bold),ForeColor=accent,Margin=new Padding(0,0,0,12)
        },0,0);
        var instructions=new Label
        {
            Dock=DockStyle.Top,AutoSize=true,ForeColor=ink,Margin=Padding.Empty,
            Text=Ui(
                "连接副屏\n选择 TabLink 客户端、浏览器接入或 USB 调试兼容模式。浏览器接入需要首次信任本机证书；进入 USB 调试模式会自动刷新设备，只有一台可连接设备时会自动选中。\n\n后台运行\n点击窗口右上角 × 只会隐藏到托盘，副屏继续运行。请从托盘菜单停止连接或退出。\n\n设置\n点击主窗口右上角“设置”，可以切换中英文、选择自动更新方式，并关闭、开启或自定义底部作者信息。\n\n故障检查\n打开“检测与日志”，可以检查线路、配对、虚拟副屏、编码和客户端呈现状态。",
                "Connect a display\nChoose TabLink client, Browser connection or USB debugging compatibility mode. Browser access requires trusting this computer's certificate once. Opening USB debugging mode refreshes devices automatically and selects the device when exactly one is connectable.\n\nRun in the background\nSelecting × hides the window to the tray while the display keeps running. Use the tray menu to stop the connection or exit.\n\nSettings\nSelect Settings at the top right to switch languages, choose the update mode, and hide, show or customise the footer author details.\n\nTroubleshooting\nOpen Diagnostics & logs to inspect the route, pairing, virtual display, encoder and client presentation state.")
        };
        var helpBody=new Panel{Name="helpBody",Dock=DockStyle.Fill,AutoScroll=true,Margin=new Padding(0,0,0,12)};
        helpBody.Controls.Add(instructions);
        helpBody.SizeChanged+=(_,_)=>instructions.MaximumSize=new Size(
            Math.Max(320,helpBody.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-8),0);
        root.Controls.Add(helpBody,0,1);
        var links=Flow();
        var github=new Button{Text="GitHub",AutoSize=true,Padding=new Padding(12,5,12,5)};
        var blog=new Button{Text=Ui("博客：linjie.space","Blog: linjie.space"),AutoSize=true,Padding=new Padding(12,5,12,5)};
        github.Click+=(_,_)=>OpenHelpLink(new Uri("https://github.com/linjierd/TabLink"));
        blog.Click+=(_,_)=>OpenHelpLink(new Uri("https://linjie.space/"));
        links.Controls.Add(github);links.Controls.Add(blog);root.Controls.Add(links,0,2);
        var close=new Button{Name="helpClose",Text=Ui("关闭","Close"),AutoSize=true,Padding=new Padding(16,6,16,6),DialogResult=DialogResult.OK,Anchor=AnchorStyles.Right};
        var commands=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Margin=new Padding(0,12,0,0)};
        commands.Controls.Add(close);root.Controls.Add(commands,0,3);dialog.AcceptButton=close;dialog.CancelButton=close;dialog.Controls.Add(root);
        return dialog;
    }

    void OpenHelpLink(Uri target)
    {
        try{Process.Start(new ProcessStartInfo(target.AbsoluteUri){UseShellExecute=true});}
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {ShowError(new IOException(Ui("无法打开链接，请检查系统默认浏览器。","The link could not be opened. Check the default browser."),ex));}
    }
}
