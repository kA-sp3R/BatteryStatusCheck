using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Runtime;

/// <summary>
/// Observes existing battery samples only. Rates come from timestamped percentages;
/// no voltage, assumed load, capacity or manufacturer runtime API is used.
/// History is intentionally transient: a restart starts a new estimation period.
/// </summary>
public sealed class RuntimeEstimator(TimeProvider? timeProvider = null)
{
    private const int MaximumDevices = 64;
    private const int MaximumSamples = 2048;
    private static readonly TimeSpan MaximumHistory = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinimumCoverage = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ConfidentCoverage = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StorageCadence = TimeSpan.FromMinutes(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Period> periods = new(StringComparer.Ordinal);

    public void Observe(IEnumerable<DeviceSnapshot> snapshots, AppSettings settings) => Observe(snapshots, settings, clock.GetUtcNow());

    public void Observe(IEnumerable<DeviceSnapshot> snapshots, AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in snapshots.GroupBy(snapshot => snapshot.Device.Id, StringComparer.Ordinal))
            {
                var values = group.ToArray();
                var snapshot = values.MaxBy(value => value.Battery.SampledAt)!;
                var key = snapshot.Device.Id;
                seen.Add(key);
                if (values.Any(value => !SameIdentity(value.Device, snapshot.Device) ||
                    value.Battery.SampledAt == snapshot.Battery.SampledAt && value.Battery != snapshot.Battery))
                {
                    periods.Remove(key);
                    continue;
                }
                if (UnavailableReason(snapshot, settings, now) is not null)
                {
                    periods.Remove(key);
                    continue;
                }
                var current = new Sample(snapshot.Battery.SampledAt!.Value, snapshot.Battery.Percentage!.Value);
                if (!periods.TryGetValue(key, out var period) || !SameIdentity(period.Identity, snapshot.Device))
                {
                    Start(snapshot.Device, current);
                    continue;
                }
                if (current.At == period.Last.At && current.Percentage == period.Last.Percentage) continue;
                if (current.At <= period.Last.At)
                {
                    periods.Remove(key); // A changed, non-advancing sample cannot enter a trend.
                    continue;
                }
                if (current.At - period.Last.At > FreshnessBudget(settings) ||
                    current.Percentage - period.Lowest > 2 ||
                    period.Last.Percentage - current.Percentage > 25 && current.At - period.Last.At < TimeSpan.FromMinutes(5))
                {
                    Start(snapshot.Device, current);
                    continue;
                }
                if (current.Percentage < period.Lowest)
                {
                    period.Drops.Add(new(current.At, period.Lowest - current.Percentage));
                    period.Lowest = current.Percentage;
                }
                if (current.At - period.Samples[^1].At >= StorageCadence ||
                    current.Percentage != period.Samples[^1].Percentage)
                    period.Samples.Add(current);
                period.Last = current;
                var cutoff = Before(current.At, MaximumHistory);
                while (period.Samples.Count > MaximumSamples || period.Samples.Count > 1 && period.Samples[1].At < cutoff)
                    period.Samples.RemoveAt(0);
                period.Drops.RemoveAll(drop => drop.At <= period.Samples[0].At);
                period.Estimate = Calculate(period);
            }
            foreach (var missing in periods.Keys.Where(key => !seen.Contains(key)).ToArray()) periods.Remove(missing);
        }
    }

    public RuntimeEstimate Get(DeviceSnapshot snapshot, AppSettings settings) => Get(snapshot, settings, clock.GetUtcNow());

    public RuntimeEstimate Get(DeviceSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            var reason = UnavailableReason(snapshot, settings, now);
            if (reason is not null)
            {
                periods.Remove(snapshot.Device.Id);
                return Unavailable(reason);
            }
            if (!periods.TryGetValue(snapshot.Device.Id, out var period))
                return Calculating("A new uninterrupted discharge period needs more observed samples.", snapshot.Battery.SampledAt);
            if (!SameIdentity(period.Identity, snapshot.Device))
            {
                periods.Remove(snapshot.Device.Id);
                return Calculating("The peripheral association changed; a new uninterrupted discharge period is required.", snapshot.Battery.SampledAt);
            }
            if (period.Last.At != snapshot.Battery.SampledAt || period.Last.Percentage != snapshot.Battery.Percentage)
                return Calculating("Waiting for this new battery sample to enter the discharge trend.", snapshot.Battery.SampledAt);
            return period.Estimate;
        }
    }

    public void Interrupt(string reason = "Monitoring interrupted.")
    {
        // No attempt is made to estimate consumption through sleep, reconnect,
        // uncertain association, or application restart.
        lock (gate) periods.Clear();
    }

    private void Start(PeripheralIdentity identity, Sample sample)
    {
        if (!periods.ContainsKey(identity.Id) && periods.Count >= MaximumDevices)
            periods.Remove(periods.MinBy(pair => pair.Value.Last.At).Key);
        periods[identity.Id] = new(identity, sample);
    }

    private static RuntimeEstimate Calculate(Period period)
    {
        var last = period.Last;
        var first = period.Samples[0];
        var allCoverage = last.At - first.At;
        if (allCoverage < MinimumCoverage || period.Drops.Count < 2 || first.Percentage - last.Percentage < 3)
            return Calculating("Collecting at least 20 minutes and multiple meaningful percentage drops; flat/coarse levels do not imply a known runtime.",
                last.At, period.Samples.Count, allCoverage);
        // Use recent consumption while retaining enough coarse steps to resolve a
        // rate. A minimum window smooths jitter; old consumption loses influence.
        var desired = Before(last.At, allCoverage >= ConfidentCoverage ? ConfidentCoverage : MinimumCoverage);
        if (period.Drops.Count >= 4 && period.Drops[^4].At < desired) desired = period.Drops[^4].At;
        else if (period.Drops.Count < 4) desired = first.At;
        var start = period.Samples.FindLastIndex(sample => sample.At < desired);
        if (start < 0) start = 0;
        var samples = period.Samples.Skip(start).ToArray();
        if (samples[^1].At != last.At) samples = samples.Append(last).ToArray();
        var coverage = samples[^1].At - samples[0].At;
        if (coverage < MinimumCoverage || samples.Length < 4)
            return Calculating("More timed discharge observations are needed to distinguish coarse gauge steps from consumption.", last.At, samples.Length, coverage);
        var drops = period.Drops.Where(drop => drop.At > samples[0].At).ToArray();
        var observedDrop = samples[0].Percentage - last.Percentage;
        if (observedDrop < 3 || drops.Length < 2)
            return Calculating("Recent discharge has not yet crossed enough distinct reported levels.", last.At, samples.Length, coverage);
        var rate = Fit(samples);
        if (!double.IsFinite(rate) || rate <= 0)
            return Calculating("No reliable declining percentage trend is established.", last.At, samples.Length, coverage);
        var step = drops.Max(drop => drop.Step);
        var midpoint = samples[0].At + TimeSpan.FromTicks(coverage.Ticks / 2);
        var early = samples.Where(sample => sample.At <= midpoint).ToArray();
        var late = samples.Where(sample => sample.At >= midpoint).ToArray();
        var earlyRate = early.Length >= 3 ? Fit(early) : double.NaN;
        var lateRate = late.Length >= 3 ? Fit(late) : double.NaN;
        var changing = double.IsFinite(earlyRate) && double.IsFinite(lateRate) && earlyRate > 0 && lateRate > 0
            ? Math.Min(1, Math.Abs(lateRate - earlyRate) / Math.Max(earlyRate, lateRate)) : .4;
        var uncertainty = Math.Clamp(.15 + step / (double)observedDrop + changing * .6, .2, .85);
        var hours = last.Percentage / rate;
        var low = Math.Max(0, last.Percentage - step / 2.0) / (rate * (1 + uncertainty));
        var high = Math.Min(100, last.Percentage + step / 2.0) / (rate * (1 - uncertainty));
        // Keep a strict bound: floating-point rounding at TimeSpan.MaxValue can
        // otherwise overflow FromHours even when the two double values compare equal.
        if (!double.IsFinite(hours) || !double.IsFinite(low) || !double.IsFinite(high) || high >= TimeSpan.MaxValue.TotalHours)
            return Unavailable("The observed trend does not yield a representable runtime range.");
        var confident = coverage >= ConfidentCoverage && drops.Length >= 3 && observedDrop >= Math.Max(3, 3 * step) &&
            uncertainty <= .5 && changing <= .35;
        var remaining = TimeSpan.FromHours(hours);
        var lower = TimeSpan.FromHours(low);
        var upper = TimeSpan.FromHours(high);
        return new(RuntimeEstimateState.Available, Format(remaining, lower, upper, confident),
            "Estimate from fresh observed discharge percentages; the range includes gauge-step and rate-change uncertainty. Firmware caching and future gaming load can change actual runtime.",
            remaining, lower, upper, confident, last.At, samples.Length, coverage, rate);
    }

    private static double Fit(Sample[] samples)
    {
        if (samples.Length < 2) return double.NaN;
        var first = samples[0].At;
        var end = samples[^1].At;
        var spanHours = (end - first).TotalHours;
        if (spanHours <= 0) return double.NaN;
        var halfLife = Math.Max(1.0 / 6, spanHours / 3);
        double sum = 0, sx = 0, sy = 0;
        var points = new (double X, double Y, double W)[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var left = index == 0 ? (samples[1].At - sample.At).TotalHours : (sample.At - samples[index - 1].At).TotalHours;
            var right = index == samples.Length - 1 ? left : (samples[index + 1].At - sample.At).TotalHours;
            var weight = Math.Max(1e-9, (left + right) / 2) * Math.Pow(.5, (end - sample.At).TotalHours / halfLife);
            var x = (sample.At - first).TotalHours;
            points[index] = (x, sample.Percentage, weight);
            sum += weight; sx += x * weight; sy += sample.Percentage * weight;
        }
        var mx = sx / sum; var my = sy / sum;
        double covariance = 0, variance = 0;
        foreach (var point in points)
        {
            covariance += point.W * (point.X - mx) * (point.Y - my);
            variance += point.W * (point.X - mx) * (point.X - mx);
        }
        return variance <= 0 ? double.NaN : -covariance / variance;
    }

    private static string Format(TimeSpan remaining, TimeSpan lower, TimeSpan upper, bool confident)
    {
        if (upper.TotalMinutes <= 15) return "Under 15 minutes remaining";
        if (confident && remaining.TotalHours >= 2)
        {
            var hours = Math.Max(1, (int)Math.Round(remaining.TotalHours, MidpointRounding.AwayFromZero));
            return hours >= 48 ? $"About {Math.Max(2, (int)Math.Round(hours / 24.0))} days remaining"
                : $"About {hours} hours remaining";
        }
        if (upper.TotalMinutes <= 120)
        {
            var low = Math.Max(0, (int)Math.Floor(lower.TotalMinutes / 15) * 15);
            var high = Math.Max(15, (int)Math.Ceiling(upper.TotalMinutes / 15) * 15);
            return low == 0 ? $"Under {high} minutes remaining" : $"{low}–{high} minutes remaining";
        }
        var lowHours = Math.Max(0, (int)Math.Floor(lower.TotalHours));
        var highHours = Math.Max(lowHours + 1, (int)Math.Ceiling(upper.TotalHours));
        if (highHours >= 72)
        {
            var lowDays = Math.Max(0, (int)Math.Floor(lower.TotalDays));
            var highDays = Math.Max(lowDays + 1, (int)Math.Ceiling(upper.TotalDays));
            return lowDays == 0 ? $"Under {highDays} days remaining" : $"{lowDays}–{highDays} days remaining";
        }
        if (lowHours == 0) return $"Under {highHours} hours remaining";
        return $"{lowHours}–{highHours} hours remaining";
    }

    private static string? UnavailableReason(DeviceSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Device.Id) || string.IsNullOrWhiteSpace(snapshot.Device.ReceiverId) ||
            snapshot.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal))
            return "Peripheral identity is missing or uncertain.";
        if (snapshot.Battery.Percentage is null) return "Reported categories or unavailable battery fields cannot supply a percentage-based runtime estimate.";
        if (!BatteryPresentation.IsFreshPercentage(snapshot, settings, now)) return "A fresh timestamped percentage is required; stale readings are excluded.";
        if (snapshot.Connection != ConnectionState.Connected) return "A confirmed peripheral association is required; receiver presence is insufficient.";
        if (snapshot.Battery.Charging != ChargingState.Discharging) return "A known uninterrupted discharge period is required; charging and uncertain charging state are excluded.";
        return null;
    }

    private static DateTimeOffset Before(DateTimeOffset at, TimeSpan duration) => at.UtcTicks < duration.Ticks
        ? DateTimeOffset.MinValue : at.ToUniversalTime() - duration;
    private static bool SameIdentity(PeripheralIdentity left, PeripheralIdentity right) => left.Id == right.Id &&
        left.ReceiverId == right.ReceiverId && left.ModelId == right.ModelId && left.Vendor == right.Vendor;
    private static TimeSpan FreshnessBudget(AppSettings settings) => TimeSpan.FromSeconds(2 * Math.Clamp(settings.RefreshIntervalSeconds, 10, 3600) + 5);
    private static RuntimeEstimate Unavailable(string detail) => new(RuntimeEstimateState.Unavailable, "Unavailable", detail);
    private static RuntimeEstimate Calculating(string detail, DateTimeOffset? at = null, int count = 0, TimeSpan coverage = default) =>
        new(RuntimeEstimateState.Calculating, "Calculating…", detail, BasedOnSampleAt: at, SampleCount: count, Coverage: coverage);
    private sealed record Sample(DateTimeOffset At, int Percentage);
    private sealed record Drop(DateTimeOffset At, int Step);
    private sealed class Period(PeripheralIdentity identity, Sample first)
    {
        public PeripheralIdentity Identity { get; } = identity;
        public List<Sample> Samples { get; } = [first];
        public List<Drop> Drops { get; } = [];
        public Sample Last = first;
        public int Lowest = first.Percentage;
        public RuntimeEstimate Estimate = Calculating("Collecting a meaningful uninterrupted discharge trend.", first.At, 1);
    }
}
