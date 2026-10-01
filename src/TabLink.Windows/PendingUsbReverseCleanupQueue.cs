using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Windows;

internal enum UsbReverseQueueState { Prepared, Owned }

internal sealed record PendingUsbReverseCleanupEntry(
    UsbReverseLease Receipt, UsbReverseQueueState State);

/// <summary>
/// Supplies the protected-file boundary used by the durable USB cleanup queue.
/// Production binds this to the exact ProgramData owner/DACL policy; tests can
/// inject an E-drive-only predicate without weakening the production default.
/// </summary>
internal interface IUsbReverseQueueFileSecurity
{
    void VerifyStorage(string folder, string completedFolder);
    FileStream CreateProtectedFile(string path, string expectedParent);
    void VerifyProtectedFile(string path);
}

/// <summary>
/// Keeps immutable, per-receipt retry authority after a display lease has
/// retired. A completed tombstone permanently wins over a pending entry, so a
/// crash between those two durable filesystem operations cannot resurrect ADB
/// cleanup authority.
/// </summary>
internal sealed class PendingUsbReverseCleanupQueue
{
    internal const int CurrentEnvelopeSchema = 3;
    internal const int DefaultMaximumPendingRecords = 64;
    internal const int DefaultMaximumCompletedRecords = 65536;
    internal const int DefaultMaximumRecordBytes = 32 * 1024;

    const string PreparedKind = "prepared";
    const string OwnedKind = "owned";
    const string CompletedKind = "completed";
    const string CompletedFolderName = "completed";
    const string LockFileName = ".queue.lock";
    const int LockAttempts = 100;
    const int LockRetryMilliseconds = 10;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    readonly string folder;
    readonly string completedFolder;
    readonly int maximumPendingRecords;
    readonly int maximumCompletedRecords;
    readonly int maximumRecordBytes;
    readonly TimeSpan completedRetention;
    readonly Func<int, long, bool> ownerIsRunning;
    readonly IUsbReverseQueueFileSecurity fileSecurity;

