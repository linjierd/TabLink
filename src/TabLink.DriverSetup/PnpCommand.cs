using System.Diagnostics;

namespace TabLink.DriverSetup;

internal static class PnpCommand
{
    internal sealed record Result(int ExitCode, string Output)
    {
        internal bool RebootRequired => ExitCode == 3010;
    }

    internal static Result Run(string operation, string exactInstanceId)
    {
        if (operation is not ("/restart-device" or "/enable-device" or "/disable-device" or "/remove-device"))
            throw new ArgumentException("Unsupported device operation.", nameof(operation));
        if (string.IsNullOrWhiteSpace(exactInstanceId) || exactInstanceId.Contains('*') || exactInstanceId.Contains('?'))
            throw new ArgumentException("An exact device instance ID is required.", nameof(exactInstanceId));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(operation);
        info.ArgumentList.Add(exactInstanceId);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Windows 设备管理工具。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(); process.WaitForExit(5000); } catch { }
            throw new TimeoutException("Windows 设备操作超时（仅请求了指定设备）。");
        }
        var text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (process.ExitCode is not (0 or 3010))
            throw new InvalidOperationException($"Windows 设备操作失败（{process.ExitCode}）：{text.Trim()}");
        return new(process.ExitCode, text.Trim());
    }
}
