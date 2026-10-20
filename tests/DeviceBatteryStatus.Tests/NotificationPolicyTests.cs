using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void FirstFreshDischargingReadingAtOrBelowThresholdCanNotifyIncludingGenuineZero(int percentage)
    {
        var policy = new NotificationPolicy();

        var alert = Assert.Single(policy.Evaluate([Device("mouse", percentage)], new AppSettings(), Now));

        Assert.Equal("mouse", alert.DeviceId);
        Assert.Equal("mouse", alert.Name);
        Assert.Equal(percentage, alert.Percentage);
    }

    [Fact]
    public void AboveThresholdDoesNotNotify()
    {
        var policy = new NotificationPolicy();

        Assert.Empty(policy.Evaluate([Device("mouse", 21)], new AppSettings(), Now));
    }

    [Fact]
    public void DisabledNotificationsDoNotEmitAlerts()
    {
        var policy = new NotificationPolicy();

        Assert.Empty(policy.Evaluate([Device("mouse", 0)],
            new AppSettings { LowBatteryNotifications = false }, Now));
    }

    [Theory]
    [InlineData(ChargingState.Charging)]
    [InlineData(ChargingState.Full)]
    public void KnownChargingOrFullReadingsDoNotTriggerLowBatteryAlerts(ChargingState charging)
    {
        var device = Device("mouse", 0) with { Battery = BatteryReading.Current(0, charging, Now) };

        Assert.Empty(new NotificationPolicy().Evaluate([device], new AppSettings(), Now));
    }

    [Fact]
    public void ARealLowReadingCanNotifyWhenTheDeviceHasNoChargingCapability()
    {
        var device = Device("mouse", 0) with
        {
            Battery = BatteryReading.Current(0, ChargingState.Unknown, Now)
        };

        var alert = Assert.Single(new NotificationPolicy().Evaluate([device], new AppSettings(), Now));

        Assert.Equal(0, alert.Percentage);
        Assert.Equal("mouse", alert.DeviceId);
        Assert.Equal(ChargingState.Unknown, device.Battery.Charging);
    }

    [Fact]
    public void StaleExpiredFutureAndDisconnectedReadingsCannotTriggerStartupAlerts()
    {
        var stale = Device("stale", 0);
        stale = stale with { Battery = stale.Battery.AsStale("Read failed.") };
        var expired = Device("expired", 0, Now.AddSeconds(-66));
        var future = Device("future", 0, Now.AddTicks(1));
        var disconnected = Device("disconnected", 0) with { Connection = ConnectionState.Disconnected };
        var failed = Device("failed", 0) with
        {
            Battery = BatteryReading.Unavailable(ReadingState.Failed, "No battery response.")
        };

        Assert.Empty(new NotificationPolicy().Evaluate(
            [stale, expired, future, disconnected, failed], new AppSettings(), Now));
    }

    [Fact]
    public void RepeatedAndNewLowSamplesDoNotSpamTheSameDevice()
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings();
        var low = Device("mouse", 10);

        Assert.Single(policy.Evaluate([low], settings, Now));
        Assert.Empty(policy.Evaluate([low], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([Device("mouse", 9, Now.AddSeconds(2))], settings, Now.AddSeconds(2)));
    }

    [Fact]
    public void DevicesAreDeduplicatedIndependentlyAndAlertsUseCustomNames()
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings
        {
            CustomNames = new Dictionary<string, string> { ["mouse"] = "Work mouse" }
        };

        var first = Assert.Single(policy.Evaluate([Device("mouse", 10)], settings, Now));
        Assert.Equal("Work mouse", first.Name);
        var second = Assert.Single(policy.Evaluate(
            [Device("mouse", 9, Now.AddSeconds(1)), Device("keyboard", 15, Now.AddSeconds(1))],
            settings, Now.AddSeconds(1)));
        Assert.Equal("keyboard", second.DeviceId);
    }

    [Fact]
    public void ARecoveryMustReachFivePointsAboveThresholdBeforeAnotherLowAlert()
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings();

        Assert.Single(policy.Evaluate([Device("mouse", 20)], settings, Now));
        Assert.Empty(policy.Evaluate([Device("mouse", 24, Now.AddSeconds(1))], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([Device("mouse", 19, Now.AddSeconds(2))], settings, Now.AddSeconds(2)));
        Assert.Empty(policy.Evaluate([Device("mouse", 25, Now.AddSeconds(3))], settings, Now.AddSeconds(3)));
        Assert.Single(policy.Evaluate([Device("mouse", 20, Now.AddSeconds(4))], settings, Now.AddSeconds(4)));
    }

    [Fact]
    public void AStaleHighValueOrAbsentDeviceDoesNotRearmLowBatteryAlerts()
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings();
        Assert.Single(policy.Evaluate([Device("mouse", 10)], settings, Now));
        var staleHigh = Device("mouse", 90, Now.AddSeconds(1));
        staleHigh = staleHigh with { Battery = staleHigh.Battery.AsStale("Unavailable.") };

        Assert.Empty(policy.Evaluate([staleHigh], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([], settings, Now.AddSeconds(2)));
        Assert.Empty(policy.Evaluate([Device("mouse", 9, Now.AddSeconds(3))], settings, Now.AddSeconds(3)));
    }

    [Fact]
    public void ChargingAtALowPercentageDoesNotRearmAPreviouslyDeliveredAlert()
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings();
        Assert.Single(policy.Evaluate([Device("mouse", 10)], settings, Now));
        var chargingLow = Device("mouse", 11, Now.AddSeconds(1)) with
        {
            Battery = BatteryReading.Current(11, ChargingState.Charging, Now.AddSeconds(1))
        };

        Assert.Empty(policy.Evaluate([chargingLow], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([Device("mouse", 10, Now.AddSeconds(2))], settings, Now.AddSeconds(2)));
    }

    [Fact]
    public void ChangingTheThresholdDoesNotRepeatAnAlreadyDeliveredLowSample()
    {
        var policy = new NotificationPolicy();
        var device = Device("mouse", 10);

        Assert.Single(policy.Evaluate([device], new AppSettings(), Now));
        Assert.Empty(policy.Evaluate([device], new AppSettings { LowBatteryThreshold = 30 }, Now.AddSeconds(1)));
    }

    private static DeviceSnapshot Device(string id, int percentage, DateTimeOffset? sampledAt = null) =>
        new(new PeripheralIdentity(id, id, DeviceVendor.Logitech, $"receiver-{id}"),
            BatteryReading.Current(percentage, ChargingState.Discharging, sampledAt ?? Now), ConnectionState.Connected);
}
