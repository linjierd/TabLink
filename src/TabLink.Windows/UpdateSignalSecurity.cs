using System.Security.Cryptography;
using System.Text.Json;

namespace TabLink.Windows;

internal static class UpdateSignalSecurity
{
    internal static string NormalizeNonce(string value, string parameterName = "nonce")
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("更新握手随机值必须是 256-bit 十六进制值。", parameterName);
        return value.ToUpperInvariant();
    }

    internal static bool FixedTimeNonceEquals(string? actual, string expected)
    {
        try
        {
            var actualBytes = Convert.FromHexString(NormalizeNonce(actual ?? "", nameof(actual)));
            var expectedBytes = Convert.FromHexString(NormalizeNonce(expected, nameof(expected)));
            return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }

    internal static bool ValidateHandshake(byte[] json, int waitPid, long waitStartUtcTicks,
        int updaterPid, long updaterStartUtcTicks, string nonce)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                HasUniqueProperty(root, "waitPid") && HasUniqueProperty(root, "waitStartUtcTicks") &&
                HasUniqueProperty(root, "updaterPid") && HasUniqueProperty(root, "updaterStartUtcTicks") && HasUniqueProperty(root, "nonce") &&
                root.GetProperty("waitPid").GetInt32() == waitPid &&
                root.GetProperty("waitStartUtcTicks").GetInt64() == waitStartUtcTicks &&
                root.GetProperty("updaterPid").GetInt32() == updaterPid &&
                root.GetProperty("updaterStartUtcTicks").GetInt64() == updaterStartUtcTicks &&
                FixedTimeNonceEquals(root.GetProperty("nonce").GetString(), nonce);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException) { return false; }
    }

    internal static bool ValidateHealth(byte[] json, int pid, long processStartUtcTicks, string executablePath, string nonce)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasUniqueProperty(root, "pid") ||
                !HasUniqueProperty(root, "processStartUtcTicks") || !HasUniqueProperty(root, "executablePath") ||
                !HasUniqueProperty(root, "nonce")) return false;
            var actualExecutable = Path.GetFullPath(root.GetProperty("executablePath").GetString() ?? "");
            return root.GetProperty("pid").GetInt32() == pid &&
                root.GetProperty("processStartUtcTicks").GetInt64() == processStartUtcTicks &&
                actualExecutable.Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase) &&
                FixedTimeNonceEquals(root.GetProperty("nonce").GetString(), nonce);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException or OverflowException) { return false; }
    }

    static bool HasUniqueProperty(JsonElement root, string name)
    {
        var count = 0;
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals(name) && ++count > 1) return false;
        return count == 1;
    }
}
