using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TabLink.Windows;

// Serializable proof of the selected output, not permission to operate on a
// device name alone. Every mutation resolves and verifies this identity again.
public sealed record DisplayLease(
    Guid LeaseId, string DeviceName, string AdapterInstanceId, string MonitorPath,
    uint AdapterLowPart, int AdapterHighPart, uint SourceId,
    uint TargetAdapterLowPart, int TargetAdapterHighPart, uint TargetId,
    DisplayDesktopState OriginalMode, IReadOnlyList<DisplayDesktopState> OtherDisplays);

public sealed record DisplayDesktopState(string DeviceName, int X, int Y, int Width, int Height,
    uint BitsPerPixel, uint Frequency, uint Orientation, uint DisplayFlags, uint FixedOutput, bool IsPrimary);

public sealed record DisplayChangeResult(bool Success, bool Changed, string Message);

public static partial class VirtualDisplayManager
{
    private const uint DmPosition = 0x20, DmWidth = 0x80000, DmHeight = 0x100000;
    private const uint CdsTest = 2, CdsUpdateRegistry = 1;
    private static readonly string TopologyMutex = @"Local\TabLink.DisplayTopology." + Process.GetCurrentProcess().SessionId;

    public static DisplayLease CaptureLease(VirtualDisplayInfo selected)
    {
        if (!selected.IsTabLinkCompatible || selected.IsPrimary)
            throw new InvalidOperationException("只能为已确认的独立 MttVDD 副屏创建连接会话。");
        ValidateLifecycleInterop();
        var paths = ReadActivePaths().Where(p => Same(p.SourceName, selected.DeviceName)).ToArray();
        if (paths.Length != 1 || !paths[0].IsMttDriver || !paths[0].IsMttMonitor || paths[0].IsCloned)
            throw new InvalidOperationException("副屏身份已变化或处于镜像模式，请刷新显示器。");
        var path = paths[0];
        var layout = ReadDesktopLayout();
        var mode = layout.SingleOrDefault(d => Same(d.DeviceName, selected.DeviceName));
        if (mode is null || mode.IsPrimary || mode.Width <= 0 || mode.Height <= 0 ||
            mode.X != selected.Bounds.X || mode.Y != selected.Bounds.Y ||
            mode.Width != selected.Bounds.Width || mode.Height != selected.Bounds.Height ||
            !layout.Any(d => d.IsPrimary && !Same(d.DeviceName, selected.DeviceName)))
            throw new InvalidOperationException("副屏布局已变化，未创建连接会话。请刷新显示器。");
        return new(Guid.NewGuid(), path.SourceName, path.AdapterInstanceId, path.MonitorPath,
            path.AdapterId.LowPart, path.AdapterId.HighPart, path.SourceId,
            path.TargetAdapterId.LowPart, path.TargetAdapterId.HighPart, path.TargetId,
            mode, layout.Where(d => !Same(d.DeviceName, selected.DeviceName)).ToArray());
    }

    public static DisplayChangeResult Detach(DisplayLease lease) => WithTopologyLock(() => DetachCore(lease));

    public static DisplayChangeResult Restore(DisplayLease lease) => WithTopologyLock(() => RestoreCore(lease));

    // DriverSetup's explicitly requested, idle-pool collection also has to
    // handle virtual-aware CCD paths not exposed as active GDI desktops. Such
    // initial clone paths cannot be represented by a normal extended lease.
    internal static DisplayChangeResult DetachIdleTarget(VirtualDisplayTarget target) => WithTopologyLock(() => DetachTargetCore(target));

