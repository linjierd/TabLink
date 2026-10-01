using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace TabLink.Windows;

/// <summary>
/// Establishes a fail-closed rendezvous between a display-session owner and
/// its independent watchdog. The event is only a readiness signal: the
/// protected lease files and the retained owner-process handle remain the
/// cleanup authority.
/// </summary>
internal static class DisplayWatcherHandshake
{
    internal const string Command = "--watch-display";
    internal static readonly TimeSpan MaximumReadyWait = TimeSpan.FromSeconds(10);
    private const string ReadyPrefix = @"Local\TabLink.DisplayWatchdog.Ready.";

    /// <summary>
    /// Adds the exact owner identity and a fresh 256-bit rendezvous nonce,
    /// starts the watchdog, then waits for verified loop entry. The child is
    /// deliberately not killed after a timeout: once the protected marker is
    /// published it must remain able to perform exact crash cleanup while the
    /// constructor rolls the session back.
    /// </summary>
    internal static void StartAndWait(ProcessStartInfo start, int ownerPid, long ownerStartUtcTicks,
        ParentEnvironment? environment = null, TimeSpan? readyWait = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        environment ??= ParentEnvironment.Local;
        var timeout = readyWait ?? MaximumReadyWait;
        if (timeout <= TimeSpan.Zero || timeout > MaximumReadyWait)
            throw new ArgumentOutOfRangeException(nameof(readyWait), "副屏保护握手等待时间必须在十秒以内。");
        if (ownerPid <= 0 || ownerStartUtcTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerPid), "副屏所有者进程身份无效。");
        ValidateBaseArguments(start);

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var eventName = ReadyEventName(nonce);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName, out var created);
        if (!created) throw new IOException("副屏保护握手名称发生冲突。请重新连接副屏。");

        start.ArgumentList.Add(ownerPid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(ownerStartUtcTicks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(nonce);

        using var watcher = environment.StartProcess(start)
            ?? throw new IOException("无法启动独立的副屏保护进程。");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (ReadExited(watcher))
                throw new IOException("副屏保护进程在确认受保护状态前退出，代码 " +
                    ReadExitCode(watcher).ToString(CultureInfo.InvariantCulture) + "。");
            var remaining = timeout - clock.Elapsed;
            var slice = remaining < environment.PollInterval ? remaining : environment.PollInterval;
            if (slice <= TimeSpan.Zero) break;
            if (!ready.WaitOne(slice)) continue;
            if (ReadExited(watcher))
                throw new IOException("副屏保护进程在就绪握手后提前退出。");
            return;
        }
        if (ReadExited(watcher))
            throw new IOException("副屏保护进程在确认受保护状态前退出，代码 " +
                ReadExitCode(watcher).ToString(CultureInfo.InvariantCulture) + "。");
        throw new TimeoutException("副屏保护进程未在十秒内确认受保护状态；连接已取消。");
    }

    internal static ChildRequest ParseChildArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 6 || !string.Equals(args[0], Command, StringComparison.Ordinal))
            throw new ArgumentException("副屏保护启动参数无效。", nameof(args));

        var currentPath = args[1];
        if (string.IsNullOrWhiteSpace(currentPath) || !Path.IsPathFullyQualified(currentPath))
            throw new ArgumentException("副屏保护文件路径无效。", nameof(args));
        try { currentPath = Path.GetFullPath(currentPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("副屏保护文件路径无效。", nameof(args)); }

        if (!Guid.TryParseExact(args[2], "D", out var leaseId) || leaseId == Guid.Empty)
            throw new ArgumentException("副屏保护会话标识无效。", nameof(args));
        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid) || ownerPid <= 0 ||
            !string.Equals(args[3], ownerPid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new ArgumentException("副屏所有者 PID 无效。", nameof(args));
        if (!long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerStartUtcTicks) ||
            ownerStartUtcTicks <= 0 ||
            !string.Equals(args[4], ownerStartUtcTicks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new ArgumentException("副屏所有者启动时间无效。", nameof(args));
        ValidateNonce(args[5]);
        return new ChildRequest(currentPath, leaseId, ownerPid, ownerStartUtcTicks, args[5]);
    }

    /// <summary>
    /// Opens and retains the exact owner-process handle before the watcher
    /// reads protected state. The returned context must live for the complete
    /// watcher loop, preventing PID reuse from changing the observed owner.
    /// </summary>
    internal static ChildContext OpenChild(ChildRequest request, ChildEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        environment ??= ChildEnvironment.Local;
        EventWaitHandle? ready = null;
        IRetainedOwnerProcess? owner = null;
        try
        {
            try { ready = EventWaitHandle.OpenExisting(ReadyEventName(request.Nonce)); }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            { throw new IOException("副屏保护握手事件不可用。"); }
            owner = environment.OpenOwner(request.OwnerPid)
                ?? throw new IOException("无法打开副屏所有者进程。");
            if (owner.Id != request.OwnerPid || owner.StartTimeUtcTicks != request.OwnerStartUtcTicks)
                throw new IOException("副屏所有者 PID 或启动时间不匹配。");
            return new ChildContext(request, ready, owner);
        }
        catch
        {
            owner?.Dispose();
            ready?.Dispose();
            throw;
        }
    }

    private static void ValidateBaseArguments(ProcessStartInfo start)
    {
        if (start.ArgumentList.Count != 3 ||
            !string.Equals(start.ArgumentList[0], Command, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(start.ArgumentList[1]) ||
            !Path.IsPathFullyQualified(start.ArgumentList[1]) ||
            !Guid.TryParseExact(start.ArgumentList[2], "D", out var leaseId) || leaseId == Guid.Empty)
            throw new ArgumentException("副屏保护启动参数无效。", nameof(start));
    }

    private static bool ReadExited(IStartedWatcherProcess watcher)
    {
        try { return watcher.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new IOException("无法确认副屏保护进程状态。"); }
    }

    private static int ReadExitCode(IStartedWatcherProcess watcher)
    {
        try { return watcher.ExitCode; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return -1; }
    }

    internal static string ReadyEventName(string nonce)
    {
        ValidateNonce(nonce);
        return ReadyPrefix + nonce;
    }

    private static void ValidateNonce(string nonce)
    {
        if (nonce is null || nonce.Length != 64 || nonce.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("副屏保护握手随机数无效。", nameof(nonce));
    }

    internal enum OwnerObservation { VerifiedRunning, Exited, Unverified }

    internal sealed class ChildRequest
    {
        internal ChildRequest(string currentPath, Guid leaseId, int ownerPid, long ownerStartUtcTicks, string nonce)
        {
            CurrentPath = currentPath;
            LeaseId = leaseId;
            OwnerPid = ownerPid;
            OwnerStartUtcTicks = ownerStartUtcTicks;
            Nonce = nonce;
        }
        internal string CurrentPath { get; }
        internal Guid LeaseId { get; }
        internal int OwnerPid { get; }
        internal long OwnerStartUtcTicks { get; }
        internal string Nonce { get; }
        public override string ToString() =>
            $"{Command} {CurrentPath} {LeaseId:D} {OwnerPid} {OwnerStartUtcTicks} [nonce-redacted]";
    }

    internal sealed class ChildContext : IDisposable
    {
        private readonly ChildRequest request;
        private readonly EventWaitHandle ready;
        private readonly IRetainedOwnerProcess owner;
        private bool protectedStateValidated;
        private bool readySignaled;
        private int disposed;

        internal ChildContext(ChildRequest request, EventWaitHandle ready, IRetainedOwnerProcess owner)
        {
            this.request = request;
            this.ready = ready;
            this.owner = owner;
        }

        /// <summary>
        /// Called only after the protected root, current file, marker and
        /// immutable bootstrap have all been verified and deserialized inside
        /// the first watcher-loop iteration.
        /// </summary>
        internal void ConfirmProtectedStateValidated(string currentPath, Guid leaseId,
            int ownerPid, long ownerStartUtcTicks)
        {
            ThrowIfDisposed();
            if (!string.Equals(Path.GetFullPath(currentPath), request.CurrentPath, StringComparison.OrdinalIgnoreCase) ||
                leaseId != request.LeaseId || ownerPid != request.OwnerPid ||
                ownerStartUtcTicks != request.OwnerStartUtcTicks)
                throw new InvalidDataException("副屏保护状态与启动时的会话身份不匹配。");
            protectedStateValidated = true;
        }

        /// <summary>
        /// Signals the parent only from inside the verified watcher loop. A
        /// caller cannot signal before binding protected state to the retained
        /// owner handle.
        /// </summary>
        internal void SignalReadyAfterLoopEntry()
        {
            ThrowIfDisposed();
            if (!protectedStateValidated)
                throw new InvalidOperationException("尚未验证副屏保护状态，不能确认保护进程就绪。");
            if (readySignaled) return;
            try { ready.Set(); }
            catch (Exception ex) when (ex is ObjectDisposedException or UnauthorizedAccessException or IOException)
            { throw new IOException("无法确认副屏保护进程就绪。"); }
            readySignaled = true;
        }

        internal OwnerObservation ObserveOwner(int ownerPid, long ownerStartUtcTicks)
        {
            ThrowIfDisposed();
            if (ownerPid != request.OwnerPid || ownerStartUtcTicks != request.OwnerStartUtcTicks)
                throw new InvalidDataException("副屏保护状态中的所有者身份已经变化。");
            try { return owner.HasExited ? OwnerObservation.Exited : OwnerObservation.VerifiedRunning; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { return OwnerObservation.Unverified; }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.Dispose();
            ready.Dispose();
        }
    }

    internal sealed record ParentEnvironment(Func<ProcessStartInfo, IStartedWatcherProcess?> StartProcess,
        TimeSpan PollInterval)
    {
        internal static ParentEnvironment Local { get; } = new(
            start =>
            {
                var process = Process.Start(start);
                return process is null ? null : new StartedWatcherProcess(process);
            }, TimeSpan.FromMilliseconds(25));
    }

    internal sealed record ChildEnvironment(Func<int, IRetainedOwnerProcess?> OpenOwner)
    {
        internal static ChildEnvironment Local { get; } = new(pid => new RetainedOwnerProcess(pid));
    }

    internal interface IStartedWatcherProcess : IDisposable
    {
        bool HasExited { get; }
        int ExitCode { get; }
    }

    internal interface IRetainedOwnerProcess : IDisposable
    {
        int Id { get; }
        long StartTimeUtcTicks { get; }
        bool HasExited { get; }
    }

    private sealed class StartedWatcherProcess(Process process) : IStartedWatcherProcess
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Dispose() => process.Dispose();
    }

    internal sealed class RetainedOwnerProcess : IRetainedOwnerProcess
    {
        private readonly Process process;
        private readonly Microsoft.Win32.SafeHandles.SafeProcessHandle exactHandle;
        internal RetainedOwnerProcess(int pid)
        {
            var candidate = Process.GetProcessById(pid);
            try
            {
                // StartTime and HasExited use temporary handles when a Process
                // was obtained by PID. Force and retain the Process-owned safe
                // handle before reading either value so a later PID reuse can
                // never be mistaken for the original display owner.
                exactHandle = candidate.SafeHandle;
                _ = exactHandle.DangerousGetHandle();
                Id = candidate.Id;
                StartTimeUtcTicks = candidate.StartTime.ToUniversalTime().Ticks;
                if (candidate.HasExited)
                    throw new InvalidOperationException("副屏所有者进程已经退出。");
                process = candidate;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }
        public int Id { get; }
        public long StartTimeUtcTicks { get; }
        public bool HasExited => process.HasExited;
        internal bool HasRetainedKernelHandle => !exactHandle.IsInvalid && !exactHandle.IsClosed;
        public void Dispose() => process.Dispose();
    }
}
