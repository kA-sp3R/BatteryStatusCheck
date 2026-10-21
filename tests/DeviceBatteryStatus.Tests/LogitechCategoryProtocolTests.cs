using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class LogitechCategoryProtocolTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x00, BatteryCategory.Unknown)]
    [InlineData(0x01, BatteryCategory.Critical)]
    [InlineData(0x02, BatteryCategory.Low)]
    [InlineData(0x04, BatteryCategory.Good)]
    [InlineData(0x08, BatteryCategory.Full)]
    public void UnifiedCategoriesRemainCategoriesEvenIfTheUnusedPercentageFieldContainsANumber(
        byte level, BatteryCategory expected)
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([73, level, 0, 0], false, SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(expected, reading.Category);
        Assert.Null(reading.Percentage);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Fact]
    public void AnUnusedUnifiedPercentageByteIsNotValidatedAsAPercentage()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([255, 2, 1, 0], false, SampleTime);

        Assert.Equal(BatteryCategory.Low, reading.Category);
        Assert.Equal(ChargingState.Charging, reading.Charging);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void UnifiedCategoryBitsAreRestrictedToTheDevicesAdvertisedCategories()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([0, 8, 0, 0], false, SampleTime, 0x07);

        Assert.Equal(BatteryCategory.Unknown, reading.Category);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void UnifiedCombinedBitsUseTheHighestSupportedReportedCategory()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([0, 0x0F, 0, 0], false, SampleTime, 0x07);

        Assert.Equal(BatteryCategory.Good, reading.Category);
        Assert.Null(reading.Percentage);
    }

    [Theory]
    [InlineData(1, BatteryCategory.Critical)]
    [InlineData(10, BatteryCategory.Critical)]
    [InlineData(11, BatteryCategory.Low)]
    [InlineData(25, BatteryCategory.Low)]
    [InlineData(30, BatteryCategory.Low)]
    [InlineData(31, BatteryCategory.Good)]
    [InlineData(50, BatteryCategory.Good)]
    [InlineData(80, BatteryCategory.Good)]
    [InlineData(81, BatteryCategory.Full)]
    [InlineData(100, BatteryCategory.Full)]
    public void BatteryStatusWithoutMileageUsesTheSpecificationCategoryRanges(
        byte reportedLevel, BatteryCategory expected)
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([reportedLevel, 0, 0], SampleTime, false);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(expected, reading.Category);
        Assert.Null(reading.Percentage);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Fact]
    public void MileageCapabilityAllowsAReportedPercentageWithoutApplyingCategoryRanges()
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([30, 0, 0], SampleTime, true);

        Assert.Equal(30, reading.Percentage);
        Assert.Null(reading.Category);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    public void BatteryStatusZeroIsUnknownWithOrWithoutMileage(bool supportsMileage, byte status)
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([0, 0, status], SampleTime, supportsMileage);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(BatteryCategory.Unknown, reading.Category);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void BatteryStatusChargeCompleteDoesNotManufactureOneHundredPercent()
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([0, 0, 3], SampleTime);

        Assert.Equal(BatteryCategory.Full, reading.Category);
        Assert.Equal(ChargingState.Full, reading.Charging);
        Assert.Null(reading.Percentage);
        Assert.Contains("charge complete", reading.Detail);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void FaultedOrInvalidBatteryStatusDoesNotSupplyACategory(byte status)
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([50, 0, status], SampleTime, false);

        Assert.Equal(ReadingState.Failed, reading.State);
        Assert.Null(reading.Category);
        Assert.Null(reading.Percentage);
    }

    [Theory]
    [InlineData(0, BatteryCategory.Unknown)]
    [InlineData(1, BatteryCategory.Critical)]
    [InlineData(2, BatteryCategory.Critical)]
    [InlineData(3, BatteryCategory.Low)]
    [InlineData(4, BatteryCategory.Low)]
    [InlineData(5, BatteryCategory.Good)]
    [InlineData(6, BatteryCategory.Good)]
    [InlineData(7, BatteryCategory.High)]
    public void LegacyStatusCodesRemainNativeCategories(byte level, BatteryCategory expected)
    {
        var reading = LogitechHidppProtocol.ParseBattery07([level, 0, 0], SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(expected, reading.Category);
        Assert.Null(reading.Percentage);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Theory]
    [InlineData(0x00, ChargingState.Discharging)]
    [InlineData(0x21, ChargingState.Charging)]
    [InlineData(0x24, ChargingState.Charging)]
    [InlineData(0x25, ChargingState.Charging)]
    [InlineData(0x22, ChargingState.Full)]
    [InlineData(0x26, ChargingState.Full)]
    [InlineData(0x20, ChargingState.Unknown)]
    public void LegacyChargingStatusDoesNotChangeTheReportedCategory(byte status, ChargingState expected)
    {
        var reading = LogitechHidppProtocol.ParseBattery07([5, status, 0], SampleTime);

        Assert.Equal(BatteryCategory.Good, reading.Category);
        Assert.Equal(expected, reading.Charging);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void LegacyChargingErrorDoesNotBecomeACategory()
    {
        var reading = LogitechHidppProtocol.ParseBattery07([5, 0x23, 0], SampleTime);

        Assert.Equal(ReadingState.Failed, reading.State);
        Assert.Null(reading.Category);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void InvalidOrTruncatedLegacyStatusCannotBecomeAnUnknownSample()
    {
        Assert.Equal(ReadingState.Failed, LogitechHidppProtocol.ParseBattery07([8, 0], SampleTime).State);
        Assert.Equal(ReadingState.Failed, LogitechHidppProtocol.ParseBattery07([0], SampleTime).State);
    }
}
