using TabLink.Core;

namespace TabLink.Windows;

internal static class WindowsUiText
{
    internal static bool ContainsHan(string value) => value.Any(character => character >= '\u3400' && character <= '\u9fff');

    internal static string Translate(string? value, ProductLanguage language)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        foreach (var pair in Pairs)
        {
            if (language == ProductLanguage.English && string.Equals(value, pair.Zh, StringComparison.Ordinal)) return pair.En;
            if (language == ProductLanguage.SimplifiedChinese && string.Equals(value, pair.En, StringComparison.Ordinal)) return pair.Zh;
        }
        return value;
    }

    internal static string TranslateRuntime(string value, ProductLanguage language)
    {
        var translated = Translate(value, language);
        if (language == ProductLanguage.SimplifiedChinese || !ContainsHan(translated)) return translated;
        var known = TranslateKnownRuntime(value);
        if (known is not null) return known;
        if (value.StartsWith("连接中断，等待平板重连：", StringComparison.Ordinal) ||
            value.StartsWith("加密连接已结束，等待平板重新连接（", StringComparison.Ordinal))
            return TranslateNativeSessionState(value, language);
        var firstHan = -1;
        for (var index = 0; index < translated.Length; index++)
            if (translated[index] is >= '\u3400' and <= '\u9fff') { firstHan = index; break; }
        if (firstHan > 0)
        {
            var englishPrefix = translated[..firstHan].TrimEnd();
            if (englishPrefix.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                return englishPrefix + " Details are available in Diagnostics & logs.";
        }
        return "A diagnostic event was recorded. Open Diagnostics & logs for the current connection state.";
    }

    internal static string TranslateNativeSessionState(string value, ProductLanguage language)
    {
        if (language == ProductLanguage.SimplifiedChinese) return value;
        var translated = Translate(value, language);
        if (!ContainsHan(translated)) return translated;
        var known = TranslateKnownRuntime(value);
        if (known is not null) return known;
        var separator = value.IndexOf(" · ", StringComparison.Ordinal);
        if (separator > 0 && value[..separator].Contains('×'))
            return value[..(separator + 3)] + TranslateNativeSessionState(value[(separator + 3)..], language);
        if (value.StartsWith("呈现回调 ", StringComparison.Ordinal) && value.EndsWith(" 帧/秒", StringComparison.Ordinal))
            return "presentation callbacks " + value[5..^4] + " fps";
        if (value.StartsWith("解码提交 ", StringComparison.Ordinal) && value.EndsWith(" 帧/秒（呈现待验证）", StringComparison.Ordinal))
            return "decode submission " + value[5..^11] + " fps (presentation awaiting verification)";
        if (value.StartsWith("连接中断，等待平板重连：", StringComparison.Ordinal))
            return "Connection interrupted; waiting for the tablet to reconnect.";
        if (value.StartsWith("加密连接已结束，等待平板重新连接（", StringComparison.Ordinal))
        {
            var suffix = value["加密连接已结束，等待平板重新连接（".Length..].TrimEnd('。');
            return "The encrypted connection ended; waiting for the tablet to reconnect (" +
                (ContainsHan(suffix) ? "connection details recorded" : suffix.Replace('）', ')'));
        }
        return "Native-client state updated; open Diagnostics & logs for details.";
    }

    private static string? TranslateKnownRuntime(string value)
    {
        if (value == "正在实测本机 H.264 编码器；本次不会创建额外虚拟屏。")
            return "Measuring the available local H.264 encoders; this does not create another virtual display.";
        if (value == "Windows 桌面切换，USB 连接保持，等待画面恢复。")
            return "The Windows desktop changed; the USB connection remains active while video recovers.";
        if (value == "USB 副屏画面已恢复。") return "USB second-screen video recovered.";
        if (value == "副屏正在使用，已暂停正式版安装包下载；连接结束后自动继续。")
            return "Stable-update download is paused while the second screen is active and will resume after disconnection.";
        if (value.StartsWith("编码器检测未绑定显示 GPU：", StringComparison.Ordinal))
            return "Encoder detection was not bound to the display GPU; available backends will be measured.";
        if (value.StartsWith("编码器检测无法读取显示 GPU，将按可用后端实测：", StringComparison.Ordinal))
            return "The display GPU could not be read; available encoder backends will be measured.";
        const string selectedPrefix="已选择 ";
        if (value.StartsWith(selectedPrefix, StringComparison.Ordinal))
        {
            var separator=value.IndexOf('：',selectedPrefix.Length);
            if(separator>selectedPrefix.Length)
            {
                var backend=TranslateEncoderBackendName(value[selectedPrefix.Length..separator],ProductLanguage.English);
                var remainder=value[(separator+1)..];
                var fpsEnd=remainder.IndexOf(" fps",StringComparison.Ordinal);
                var details=fpsEnd>=0?remainder[..(fpsEnd+4)]:(ContainsHan(remainder)?ExtractAsciiMeasurement(remainder):remainder);
                return $"Selected {backend}"+(string.IsNullOrWhiteSpace(details)?".":": "+details);
            }
        }
        if (value.StartsWith("画质计划已更新：", StringComparison.Ordinal))
        {
            var marker=value.IndexOf(" Mbps",StringComparison.Ordinal);
            var rate=marker>"画质计划已更新：".Length
                ?value["画质计划已更新：".Length..(marker+5)]
                :"the requested bit rate";
            return $"The quality plan was updated to {rate}; the same connection and display remain active while the encoder is rebuilt.";
        }
        if (value.StartsWith("自动更新已安全阻止，旧缓存不会被复用：", StringComparison.Ordinal))
            return "Automatic update was safely blocked; the old cache will not be reused. See Diagnostics & logs for details.";
        if (value.StartsWith("自动更新检查暂不可用：", StringComparison.Ordinal))
            return "Automatic update checking is temporarily unavailable. See Diagnostics & logs for details.";
        if (value.StartsWith("自动安装暂未启动：", StringComparison.Ordinal))
            return "Automatic installation has not started. See Diagnostics & logs for details.";
        if (value.StartsWith("软件编码器优先级无法降低：", StringComparison.Ordinal))
            return "The software encoder priority could not be lowered; streaming continues with the current process priority.";
        if (value.StartsWith("H.264 捕获后端：", StringComparison.Ordinal))
        {
            const string encoderMarker="；编码器：";
            var marker=value.IndexOf(encoderMarker,StringComparison.Ordinal);
            if(marker>0)
            {
                var capture=value["H.264 捕获后端：".Length..marker];
                var encoder=value[(marker+encoderMarker.Length)..];
                var fallback=encoder.IndexOf('；');
                if(fallback>=0)encoder=encoder[..fallback];
                if(!ContainsHan(capture)&&!ContainsHan(encoder))
                    return $"H.264 capture backend: {capture}; encoder: {encoder}";
            }
            return "The H.264 capture backend and encoder were selected; see Diagnostics & logs for details.";
        }
        if (value.StartsWith("正式版 ", StringComparison.Ordinal))
        {
            const string marker=" 已准备完成；";
            var end=value.IndexOf(marker,StringComparison.Ordinal);
            if(end>4)return $"Stable release {value[4..end]} is ready and will install after TabLink exits, followed by startup verification.";
        }
        if (value.StartsWith("诊断记录暂时无法保存", StringComparison.Ordinal))
        {
            var open=value.IndexOf('（');
            var close=open>=0?value.IndexOf('）',open+1):-1;
            var name=open>=0&&close>open?value[(open+1)..close]:null;
            return name is null
                ?"A diagnostic record could not be saved temporarily; the display connection continues."
                :$"Diagnostic record {name} could not be saved temporarily; the display connection continues.";
        }
        return null;
    }

    private static string ExtractAsciiMeasurement(string value)
    {
        var characters=value.Where(character=>character<128||character is '×' or '@' or '·').ToArray();
        return new string(characters).Trim(' ','.',';',':','，','。','；');
    }

    internal static string TranslateNativeSessionLog(string value, ProductLanguage language)
    {
        if (language == ProductLanguage.SimplifiedChinese) return value;
        if (value.StartsWith("设备端口 ", StringComparison.Ordinal))
        {
            var separator = value.IndexOf('：');
            if (separator > 5)
                return "Device port " + value[5..separator] + ": " + TranslateNativeSessionState(value[(separator + 1)..], language);
        }
        if (value.StartsWith("设备 ", StringComparison.Ordinal))
        {
            var portEnd = value.IndexOf(' ', 3);
            if (portEnd > 3)
            {
                var port = value[3..portEnd];
                var detail = value[(portEnd + 1)..];
                foreach (var (zh, en) in NativeDeviceLogPrefixes)
                    if (detail.StartsWith(zh, StringComparison.Ordinal))
                    {
                        var suffix = detail[zh.Length..];
                        return $"Device {port} {en}" + (ContainsHan(suffix)
                            ? "Details are available in Diagnostics & logs."
                            : suffix);
                    }
            }
        }
        return TranslateRuntime(value, language);
    }

    internal static string TranslateUsbRecoveryDetail(string value, ProductLanguage language)
    {
        if (language == ProductLanguage.SimplifiedChinese) return value;
        var translated = Translate(value, language);
        return ContainsHan(translated)
            ? "Recovery did not complete safely; open Diagnostics & logs for the recorded reason."
            : translated;
    }

    internal static string TranslateHealthDetail(string value, ProductLanguage language)
    {
        if(language==ProductLanguage.SimplifiedChinese)return value;
        var translated=Translate(value,language);
        if(!ContainsHan(translated))return translated;
        var known=TranslateKnownRuntime(value);
        if(known is not null)return known;
        foreach(var (zh,en) in HealthDetails)
            if(string.Equals(value,zh,StringComparison.Ordinal))return en;
        const string sentPrefix="本次连接已发送 ", sentSuffix=" 帧";
        if(value.StartsWith(sentPrefix,StringComparison.Ordinal)&&value.EndsWith(sentSuffix,StringComparison.Ordinal))
            return "This connection has sent "+value[sentPrefix.Length..^sentSuffix.Length]+" frames";
        const string submittedPrefix="客户端已提交 ", submittedSuffix=" 帧到解码器";
        if(value.StartsWith(submittedPrefix,StringComparison.Ordinal)&&value.EndsWith(submittedSuffix,StringComparison.Ordinal))
            return "The client submitted "+value[submittedPrefix.Length..^submittedSuffix.Length]+" frames to the decoder";
        const string presentedPrefix="收到 Surface 呈现回调 ", presentedSuffix=" 帧";
        if(value.StartsWith(presentedPrefix,StringComparison.Ordinal)&&value.EndsWith(presentedSuffix,StringComparison.Ordinal))
            return "Received Surface presentation callbacks for "+value[presentedPrefix.Length..^presentedSuffix.Length]+" frames";
        const string browserPresentedPrefix="浏览器已确认呈现 ", browserPresentedSuffix=" 帧";
        if(value.StartsWith(browserPresentedPrefix,StringComparison.Ordinal)&&value.EndsWith(browserPresentedSuffix,StringComparison.Ordinal))
            return "The browser confirmed presentation of "+value[browserPresentedPrefix.Length..^browserPresentedSuffix.Length]+" frames";
        const string nativeRoutePrefix="选择原生客户端线路 ";
        if(value.StartsWith(nativeRoutePrefix,StringComparison.Ordinal))
            return "Selected native-client route "+SafeTechnicalSuffix(value[nativeRoutePrefix.Length..]);
        const string browserRoutePrefix="选择浏览器线路 ";
        if(value.StartsWith(browserRoutePrefix,StringComparison.Ordinal))
            return "Selected browser route "+SafeTechnicalSuffix(value[browserRoutePrefix.Length..]);
        const string routePrefix="选择线路 ";
        if(value.StartsWith(routePrefix,StringComparison.Ordinal))
            return "Selected route "+SafeTechnicalSuffix(value[routePrefix.Length..]);
        const string tlsSuffix=" · TLS 监听已启动";
        if(value.EndsWith(tlsSuffix,StringComparison.Ordinal))
            return SafeTechnicalSuffix(value[..^tlsSuffix.Length])+" · TLS listener started";
        const string browserListenerSuffix=" · 本地 HTTPS/WebRTC 已启动";
        if(value.EndsWith(browserListenerSuffix,StringComparison.Ordinal))
            return SafeTechnicalSuffix(value[..^browserListenerSuffix.Length])+" · local HTTPS/WebRTC started";
        if(value.StartsWith("收回并卸载本次拥有的虚拟副屏失败：",StringComparison.Ordinal))
            return "Failed to reclaim and remove the virtual display owned by this session. See Diagnostics & logs for details.";
        return TranslateRuntime(value,language);
    }

    internal static string TranslateEncoderBackendName(string value, ProductLanguage language) =>
        language==ProductLanguage.English&&string.Equals(value,"软件 x264",StringComparison.Ordinal)
            ?"Software x264"
            :value;

    internal static string TranslateEncoderDowngradeReason(string value, ProductLanguage language)
    {
        if(language==ProductLanguage.SimplifiedChinese||!ContainsHan(value))return value;
        const string requestedPrefix="设备请求 ";
        const string softwareSuffix=" fps，软件兼容模式最高 30 fps";
        if(value.StartsWith(requestedPrefix,StringComparison.Ordinal)&&value.EndsWith(softwareSuffix,StringComparison.Ordinal))
            return $"Device requested {value[requestedPrefix.Length..^softwareSuffix.Length]} fps; software compatibility is limited to 30 fps";
        const string safeLimitMarker=" fps，当前编码路径安全上限为 ";
        const string fpsSuffix=" fps";
        if(value.StartsWith(requestedPrefix,StringComparison.Ordinal)&&value.EndsWith(fpsSuffix,StringComparison.Ordinal))
        {
            var marker=value.IndexOf(safeLimitMarker,requestedPrefix.Length,StringComparison.Ordinal);
            if(marker>requestedPrefix.Length)
            {
                var requested=value[requestedPrefix.Length..marker];
                var effective=value[(marker+safeLimitMarker.Length)..^fpsSuffix.Length];
                return $"Device requested {requested} fps; the current encoding path is safely limited to {effective} fps";
            }
        }
        const string currentPrefix="当前编码 ";
        if(value.StartsWith(currentPrefix,StringComparison.Ordinal))
        {
            var separator=value.IndexOf('；',currentPrefix.Length);
            var mode=separator<0?value[currentPrefix.Length..]:value[currentPrefix.Length..separator];
            var result="Current encoding "+mode;
            if(separator>=0)
            {
                var reason=value[(separator+1)..];
                result+="; "+TranslateQualityReason(reason);
            }
            return ContainsHan(result)?"The encoder is running below the requested display profile.":result;
        }
        return "The encoder is running below the requested display profile.";
    }

    private static string TranslateQualityReason(string value) => value switch
    {
        "软件兼容模式最高 30 fps"=>"software compatibility is limited to 30 fps",
        "连接初始画质"=>"initial connection quality",
        "连续 3 个窗口出现传输或接收压力，降低一档码率"=>"transport or receiver pressure persisted for three windows; bitrate was reduced one level",
        "连续约 30 秒稳定，尝试恢复一档码率"=>"the connection was stable for about 30 seconds; bitrate recovery was attempted",
        _=>ContainsHan(value)?"adaptive quality policy":value
    };

    private static string SafeTechnicalSuffix(string value)
    {
        if(!ContainsHan(value))return value;
        var marker=value.LastIndexOf(" · ",StringComparison.Ordinal);
        if(marker>=0&&!ContainsHan(value[(marker+3)..]))return "selected interface · "+value[(marker+3)..];
        var colon=value.LastIndexOf(':');
        if(colon>0)
        {
            var start=colon-1;
            while(start>=0&&(char.IsDigit(value[start])||value[start] is '.' or ':'))start--;
            var address=value[(start+1)..];
            if(!ContainsHan(address))return "selected interface · "+address;
        }
        return "selected interface";
    }

    private static readonly (string Zh, string En)[] NativeDeviceLogPrefixes =
    [
        ("副屏准备失败：", "display preparation failed: "),
        ("画面输入清理需要检查：", "Video-input cleanup needs attention: "),
        ("电源请求清理需要检查：", "Power-request cleanup needs attention: ")
    ];

    private static readonly (string Zh,string En)[] HealthDetails =
    [
        ("客户端已断开，监听仍在等待重新认证","The client disconnected; the listener is still waiting for reauthentication"),
        ("客户端已重新连接，等待本次连接的新证据","The client reconnected; waiting for fresh evidence from this connection"),
        ("安全桌面或采集恢复期间保留连接","The connection is retained while the secure desktop or capture recovers"),
        ("采集已恢复，等待新的发送、解码与呈现证据","Capture recovered; waiting for new send, decode and presentation evidence"),
        ("最近 5 秒没有新的解码提交或呈现回调证据","No new decode-submission or presentation-callback evidence arrived in the last five seconds"),
        ("最近 5 秒没有新的客户端呈现回调证据","No new client presentation-callback evidence arrived in the last five seconds"),
        ("正在按原生客户端报告的模式准备唯一虚拟副屏","Preparing the single virtual display using the mode reported by the native client"),
        ("正在启动原生客户端的视频流水线","Starting the native-client video pipeline"),
        ("原生客户端连接已停止并回收本次副屏","The native-client connection stopped and its display was reclaimed"),
        ("等待原生客户端扫描当前二维码并完成 TLS 与令牌认证","Waiting for the native client to scan the current QR code and complete TLS and token authentication"),
        ("所有连接已停止并回收本次副屏","All connections stopped and this session's display was reclaimed"),
        ("正在按浏览器请求模式准备唯一虚拟副屏","Preparing the single virtual display using the browser-requested mode"),
        ("正在启动浏览器兼容的视频流水线","Starting the browser-compatible video pipeline"),
        ("等待浏览器使用当前单次二维码完成认证与屏幕参数上报","Waiting for the browser to authenticate with the current one-time QR code and report its display profile"),
        ("浏览器已断开；本次拥有的副屏仍在等待精确回收","The browser disconnected; the display owned by this session is still awaiting exact reclamation"),
        ("浏览器已断开，等待重新配对","The browser disconnected; waiting for a new pairing"),
        ("浏览器画面正在恢复","Browser video is recovering"),
        ("浏览器媒体通道已建立，等待页面呈现确认","The browser media channel is established; waiting for page-presentation confirmation"),
        ("最近 5 秒没有新的浏览器呈现回调证据","No new browser presentation-callback evidence arrived in the last five seconds"),
        ("Windows 暂时切换桌面，等待普通桌面恢复。","Windows temporarily changed desktops; waiting for the regular desktop to recover."),
        ("画面正在恢复。","Video is recovering."),
        ("正在建立加密的浏览器副屏连接。","Establishing the encrypted browser display connection."),
        ("正在为此设备准备独立副屏。","Preparing an independent display for this device."),
        ("已连接，等待浏览器呈现画面。","Connected; waiting for the browser to present video."),
        ("浏览器正在呈现独立副屏。","The browser is presenting the independent display."),
        ("浏览器副屏已断开。","The browser display disconnected."),
        ("浏览器接入已关闭并回收本次副屏","Browser access was disabled and this session's display was reclaimed"),
        ("正在按设备报告的模式准备唯一虚拟副屏","Preparing the single virtual display using the mode reported by the device"),
        ("正在启动桌面捕获与 H.264 编码器","Starting desktop capture and the H.264 encoder"),
        ("等待已信任设备签名认证，或扫描二维码登记新设备","Waiting for signed authentication from a trusted device or a QR scan to register a new device"),
        ("等待客户端扫描二维码并完成一次性注册","Waiting for the client to scan the QR code and complete one-time registration"),
        ("等待已信任设备完成签名认证","Waiting for a trusted device to complete signed authentication"),
        ("手动选择了一台已列出的 USB 设备","A listed USB device was selected manually"),
        ("USB 身份与 ADB 授权已核验，准备本机反向通道","USB identity and ADB authorisation were verified; preparing the local reverse channel"),
        ("正在从明确选中的客户端读取屏幕参数","Reading display parameters from the explicitly selected client"),
        ("本次副屏已精确回收，等待浏览器重新配对","This session's display was reclaimed exactly; waiting for the browser to pair again"),
        ("本次拥有的虚拟副屏已精确回收","The virtual display owned by this session was reclaimed exactly"),
        ("连接已停止","The connection stopped"),
        ("连接已停止并回收本次副屏","The connection stopped and this session's display was reclaimed")
    ];

    private static readonly (string Zh, string En)[] Pairs =
    [
        ("让手机、平板成为电脑的独立扩展桌面", "Turn a phone or tablet into an independent extended desktop"),
        ("语言", "Language"), ("跟随系统", "Follow system"),
        ("首次运行跟随 Windows 显示语言（中文系统使用简体中文，其他系统使用 English）。选择后立即保存并应用。",
            "On first run TabLink follows the Windows display language (zh-* uses Simplified Chinese; all others use English). Your selection is saved and applied immediately."),
        ("连接副屏", "Connect display"), ("设置", "Settings"), ("检测与日志", "Diagnostics & logs"),
        ("连接方式", "Connection method"), ("画质", "Quality"), ("编码", "Encoder"),
        ("USB 平板", "USB tablet"), ("刷新设备", "Refresh devices"),
        ("连接选中的平板", "Connect selected tablet"), ("停止连接", "Stop connection"),
        ("安装安卓客户端", "Install Android client"), ("选择 adb.exe", "Choose adb.exe"),
        ("允许平板触控操作副屏", "Allow touch control from the tablet"),
        ("尚未连接", "Not connected"), ("按需启用唯一一块虚拟副屏", "One virtual display is enabled only when needed"),
        ("正在定位 Android 平台工具…", "Locating Android Platform-Tools…"),
        ("设备序列号（可单独填写）", "Device serial (may be entered alone)"),
        ("VID，如 19D2", "VID, e.g. 19D2"), ("PID，如 0246", "PID, e.g. 0246"),
        ("备注，如 随身 Wi-Fi", "Note, e.g. portable Wi-Fi"),
        ("加入排除列表", "Add exclusion"), ("移除选中规则", "Remove selected rule"),
        ("允许软件回退（最高 30 fps）", "Allow software fallback (up to 30 fps)"),
        ("设备保护", "Device protection"), ("连接记录", "Connection log"),
        ("设备序列号", "Device serial"), ("备注名称", "Description"),
        ("连接时自动安装唯一副屏，断开时自动卸载。APK 会读取设备方向、原生分辨率和支持的刷新率，并显示 H.264 实际接收帧率。",
            "TabLink installs the single virtual display when connecting and removes it when disconnecting. The APK reports orientation, native resolution and supported refresh rates, and shows the actual H.264 receive rate."),
        ("序列号或 VID/PID 任一匹配都会阻止 USB 连接与安装 APK。已默认保护你的 F50 Pro。\n新增规则命中正在使用的 USB 设备时，会先停止该连接。",
            "A matching serial number or VID/PID blocks USB connection and APK installation. Your F50 Pro is protected by default.\nIf a new rule matches the active USB device, TabLink stops that connection first."),
        ("显示底部作者信息", "Show author details in the footer"), ("显示底部信息", "Show footer details"), ("保存并应用", "Save and apply"),
        ("恢复默认", "Restore defaults"), ("底部作者信息", "Footer author details"), ("底部信息", "Footer details"),
        ("底部提示文字", "Footer summary"), ("作者文字", "Author text"), ("GitHub 显示文字", "GitHub label"), ("GitHub 地址", "GitHub URL"),
        ("博客显示文字", "Blog label"), ("博客地址", "Blog URL"),
        ("作者信息默认显示在主窗口底部。保存后立即生效，不会停止当前副屏；链接只接受完整的 HTTPS 地址。",
            "Author details appear in the main-window footer by default. Changes apply immediately without stopping the display; links must use complete HTTPS URLs."),
        ("底部提示和作者信息默认显示。可以整体关闭或自定义；保存后立即生效且不会停止当前副屏，链接只接受完整的 HTTPS 地址。",
            "The footer summary and author details are shown by default. You can hide or customise the whole footer; changes apply immediately without stopping the display, and links must use complete HTTPS URLs."),
        ("只有一块副屏，点 × 后在托盘继续运行", "One second screen only; selecting × keeps TabLink running in the tray"),
        ("帮助", "Help"),
        ("作者：张林杰（Jey / @linjierd）", "Author: Zhang Linjie (Jey / @linjierd)"),
        ("博客：linjie.space", "Blog: linjie.space"),
        ("软件更新", "Software updates"), ("自动更新（推荐）", "Automatic updates (recommended)"),
        ("自动下载后手动安装", "Download automatically, install manually"), ("从不更新", "Never update"),
        ("重启并安装已下载更新", "Restart and install downloaded update"),
        ("选择 TabLink 如何获取并安装已签名的正式版更新。更改后立即保存并生效。",
            "Choose how TabLink obtains and installs signed stable releases. Changes are saved and applied immediately."),
        ("后台检查并下载；没有副屏连接且电脑空闲时自动重启安装。",
            "Check and download in the background; restart and install automatically when no display is connected and the computer is idle."),
        ("后台检查并下载；只有点击设置页或托盘中的安装按钮后才会重启安装。",
            "Check and download in the background; restart and install only after you select Install in Settings or the tray."),
        ("不检查、不下载，也不会在退出 TabLink 时安装已缓存的更新。",
            "Do not check or download, and do not install a cached update when TabLink exits."),
        ("刷新线路", "Refresh routes"), ("开始配对", "Start pairing"), ("可信设备", "Trusted devices"),
        ("复制连接链接", "Copy connection link"), ("打开 APK 所在文件夹", "Open APK folder"),
        ("选择线路后点击“开始配对”。首次扫码登记可信设备；以后可自动发现并重连。",
            "Select a route and choose Start pairing. The first scan registers a trusted device; later connections can be discovered and restored automatically."),
        ("电脑和平板连接同一局域网；数据线连接时，请开启平板 USB 网络共享。\n打开 TabLink 客户端扫码连接，无需开发者模式。",
            "Connect both devices to the same local network; for a cable, enable USB tethering.\nScan with the TabLink client. Developer options are not required."),
        ("USB 网络共享可能改变上网线路；TabLink 不修改路由或 DNS。\n认证后才安装唯一虚拟屏；断开时卸载并清理规则。\n帧率取决于信号和性能；客户端请求原生分辨率与刷新率。",
            "USB tethering may change internet access; TabLink does not change routing or DNS.\nThe single virtual display is installed after authentication and removed when disconnected.\nFrame rate depends on signal and performance; the client requests native size and refresh rate."),
        ("刷新线路", "Refresh routes"), ("开启浏览器接入", "Enable browser connection"),
        ("重新生成配对二维码", "Generate a new pairing QR code"), ("关闭浏览器接入", "Disable browser connection"),
        ("停止选中浏览器", "Stop selected browser"), ("导出本机证书", "Export this computer's certificate"),
        ("复制接入链接", "Copy connection link"), ("选择当前可用线路，再开启浏览器接入。", "Select an available route, then enable browser access."),
        ("网络线路", "Network route"),
        ("检测连接", "Run diagnostics"), ("修复：使用内置 ADB", "Repair: use bundled ADB"),
        ("重建连接", "Rebuild connection"), ("修复所选问题", "Repair selected issue"),
        ("日志目录", "Logs folder"), ("导出脱敏支持包", "Export redacted support bundle"),
        ("读取设备请求模式", "Read device-requested modes"), ("配置选中显示模式", "Configure selected display mode"),
        ("显示模式修复只处理设备上报的模式。", "Display-mode repair only uses modes reported by the device."),
        ("选择一个连接阶段可查看完整证据与修复建议。", "Select a connection stage to view its evidence and suggested repair."),
        ("按真实事件检查线路、认证、副屏、发送、客户端解码提交和呈现回调；呈现回调仍不等于物理面板测量。",
            "Inspect real events for routing, authentication, display, sending, client decode submission and presentation callbacks. A callback is still not a physical-panel measurement."),
        ("连接阶段", "Connection stage"), ("状态", "State"), ("当前证据与建议", "Current evidence and guidance"),
        ("添加原生客户端连接", "Add native-client connection"), ("停止选中设备", "Stop selected device"),
        ("重连选中设备", "Reconnect selected device"), ("停止当前设备", "Stop current device"),
        ("原生客户端", "Native client"),
        ("只允许一台原生客户端使用唯一的扩展屏。先在 Wi-Fi / USB 免调试页选择线路，再添加设备。\n设备认证并上报屏幕参数后才安装虚拟屏；停止连接后自动卸载。",
            "Only one native client can use the single extended display. Select a route on the Wi-Fi / USB page, then add a device.\nThe virtual display is installed only after authentication and profile reporting, and is removed when the connection stops."),
        ("打开主窗口", "Open main window"), ("退出 TabLink", "Exit TabLink"),
        ("正在检查正式版更新…", "Checking for stable updates…"), ("停止连接", "Stop connection"),
        ("浏览器接入尚未开启", "Browser connection is not enabled"),
        ("等待安卓 USB 调试设备", "Waiting for an Android USB debugging device"),
        ("本地 HTTPS + WebRTC · 单设备", "Local HTTPS + WebRTC · one device"),
        ("兼容连接 · 按需启用唯一副屏", "Compatibility connection · one display enabled when needed"),
        ("本地加密连接 · 无需 USB 调试", "Encrypted local connection · no USB debugging"),
        ("选择 Wi-Fi 或 USB 网络共享线路，开始配对", "Select a Wi-Fi or USB-tethering route to begin pairing"),
        ("尚无可用线路：请连接 Wi-Fi 或开启平板 USB 网络共享", "No route is available. Connect to Wi-Fi or enable USB tethering on the tablet."),
        ("浏览器接入已开启，等待设备扫码", "Browser connection is ready; waiting for a device to scan"),
        ("浏览器副屏正在传输", "Browser display is streaming"),
        ("副屏服务运行中", "Second-screen service is running"),
        ("平板已连接，正在传输副屏", "Tablet connected; streaming the second screen"),
        ("画面采集正在恢复，连接保留", "Video capture is recovering; the connection remains active"),
        ("需要处理连接条件", "Connection needs attention"),
        ("更新检查已关闭。", "Update checks are disabled."),
        ("将自动检查和下载正式版；安装前会等待你的明确操作。", "TabLink checks for and downloads stable releases automatically, then waits for you to install."),
        ("将自动检查、下载并在空闲时安装正式版。", "TabLink checks for, downloads and installs stable releases while idle."),
        ("没有待安装更新", "No update is ready to install"),
        ("已保存并应用。", "Saved and applied."), ("有尚未保存的更改。", "There are unsaved changes."),
        ("当前没有活动连接；开始连接后将显示六阶段进度。", "There is no active connection. Six-stage progress appears after a connection starts."),
        ("未发现 ADB 平板：请确认 USB 调试已开启并在平板允许此电脑", "No ADB tablet found: enable USB debugging and allow this computer on the tablet"),
        ("当前没有虚拟副屏 · 连接时自动安装", "No virtual display is present · installed automatically when connecting"),
        ("尚无活动虚拟副屏", "No active virtual display"),
        ("已读取设备上报的请求模式；只会配置选中模式。", "Device-requested modes loaded. Only the selected mode will be configured."),
        ("尚未收到设备屏幕参数；请先完成一次认证或连接。", "No display profile has been received. Authenticate or connect a device first."),
        ("正在启动", "Starting"), ("等待扫码配对（5 分钟有效）", "Waiting for QR pairing (valid for five minutes)"),
        ("已配对，等待显示首帧", "Paired; waiting for the first displayed frame"),
        ("线路已断开或被排除", "The route disconnected or was excluded"), ("配对已超时", "Pairing expired"),
        ("设备超过首帧或后续呈现期限", "The device exceeded its first-frame or presentation deadline"),
        ("画面暂停，连接保留", "Video paused; connection retained"), ("等待设备重连", "Waiting for the device to reconnect"),
        ("等待画面", "Waiting for video"), ("连接启动已停止，仍有资源需要重试清理", "Connection startup stopped; resource cleanup must be retried"),
        ("本次拥有的副屏已精确回收", "This session's display was reclaimed exactly"),
        ("平板已连接，正在传输副屏", "Tablet connected; streaming the second screen"),
        ("恢复预算已经用完。", "The recovery time budget was exhausted."), ("平板已经重新连接。", "The tablet has reconnected."),
        ("本会话 USB 端点已指向其他映射，未替换或删除它。", "This session's USB endpoint points to another mapping; it was neither replaced nor removed."),
        ("USB 通道恢复后，平板已自动重连。", "The tablet reconnected automatically after USB-channel recovery."),
        ("已用原会话令牌启动客户端并恢复连接。", "The client was started with the existing session token and the connection recovered."),
        ("USB 通道已检查，但平板尚未重新完成认证连接。", "The USB channel was checked, but the tablet has not completed authenticated reconnection."),
        ("本次 USB 恢复达到时间预算。", "This USB-recovery attempt reached its time budget."),
        ("尚未配置 Android 平台工具，请先选择 adb.exe。", "Android Platform-Tools are not configured. Select adb.exe first."),
        ("排除规则已添加并保存。", "The exclusion rule was added and saved."),
        ("配置", "Configuration"), ("通过", "Passed"), ("需要处理", "Needs attention"),
        ("信息", "Information"), ("未完成", "Incomplete"), ("检测", "Diagnostics"),
        ("内置 ADB", "Bundled ADB"), ("ADB 组件", "ADB component"), ("ADB 选择", "ADB selection"),
        ("ADB 执行", "ADB execution"), ("安卓调试设备", "Android debugging devices"),
        ("网络线路", "Network routes"), ("虚拟副屏", "Virtual display"), ("H.264 编码器", "H.264 encoder"),
        ("安卓客户端", "Android client"), ("USB 冲突", "USB conflict"), ("主连接", "Primary connection")
    ];
}
