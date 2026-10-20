using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class Milestone2PolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StaleZeroNeitherWinsTheTraySelectionNorCreatesAnAlert()
    {
        var stale = Device("stale", 0) with { Battery = Device("stale", 0).Battery.AsStale("Refresh failed.") };
        var fresh = Device("fresh", 31);
        var settings = new AppSettings();

        var tray = BatteryPresentation.CreateTray([stale, fresh], settings, Now);
        var alerts = new NotificationPolicy().Evaluate([stale, fresh], settings, Now);

        Assert.Equal("fresh", tray.DeviceId);
        Assert.Equal(31, tray.Percentage);
        Assert.Equal(BatteryColor.Green, tray.Color);
        Assert.Empty(alerts);
    }

    [Fact]
    public void FreshZeroIsRedAndCanNotifyWithoutInventingAChargingState()
    {
        var zero = Device("mouse", 0, charging: ChargingState.Unknown);
        var settings = new AppSettings();

        var tray = BatteryPresentation.CreateTray([zero, Device("keyboard", 50)], settings, Now);
        var alert = Assert.Single(new NotificationPolicy().Evaluate([zero], settings, Now));

        Assert.Equal("mouse", tray.DeviceId);
        Assert.Equal(0, tray.Percentage);
        Assert.Equal(BatteryColor.Red, tray.Color);
        Assert.Equal(0, alert.Percentage);
        Assert.Contains("Charging state unknown", tray.Tooltip);
    }

    [Fact]
    public void AnUnavailablePreferredDeviceKeepsTheTrayNeutralRatherThanSwitchingDevices()
    {
        var preferred = Device("preferred", 8) with
        {
            Battery = BatteryReading.Unavailable(ReadingState.Disconnected, "Receiver removed."),
            Connection = ConnectionState.Disconnected
        };
        var settings = new AppSettings { PreferredDeviceId = "preferred" };

        var disconnectedTray = BatteryPresentation.CreateTray([preferred, Device("other", 40)], settings, Now);
        var absentTray = BatteryPresentation.CreateTray([Device("other", 40)], settings, Now);

        Assert.Equal("preferred", disconnectedTray.DeviceId);
        Assert.Equal(BatteryColor.Neutral, disconnectedTray.Color);
        Assert.Null(disconnectedTray.Percentage);
        Assert.Equal("preferred", absentTray.DeviceId);
        Assert.Equal(BatteryColor.Neutral, absentTray.Color);
        Assert.Null(absentTray.Percentage);
    }

    [Fact]
    public void CustomNamesChangePresentationWithoutChangingReceiverAssociation()
    {
        var device = Device("mouse", 10);
        var settings = new AppSettings
        {
            PreferredDeviceId = "mouse",
            CustomNames = new Dictionary<string, string> { ["mouse"] = "Work mouse" }
        };

        var tray = BatteryPresentation.CreateTray([device], settings, Now);
        var alert = Assert.Single(new NotificationPolicy().Evaluate([device], settings, Now));

        Assert.Equal("mouse", tray.DeviceId);
        Assert.StartsWith("Work mouse:", tray.Tooltip);
        Assert.Equal("mouse", alert.DeviceId);
        Assert.Equal("Work mouse", alert.Name);
        Assert.Equal("receiver-mouse", device.Device.ReceiverId);
        Assert.Equal("mouse", device.Device.Name);
    }

    [Fact]
    public void AlertDeduplicationRequiresANewRecoverySampleBeforeAnotherLowCycle()
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings();

        Assert.Single(policy.Evaluate([Device("mouse", 20)], settings, Now));
        Assert.Empty(policy.Evaluate([Device("mouse", 19)], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([Device("mouse", 90, Now.AddSeconds(-1))], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([Device("mouse", 24, Now.AddSeconds(2))], settings, Now.AddSeconds(2)));
        Assert.Empty(policy.Evaluate([Device("mouse", 19, Now.AddSeconds(3))], settings, Now.AddSeconds(3)));
        Assert.Empty(policy.Evaluate([Device("mouse", 25, Now.AddSeconds(4))], settings, Now.AddSeconds(4)));
        Assert.Single(policy.Evaluate([Device("mouse", 20, Now.AddSeconds(5))], settings, Now.AddSeconds(5)));
    }

    [Fact]
    public void DisablingAndReenablingNotificationsDoesNotRepeatAnAlreadyAlertedCycle()
    {
        var policy = new NotificationPolicy();
        var enabled = new AppSettings();

        Assert.Single(policy.Evaluate([Device("mouse", 10)], enabled, Now));
        Assert.Empty(policy.Evaluate([Device("mouse", 90, Now.AddSeconds(1))],
            enabled with { LowBatteryNotifications = false }, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([Device("mouse", 9, Now.AddSeconds(2))], enabled, Now.AddSeconds(2)));
        Assert.Empty(policy.Evaluate([Device("mouse", 25, Now.AddSeconds(3))], enabled, Now.AddSeconds(3)));
        Assert.Single(policy.Evaluate([Device("mouse", 20, Now.AddSeconds(4))], enabled, Now.AddSeconds(4)));
    }

    private static DeviceSnapshot Device(string id, int percentage, DateTimeOffset? sampledAt = null,
        ChargingState charging = ChargingState.Discharging) =>
        new(new PeripheralIdentity(id, id, DeviceVendor.Logitech, $"receiver-{id}"),
            BatteryReading.Current(percentage, charging, sampledAt ?? Now), ConnectionState.Connected);
}
