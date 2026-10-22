using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Presentation;

public sealed record TrayIconItem(string Key, TrayPresentation Presentation);

/// <summary>Decides which icons exist without touching the Windows notification area.</summary>
public static class TrayIconSelection
{
    public const string SummaryKey = "summary";
    public const int MaximumDeviceIcons = 16;

    public static string DeviceKey(string deviceId) => $"device/{deviceId}";

    public static IReadOnlyList<TrayIconItem> Create(IEnumerable<DeviceSnapshot> devices,
        AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        var preferences = settings.Normalize();
        var snapshots = devices.ToArray();
        if (preferences.TrayIconMode != TrayIconMode.SelectedDevices || preferences.TrayDeviceIds.Count == 0)
            return Array.AsReadOnly(new[] { new TrayIconItem(SummaryKey,
                BatteryPresentation.CreateTray(snapshots, preferences, now)) });

        var byId = snapshots.ToLookup(snapshot => snapshot.Device.Id, StringComparer.Ordinal);
        var items = new List<TrayIconItem>();
        foreach (var id in preferences.TrayDeviceIds.Distinct(StringComparer.Ordinal).Take(MaximumDeviceIcons))
        {
            var matches = byId[id].ToArray();
            TrayPresentation presentation;
            if (matches.Length != 1)
            {
                var name = preferences.CustomNames.GetValueOrDefault(id) ?? $"Selected device {items.Count + 1}";
                presentation = new(null, BatteryColor.Neutral,
                    $"{TooltipName(name)}: {(matches.Length == 0 ? "unavailable — not currently detected" : "ambiguous device readings")}", id);
            }
            else
            {
                var snapshot = matches[0];
                // An explicit preferred identity prevents a lower battery from another
                // peripheral being substituted for this icon's selected device.
                presentation = BatteryPresentation.CreateTray([snapshot],
                    preferences with { PreferredDeviceId = id }, now) with
                { Tooltip = DeviceTooltip(snapshot, preferences, now) };
            }
            items.Add(new(DeviceKey(id), presentation));
        }
        return items.AsReadOnly();
    }

    private static string DeviceTooltip(DeviceSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        var name = TooltipName(BatteryPresentation.GetDisplayName(snapshot.Device, settings));
        var battery = snapshot.Battery;
        if (snapshot.Connection == ConnectionState.Disconnected || battery.State == ReadingState.Disconnected)
            return $"{name}: Disconnected";
        if (battery.State is not (ReadingState.Current or ReadingState.Stale))
            return $"{name}: {battery.State} · {(snapshot.Connection == ConnectionState.Connected ? "connected" : "link unknown")}";

        var stale = battery.State == ReadingState.Stale || !BatteryPresentation.IsFreshCurrent(snapshot, settings, now);
        var value = battery.Percentage is { } percentage ? $"{percentage}%"
            : battery.Category is { } category ? $"{category} · category" : "unknown";
        var sampled = battery.SampledAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        if (stale)
            return $"{name}: {value} · Stale · sampled {sampled}";

        var charge = battery.Charging switch
        {
            ChargingState.Charging => "charging",
            ChargingState.Discharging => "on battery",
            ChargingState.Full => "fully charged",
            _ => "charge unknown"
        };
        var link = snapshot.Connection == ConnectionState.Unknown ? "; link unknown" : "";
        return $"{name}: {value} · {sampled} · {charge}{link}";
    }

    private static string TooltipName(string? name)
    {
        name = AppSettings.NormalizeName(name);
        if (name.Length == 0) return "Selected device";
        if (name.Length <= 32) return name;
        int cut = char.IsHighSurrogate(name[30]) ? 30 : 31;
        return name[..cut] + "…";
    }
}
