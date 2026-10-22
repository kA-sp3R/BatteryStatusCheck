using System.Globalization;
using System.Text.RegularExpressions;

namespace DeviceBatteryStatus.Core.Updates;

/// <summary>SemVer ordering sufficient for stable GitHub releases; current development builds may have a suffix.</summary>
internal sealed record ReleaseVersion(int Major, int Minor, int Patch, bool Prerelease) : IComparable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool TryParse(string? text, out ReleaseVersion? version)
    {
        version = null;
        if (text is null || text.Length > 120) return false;
        var match = Pattern.Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch)) return false;
        if (match.Groups[4].Success && match.Groups[4].Value.Split('.').Any(identifier =>
                identifier.Length > 1 && identifier.All(char.IsAsciiDigit) && identifier[0] == '0')) return false;
        version = new(major, minor, patch, match.Groups[4].Success);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int compare = Major.CompareTo(other.Major);
        if (compare != 0) return compare;
        compare = Minor.CompareTo(other.Minor);
        if (compare != 0) return compare;
        compare = Patch.CompareTo(other.Patch);
        return compare != 0 ? compare : other.Prerelease.CompareTo(Prerelease);
    }
}
