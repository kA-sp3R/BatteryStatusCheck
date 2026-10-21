using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Presentation;

public sealed record LowBatteryAlert(string DeviceId, string Name, int Percentage);

/// <summary>One low-battery alert per measured discharge cycle; unavailable states do not rearm alerts.</summary>
public sealed class NotificationPolicy
{
    private readonly Dictionary<string, DeviceState> states = new(StringComparer.Ordinal);

    public IReadOnlyList<LowBatteryAlert> Evaluate(IEnumerable<DeviceSnapshot> devices, AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.LowBatteryNotifications) return [];
        var preferences = settings.Normalize();
        var alerts = new List<LowBatteryAlert>();
        foreach (var snapshot in devices.GroupBy(device => device.Device.Id, StringComparer.Ordinal)
                     .Select(group => group.OrderByDescending(device => device.Battery.SampledAt).First()))
        {
            if (!BatteryPresentation.IsFreshPercentage(snapshot, preferences, now)) continue;
            if (!states.TryGetValue(snapshot.Device.Id, out var state))
                states[snapshot.Device.Id] = state = new();
            var sampledAt = snapshot.Battery.SampledAt!.Value;
            if (state.LastEvaluatedAt is DateTimeOffset previous && sampledAt <= previous) continue;
            state.LastEvaluatedAt = sampledAt;
            var percentage = snapshot.Battery.Percentage!.Value;
            if (percentage >= preferences.LowBatteryThreshold + 5) state.Armed = true;
            if (!state.Armed || percentage > preferences.LowBatteryThreshold ||
                snapshot.Battery.Charging is ChargingState.Charging or ChargingState.Full) continue;
            state.Armed = false;
            alerts.Add(new(snapshot.Device.Id, BatteryPresentation.GetDisplayName(snapshot.Device, preferences), percentage));
        }
        return alerts.AsReadOnly();
    }

    private sealed class DeviceState
    {
        public bool Armed { get; set; } = true;
        public DateTimeOffset? LastEvaluatedAt { get; set; }
    }
}
