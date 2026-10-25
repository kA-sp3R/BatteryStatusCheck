using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Alerts;

/// <summary>
/// An independently owned, validated settings snapshot for repeated policy checks.
/// Prepare again when preferences change; freshness and notification eligibility
/// are still checked on every evaluation and revalidation.
/// </summary>
public sealed class GamingAlertPreferences
{
    internal AppSettings Settings { get; }
    internal GamingAlertSettings Rules { get; }

    private GamingAlertPreferences(AppSettings settings)
    {
        Settings = settings.Normalize();
        // Normalize already copied these rules, including all device overrides.
        // Legacy settings only need their fallback rule constructed once.
        Rules = Settings.GamingAlerts ?? GamingAlertSettings.FromLegacy(Settings);
    }

    public static GamingAlertPreferences Create(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings);
    }
}
