using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace TabLink.Core;

/// <summary>
/// Immutable per-session device-side ADB reverse endpoint. Android connects to
/// <see cref="DevicePort"/> while ADB forwards it to TabLink's fixed loopback
/// frame server. A default value is deliberately invalid so receipts written
/// before this field existed cannot authorize cleanup of a later mapping.
/// </summary>
public readonly record struct AdbReverseEndpoint
{
    public const int MinimumDevicePort = 49152;
    public const int MaximumDevicePort = 65535;
    public const int LocalServerPort = 27183;

    public int DevicePort { get; }

    [JsonConstructor]
    public AdbReverseEndpoint(int devicePort)
    {
        if (devicePort is < MinimumDevicePort or > MaximumDevicePort)
            throw new ArgumentOutOfRangeException(nameof(devicePort),
                $"The device-side USB endpoint must be between {MinimumDevicePort} and {MaximumDevicePort}.");
        DevicePort = devicePort;
    }

    public bool IsValid => DevicePort is >= MinimumDevicePort and <= MaximumDevicePort;

    public static AdbReverseEndpoint CreateRandom() =>
        new(RandomNumberGenerator.GetInt32(MinimumDevicePort, MaximumDevicePort + 1));

    internal void Validate()
    {
        if (!IsValid)
            throw new ArgumentException("The per-session USB endpoint is invalid.", nameof(AdbReverseEndpoint));
    }

    internal string DeviceAddress => "tcp:" + DevicePort;
    internal static string LocalAddress => "tcp:" + LocalServerPort;
}
