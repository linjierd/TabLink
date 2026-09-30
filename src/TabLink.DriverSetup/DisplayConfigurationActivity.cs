using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace TabLink.DriverSetup;

internal static class DisplayConfigurationActivity
{
    internal static IDisposable AcquireLeaseLock()
    {
        var mutex = new Mutex(false, @"Local\TabLink.DisplayLease." + Process.GetCurrentProcess().SessionId);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("副屏会话正在改变所有权，未配置或重启驱动。");
            return new HeldMutex(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    internal static void AssertIdle()
    {
        // Includes loopback ADB, native network slots, and the browser listener.
        // A port owned by another process is conservatively treated as in use.
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port is >= 27183 and <= 27192))
            throw new IOException("TabLink连接端口仍在监听，请停止全部USB、网络和浏览器连接后配置显示池。");
        AssertNoLiveDisplayLeases();
    }

    internal static void AssertNoLiveDisplayLeases()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
        var files = new List<string>();
        var legacy = Path.Combine(folder, "active-display-lease.json");
        if (File.Exists(legacy)) files.Add(legacy);
        var leases = Path.Combine(folder, "display-leases");
        if (Directory.Exists(leases))
            files.AddRange(Directory.GetFiles(leases, "*.json").Where(f => IsLeaseFileName(Path.GetFileName(f))));
        foreach (var file in files)
        {
            var marker = file + ".lease-id";
            if (!File.Exists(marker)) continue;
            var id = JsonSerializer.Deserialize<Guid>(File.ReadAllText(marker));
            if (id == Guid.Empty) continue;
            using var state = JsonDocument.Parse(File.ReadAllText(file));
            var root = state.RootElement;
            if (root.GetProperty("Lease").GetProperty("LeaseId").GetGuid() != id)
                throw new IOException("副屏租约与所有权标记不匹配，未重启驱动。");
            var pid = root.GetProperty("OwnerPid").GetInt32();
            var ticks = root.GetProperty("OwnerStartUtcTicks").GetInt64();
            if (pid <= 0 || ticks <= 0) throw new IOException("副屏租约进程身份无效，未重启驱动。");
            if (OwnerMayBeRunning(pid, ticks)) throw new IOException("仍有设备持有副屏租约，请停止全部连接后配置显示池。");
        }
    }

    internal static bool IsLeaseFileName(string name) => name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal) &&
        name.AsSpan(0, 64).ToArray().All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool OwnerMayBeRunning(int pid, long ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
