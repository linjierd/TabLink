namespace TabLink.Windows;

internal sealed class SecondScreenWelcome : Form
{
    readonly System.Windows.Forms.Timer clock=new(){Interval=1000};
    readonly Label time=new(){AutoSize=true,Font=new Font("Segoe UI",28),ForeColor=Color.FromArgb(130,194,255)};
    public SecondScreenWelcome(Rectangle displayBounds)
    {
        Text="TabLink · 副屏已就绪";StartPosition=FormStartPosition.Manual;
        AutoScaleMode=AutoScaleMode.Dpi;Bounds=displayBounds;BackColor=Color.FromArgb(14,24,41);ForeColor=Color.White;
        Font=new Font("Microsoft YaHei UI",15);WindowState=FormWindowState.Normal;
        var layout=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(52),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        layout.Controls.Add(new Label{Text="TabLink",Font=new Font("Segoe UI",48,FontStyle.Bold),AutoSize=true});
        layout.Controls.Add(new Label{Text="平板现在是电脑的第二块桌面",Font=new Font("Microsoft YaHei UI",23,FontStyle.Bold),AutoSize=true,Margin=new Padding(0,18,0,18)});
        layout.Controls.Add(new Label{Text="这是独立的 Windows 扩展屏。\n将电脑窗口拖到主屏右侧，就可以在这里继续使用。\n\n点击下方按钮收起说明，即可看到副屏桌面。\n电脑上的 TabLink 需要保持运行；停止连接会自动收回副屏。",AutoSize=true,MaximumSize=new Size(1050,0),Margin=new Padding(0,10,0,22)});
        layout.Controls.Add(time);
        var close=new Button{Text="收起说明，使用副屏",AutoSize=true,Padding=new Padding(16,9,16,9),BackColor=Color.FromArgb(31,105,210),ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Margin=new Padding(0,24,0,0)};
        close.Click+=(_,_)=>Close();layout.Controls.Add(close);Controls.Add(layout);
        void FitText()
        {
            var width=Math.Max(240,layout.ClientSize.Width-layout.Padding.Horizontal-24);
            foreach(var label in layout.Controls.OfType<Label>())label.MaximumSize=new Size(width,0);
        }
        layout.SizeChanged+=(_,_)=>FitText();Shown+=(_,_)=>FitText();
        clock.Tick+=(_,_)=>time.Text=DateTime.Now.ToString("HH:mm:ss");time.Text=DateTime.Now.ToString("HH:mm:ss");clock.Start();
        FormClosed+=(_,_)=>clock.Dispose();
    }
}
