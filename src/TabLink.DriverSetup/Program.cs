using System.Text.Json;
using System.Globalization;

namespace TabLink.DriverSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.LastOrDefault() == "--quiet";
        if (quiet) args = args[..^1];
        var command = args.FirstOrDefault();
        HeldDriverCommandInvocation? held = null;
        try
        {
            if (command == "--prepare-single-display-held") held = HeldDriverCommandInvocation.Parse(args, 3);
            if (command == "--remove-session-display-held") held = HeldDriverCommandInvocation.Parse(args, 0);
        }
        catch (ArgumentException ex)
        {
            if (!quiet) MessageBox.Show(ex.Message, "TabLink 驱动管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
        // There is deliberately no auto-install entry point, service, download, or
        // certificate-store change. Every operation requires an explicit argument.
        var profile = args.Length == 4 && args[0] == "--configure-display";
        var single = args.Length == 4 && args[0] == "--prepare-single-display" ||
            held?.Command == "--prepare-single-display-held";
        var removeSingle = args.Length == 1 && args[0] == "--remove-session-display" ||
            held?.Command == "--remove-session-display-held";
        var pool = args.Length == 2 && args[0] == "--configure-pool";
        var tablet = args.Length == 3 && args[0] == "--prepare-tablet-only";
        var control = args.Length == 1 && args[0] is "--install" or "--uninstall" or "--collect-idle-pool";
        if (!profile && !single && !removeSingle && !pool && !tablet && !control)
        {
            if (!quiet) MessageBox.Show("请从 TabLink 启动此程序。可用操作：--install、--prepare-tablet-only <child-id> <parent-id>、--prepare-single-display 宽度 高度 刷新率、--remove-session-display，以及显式单屏维护命令。", "TabLink 驱动管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        InstallResult result;
        try
        {
            InstallResult Dispatch() => args[0] switch
            {
                "--install" => DriverInstaller.InstallStandalone(),
                "--prepare-tablet-only" => TabletPreparer.Prepare(args[1], args[2]),
                "--prepare-single-display" => DriverInstaller.PrepareSingleDisplay(ParseNumber(args[1]), ParseNumber(args[2]), ParseNumber(args[3])),
                "--prepare-single-display-held" => DriverInstaller.PrepareSingleDisplayWithCallerLease(
                    ParseNumber(held!.CommandArguments[0]), ParseNumber(held.CommandArguments[1]),
                    ParseNumber(held.CommandArguments[2]), held.Credentials),
                "--remove-session-display" => DriverInstaller.RemoveSessionDisplay(),
                "--remove-session-display-held" => DriverInstaller.RemoveSessionDisplayWithCallerLease(held!.Credentials),
                "--configure-display" => DriverInstaller.ConfigureDisplay(ParseNumber(args[1]), ParseNumber(args[2]), ParseNumber(args[3])),
                "--configure-pool" => DriverInstaller.ConfigurePool(ParseNumber(args[1])),
                "--collect-idle-pool" => DriverInstaller.CollectOwnedIdlePool(),
                _ => DriverInstaller.Control(args[0])
            };
            // Display mutations acquire lifecycle -> operation -> protected
            // display-lease lock inside their boundary. Wrapping those in the
            // operation mutex here would invert the host's lifecycle -> helper
            // order and can deadlock a concurrent maintenance command.
            result = DisplayMutationBoundary.IsDisplayMutationCommand(args[0])
                ? Dispatch()
                : DriverOperationLock.Run(Dispatch);
        }
        catch (Exception ex)
        {
            result = new(false, "failed", ex.Message, null, false, DateTimeOffset.UtcNow);
        }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, args[0] switch { "--prepare-tablet-only" => "tablet-prepare-result.json", "--prepare-single-display" or "--prepare-single-display-held" => "single-display-prepare-result.json", "--remove-session-display" or "--remove-session-display-held" => "single-display-remove-result.json", "--configure-display" => "display-configure-result.json", "--configure-pool" => "display-pool-result.json", "--collect-idle-pool" => "display-pool-collect-result.json", _ => "driver-install-result.json" });
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.Move(temporary, path, true);
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(result.Message + "\n\n无法保存安装结果：" + ex.Message, "TabLink 驱动安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        if (!quiet) MessageBox.Show(result.Message, "TabLink 驱动安装", MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return result.Success ? 0 : 1;
    }

    private static int ParseNumber(string value)
    {
        if (value.Length > 5 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            value != number.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("宽度、高度和刷新率必须为无符号十进制整数。");
        return number;
    }
}

internal sealed record InstallResult(bool Success, string State, string Message, string? InstanceId, bool RebootRequired, DateTimeOffset Timestamp);
