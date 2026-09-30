using System.Globalization;

namespace TabLink.Windows;

internal readonly record struct StableSemanticVersion(ulong Major, ulong Minor, ulong Patch) : IComparable<StableSemanticVersion>
{
    public static StableSemanticVersion Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new FormatException("版本号不能为空或包含首尾空格。");
        var parts = value.Split('.');
        if (parts.Length != 3)
            throw new FormatException("正式版本号必须是 major.minor.patch，不能包含预发布标记。");
        return new(ParsePart(parts[0]), ParsePart(parts[1]), ParsePart(parts[2]));
    }

    static ulong ParsePart(string value)
    {
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0') || value.Any(c => c is < '0' or > '9'))
            throw new FormatException("版本号格式无效。");
        return ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public int CompareTo(StableSemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");
}
