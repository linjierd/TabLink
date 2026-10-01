using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TabLink.Core;

public sealed class DeviceExclusionRule
{
    public string? Serial { get; set; }
    public string? Vid { get; set; }
    public string? Pid { get; set; }
    public string? Label { get; set; }
}

public sealed class DevicePolicySettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? AdbPath { get; set; }
    public List<DeviceExclusionRule> ExcludedDevices { get; set; } =
    [
        new() { Vid = "19D2", Pid = "0246", Label = "F50 Pro — USB tethering" },
        new() { Vid = "19D2", Pid = "0621", Label = "F50 Pro — alternate USB mode" }
    ];
}

public sealed record UsbDeviceIdentity(string Serial, string Vid, string Pid);

public sealed record AdbDevice(string Serial, string State, string? Model = null,
    string? TransportId = null, string? Details = null)
{
    public bool IsNetworkTransport => DevicePolicy.IsNetworkSerial(Serial) ||
        (Details?.Contains("transport:tcp", StringComparison.OrdinalIgnoreCase) ?? false);
}

public sealed record DevicePolicyDecision(bool Allowed, string Reason, UsbDeviceIdentity? UsbIdentity);

/// <summary>Proof of an explicit approval. Its internal constructor prevents unchecked target strings.</summary>
public sealed class ApprovedUsbDevice
{
    internal ApprovedUsbDevice(DevicePolicy owner, AdbDevice device, UsbDeviceIdentity identity, string fingerprint)
        => (Owner, Device, UsbIdentity, Fingerprint) = (owner, device, identity, fingerprint);
    public string Serial => Device.Serial;
    public UsbDeviceIdentity UsbIdentity { get; }
    internal DevicePolicy Owner { get; }
    internal AdbDevice Device { get; }
    internal string Fingerprint { get; }
}

public sealed class DevicePolicy
{
    private readonly DevicePolicySettings _settings;
    public DevicePolicy(DevicePolicySettings settings) => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public DevicePolicyDecision Evaluate(AdbDevice device, IReadOnlyList<UsbDeviceIdentity> usbInventory)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(usbInventory);
        try { ValidateSettings(_settings); }
        catch (ArgumentException ex) { return new(false, "Configuration is invalid: " + ex.Message, null); }
        if (!IsSafeSerial(device.Serial)) return new(false, "Invalid device serial.", null);
        if (device.IsNetworkTransport) return new(false, "Network and emulator transports are not allowed.", null);
        if (!string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase))
            return new(false, "Device is not authorized and ready (state: " + device.State + ").", null);
        // An adb usb: field is only a hint. The current Windows USB inventory is required.
        var matches = usbInventory.Where(x => x is not null &&
            string.Equals(x.Serial, device.Serial, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (matches.Length != 1) return new(false, "No unique present Windows USB device matches this serial.", null);
        var identity = matches[0];
        if (!IsHexId(identity.Vid) || !IsHexId(identity.Pid))
            return new(false, "Windows USB identity is incomplete.", null);
        foreach (var rule in _settings.ExcludedDevices)
        {
            var serialMatch = !string.IsNullOrWhiteSpace(rule.Serial) &&
                string.Equals(rule.Serial.Trim(), device.Serial, StringComparison.OrdinalIgnoreCase);
            var hardwareMatch = !string.IsNullOrWhiteSpace(rule.Vid) &&
                string.Equals(rule.Vid.Trim(), identity.Vid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.Pid!.Trim(), identity.Pid, StringComparison.OrdinalIgnoreCase);
            if (serialMatch || hardwareMatch)
                return new(false, "Excluded device: " + (string.IsNullOrWhiteSpace(rule.Label) ? "matching exclusion rule" : rule.Label), identity);
        }
        return new(true, "Present USB device; eligible for explicit approval.", identity);
    }

    public ApprovedUsbDevice Approve(AdbDevice device, IReadOnlyList<UsbDeviceIdentity> usbInventory)
    {
        var result = Evaluate(device, usbInventory);
        if (!result.Allowed) throw new DevicePolicyException(result.Reason);
        return new(this, device, result.UsbIdentity!, Fingerprint());
    }

    internal void ValidateApproval(ApprovedUsbDevice approved, AdbDevice current, IReadOnlyList<UsbDeviceIdentity> inventory)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (!ReferenceEquals(approved.Owner, this)) throw new DevicePolicyException("Approval belongs to another policy instance.");
        var decision = Evaluate(current, inventory);
        if (!decision.Allowed) throw new DevicePolicyException(decision.Reason);
        if (approved.Fingerprint != Fingerprint()) throw new DevicePolicyException("Settings changed; approve the USB device again.");
        if (!string.Equals(approved.Serial, current.Serial, StringComparison.Ordinal) ||
            !string.Equals(approved.UsbIdentity.Vid, decision.UsbIdentity!.Vid, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(approved.UsbIdentity.Pid, decision.UsbIdentity.Pid, StringComparison.OrdinalIgnoreCase))
            throw new DevicePolicyException("USB identity changed; approve the USB device again.");
    }

    private string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_settings))));

    public static void ValidateSettings(DevicePolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != 1) throw new ArgumentException("Unsupported settings schema version.");
        if (settings.ExcludedDevices is null) throw new ArgumentException("Exclusion rules must be present.");
        if (settings.ExcludedDevices.Count > 1000) throw new ArgumentException("Too many exclusion rules.");
        foreach (var rule in settings.ExcludedDevices)
        {
            if (rule is null) throw new ArgumentException("A null exclusion rule is not allowed.");
            bool hasSerial = !string.IsNullOrWhiteSpace(rule.Serial);
            bool hasVid = !string.IsNullOrWhiteSpace(rule.Vid), hasPid = !string.IsNullOrWhiteSpace(rule.Pid);
            if (!hasSerial && !hasVid && !hasPid) throw new ArgumentException("An exclusion needs a serial or VID/PID pair.");
            if (hasSerial && !IsSafeSerial(rule.Serial!.Trim())) throw new ArgumentException("An exclusion serial is invalid.");
            if (hasVid != hasPid || (hasVid && (!IsHexId(rule.Vid!.Trim()) || !IsHexId(rule.Pid!.Trim()))))
                throw new ArgumentException("VID and PID must both be exactly four hexadecimal digits.");
        }
    }

    internal static bool IsSafeSerial(string serial) => !string.IsNullOrWhiteSpace(serial) && serial.Length <= 256 &&
        !serial.StartsWith('-') && Regex.IsMatch(serial, @"\A[A-Za-z0-9._:+-]+\z", RegexOptions.CultureInvariant);
    internal static bool IsHexId(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{4}\z", RegexOptions.CultureInvariant);
    public static bool IsSafeUsbSerial(string serial) => IsSafeSerial(serial) && !IsNetworkSerial(serial);
    public static bool IsUsbHardwareId(string? value) => IsHexId(value);
    public static bool IsNetworkSerial(string serial) => serial.Contains(':') ||
        serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) ||
        serial.Contains("._tcp", StringComparison.OrdinalIgnoreCase) ||
        serial.Contains("_adb-tls", StringComparison.OrdinalIgnoreCase);
}

public class DevicePolicyException(string message) : InvalidOperationException(message);

/// <summary>
/// The explicitly approved physical USB device is still expected, but adb has
/// not made that one transport ready yet. Callers may retry this condition;
/// all identity, exclusion and approval failures remain DevicePolicyException.
/// </summary>
public sealed class AdbDeviceTemporarilyUnavailableException(string message) : DevicePolicyException(message);
