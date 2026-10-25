using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Runtime;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Alerts;

/// <summary>
/// Read-only threshold policy. Each condition/severity is delivered once until genuine recovery;
/// a device cooldown additionally combines independent triggers. Critical escalation can bypass
/// a previous low alert's cooldown. Only successful display acknowledges delivery.
/// </summary>
public sealed class GamingAlertPolicy
{
    private const int MaximumRememberedDevices = 128;
    private readonly Dictionary<string, DeviceState> states = new(StringComparer.Ordinal);

    public IReadOnlyList<GamingBatteryAlert> Evaluate(IEnumerable<DeviceSnapshot> snapshots, AppSettings settings,
        IReadOnlyDictionary<string, RuntimeEstimate> runtime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);
        return Evaluate(GamingAlertPreferences.Create(settings), snapshots, runtime, now);
    }

    /// <summary>Reuses an owned preferences snapshot while checking current samples and cooldowns.</summary>
    public IReadOnlyList<GamingBatteryAlert> Evaluate(GamingAlertPreferences preferences,
        IEnumerable<DeviceSnapshot> snapshots, IReadOnlyDictionary<string, RuntimeEstimate> runtime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(runtime);
        var settings = preferences.Settings;
        var rules = preferences.Rules;
        var result = new List<GamingBatteryAlert>();
        foreach (var snapshot in snapshots.GroupBy(value => value.Device.Id, StringComparer.Ordinal)
                     .Select(group => group.OrderByDescending(value => value.Battery.SampledAt).First()))
        {
            if (!BatteryPresentation.IsFreshCurrent(snapshot, settings, now)) continue;
            var state = GetState(snapshot.Device.Id, now);
            var rule = rules.ForDevice(snapshot.Device.Id);
            runtime.TryGetValue(snapshot.Device.Id, out var estimate);
            Recover(state, snapshot, rule, estimate);
            var candidate = CreateCandidate(snapshot, settings, rules, estimate, now);
            if (candidate is null) continue;
            var eligible = candidate.Signals.Where(pair => state.For(pair.Key).IsArmed(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (eligible.Count == 0) continue;
            var severity = eligible.Values.Max();
            var escalation = severity == GamingAlertSeverity.Critical && state.LastSeverity == GamingAlertSeverity.Low;
            if (!escalation && state.LastShownAt is { } last && now - last < TimeSpan.FromMinutes(rule.CooldownMinutes))
                continue;
            result.Add(candidate with
            {
                Severity = severity,
                Conditions = Array.AsReadOnly(eligible.Keys.ToArray()),
                Signals = eligible
            });
        }
        return result.OrderByDescending(value => value.Severity).ThenBy(value => value.DeviceId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Rechecks current relevance, including settings and freshness, but deliberately ignores
    /// delivery cooldown so an already-visible acknowledged alert can finish its display duration.
    /// </summary>
    public GamingBatteryAlert? Revalidate(GamingBatteryAlert candidate, IEnumerable<DeviceSnapshot> snapshots,
        AppSettings settings, IReadOnlyDictionary<string, RuntimeEstimate> runtime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);
        if (candidate.IsPreview) return null;
        return Revalidate(GamingAlertPreferences.Create(settings), candidate, snapshots, runtime, now);
    }

    /// <summary>Reuses prepared preferences without skipping freshness or queued-alert relevance checks.</summary>
    public GamingBatteryAlert? Revalidate(GamingAlertPreferences preferences, GamingBatteryAlert candidate,
        IEnumerable<DeviceSnapshot> snapshots, IReadOnlyDictionary<string, RuntimeEstimate> runtime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(runtime);
        if (candidate.IsPreview) return null;
        var snapshot = snapshots.Where(value => value.Device.Id == candidate.DeviceId)
            .OrderByDescending(value => value.Battery.SampledAt).FirstOrDefault();
        if (snapshot is null) return null;
        runtime.TryGetValue(snapshot.Device.Id, out var estimate);
        var current = CreateCandidate(snapshot, preferences.Settings, preferences.Rules, estimate, now);
        if (current is null) return null;
        // Keep the original triggering sources. An unrelated, previously acknowledged runtime
        // condition must not turn a newly queued percentage alert into a duplicate critical alert.
        var relevant = current.Signals.Where(pair => candidate.Conditions.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return relevant.Count == 0 ? null : current with
        {
            CreatedAt = candidate.CreatedAt,
            Severity = relevant.Values.Max(),
            Conditions = Array.AsReadOnly(relevant.Keys.ToArray()),
            Signals = relevant
        };
    }

    public void Acknowledge(GamingBatteryAlert alert, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (alert.IsPreview) return;
        var state = GetState(alert.DeviceId, now);
        foreach (var signal in alert.Signals) state.For(signal.Key).Acknowledge(signal.Value);
        state.LastShownAt = now;
        state.LastSeverity = alert.Severity;
    }

    private static GamingBatteryAlert? CreateCandidate(DeviceSnapshot snapshot, AppSettings settings,
        GamingAlertSettings rules, RuntimeEstimate? estimate, DateTimeOffset now)
    {
        var rule = rules.ForDevice(snapshot.Device.Id);
        if (!settings.LowBatteryNotifications || !rule.Enabled ||
            rules.GlobalRule.SnoozedUntil > now || rule.SnoozedUntil > now ||
            !BatteryPresentation.IsFreshCurrent(snapshot, settings, now) ||
            snapshot.Battery.Charging is ChargingState.Charging or ChargingState.Full)
            return null;
        var signals = new Dictionary<GamingAlertCondition, GamingAlertSeverity>();
        if (snapshot.Battery.Percentage is { } percentage && rule.Mode is GamingAlertMode.Percentage or GamingAlertMode.Both)
        {
            if (percentage <= rule.CriticalPercentage) signals[GamingAlertCondition.Percentage] = GamingAlertSeverity.Critical;
            else if (percentage <= rule.LowPercentage) signals[GamingAlertCondition.Percentage] = GamingAlertSeverity.Low;
        }
        if (snapshot.Battery.Percentage is null && rule.NativeCategoryAlerts)
        {
            if (snapshot.Battery.Category == BatteryCategory.Critical) signals[GamingAlertCondition.Category] = GamingAlertSeverity.Critical;
            else if (snapshot.Battery.Category == BatteryCategory.Low) signals[GamingAlertCondition.Category] = GamingAlertSeverity.Low;
        }
        if (rule.Mode is GamingAlertMode.Runtime or GamingAlertMode.Both && IsUsableRuntime(snapshot, estimate) &&
            estimate!.IsConfident && estimate.UpperBound is { } upper)
        {
            if (upper <= TimeSpan.FromMinutes(rule.CriticalRuntimeMinutes)) signals[GamingAlertCondition.Runtime] = GamingAlertSeverity.Critical;
            else if (upper <= TimeSpan.FromMinutes(rule.LowRuntimeMinutes)) signals[GamingAlertCondition.Runtime] = GamingAlertSeverity.Low;
        }
        if (signals.Count == 0) return null;
        var runtime = IsUsableRuntime(snapshot, estimate) ? estimate : null;
        var runtimeText = runtime?.Text ?? (snapshot.Battery.Percentage is not null &&
            snapshot.Connection == ConnectionState.Connected && snapshot.Battery.Charging == ChargingState.Discharging &&
            estimate?.State == RuntimeEstimateState.Calculating && estimate.BasedOnSampleAt == snapshot.Battery.SampledAt
                ? "Calculating…" : "Unavailable");
        return new(snapshot.Device.Id, BatteryPresentation.GetDisplayName(snapshot.Device, settings),
            snapshot.Battery.Percentage, snapshot.Battery.Category, runtimeText, signals.Values.Max(), rule,
            Array.AsReadOnly(signals.Keys.ToArray()), now) { Runtime = runtime, Signals = signals };
    }

    private static bool IsUsableRuntime(DeviceSnapshot snapshot, RuntimeEstimate? estimate) =>
        snapshot.Battery.Percentage is not null && snapshot.Connection == ConnectionState.Connected &&
        snapshot.Battery.Charging == ChargingState.Discharging && estimate is
        { State: RuntimeEstimateState.Available, Remaining: { } remaining, LowerBound: { } lower, UpperBound: { } upper } &&
        remaining >= TimeSpan.Zero && lower >= TimeSpan.Zero && upper >= lower && remaining >= lower && remaining <= upper &&
        estimate.BasedOnSampleAt == snapshot.Battery.SampledAt;

    private static void Recover(DeviceState state, DeviceSnapshot snapshot, GamingAlertRule rule, RuntimeEstimate? estimate)
    {
        // Only fresh, newer samples may rearm; stale, missing and disconnected observations cannot.
        if (snapshot.Battery.SampledAt is not { } sampledAt || state.LastRecoverySampleAt >= sampledAt) return;
        state.LastRecoverySampleAt = sampledAt;
        if (snapshot.Battery.Percentage is { } percentage)
        {
            var latch = state.For(GamingAlertCondition.Percentage);
            if (percentage >= rule.LowPercentage + rule.RecoveryPercentage) latch.LowArmed = true;
            if (percentage >= rule.CriticalPercentage + rule.RecoveryPercentage) latch.CriticalArmed = true;
        }
        if (snapshot.Battery.Category is BatteryCategory.Good or BatteryCategory.High or BatteryCategory.Full)
        {
            var latch = state.For(GamingAlertCondition.Category);
            latch.LowArmed = latch.CriticalArmed = true;
        }
        if (IsUsableRuntime(snapshot, estimate) && estimate!.IsConfident && estimate.LowerBound is { } lower)
        {
            var latch = state.For(GamingAlertCondition.Runtime);
            if (lower >= TimeSpan.FromMinutes(rule.LowRuntimeMinutes + rule.RecoveryRuntimeMinutes)) latch.LowArmed = true;
            if (lower >= TimeSpan.FromMinutes(rule.CriticalRuntimeMinutes + rule.RecoveryRuntimeMinutes)) latch.CriticalArmed = true;
        }
    }

    private DeviceState GetState(string deviceId, DateTimeOffset now)
    {
        if (!states.TryGetValue(deviceId, out var state))
        {
            if (states.Count >= MaximumRememberedDevices)
                states.Remove(states.MinBy(pair => pair.Value.LastSeenAt).Key);
            states[deviceId] = state = new();
        }
        state.LastSeenAt = now;
        return state;
    }

    private sealed class DeviceState
    {
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset? LastShownAt { get; set; }
        public DateTimeOffset? LastRecoverySampleAt { get; set; }
        public GamingAlertSeverity? LastSeverity { get; set; }
        private readonly Dictionary<GamingAlertCondition, Latch> latches = new();
        public Latch For(GamingAlertCondition condition)
        {
            if (!latches.TryGetValue(condition, out var value)) latches[condition] = value = new();
            return value;
        }
    }

    private sealed class Latch
    {
        public bool LowArmed { get; set; } = true;
        public bool CriticalArmed { get; set; } = true;
        public bool IsArmed(GamingAlertSeverity severity) => severity == GamingAlertSeverity.Critical ? CriticalArmed : LowArmed;
        public void Acknowledge(GamingAlertSeverity severity)
        {
            LowArmed = false;
            if (severity == GamingAlertSeverity.Critical) CriticalArmed = false;
        }
    }
}
