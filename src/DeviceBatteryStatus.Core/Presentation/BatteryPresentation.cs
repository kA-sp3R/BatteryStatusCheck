using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Presentation;

public enum BatteryColor { Green, Yellow, Red, Neutral }

public sealed record TrayPresentation(int? Percentage, BatteryColor Color, string Tooltip, string? DeviceId);

public static class BatteryPresentation
{
    public static TrayPresentation CreateTray(IEnumerable<DeviceSnapshot> devices, AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        var preferences = settings.Normalize();
        var snapshots = devices.ToArray();
        DeviceSnapshot? selected;
        if (preferences.PreferredDeviceId is not null)
        {
            selected = snapshots.FirstOrDefault(device => device.Device.Id == preferences.PreferredDeviceId);
            if (selected is null)
                return new(null, BatteryColor.Neutral, "Preferred device is unavailable.", preferences.PreferredDeviceId);
            if (!IsFreshCurrent(selected, preferences, now))
                return new(null, BatteryColor.Neutral,
                    $"{GetDisplayName(selected.Device, preferences)}: {GetStatusText(selected, preferences, now)}",
                    selected.Device.Id);
        }
        else
        {
            selected = snapshots.Where(device => IsFreshCurrent(device, preferences, now))
                .OrderBy(device => device.Battery.Percentage)
                .ThenBy(device => device.Device.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.Device.Id, StringComparer.Ordinal).FirstOrDefault();
            if (selected is null)
                return new(null, BatteryColor.Neutral,
                    snapshots.Length == 0 ? "Device Battery Status: no supported receiver found."
                        : "Device Battery Status: no current battery reading.", null);
        }

        var percentage = selected.Battery.Percentage!.Value;
        var color = percentage > 30 ? BatteryColor.Green : percentage >= 11 ? BatteryColor.Yellow : BatteryColor.Red;
        return new(percentage, color,
            $"{GetDisplayName(selected.Device, preferences)}: {percentage}% · {GetStatusText(selected, preferences, now)}",
            selected.Device.Id);
    }

    public static bool IsFreshCurrent(DeviceSnapshot device, AppSettings settings, DateTimeOffset now)
    {
        if (device.Connection == ConnectionState.Disconnected || device.Battery.State != ReadingState.Current ||
            device.Battery.Percentage is null || device.Battery.SampledAt is not DateTimeOffset sampledAt || sampledAt > now)
            return false;
        var maximumAge = TimeSpan.FromSeconds(2 * Math.Clamp(settings.RefreshIntervalSeconds, 10, 3600) + 5);
        return now - sampledAt <= maximumAge;
    }

    public static string GetDisplayName(PeripheralIdentity device, AppSettings settings) =>
        settings.CustomNames is not null && settings.CustomNames.TryGetValue(device.Id, out var alias) &&
        AppSettings.NormalizeName(alias) is { Length: > 0 } name ? name : device.Name;

    public static string GetStatusText(DeviceSnapshot device, AppSettings settings, DateTimeOffset now)
    {
        if (device.Connection == ConnectionState.Disconnected || device.Battery.State == ReadingState.Disconnected)
            return "Disconnected";
        if (device.Battery.State == ReadingState.Current && !IsFreshCurrent(device, settings, now))
            return "Stale — last measurement is no longer current";
        if (device.Battery.State != ReadingState.Current)
            return device.Battery.State.ToString();
        var charging = device.Battery.Charging switch
        {
            ChargingState.Charging => "Charging",
            ChargingState.Discharging => "On battery",
            ChargingState.Full => "Fully charged",
            _ => "Charging state unknown"
        };
        return device.Connection == ConnectionState.Unknown ? $"{charging}; link unknown" : charging;
    }
}
