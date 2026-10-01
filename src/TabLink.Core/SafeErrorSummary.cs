namespace TabLink.Core;

/// <summary>
/// Produces text that is safe to place in the UI and persistent logs. Raw ADB
/// stdout, stderr, executable paths and device identifiers remain available to
/// the in-memory exception object, but are never copied into this summary.
/// </summary>
public static class SafeErrorSummary
{
    public static string ForUser(Exception error, bool adbOperation = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        foreach (var candidate in Enumerate(error))
        {
            if (candidate is AdbCommandException command)
                return $"ADB 错误：{nameof(AdbCommandException)}（退出代码 {command.ExitCode}）。";
            if (candidate is AdbExecutionException)
                return $"ADB 错误：{nameof(AdbExecutionException)}。";
            if (candidate is AdbResponseException)
                return $"ADB 错误：{nameof(AdbResponseException)}。";
            if (candidate is AdbDeviceTemporarilyUnavailableException)
                return $"ADB 错误：{nameof(AdbDeviceTemporarilyUnavailableException)}。";
            if (candidate is DevicePolicyException)
                return $"ADB 错误：{candidate.GetType().Name}。";
        }
        return adbOperation
            ? $"ADB 错误：{error.GetType().Name}。"
            : error.Message;
    }

    internal static bool IsExecutionFailure(Exception error) =>
        error is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException;

    static IEnumerable<Exception> Enumerate(Exception root)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            yield return current;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
    }
}

/// <summary>
/// A safe boundary for failures while starting or communicating with the ADB
/// child process. The inner exception is retained for in-memory debugging;
/// callers must use <see cref="SafeErrorSummary"/> for user-visible output.
/// </summary>
public sealed class AdbExecutionException(Exception innerException)
    : IOException("ADB child process execution failed.", innerException);

/// <summary>Safe boundary for malformed or untrusted ADB response payloads.</summary>
public sealed class AdbResponseException(Exception innerException)
    : IOException("ADB response validation failed.", innerException);
