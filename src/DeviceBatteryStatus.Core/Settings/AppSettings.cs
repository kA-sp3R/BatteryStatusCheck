using System.Collections.ObjectModel;
using System.Globalization;

namespace DeviceBatteryStatus.Core.Settings;

public sealed record AppSettings
{
    public int RefreshIntervalSeconds { get; init; } = 30;
    public bool LowBatteryNotifications { get; init; } = true;
    public int LowBatteryThreshold { get; init; } = 20;
    public string? PreferredDeviceId { get; init; }
    public bool StartWithWindows { get; init; }
    public IReadOnlyDictionary<string, string> CustomNames { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Use at settings boundaries to validate values and take an independent, read-only copy.</summary>
    public AppSettings Normalize()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in CustomNames ?? new Dictionary<string, string>())
        {
            var id = NormalizeDeviceId(pair.Key);
            var name = NormalizeName(pair.Value);
            if (id is not null && name.Length > 0) names[id] = name;
        }
        return this with
        {
            RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 10, 3600),
            LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 50),
            PreferredDeviceId = NormalizeDeviceId(PreferredDeviceId),
            CustomNames = new ReadOnlyDictionary<string, string>(names)
        };
    }

    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var clean = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        var elements = new StringInfo(clean);
        return elements.LengthInTextElements > 60 ? elements.SubstringByTextElements(0, 60) : clean;
    }

    private static string? NormalizeDeviceId(string? id)
    {
        var clean = id?.Trim();
        return string.IsNullOrEmpty(clean) || clean.Length > 512 || clean.Any(char.IsControl) ? null : clean;
    }
}
