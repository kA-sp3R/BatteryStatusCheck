using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class TrayIconSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 1, 2, TimeSpan.Zero);

    [Fact]
    public void DefaultSingleIconRetainsTheExistingLowestBatteryAndPreferenceRules()
    {
        DeviceSnapshot[] devices = [Device("other", 0), Device("preferred", 80)];
        var settings = new AppSettings { PreferredDeviceId = "preferred" };

        var item = Assert.Single(TrayIconSelection.Create(devices, settings, Now));

        Assert.Equal(TrayIconSelection.SummaryKey, item.Key);
        Assert.Equal(BatteryPresentation.CreateTray(devices, settings, Now), item.Presentation);
        Assert.Equal(80, item.Presentation.Percentage);
    }

    [Fact]
    public void OnlyExplicitlySelectedDevicesReceiveIconsAndEachKeepsItsActualPercentage()
    {
        var settings = Selected("mouse", "keyboard");
        var items = TrayIconSelection.Create([Device("other", 1), Device("keyboard", 100), Device("mouse", 0)],
            settings, Now);

        Assert.Equal(new[] { "mouse", "keyboard" }, items.Select(item => item.Presentation.DeviceId));
        Assert.Equal(new int?[] { 0, 100 }, items.Select(item => item.Presentation.Percentage));
        Assert.Equal(new[] { BatteryColor.Red, BatteryColor.Green }, items.Select(item => item.Presentation.Color));
        Assert.DoesNotContain(items, item => item.Presentation.DeviceId == "other");
    }

    [Fact]
    public void SinglePreferredSelectionDoesNotOverrideSeparateDeviceIcons()
    {
        var settings = Selected("mouse", "keyboard") with { PreferredDeviceId = "keyboard" };
        var items = TrayIconSelection.Create([Device("mouse", 12), Device("keyboard", 90)], settings, Now);

        Assert.Equal(new int?[] { 12, 90 }, items.Select(item => item.Presentation.Percentage));
    }

    [Fact]
    public void AnEmptySelectedListKeepsOneAccessibleSummaryIcon()
    {
        var item = Assert.Single(TrayIconSelection.Create([Device("mouse", 15)], Selected(), Now));

        Assert.Equal(TrayIconSelection.SummaryKey, item.Key);
        Assert.Equal(15, item.Presentation.Percentage);
    }

    [Fact]
    public void MissingSelectedDevicesStayNeutralInsteadOfBorrowingAnotherBattery()
    {
        var settings = Selected("absent", "present") with
        { CustomNames = new Dictionary<string, string> { ["absent"] = "My missing mouse" } };
        var items = TrayIconSelection.Create([Device("other", 3), Device("present", 70)], settings, Now);

        Assert.Equal(2, items.Count);
        var missing = items[0];
        Assert.Null(missing.Presentation.Percentage);
        Assert.Equal(BatteryColor.Neutral, missing.Presentation.Color);
        Assert.Equal("absent", missing.Presentation.DeviceId);
        Assert.Contains("My missing mouse", missing.Presentation.Tooltip);
        Assert.Contains("unavailable", missing.Presentation.Tooltip);
        Assert.Equal(70, items[1].Presentation.Percentage);
    }

    [Theory]
    [InlineData(BatteryCategory.Unknown)]
    [InlineData(BatteryCategory.Critical)]
    [InlineData(BatteryCategory.Low)]
    [InlineData(BatteryCategory.Good)]
    [InlineData(BatteryCategory.High)]
    [InlineData(BatteryCategory.Full)]
    public void NativeCategoriesKeepTheirExactReportedLabelWithoutAnEstimatedPercentage(BatteryCategory category)
    {
        var snapshot = Device("mouse", 45) with
        { Battery = BatteryReading.CurrentCategory(category, ChargingState.Unknown, Now) };

        var item = Assert.Single(TrayIconSelection.Create([snapshot], Selected("mouse"), Now));

        Assert.Null(item.Presentation.Percentage);
        Assert.Equal(BatteryColor.Neutral, item.Presentation.Color);
        Assert.Contains(category.ToString(), item.Presentation.Tooltip);
        Assert.Contains("category", item.Presentation.Tooltip);
        Assert.DoesNotContain("%", item.Presentation.Tooltip);
        Assert.Contains("2026-10-22 08:01:02Z", item.Presentation.Tooltip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitlyStaleAndExpiredPercentagesUseNeutralIconsWithTheirOriginalSampleTime(bool explicitStale)
    {
        var sampled = Now.AddMinutes(-10);
        var snapshot = Device("mouse", 0, sampled);
        if (explicitStale) snapshot = snapshot with { Battery = snapshot.Battery.AsStale("Read failed.") };

        var item = Assert.Single(TrayIconSelection.Create([snapshot], Selected("mouse"), Now));

        Assert.Null(item.Presentation.Percentage);
        Assert.Equal(BatteryColor.Neutral, item.Presentation.Color);
        Assert.Contains("0%", item.Presentation.Tooltip);
        Assert.Contains("Stale", item.Presentation.Tooltip);
        Assert.Contains("2026-10-22 07:51:02Z", item.Presentation.Tooltip);
        Assert.Equal(sampled, snapshot.Battery.SampledAt);
    }

    [Fact]
    public void StaleCategoriesStayCategoriesWithAVisibleStaleLabel()
    {
        var snapshot = Device("mouse", 50) with
        { Battery = BatteryReading.CurrentCategory(BatteryCategory.Low, ChargingState.Unknown, Now.AddHours(-1)).AsStale("Failure") };

        var item = Assert.Single(TrayIconSelection.Create([snapshot], Selected("mouse"), Now));

        Assert.Null(item.Presentation.Percentage);
        Assert.Contains("Low · category", item.Presentation.Tooltip);
        Assert.Contains("Stale", item.Presentation.Tooltip);
        Assert.DoesNotContain("%", item.Presentation.Tooltip);
    }

    [Theory]
    [InlineData(ReadingState.Disconnected)]
    [InlineData(ReadingState.Unsupported)]
    [InlineData(ReadingState.Failed)]
    public void UnavailableReadingsNeverAcquireAPercentageFromOtherSelectedDevices(ReadingState state)
    {
        var snapshot = Device("mouse", 40) with
        { Battery = BatteryReading.Unavailable(state, "Diagnostic detail.") };
        var items = TrayIconSelection.Create([snapshot, Device("keyboard", 10)], Selected("mouse", "keyboard"), Now);

        Assert.Null(items[0].Presentation.Percentage);
        Assert.Equal(BatteryColor.Neutral, items[0].Presentation.Color);
        Assert.Contains(state.ToString(), items[0].Presentation.Tooltip);
        Assert.Equal(10, items[1].Presentation.Percentage);
    }

    [Fact]
    public void ConfirmedDisconnectionSuppressesAnInconsistentCachedPercentage()
    {
        var snapshot = Device("mouse", 0) with { Connection = ConnectionState.Disconnected };

        var item = Assert.Single(TrayIconSelection.Create([snapshot], Selected("mouse"), Now));

        Assert.Null(item.Presentation.Percentage);
        Assert.Contains("Disconnected", item.Presentation.Tooltip);
        Assert.DoesNotContain("0%", item.Presentation.Tooltip);
    }

    [Fact]
    public void DuplicateSnapshotIdentitiesAreNeutralRatherThanArbitrarilyAttributingAMeasurement()
    {
        var item = Assert.Single(TrayIconSelection.Create([Device("mouse", 10), Device("mouse", 90)],
            Selected("mouse"), Now));

        Assert.Null(item.Presentation.Percentage);
        Assert.Equal(BatteryColor.Neutral, item.Presentation.Color);
        Assert.Contains("ambiguous", item.Presentation.Tooltip);
    }

    [Fact]
    public void ReorderingDiscoveryDoesNotChangeIconKeysOrSelectedOrdering()
    {
        var settings = Selected("keyboard", "mouse");
        var first = TrayIconSelection.Create([Device("mouse", 0), Device("keyboard", 75)], settings, Now);
        var second = TrayIconSelection.Create([Device("keyboard", 74), Device("mouse", 0)], settings, Now);

        Assert.Equal(first.Select(item => item.Key), second.Select(item => item.Key));
        Assert.Equal(new[] { TrayIconSelection.DeviceKey("keyboard"), TrayIconSelection.DeviceKey("mouse") },
            second.Select(item => item.Key));
    }

    [Fact]
    public void DuplicateAndExcessSelectionsCannotAllocateAnUnboundedSetOfIcons()
    {
        var ids = Enumerable.Range(0, 100).Select(index => $"device-{index}").ToArray();
        var settings = Selected(ids.Concat(ids).ToArray());

        var items = TrayIconSelection.Create([], settings, Now);

        Assert.Equal(TrayIconSelection.MaximumDeviceIcons, items.Count);
        Assert.Equal(items.Count, items.Select(item => item.Key).Distinct().Count());
        Assert.All(items, item => Assert.Null(item.Presentation.Percentage));
    }

    [Fact]
    public void TooltipNamesRemainReadableWithoutLosingTheSampleTimeOrSplittingASurrogatePair()
    {
        var settings = Selected("mouse") with
        { CustomNames = new Dictionary<string, string> { ["mouse"] = new string('A', 30) + "😀" + new string('B', 20) } };

        var item = Assert.Single(TrayIconSelection.Create([Device("mouse", 0)], settings, Now));

        Assert.Contains("2026-10-22 08:01:02Z", item.Presentation.Tooltip);
        Assert.Contains("…", item.Presentation.Tooltip);
        Assert.DoesNotContain("\uD83D…", item.Presentation.Tooltip);
        Assert.True(item.Presentation.Tooltip.Length <= 127);
    }

    private static AppSettings Selected(params string[] ids) => new()
    { TrayIconMode = TrayIconMode.SelectedDevices, TrayDeviceIds = ids };

    private static DeviceSnapshot Device(string id, int percentage, DateTimeOffset? sampled = null) => new(
        new(id, id, DeviceVendor.Razer, "receiver-" + id),
        BatteryReading.Current(percentage, ChargingState.Discharging, sampled ?? Now), ConnectionState.Connected);
}
