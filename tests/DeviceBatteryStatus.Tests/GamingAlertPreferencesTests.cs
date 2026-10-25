using System.Collections;
using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Runtime;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class GamingAlertPreferencesTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, RuntimeEstimate> NoRuntime = new Dictionary<string, RuntimeEstimate>();

    [Fact]
    public void PreparationCopiesInputDictionariesOnceInsteadOfOnEveryPolicyTick()
    {
        var names = new CountingDictionary<string> { ["mouse"] = "Gaming mouse" };
        var rules = new CountingDictionary<GamingAlertRule> { ["mouse"] = new() { LowPercentage = 30 } };
        var raw = new AppSettings { CustomNames = names, GamingAlerts = new() { DeviceOverrides = rules } };
        var preferences = GamingAlertPreferences.Create(raw);
        Assert.Equal(1, names.Enumerations);
        Assert.Equal(1, rules.Enumerations);
        var policy = new GamingAlertPolicy();
        for (var tick = 0; tick < 50; tick++)
        {
            var now = Epoch.AddSeconds(tick);
            var candidate = Assert.Single(policy.Evaluate(preferences, [Device(25)], NoRuntime, now));
            Assert.NotNull(policy.Revalidate(preferences, candidate, [Device(25)], NoRuntime, now));
        }
        Assert.Equal(1, names.Enumerations);
        Assert.Equal(1, rules.Enumerations);
    }

    [Fact]
    public void PreparedPreferencesOwnAnIndependentSnapshotOfCallerMutableCollections()
    {
        var names = new Dictionary<string, string> { ["mouse"] = "Original name" };
        var overrides = new Dictionary<string, GamingAlertRule> { ["mouse"] = new() { LowPercentage = 30 } };
        var raw = new AppSettings { CustomNames = names, GamingAlerts = new() { DeviceOverrides = overrides } };
        var prepared = GamingAlertPreferences.Create(raw);
        names["mouse"] = "Changed name";
        overrides["mouse"] = new() { LowPercentage = 10, CriticalPercentage = 5 };
        var policy = new GamingAlertPolicy();

        var candidate = Assert.Single(policy.Evaluate(prepared, [Device(25)], NoRuntime, Epoch));

        Assert.Equal("Original name", candidate.Name);
        Assert.Equal(30, candidate.Rule.LowPercentage);
        Assert.NotNull(policy.Revalidate(prepared, candidate, [Device(25)], NoRuntime, Epoch));
        Assert.Empty(policy.Evaluate([Device(25)], raw, NoRuntime, Epoch));
        var refreshed = GamingAlertPreferences.Create(raw);
        Assert.Empty(policy.Evaluate(refreshed, [Device(25)], NoRuntime, Epoch));
    }

    [Fact]
    public void OriginalApisStillObserveCurrentRawSettingsWithoutDoubleCopyingOverrides()
    {
        var names = new CountingDictionary<string> { ["mouse"] = "Original name" };
        var rules = new CountingDictionary<GamingAlertRule> { ["mouse"] = new() { LowPercentage = 30 } };
        var settings = new AppSettings { CustomNames = names, GamingAlerts = new() { DeviceOverrides = rules } };
        var policy = new GamingAlertPolicy();
        var candidate = Assert.Single(policy.Evaluate([Device(25)], settings, NoRuntime, Epoch));
        Assert.Equal(1, names.Enumerations);
        Assert.Equal(1, rules.Enumerations);
        Assert.NotNull(policy.Revalidate(candidate, [Device(25)], settings, NoRuntime, Epoch));
        Assert.Equal(2, names.Enumerations);
        Assert.Equal(2, rules.Enumerations);
        rules["mouse"] = new() { LowPercentage = 10, CriticalPercentage = 5 };
        Assert.Empty(policy.Evaluate([Device(25)], settings, NoRuntime, Epoch));
        Assert.Equal(3, rules.Enumerations);
    }

    [Fact]
    public void PreparedChecksStillEnforceFreshnessChargingAndQueuedAlertRelevance()
    {
        var policy = new GamingAlertPolicy();
        var preferences = GamingAlertPreferences.Create(new());
        var sample = Device(15);
        var candidate = Assert.Single(policy.Evaluate(preferences, [sample], NoRuntime, Epoch));
        Assert.NotNull(policy.Revalidate(preferences, candidate, [sample], NoRuntime, Epoch.AddSeconds(65)));
        Assert.Null(policy.Revalidate(preferences, candidate, [sample], NoRuntime, Epoch.AddSeconds(66)));
        Assert.Empty(policy.Evaluate(preferences, [sample], NoRuntime, Epoch.AddSeconds(66)));
        var charging = sample with { Battery = BatteryReading.Current(15, ChargingState.Charging, Epoch) };
        Assert.Null(policy.Revalidate(preferences, candidate, [charging], NoRuntime, Epoch));
        Assert.Empty(policy.Evaluate(preferences, [charging], NoRuntime, Epoch));
        Assert.Null(policy.Revalidate(preferences, candidate, [Device(80)], NoRuntime, Epoch));
        Assert.Null(policy.Revalidate(preferences, candidate, [], NoRuntime, Epoch));
        var off = GamingAlertPreferences.Create(new() { LowBatteryNotifications = false });
        Assert.Null(policy.Revalidate(off, candidate, [sample], NoRuntime, Epoch));
        Assert.Empty(policy.Evaluate(off, [sample], NoRuntime, Epoch));
    }

    [Fact]
    public void PreparedChecksStillApplyAcknowledgementRecoveryCooldownAndCriticalEscalation()
    {
        var policy = new GamingAlertPolicy();
        var preferences = GamingAlertPreferences.Create(new());
        var low = Assert.Single(policy.Evaluate(preferences, [Device(20)], NoRuntime, Epoch));
        policy.Acknowledge(low, Epoch);
        Assert.Empty(policy.Evaluate(preferences, [Device(20)], NoRuntime, Epoch.AddSeconds(1)));
        // A successful display must not prematurely dismiss the visible candidate.
        Assert.NotNull(policy.Revalidate(preferences, low, [Device(20)], NoRuntime, Epoch.AddSeconds(1)));
        var critical = Assert.Single(policy.Evaluate(preferences, [Device(10, 1)], NoRuntime, Epoch.AddMinutes(1)));
        Assert.Equal(GamingAlertSeverity.Critical, critical.Severity);
        policy.Acknowledge(critical, Epoch.AddMinutes(1));
        Assert.Empty(policy.Evaluate(preferences, [Device(25, 2)], NoRuntime, Epoch.AddMinutes(2)));
        Assert.Empty(policy.Evaluate(preferences, [Device(20, 3)], NoRuntime, Epoch.AddMinutes(3)));
        Assert.Single(policy.Evaluate(preferences, [Device(20, 17)], NoRuntime, Epoch.AddMinutes(17)));
    }

    [Fact]
    public void LegacyFallbackAndMalformedSettingsAreNormalizedAtPreparation()
    {
        var policy = new GamingAlertPolicy();
        var legacy = GamingAlertPreferences.Create(new() { LowBatteryThreshold = 15 });
        Assert.Empty(policy.Evaluate(legacy, [Device(16)], NoRuntime, Epoch));
        Assert.Equal(GamingAlertSeverity.Low, Assert.Single(policy.Evaluate(legacy, [Device(15)], NoRuntime, Epoch)).Severity);
        var malformed = GamingAlertPreferences.Create(new()
        {
            RefreshIntervalSeconds = -1,
            GamingAlerts = new() { GlobalRule = new() { LowPercentage = -2, CriticalPercentage = 99, DurationSeconds = 999 } }
        });
        var candidate = Assert.Single(policy.Evaluate(malformed, [Device(0)], NoRuntime, Epoch));
        Assert.Equal(1, candidate.Rule.LowPercentage);
        Assert.Equal(0, candidate.Rule.CriticalPercentage);
        Assert.Equal(30, candidate.Rule.DurationSeconds);
        Assert.Null(policy.Revalidate(malformed, candidate, [Device(0)], NoRuntime, Epoch.AddSeconds(26)));
    }

    [Fact]
    public void PreparedRuntimeAndCategoryRulesPreserveTheirNativeSources()
    {
        var policy = new GamingAlertPolicy();
        var preferences = GamingAlertPreferences.Create(new()
        { GamingAlerts = new() { GlobalRule = new() { Mode = GamingAlertMode.Runtime } } });
        var sample = Device(80);
        var estimate = new RuntimeEstimate(RuntimeEstimateState.Available, "About 40 minutes remaining", "Synthetic trend",
            TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(50), true, Epoch);
        var runtime = new Dictionary<string, RuntimeEstimate> { ["mouse"] = estimate };
        Assert.Equal(GamingAlertCondition.Runtime,
            Assert.Single(Assert.Single(policy.Evaluate(preferences, [sample], runtime, Epoch)).Conditions));
        runtime["mouse"] = estimate with { IsConfident = false };
        Assert.Empty(policy.Evaluate(preferences, [sample], runtime, Epoch));
        var category = sample with { Battery = BatteryReading.CurrentCategory(BatteryCategory.Critical, ChargingState.Unknown, Epoch) };
        var alert = Assert.Single(policy.Evaluate(preferences, [category], NoRuntime, Epoch));
        Assert.Equal(BatteryCategory.Critical, alert.Category);
        Assert.Null(alert.Percentage);
        Assert.Equal("Unavailable", alert.RuntimeText);
    }

    [Fact]
    public void GlobalAndOverrideSnoozesAreStillCheckedAgainstCurrentTime()
    {
        var policy = new GamingAlertPolicy();
        var preferences = GamingAlertPreferences.Create(new()
        {
            GamingAlerts = new()
            {
                GlobalRule = new() { SnoozedUntil = Epoch.AddMinutes(5) },
                DeviceOverrides = new Dictionary<string, GamingAlertRule>
                { ["mouse"] = new() { SnoozedUntil = Epoch.AddMinutes(10) } }
            }
        });
        Assert.Empty(policy.Evaluate(preferences, [Device(15)], NoRuntime, Epoch));
        Assert.Empty(policy.Evaluate(preferences, [Device(15, 6)], NoRuntime, Epoch.AddMinutes(6)));
        Assert.Single(policy.Evaluate(preferences, [Device(15, 11)], NoRuntime, Epoch.AddMinutes(11)));
    }

    private static DeviceSnapshot Device(int percentage, int minute = 0) =>
        new(new("mouse", "Mouse", DeviceVendor.Razer, "receiver"),
            BatteryReading.Current(percentage, ChargingState.Discharging, Epoch.AddMinutes(minute)), ConnectionState.Connected);

    private sealed class CountingDictionary<T> : IReadOnlyDictionary<string, T>
    {
        private readonly Dictionary<string, T> values = new(StringComparer.Ordinal);
        public int Enumerations { get; private set; }
        public T this[string key] { get => values[key]; set => values[key] = value; }
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<T> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, out T value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, T>> GetEnumerator()
        {
            Enumerations++;
            return values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
