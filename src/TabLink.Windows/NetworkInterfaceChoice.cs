using System.Net;

namespace TabLink.Windows;

public enum NetworkInterfaceKind { WiFi, Usb, Ethernet }

/// <summary>
/// Immutable, already-policy-filtered description of one usable local IPv4
/// route. Inventory and mutation remain the caller's responsibility.
/// </summary>
public sealed record NetworkInterfaceChoice(IPAddress LocalAddress, string InterfaceAlias, int PrefixLength,
    NetworkInterfaceKind Kind, string? UsbSerial, string InterfaceId, int InterfaceIndex)
{
    public const int Port = 27184;
    public IPEndPoint Endpoint => new(LocalAddress, Port);
    internal bool HasSameBinding(NetworkInterfaceChoice other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind==other.Kind&&
            string.Equals(InterfaceId,other.InterfaceId,StringComparison.OrdinalIgnoreCase)&&
            string.Equals(InterfaceAlias,other.InterfaceAlias,StringComparison.Ordinal)&&
            InterfaceIndex==other.InterfaceIndex&&LocalAddress.Equals(other.LocalAddress)&&
            PrefixLength==other.PrefixLength&&
            string.Equals(UsbSerial,other.UsbSerial,StringComparison.OrdinalIgnoreCase);
    }
    public string DisplayText => $"{Kind switch { NetworkInterfaceKind.WiFi => "Wi-Fi", NetworkInterfaceKind.Usb => "USB 网络", _ => "有线网络" }} · {InterfaceAlias} · {LocalAddress}" + (UsbSerial is null ? "" : $" · {UsbSerial}");
    public override string ToString() => DisplayText;
}
