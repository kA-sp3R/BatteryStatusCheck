using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class CategoryPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BatteryCategory.Unknown, "Unknown")]
    [InlineData(BatteryCategory.Critical, "Critical")]
    [InlineData(BatteryCategory.Low, "Low")]
    [InlineData(BatteryCategory.Good, "Good")]
    [InlineData(BatteryCategory.High, "High")]
    [InlineData(BatteryCategory.Full, "Full")]
    public void ReportedCategoriesRemainCurrentAndDisplayTheirNativeLabelsWithoutPercentages(
        BatteryCategory category, string label)
    {
        var device = CategoryDevice("mouse", category);
        var settings = new AppSettings();

        Assert.True(BatteryPresentation.IsFreshCurrent(device, settings, Now));
        Assert.False(BatteryPresentation.IsFreshPercentage(device, settings, Now));
        Assert.Equal($"{label} · category", BatteryPresentation.GetBatteryValueText(device, settings, Now));
        var tray = BatteryPresentation.CreateTray([device], settings, Now);
        Assert.Equal("mouse", tray.DeviceId);
        Assert.Null(tray.Percentage);
        Assert.Equal(BatteryColor.Neutral, tray.Color);
        Assert.Contains($"{label} · category", tray.Tooltip);
        Assert.DoesNotContain("%", tray.Tooltip);
        Assert.DoesNotContain("unavailable", tray.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CategoryFreshnessUsesTheSameAgeAndLinkRulesAsPercentageReadings()
    {
        var settings = new AppSettings { RefreshIntervalSeconds = 10 };
        var boundary = CategoryDevice("mouse", BatteryCategory.Low, Now.AddSeconds(-25));
        var expired = CategoryDevice("mouse", BatteryCategory.Low, Now.AddSeconds(-25).AddTicks(-1));
        var future = CategoryDevice("mouse", BatteryCategory.Low, Now.AddTicks(1));
        var disconnected = CategoryDevice("mouse", BatteryCategory.Low) with
        {
            Connection = ConnectionState.Disconnected
        };

        Assert.True(BatteryPresentation.IsFreshCurrent(boundary, settings, Now));
        Assert.False(BatteryPresentation.IsFreshCurrent(expired, settings, Now));
        Assert.False(BatteryPresentation.IsFreshCurrent(future, settings, Now));
        Assert.False(BatteryPresentation.IsFreshCurrent(disconnected, settings, Now));
        Assert.Equal("Low · category · stale", BatteryPresentation.GetBatteryValueText(expired, settings, Now));
    }

    [Fact]
    public void AStaleCategoryKeepsItsReportedLabelAndSampleTime()
    {
        var sampledAt = Now.AddMinutes(-2);
        var device = CategoryDevice("mouse", BatteryCategory.Good, sampledAt);
        device = device with { Battery = device.Battery.AsStale("Read failed.") };
        var settings = new AppSettings { PreferredDeviceId = "mouse" };

        var tray = BatteryPresentation.CreateTray([device], settings, Now);

        Assert.Equal("Good · category · stale", BatteryPresentation.GetBatteryValueText(device, settings, Now));
        Assert.Equal(sampledAt, device.Battery.SampledAt);
        Assert.Null(tray.Percentage);
        Assert.Contains("Good · category · stale", tray.Tooltip);
    }

    [Fact]
    public void AReportedCriticalCategoryIsNotRankedAgainstAGenuinePercentage()
    {
        var measured = PercentageDevice("percentage", 72);

        var tray = BatteryPresentation.CreateTray(
            [CategoryDevice("category", BatteryCategory.Critical), measured], new AppSettings(), Now);

        Assert.Equal("percentage", tray.DeviceId);
        Assert.Equal(72, tray.Percentage);
        Assert.Equal(BatteryColor.Green, tray.Color);
    }

    [Fact]
    public void APreferredCategoryDeviceDoesNotFallBackToAnotherDevicesPercentage()
    {
        var settings = new AppSettings { PreferredDeviceId = "category" };

        var tray = BatteryPresentation.CreateTray(
            [PercentageDevice("percentage", 0), CategoryDevice("category", BatteryCategory.Low)], settings, Now);

        Assert.Equal("category", tray.DeviceId);
        Assert.Null(tray.Percentage);
        Assert.Equal(BatteryColor.Neutral, tray.Color);
        Assert.Contains("Low · category", tray.Tooltip);
    }

    [Fact]
    public void ARealZeroRemainsAMeasuredPercentageWhenCategoryDevicesArePresent()
    {
        var zero = PercentageDevice("zero", 0);
        var settings = new AppSettings();

        var tray = BatteryPresentation.CreateTray([CategoryDevice("category", BatteryCategory.Full), zero], settings, Now);

        Assert.True(BatteryPresentation.IsFreshPercentage(zero, settings, Now));
        Assert.Equal("0%", BatteryPresentation.GetBatteryValueText(zero, settings, Now));
        Assert.Equal(0, tray.Percentage);
        Assert.Equal("zero", tray.DeviceId);
    }

    [Fact]
    public void CategoryChargingAndRadioLinkRemainIndependent()
    {
        var device = CategoryDevice("mouse", BatteryCategory.Full) with { Connection = ConnectionState.Unknown };

        var tray = BatteryPresentation.CreateTray([device], new AppSettings(), Now);

        Assert.Contains("Full · category", tray.Tooltip);
        Assert.Contains("Charging state unknown", tray.Tooltip);
        Assert.Contains("link unknown", tray.Tooltip);
        Assert.DoesNotContain("Fully charged", tray.Tooltip);
    }

    [Theory]
    [InlineData(BatteryCategory.Unknown)]
    [InlineData(BatteryCategory.Critical)]
    [InlineData(BatteryCategory.Low)]
    [InlineData(BatteryCategory.Good)]
    [InlineData(BatteryCategory.High)]
    [InlineData(BatteryCategory.Full)]
    public void NoReportedCategoryTriggersAPercentageThresholdAlert(BatteryCategory category)
    {
        Assert.Empty(new NotificationPolicy().Evaluate([CategoryDevice("mouse", category)], new AppSettings(), Now));
    }

    [Theory]
    [InlineData(BatteryCategory.Unknown)]
    [InlineData(BatteryCategory.Critical)]
    [InlineData(BatteryCategory.Low)]
    [InlineData(BatteryCategory.Good)]
    [InlineData(BatteryCategory.High)]
    [InlineData(BatteryCategory.Full)]
    public void ACategoryCannotRearmAnAlreadyDeliveredPercentageAlert(BatteryCategory category)
    {
        var policy = new NotificationPolicy();
        var settings = new AppSettings();
        Assert.Single(policy.Evaluate([PercentageDevice("mouse", 10)], settings, Now));

        Assert.Empty(policy.Evaluate([CategoryDevice("mouse", category, Now.AddSeconds(1))], settings, Now.AddSeconds(1)));
        Assert.Empty(policy.Evaluate([PercentageDevice("mouse", 9, Now.AddSeconds(2))], settings, Now.AddSeconds(2)));
    }

    private static DeviceSnapshot CategoryDevice(string id, BatteryCategory category, DateTimeOffset? sampledAt = null) =>
        new(new PeripheralIdentity(id, id, DeviceVendor.Logitech, $"receiver-{id}"),
            BatteryReading.CurrentCategory(category, ChargingState.Unknown, sampledAt ?? Now), ConnectionState.Connected);

    private static DeviceSnapshot PercentageDevice(string id, int percentage, DateTimeOffset? sampledAt = null) =>
        new(new PeripheralIdentity(id, id, DeviceVendor.Logitech, $"receiver-{id}"),
            BatteryReading.Current(percentage, ChargingState.Discharging, sampledAt ?? Now), ConnectionState.Connected);
}
