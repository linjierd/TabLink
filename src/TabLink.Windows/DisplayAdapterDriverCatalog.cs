using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TabLink.Windows;

internal sealed record DisplayAdapterDriverIdentity(string Description, uint VendorId, uint DeviceId,
    string? DriverVersion, string? ProviderName, string DeviceInstanceId);

internal static partial class DisplayAdapterDriverCatalog
{
    const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    internal static IReadOnlyList<DisplayAdapterDriverIdentity> Read(out string? warning)
    {
        warning = null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var displayClass = machine.OpenSubKey(DisplayClassPath, writable: false);
            if (displayClass is null)
            {
                warning = "Windows 未返回显示驱动目录";
                return [];
            }
            var result = new List<DisplayAdapterDriverIdentity>();
            foreach (var name in displayClass.GetSubKeyNames().OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var adapter = displayClass.OpenSubKey(name, writable: false);
                    if (adapter is null) continue;
                    var identity = Text(adapter.GetValue("MatchingDeviceId")) ?? Text(adapter.GetValue("HardwareID"));
                    var match = identity is null ? null : PciIdentity().Match(identity);
                    if (match is not { Success: true }) continue;
                    var vendor = uint.Parse(match.Groups["vendor"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var device = uint.Parse(match.Groups["device"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var description = Text(adapter.GetValue("DriverDesc")) ?? Text(adapter.GetValue("Device Description")) ?? identity!;
                    result.Add(new(description, vendor, device, Text(adapter.GetValue("DriverVersion")),
                        Text(adapter.GetValue("ProviderName")), identity!));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    warning ??= "部分显示驱动身份无权读取，已按可见设备继续检测";
                }
            }
            return result.DistinctBy(item => (item.VendorId, item.DeviceId, item.DriverVersion, item.Description),
                EqualityComparer<(uint, uint, string?, string)>.Default).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            warning = "无法读取显示驱动版本：" + error.Message;
            return [];
        }
    }

    static string? Text(object? value)
    {
        var text = value switch
        {
            string item => item,
            string[] items => items.FirstOrDefault(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length <= 512 ? text : text[..512];
    }

    [GeneratedRegex(@"(?:^|[\\&])VEN_(?<vendor>[0-9A-Fa-f]{4})&DEV_(?<device>[0-9A-Fa-f]{4})(?:&|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PciIdentity();
}
