namespace TabLink.DriverSetup;

// This is the helper's existing process-wide operation mutex, extracted so its
// bounded wait and abandoned-owner behavior can be tested without running any
// driver command. It is separate from the full-session lifecycle mutex. The
// helper process itself owns it for every command, including *-held calls.
internal static class DriverOperationLock
{
    internal const string MutexName = @"Global\TabLink.DriverSetup.25.7.23";

    internal static T Run<T>(Func<T> action) =>
        Run(MutexName, TimeSpan.FromMinutes(2), action);

    internal static T Run<T>(string mutexName, TimeSpan timeout, Func<T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("等待虚拟显示驱动操作完成超时；未启动并发安装或卸载。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