    internal PendingUsbReverseCleanupQueue(string folder,
        int maximumPendingRecords = DefaultMaximumPendingRecords,
        int maximumRecordBytes = DefaultMaximumRecordBytes,
        int maximumCompletedRecords = DefaultMaximumCompletedRecords,
        TimeSpan? completedRetention = null,
        Func<int, long, bool>? ownerIsRunning = null,
        IUsbReverseQueueFileSecurity? fileSecurity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (maximumPendingRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingRecords));
        if (maximumCompletedRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCompletedRecords));
        if (maximumRecordBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes));
        if (completedRetention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(completedRetention));
        this.folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        completedFolder = Path.Combine(this.folder, CompletedFolderName);
        this.maximumPendingRecords = maximumPendingRecords;
        this.maximumCompletedRecords = maximumCompletedRecords;
        this.maximumRecordBytes = maximumRecordBytes;
        this.completedRetention = completedRetention ?? TimeSpan.FromDays(7);
        this.ownerIsRunning = ownerIsRunning ?? OwnerIsRunning;
        this.fileSecurity = fileSecurity ?? UsbReverseProtectedStorage.QueueFileSecurity;
    }

    /// <summary>
    /// Persists a non-removal intent before ADB is asked to bind the endpoint.
    /// Prepared records reserve their endpoint but can never authorize removal.
    /// </summary>
    internal void Prepare(UsbReverseLease receipt)
    {
        Validate(receipt);
        using var queueLock = AcquireLock();

        var completedPath = CompletedPathFor(receipt.Id);
        if (File.Exists(completedPath))
        {
            var completed = Read(completedPath, completedFolder, CompletedKind, receipt.Id);
            RequireSameReceipt(completed.Receipt, receipt,
                "同一 USB 清理标识已经由不同的完成记录占用。");
            RemoveExactPendingAfterCompletion(receipt);
            throw new InvalidDataException("USB 清理标识已经完成，不能重新准备绑定。");
        }

        var path = PendingPathFor(receipt.Id);
        if (File.Exists(path))
        {
            var existing = ReadPending(path, receipt.Id);
            RequireSameReceipt(existing.Receipt, receipt,
                "同一 USB 清理标识对应了不同的所有权记录。");
            if (existing.Kind == OwnedKind)
                throw new InvalidDataException("USB 清理标识已经拥有删除权限，不能重新准备绑定。");
            return;
        }

        EnsureCapacity(folder, maximumPendingRecords, "USB 待清理队列已满，拒绝丢弃旧记录。");
        var envelope = new QueueEnvelope(CurrentEnvelopeSchema, PreparedKind,
            NextPendingSequence(), receipt);
        var bytes = Serialize(envelope);
        WriteNewAtomically(path, bytes, envelope);

        // A writer from a future implementation may have completed this ID
        // without using our lock. Tombstone dominance still closes that race.
        if (File.Exists(completedPath))
        {
            var completed = Read(completedPath, completedFolder, CompletedKind, receipt.Id);
            RequireSameReceipt(completed.Receipt, receipt,
                "USB 待清理记录与并发完成记录冲突。");
            RemoveExactPendingAfterCompletion(receipt);
        }
    }

    /// <summary>
    /// Promotes an exact prepared intent only after --no-rebind returned
    /// success. A crash before this durable transition may leave an orphaned
    /// ADB mapping, but it can never grant authority to delete another owner.
    /// </summary>
    internal void Activate(UsbReverseLease receipt)
    {
        Validate(receipt);
        using var queueLock = AcquireLock();

        var completedPath = CompletedPathFor(receipt.Id);
        if (File.Exists(completedPath))
        {
            var completed = Read(completedPath, completedFolder, CompletedKind, receipt.Id);
            RequireSameReceipt(completed.Receipt, receipt,
                "USB 清理标识已经完成，不能重新授予删除权限。");
            throw new InvalidDataException("USB 清理标识已经完成，不能重新授予删除权限。");
        }

        var path = PendingPathFor(receipt.Id);
        if (!File.Exists(path))
            throw new InvalidDataException("USB 绑定成功前缺少持久化准备记录，拒绝授予删除权限。");
        var existing = ReadPending(path, receipt.Id);
        RequireSameReceipt(existing.Receipt, receipt,
            "USB 准备记录已经变化，拒绝授予删除权限。");
        if (existing.Kind == OwnedKind) return;
        if (existing.Kind != PreparedKind)
            throw new InvalidDataException("USB 准备记录状态无效。");
        var owned = new QueueEnvelope(CurrentEnvelopeSchema, OwnedKind, existing.Sequence, receipt);
        var bytes = Serialize(owned);
        ReplaceAtomically(path, bytes, owned);
    }

    internal IReadOnlyList<PendingUsbReverseCleanupEntry> ReadPendingEntries(int maximum,
        Action<string>? reportInvalid = null)
    {
        if (maximum <= 0) return [];
        using var queueLock = AcquireLock();
        var candidates = new List<(PendingUsbReverseCleanupEntry Entry, long Sequence)>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fileId = IdFromRecordPath(path);
                var pending = ReadPending(path, fileId);
                var completedPath = CompletedPathFor(fileId);
                if (File.Exists(completedPath))
                {
                    var completed = Read(completedPath, completedFolder, CompletedKind, fileId);
                    RequireSameReceipt(completed.Receipt, pending.Receipt,
                        "USB 待清理记录与完成墓碑不一致。");
                    File.Delete(path);
                    continue;
                }
                candidates.Add((new(pending.Receipt, pending.Kind == OwnedKind
                    ? UsbReverseQueueState.Owned : UsbReverseQueueState.Prepared), pending.Sequence));
            }
            catch (Exception ex) when (IsRecordFailure(ex))
            {
                reportInvalid?.Invoke(Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        return candidates.OrderBy(x => x.Sequence)
            .ThenBy(x => x.Entry.Receipt.Id)
            .Take(maximum).Select(x => x.Entry).ToArray();
    }

    internal IReadOnlyList<UsbReverseLease> ReadPending(int maximum,
        Action<string>? reportInvalid = null) =>
        ReadPendingEntries(maximum, reportInvalid).Select(x => x.Receipt).ToArray();

    internal void RequirePending(UsbReverseLease receipt, UsbReverseQueueState? requiredState = null)
    {
        Validate(receipt);
        using var queueLock = AcquireLock();
        var path = PendingPathFor(receipt.Id);
        if (!File.Exists(path))
            throw new InvalidDataException("USB 清理记录尚未持久化。");
        var pending = ReadPending(path, receipt.Id);
        RequireSameReceipt(pending.Receipt, receipt, "USB 清理记录与当前会话不一致。");
        if (requiredState is UsbReverseQueueState.Prepared && pending.Kind != PreparedKind ||
            requiredState is UsbReverseQueueState.Owned && pending.Kind != OwnedKind)
            throw new InvalidDataException("USB 清理记录状态与当前会话不一致。");
    }

    /// <summary>Moves a deferred record behind every currently pending entry.</summary>
    internal void Defer(PendingUsbReverseCleanupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry.Receipt);
        using var queueLock = AcquireLock();
        var path = PendingPathFor(entry.Receipt.Id);
        if (!File.Exists(path)) return;
        var pending = ReadPending(path, entry.Receipt.Id);
        RequireSameReceipt(pending.Receipt, entry.Receipt,
            "USB 清理记录已经变化，拒绝调整重试顺序。");
        var expectedKind = entry.State == UsbReverseQueueState.Owned ? OwnedKind : PreparedKind;
        if (pending.Kind != expectedKind)
            throw new InvalidDataException("USB 清理记录状态已经变化，拒绝调整重试顺序。");
        var deferred = pending with { Sequence = NextPendingSequence() };
        ReplaceAtomically(path, Serialize(deferred), deferred);
    }

    internal void Complete(UsbReverseLease receipt)
    {
        Validate(receipt);
        var envelope = new QueueEnvelope(CurrentEnvelopeSchema, CompletedKind, 0, receipt);
        var bytes = Serialize(envelope);
        using var queueLock = AcquireLock();

        var path = PendingPathFor(receipt.Id);
        if (File.Exists(path))
        {
            var pending = ReadPending(path, receipt.Id);
            RequireSameReceipt(pending.Receipt, receipt,
                "USB 待清理记录已经变化，拒绝完成它。");
        }

        var completedPath = CompletedPathFor(receipt.Id);
        if (File.Exists(completedPath))
        {
            var completed = Read(completedPath, completedFolder, CompletedKind, receipt.Id);
            RequireSameReceipt(completed.Receipt, receipt,
                "USB 清理标识已经由不同的完成记录占用。");
        }
        else
        {
            CompactCompletedIfNeeded();
            EnsureCapacity(completedFolder, maximumCompletedRecords,
                "USB 清理完成记录已满且没有可安全回收的旧墓碑，拒绝删除待清理证明。");
            WriteNewAtomically(completedPath, bytes, envelope);
        }

        // The tombstone is durable before this delete. If deletion fails or the
        // process crashes here, ReadPending observes the exact tombstone and
        // safely converges by deleting the duplicate pending entry later.
        if (File.Exists(path)) File.Delete(path);
    }

    void CompactCompletedIfNeeded()
    {
        if (!Directory.Exists(completedFolder)) return;
        var paths = Directory.EnumerateFiles(completedFolder, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var count = paths.Count;
        if (count < maximumCompletedRecords) return;
        var now = DateTime.UtcNow;
        foreach (var completedPath in paths)
        {
            if (count < maximumCompletedRecords) break;
            var written = File.GetLastWriteTimeUtc(completedPath);
            if (written > now || now - written < completedRetention) continue;
            var id = IdFromCompletedRecordPath(completedPath);
            var completed = Read(completedPath, completedFolder, CompletedKind, id);
            if (File.Exists(PendingPathFor(id))) continue;
            if (ownerIsRunning(completed.Receipt.OwnerPid, completed.Receipt.OwnerStartUtcTicks)) continue;
            File.Delete(completedPath);
            count--;
        }
    }

    void RemoveExactPendingAfterCompletion(UsbReverseLease receipt)
    {
        var path = PendingPathFor(receipt.Id);
        if (!File.Exists(path)) return;
        var pending = ReadPending(path, receipt.Id);
        RequireSameReceipt(pending.Receipt, receipt,
            "完成墓碑旁存在不同的 USB 待清理记录。");
        File.Delete(path);
    }

    QueueEnvelope ReadPending(string path, Guid expectedId)
    {
        var envelope = Read(path, folder, null, expectedId);
        if (envelope.Kind is not (PreparedKind or OwnedKind))
            throw new InvalidDataException("USB 清理记录状态无效。");
        return envelope;
    }

    QueueEnvelope Read(string path, string expectedFolder, string? expectedKind, Guid expectedId)
    {
        EnsureRecordPath(path, expectedFolder, expectedId);
        fileSecurity.VerifyProtectedFile(path);
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("USB 清理记录不能是重解析点。");
        if (info.Length <= 0 || info.Length > maximumRecordBytes)
            throw new InvalidDataException("USB 清理记录大小超出允许范围。");

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   4096, FileOptions.SequentialScan))
        {
            if (stream.Length <= 0 || stream.Length > maximumRecordBytes)
                throw new InvalidDataException("USB 清理记录大小超出允许范围。");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        // Recheck the exact owner/DACL and file type after every read. The
        // protected parent prevents an ordinary user from replacing the file
        // between these checks; a privileged concurrent mutation fails closed.
        fileSecurity.VerifyProtectedFile(path);

        RejectDuplicateProperties(bytes);
        var envelope = JsonSerializer.Deserialize<QueueEnvelope>(bytes, JsonOptions)
            ?? throw new InvalidDataException("USB 清理记录为空。");
        if (envelope.Schema != CurrentEnvelopeSchema)
            throw new InvalidDataException("USB 清理记录版本不受支持。");
        if (expectedKind is not null && !string.Equals(envelope.Kind, expectedKind, StringComparison.Ordinal))
            throw new InvalidDataException("USB 清理记录状态无效。");
        if (envelope.Kind == CompletedKind ? envelope.Sequence != 0 : envelope.Sequence <= 0)
            throw new InvalidDataException("USB 清理记录顺序无效。");
        if (envelope.Receipt is null)
            throw new InvalidDataException("USB 清理记录缺少所有权收据。");
        Validate(envelope.Receipt);
        if (envelope.Receipt.Id != expectedId)
            throw new InvalidDataException("USB 清理文件名与记录标识不一致。");
        return envelope;
    }

    void ReplaceAtomically(string path, byte[] bytes, QueueEnvelope expected)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("USB 清理记录目录无效。");
        var temporary = Path.Combine(directory,
            expected.Receipt.Id.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = fileSecurity.CreateProtectedFile(temporary, directory))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            fileSecurity.VerifyProtectedFile(temporary);
            fileSecurity.VerifyProtectedFile(path);
            File.Move(temporary, path, overwrite: true);
            fileSecurity.VerifyProtectedFile(path);
            var actual = ReadPending(path, expected.Receipt.Id);
            if (actual.Kind != expected.Kind || actual.Sequence != expected.Sequence)
                throw new InvalidDataException("USB 清理记录状态转换未持久化。");
            RequireSameReceipt(actual.Receipt, expected.Receipt,
                "USB 清理记录状态转换后内容不一致。");
        }
        finally
        {
            DeleteTemporaryIfStillProtected(temporary);
        }
    }

    byte[] Serialize(QueueEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > maximumRecordBytes)
            throw new InvalidDataException("USB 清理记录大小超出允许范围。");
        return bytes;
    }

    long NextPendingSequence()
    {
        var maximum = 0L;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            var id = IdFromRecordPath(path);
            var pending = ReadPending(path, id);
            if (pending.Sequence > maximum) maximum = pending.Sequence;
        }
        if (maximum == long.MaxValue)
            throw new IOException("USB 清理重试顺序已经达到上限，拒绝覆盖现有记录。");
        return maximum + 1;
    }

    void WriteNewAtomically(string path, byte[] bytes, QueueEnvelope expected)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("USB 清理记录目录无效。");
        var temporary = Path.Combine(directory,
            expected.Receipt.Id.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = fileSecurity.CreateProtectedFile(temporary, directory))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            fileSecurity.VerifyProtectedFile(temporary);

            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                var actual = expected.Kind == CompletedKind
                    ? Read(path, completedFolder, CompletedKind, expected.Receipt.Id)
                    : ReadPending(path, expected.Receipt.Id);
                RequireSameReceipt(actual.Receipt, expected.Receipt,
                    "USB 清理记录在原子写入时发生冲突。");
                if (actual.Kind != expected.Kind || actual.Sequence != expected.Sequence)
                    throw new InvalidDataException("USB 清理记录在原子写入时发生状态冲突。");
                return;
            }
            fileSecurity.VerifyProtectedFile(path);
            var committed = expected.Kind == CompletedKind
                ? Read(path, completedFolder, CompletedKind, expected.Receipt.Id)
                : ReadPending(path, expected.Receipt.Id);
            RequireSameReceipt(committed.Receipt, expected.Receipt,
                "USB 清理记录提交后内容不一致。");
            if (committed.Kind != expected.Kind || committed.Sequence != expected.Sequence)
                throw new InvalidDataException("USB 清理记录提交后状态不一致。");
        }
        finally
        {
            DeleteTemporaryIfStillProtected(temporary);
        }
    }

    void DeleteTemporaryIfStillProtected(string temporary)
    {
        if (!File.Exists(temporary)) return;
        try
        {
            fileSecurity.VerifyProtectedFile(temporary);
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                     UnauthorizedAccessException or ArgumentException) { }
    }

    FileStream AcquireLock()
    {
        fileSecurity.VerifyStorage(folder, completedFolder);
        var lockPath = Path.Combine(folder, LockFileName);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                fileSecurity.VerifyStorage(folder, completedFolder);
                if (!File.Exists(lockPath))
                {
                    FileStream? created = null;
                    try { created = fileSecurity.CreateProtectedFile(lockPath, folder); }
                    catch (IOException) when (File.Exists(lockPath)) { }
                    if (created is not null)
                    {
                        try
                        {
                            created.Flush(flushToDisk: true);
                            // Keep the exclusive protected-creation handle as
                            // the queue lock. Reverify while that exact object
                            // is still held, so creation never has a close/open
                            // path window.
                            fileSecurity.VerifyProtectedFile(lockPath);
                            fileSecurity.VerifyStorage(folder, completedFolder);
                            return created;
                        }
                        catch
                        {
                            created.Dispose();
                            throw;
                        }
                    }
                }
                fileSecurity.VerifyProtectedFile(lockPath);
                var opened = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
                try
                {
                    // Reverify after acquiring FileShare.None. This proves the
                    // held lock object still has the exact owner/DACL and is an
                    // ordinary, non-reparse file rather than trusting only the
                    // earlier path lookup.
                    fileSecurity.VerifyProtectedFile(lockPath);
                    fileSecurity.VerifyStorage(folder, completedFolder);
                    return opened;
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }
            }
            catch (IOException) when (attempt + 1 < LockAttempts)
            {
                Thread.Sleep(LockRetryMilliseconds);
            }
        }
    }

    static void EnsureCapacity(string directory, int maximum, string message)
    {
        if (!Directory.Exists(directory)) return;
        var count = 0;
        foreach (var _ in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (++count >= maximum) throw new IOException(message);
        }
    }

    static void RejectDuplicateProperties(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        Visit(document.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("USB 清理记录含有重复属性。");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
        }
    }

    void EnsureRecordPath(string path, string expectedFolder, Guid expectedId)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), expectedFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(full), expectedId.ToString("N") + ".json",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("USB 清理路径超出允许目录或文件名无效。");
    }

    Guid IdFromRecordPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("USB 待清理路径超出允许目录。");
        var name = Path.GetFileNameWithoutExtension(full);
        if (!Guid.TryParseExact(name, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("USB 待清理文件名无效。");
        return id;
    }

    Guid IdFromCompletedRecordPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), completedFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("USB 清理墓碑路径超出允许目录。");
        var name = Path.GetFileNameWithoutExtension(full);
        if (!Guid.TryParseExact(name, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("USB 清理墓碑文件名无效。");
        return id;
    }

    string PendingPathFor(Guid id) => Path.Combine(folder, RecordFileName(id));
    string CompletedPathFor(Guid id) => Path.Combine(completedFolder, RecordFileName(id));
    static string RecordFileName(Guid id) => id.ToString("N") + ".json";

    static void RequireSameReceipt(UsbReverseLease actual, UsbReverseLease expected, string message)
    {
        if (actual != expected) throw new InvalidDataException(message);
    }

    static void Validate(UsbReverseLease receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != UsbReverseLease.CurrentSchemaVersion || receipt.Id == Guid.Empty ||
            receipt.OwnerPid <= 0 || receipt.OwnerStartUtcTicks <= 0 || !receipt.Endpoint.IsValid ||
            !IsValidUserSid(receipt.OwnerUserSid) ||
            !TabLink.Core.DevicePolicy.IsSafeUsbSerial(receipt.Serial) ||
            !TabLink.Core.DevicePolicy.IsUsbHardwareId(receipt.Vid) ||
            !TabLink.Core.DevicePolicy.IsUsbHardwareId(receipt.Pid))
            throw new InvalidDataException("USB 待清理所有权记录无效。");
    }

    static bool IsValidUserSid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 184 ||
            !value.StartsWith("S-1-", StringComparison.Ordinal)) return false;
        try
        {
            var sid = new System.Security.Principal.SecurityIdentifier(value);
            return string.Equals(sid.Value, value, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    static bool IsRecordFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;

    static bool OwnerIsRunning(int pid, long startTicks)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    sealed record QueueEnvelope(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("receipt")] UsbReverseLease Receipt);
}
