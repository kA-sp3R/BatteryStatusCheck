using System.Collections.ObjectModel;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Alerts;

public enum GamingAlertMode { Percentage, Runtime, Both }
public enum GamingAlertSeverity { Low, Critical }
public enum GamingAlertCondition { Percentage, Runtime, Category }

/// <summary>A complete global rule or explicitly selected per-device override.</summary>
public sealed record GamingAlertRule
{
    public bool Enabled { get; init; } = true;
    public GamingAlertMode Mode { get; init; } = GamingAlertMode.Percentage;
    public int LowPercentage { get; init; } = 20;
    public int CriticalPercentage { get; init; } = 10;
    public int LowRuntimeMinutes { get; init; } = 60;
    public int CriticalRuntimeMinutes { get; init; } = 15;
    public bool NativeCategoryAlerts { get; init; } = true;
    public int CooldownMinutes { get; init; } = 15;
    public int RecoveryPercentage { get; init; } = 5;
    public int RecoveryRuntimeMinutes { get; init; } = 10;
    public int DurationSeconds { get; init; } = 8;
    public string? DestinationMonitor { get; init; }
    public bool Sound { get; init; }
    public DateTimeOffset? SnoozedUntil { get; init; }

    public bool TryValidate(out string? error)
    {
        error = !Enum.IsDefined(Mode) ? "Choose percentage, runtime or both alert sources."
            : LowPercentage is < 1 or > 50 || CriticalPercentage < 0 || CriticalPercentage >= LowPercentage
                ? "Low percentage must be 1–50%; critical must be 0% or higher and below low."
            : LowRuntimeMinutes is < 2 or > 1440 || CriticalRuntimeMinutes < 1 || CriticalRuntimeMinutes >= LowRuntimeMinutes
                ? "Low runtime must be 2–1440 minutes; critical must be at least 1 minute and below low."
            : DurationSeconds is < 3 or > 30 ? "Display duration must be 3–30 seconds."
            : CooldownMinutes is < 1 or > 240 ? "Cooldown must be 1–240 minutes."
            : RecoveryPercentage is < 1 or > 25 ? "Percentage recovery must be 1–25 points."
            : RecoveryRuntimeMinutes is < 1 or > 120 ? "Runtime recovery must be 1–120 minutes."
            : DestinationMonitor is not null && (DestinationMonitor.Length > 128 || DestinationMonitor.Any(char.IsControl))
                ? "Choose a valid destination monitor."
            : null;
        return error is null;
    }

    public GamingAlertRule Normalize()
    {
        var low = Math.Clamp(LowPercentage, 1, 50);
        var lowRuntime = Math.Clamp(LowRuntimeMinutes, 2, 1440);
        var monitor = DestinationMonitor?.Trim();
        return this with
        {
            Mode = Enum.IsDefined(Mode) ? Mode : GamingAlertMode.Percentage,
            LowPercentage = low,
            CriticalPercentage = Math.Clamp(CriticalPercentage, 0, low - 1),
            LowRuntimeMinutes = lowRuntime,
            CriticalRuntimeMinutes = Math.Clamp(CriticalRuntimeMinutes, 1, lowRuntime - 1),
            DurationSeconds = Math.Clamp(DurationSeconds, 3, 30),
            CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 240),
            RecoveryPercentage = Math.Clamp(RecoveryPercentage, 1, 25),
            RecoveryRuntimeMinutes = Math.Clamp(RecoveryRuntimeMinutes, 1, 120),
            DestinationMonitor = string.IsNullOrWhiteSpace(monitor) || monitor.Length > 128 || monitor.Any(char.IsControl) ? null : monitor
        };
    }
}

public sealed record GamingAlertSettings
{
    public GamingAlertRule GlobalRule { get; init; } = new();
    public IReadOnlyDictionary<string, GamingAlertRule> DeviceOverrides { get; init; } =
        new ReadOnlyDictionary<string, GamingAlertRule>(new Dictionary<string, GamingAlertRule>(StringComparer.Ordinal));

    public GamingAlertSettings Normalize()
    {
        var overrides = new Dictionary<string, GamingAlertRule>(StringComparer.Ordinal);
        foreach (var pair in DeviceOverrides ?? new Dictionary<string, GamingAlertRule>())
        {
            var id = pair.Key?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > 512 || id.Any(char.IsControl) || pair.Value is null) continue;
            overrides[id] = pair.Value.Normalize();
            if (overrides.Count == 64) break;
        }
        return this with
        {
            GlobalRule = (GlobalRule ?? new()).Normalize(),
            DeviceOverrides = new ReadOnlyDictionary<string, GamingAlertRule>(overrides)
        };
    }

    public GamingAlertRule ForDevice(string deviceId) =>
        DeviceOverrides.TryGetValue(deviceId, out var rule) ? rule : GlobalRule;

    /// <summary>Old settings retain their notification master and configured low threshold.</summary>
    public static GamingAlertSettings FromLegacy(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GamingAlerts is not null) return settings.GamingAlerts.Normalize();
        var low = Math.Clamp(settings.LowBatteryThreshold, 1, 50);
        return new() { GlobalRule = new() { LowPercentage = low, CriticalPercentage = Math.Min(10, low - 1) } };
    }
}
