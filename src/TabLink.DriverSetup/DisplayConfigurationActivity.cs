using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace TabLink.DriverSetup;

internal static class DisplayConfigurationActivity
{
    internal enum LeaseOwnerStatus { Exited, Running, Unverified }

    private sealed class LeaseFiles
    {
        internal string? Current { get; set; }
        internal string? Marker { get; set; }
        internal Dictionary<Guid, string> Bootstraps { get; } = [];
    }

    private sealed record LeaseState(Guid LeaseId, int OwnerPid, long OwnerStartUtcTicks, string LeaseJson);

    internal static IDisposable AcquireLeaseLock()
        => DriverInstaller.AcquireDisplayLeaseMutationLock();

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
        // The caller holds the same protected file lock used by SessionGuard.
        // Reverify the complete namespace and every authorization file before
        // interpreting any marker or process identity.
        DriverInstaller.VerifyDisplayLeaseProtectedNamespace();
        AssertProtectedLeaseDirectoryIdle(DriverInstaller.DisplayLeaseRoot,
            DriverInstaller.VerifyDisplayLeaseStateFile, ReadOwnerStatus);
        AssertLegacyLeaseStateInactive();
    }

    internal static void AssertProtectedLeaseDirectoryIdle(string protectedRoot,
        Action<string> verifyProtectedFile, Func<int, long, LeaseOwnerStatus> ownerStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedRoot);
        ArgumentNullException.ThrowIfNull(verifyProtectedFile);
        ArgumentNullException.ThrowIfNull(ownerStatus);
        var root = Path.GetFullPath(protectedRoot);
        RejectReparseDirectory(root);
        var groups = new Dictionary<string, LeaseFiles>(StringComparer.Ordinal);
        foreach (var entry in Directory.GetFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(entry))
                throw new InvalidDataException("显示租约保护目录包含未授权子目录，未配置或重启驱动：" + Path.GetFileName(entry));
            var name = Path.GetFileName(entry);
            if (name == ".display-lease.lock") continue;

            string currentName;
            Guid bootstrapId = Guid.Empty;
            var kind = 0; // 1 current, 2 marker, 3 bootstrap
            if (IsLeaseFileName(name))
            {
                currentName = name;
                kind = 1;
            }
            else if (TryGetMarkerCurrentName(name, out currentName)) kind = 2;
            else if (TryGetBootstrapIdentity(name, out currentName, out bootstrapId)) kind = 3;
            else throw new InvalidDataException("显示租约保护目录包含未授权状态文件，未配置或重启驱动：" + name);

            verifyProtectedFile(entry);
            if (!groups.TryGetValue(currentName, out var files))
                groups.Add(currentName, files = new LeaseFiles());
            if (kind == 1)
            {
                if (files.Current is not null) throw new InvalidDataException("显示租约 current 状态重复。");
                files.Current = entry;
            }
            else if (kind == 2)
            {
                if (files.Marker is not null) throw new InvalidDataException("显示租约 marker 状态重复。");
                files.Marker = entry;
            }
            else if (!files.Bootstraps.TryAdd(bootstrapId, entry))
                throw new InvalidDataException("显示租约 bootstrap generation 重复。");
        }

        foreach (var (currentName, files) in groups)
            AssertLeaseGroupInactive(currentName, files, ownerStatus);
    }

    private static void AssertLeaseGroupInactive(string currentName, LeaseFiles files,
        Func<int, long, LeaseOwnerStatus> ownerStatus)
    {
        LeaseState? current = files.Current is null ? null : ReadLeaseState(files.Current, allowLegacyReverseLease: false);
        var bootstraps = files.Bootstraps.ToDictionary(pair => pair.Key,
            pair => ReadLeaseState(pair.Value, allowLegacyReverseLease: false));
        foreach (var (generation, state) in bootstraps)
            if (state.LeaseId != generation)
                throw new InvalidDataException("显示租约 bootstrap 文件名与内容 generation 不匹配：" + currentName);

        if (files.Marker is null)
        {
            // This is a bootstrap/current-before-marker crash cut. The global
            // lifecycle + display-lease locks prove there is no writer at this
            // instant, but the recorded owner must also be conclusively gone.
            if (current is null && bootstraps.Count == 0)
                throw new InvalidDataException("发现孤立显示租约文件，未配置或重启驱动。");
            foreach (var state in EnumerateDistinctStates(current, bootstraps.Values))
                RequireExitedOwner(state, ownerStatus, "缺少 marker 的未提交显示租约仍可能有活动 owner。");
            return;
        }

        var marker = ReadMarker(files.Marker);
        if (marker == Guid.Empty)
        {
            // An empty protected marker is an explicit retirement record. Keep
            // requiring a structurally coherent current/bootstrap pair so an
            // orphan empty marker cannot manufacture an idle result.
            if (current is null || !bootstraps.TryGetValue(current.LeaseId, out var retired))
                throw new InvalidDataException("空显示租约 marker 缺少匹配的 current/bootstrap 状态。");
            RequireSameOwnerAndLease(current, retired, "已退休显示租约 current/bootstrap 不一致。");
            foreach (var state in bootstraps.Where(pair => pair.Key != current.LeaseId).Select(pair => pair.Value))
            {
                // A single allocation can publish a provisional generation,
                // activate the target, and then publish a final generation for
                // the same target. Retiring the final generation supersedes an
                // older bootstrap from the exact same process incarnation: its
                // watcher has already lost marker authority. PID equality alone
                // is insufficient because Windows can reuse a process ID.
                if (SameExactOwner(state, retired)) continue;
                RequireExitedOwner(state, ownerStatus, "已退休 marker 旁的未引用 bootstrap 仍可能有活动 owner。");
            }
            return;
        }

        if (!bootstraps.TryGetValue(marker, out var authoritative))
            throw new InvalidDataException("非空显示租约 marker 缺少对应 bootstrap generation。");
        RequireExitedOwner(authoritative, ownerStatus, "仍有设备持有副屏租约，请停止全部连接后配置显示池。");

        // current can belong to the marker generation, or to a process that
        // crashed after publishing a newer current but before switching marker.
        // Only a conclusively exited unreferenced owner is safe to treat as a
        // crash residue. The marker-referenced bootstrap remains authoritative.
        if (current is not null)
        {
            if (current.LeaseId == marker)
                RequireSameOwnerAndLease(current, authoritative, "副屏 current 与 marker bootstrap 不一致。");
            else RequireExitedOwner(current, ownerStatus, "未提交的 current generation 仍可能有活动 owner。");
        }
        foreach (var state in bootstraps.Where(pair => pair.Key != marker).Select(pair => pair.Value))
            RequireExitedOwner(state, ownerStatus, "未引用的 bootstrap generation 仍可能有活动 owner。");
    }

    private static IEnumerable<LeaseState> EnumerateDistinctStates(LeaseState? current,
        IEnumerable<LeaseState> bootstraps)
    {
        var states = current is null ? bootstraps : bootstraps.Prepend(current);
        return states.GroupBy(state => (state.LeaseId, state.OwnerPid, state.OwnerStartUtcTicks))
            .Select(group => group.First());
    }

    private static void RequireExitedOwner(LeaseState state,
        Func<int, long, LeaseOwnerStatus> ownerStatus, string message)
    {
        if (ownerStatus(state.OwnerPid, state.OwnerStartUtcTicks) != LeaseOwnerStatus.Exited)
            throw new IOException(message);
    }

    private static void RequireSameOwnerAndLease(LeaseState left, LeaseState right, string message)
    {
        if (left.LeaseId != right.LeaseId || left.OwnerPid != right.OwnerPid ||
            left.OwnerStartUtcTicks != right.OwnerStartUtcTicks || left.LeaseJson != right.LeaseJson)
            throw new InvalidDataException(message);
    }

    private static bool SameExactOwner(LeaseState left, LeaseState right) =>
        left.OwnerPid == right.OwnerPid && left.OwnerStartUtcTicks == right.OwnerStartUtcTicks;

    private static LeaseState ReadLeaseState(string path, bool allowLegacyReverseLease)
    {
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var root = state.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("显示租约 JSON 根节点无效。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "Lease", "OwnerPid", "OwnerStartUtcTicks", "DeadlineUtc", "ReverseLeaseV2", "StopRequested" };
            if (allowLegacyReverseLease) allowed.Add("ReverseLease");
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || !allowed.Contains(property.Name))
                    throw new InvalidDataException("显示租约 JSON 包含重复或未授权字段：" + property.Name);
            if (allowLegacyReverseLease && names.Contains("ReverseLease") && names.Contains("ReverseLeaseV2"))
                throw new InvalidDataException("旧版显示租约同时包含冲突的 ReverseLease 与 ReverseLeaseV2 字段。");
            if (!names.Contains("Lease") || !names.Contains("OwnerPid") ||
                !names.Contains("OwnerStartUtcTicks") || !names.Contains("DeadlineUtc"))
                throw new InvalidDataException("显示租约 JSON 缺少必要字段。");
            var lease = root.GetProperty("Lease");
            if (lease.ValueKind != JsonValueKind.Object) throw new InvalidDataException("显示租约 Lease 字段无效。");
            var leaseId = lease.GetProperty("LeaseId").GetGuid();
            var pid = root.GetProperty("OwnerPid").GetInt32();
            var ticks = root.GetProperty("OwnerStartUtcTicks").GetInt64();
            _ = root.GetProperty("DeadlineUtc").GetDateTime();
            if (leaseId == Guid.Empty || pid <= 0 || ticks <= 0)
                throw new InvalidDataException("显示租约 generation 或 owner 身份无效。");
            return new(leaseId, pid, ticks, lease.GetRawText());
        }
        catch (JsonException ex) { throw new InvalidDataException("显示租约 JSON 无效：" + Path.GetFileName(path), ex); }
        catch (KeyNotFoundException ex) { throw new InvalidDataException("显示租约 JSON 缺少必要字段：" + Path.GetFileName(path), ex); }
        catch (FormatException ex) { throw new InvalidDataException("显示租约 JSON 字段格式无效：" + Path.GetFileName(path), ex); }
        catch (InvalidOperationException ex) { throw new InvalidDataException("显示租约 JSON 字段类型无效：" + Path.GetFileName(path), ex); }
    }

    private static Guid ReadMarker(string path)
    {
        try { return JsonSerializer.Deserialize<Guid>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("显示租约 marker JSON 无效。", ex); }
    }

    private static void AssertLegacyLeaseStateInactive()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
        AssertLegacyLeaseDirectoryInactive(folder, ReadOwnerStatus);
    }

    // This compatibility scan is deliberately read-only. It can prove that a
    // pre-ProgramData owner has exited, but it never grants display, ADB,
    // deletion, or mutation authority from a user-writable legacy file.
    internal static void AssertLegacyLeaseDirectoryInactive(string legacyTabLinkRoot,
        Func<int, long, LeaseOwnerStatus> ownerStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyTabLinkRoot);
        ArgumentNullException.ThrowIfNull(ownerStatus);
        var folder = Path.GetFullPath(legacyTabLinkRoot);
        var files = new List<string>();
        var legacy = Path.Combine(folder, "active-display-lease.json");
        if (File.Exists(legacy)) files.Add(legacy);
        var oldLeases = Path.Combine(folder, "display-leases");
        if (Directory.Exists(oldLeases))
            files.AddRange(Directory.GetFiles(oldLeases, "*.json", SearchOption.TopDirectoryOnly)
                .Where(file => IsLeaseFileName(Path.GetFileName(file))));
        foreach (var file in files)
        {
            var markerPath = file + ".lease-id";
            if (!File.Exists(markerPath))
                throw new InvalidDataException("旧版显示租约缺少 marker；未改写用户目录，也未判定为空闲。");
            var marker = ReadMarker(markerPath);
            var state = ReadLeaseState(file, allowLegacyReverseLease: true);
            if (marker != Guid.Empty && state.LeaseId != marker)
                throw new InvalidDataException("旧版显示租约与所有权 marker 不匹配。");
            if (ownerStatus(state.OwnerPid, state.OwnerStartUtcTicks) != LeaseOwnerStatus.Exited)
                throw new IOException("旧版连接仍可能持有副屏租约，请先停止连接。");
        }
    }

    internal static bool IsLeaseFileName(string name) => name.Length == 69 &&
        name.EndsWith(".json", StringComparison.Ordinal) && IsUpperHex(name.AsSpan(0, 64));

    private static bool TryGetMarkerCurrentName(string name, out string currentName)
    {
        const string suffix = ".lease-id";
        currentName = name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : "";
        return IsLeaseFileName(currentName);
    }

    private static bool TryGetBootstrapIdentity(string name, out string currentName, out Guid generation)
    {
        const string suffix = ".initial.json";
        currentName = "";
        generation = Guid.Empty;
        if (!name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var withoutSuffix = name[..^suffix.Length];
        var separator = withoutSuffix.LastIndexOf('.');
        if (separator < 0) return false;
        currentName = withoutSuffix[..separator];
        var id = withoutSuffix[(separator + 1)..];
        return IsLeaseFileName(currentName) && Guid.TryParseExact(id, "N", out generation) && generation != Guid.Empty;
    }

    private static bool IsUpperHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')) return false;
        return true;
    }

    private static void RejectReparseDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("显示租约保护目录不存在或是重解析点，未配置或重启驱动。");
    }

    private static LeaseOwnerStatus ReadOwnerStatus(int pid, long ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks
                ? LeaseOwnerStatus.Running : LeaseOwnerStatus.Exited;
        }
        catch (ArgumentException) { return LeaseOwnerStatus.Exited; }
        catch (InvalidOperationException) { return LeaseOwnerStatus.Exited; }
        catch (System.ComponentModel.Win32Exception) { return LeaseOwnerStatus.Unverified; }
    }
}
