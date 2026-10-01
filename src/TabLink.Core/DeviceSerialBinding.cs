using System.Security.Cryptography;
using System.Text;

namespace TabLink.Core;

/// <summary>
/// Creates the privacy-preserving device binding used by offline measurement tools.
/// The v1 canonical form is the exact, case-sensitive USB serial (no trimming or
/// Unicode normalization), prefixed with <c>tablink-device-serial-v1\0</c>, encoded
/// as UTF-8 and hashed with SHA-256. The result is lowercase hexadecimal.
/// </summary>
public static class DeviceSerialBinding
{
    public const string Algorithm = "sha256-utf8-tablink-device-serial-v1";
    private const string DomainSeparator = "tablink-device-serial-v1\0";

    public static string ComputeSha256(string serial)
    {
        if (!DevicePolicy.IsSafeUsbSerial(serial))
            throw new ArgumentException("A valid physical USB serial is required.", nameof(serial));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DomainSeparator + serial)))
            .ToLowerInvariant();
    }
}
