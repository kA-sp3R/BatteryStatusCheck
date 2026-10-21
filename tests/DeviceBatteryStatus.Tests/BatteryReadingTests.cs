using DeviceBatteryStatus.Core.Models;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class BatteryReadingTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void MeasuredPercentageIncludingGenuineZeroRemainsCurrent(int percentage)
    {
        var reading = BatteryReading.Current(percentage, ChargingState.Discharging, SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(percentage, reading.Percentage);
        Assert.Null(reading.Category);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void OutOfRangeMeasurementIsRejectedInsteadOfClamped(int percentage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BatteryReading.Current(percentage, ChargingState.Unknown, SampleTime));

    [Theory]
    [InlineData(ReadingState.Disconnected)]
    [InlineData(ReadingState.Unsupported)]
    [InlineData(ReadingState.Failed)]
    public void UnavailableReadingDefaultsToNoPercentageAndUnknownChargingState(ReadingState state)
    {
        var reading = BatteryReading.Unavailable(state, "No measured battery value.");

        Assert.Equal(state, reading.State);
        Assert.Null(reading.Percentage);
        Assert.Null(reading.Category);
        Assert.False(reading.HasMeasurement);
        Assert.Null(reading.SampledAt);
        Assert.Equal(ChargingState.Unknown, reading.Charging);
        Assert.Equal("No measured battery value.", reading.Detail);
    }

    [Fact]
    public void MissingPercentageCanPreserveAnIndependentlyReportedChargingState()
    {
        var reading = BatteryReading.Unavailable(ReadingState.Unsupported,
            "Percentage is unavailable; charging status was reported.", ChargingState.Charging);

        Assert.Null(reading.Percentage);
        Assert.Equal(ReadingState.Unsupported, reading.State);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Theory]
    [InlineData(ReadingState.Current)]
    [InlineData(ReadingState.Stale)]
    public void ACurrentOrStaleReadingCannotBeCreatedWithoutAMeasurement(ReadingState state) =>
        Assert.Throws<ArgumentException>(() => BatteryReading.Unavailable(state, "Missing measurement."));

    [Fact]
    public void StaleReadingRetainsGenuineZeroAndOriginalSampleTimeButNotChargingClaim()
    {
        var original = BatteryReading.Current(0, ChargingState.Charging, SampleTime);

        var stale = original.AsStale("Receiver no longer responds.");

        Assert.Equal(ReadingState.Stale, stale.State);
        Assert.Equal(0, stale.Percentage);
        Assert.Equal(SampleTime, stale.SampledAt);
        Assert.Equal(ChargingState.Unknown, stale.Charging);
        Assert.Equal("Receiver no longer responds.", stale.Detail);
        Assert.Equal(ReadingState.Current, original.State);
        Assert.Equal(ChargingState.Charging, original.Charging);
    }

    [Fact]
    public void RepeatedFailuresDoNotRefreshTheOriginalMeasurementTime()
    {
        var original = BatteryReading.Current(47, ChargingState.Discharging, SampleTime);

        var stale = original.AsStale("First failure.").AsStale("Second failure.");

        Assert.Equal(47, stale.Percentage);
        Assert.Equal(SampleTime, stale.SampledAt);
        Assert.Equal("Second failure.", stale.Detail);
    }

    [Theory]
    [InlineData(ReadingState.Disconnected)]
    [InlineData(ReadingState.Unsupported)]
    [InlineData(ReadingState.Failed)]
    public void AReadingWithoutAPreviousMeasurementCannotBecomeStale(ReadingState state) =>
        Assert.Throws<InvalidOperationException>(() => BatteryReading.Unavailable(state, "Unavailable.").AsStale("Retry failed."));

    [Theory]
    [InlineData(BatteryCategory.Unknown)]
    [InlineData(BatteryCategory.Critical)]
    [InlineData(BatteryCategory.Low)]
    [InlineData(BatteryCategory.Good)]
    [InlineData(BatteryCategory.High)]
    [InlineData(BatteryCategory.Full)]
    public void ReportedCategoriesRemainCurrentWithoutAnEstimatedPercentage(BatteryCategory category)
    {
        var reading = BatteryReading.CurrentCategory(category, ChargingState.Discharging, SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(category, reading.Category);
        Assert.True(reading.HasMeasurement);
        Assert.Null(reading.Percentage);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Fact]
    public void StaleCategoryRetainsOriginalValueAndTimeWithoutChargingClaim()
    {
        var original = BatteryReading.CurrentCategory(BatteryCategory.Low, ChargingState.Charging,
            SampleTime, "Native category.");

        var stale = original.AsStale("Read failed.");

        Assert.Equal(BatteryCategory.Low, stale.Category);
        Assert.Null(stale.Percentage);
        Assert.Equal(SampleTime, stale.SampledAt);
        Assert.Equal(ChargingState.Unknown, stale.Charging);
        Assert.Equal(ReadingState.Current, original.State);
        Assert.Equal(ChargingState.Charging, original.Charging);
    }

    [Fact]
    public void UndefinedCategoryCannotBecomeAMeasurement()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BatteryReading.CurrentCategory(
            (BatteryCategory)99, ChargingState.Unknown, SampleTime));
    }
}
