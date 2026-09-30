using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace TabLink.Windows;

// This process-level watcher starts before the UI can create a display. It
// closes the gaps before SessionGuard publishes its marker and after that
// marker has been retired but before the strict driver helper has returned.
internal static class DriverOwnerWatchdog
{
    private const string Command = "--watch-driver-owner";
    private const string ReadyPrefix = @"Local\TabLink.DriverOwnerWatchdog.Ready.";

    internal static void StartForCurrentProcess()
    {
        using var owner = Process.GetCurrentProcess();
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("无法定位 TabLink 主程序，未启动驱动生命周期保护。", executable);
        var startTicks = owner.StartTime.ToUniversalTime().Ticks;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var eventName = ReadyEventName(nonce);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName, out var created);
        if (!created) throw new IOException("驱动生命周期保护握手名称发生冲突。请重新启动 TabLink。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(Command);
        start.ArgumentList.Add(owner.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(nonce);
        using var watcher = Process.Start(start) ?? throw new IOException("无法启动独立的驱动生命周期保护进程。");
        if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
        {
            var detail = watcher.HasExited ? "保护进程已提前退出，代码 " + watcher.ExitCode.ToString(CultureInfo.InvariantCulture) : "保护进程未按时确认";
            throw new TimeoutException(detail + "；TabLink 未开始任何副屏连接。");
        }
        if (watcher.HasExited)
            throw new IOException("驱动生命周期保护进程在握手后提前退出；TabLink 未开始任何副屏连接。");
        // Disposing the local Process wrapper does not terminate the independent
        // child. It retains its own exact handle to this owner until UI exit.
    }

    internal static async Task<int> RunChildAsync(string[] args, Func<Task> removeAfterOwnerExit)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(removeAfterOwnerExit);
        if (args.Length != 4 || args[0] != Command)
            throw new ArgumentException("驱动生命周期保护参数无效。", nameof(args));
        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid) || ownerPid <= 0 ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerStartTicks) || ownerStartTicks <= 0)
            throw new ArgumentException("驱动生命周期保护的进程身份无效。", nameof(args));
        var nonce = args[3];
        var eventName = ReadyEventName(nonce);
        using var ready = EventWaitHandle.OpenExisting(eventName);
        return await WatchVerifiedOwnerAsync(ownerPid, ownerStartTicks,
            static pid => new SystemOwnerProcess(pid),
            () => ready.Set(), removeAfterOwnerExit).ConfigureAwait(false);
    }

    internal static async Task<int> WatchVerifiedOwnerAsync(int expectedPid, long expectedStartTicks,
        Func<int, IDriverOwnerProcess> openOwner, Action signalReady, Func<Task> removeAfterOwnerExit)
    {
        if (expectedPid <= 0 || expectedStartTicks <= 0) throw new ArgumentOutOfRangeException(nameof(expectedPid));
        ArgumentNullException.ThrowIfNull(openOwner);
        ArgumentNullException.ThrowIfNull(signalReady);
        ArgumentNullException.ThrowIfNull(removeAfterOwnerExit);
        using var owner = openOwner(expectedPid) ?? throw new IOException("无法打开 TabLink 所有者进程。");
        if (owner.Id != expectedPid || owner.StartTimeUtcTicks != expectedStartTicks)
            throw new IOException("TabLink 所有者 PID 或启动时间不匹配，保护进程未获得清理权限。");
        signalReady();
        await owner.WaitForExitAsync().ConfigureAwait(false);
        // The retained process handle identifies the same process even if its
        // numeric PID is subsequently reused. Cleanup itself remains limited
        // to the exact protected driver receipt.
        await removeAfterOwnerExit().ConfigureAwait(false);
        return 0;
    }

    private static string ReadyEventName(string nonce)
    {
        if (nonce.Length != 64 || nonce.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("驱动生命周期保护握手随机数无效。", nameof(nonce));
        return ReadyPrefix + nonce;
    }

    internal interface IDriverOwnerProcess : IDisposable
    {
        int Id { get; }
        long StartTimeUtcTicks { get; }
        Task WaitForExitAsync();
    }

    private sealed class SystemOwnerProcess : IDriverOwnerProcess
    {
        private readonly Process process;
        internal SystemOwnerProcess(int pid)
        {
            process = Process.GetProcessById(pid);
            Id = process.Id;
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        public int Id { get; }
        public long StartTimeUtcTicks { get; }
        public Task WaitForExitAsync() => process.WaitForExitAsync(CancellationToken.None);
        public void Dispose() => process.Dispose();
    }
}
