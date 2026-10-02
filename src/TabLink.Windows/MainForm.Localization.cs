using System.Globalization;
using System.Security;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed partial class MainForm
{
    readonly LanguagePreferencesStore languagePreferencesStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink", "language.json"));
    readonly CultureInfo startupUiCulture = CultureInfo.CurrentUICulture;
    ProductLanguageMode languageMode = ProductLanguageMode.System;
    ProductLanguage uiLanguage = ProductLanguage.English;
    LanguagePreferencesLoadStatus languageLoadStatus = LanguagePreferencesLoadStatus.MissingSystemDefault;
    bool populatingLanguage;
    bool applyingLanguage;
    string? localizedStatusChinese;
    string? localizedStatusEnglish;
    string? runtimeStatusSource;
    string? pairingHintChinese;
    string? pairingHintEnglish;
    string? browserHintChinese;
    string? browserHintEnglish;
    string? metricsChinese;
    string? metricsEnglish;
    readonly RadioButton systemLanguage = new() { Text = "跟随系统", AutoSize = true, Margin = new Padding(0, 2, 18, 2) };
    readonly RadioButton chineseLanguage = new() { Text = "简体中文", AutoSize = true, Margin = new Padding(0, 2, 18, 2) };
    readonly RadioButton englishLanguage = new() { Text = "English", AutoSize = true, Margin = new Padding(0, 2, 18, 2) };
    readonly Label languagePreferenceState = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(90, 107, 128),
        Margin = new Padding(0, 7, 0, 0),
        UseMnemonic = false
    };

    string Ui(string simplifiedChinese, string english) =>
        uiLanguage == ProductLanguage.SimplifiedChinese ? simplifiedChinese : english;

    void LoadLanguagePreference()
    {
        var loaded = languagePreferencesStore.Load(startupUiCulture);
        languageMode = loaded.Mode;
        uiLanguage = loaded.EffectiveLanguage;
        languageLoadStatus = loaded.Status;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
            uiLanguage == ProductLanguage.SimplifiedChinese ? "zh-CN" : "en-SG");
    }

    Control BuildLanguageSettingsPanel()
    {
        var group = new GroupBox
        {
            Text = Ui("语言", "Language"), Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = Ui("首次运行跟随 Windows 显示语言（中文系统使用简体中文，其他系统使用 English）。选择后立即保存并应用。",
                "On first run TabLink follows the Windows display language (zh-* uses Simplified Chinese; all others use English). Your selection is saved and applied immediately."),
            AutoSize = true, ForeColor = muted, MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 7), UseMnemonic = false
        }, 0, 0);
        var choices = Flow(systemLanguage, chineseLanguage, englishLanguage);
        layout.Controls.Add(choices, 0, 1);
        layout.Controls.Add(languagePreferenceState, 0, 2);
        group.Controls.Add(layout);
        systemLanguage.CheckedChanged += (_, _) => SaveLanguageWhenChecked(systemLanguage, ProductLanguageMode.System);
        chineseLanguage.CheckedChanged += (_, _) => SaveLanguageWhenChecked(chineseLanguage, ProductLanguageMode.SimplifiedChinese);
        englishLanguage.CheckedChanged += (_, _) => SaveLanguageWhenChecked(englishLanguage, ProductLanguageMode.English);
        PopulateLanguageEditors();
        RefreshLanguagePreferenceState();
        return group;
    }

    void PopulateLanguageEditors()
    {
        populatingLanguage = true;
        try
        {
            systemLanguage.Checked = languageMode == ProductLanguageMode.System;
            chineseLanguage.Checked = languageMode == ProductLanguageMode.SimplifiedChinese;
            englishLanguage.Checked = languageMode == ProductLanguageMode.English;
        }
        finally { populatingLanguage = false; }
    }

    void SaveLanguageWhenChecked(RadioButton editor, ProductLanguageMode mode)
    {
        if (populatingLanguage || !editor.Checked || mode == languageMode) return;
        var previousMode = languageMode;
        var previousLanguage = uiLanguage;
        try
        {
            languagePreferencesStore.Save(mode);
            languageMode = mode;
            uiLanguage = LanguagePreferencesStore.Resolve(mode, startupUiCulture);
            languageLoadStatus = LanguagePreferencesLoadStatus.Loaded;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                uiLanguage == ProductLanguage.SimplifiedChinese ? "zh-CN" : "en-SG");
            ApplyUiLanguage();
            RefreshLanguagePreferenceState(saved: true);
            Log(Ui("界面语言已切换为简体中文。", "The interface language has been changed to English."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            languageMode = previousMode;
            uiLanguage = previousLanguage;
            PopulateLanguageEditors();
            languagePreferenceState.Text = Ui("语言设置无法保存；继续使用原语言。", "The language preference could not be saved; the previous language remains active.");
            languagePreferenceState.ForeColor = Color.FromArgb(180, 45, 45);
            Log(Ui("语言设置无法保存：", "The language preference could not be saved: ") + SafeError(ex));
        }
    }

    void RefreshLanguagePreferenceState(bool saved = false)
    {
        languagePreferenceState.ForeColor = saved ? Color.FromArgb(36, 120, 70) : muted;
        languagePreferenceState.Text = saved
            ? Ui("已保存并立即应用。", "Saved and applied immediately.")
            : languageLoadStatus == LanguagePreferencesLoadStatus.InvalidSystemDefault
                ? Ui("语言设置文件无效，已安全回退到 Windows 显示语言。", "The language preference file is invalid, so TabLink safely follows the Windows display language.")
                : languageMode == ProductLanguageMode.System
                    ? Ui("当前跟随 Windows 显示语言。", "Currently following the Windows display language.")
                    : Ui("已使用保存的语言。", "Using the saved language.");
    }

    void ApplyUiLanguage()
    {
        if (applyingLanguage) return;
        var previousStatusChinese = localizedStatusChinese;
        var previousStatusEnglish = localizedStatusEnglish;
        var previousRuntimeStatusSource = runtimeStatusSource;
        applyingLanguage = true;
        SuspendLayout();
        try
        {
            Text = Ui("TabLink · 平板副屏", "TabLink · Second screen");
            ApplyLanguageToControlTree(this);
            ApplyLanguageToToolStrip(trayMenu.Items);
            if (healthStages.Columns.Count == 3)
            {
                healthStages.Columns[0].Width = uiLanguage == ProductLanguage.English ? 340 : 210;
                healthStages.Columns[1].Width = uiLanguage == ProductLanguage.English ? 100 : 90;
                ResizeHealthColumns();
            }
            tray.Text = Ui("TabLink · USB 平板副屏", "TabLink · USB second screen");
            RefreshChoiceLists();
            RebuildNetworkChoiceDisplay(networks);
            RebuildNetworkChoiceDisplay(browserNetworks);
            RefreshRequestedModes(reportEmpty:false);
            for (var index = 0; index < devices.Items.Count; index++)
                devices.Items[index] = devices.Items[index] is DeviceChoice device
                    ? new DeviceChoice(device.Device, device.Decision, uiLanguage)
                    : WindowsUiText.Translate(devices.Items[index]?.ToString(), uiLanguage);
            for (var index = 0; index < displays.Items.Count; index++)
                if (displays.Items[index] is string text) displays.Items[index] = WindowsUiText.Translate(text, uiLanguage);
            RefreshSessionList();
            var selectedBrowser = (browserSessions.SelectedItem as BrowserSessionRow)?.Status.Id;
            browserSessions.BeginUpdate();
            browserSessions.Items.Clear();
            foreach (var state in browserStates.Values.OrderBy(value => value.Id))
                browserSessions.Items.Add(new BrowserSessionRow(state, uiLanguage));
            foreach (var row in browserSessions.Items.OfType<BrowserSessionRow>())
                if (row.Status.Id == selectedBrowser) { browserSessions.SelectedItem = row; break; }
            browserSessions.EndUpdate();
            RefreshLanguagePreferenceState();
            RefreshUpdatePreferenceUi();
            UpdateNetworkButtons(!busy && !stopping && !closing && !exitStarting && !updateExitStarted &&
                !connectionStarts.IsStarting && !HasAnySessions && settingsValid);
            RefreshConnectionHealthUi();
            ShowConnectionMode();
            RefreshLocalizedTransientText();
            if (previousStatusChinese is not null && previousStatusEnglish is not null || previousRuntimeStatusSource is not null)
            {
                localizedStatusChinese = previousStatusChinese;
                localizedStatusEnglish = previousStatusEnglish;
                runtimeStatusSource = previousRuntimeStatusSource;
            }
            RefreshLocalizedStatus();
            RefreshEncoderMetrics();
            ResizeAuthorFooterRow();
        }
        finally
        {
            ResumeLayout(performLayout: true);
            PerformLayout();
            applyingLanguage = false;
        }
    }

    void ApplyLanguageToControlTree(Control root)
    {
        root.Text = WindowsUiText.Translate(root.Text, uiLanguage);
        if (root is TextBox textBox)
            textBox.PlaceholderText = WindowsUiText.Translate(textBox.PlaceholderText, uiLanguage);
        if (root is ListView listView)
            foreach (ColumnHeader column in listView.Columns)
                column.Text = WindowsUiText.Translate(column.Text, uiLanguage);
        foreach (Control child in root.Controls) ApplyLanguageToControlTree(child);
    }

    void ApplyLanguageToToolStrip(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.Text = WindowsUiText.Translate(item.Text, uiLanguage);
            if (item is ToolStripDropDownItem dropDown) ApplyLanguageToToolStrip(dropDown.DropDownItems);
        }
    }

    void RefreshChoiceLists()
    {
        var connectionIndex = Math.Max(0, connectionMode.SelectedIndex);
        var selectedQualityValue = (qualityMode.SelectedItem as QualityChoice)?.Preset ?? selectedQuality;
        var selectedEncoderValue = (encoderMode.SelectedItem as EncoderChoice)?.Preference ?? selectedEncoder;
        connectionMode.BeginUpdate();
        connectionMode.Items.Clear();
        connectionMode.Items.AddRange(uiLanguage == ProductLanguage.SimplifiedChinese
            ? ["TabLink 客户端（推荐）", "浏览器接入", "USB 调试（兼容）"]
            : ["TabLink client (recommended)", "Browser connection", "USB debugging (compatibility)"]);
        connectionMode.SelectedIndex = Math.Min(connectionIndex, connectionMode.Items.Count - 1);
        connectionMode.EndUpdate();
        qualityMode.BeginUpdate();
        qualityMode.Items.Clear();
        qualityMode.Items.AddRange(Enum.GetValues<VideoQualityPreset>()
            .Select(value => (object)new QualityChoice(value, uiLanguage)).ToArray());
        qualityMode.SelectedItem = qualityMode.Items.OfType<QualityChoice>().First(item => item.Preset == selectedQualityValue);
        qualityMode.EndUpdate();
        encoderMode.BeginUpdate();
        encoderMode.Items.Clear();
        encoderMode.Items.AddRange(Enum.GetValues<VideoEncoderPreference>()
            .Select(value => (object)new EncoderChoice(value, uiLanguage)).ToArray());
        encoderMode.SelectedItem = encoderMode.Items.OfType<EncoderChoice>().First(item => item.Preference == selectedEncoderValue);
        encoderMode.EndUpdate();
        LoadRules();
    }

    void ConfigureNetworkChoiceFormatting()
    {
        networks.FormattingEnabled = true;
        browserNetworks.FormattingEnabled = true;
        networks.Format += FormatNetworkChoice;
        browserNetworks.Format += FormatNetworkChoice;
    }

    void FormatNetworkChoice(object? sender, ListControlConvertEventArgs args)
    {
        if (args.ListItem is NetworkInterfaceChoice choice)
            args.Value = choice.DisplayTextFor(uiLanguage == ProductLanguage.English);
    }

    void RebuildNetworkChoiceDisplay(ComboBox editor)
    {
        var selectedId = (editor.SelectedItem as NetworkInterfaceChoice)?.InterfaceId;
        var selectedIndex = editor.SelectedIndex;
        var items = editor.Items.Cast<object>().Select(item => item is string text
            ? (object)WindowsUiText.Translate(text, uiLanguage)
            : item).ToArray();
        editor.BeginUpdate();
        try
        {
            editor.Items.Clear();
            editor.Items.AddRange(items);
            if (selectedId is not null)
                editor.SelectedItem = editor.Items.OfType<NetworkInterfaceChoice>().FirstOrDefault(choice =>
                    string.Equals(choice.InterfaceId, selectedId, StringComparison.OrdinalIgnoreCase));
            else if (selectedIndex >= 0 && editor.Items.Count > 0)
                editor.SelectedIndex = Math.Min(selectedIndex, editor.Items.Count - 1);
        }
        finally { editor.EndUpdate(); }
        editor.Refresh();
    }

    string LocalizedSafeError(Exception ex, bool adbOperation = false) =>
        SafeErrorForLanguage(ex, uiLanguage, adbOperation);

    static string SafeErrorForLanguage(Exception ex, ProductLanguage language, bool adbOperation = false)
    {
        var summary = SafeErrorSummary.ForUser(ex, adbOperation);
        if (language == ProductLanguage.SimplifiedChinese || !WindowsUiText.ContainsHan(summary)) return summary;
        return ex switch
        {
            AdbCommandException command => $"ADB operation failed ({nameof(AdbCommandException)}, exit code {command.ExitCode}).",
            AdbExecutionException => $"ADB operation failed ({nameof(AdbExecutionException)}).",
            AdbResponseException => $"ADB operation failed ({nameof(AdbResponseException)}).",
            AdbDeviceTemporarilyUnavailableException => $"ADB operation failed ({nameof(AdbDeviceTemporarilyUnavailableException)}).",
            _ => $"The operation could not be completed ({ex.GetType().Name})."
        };
    }

    string RuntimeUi(string message) => WindowsUiText.TranslateRuntime(message, uiLanguage);
    string UsbRecoveryDetail(string detail) => WindowsUiText.TranslateUsbRecoveryDetail(detail, uiLanguage);

    void RefreshLocalizedStatus()
    {
        if (localizedStatusChinese is not null && localizedStatusEnglish is not null)
            status.Text = Ui(localizedStatusChinese, localizedStatusEnglish);
        else if (runtimeStatusSource is not null)
            status.Text = RuntimeUi(runtimeStatusSource);
    }

    void SetPairingHint(string simplifiedChinese, string english)
    {
        pairingHintChinese = simplifiedChinese;
        pairingHintEnglish = english;
        pairingHint.Text = Ui(simplifiedChinese, english);
    }

    void SetBrowserHint(string simplifiedChinese, string english)
    {
        browserHintChinese = simplifiedChinese;
        browserHintEnglish = english;
        browserHint.Text = Ui(simplifiedChinese, english);
    }

    void SetMetrics(string simplifiedChinese, string english)
    {
        metricsChinese = simplifiedChinese;
        metricsEnglish = english;
        metrics.Text = Ui(simplifiedChinese, english);
    }

    void RefreshLocalizedTransientText()
    {
        if (pairingHintChinese is not null && pairingHintEnglish is not null)
            pairingHint.Text = Ui(pairingHintChinese, pairingHintEnglish);
        if (browserHintChinese is not null && browserHintEnglish is not null)
            browserHint.Text = Ui(browserHintChinese, browserHintEnglish);
        if (metricsChinese is not null && metricsEnglish is not null)
            metrics.Text = Ui(metricsChinese, metricsEnglish);
        RefreshDiagnosticReportLanguage();
    }
}