    private static DisplayChangeResult DetachTargetCore(VirtualDisplayTarget target, DisplayLease? expectedLease = null)
    {
        ValidateLifecycleInterop();
        var verified = GetTargets().SingleOrDefault(t => t.TargetKey == target.TargetKey);
        if (verified is null || verified.IsPrimary || !Same(verified.AdapterInstanceId, target.AdapterInstanceId) ||
            !Same(verified.MonitorPath, target.MonitorPath))
            return new(false, false, "空闲虚拟目标的设备身份或非主屏状态发生变化，未操作该目标。");
        if (!verified.IsActive) return new(true, false, "该虚拟目标已经断开。");
        var before = ReadDesktopLayout();
        var snapshot = ReadNativeSnapshot(2);
        bool Own(DISPLAYCONFIG_PATH_INFO p) => p.targetInfo.adapterId == new LUID(target.AdapterLowPart, target.AdapterHighPart) && p.targetInfo.id == target.TargetId;
        var own = snapshot.Paths.Where(Own).ToArray();
        if (own.Length != 1) return new(false, false, "空闲虚拟目标的活动 CCD 路径不唯一，未操作。");
        var removed = own[0];
        if (expectedLease is not null &&
            (removed.sourceInfo.adapterId != new LUID(expectedLease.AdapterLowPart, expectedLease.AdapterHighPart) ||
                removed.sourceInfo.id != expectedLease.SourceId))
            return new(false, false, "会话的虚拟显示源身份发生变化，未收回目标。");
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = Header<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(1, removed.sourceInfo.adapterId, removed.sourceInfo.id) };
        if (GetSourceName(ref source) != 0 || before.Any(d => d.IsPrimary && Same(d.DeviceName, source.viewGdiDeviceName)))
            return new(false, false, "空闲目标涉及主显示源，未操作。");
        var retained = snapshot.Paths.Where(p => !Own(p)).ToArray();
        if (retained.Length == 0 || !before.Any(d => d.IsPrimary)) return new(false, false, "不能移除最后一个显示目标。");
        string Identity(IEnumerable<DISPLAYCONFIG_PATH_INFO> paths) => ActiveIdentity(paths.Select(p => PathIdentity(p.sourceInfo.adapterId, p.sourceInfo.id, p.targetInfo.adapterId, p.targetInfo.id)));
        var allIdentity = Identity(snapshot.Paths);
        var otherIdentity = Identity(retained);
        var allState = NativeActiveState(snapshot);
        var retainedState = NativeActiveState(new(retained, snapshot.Modes));
        var verifiedPathsBefore = ReadActivePaths();
        var movablePaths = verifiedPathsBefore.Where(p => p.IsMttDriver && p.IsMttMonitor && !p.IsCloned &&
            Same(p.AdapterInstanceId, target.AdapterInstanceId) && !Same(p.SourceName, source.viewGdiDeviceName) &&
            before.Any(d => !d.IsPrimary && Same(d.DeviceName, p.SourceName))).ToArray();
        var movablePathKeys = movablePaths.Select(p => PathIdentity(p.AdapterId, p.SourceId, p.TargetAdapterId, p.TargetId)).ToHashSet(StringComparer.Ordinal);
        var movableNames = movablePaths.Select(p => p.SourceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validate = SetDisplayConfig((uint)retained.Length, retained, (uint)snapshot.Modes.Length, snapshot.Modes, 0x60 | SdcVirtualModeAware);
        if (validate != 0) return new(false, false, "测试收回空闲 CCD 目标失败（" + validate + "）。");
        var preApply = ReadNativeSnapshot(2);
        if (!LayoutsEqual(before, ReadDesktopLayout()) || allIdentity != Identity(preApply.Paths) || allState != NativeActiveState(preApply) ||
            !GetTargets().Any(t => t.TargetKey == target.TargetKey && !t.IsPrimary && t.IsActive))
            return new(false, false, "桌面在收回前发生变化，未操作。");
        var applied = SetDisplayConfig((uint)retained.Length, retained, (uint)snapshot.Modes.Length, snapshot.Modes, 0xa0 | SdcVirtualModeAware);
        if (applied != 0) return new(false, false, "收回空闲 CCD 目标失败（" + applied + "）。");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var after = ReadNativeSnapshot(2);
            if (!after.Paths.Any(Own))
            {
                var otherBefore = before.Where(d => !Same(d.DeviceName, source.viewGdiDeviceName)).ToArray();
                var otherAfter = ReadDesktopLayout().Where(d => !Same(d.DeviceName, source.viewGdiDeviceName)).ToArray();
                var same = otherIdentity == Identity(after.Paths) && retainedState == NativeActiveState(after) && LayoutsEqual(otherBefore, otherAfter);
                if (same) return new(true, true, "已仅收回此 TabLink CCD 目标，其余活动路径与桌面保持不变。");
                var verifiedPathsAfter = ReadActivePaths();
                var sameMovableIdentity = movablePaths.All(old => verifiedPathsAfter.Count(current =>
                    current.IsMttDriver && current.IsMttMonitor && !current.IsCloned &&
                    Same(old.SourceName, current.SourceName) && Same(old.AdapterInstanceId, current.AdapterInstanceId) &&
                    Same(old.MonitorPath, current.MonitorPath) && old.AdapterId == current.AdapterId && old.SourceId == current.SourceId &&
                    old.TargetAdapterId == current.TargetAdapterId && old.TargetId == current.TargetId) == 1);
                var relocated = otherIdentity == Identity(after.Paths) && sameMovableIdentity &&
                    NativeActiveState(new(retained, snapshot.Modes), movablePathKeys) == NativeActiveState(after, movablePathKeys) &&
                    IsOnlyOwnedPositionChange(otherBefore, otherAfter, movableNames);
                SaveDetachDelta(target, snapshot, after, before, otherAfter, relocated);
                return relocated
                    ? new(true, true, "已收回该目标。Windows 自动移动了其余同驱动副屏的位置；身份、尺寸、刷新率及物理屏保持不变，各会话将跟随新位置。")
                    : new(false, true, "目标已收回，但其余路径或桌面同时变化，未覆盖其他设置。已记录 CCD 与桌面差异。");
            }
            Thread.Sleep(100);
        }
        return new(false, true, "Windows 接受收回请求，但该 CCD 目标仍然活动。");
    }

    public static DisplayLease CaptureDetachedLease(int width, int height, int refreshRate)
    {
        var targets = GetTargets();
        if (targets.Count != 1 || targets[0].IsActive)
            throw new InvalidOperationException("需要唯一、未活动且身份明确的 MttVDD 目标才能创建新副屏会话。");
        var sources = targets[0].Sources.Where(s => !s.IsInUse && !s.IsCloned).ToArray();
        if (sources.Length != 1)
            throw new InvalidOperationException("需要唯一且未使用的虚拟显示源。");
        return CaptureDetachedLease(targets[0], sources[0], width, height, refreshRate);
    }

    public static DisplayLease CaptureDetachedLease(VirtualDisplayTarget selected, VirtualDisplaySource selectedSource,
        int width, int height, int refreshRate)
    {
        if (width is < 320 or > 7680 || height is < 320 or > 7680 || (long)width * height > 33_177_600 || refreshRate is < 30 or > 240)
            throw new ArgumentOutOfRangeException(nameof(width), "副屏尺寸或刷新率超出支持范围。");
        ValidateLifecycleInterop();
        var all = ReadDisplayPaths(1);
        var candidates = all.Where(p => p.IsMttDriver && p.IsMttMonitor && p.TargetAvailable &&
            TargetKey(p.AdapterInstanceId, p.MonitorPath, p.TargetAdapterId.LowPart, p.TargetAdapterId.HighPart, p.TargetId) == selected.TargetKey &&
            SourceKey(p.AdapterId.LowPart, p.AdapterId.HighPart, p.SourceId) == selectedSource.SourceKey && Same(p.SourceName, selectedSource.DeviceName)).ToArray();
        if (candidates.Length != 1 || candidates[0].IsActive || candidates[0].IsCloned ||
            all.Any(p => p.IsActive && (SourceKey(p.AdapterId.LowPart, p.AdapterId.HighPart, p.SourceId) == selectedSource.SourceKey ||
                TargetKey(p.AdapterInstanceId, p.MonitorPath, p.TargetAdapterId.LowPart, p.TargetAdapterId.HighPart, p.TargetId) == selected.TargetKey)))
            throw new InvalidOperationException("所选虚拟目标或显示源已在使用、失效或不再唯一，未创建副屏会话。");
        var path = candidates[0];
        var others = ReadDesktopLayout();
        if (others.Any(d => Same(d.DeviceName, path.SourceName)) || !others.Any(d => d.IsPrimary))
            throw new InvalidOperationException("原虚拟源仍在使用或主屏不可用，未创建副屏会话。");
        // A fresh driver LUID invalidates the old lease. Place the new target
        // against the rightmost live monitor; do not reuse an unverified name.
        var anchor = others.OrderByDescending(d => (long)d.X + d.Width).First();
        var requested = new DisplayDesktopState(path.SourceName, checked(anchor.X + anchor.Width), anchor.Y,
            width, height, 32, (uint)refreshRate, 0, 0, 0, false);
        var lease = new DisplayLease(Guid.NewGuid(), path.SourceName, path.AdapterInstanceId, path.MonitorPath,
            path.AdapterId.LowPart, path.AdapterId.HighPart, path.SourceId,
            path.TargetAdapterId.LowPart, path.TargetAdapterId.HighPart, path.TargetId, requested, others.ToArray());
        var choices = GetSupportedModes(lease).Where(m => m.Width == width && m.Height == height && m.RefreshRate == refreshRate)
            .OrderBy(m => m.Orientation == 0 ? 0 : 1).ThenBy(m => m.Orientation).ToArray();
        // The caller needs this exact-device proof to inspect/configure an
        // unsupported requested mode. Restore still re-enumerates and refuses
        // to apply any mode that is absent from the driver's supported list.
        return choices.Length == 0 ? lease : lease with { OriginalMode = requested with { Orientation = choices[0].Orientation } };
    }

    private static DisplayChangeResult DetachCore(DisplayLease lease)
    {
        ValidateLease(lease);
        var allPaths = ReadDisplayPaths(1);
        var activePaths = allPaths.Where(p => p.IsActive).ToArray();
        var identity = allPaths.Where(p => MatchesLease(p, lease)).ToArray();
        if (identity.Length == 0)
            return new(false, false, "无法重新确认原虚拟屏的设备身份，未更改显示设置。");
        var selectedPaths = activePaths.Where(p => Same(p.SourceName, lease.DeviceName)).ToArray();
        var before = ReadDesktopLayout();
        var current = before.SingleOrDefault(d => Same(d.DeviceName, lease.DeviceName));
        if (current is null && !identity.Any(p => p.IsActive))
            return new(true, false, "此虚拟副屏已从活动桌面断开。");
        if (current is null || current.IsPrimary || selectedPaths.Length != 1 ||
            !MatchesLease(selectedPaths[0], lease) || selectedPaths[0].IsCloned ||
            !before.Any(d => d.IsPrimary && !Same(d.DeviceName, lease.DeviceName)))
            return new(false, false, "副屏已成为主屏、镜像屏或设备身份发生变化，未更改显示设置。");

        var target = GetTargets().SingleOrDefault(t => t.TargetKey == TargetKey(lease));
        if (target is null) return new(false, false, "原虚拟目标已经变化，未断开副屏。");
        // Legacy ChangeDisplaySettingsEx may collapse other IddCx outputs when
        // detaching a member of a multi-monitor pool. Remove exactly this CCD
        // target while preserving every remaining source mode and target path.
        return DetachTargetCore(target, lease);
    }

    private static DisplayChangeResult RestoreCore(DisplayLease lease)
    {
        ValidateLease(lease);
        var allPaths = ReadDisplayPaths(1);
        var identity = allPaths.Where(p => MatchesLease(p, lease)).ToArray();
        if (identity.Length == 0)
            return new(false, false, "原虚拟屏已被重新枚举或移除，请刷新并重新选择；未更改任何显示器。");
        var before = ReadDesktopLayout();
        var current = before.SingleOrDefault(d => Same(d.DeviceName, lease.DeviceName));
        if (current is not null)
        {
            var active = allPaths.Where(p => p.IsActive && Same(p.SourceName, lease.DeviceName)).ToArray();
            return !current.IsPrimary && active.Length == 1 && MatchesLease(active[0], lease) && !active[0].IsCloned
                ? new(true, false, "原虚拟副屏已经启用。")
                : new(false, false, "原虚拟屏当前不是独立副屏，未更改显示设置。");
        }
        if (identity.Any(p => p.IsActive) || !LayoutsEqual(before, lease.OtherDisplays))
            return new(false, false, "其他显示器布局已变化，无法安全恢复原副屏位置。请在 Windows 显示设置中重新启用副屏。");
        if (!before.Any(d => d.IsPrimary) || !IsAdjacentWithoutOverlap(lease.OriginalMode, before))
            return new(false, false, "原副屏位置不再与现有桌面相邻，未更改显示设置。");

        // Legacy ChangeDisplaySettingsEx can change an active IddCx output but
        // rejects reattaching this driver on Windows 11. Supply every existing
        // active CCD path unchanged and append only the proven leased target.
        // No topology preset, ALLOW_CHANGES or SAVE_TO_DATABASE is used.
        _ = GetRestorableNativeMode(lease.OriginalMode);
        var restore = BuildRestoreConfiguration(lease);
        var test = SetDisplayConfig((uint)restore.Paths.Length, restore.Paths, (uint)restore.Modes.Length, restore.Modes, 0x60 | SdcVirtualModeAware);
        if (test != 0) return new(false, false, "测试恢复虚拟副屏失败（Windows CCD 返回码 " + test + "）。");
        if (!LayoutsEqual(before, ReadDesktopLayout()) || !ReadDisplayPaths(1).Any(p => MatchesLease(p, lease)) ||
            restore.ActiveIdentity != ActiveIdentity(ReadActivePaths().Select(p => PathIdentity(p.AdapterId, p.SourceId, p.TargetAdapterId, p.TargetId))))
            return new(false, false, "显示器在恢复前发生变化，请重试。");
        var result = SetDisplayConfig((uint)restore.Paths.Length, restore.Paths, (uint)restore.Modes.Length, restore.Modes, 0xa0 | SdcVirtualModeAware);
        if (result != 0) return new(false, false, "恢复虚拟副屏失败（Windows CCD 返回码 " + result + "）。");
        IReadOnlyList<DisplayDesktopState> after;
        ActiveDisplay[] activeRecovered;
        try
        {
            after = WaitForOwnState(lease.DeviceName, true);
            activeRecovered = ReadActivePaths().Where(p => Same(p.SourceName, lease.DeviceName)).ToArray();
        }
        catch (Exception ex) { return new(false, true, "已提交恢复请求，但验证当前桌面失败：" + ex.Message); }
        var recovered = after.SingleOrDefault(d => Same(d.DeviceName, lease.DeviceName));
        if (recovered is null || recovered.IsPrimary || activeRecovered.Length != 1 ||
            !MatchesLease(activeRecovered[0], lease) || activeRecovered[0].IsCloned ||
            recovered.Width != lease.OriginalMode.Width || recovered.Height != lease.OriginalMode.Height ||
            recovered.X != lease.OriginalMode.X || recovered.Y != lease.OriginalMode.Y || recovered.Frequency != lease.OriginalMode.Frequency ||
            !LayoutsEqual(before, after.Where(d => !Same(d.DeviceName, lease.DeviceName))))
            return new(false, true, "Windows 接受了恢复请求，但副屏或其他屏幕布局未通过验证；请停止连接并检查显示设置。");
        return new(true, true, "已恢复原虚拟副屏，主屏和其他显示器保持原布局。");
    }

    private static bool MatchesLease(ActiveDisplay path, DisplayLease lease) =>
        path.IsMttDriver && path.IsMttMonitor && Same(path.SourceName, lease.DeviceName) &&
        Same(path.AdapterInstanceId, lease.AdapterInstanceId) && Same(path.MonitorPath, lease.MonitorPath) &&
        path.AdapterId == new LUID(lease.AdapterLowPart, lease.AdapterHighPart) && path.SourceId == lease.SourceId &&
        path.TargetAdapterId == new LUID(lease.TargetAdapterLowPart, lease.TargetAdapterHighPart) && path.TargetId == lease.TargetId;

    private sealed record RestoreConfiguration(DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes, string ActiveIdentity);
    private sealed record NativeDisplaySnapshot(DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes);

    private static string NativeActiveState(NativeDisplaySnapshot snapshot, IReadOnlySet<string>? movable = null) => string.Join("|", snapshot.Paths.Select(path =>
    {
        var index = (path.flags & PathSupportVirtualMode) != 0 ? path.sourceInfo.modeInfoIdx >> 16 : path.sourceInfo.modeInfoIdx;
        if (index >= snapshot.Modes.Length) throw new InvalidOperationException("活动 CCD 路径缺少源模式。");
        var mode = snapshot.Modes[index];
        if (mode.infoType != 1 || mode.id != path.sourceInfo.id || mode.adapterId != path.sourceInfo.adapterId)
            throw new InvalidOperationException("活动 CCD 源模式身份不匹配。");
        var key = PathIdentity(path.sourceInfo.adapterId, path.sourceInfo.id, path.targetInfo.adapterId, path.targetInfo.id);
        var allowPosition = movable?.Contains(key) == true;
        return key +
            $":{(allowPosition ? 0 : mode.sourceX)},{(allowPosition ? 0 : mode.sourceY)},{mode.sourceWidth},{mode.sourceHeight},{mode.sourcePixelFormat}" +
            $":{path.targetInfo.rotation},{path.targetInfo.scaling},{path.targetInfo.refreshRate.Numerator},{path.targetInfo.refreshRate.Denominator},{path.targetInfo.scanLineOrdering}";
    }).Order(StringComparer.Ordinal));

    internal static bool IsOnlyOwnedPositionChange(IReadOnlyList<DisplayDesktopState> before,
        IReadOnlyList<DisplayDesktopState> after, IReadOnlySet<string> movableNames)
    {
        if (before.Count != after.Count || before.Select(d => d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != before.Count ||
            after.Select(d => d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != after.Count) return false;
        foreach (var original in before)
        {
            var current = after.SingleOrDefault(d => Same(d.DeviceName, original.DeviceName));
            if (current is null || current.X is < -65536 or > 65536 || current.Y is < -65536 or > 65536) return false;
            if (movableNames.Contains(original.DeviceName) && !original.IsPrimary)
            { if (current with { X = original.X, Y = original.Y } != original) return false; }
            else if (current != original) return false;
        }
        for (var i = 0; i < after.Count; i++)
            for (var j = i + 1; j < after.Count; j++)
                if (Math.Min((long)after[i].X + after[i].Width, (long)after[j].X + after[j].Width) > Math.Max(after[i].X, after[j].X) &&
                    Math.Min((long)after[i].Y + after[i].Height, (long)after[j].Y + after[j].Height) > Math.Max(after[i].Y, after[j].Y)) return false;
        return true;
    }

    private static void SaveDetachDelta(VirtualDisplayTarget removed, NativeDisplaySnapshot beforeCcd,
        NativeDisplaySnapshot afterCcd, IReadOnlyList<DisplayDesktopState> beforeGdi,
        IReadOnlyList<DisplayDesktopState> afterGdi, bool acceptedPositionOnly)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "display-detach-delta-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow, removed.TargetKey, acceptedPositionOnly,
                beforeCcd = NativeActiveState(beforeCcd).Split('|'), afterCcd = NativeActiveState(afterCcd).Split('|'),
                ccdFieldOrder = "sourceLuid:sourceId/targetLuid:targetId:x,y,width,height,pixelFormat:rotation,scaling,refreshNumerator,refreshDenominator,scanline",
                beforeGdi, afterGdi
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Diagnostics must not turn a completed detach into a second operation. */ }
    }

    private static NativeDisplaySnapshot ReadNativeSnapshot(uint flags)
    {
        flags |= QdcVirtualModeAware;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != 0 || pathCount > 1024 || modeCount > 4096) throw new InvalidOperationException("无法读取原始 CCD 显示路径。");
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == 122) continue;
            if (error != 0) throw new InvalidOperationException("无法读取原始 CCD 显示模式（" + error + "）。");
            return new(paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
        }
        throw new InvalidOperationException("CCD 拓扑持续变化，未操作显示器。");
    }

    private static string PathIdentity(LUID sourceAdapter, uint source, LUID targetAdapter, uint target) =>
        $"{sourceAdapter.HighPart}:{sourceAdapter.LowPart}:{source}/{targetAdapter.HighPart}:{targetAdapter.LowPart}:{target}";
    private static string ActiveIdentity(IEnumerable<string> identities) => string.Join("|", identities.Order(StringComparer.Ordinal));

    private static RestoreConfiguration BuildRestoreConfiguration(DisplayLease lease)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(1 | QdcVirtualModeAware, out var pathCount, out var modeCount);
            if (error != 0 || pathCount > 1024 || modeCount > 4096)
                throw new InvalidOperationException("无法读取原始显示路径，未恢复副屏。");
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            error = QueryDisplayConfig(1 | QdcVirtualModeAware, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == 122) continue;
            if (error != 0) throw new InvalidOperationException("无法读取原始显示模式（" + error + "）。");
            var sourceAdapter = new LUID(lease.AdapterLowPart, lease.AdapterHighPart);
            var targetAdapter = new LUID(lease.TargetAdapterLowPart, lease.TargetAdapterHighPart);
            var own = paths.Take((int)pathCount).Where(p => p.sourceInfo.adapterId == sourceAdapter && p.sourceInfo.id == lease.SourceId &&
                p.targetInfo.adapterId == targetAdapter && p.targetInfo.id == lease.TargetId).ToArray();
            if (own.Length != 1 || (own[0].flags & 1) != 0 || !own[0].targetInfo.targetAvailable)
                throw new InvalidOperationException("原虚拟目标不是唯一可用的未活动路径，未恢复副屏。");
            var active = paths.Take((int)pathCount).Where(p => (p.flags & 1) != 0).ToList();
            if (active.Count == 0 || active.Any(p => p.sourceInfo.adapterId == sourceAdapter && p.sourceInfo.id == lease.SourceId))
                throw new InvalidOperationException("原虚拟源已被其他活动路径使用，未恢复副屏。");
            var suppliedModes = modes.Take((int)modeCount).ToList();
            var activeIdentity = ActiveIdentity(active.Select(p => PathIdentity(p.sourceInfo.adapterId, p.sourceInfo.id, p.targetInfo.adapterId, p.targetInfo.id)));
            var selected = own[0];
            selected.flags |= 1;
            selected.sourceInfo.modeInfoIdx = EncodeSourceModeIndex(selected.flags, (uint)suppliedModes.Count);
            selected.targetInfo.modeInfoIdx = uint.MaxValue;
            selected.targetInfo.rotation = lease.OriginalMode.Orientation + 1;
            selected.targetInfo.scaling = 1; // DISPLAYCONFIG_SCALING_IDENTITY.
            selected.targetInfo.refreshRate = new() { Numerator = lease.OriginalMode.Frequency, Denominator = 1 };
            // A specified refresh rate requires a specified scan-line ordering.
            // UNSPECIFIED with nonzero refresh returns ERROR_INVALID_PARAMETER.
            selected.targetInfo.scanLineOrdering = 1; // Progressive.
            suppliedModes.Add(new DISPLAYCONFIG_MODE_INFO
            {
                infoType = 1, id = lease.SourceId, adapterId = sourceAdapter,
                sourceWidth = (uint)lease.OriginalMode.Width, sourceHeight = (uint)lease.OriginalMode.Height,
                sourcePixelFormat = 4, sourceX = lease.OriginalMode.X, sourceY = lease.OriginalMode.Y
            });
            active.Add(selected);
            return new(active.ToArray(), suppliedModes.ToArray(), activeIdentity);
        }
        throw new InvalidOperationException("显示拓扑持续变化，未恢复副屏。");
    }

    internal static uint EncodeSourceModeIndex(uint pathFlags, uint modeIndex)
    {
        if ((pathFlags & PathSupportVirtualMode) == 0) return modeIndex;
        if (modeIndex >= 0xffff) throw new ArgumentOutOfRangeException(nameof(modeIndex));
        // Virtual-aware union: low 16 bits cloneGroupId INVALID; high 16 bits
        // sourceModeInfoIdx. Target's two mode indices stay INVALID (0xffffffff).
        return (modeIndex << 16) | 0xffff;
    }

    private static void ValidateLease(DisplayLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLifecycleInterop();
        if (lease.LeaseId == Guid.Empty || string.IsNullOrWhiteSpace(lease.DeviceName) ||
            string.IsNullOrWhiteSpace(lease.AdapterInstanceId) ||
            lease.MonitorPath?.Contains(@"DISPLAY#MTT1337#", StringComparison.OrdinalIgnoreCase) != true ||
            lease.OriginalMode is null || lease.OriginalMode.IsPrimary ||
            !Same(lease.OriginalMode.DeviceName, lease.DeviceName) ||
            lease.OriginalMode.Width is < 1 or > 16384 || lease.OriginalMode.Height is < 1 or > 16384 ||
            lease.OriginalMode.X is < -65536 or > 65536 || lease.OriginalMode.Y is < -65536 or > 65536 ||
            lease.OtherDisplays is null || lease.OtherDisplays.Count == 0 || lease.OtherDisplays.Count > 64 ||
            lease.OtherDisplays.Any(d => d is null || Same(d.DeviceName, lease.DeviceName)))
            throw new InvalidOperationException("虚拟副屏会话记录无效，未更改显示设置。");
    }

    private static DisplayChangeResult WithTopologyLock(Func<DisplayChangeResult> action)
    {
        using var mutex = new Mutex(false, TopologyMutex);
        var locked = false;
        try
        {
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { locked = true; }
            return locked ? action() : new(false, false, "另一个副屏操作尚未结束，请稍后重试。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { return new(false, false, ex.Message); }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    private static IReadOnlyList<DisplayDesktopState> ReadDesktopLayout()
    {
        var result = new List<DisplayDesktopState>();
        for (uint index = 0; index < 128; index++)
        {
            var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & 1) == 0) continue; // Attached to desktop.
            var mode = NewDevMode();
            if (!EnumDisplaySettingsEx(device.DeviceName, -1, ref mode, 0))
                throw new InvalidOperationException("无法读取活动显示器的当前模式：" + device.DeviceName);
            result.Add(new(device.DeviceName, mode.dmPositionX, mode.dmPositionY, checked((int)mode.dmPelsWidth), checked((int)mode.dmPelsHeight),
                mode.dmBitsPerPel, mode.dmDisplayFrequency, mode.dmDisplayOrientation, mode.dmDisplayFlags, mode.dmDisplayFixedOutput,
                (device.StateFlags & 4) != 0));
        }
        if (result.Count == 0) throw new InvalidOperationException("无法读取当前桌面，未更改显示设置。");
        return result;
    }

    private static IReadOnlyList<DisplayDesktopState> WaitForOwnState(string name, bool attached)
    {
        var layout = ReadDesktopLayout();
        for (var attempt = 0; attempt < 20 && layout.Any(d => Same(d.DeviceName, name)) != attached; attempt++)
        {
            Thread.Sleep(100);
            layout = ReadDesktopLayout();
        }
        return layout;
    }

    private static bool LayoutsEqual(IEnumerable<DisplayDesktopState> a, IEnumerable<DisplayDesktopState> b) =>
        a.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).SequenceEqual(b.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase));

    private static bool IsAdjacentWithoutOverlap(DisplayDesktopState own, IEnumerable<DisplayDesktopState> others)
    {
        var adjacent = false;
        foreach (var other in others)
        {
            var overlapX = Math.Min((long)own.X + own.Width, (long)other.X + other.Width) - Math.Max(own.X, other.X);
            var overlapY = Math.Min((long)own.Y + own.Height, (long)other.Y + other.Height) - Math.Max(own.Y, other.Y);
            if (overlapX > 0 && overlapY > 0) return false;
            adjacent |= overlapX == 0 && overlapY > 0 || overlapY == 0 && overlapX > 0;
        }
        return adjacent;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static DisplayChangeResult NativeFailure(string action, int code) => new(false, code == 1,
        action + "失败（Windows 显示返回码 " + code + "）。" + (code == 1 ? "Windows 要求重启；程序不会自动重启。" : ""));

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
    private static DEVMODE GetRestorableNativeMode(DisplayDesktopState saved)
    {
        // Re-enumerate the detached device in this process before requesting a
        // mode. Windows/IddCx may reject a reconstructed DEVMODE on a newly
        // started process. The documented EnumDisplaySettingsEx result includes
        // the driver header and only fields actually supported by the device.
        // Starting at index zero also initializes Windows' mode enumeration.
        for (var index = 0; index < 4096; index++)
        {
            var native = NewDevMode();
            if (!EnumDisplaySettingsEx(saved.DeviceName, index, ref native, 4)) break; // EDS_ROTATEDMODE.
            if (native.dmPelsWidth != saved.Width || native.dmPelsHeight != saved.Height ||
                native.dmBitsPerPel != saved.BitsPerPixel || native.dmDisplayFrequency != saved.Frequency ||
                native.dmDisplayOrientation != saved.Orientation || native.dmDisplayFlags != saved.DisplayFlags)
                continue;
            if (native.dmDriverExtra != 0)
                throw new InvalidOperationException("虚拟显示驱动要求专用模式数据，未尝试重建或恢复显示模式。");
            native.dmFields |= DmPosition;
            native.dmPositionX = saved.X;
            native.dmPositionY = saved.Y;
            return native;
        }
        throw new InvalidOperationException("原虚拟副屏的显示模式已不在驱动支持列表中，未更改显示设置。");
    }

    internal static void ValidateLifecycleInterop()
    {
        if (Marshal.SizeOf<DEVMODE>() != 220 || Marshal.OffsetOf<DEVMODE>(nameof(DEVMODE.dmPelsWidth)).ToInt32() != 172 ||
            Marshal.SizeOf<DISPLAY_DEVICE>() != 840 || Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>() != 72 ||
            Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>() != 64)
            throw new InvalidOperationException("显示器控制结构大小不匹配，无法安全更改显示设置。");
    }

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DEVMODE
    {
        [FieldOffset(64)] public ushort dmSpecVersion;
        [FieldOffset(66)] public ushort dmDriverVersion;
        [FieldOffset(68)] public ushort dmSize;
        [FieldOffset(70)] public ushort dmDriverExtra;
        [FieldOffset(72)] public uint dmFields;
        [FieldOffset(76)] public int dmPositionX;
        [FieldOffset(80)] public int dmPositionY;
        [FieldOffset(84)] public uint dmDisplayOrientation;
        [FieldOffset(88)] public uint dmDisplayFixedOutput;
        [FieldOffset(168)] public uint dmBitsPerPel;
        [FieldOffset(172)] public uint dmPelsWidth;
        [FieldOffset(176)] public uint dmPelsHeight;
        [FieldOffset(180)] public uint dmDisplayFlags;
        [FieldOffset(184)] public uint dmDisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint deviceIndex, ref DISPLAY_DEVICE device, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, ref DEVMODE mode, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE mode, IntPtr hwnd, uint flags, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] DISPLAYCONFIG_PATH_INFO[] paths, uint modeCount, [In] DISPLAYCONFIG_MODE_INFO[] modes, uint flags);
}
