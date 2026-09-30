using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TabLink.Core;

namespace TabLink.Windows;

public enum NetworkInterfaceKind { WiFi, Usb, Ethernet }

public sealed record NetworkInterfaceChoice(IPAddress LocalAddress, string InterfaceAlias, int PrefixLength,
    NetworkInterfaceKind Kind, string? UsbSerial, string InterfaceId, int InterfaceIndex)
{
    public const int Port = 27184;
    public IPEndPoint Endpoint => new(LocalAddress, Port);
    public string DisplayText => $"{Kind switch { NetworkInterfaceKind.WiFi => "Wi-Fi", NetworkInterfaceKind.Usb => "USB 网络", _ => "有线网络" }} · {InterfaceAlias} · {LocalAddress}" + (UsbSerial is null ? "" : $" · {UsbSerial}");
    public override string ToString() => DisplayText;
}

/// <summary>Read-only local network inventory. Never sends USB requests or changes network configuration.</summary>
public static partial class NetworkInterfaceCatalog
{
    public static List<NetworkInterfaceChoice> GetChoices(DevicePolicySettings settings)
    {
        DevicePolicy.ValidateSettings(settings);
        var policy = new DevicePolicy(settings);
        var adapters = ReadAdapterIdentities();
        var choices = new List<NetworkInterfaceChoice>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!Guid.TryParse(nic.Id, out var adapterGuid) || !adapters.TryGetValue(adapterGuid, out var adapter)) continue;
            if (string.IsNullOrWhiteSpace(adapter.InstanceId) || IsVirtualName(nic.Description) || IsVirtualName(nic.Name)) continue;

            NetworkInterfaceKind kind;
            string? serial = null;
            var usb = TryResolveUsbParent(adapter.InstanceId);
            if (usb is not null)
            {
                // DevicePolicy.Evaluate is a pure identity/exclusion validator. Its
                // historical AdbDevice DTO is used only as a data carrier here:
                // no adb command, device approval, or USB handle is involved.
                var decision = policy.Evaluate(new AdbDevice(usb.Serial, "device"), [usb]);
                if (!decision.Allowed) continue;
                kind = NetworkInterfaceKind.Usb;
                serial = usb.Serial;
            }
            else
            {
                // A USB adapter with an unresolvable/unstable parent must never be
                // relabelled as generic Ethernet to bypass exclusions.
                if (adapter.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (!adapter.Physical || !IsPhysicalBus(adapter.InstanceId)) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) kind = NetworkInterfaceKind.WiFi;
                else if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT) kind = NetworkInterfaceKind.Ethernet;
                else continue;
            }

            IPInterfaceProperties properties;
            int index;
            try { properties = nic.GetIPProperties(); index = properties.GetIPv4Properties().Index; }
            catch (NetworkInformationException) { continue; } // Adapter disappeared during enumeration.
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!IsUsableAddress(unicast.Address) || unicast.PrefixLength is < 1 or > 32) continue;
                if (unicast.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Duplicate or DuplicateAddressDetectionState.Invalid or DuplicateAddressDetectionState.Tentative) continue;
                choices.Add(new(unicast.Address, nic.Name, unicast.PrefixLength, kind, serial, nic.Id, index));
            }
        }
        return choices.DistinctBy(x => (x.InterfaceId, x.LocalAddress)).OrderBy(x => x.Kind == NetworkInterfaceKind.Usb ? 0 : x.Kind == NetworkInterfaceKind.WiFi ? 1 : 2)
            .ThenBy(x => x.InterfaceAlias, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.LocalAddress.ToString(), StringComparer.Ordinal).ToList();
    }

    internal static bool IsUsableAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsPhysicalBus(string id) => id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("PCMCIA\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualName(string value) => new[] { "Hyper-V", "vEthernet", "VMware", "VirtualBox", "Loopback", "Wintun", "sing-tun", "singbox", "TAP-Windows", "VPN", "WireGuard", "Tailscale", "ZeroTier", "Virtual Ethernet" }
        .Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase));

    private sealed record AdapterIdentity(string InstanceId, bool Physical);

    private static Dictionary<Guid, AdapterIdentity> ReadAdapterIdentities()
    {
        var records = new Dictionary<Guid, AdapterIdentity>();
        object? locator = null, service = null, results = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new InvalidOperationException("Windows WMI 服务不可用，无法安全识别网络接口。");
            locator = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建 Windows WMI 查询对象。");
            service = ((dynamic)locator).ConnectServer(".", @"root\cimv2");
            // ItemIndex avoids the SWbem forward-only _NewEnum COM marshaling
            // failure seen with .NET 10. These few local adapter rows are bounded.
            results = ((dynamic)service).ExecQuery("SELECT GUID, PNPDeviceID, PhysicalAdapter FROM Win32_NetworkAdapter WHERE GUID IS NOT NULL", "WQL", 0);
            var count = (int)((dynamic)results).Count;
            if (count > 4096) throw new InvalidOperationException("Windows 返回了过多网络设备记录。");
            for (var index = 0; index < count; index++)
            {
                object row = ((dynamic)results).ItemIndex(index);
                try
                {
                    string? guidText = GetWmiProperty(row, "GUID") as string;
                    string? instance = GetWmiProperty(row, "PNPDeviceID") as string;
                    object? physicalValue = GetWmiProperty(row, "PhysicalAdapter");
                    if (!Guid.TryParse(guidText, out var guid) || string.IsNullOrWhiteSpace(instance)) continue;
                    var identity = new AdapterIdentity(instance, physicalValue is bool physical && physical);
                    if (records.TryGetValue(guid, out var previous) && previous != identity)
                        throw new InvalidOperationException("一个网络 GUID 对应多个设备，无法安全识别该网络接口。");
                    records[guid] = identity;
                }
                finally { ReleaseCom(row); }
            }
        }
        catch (COMException ex) { throw new InvalidOperationException("读取 Windows 网络设备身份失败，未把未知接口列为可用 USB 连接。", ex); }
        finally { ReleaseCom(results); ReleaseCom(service); ReleaseCom(locator); }
        return records;
    }

    private static object? GetWmiProperty(object row, string name)
    {
        object? properties = null, property = null;
        try
        {
            properties = ((dynamic)row).Properties_;
            property = ((dynamic)properties).Item(name);
            return ((dynamic)property).Value;
        }
        finally { ReleaseCom(property); ReleaseCom(properties); }
    }

    private static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    internal static UsbDeviceIdentity? TryResolveUsbParent(string deviceInstanceId)
    {
        if (CM_Locate_DevNode(out var node, deviceInstanceId, 0) != 0) return null;
        var seen = new HashSet<uint>();
        for (var depth = 0; depth < 16 && seen.Add(node); depth++)
        {
            var id = new StringBuilder(1024);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) return null;
            var match = UsbParentPattern().Match(id.ToString());
            if (match.Success)
            {
                var serial = match.Groups[3].Value;
                if (DevicePolicy.IsNetworkSerial(serial)) return null;
                return new(serial, match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant());
            }
            if (CM_Get_Parent(out node, node, 0) != 0) return null;
        }
        return null;
    }

    // Reject Windows-generated location IDs containing '&'; only the literal USB
    // parent serial is eligible, independent of USB mode's changing PID/MI values.
    [GeneratedRegex(@"\AUSB\\VID_([0-9a-f]{4})&PID_([0-9a-f]{4})\\([A-Za-z0-9._:+][A-Za-z0-9._:+-]{0,255})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbParentPattern();
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
}
