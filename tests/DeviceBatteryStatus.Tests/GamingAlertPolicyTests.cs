using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Runtime;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class GamingAlertPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, RuntimeEstimate> NoRuntime = new Dictionary<string, RuntimeEstimate>();

    [Theory]
    [InlineData(0, GamingAlertSeverity.Critical)]
    [InlineData(10, GamingAlertSeverity.Critical)]
    [InlineData(11, GamingAlertSeverity.Low)]
    [InlineData(20, GamingAlertSeverity.Low)]
    public void RealPercentageIncludingZeroAlertsWithoutAUsableRuntime(int percentage, GamingAlertSeverity severity)
    {
        var alert = Assert.Single(new GamingAlertPolicy().Evaluate([Device(percentage)], new(), NoRuntime, Now));
        Assert.Equal(percentage, alert.Percentage);
        Assert.Equal(severity, alert.Severity);
        Assert.Equal("Unavailable", alert.RuntimeText);
        Assert.Null(alert.Category);
    }

    [Theory]
    [InlineData(BatteryCategory.Low, GamingAlertSeverity.Low)]
    [InlineData(BatteryCategory.Critical, GamingAlertSeverity.Critical)]
    public void NativeCategoriesAlertWithoutInventingPercentagesOrRuntime(BatteryCategory category, GamingAlertSeverity severity)
    {
        var device = Device(90) with { Battery = BatteryReading.CurrentCategory(category, ChargingState.Unknown, Now) };
        var alert = Assert.Single(new GamingAlertPolicy().Evaluate([device], Settings(new() { Mode = GamingAlertMode.Runtime }), NoRuntime, Now));
        Assert.Null(alert.Percentage);
        Assert.Equal(category, alert.Category);
        Assert.Equal(severity, alert.Severity);
        Assert.Equal("Unavailable", alert.RuntimeText);
    }

    [Theory]
    [InlineData(BatteryCategory.Unknown)]
    [InlineData(BatteryCategory.Good)]
    [InlineData(BatteryCategory.High)]
    [InlineData(BatteryCategory.Full)]
    public void OtherCategoriesDoNotAlert(BatteryCategory category)
    {
        var device = Device(90) with { Battery = BatteryReading.CurrentCategory(category, ChargingState.Unknown, Now) };
        Assert.Empty(new GamingAlertPolicy().Evaluate([device], new(), NoRuntime, Now));
    }

    [Fact]
    public void FreshRealPercentageCanAlertWithUnknownLinkOrChargingCapability()
    {
        var device = Device(15) with { Connection = ConnectionState.Unknown,
            Battery = BatteryReading.Current(15, ChargingState.Unknown, Now) };
        Assert.Single(new GamingAlertPolicy().Evaluate([device], new(), NoRuntime, Now));
    }

    [Fact]
    public void InvalidFreshnessDisconnectAndConfirmedChargingCannotAlert()
    {
        var valid = Device(10);
        DeviceSnapshot[] invalid = [valid with { Battery = valid.Battery.AsStale("Failure") },
            Device(10, Now.AddSeconds(-66)), Device(10, Now.AddTicks(1)),
            valid with { Connection = ConnectionState.Disconnected },
            valid with { Battery = BatteryReading.Current(10, ChargingState.Charging, Now) },
            valid with { Battery = BatteryReading.Current(10, ChargingState.Full, Now) },
            valid with { Battery = BatteryReading.Unavailable(ReadingState.Failed, "No reply") }];
        foreach (var device in invalid)
            Assert.Empty(new GamingAlertPolicy().Evaluate([device], new(), NoRuntime, Now));
    }

    [Fact]
    public void QueueingDoesNotConsumeDeliveryButSuccessfulDisplayDoes()
    {
        var policy = new GamingAlertPolicy();
        var first = Assert.Single(policy.Evaluate([Device(15)], new(), NoRuntime, Now));
        Assert.Single(policy.Evaluate([Device(15)], new(), NoRuntime, Now.AddSeconds(1)));
        policy.Acknowledge(first, Now);
        Assert.Empty(policy.Evaluate([Device(15)], new(), NoRuntime, Now.AddSeconds(1)));
        Assert.NotNull(policy.Revalidate(first, [Device(15)], new(), NoRuntime, Now.AddSeconds(1)));
    }

    [Fact]
    public void SustainedLowDoesNotRepeatAfterCooldownAndCriticalCanEscalateImmediately()
    {
        var policy = new GamingAlertPolicy();
        policy.Acknowledge(Assert.Single(policy.Evaluate([Device(15)], new(), NoRuntime, Now)), Now);
        Assert.Empty(policy.Evaluate([Device(14, Now.AddHours(1))], new(), NoRuntime, Now.AddHours(1)));
        var critical = Assert.Single(policy.Evaluate([Device(10, Now.AddSeconds(5))], new(), NoRuntime, Now.AddSeconds(5)));
        Assert.Equal(GamingAlertSeverity.Critical, critical.Severity);
        policy.Acknowledge(critical, Now.AddSeconds(5));
        Assert.Empty(policy.Evaluate([Device(9, Now.AddHours(2))], new(), NoRuntime, Now.AddHours(2)));
    }

    [Fact]
    public void RecoveryNeedsConfiguredHysteresisAndCooldown()
    {
        var policy = new GamingAlertPolicy();
        policy.Acknowledge(Assert.Single(policy.Evaluate([Device(20)], new(), NoRuntime, Now)), Now);
        Assert.Empty(policy.Evaluate([Device(24, Now.AddMinutes(1))], new(), NoRuntime, Now.AddMinutes(1)));
        Assert.Empty(policy.Evaluate([Device(20, Now.AddMinutes(16))], new(), NoRuntime, Now.AddMinutes(16)));
        Assert.Empty(policy.Evaluate([Device(25, Now.AddMinutes(17))], new(), NoRuntime, Now.AddMinutes(17)));
        var second = Assert.Single(policy.Evaluate([Device(20, Now.AddMinutes(18))], new(), NoRuntime, Now.AddMinutes(18)));
        policy.Acknowledge(second, Now.AddMinutes(18));
        Assert.Empty(policy.Evaluate([Device(25, Now.AddMinutes(19))], new(), NoRuntime, Now.AddMinutes(19)));
        Assert.Empty(policy.Evaluate([Device(20, Now.AddMinutes(20))], new(), NoRuntime, Now.AddMinutes(20)));
        Assert.Single(policy.Evaluate([Device(20, Now.AddMinutes(34))], new(), NoRuntime, Now.AddMinutes(34)));
    }

    [Fact]
    public void StaleAbsentOrOldRecoverySamplesNeverRearm()
    {
        var policy = new GamingAlertPolicy();
        policy.Acknowledge(Assert.Single(policy.Evaluate([Device(20)], new(), NoRuntime, Now)), Now);
        Assert.Empty(policy.Evaluate([Device(90, Now.AddMinutes(1)) with { Battery = Device(90).Battery.AsStale("Stale") }], new(), NoRuntime, Now.AddMinutes(1)));
        Assert.Empty(policy.Evaluate([], new(), NoRuntime, Now.AddMinutes(2)));
        Assert.Empty(policy.Evaluate([Device(90, Now.AddSeconds(-1))], new(), NoRuntime, Now));
        Assert.Empty(policy.Evaluate([Device(20, Now.AddHours(1))], new(), NoRuntime, Now.AddHours(1)));
    }

    [Fact]
    public void CategoryNeedsNativeGoodRecoveryNotLowCriticalFluctuation()
    {
        var policy = new GamingAlertPolicy();
        DeviceSnapshot Category(BatteryCategory category, int minute) => Device(90, Now.AddMinutes(minute)) with
        { Battery = BatteryReading.CurrentCategory(category, ChargingState.Discharging, Now.AddMinutes(minute)) };
        policy.Acknowledge(Assert.Single(policy.Evaluate([Category(BatteryCategory.Critical, 0)], new(), NoRuntime, Now)), Now);
        Assert.Empty(policy.Evaluate([Category(BatteryCategory.Low, 20)], new(), NoRuntime, Now.AddMinutes(20)));
        Assert.Empty(policy.Evaluate([Category(BatteryCategory.Critical, 21)], new(), NoRuntime, Now.AddMinutes(21)));
        Assert.Empty(policy.Evaluate([Category(BatteryCategory.Good, 22)], new(), NoRuntime, Now.AddMinutes(22)));
        Assert.Single(policy.Evaluate([Category(BatteryCategory.Low, 23)], new(), NoRuntime, Now.AddMinutes(23)));
    }

    [Fact]
    public void RuntimeThresholdUsesConfidentUpperBoundRatherThanPointEstimate()
    {
        var policy = new GamingAlertPolicy();
        var settings = Settings(new() { Mode = GamingAlertMode.Runtime });
        var device = Device(80);
        Assert.Empty(policy.Evaluate([device], settings, Runtime(device, 40, 30, 70), Now));
        Assert.Empty(policy.Evaluate([device], settings, Runtime(device, 40, 30, 50, false), Now));
        var low = Assert.Single(policy.Evaluate([device], settings, Runtime(device, 40, 30, 50), Now));
        Assert.Equal(GamingAlertSeverity.Low, low.Severity);
        var critical = Assert.Single(policy.Evaluate([device], settings, Runtime(device, 10, 5, 15), Now));
        Assert.Equal(GamingAlertSeverity.Critical, critical.Severity);
    }

    [Fact]
    public void RuntimeRequiresSameCurrentSampleConnectedDischargingAndValidBounds()
    {
        var device = Device(80);
        var settings = Settings(new() { Mode = GamingAlertMode.Runtime });
        var original = Runtime(device, 10, 5, 15);
        var estimate = original["mouse"];
        RuntimeEstimate[] invalid = [estimate with { BasedOnSampleAt = Now.AddSeconds(-1) },
            estimate with { State = RuntimeEstimateState.Calculating }, estimate with { LowerBound = TimeSpan.FromMinutes(-1) },
            estimate with { UpperBound = TimeSpan.FromMinutes(2) }, estimate with { Remaining = TimeSpan.FromMinutes(30) }];
        foreach (var value in invalid)
            Assert.Empty(new GamingAlertPolicy().Evaluate([device], settings, new Dictionary<string, RuntimeEstimate> { ["mouse"] = value }, Now));
        Assert.Empty(new GamingAlertPolicy().Evaluate([device with { Connection = ConnectionState.Unknown }], settings, original, Now));
        Assert.Empty(new GamingAlertPolicy().Evaluate([device with { Battery = BatteryReading.Current(80, ChargingState.Unknown, Now) }], settings, original, Now));
    }

    [Fact]
    public void BothModesCoalesceConditionsAndRuntimeRecoveryUsesConservativeLowerBound()
    {
        var policy = new GamingAlertPolicy();
        var settings = Settings(new() { Mode = GamingAlertMode.Both });
        var device = Device(15);
        var first = Assert.Single(policy.Evaluate([device], settings, Runtime(device, 40, 30, 50), Now));
        Assert.Equal(2, first.Conditions.Count);
        policy.Acknowledge(first, Now);
        var next = Device(15, Now.AddMinutes(20));
        Assert.Empty(policy.Evaluate([next], settings, Runtime(next, 80, 65, 95), Now.AddMinutes(20)));
        next = Device(15, Now.AddMinutes(21));
        Assert.Empty(policy.Evaluate([next], settings, Runtime(next, 85, 75, 95), Now.AddMinutes(21)));
        next = Device(15, Now.AddMinutes(22));
        var second = Assert.Single(policy.Evaluate([next], settings, Runtime(next, 40, 30, 50), Now.AddMinutes(22)));
        Assert.Equal(GamingAlertCondition.Runtime, Assert.Single(second.Conditions));
    }

    [Fact]
    public void MasterSnoozeOverrideSnoozeAndDisabledRulesAreRecheckedBeforeDisplay()
    {
        var policy = new GamingAlertPolicy();
        var candidate = Assert.Single(policy.Evaluate([Device(15)], new(), NoRuntime, Now));
        AppSettings[] suppressed = [new() { LowBatteryNotifications = false }, Settings(new() { Enabled = false }),
            Settings(new() { SnoozedUntil = Now.AddHours(1) }),
            Settings(new(), new() { SnoozedUntil = Now.AddHours(1) })];
        foreach (var settings in suppressed)
        {
            Assert.Empty(policy.Evaluate([Device(15)], settings, NoRuntime, Now));
            Assert.Null(policy.Revalidate(candidate, [Device(15)], settings, NoRuntime, Now));
        }
        Assert.Null(policy.Revalidate(candidate, [Device(90)], new(), NoRuntime, Now));
        Assert.Null(policy.Revalidate(candidate, [Device(15) with
        { Battery = BatteryReading.Current(15, ChargingState.Charging, Now) }], new(), NoRuntime, Now));
        Assert.Null(policy.Revalidate(candidate, [Device(15)], new(), NoRuntime, Now.AddSeconds(66)));
        Assert.Null(policy.Revalidate(candidate, [], new(), NoRuntime, Now));
    }

    [Fact]
    public void GlobalSnoozeAlsoSuppressesAnEnabledPerDeviceOverride()
    {
        var settings = Settings(new() { SnoozedUntil = Now.AddMinutes(1) }, new() { LowPercentage = 30 });
        Assert.Empty(new GamingAlertPolicy().Evaluate([Device(15)], settings, NoRuntime, Now));
        Assert.Single(new GamingAlertPolicy().Evaluate([Device(15, Now.AddMinutes(2))], settings, NoRuntime, Now.AddMinutes(2)));
    }

    [Fact]
    public void DeviceOverridesAndNamesAreAppliedWithoutMutatingGlobalDefaults()
    {
        var settings = Settings(new() { LowPercentage = 10, CriticalPercentage = 5 }, new() { LowPercentage = 30 });
        settings = settings with { CustomNames = new Dictionary<string, string> { ["mouse"] = "Gaming mouse" } };
        var alert = Assert.Single(new GamingAlertPolicy().Evaluate([Device(25), Device(25) with
        { Device = new("other", "other", DeviceVendor.Razer, "receiver2") }], settings, NoRuntime, Now));
        Assert.Equal("Gaming mouse", alert.Name);
        Assert.Equal(30, alert.Rule.LowPercentage);
        Assert.Equal(10, settings.GamingAlerts!.GlobalRule.LowPercentage);
    }

    [Fact]
    public void PreviewAcknowledgementNeverConsumesARealAlert()
    {
        var policy = new GamingAlertPolicy();
        var alert = Assert.Single(policy.Evaluate([Device(15)], new(), NoRuntime, Now));
        policy.Acknowledge(alert with { IsPreview = true }, Now);
        Assert.Single(policy.Evaluate([Device(15)], new(), NoRuntime, Now));
    }

    [Fact]
    public void RevalidationKeepsOnlyTheOriginallyEligibleSourcesAndCreationTime()
    {
        var policy = new GamingAlertPolicy();
        var settings = Settings(new() { Mode = GamingAlertMode.Both });
        var device = Device(80);
        var runtimeCritical = Assert.Single(policy.Evaluate([device], settings, Runtime(device, 10, 5, 15), Now));
        policy.Acknowledge(runtimeCritical, Now);
        var later = Now.AddMinutes(16);
        device = Device(15, later);
        var percentageLow = Assert.Single(policy.Evaluate([device], settings, Runtime(device, 10, 5, 15), later));
        Assert.Equal(GamingAlertCondition.Percentage, Assert.Single(percentageLow.Conditions));
        var revalidated = Assert.IsType<GamingBatteryAlert>(policy.Revalidate(percentageLow, [device], settings, Runtime(device, 10, 5, 15), later.AddSeconds(1)));
        Assert.Equal(GamingAlertSeverity.Low, revalidated.Severity);
        Assert.Equal(later, revalidated.CreatedAt);
        Assert.Equal(GamingAlertCondition.Percentage, Assert.Single(revalidated.Conditions));
    }

    private static DeviceSnapshot Device(int percentage, DateTimeOffset? at = null) =>
        new(new("mouse", "Mouse", DeviceVendor.Razer, "receiver"), BatteryReading.Current(percentage, ChargingState.Discharging, at ?? Now), ConnectionState.Connected);

    private static AppSettings Settings(GamingAlertRule global, GamingAlertRule? device = null) => new()
    { GamingAlerts = new() { GlobalRule = global, DeviceOverrides = device is null ? new Dictionary<string, GamingAlertRule>() : new Dictionary<string, GamingAlertRule> { ["mouse"] = device } } };

    private static IReadOnlyDictionary<string, RuntimeEstimate> Runtime(DeviceSnapshot device, int remaining, int lower, int upper, bool confident = true) =>
        new Dictionary<string, RuntimeEstimate> { [device.Device.Id] = new(RuntimeEstimateState.Available, "About 40 minutes remaining", "Synthetic",
            TimeSpan.FromMinutes(remaining), TimeSpan.FromMinutes(lower), TimeSpan.FromMinutes(upper), confident, device.Battery.SampledAt) };
}
