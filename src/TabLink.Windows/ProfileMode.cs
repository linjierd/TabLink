using System.Drawing;

namespace TabLink.Windows;

public sealed record DisplayMode(int Width, int Height, int RefreshRate, uint Orientation);

public static partial class VirtualDisplayManager
{
    // Cheap physical-pixel check for each encoded access unit. The caller keeps
    // the separate bounded-interval CCD/SetupAPI identity and primary check.
    public static bool HasCurrentBounds(string deviceName, Rectangle bounds)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || bounds.Width <= 0 || bounds.Height <= 0) return false;
        var native = NewDevMode();
        return EnumDisplaySettingsEx(deviceName, -1, ref native, 0) &&
            native.dmPositionX == bounds.X && native.dmPositionY == bounds.Y &&
            native.dmPelsWidth == bounds.Width && native.dmPelsHeight == bounds.Height;
    }

    public static IReadOnlyList<DisplayMode> GetSupportedModes(DisplayLease lease)
    {
        ValidateLease(lease);
        if (!ReadDisplayPaths(1).Any(path => MatchesLease(path, lease)))
            throw new InvalidOperationException("虚拟副屏身份已变化，无法读取原会话的模式列表。");
        var result = new List<DisplayMode>();
        for (var index = 0; index < 4096; index++)
        {
            var native = NewDevMode();
            if (!EnumDisplaySettingsEx(lease.DeviceName, index, ref native, 4)) break;
            if (native.dmBitsPerPel != 32) continue;
            result.Add(new(checked((int)native.dmPelsWidth), checked((int)native.dmPelsHeight),
                checked((int)native.dmDisplayFrequency), native.dmDisplayOrientation));
        }
        return result.Distinct().ToArray();
    }

    public static DisplayChangeResult SetMode(DisplayLease lease, int width, int height, int refreshRate) =>
        WithTopologyLock(() => SetModeCore(lease, width, height, refreshRate));

    private static DisplayChangeResult SetModeCore(DisplayLease lease, int width, int height, int refreshRate)
    {
        ValidateLease(lease);
        if (width is < 320 or > 7680 || height is < 320 or > 7680 || (long)width * height > 33_177_600 || refreshRate is < 30 or > 240)
            return new(false, false, "请求的虚拟屏尺寸或刷新率超出支持范围。");
        var paths = ReadActivePaths().Where(p => Same(p.SourceName, lease.DeviceName)).ToArray();
        var before = ReadDesktopLayout();
        var own = before.SingleOrDefault(d => Same(d.DeviceName, lease.DeviceName));
        if (own is null || own.IsPrimary || paths.Length != 1 || !MatchesLease(paths[0], lease) || paths[0].IsCloned ||
            !before.Any(d => d.IsPrimary && !Same(d.DeviceName, lease.DeviceName)))
            return new(false, false, "原虚拟屏不是活动的独立非主屏，未更改显示模式。");
        if (own.Width == width && own.Height == height && own.Frequency == refreshRate)
            return new(true, false, "虚拟副屏已使用请求的分辨率和刷新率。");
        var target = own with { Width = width, Height = height, Frequency = (uint)refreshRate };
        if (!IsAdjacentWithoutOverlap(target, before.Where(d => !Same(d.DeviceName, lease.DeviceName))))
            return new(false, false, "请求的副屏尺寸会覆盖其他显示器或脱离桌面边缘，未更改显示模式。");

        // Prefer a native portrait/landscape mode. When the driver only exposes
        // rotation, EDS_ROTATEDMODE also allows an exact rotated mode as fallback.
        var options = GetSupportedModes(lease).Where(m => m.Width == width && m.Height == height && m.RefreshRate == refreshRate)
            .OrderBy(m => m.Orientation == 0 ? 0 : 1).ThenBy(m => m.Orientation).ToArray();
        if (options.Length == 0)
            return new(false, false, "驱动尚未提供 " + width + " × " + height + " @ " + refreshRate + " Hz，请先添加此平板的显示模式。");
        target = target with { Orientation = options[0].Orientation };
        var native = GetRestorableNativeMode(target);
        var test = ChangeDisplaySettingsEx(lease.DeviceName, ref native, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != 0) return NativeFailure("测试副屏显示模式", test);
        if (!LayoutsEqual(before, ReadDesktopLayout()) || !ReadActivePaths().Any(p => MatchesLease(p, lease) && !p.IsCloned))
            return new(false, false, "显示器在应用模式前发生变化，请重试。");
        var code = ChangeDisplaySettingsEx(lease.DeviceName, ref native, IntPtr.Zero, 0, IntPtr.Zero);
        if (code != 0) return NativeFailure("设置副屏显示模式", code);
        try
        {
            var after = ReadDesktopLayout();
            var changed = after.SingleOrDefault(d => Same(d.DeviceName, lease.DeviceName));
            if (changed is null || changed.IsPrimary || changed.X != own.X || changed.Y != own.Y ||
                changed.Width != width || changed.Height != height || changed.Frequency != refreshRate ||
                !LayoutsEqual(before.Where(d => !Same(d.DeviceName, lease.DeviceName)), after.Where(d => !Same(d.DeviceName, lease.DeviceName))))
                return new(false, true, "Windows 接受了模式请求，但结果未通过验证；请停止连接并检查显示设置。");
            return new(true, true, "虚拟副屏已设为 " + width + " × " + height + " @ " + refreshRate + " Hz，其他显示器保持原布局。");
        }
        catch (Exception ex) { return new(false, true, "已提交模式请求，但验证失败：" + ex.Message); }
    }
}
