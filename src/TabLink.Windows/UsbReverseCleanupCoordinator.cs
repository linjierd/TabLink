using System.Collections.Concurrent;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed record UsbReverseCleanupPass(int Examined, int Completed, int Deferred);

/// <summary>
/// Connects protected receipt storage to exact, policy-checked ADB cleanup and
/// endpoint allocation. Diagnostics intentionally contain no device identity.
/// </summary>
internal sealed class UsbReverseCleanupCoordinator
{
    const int MaximumCleanupAttemptsPerPass = 4;
    readonly PendingUsbReverseCleanupQueue queue;
    readonly UsbReverseMutationGate mutationGate;
    readonly Func<UsbReverseLease, bool, bool, CancellationToken, Task<UsbReverseCleanupResult>> cleanup;
    readonly Action<string, object>? diagnostic;
    readonly ConcurrentDictionary<Guid, UsbReverseLease> retiredByThisProcess = new();
    readonly int processId;
    readonly long processStartTicks;

    static readonly Lazy<UsbReverseCleanupCoordinator> SharedCoordinator = new(() => new(
        UsbReverseProtectedStorage.OpenQueue(), UsbReverseMutationGate.Shared,
        UsbReverseLease.CleanupPendingAsync,
        (name, value) => Diagnostics.Save(name, value)));
    internal static UsbReverseCleanupCoordinator Shared => SharedCoordinator.Value;

    internal UsbReverseCleanupCoordinator(PendingUsbReverseCleanupQueue queue,
        UsbReverseMutationGate mutationGate,
        Func<UsbReverseLease, bool, bool, CancellationToken, Task<UsbReverseCleanupResult>> cleanup,
        Action<string, object>? diagnostic = null,
        int? processId = null,
        long? processStartTicks = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
        this.cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        this.diagnostic = diagnostic;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        this.processId = processId ?? current.Id;
        this.processStartTicks = processStartTicks ?? current.StartTime.ToUniversalTime().Ticks;
    }

    internal void MarkRetiredByThisProcess(UsbReverseLease receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.OwnerPid != processId || receipt.OwnerStartUtcTicks != processStartTicks)
            throw new InvalidDataException("只能注销当前进程自己创建的 USB 删除权限。");
        queue.RequirePending(receipt, UsbReverseQueueState.Owned);
        if (!retiredByThisProcess.TryAdd(receipt.Id, receipt) &&
            (!retiredByThisProcess.TryGetValue(receipt.Id, out var existing) || existing != receipt))
            throw new InvalidDataException("USB 注销例外与已有收据冲突。");
    }

    internal void PrepareActiveMutation(UsbReverseLease receipt) => queue.Prepare(receipt);
    internal void ActivateActiveMutation(UsbReverseLease receipt) => queue.Activate(receipt);
    internal void CompleteActiveMutation(UsbReverseLease receipt) => queue.Complete(receipt);

    internal Task<T> RunMutationAsync<T>(
        Func<IReadOnlySet<AdbReverseEndpoint>, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return mutationGate.RunAsync(async ct =>
        {
            var reserved = ReadReservedEndpoints();
            return await action(reserved, ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    internal Task RunMutationAsync(
        Func<IReadOnlySet<AdbReverseEndpoint>, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunMutationAsync(async (reserved, ct) =>
        {
            await action(reserved, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    internal Task<UsbReverseCleanupPass> ProcessPendingAsync(string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return mutationGate.RunAsync(async ct =>
        {
            var entries = ReadAllPending().Take(MaximumCleanupAttemptsPerPass).ToArray();
            var completed = 0;
            foreach (var entry in entries)
            {
                var receipt = entry.Receipt;
                var retired = retiredByThisProcess.TryGetValue(receipt.Id, out var retiredReceipt) &&
                    retiredReceipt == receipt;
                UsbReverseCleanupResult result;
                try
                {
                    result = await cleanup(receipt, retired,
                        entry.State == UsbReverseQueueState.Owned, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result = new(UsbReverseCleanupStatus.Failed, ex.Message);
                }
                var completionPersisted = false;
                if (result.Complete)
                {
                    try
                    {
                        queue.Complete(receipt);
                        completionPersisted = true;
                        if (retiredByThisProcess.TryGetValue(receipt.Id, out var current) && current == receipt)
                            retiredByThisProcess.TryRemove(receipt.Id, out _);
                        completed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The ADB result may already be terminal, but deletion
                        // authority is consumed only after its durable tombstone
                        // exists. Rotate this intact proof so a full/unavailable
                        // tombstone store cannot starve later records.
                        queue.Defer(entry);
                    }
                }
                else queue.Defer(entry);
                diagnostic?.Invoke("usb-reverse-pending.json", new
                {
                    timestamp = DateTimeOffset.Now,
                    reason,
                    receipt.Id,
                    entry.State,
                    result.Status,
                    completionPersisted
                });
            }
            return new UsbReverseCleanupPass(entries.Length, completed, entries.Length - completed);
        }, cancellationToken);
    }

    internal void ProcessPendingBlocking(string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        ProcessPendingAsync(reason, timeout.Token).GetAwaiter().GetResult();
    }

    internal IReadOnlySet<AdbReverseEndpoint> ReadReservedEndpoints()
        => ReadAllPending().Select(x => x.Receipt.Endpoint).ToHashSet();

    PendingUsbReverseCleanupEntry[] ReadAllPending()
    {
        var invalid = new List<string>();
        var entries = queue.ReadPendingEntries(int.MaxValue, invalid.Add).ToArray();
        if (invalid.Count != 0)
            throw new InvalidDataException("USB 待清理队列包含无效记录，已阻止分配新端点：" + invalid[0]);
        return entries;
    }
}
