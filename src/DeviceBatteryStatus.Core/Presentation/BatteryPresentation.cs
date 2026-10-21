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
            if (!IsFreshPercentage(selected, preferences, now))
                return CreateNeutralTray(selected, preferences, now);
        }
        else
        {
            selected = snapshots.Where(device => IsFreshPercentage(device, preferences, now))
                .OrderBy(device => device.Battery.Percentage)
                .ThenBy(device => device.Device.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.Device.Id, StringComparer.Ordinal).FirstOrDefault();
            if (selected is null)
            {
                var categoryDevice = snapshots.Where(device => IsFreshCurrent(device, preferences, now))
                    .OrderBy(device => device.Device.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(device => device.Device.Id, StringComparer.Ordinal).FirstOrDefault();
                if (categoryDevice is not null) return CreateNeutralTray(categoryDevice, preferences, now);
                return new(null, BatteryColor.Neutral,
                    snapshots.Length == 0 ? "Device Battery Status: no supported receiver found."
                        : "Device Battery Status: no current battery reading.", null);
            }
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
            !device.Battery.HasMeasurement ||
            device.Battery.SampledAt is not DateTimeOffset sampledAt || sampledAt > now)
            return false;
        var maximumAge = TimeSpan.FromSeconds(2 * Math.Clamp(settings.RefreshIntervalSeconds, 10, 3600) + 5);
        return now - sampledAt <= maximumAge;
    }

    public static bool IsFreshPercentage(DeviceSnapshot device, AppSettings settings, DateTimeOffset now) =>
        device.Battery.Percentage is not null && IsFreshCurrent(device, settings, now);

    public static string GetBatteryValueText(DeviceSnapshot device, AppSettings settings, DateTimeOffset now)
    {
        var fresh = IsFreshCurrent(device, settings, now);
        var stale = device.Battery.State == ReadingState.Stale ||
            (device.Battery.State == ReadingState.Current && !fresh);
        if (!fresh && !stale) return "—";
        var value = device.Battery.Percentage is { } percentage
            ? $"{percentage}%"
            : device.Battery.Category is { } category ? $"{category} · category" : "—";
        return stale && value != "—" ? $"{value} · stale" : value;
    }

    private static TrayPresentation CreateNeutralTray(DeviceSnapshot device, AppSettings settings, DateTimeOffset now)
    {
        var value = GetBatteryValueText(device, settings, now);
        var status = GetStatusText(device, settings, now);
        var description = value == "—" ? status : $"{value} · {status}";
        return new(null, BatteryColor.Neutral, $"{GetDisplayName(device.Device, settings)}: {description}", device.Device.Id);
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
