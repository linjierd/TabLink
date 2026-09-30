using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TabLink.DriverSetup;

internal static class TabletPreparer
{
    private const string RequiredCompatibleId = @"USB\Class_ff&SubClass_42&Prot_01";
    private const string AdbInterfaceGuid = "{F72FE0D4-CBCB-407D-8814-9ED673D0DD6B}";
    private const string ParametersName = "Device Parameters";
    private const string ValueName = "DeviceInterfaceGUIDs";
    private const int MaximumDeviceInstanceIdLength = 200;

    internal static InstallResult Prepare(string childId, string parentId)
    {
        (childId, parentId) = ValidateTarget(childId, parentId);
        // The caller must name one exact USB child and its exact current USB
        // parent. The checks below still require a present WINUSB ADB interface.
        var before = ReadAndValidate(childId, parentId);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, $"tablet-interface-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(stream, before, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (before.OriginalValues.Any(IsAdbGuid))
            return new(true, "tabletAlreadyPrepared", "这台平板已经注册 ADB 接口 GUID，未修改或重启任何设备。未安装虚拟副屏。备份：" + backup, childId, false, DateTimeOffset.UtcNow);

        var expected = before.OriginalValues.Append(AdbInterfaceGuid).ToArray();
        var changed = false;
        try
        {
            var current = ReadAndValidate(childId, parentId);
            if (!SameOriginalState(before, current))
                throw new InvalidOperationException("平板接口注册信息在备份后发生变化，未修改任何设备。");
            using (var device = Registry.LocalMachine.OpenSubKey(DeviceKeyPath(childId), true) ?? throw new InvalidOperationException("平板设备注册记录已消失。"))
            using (var parameters = device.CreateSubKey(ParametersName, true))
            {
                parameters.SetValue(ValueName, expected, RegistryValueKind.MultiString);
                changed = true;
                parameters.Flush();
            }
            // Ensure the exact child remains present and is still under the exact
            // verified parent before asking Windows to restart only that interface.
            ReadAndValidate(childId, parentId);
            var restarted = PnpCommand.Run("/restart-device", childId);
            var after = ReadAndValidate(childId, parentId);
            if (!after.OriginalValues.Any(IsAdbGuid))
                throw new InvalidOperationException("重启后 ADB 接口 GUID 未保留。");
            return new(true, "tabletPrepared", "已补齐这台已核实平板的 ADB 接口 GUID，并只刷新了它的 ADB 子接口。未安装或开启虚拟副屏，也未重启父 USB 或其他设备。\n备份：" + backup + (restarted.RebootRequired ? "\nWindows 要求重启后生效，请保存工作后自行重启。" : ""), childId, restarted.RebootRequired, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            var rollback = "未写入 GUID。";
            if (changed)
            {
                try
                {
                    Restore(childId, parentId, before, expected);
                    rollback = "已恢复 GUID 的原始值或原来不存在的状态。";
                    try { ReadAndValidate(childId, parentId); PnpCommand.Run("/restart-device", childId); }
                    catch (Exception restart) { rollback += " 仅此 ADB 子接口的刷新未成功：" + restart.Message; }
                }
                catch (Exception restore) { rollback = "恢复原值失败：" + restore.Message; }
            }
            return new(false, "tabletPreparationFailed", error.Message + "\n" + rollback + "\n原值备份：" + backup + "\n未安装虚拟副屏。", childId, false, DateTimeOffset.UtcNow);
        }
    }

    // Exposed only internally for read-only integration validation. It never
    // opens a writable registry handle and performs no PnP operations.
    internal static Snapshot ReadAndValidate(string childId, string parentId)
    {
        (childId, parentId) = ValidateTarget(childId, parentId);
        var node = GetExactPresentDevInst(childId);
        CheckCm(CM_Get_Parent(out var parent, node, 0), "读取平板父设备关系");
        var parentText = new StringBuilder(1024);
        CheckCm(CM_Get_Device_ID(parent, parentText, parentText.Capacity, 0), "读取平板父设备标识");
        if (!parentText.ToString().Equals(parentId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ADB 子接口的当前父设备与显式指定的 USB 父设备不匹配，已停止。");
        using var device = Registry.LocalMachine.OpenSubKey(DeviceKeyPath(childId), false) ?? throw new InvalidOperationException("找不到已核实平板的设备记录。");
        if (!string.Equals(device.GetValue("Service") as string, "WINUSB", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("平板子接口当前未使用 WINUSB，已停止。");
        var compatible = device.GetValue("CompatibleIDs") as string[] ?? [];
        if (!compatible.Contains(RequiredCompatibleId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("平板子接口没有已核实的 ADB 兼容标识，已停止。");
        using var parameters = device.OpenSubKey(ParametersName, false);
        var exists = parameters?.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase) == true;
        if (exists && parameters!.GetValueKind(ValueName) != RegistryValueKind.MultiString)
            throw new InvalidOperationException("现有 DeviceInterfaceGUIDs 不是 REG_MULTI_SZ，为保留现有数据已停止。");
        var values = exists ? parameters!.GetValue(ValueName) as string[] ?? throw new InvalidDataException("无法读取现有接口 GUID 多字符串。") : [];
        return new(childId, parentId, "WINUSB", RequiredCompatibleId, parameters is not null, exists, exists ? "REG_MULTI_SZ" : null, values, DateTimeOffset.UtcNow);
    }

    private static void Restore(string childId, string parentId, Snapshot original, string[] expectedCurrent)
    {
        ReadAndValidate(childId, parentId);
        using var device = Registry.LocalMachine.OpenSubKey(DeviceKeyPath(childId), true) ?? throw new InvalidOperationException("原设备注册记录已消失。");
        using (var parameters = device.OpenSubKey(ParametersName, true) ?? throw new InvalidOperationException("原设备参数记录已消失。"))
        {
            var values = parameters.GetValue(ValueName) as string[];
            if (parameters.GetValueKind(ValueName) != RegistryValueKind.MultiString || values is null || !values.SequenceEqual(expectedCurrent))
                throw new InvalidOperationException("GUID 值已被其他程序更改，为避免覆盖其更改已停止回滚。");
            if (original.ValueExisted) parameters.SetValue(ValueName, original.OriginalValues, RegistryValueKind.MultiString);
            else parameters.DeleteValue(ValueName, false);
            parameters.Flush();
        }
        if (!original.ParametersKeyExisted)
        {
            using var parameters = device.OpenSubKey(ParametersName, false);
            if (parameters is not null && parameters.ValueCount == 0 && parameters.SubKeyCount == 0)
            {
                parameters.Close();
                device.DeleteSubKey(ParametersName, false);
            }
        }
    }

    private static bool SameOriginalState(Snapshot a, Snapshot b) =>
        a.ChildInstanceId.Equals(b.ChildInstanceId, StringComparison.OrdinalIgnoreCase) &&
        a.ParentInstanceId.Equals(b.ParentInstanceId, StringComparison.OrdinalIgnoreCase) &&
        a.ParametersKeyExisted == b.ParametersKeyExisted && a.ValueExisted == b.ValueExisted &&
        a.OriginalValues.SequenceEqual(b.OriginalValues);
    private static bool IsAdbGuid(string value) => Guid.TryParse(value, out var parsed) && parsed == Guid.Parse(AdbInterfaceGuid);

    private static (string ChildId, string ParentId) ValidateTarget(string? childId, string? parentId)
    {
        var child = ValidateUsbInstanceId(childId, "child-id");
        var parent = ValidateUsbInstanceId(parentId, "parent-id");
        if (child.Equals(parent, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ADB 子接口和 USB 父设备不能是同一个实例 ID。");
        return (child, parent);
    }

    private static string ValidateUsbInstanceId(string? value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumDeviceInstanceIdLength || value != value.Trim())
            throw new ArgumentException($"{argumentName} 必须是完整的 USB 设备实例 ID。", argumentName);
        var parts = value.Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            parts.Skip(1).Any(part => part.Length == 0 || !char.IsAsciiLetterOrDigit(part[0]) ||
                part.Any(character => !IsSafeUsbInstanceIdCharacter(character))))
            throw new ArgumentException($"{argumentName} 必须是 USB\\设备ID\\实例ID，且不能包含通配符、路径片段或控制字符。", argumentName);
        return value;
    }

    private static bool IsSafeUsbInstanceIdCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '&' or '_' or '-' or '.' or '+' or '{' or '}';

    private static string DeviceKeyPath(string childId) => @"SYSTEM\CurrentControlSet\Enum\" + childId;

    private static uint GetExactPresentDevInst(string childId)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 2 | 4); // PRESENT | ALLCLASSES
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前 USB 设备。");
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error, "读取 USB 设备失败。");
                }
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 USB 实例标识失败。");
                if (id.ToString().Equals(childId, StringComparison.OrdinalIgnoreCase)) return info.DevInst;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        throw new InvalidOperationException("未发现当前已连接的指定平板 ADB 子接口；未修改任何设备。");
    }

    private static void CheckCm(uint code, string action) { if (code != 0) throw new InvalidOperationException(action + $"失败（CM 0x{code:X}）。"); }
    internal sealed record Snapshot(string ChildInstanceId, string ParentInstanceId, string Service, string RequiredCompatibleId, bool ParametersKeyExisted, bool ValueExisted, string? OriginalKind, string[] OriginalValues, DateTimeOffset Timestamp);
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(IntPtr classId, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
}
