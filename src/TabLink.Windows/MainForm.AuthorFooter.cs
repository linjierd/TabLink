using System.Diagnostics;
using System.Security;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly AuthorFooterPreferencesStore authorFooterStore=new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","author-footer.json"));
    AuthorFooterPreferences authorFooterPreferences=AuthorFooterPreferences.CreateDefault();

    readonly CheckBox authorFooterEnabledEditor=new(){Text="显示底部作者信息",AutoSize=true};
    readonly TextBox authorFooterAuthorEditor=new(){Dock=DockStyle.Fill,MaxLength=AuthorFooterPreferences.MaximumAuthorTextLength};
    readonly TextBox authorFooterGitHubLabelEditor=new(){Dock=DockStyle.Fill,MaxLength=AuthorFooterPreferences.MaximumLinkLabelLength};
    readonly TextBox authorFooterGitHubUrlEditor=new(){Dock=DockStyle.Fill,MaxLength=AuthorFooterPreferences.MaximumUrlLength};
    readonly TextBox authorFooterBlogLabelEditor=new(){Dock=DockStyle.Fill,MaxLength=AuthorFooterPreferences.MaximumLinkLabelLength};
    readonly TextBox authorFooterBlogUrlEditor=new(){Dock=DockStyle.Fill,MaxLength=AuthorFooterPreferences.MaximumUrlLength};
    readonly Button saveAuthorFooter=new(){Text="保存并应用"};
    readonly Button restoreAuthorFooter=new(){Text="恢复默认"};
    readonly Label authorFooterHelp=new()
    {
        Text="作者信息默认显示在主窗口底部。保存后立即生效，不会停止当前副屏；链接只接受完整的 HTTPS 地址。",
        AutoSize=true,ForeColor=Color.FromArgb(90,107,128),Margin=new Padding(0,0,0,8)
    };
    readonly Label authorFooterPreferenceState=new()
    {
        AutoSize=true,ForeColor=Color.FromArgb(90,107,128),Margin=new Padding(8,8,0,0),UseMnemonic=false
    };

    readonly FlowLayoutPanel authorFooterRow=new()
    {
        Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
        FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=Padding.Empty,Padding=Padding.Empty
    };
    readonly Label authorFooterAuthor=new()
    {
        AutoSize=false,AutoEllipsis=true,ForeColor=Color.FromArgb(90,107,128),
        TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0,0,12,0),UseMnemonic=false
    };
    readonly LinkLabel authorFooterGitHub=new()
    {
        AutoSize=false,AutoEllipsis=true,LinkColor=Color.FromArgb(31,105,210),ActiveLinkColor=Color.FromArgb(31,105,210),
        VisitedLinkColor=Color.FromArgb(31,105,210),TextAlign=ContentAlignment.MiddleLeft,
        Margin=new Padding(0,0,12,0),UseMnemonic=false
    };
    readonly LinkLabel authorFooterBlog=new()
    {
        AutoSize=false,AutoEllipsis=true,LinkColor=Color.FromArgb(31,105,210),ActiveLinkColor=Color.FromArgb(31,105,210),
        VisitedLinkColor=Color.FromArgb(31,105,210),TextAlign=ContentAlignment.MiddleLeft,
        Margin=Padding.Empty,UseMnemonic=false
    };

    Control BuildAuthorFooterSettingsPanel()
    {
        var group=new GroupBox
        {
            Text="底部作者信息",Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            Padding=new Padding(12),Margin=new Padding(0,0,0,12)
        };
        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            ColumnCount=2,RowCount=8,Margin=Padding.Empty,Padding=Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(var row=0;row<layout.RowCount;row++)layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(authorFooterHelp,0,0);layout.SetColumnSpan(authorFooterHelp,2);
        authorFooterEnabledEditor.Margin=new Padding(0,2,0,8);
        layout.Controls.Add(authorFooterEnabledEditor,0,1);layout.SetColumnSpan(authorFooterEnabledEditor,2);
        AddAuthorFooterEditorRow(layout,2,"作者文字",authorFooterAuthorEditor);
        AddAuthorFooterEditorRow(layout,3,"GitHub 显示文字",authorFooterGitHubLabelEditor);
        AddAuthorFooterEditorRow(layout,4,"GitHub 地址",authorFooterGitHubUrlEditor);
        AddAuthorFooterEditorRow(layout,5,"博客显示文字",authorFooterBlogLabelEditor);
        AddAuthorFooterEditorRow(layout,6,"博客地址",authorFooterBlogUrlEditor);

        var commands=Flow(saveAuthorFooter,restoreAuthorFooter);
        commands.Controls.Add(authorFooterPreferenceState);
        layout.Controls.Add(commands,0,7);layout.SetColumnSpan(commands,2);
        group.Controls.Add(layout);

        saveAuthorFooter.Click+=(_,_)=>SaveAndApplyAuthorFooterPreferences();
        restoreAuthorFooter.Click+=(_,_)=>RestoreDefaultAuthorFooterPreferences();
        PopulateAuthorFooterEditors(authorFooterPreferences);
        authorFooterEnabledEditor.CheckedChanged+=(_,_)=>MarkAuthorFooterPreferencesDirty();
        foreach(var editor in new[]{authorFooterAuthorEditor,authorFooterGitHubLabelEditor,
            authorFooterGitHubUrlEditor,authorFooterBlogLabelEditor,authorFooterBlogUrlEditor})
            editor.TextChanged+=(_,_)=>MarkAuthorFooterPreferencesDirty();
        return group;
    }

    static void AddAuthorFooterEditorRow(TableLayoutPanel layout,int row,string caption,Control editor)
    {
        var label=new Label
        {
            Text=caption,AutoSize=true,ForeColor=Color.FromArgb(90,107,128),
            TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0,7,14,6),UseMnemonic=false
        };
        editor.Margin=new Padding(0,2,0,6);
        layout.Controls.Add(label,0,row);layout.Controls.Add(editor,1,row);
    }

    Control BuildFooter()
    {
        var footer=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            ColumnCount=1,RowCount=2,Margin=Padding.Empty,Padding=Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label
        {
            Text="只启用一块副屏  ·  点 × 后在托盘继续运行",Dock=DockStyle.Fill,AutoSize=true,
            ForeColor=muted,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty,UseMnemonic=false
        },0,0);

        authorFooterGitHub.LinkClicked+=OpenAuthorFooterLink;
        authorFooterBlog.LinkClicked+=OpenAuthorFooterLink;
        authorFooterRow.Controls.Add(authorFooterAuthor);
        authorFooterRow.Controls.Add(authorFooterGitHub);
        authorFooterRow.Controls.Add(authorFooterBlog);
        authorFooterRow.SizeChanged+=(_,_)=>ResizeAuthorFooterRow();
        footer.SizeChanged+=(_,_)=>ResizeAuthorFooterRow();
        SizeChanged+=(_,_)=>ResizeAuthorFooterRow();
        footer.Controls.Add(authorFooterRow,0,1);
        ApplyAuthorFooterPreferences(authorFooterPreferences);
        return footer;
    }

    void PopulateAuthorFooterEditors(AuthorFooterPreferences preferences)
    {
        authorFooterEnabledEditor.Checked=preferences.Enabled;
        authorFooterAuthorEditor.Text=preferences.AuthorText;
        authorFooterGitHubLabelEditor.Text=preferences.GitHubLabel;
        authorFooterGitHubUrlEditor.Text=preferences.GitHubUrl;
        authorFooterBlogLabelEditor.Text=preferences.BlogLabel;
        authorFooterBlogUrlEditor.Text=preferences.BlogUrl;
    }

    AuthorFooterPreferences ReadAuthorFooterEditors()=>new()
    {
        Enabled=authorFooterEnabledEditor.Checked,
        AuthorText=authorFooterAuthorEditor.Text,
        GitHubLabel=authorFooterGitHubLabelEditor.Text,
        GitHubUrl=authorFooterGitHubUrlEditor.Text,
        BlogLabel=authorFooterBlogLabelEditor.Text,
        BlogUrl=authorFooterBlogUrlEditor.Text
    };

    void SaveAndApplyAuthorFooterPreferences()
    {
        try
        {
            var saved=ReadAuthorFooterEditors().NormalizeAndValidate();
            authorFooterStore.Save(saved);
            authorFooterPreferences=saved;
            PopulateAuthorFooterEditors(saved);
            ApplyAuthorFooterPreferences(saved);
            SetAuthorFooterPreferenceState("已保存并应用。",false);
            Log("底部作者信息已保存并应用。");
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            SetAuthorFooterPreferenceState(AuthorFooterPreferenceError(ex),true);
            Log("底部作者信息未保存："+SafeError(ex));
        }
    }

    void RestoreDefaultAuthorFooterPreferences()
    {
        PopulateAuthorFooterEditors(AuthorFooterPreferences.CreateDefault());
        SaveAndApplyAuthorFooterPreferences();
    }

    void ApplyAuthorFooterPreferences(AuthorFooterPreferences preferences)
    {
        var applied=preferences.NormalizeAndValidate();
        authorFooterAuthor.Text=applied.AuthorText;
        ConfigureAuthorFooterLink(authorFooterGitHub,applied.GitHubLabel,applied.GitHubUrl);
        ConfigureAuthorFooterLink(authorFooterBlog,applied.BlogLabel,applied.BlogUrl);
        authorFooterRow.Visible=applied.Enabled;
        ResizeAuthorFooterRow();
        authorFooterRow.Parent?.PerformLayout();
    }

    static void ConfigureAuthorFooterLink(LinkLabel link,string label,string address)
    {
        link.Text=label;
        link.Tag=TryCreateSafeAuthorUri(address);
        link.Visible=label.Length>0&&link.Tag is Uri;
        link.Enabled=link.Tag is Uri;
    }

    static Uri? TryCreateSafeAuthorUri(string address)
    {
        if(string.IsNullOrWhiteSpace(address)||address.Length>AuthorFooterPreferences.MaximumUrlLength||
            address.Any(char.IsControl)||!Uri.TryCreate(address,UriKind.Absolute,out var uri)||
            !string.Equals(uri.Scheme,Uri.UriSchemeHttps,StringComparison.OrdinalIgnoreCase)||
            string.IsNullOrWhiteSpace(uri.IdnHost)||!string.IsNullOrEmpty(uri.UserInfo)||
            uri.AbsoluteUri.Length>AuthorFooterPreferences.MaximumUrlLength)return null;
        return uri;
    }

    void OpenAuthorFooterLink(object? sender,LinkLabelLinkClickedEventArgs _)
    {
        if(sender is not LinkLabel{Tag:Uri target}||TryCreateSafeAuthorUri(target.AbsoluteUri) is not {} safe)
        {
            NotifyAuthorFooterLinkFailure("链接地址无效，请在设置中重新保存。");
            Log("底部作者链接未打开：保存的地址未通过 HTTPS 校验。");
            return;
        }
        try{Process.Start(new ProcessStartInfo(safe.AbsoluteUri){UseShellExecute=true});}
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or SecurityException)
        {
            NotifyAuthorFooterLinkFailure("无法打开链接，请检查系统默认浏览器。");
            Log("无法打开底部作者链接："+SafeError(ex));
        }
    }

    void NotifyAuthorFooterLinkFailure(string message)
    {
        SetAuthorFooterPreferenceState(message,true);
        tray.ShowBalloonTip(4000,"TabLink",message,ToolTipIcon.Info);
    }

    void SetAuthorFooterPreferenceState(string message,bool error)
    {
        authorFooterPreferenceState.Text=message;
        authorFooterPreferenceState.ForeColor=error?Color.FromArgb(180,45,45):Color.FromArgb(36,120,70);
    }

    void MarkAuthorFooterPreferencesDirty()
    {
        authorFooterPreferenceState.Text="有尚未保存的更改。";
        authorFooterPreferenceState.ForeColor=muted;
    }

    static string AuthorFooterPreferenceError(Exception ex)
    {
        if(ex is ArgumentException argument)
        {
            if(argument.ParamName is nameof(AuthorFooterPreferences.GitHubUrl) or nameof(AuthorFooterPreferences.BlogUrl))
                return "未保存：链接必须是完整的 HTTPS 地址，且不能包含账号信息。";
            return "未保存：请检查作者文字、链接文字和地址，不能包含换行或超出长度限制。";
        }
        return "未保存：设置文件所在位置当前不可写。";
    }

    void ResizeAuthorFooterSettings(int availableWidth)
    {
        var width=Math.Max(240,availableWidth);
        authorFooterHelp.MaximumSize=new Size(width,0);
        authorFooterPreferenceState.MaximumSize=new Size(width,0);
    }

    void ResizeAuthorFooterRow()
    {
        if(authorFooterRow.ClientSize.Width<=0)return;
        var width=Math.Max(240,ClientSize.Width-32-authorFooterRow.Padding.Horizontal-4);
        var rowHeight=Math.Max(24,Font.Height+7);
        var linkLimit=Math.Max(88,width/4);
        var githubWidth=authorFooterGitHub.Visible
            ?Math.Min(MeasureFooterText(authorFooterGitHub),linkLimit):0;
        var blogWidth=authorFooterBlog.Visible
            ?Math.Min(MeasureFooterText(authorFooterBlog),linkLimit):0;
        var linkMargins=(authorFooterGitHub.Visible?authorFooterGitHub.Margin.Horizontal:0)+
            (authorFooterBlog.Visible?authorFooterBlog.Margin.Horizontal:0);
        var authorLimit=Math.Max(120,width-githubWidth-blogWidth-linkMargins-authorFooterAuthor.Margin.Horizontal);
        var authorWidth=Math.Min(MeasureFooterText(authorFooterAuthor),authorLimit);
        authorFooterAuthor.Size=new Size(authorWidth,rowHeight);
        authorFooterGitHub.Size=new Size(githubWidth,rowHeight);
        authorFooterBlog.Size=new Size(blogWidth,rowHeight);
    }

    static int MeasureFooterText(Control control)=>Math.Max(16,
        TextRenderer.MeasureText(control.Text,control.Font,new Size(int.MaxValue,int.MaxValue),
            TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+24);
}
