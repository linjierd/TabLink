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

/// <summary>
/// Establishes the only permitted lock order for a virtual-display mutation:
/// machine lifecycle mutex, helper operation mutex, then the protected display
/// lease file lock acquired by the command core. A *-held helper may skip the
/// first acquisition only after consuming a protected, one-time handoff from
/// the exact Windows host that already owns the lifecycle mutex.
/// </summary>
internal static class DisplayMutationBoundary
{
    internal const string LifecycleMutexName = DriverLifecycleHandoffProtocol.LifecycleMutexName;

    internal static bool IsDisplayMutationCommand(string command) => command is
        "--install" or "--uninstall" or
        "--prepare-single-display" or "--prepare-single-display-held" or
        "--remove-session-display" or "--remove-session-display-held" or
        "--configure-display" or "--configure-pool" or "--collect-idle-pool";

    internal static T RunStandalone<T>(Func<T> action) =>
        RunStandalone(LifecycleMutexName, DriverOperationLock.MutexName, TimeSpan.FromMinutes(2), action);

    // Isolated tests use unique Local mutex names. Production always uses the
    // fixed Global names above.
    internal static T RunStandalone<T>(string lifecycleMutexName,
        string operationMutexName, TimeSpan timeout, Func<T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleMutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationMutexName);
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var lifecycle = new Mutex(false, lifecycleMutexName);
        var acquired = false;
        try
        {
            try { acquired = lifecycle.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new TimeoutException("另一个 TabLink 进程仍持有唯一虚拟显示设备生命周期；未启动维护操作。");
            return DriverOperationLock.Run(operationMutexName, timeout, action);
        }
        finally { if (acquired) lifecycle.ReleaseMutex(); }
    }

    internal static T RunHeld<T>(DriverLifecycleHandoffCredentials credentials,
        string command, IReadOnlyList<string> canonicalArguments, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(action);
        return DriverOperationLock.Run(() =>
        {
            // The process handle returned here remains alive throughout the
            // display-lease check and the command's PnP/configuration mutation.
            using var handoff = DriverLifecycleHandoffRuntime.ConsumeProduction(
                credentials, command, canonicalArguments);
            return action();
        });
    }
}
