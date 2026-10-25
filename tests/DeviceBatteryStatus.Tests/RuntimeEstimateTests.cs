using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Runtime;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

/// <summary>All observations are synthetic percentages; these checks make no HID calls.</summary>
public sealed class RuntimeEstimateTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly AppSettings Settings = new() { RefreshIntervalSeconds = 600 };
    private static readonly PeripheralIdentity Identity = new("synthetic-mouse", "Synthetic mouse", DeviceVendor.Razer, "synthetic-receiver", "synthetic-model");

    [Fact]
    public void ANewPercentagePeriodIsCalculatingWithoutInventingATime()
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(80, 0);
        estimator.Observe([sample], Settings, Epoch);

        var estimate = estimator.Get(sample, Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Calculating, estimate.State);
        Assert.Equal("Calculating…", estimate.Text);
        Assert.Null(estimate.Remaining);
        Assert.False(estimate.IsConfident);
        Assert.Equal(Epoch, estimate.BasedOnSampleAt);
    }

    [Fact]
    public void AMeaningfulLinearTrendUsesReportedPercentagesAndElapsedSampleTime()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 21).Select(minute => (minute, 80 - minute)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(20));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.InRange(estimate.DischargePercentagePerHour!.Value, 59.99, 60.01);
        Assert.InRange(estimate.Remaining!.Value.TotalMinutes, 59.99, 60.01);
        Assert.True(estimate.LowerBound < estimate.Remaining);
        Assert.True(estimate.UpperBound > estimate.Remaining);
        Assert.False(estimate.IsConfident); // Twenty minutes is not enough for alert thresholds.
        Assert.Equal(Epoch.AddMinutes(20), estimate.BasedOnSampleAt);
    }

    [Fact]
    public void StableLongEnoughTrendBecomesConfidentAndDisplaysRoundedTime()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute / 2)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(30));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.True(estimate.IsConfident);
        Assert.InRange(estimate.DischargePercentagePerHour!.Value, 28, 32);
        Assert.Contains("About 2 hours", estimate.Text);
        Assert.DoesNotContain("65", estimate.Text); // No false minute-level precision.
    }

    [Fact]
    public void AFlatGaugeDoesNotImplyInfiniteOrKnownRuntime()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 121).Select(minute => (minute, 73)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(120));

        Assert.Equal(RuntimeEstimateState.Calculating, estimate.State);
        Assert.Null(estimate.Remaining);
        Assert.False(estimate.IsConfident);
    }

    [Fact]
    public void OneLargeCoarseStepDoesNotEstablishAConsumptionTrend()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 41).Select(minute => (minute, minute < 20 ? 80 : 60)));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(40)).State);
    }

    [Fact]
    public void CoarseStepsCanProduceARangeWithoutPretendingToHaveHighConfidence()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 51).Select(minute => (minute, 80 - minute / 20 * 10)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(50));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.False(estimate.IsConfident);
        Assert.NotNull(estimate.LowerBound);
        Assert.NotNull(estimate.UpperBound);
        Assert.True(estimate.UpperBound > estimate.LowerBound);
    }

    [Fact]
    public void AtLeastThreeReportedPointsMustBeLostEvenWithManySamples()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 41).Select(minute => (minute, 80 - minute / 20)));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(40)).State);
    }

    [Fact]
    public void IrregularSamplingUsesTimestampCoverageRatherThanAssumingFixedCadence()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, new[] { 0, 1, 2, 7, 11, 12, 19, 25, 30, 31, 39, 40 }
            .Select(minute => (minute, 90 - minute)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(40));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.InRange(estimate.DischargePercentagePerHour!.Value, 59.99, 60.01);
        Assert.InRange(estimate.Remaining!.Value.TotalMinutes, 49.99, 50.01);
        Assert.True(estimate.IsConfident);
    }

    [Fact]
    public void RepeatedPresentationOfOneSampleDoesNotInventMoreObservationsOrDischarge()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var before = estimator.Get(latest, Settings, Epoch.AddMinutes(30));

        for (var second = 0; second < 100; second++)
            estimator.Observe([latest], Settings, Epoch.AddMinutes(30).AddSeconds(second));
        var after = estimator.Get(latest, Settings, Epoch.AddMinutes(31));

        Assert.Equal(before, after);
        Assert.Equal(Epoch.AddMinutes(30), after.BasedOnSampleAt);
    }

    [Fact]
    public void SustainedHigherConsumptionReplacesOlderSlowerConsumption()
    {
        var estimator = new RuntimeEstimator();
        var initial = Feed(estimator, Enumerable.Range(0, 41).Select(minute => (minute, 100 - minute / 4)));
        var slow = estimator.Get(initial, Settings, Epoch.AddMinutes(40));
        var latest = Feed(estimator, Enumerable.Range(41, 30).Select(minute => (minute, 90 - (minute - 40))));

        var faster = estimator.Get(latest, Settings, Epoch.AddMinutes(70));

        Assert.Equal(RuntimeEstimateState.Available, faster.State);
        Assert.True(faster.DischargePercentagePerHour > slow.DischargePercentagePerHour * 2);
        Assert.InRange(faster.DischargePercentagePerHour!.Value, 55, 65);
        Assert.True(faster.Remaining < slow.Remaining);
    }

    [Fact]
    public void AnAbruptRateChangeWidensUncertaintyBeforeItBecomesStable()
    {
        var estimator = new RuntimeEstimator();
        Feed(estimator, Enumerable.Range(0, 41).Select(minute => (minute, 100 - minute / 4)));
        var latest = Feed(estimator, Enumerable.Range(41, 10).Select(minute => (minute, 90 - (minute - 40))));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(50));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.False(estimate.IsConfident);
    }

    [Fact]
    public void AValidZeroIsNotTreatedAsMissing()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 30 - minute)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(30));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.Equal(TimeSpan.Zero, estimate.Remaining);
        Assert.Equal("Under 15 minutes remaining", estimate.Text);
    }

    [Fact]
    public void ACoarseGaugeAtZeroDoesNotInventAPositiveLowerRuntimeBound()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 601).Select(minute => (minute, 20 - minute / 300 * 10)));

        var estimate = estimator.Get(latest, Settings, Epoch.AddMinutes(600));

        Assert.Equal(RuntimeEstimateState.Available, estimate.State);
        Assert.Equal(TimeSpan.Zero, estimate.Remaining);
        Assert.Equal(TimeSpan.Zero, estimate.LowerBound);
        Assert.StartsWith("Under ", estimate.Text);
        Assert.DoesNotContain("–", estimate.Text);
    }

    [Theory]
    [InlineData(BatteryCategory.Unknown)]
    [InlineData(BatteryCategory.Critical)]
    [InlineData(BatteryCategory.Low)]
    [InlineData(BatteryCategory.Good)]
    [InlineData(BatteryCategory.High)]
    [InlineData(BatteryCategory.Full)]
    public void NativeCategoriesCannotBecomePercentagesOrRuntime(BatteryCategory category)
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(20, 0) with { Battery = BatteryReading.CurrentCategory(category, ChargingState.Discharging, Epoch) };

        estimator.Observe([sample], Settings, Epoch);
        var estimate = estimator.Get(sample, Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimate.State);
        Assert.Equal("Unavailable", estimate.Text);
        Assert.Null(estimate.Remaining);
        Assert.Contains("categories", estimate.Detail);
    }

    [Theory]
    [InlineData(ChargingState.Charging)]
    [InlineData(ChargingState.Full)]
    [InlineData(ChargingState.Unknown)]
    public void ChargingOrUncertainChargingInterruptsThePeriod(ChargingState charging)
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var interruption = Snapshot(50, 31) with { Battery = BatteryReading.Current(50, charging, Epoch.AddMinutes(31)) };
        estimator.Observe([interruption], Settings, Epoch.AddMinutes(31));

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(interruption, Settings, Epoch.AddMinutes(31)).State);
        var resumed = Snapshot(49, 32);
        estimator.Observe([resumed], Settings, Epoch.AddMinutes(32));
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(resumed, Settings, Epoch.AddMinutes(32)).State);
    }

    [Theory]
    [InlineData(ConnectionState.Unknown)]
    [InlineData(ConnectionState.Disconnected)]
    public void ReceiverPresenceAndUnknownAssociationAreInsufficient(ConnectionState connection)
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(40, 0) with { Connection = connection };
        estimator.Observe([sample], Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(sample, Settings, Epoch).State);
    }

    [Theory]
    [InlineData(ReadingState.Failed)]
    [InlineData(ReadingState.Disconnected)]
    [InlineData(ReadingState.Unsupported)]
    public void UnavailableBatteryFieldsDoNotEnterATrend(ReadingState state)
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(40, 0) with { Battery = BatteryReading.Unavailable(state, "Synthetic unavailable reading.") };
        estimator.Observe([sample], Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(sample, Settings, Epoch).State);
    }

    [Fact]
    public void ExpirationIsCheckedIndependentlyOfNewObservationsAndResetsThePeriod()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var expiredAt = Epoch.AddMinutes(30).AddSeconds(1206);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(latest, Settings, expiredAt).State);
        var resumed = Snapshot(48, 51);
        estimator.Observe([resumed], Settings, Epoch.AddMinutes(51));
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(resumed, Settings, Epoch.AddMinutes(51)).State);
    }

    [Fact]
    public void ExplicitStaleReadingsAreRejectedEvenInsideTheFreshnessTimeBudget()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var stale = latest with { Battery = latest.Battery.AsStale("Synthetic failure.") };

        estimator.Observe([stale], Settings, Epoch.AddMinutes(30));

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(stale, Settings, Epoch.AddMinutes(30)).State);
    }

    [Fact]
    public void FutureSamplesAreInvalidAndCannotPolluteTheTrend()
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(50, 1);
        estimator.Observe([sample], Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(sample, Settings, Epoch).State);
        var valid = Snapshot(50, 0);
        estimator.Observe([valid], Settings, Epoch);
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(valid, Settings, Epoch).State);
    }

    [Fact]
    public void ASubstantialGapStartsANewPeriodInsteadOfChargingDisconnectedTimeToTheBattery()
    {
        var estimator = new RuntimeEstimator();
        Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var resumed = Snapshot(40, 51);
        estimator.Observe([resumed], Settings, Epoch.AddMinutes(51));

        var estimate = estimator.Get(resumed, Settings, Epoch.AddMinutes(51));
        Assert.Equal(RuntimeEstimateState.Calculating, estimate.State);
        Assert.Equal(1, estimate.SampleCount);
    }

    [Fact]
    public void NormalFreshnessBoundaryAcceptsContinuityButOneTickBeyondItResets()
    {
        var settings = Settings with { RefreshIntervalSeconds = 10 };
        var estimator = new RuntimeEstimator();
        var first = Snapshot(50, 0);
        estimator.Observe([first], settings, Epoch);
        var boundary = first with { Battery = BatteryReading.Current(49, ChargingState.Discharging, Epoch.AddSeconds(25)) };
        estimator.Observe([boundary], settings, Epoch.AddSeconds(25));
        Assert.Equal(2, estimator.Get(boundary, settings, Epoch.AddSeconds(25)).SampleCount);
        var afterGapAt = Epoch.AddSeconds(50).AddTicks(1);
        var afterGap = first with { Battery = BatteryReading.Current(48, ChargingState.Discharging, afterGapAt) };
        estimator.Observe([afterGap], settings, afterGapAt);
        Assert.Equal(1, estimator.Get(afterGap, settings, afterGapAt).SampleCount);
    }

    [Theory]
    [InlineData("receiver")]
    [InlineData("model")]
    [InlineData("vendor")]
    public void ChangedAssociationStartsANewPeriod(string component)
    {
        var estimator = new RuntimeEstimator();
        Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var identity = component switch
        {
            "receiver" => Identity with { ReceiverId = "another-receiver" },
            "model" => Identity with { ModelId = "another-model" },
            _ => Identity with { Vendor = DeviceVendor.Alienware }
        };
        var changed = Snapshot(49, 31) with { Device = identity };
        estimator.Observe([changed], Settings, Epoch.AddMinutes(31));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(changed, Settings, Epoch.AddMinutes(31)).State);
        Assert.Equal(1, estimator.Get(changed, Settings, Epoch.AddMinutes(31)).SampleCount);
    }

    [Fact]
    public void DisplayingAChangedIdentityAlsoInvalidatesTheOldPeriod()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var changed = latest with { Device = Identity with { ModelId = "changed" } };

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(changed, Settings, Epoch.AddMinutes(30)).State);
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(30)).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("synthetic:unidentified")]
    public void MissingOrExplicitlyUnidentifiedPeripheralCannotHaveARuntime(string id)
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(50, 0) with { Device = Identity with { Id = id } };
        estimator.Observe([sample], Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(sample, Settings, Epoch).State);
    }

    [Fact]
    public void MissingReceiverAssociationCannotHaveARuntime()
    {
        var estimator = new RuntimeEstimator();
        var sample = Snapshot(50, 0) with { Device = Identity with { ReceiverId = "" } };
        estimator.Observe([sample], Settings, Epoch);

        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(sample, Settings, Epoch).State);
    }

    [Fact]
    public void ReplayedTimestampWithADifferentValueInvalidatesHistory()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        estimator.Observe([Snapshot(49, 30)], Settings, Epoch.AddMinutes(30));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(30)).State);
    }

    [Fact]
    public void ConflictingSameTimeSamplesInvalidateThePeriod()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var conflict = Snapshot(49, 30);
        estimator.Observe([latest, conflict], Settings, Epoch.AddMinutes(30));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(30)).State);
    }

    [Fact]
    public void NonAdvancingChangedOrOutOfOrderSamplesCannotEnterTheTrend()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        estimator.Observe([Snapshot(49, 29)], Settings, Epoch.AddMinutes(30));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(30)).State);
    }

    [Fact]
    public void ASubstantialRiseStartsANewPeriodButSmallGaugeNoiseIsSmoothed()
    {
        var estimator = new RuntimeEstimator();
        Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        var small = Snapshot(51, 31);
        estimator.Observe([small], Settings, Epoch.AddMinutes(31));
        Assert.Equal(RuntimeEstimateState.Available, estimator.Get(small, Settings, Epoch.AddMinutes(31)).State);
        var rise = Snapshot(53, 32);
        estimator.Observe([rise], Settings, Epoch.AddMinutes(32));
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(rise, Settings, Epoch.AddMinutes(32)).State);
    }

    [Fact]
    public void ImplausiblyAbruptLargeDropResetsRatherThanExtrapolatingIt()
    {
        var estimator = new RuntimeEstimator();
        Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 100 - minute)));
        var discontinuity = Snapshot(20, 31);
        estimator.Observe([discontinuity], Settings, Epoch.AddMinutes(31));

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(discontinuity, Settings, Epoch.AddMinutes(31)).State);
    }

    [Fact]
    public void MissingDevicesAndExplicitInterruptionsDoNotLeaveReusableHistory()
    {
        var estimator = new RuntimeEstimator();
        var latest = Feed(estimator, Enumerable.Range(0, 31).Select(minute => (minute, 80 - minute)));
        estimator.Observe([], Settings, Epoch.AddMinutes(30));
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(30)).State);
        estimator.Observe([latest], Settings, Epoch.AddMinutes(30));
        estimator.Interrupt("Synthetic sleep.");
        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(latest, Settings, Epoch.AddMinutes(30)).State);
    }

    [Fact]
    public void SeparateDevicesHaveIndependentObservedRates()
    {
        var estimator = new RuntimeEstimator();
        DeviceSnapshot first = Snapshot(80, 0), second = first;
        for (var minute = 0; minute <= 30; minute++)
        {
            first = Snapshot(80 - minute, minute);
            second = Snapshot(80 - minute / 2, minute) with { Device = Identity with { Id = "second-device" } };
            estimator.Observe([first, second], Settings, Epoch.AddMinutes(minute));
        }

        var fast = estimator.Get(first, Settings, Epoch.AddMinutes(30));
        var slow = estimator.Get(second, Settings, Epoch.AddMinutes(30));
        Assert.True(fast.DischargePercentagePerHour > slow.DischargePercentagePerHour * 1.8);
        Assert.True(fast.Remaining < slow.Remaining);
    }

    [Fact]
    public void TheClockOverloadsUseTheInjectedClock()
    {
        var clock = new FixedClock(Epoch);
        var estimator = new RuntimeEstimator(clock);
        var first = Snapshot(50, 0);
        estimator.Observe([first], Settings);

        Assert.Equal(RuntimeEstimateState.Calculating, estimator.Get(first, Settings).State);
        clock.Now = Epoch.AddHours(1);
        Assert.Equal(RuntimeEstimateState.Unavailable, estimator.Get(first, Settings).State);
    }

    private static DeviceSnapshot Feed(RuntimeEstimator estimator, IEnumerable<(int Minute, int Percentage)> samples)
    {
        DeviceSnapshot? latest = null;
        foreach (var (minute, percentage) in samples)
        {
            latest = Snapshot(percentage, minute);
            estimator.Observe([latest], Settings, Epoch.AddMinutes(minute));
        }
        return latest!;
    }

    private static DeviceSnapshot Snapshot(int percentage, int minute) => new(Identity,
        BatteryReading.Current(percentage, ChargingState.Discharging, Epoch.AddMinutes(minute)), ConnectionState.Connected);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
