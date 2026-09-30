using System.Drawing;

namespace TabLink.Windows;

internal sealed record DisplayRelocation(VirtualDisplayInfo CurrentDisplay, DisplayLease RememberedLease);
internal sealed class DisplayLayoutChangingException(string message):IOException(message);

public static partial class VirtualDisplayManager
{
    /// <summary>Resolves the same leased output at its current position; never changes display settings.</summary>
    /// <exception cref="IOException">Identity, active/extended status, or any mode field except X/Y changed.</exception>
    public static VirtualDisplayInfo ResolveCurrent(DisplayLease lease) => ResolveLayout(lease).CurrentDisplay;

    internal static DisplayRelocation ResolveLayout(DisplayLease lease)
    {
        try
        {
            ValidateLease(lease);
            var paths = ReadActivePaths();
            var layout = ReadDesktopLayout();
            var resolved = ResolveLayoutSnapshot(lease, paths, layout);
            // Recheck the path after reading GDI state, so a newly reused GDI name is not enough.
            _ = ResolveLayoutSnapshot(lease, ReadActivePaths(), layout);
            if (!HasCurrentBounds(resolved.CurrentDisplay.DeviceName, resolved.CurrentDisplay.Bounds))
                throw new DisplayLayoutChangingException("副屏位置正在变化，请重新读取同一设备。");
            return resolved;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { throw new IOException("无法确认原虚拟副屏的当前布局：" + ex.Message, ex); }
    }

    // Pure snapshot validation shared by the native reader and relocation regression tests.
    internal static DisplayRelocation ResolveLayoutSnapshot(DisplayLease lease,
        IReadOnlyList<ActiveDisplay> paths, IReadOnlyList<DisplayDesktopState> layout)
    {
        ValidateLease(lease);
        var selectedPaths = paths.Where(path => path.IsActive && Same(path.SourceName, lease.DeviceName)).ToArray();
        var currentModes = layout.Where(mode => Same(mode.DeviceName, lease.DeviceName)).ToArray();
        if (selectedPaths.Length != 1 || currentModes.Length != 1 || selectedPaths[0].IsCloned ||
            !MatchesLease(selectedPaths[0], lease))
            throw new IOException("原副屏已停用、处于镜像状态或设备身份改变，不能跟随此显示器。");
        var current = currentModes[0];
        if (current.IsPrimary || !layout.Any(mode => mode.IsPrimary && !Same(mode.DeviceName, lease.DeviceName)))
            throw new IOException("原副屏已成为主屏或没有独立主屏，不能继续采集。");
        if (current.X is < -65536 or > 65536 || current.Y is < -65536 or > 65536 ||
            current with { DeviceName = lease.OriginalMode.DeviceName, X = lease.OriginalMode.X, Y = lease.OriginalMode.Y } != lease.OriginalMode)
            throw new IOException("副屏分辨率、刷新率或显示模式已经改变；只允许自动跟随位置移动。");
        var path = selectedPaths[0];
        var info = new VirtualDisplayInfo(current.DeviceName,
            string.IsNullOrWhiteSpace(path.FriendlyName) ? current.DeviceName : path.FriendlyName,
            false, new Rectangle(current.X, current.Y, current.Width, current.Height), true);
        // This is a reconnect preference, not replacement ownership. Preserve the original LeaseId
        // and identity, while recording the user's current layout without applying it anywhere.
        var remembered = lease with
        {
            OriginalMode = lease.OriginalMode with { X = current.X, Y = current.Y },
            OtherDisplays = layout.Where(mode => !Same(mode.DeviceName, lease.DeviceName)).ToArray()
        };
        return new(info, remembered);
    }

    internal static bool RememberedLayoutEquals(DisplayLease left, DisplayLease right) =>
        SameLeaseIdentity(left, right) && left.OriginalMode == right.OriginalMode &&
        LayoutsEqual(left.OtherDisplays, right.OtherDisplays);

    // A freshly enumerated inactive target provides current authority. Only reuse a saved position
    // when that exact target and the other displays still match and the requested mode fits there.
    internal static DisplayLease ReuseRememberedPosition(DisplayLease fresh, DisplayLease remembered)
    {
        ValidateLease(fresh);
        ValidateLease(remembered);
        if (!SameLeaseIdentity(fresh, remembered) || !LayoutsEqual(fresh.OtherDisplays, remembered.OtherDisplays)) return fresh;
        var positioned = fresh.OriginalMode with { X = remembered.OriginalMode.X, Y = remembered.OriginalMode.Y };
        return IsAdjacentWithoutOverlap(positioned, fresh.OtherDisplays) ? fresh with { OriginalMode = positioned } : fresh;
    }

    static bool SameLeaseIdentity(DisplayLease left, DisplayLease right) =>
        Same(left.DeviceName, right.DeviceName) && Same(left.AdapterInstanceId, right.AdapterInstanceId) &&
        Same(left.MonitorPath, right.MonitorPath) && left.AdapterLowPart == right.AdapterLowPart &&
        left.AdapterHighPart == right.AdapterHighPart && left.SourceId == right.SourceId &&
        left.TargetAdapterLowPart == right.TargetAdapterLowPart && left.TargetAdapterHighPart == right.TargetAdapterHighPart &&
        left.TargetId == right.TargetId;
}
