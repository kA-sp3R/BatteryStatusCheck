using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class BatteryPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, BatteryColor.Red)]
    [InlineData(10, BatteryColor.Red)]
    [InlineData(11, BatteryColor.Yellow)]
    [InlineData(30, BatteryColor.Yellow)]
    [InlineData(31, BatteryColor.Green)]
    [InlineData(100, BatteryColor.Green)]
    public void TrayUsesTheSpecifiedBatteryColorBoundariesWithoutReplacingGenuineZero(
        int percentage, BatteryColor expected)
    {
        var tray = BatteryPresentation.CreateTray([Device("mouse", percentage)], new AppSettings(), Now);

        Assert.Equal(percentage, tray.Percentage);
        Assert.Equal(expected, tray.Color);
        Assert.Equal("mouse", tray.DeviceId);
    }

    [Fact]
    public void LowestFreshMeasuredPercentageWinsIncludingAValidZero()
    {
        var tray = BatteryPresentation.CreateTray(
            [Device("keyboard", 52), Device("headset", 0), Device("mouse", 19)], new AppSettings(), Now);

        Assert.Equal("headset", tray.DeviceId);
        Assert.Equal(0, tray.Percentage);
        Assert.Equal(BatteryColor.Red, tray.Color);
        Assert.Contains("headset", tray.Tooltip);
    }

    [Fact]
    public void StaleExpiredFutureAndDisconnectedSamplesCannotBecomeTheLowestBattery()
    {
        var stale = Device("stale", 0);
        stale = stale with { Battery = stale.Battery.AsStale("Read failed.") };
        var expired = Device("expired", 0, Now.AddSeconds(-66));
        var future = Device("future", 0, Now.AddTicks(1));
        var disconnected = Device("disconnected", 0) with { Connection = ConnectionState.Disconnected };
        var failed = Device("failed", 0) with
        {
            Battery = BatteryReading.Unavailable(ReadingState.Failed, "No response.")
        };
        var unsupported = Device("unsupported", 0) with
        {
            Battery = BatteryReading.Unavailable(ReadingState.Unsupported, "No percentage protocol.")
        };

        var tray = BatteryPresentation.CreateTray(
            [stale, expired, future, disconnected, failed, unsupported, Device("valid", 72)], new AppSettings(), Now);

        Assert.Equal("valid", tray.DeviceId);
        Assert.Equal(72, tray.Percentage);
    }

    [Fact]
    public void NoMeasuredDevicesProducesANeutralIconWithoutAFabricatedPercentage()
    {
        var tray = BatteryPresentation.CreateTray([], new AppSettings(), Now);

        Assert.Null(tray.Percentage);
        Assert.Equal(BatteryColor.Neutral, tray.Color);
        Assert.False(string.IsNullOrWhiteSpace(tray.Tooltip));
    }

    [Fact]
    public void PreferredDeviceOverridesALowerPercentageFromAnotherDevice()
    {
        var settings = new AppSettings { PreferredDeviceId = "preferred" };

        var tray = BatteryPresentation.CreateTray(
            [Device("other", 4), Device("preferred", 90)], settings, Now);

        Assert.Equal("preferred", tray.DeviceId);
        Assert.Equal(90, tray.Percentage);
        Assert.Equal(BatteryColor.Green, tray.Color);
    }

    [Fact]
    public void AnUnavailablePreferredDeviceDoesNotSilentlyFallBackToAnotherDevice()
    {
        var settings = new AppSettings { PreferredDeviceId = "preferred" };
        var preferred = Device("preferred", 7);
        preferred = preferred with { Battery = preferred.Battery.AsStale("Receiver read failed.") };

        var tray = BatteryPresentation.CreateTray([Device("other", 4), preferred], settings, Now);

        Assert.Null(tray.Percentage);
        Assert.Equal(BatteryColor.Neutral, tray.Color);
        Assert.Contains("stale", tray.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APreferredDeviceMissingFromDiscoveryStaysNeutral()
    {
        var tray = BatteryPresentation.CreateTray([Device("other", 4)],
            new AppSettings { PreferredDeviceId = "not-present" }, Now);

        Assert.Null(tray.Percentage);
        Assert.Equal(BatteryColor.Neutral, tray.Color);
    }

    [Fact]
    public void FreshnessUsesTheConfiguredRefreshIntervalAndIncludesTheExactBoundary()
    {
        var settings = new AppSettings { RefreshIntervalSeconds = 10 };

        Assert.True(BatteryPresentation.IsFreshCurrent(Device("mouse", 42, Now.AddSeconds(-25)), settings, Now));
        Assert.False(BatteryPresentation.IsFreshCurrent(Device("mouse", 42, Now.AddSeconds(-25).AddTicks(-1)), settings, Now));
        Assert.False(BatteryPresentation.IsFreshCurrent(Device("mouse", 42, Now.AddTicks(1)), settings, Now));
    }

    [Fact]
    public void CurrentBatteryWithUnknownRadioLinkKeepsThatLimitationInItsTooltip()
    {
        var device = Device("mouse", 43) with { Connection = ConnectionState.Unknown };

        var tray = BatteryPresentation.CreateTray([device], new AppSettings(), Now);

        Assert.Equal(43, tray.Percentage);
        Assert.Contains("unknown", tray.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooltipUsesTheCustomNameAndKnownChargingInformation()
    {
        var settings = new AppSettings
        {
            CustomNames = new Dictionary<string, string> { ["headset"] = "Desk headset" }
        };
        var headset = Device("headset", 84) with
        {
            Battery = BatteryReading.Current(84, ChargingState.Charging, Now)
        };

        var tray = BatteryPresentation.CreateTray([headset], settings, Now);

        Assert.Equal("Desk headset", BatteryPresentation.GetDisplayName(headset.Device, settings));
        Assert.Contains("Desk headset", tray.Tooltip);
        Assert.Contains("charging", tray.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceSnapshot Device(string id, int percentage, DateTimeOffset? sampledAt = null) =>
        new(new PeripheralIdentity(id, id, DeviceVendor.Logitech, $"receiver-{id}"),
            BatteryReading.Current(percentage, ChargingState.Discharging, sampledAt ?? Now), ConnectionState.Connected);
}
