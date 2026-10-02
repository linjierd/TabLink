using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly UpdatePreferencesStore updatePreferencesStore=new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","update-preferences.json"));
    UpdatePreferences updatePreferences=new(UpdateMode.Automatic);
    UpdatePreferencesLoadStatus updatePreferencesLoadStatus=UpdatePreferencesLoadStatus.MissingDefault;
    bool populatingUpdatePreferences;
    bool changingUpdateMode;

    readonly RadioButton automaticUpdates=new()
    {
        Text="自动更新（推荐）",AutoSize=true,Margin=new Padding(0,2,0,2)
    };
    readonly RadioButton downloadThenAskUpdates=new()
    {
        Text="自动下载后手动安装",AutoSize=true,Margin=new Padding(0,2,0,2)
    };
    readonly RadioButton neverUpdates=new()
    {
        Text="从不更新",AutoSize=true,Margin=new Padding(0,2,0,2)
    };
    readonly Label updatePreferenceState=new()
    {
        AutoSize=true,ForeColor=Color.FromArgb(90,107,128),MaximumSize=new Size(820,0),
        Margin=new Padding(0,7,0,7),UseMnemonic=false
    };
    readonly Button installReadyUpdate=new(){Text="重启并安装已下载更新",AutoSize=true};

    UpdateMode CurrentUpdateMode=>updatePreferences.Mode;
    string ExpectedProtectedInstallDirectory=>WindowsUpdatePathPolicy.GetExpectedInstallDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    bool CanInstallReadyUpdateFromCurrentLocation=>Path.GetFullPath(AppContext.BaseDirectory)
        .TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)
        .Equals(ExpectedProtectedInstallDirectory,StringComparison.OrdinalIgnoreCase);

    Control BuildUpdateSettingsPanel()
    {
        var group=new GroupBox
        {
            Text="软件更新",Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            Padding=new Padding(12),Margin=new Padding(0,0,0,12)
        };
        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            ColumnCount=1,RowCount=4,Margin=Padding.Empty,Padding=Padding.Empty
        };
        for(var row=0;row<layout.RowCount;row++)layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var help=new Label
        {
            Text="选择 TabLink 如何获取并安装已签名的正式版更新。更改后立即保存并生效。",
            AutoSize=true,ForeColor=Color.FromArgb(90,107,128),MaximumSize=new Size(820,0),
            Margin=new Padding(0,0,0,7),UseMnemonic=false
        };
        var automaticHelp=UpdatePreferenceHelp("后台检查并下载；没有副屏连接且电脑空闲时自动重启安装。");
        var downloadHelp=UpdatePreferenceHelp("后台检查并下载；只有点击设置页或托盘中的安装按钮后才会重启安装。");
        var neverHelp=UpdatePreferenceHelp("不检查、不下载，也不会在退出 TabLink 时安装已缓存的更新。");
        var choices=new TableLayoutPanel
        {
            Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            ColumnCount=1,RowCount=6,Margin=Padding.Empty,Padding=Padding.Empty
        };
        for(var row=0;row<choices.RowCount;row++)choices.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        choices.Controls.Add(automaticUpdates,0,0);choices.Controls.Add(automaticHelp,0,1);
        choices.Controls.Add(downloadThenAskUpdates,0,2);choices.Controls.Add(downloadHelp,0,3);
        choices.Controls.Add(neverUpdates,0,4);choices.Controls.Add(neverHelp,0,5);
        layout.Controls.Add(help,0,0);layout.Controls.Add(choices,0,1);
        layout.Controls.Add(updatePreferenceState,0,2);layout.Controls.Add(installReadyUpdate,0,3);
        group.Controls.Add(layout);

        automaticUpdates.CheckedChanged+=async(_,_)=>await SaveUpdateModeWhenCheckedAsync(automaticUpdates,UpdateMode.Automatic);
        downloadThenAskUpdates.CheckedChanged+=async(_,_)=>await SaveUpdateModeWhenCheckedAsync(downloadThenAskUpdates,UpdateMode.DownloadThenAsk);
        neverUpdates.CheckedChanged+=async(_,_)=>await SaveUpdateModeWhenCheckedAsync(neverUpdates,UpdateMode.Never);
        installReadyUpdate.Click+=async(_,_)=>await InstallReadyUpdateAsync();
        PopulateUpdatePreferenceEditors(updatePreferences.Mode);
        RefreshUpdatePreferenceUi();
        return group;
    }

    static Label UpdatePreferenceHelp(string text)=>new()
    {
        Text=text,AutoSize=true,ForeColor=Color.FromArgb(90,107,128),
        Margin=new Padding(26,0,0,5),UseMnemonic=false
    };

    void PopulateUpdatePreferenceEditors(UpdateMode mode)
    {
        populatingUpdatePreferences=true;
        try
        {
            automaticUpdates.Checked=mode==UpdateMode.Automatic;
            downloadThenAskUpdates.Checked=mode==UpdateMode.DownloadThenAsk;
            neverUpdates.Checked=mode==UpdateMode.Never;
        }
        finally{populatingUpdatePreferences=false;}
    }

    async Task SaveUpdateModeWhenCheckedAsync(RadioButton editor,UpdateMode mode)
    {
        if(populatingUpdatePreferences||!editor.Checked||mode==updatePreferences.Mode)return;
        if(changingUpdateMode){PopulateUpdatePreferenceEditors(updatePreferences.Mode);return;}
        if(closing||exitStarting||updateExitStarted){PopulateUpdatePreferenceEditors(updatePreferences.Mode);return;}
        var previous=updatePreferences;
        var previousStatus=updatePreferencesLoadStatus;
        changingUpdateMode=true;
        RefreshUpdatePreferenceUi(Ui("正在应用更新方式…","Applying update preference…"));
        try
        {
            var next=new UpdatePreferences(mode);
            updatePreferencesStore.Save(next);
            updatePreferences=next;
            updatePreferencesLoadStatus=UpdatePreferencesLoadStatus.Loaded;
            await ApplyUpdateModeAsync(mode);
            changingUpdateMode=false;
            RefreshUpdatePreferenceUi(Ui("已保存并立即生效。","Saved and applied immediately."));
            Log(Ui("软件更新方式已设置为“","Update mode set to “")+UpdateModeDisplay(mode)+Ui("”。","”."));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException)
        {
            if(mode==UpdateMode.Never)
            {
                updatePreferences=UpdatePreferences.FailClosed;
                updatePreferencesLoadStatus=UpdatePreferencesLoadStatus.InvalidFailClosed;
                PopulateUpdatePreferenceEditors(UpdateMode.Never);
                await ApplyUpdateModeAsync(UpdateMode.Never);
                changingUpdateMode=false;
                RefreshUpdatePreferenceUi(Ui("“从不更新”未能写入磁盘；本次运行已安全关闭更新，重启后请再次保存。原因：","“Never update” could not be written to disk. Updates are safely disabled for this run; save again after restarting. Reason: ")+SafeError(ex));
                Log(Ui("“从不更新”未能持久保存；本次运行已安全关闭更新：","“Never update” could not be persisted; updates are disabled for this run: ")+SafeError(ex));
                return;
            }
            updatePreferences=previous;
            updatePreferencesLoadStatus=previousStatus;
            PopulateUpdatePreferenceEditors(previous.Mode);
            changingUpdateMode=false;
            RefreshUpdatePreferenceUi(Ui("更新方式未能保存：","The update mode could not be saved: ")+SafeError(ex));
            Log(Ui("软件更新方式未能保存：","The software update mode could not be saved: ")+SafeError(ex));
        }
        finally
        {
            if(changingUpdateMode)
            {
                changingUpdateMode=false;
                RefreshUpdatePreferenceUi();
            }
        }
    }

    void RefreshUpdatePreferenceUi(string? prefix=null)
    {
        var ready=updateCoordinator?.Ready;
        var detail=CurrentUpdateMode switch
        {
            UpdateMode.Never=>Ui("更新检查已关闭。","Update checks are disabled."),
            UpdateMode.DownloadThenAsk when ready is not null=>Ui($"正式版 {ready.Version} 已安全下载，等待手动安装。",$"Stable release {ready.Version} was downloaded and verified; waiting for manual installation."),
            UpdateMode.Automatic when ready is not null=>Ui($"正式版 {ready.Version} 已安全下载，将在没有连接和操作时自动安装。",$"Stable release {ready.Version} was downloaded and verified; it will install when no connection or operation is active."),
            UpdateMode.DownloadThenAsk=>Ui("将自动检查和下载正式版；安装前会等待你的明确操作。","TabLink checks for and downloads stable releases automatically, then waits for you to install."),
            _=>Ui("将自动检查、下载并在空闲时安装正式版。","TabLink checks for, downloads and installs stable releases while idle.")
        };
        if(CurrentUpdateMode!=UpdateMode.Never&&!CanInstallReadyUpdateFromCurrentLocation)
            detail+=Ui(" 当前从便携目录运行；可以检查和下载，安装需从 "," TabLink is running from a portable directory; it can check and download, but installation must be started from ")+ExpectedProtectedInstallDirectory+Ui(" 启动。",".");
        if(string.IsNullOrWhiteSpace(prefix)&&updatePreferencesLoadStatus==UpdatePreferencesLoadStatus.InvalidFailClosed)
            prefix=Ui("更新偏好文件无效，已安全切换为“从不更新”。","The update preference file is invalid; TabLink safely switched to Never update.");
        if(string.IsNullOrWhiteSpace(prefix)&&CurrentUpdateMode!=UpdateMode.Never&&!string.IsNullOrWhiteSpace(updateConfigurationError))
            prefix=updateConfigurationError;
        updatePreferenceState.Text=string.IsNullOrWhiteSpace(prefix)?detail:prefix+" "+detail;
        var canChange=!changingUpdateMode&&!closing&&!exitStarting&&!updateExitStarted;
        automaticUpdates.Enabled=downloadThenAskUpdates.Enabled=neverUpdates.Enabled=canChange;
        installReadyUpdate.Visible=CurrentUpdateMode!=UpdateMode.Never;
        installReadyUpdate.Enabled=installReadyUpdate.Visible&&CanInstallReadyUpdateFromCurrentLocation&&ready is not null&&!busy&&!stopping&&
            !closing&&!exitStarting&&!updateExitStarted&&!connectionStarts.IsStarting;
    }

    void ResizeUpdateSettings(int width)
    {
        updatePreferenceState.MaximumSize=new Size(width,0);
    }

    string UpdateModeDisplay(UpdateMode mode)=>mode switch
    {
        UpdateMode.Automatic=>Ui("自动更新","Automatic updates"),
        UpdateMode.DownloadThenAsk=>Ui("自动下载后手动安装","Download automatically, install manually"),
        UpdateMode.Never=>Ui("从不更新","Never update"),
        _=>mode.ToString()
    };
}
