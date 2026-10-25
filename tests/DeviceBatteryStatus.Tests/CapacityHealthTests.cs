using System.Text.Json;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Models;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class CapacityHealthTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly PeripheralIdentity Identity = new("synthetic-device", "Synthetic capacity device", DeviceVendor.Logitech, "synthetic-receiver");

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(80, 100, 80)]
    [InlineData(120, 100, 120)]
    public void HealthIsTheIndependentCapacityRatioWithoutClamping(int usable, int design, int expected)
    {
        Assert.Equal((decimal)expected, CapacityHealthService.CalculateHealth(usable, CapacityUnit.MilliampHours,
            CapacityBasis.BatteryPack, new(design, CapacityUnit.MilliampHours, CapacityBasis.BatteryPack, "Original specification")));
    }

    [Fact]
    public void RepresentablePositiveHealthIsNotLostToIntermediateRounding()
    {
        const decimal tinyCapacity = 0.0000000000000000000000000001m;
        var actual = CapacityHealthService.CalculateHealth(tinyCapacity, CapacityUnit.MilliampHours,
            CapacityBasis.BatteryPack, new(100m, CapacityUnit.MilliampHours, CapacityBasis.BatteryPack, "Original specification"));
        Assert.Equal(tinyCapacity, actual);
    }

    [Fact]
    public void UnrepresentablePositiveHealthDoesNotBecomeAGenuineZero()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(1_000_000_000m));
        var sample = fixture.Reported(0.0000000000000000000000000001m);
        fixture.Observe(sample);
        var details = fixture.Service.GetDetails(fixture.Snapshot(sample));
        Assert.NotNull(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        Assert.Contains("cannot be represented", details.Explanation);
    }

    [Fact]
    public void UnrepresentablePositiveDischargeIsRejectedInsteadOfBecomingZeroCapacity()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100m));
        fixture.Observe(fixture.Telemetry(current: 0.0000000000000000000000000001m, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 0m, empty: true);
        fixture.Observe(end);
        var details = fixture.Service.GetDetails(fixture.Snapshot(end));
        Assert.Null(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        Assert.Contains("cannot be represented", Assert.Single(details.History).Interruption!);
    }

    [Fact]
    public void CurrentSamplesIntegrateByTheTrapezoidRuleAcrossExplicitBoundaries()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(50));
        fixture.Observe(fixture.Telemetry(current: 1000, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 2000, empty: true);
        fixture.Observe(end);
        var details = fixture.Service.GetDetails(fixture.Snapshot(end));

        Assert.Equal(25m, details.LatestMeasurement!.FullChargeCapacity);
        Assert.Equal(50m, details.HealthPercentage);
        Assert.Equal(CapacityMeasurementMethod.CurrentIntegration, details.LatestMeasurement.Method);
        Assert.Equal(CapacityUnit.MilliampHours, details.LatestMeasurement.Unit);
        Assert.Equal(2, details.LatestMeasurement.SampleCount);
        Assert.Equal(TimeSpan.FromMinutes(1), details.LatestMeasurement.ObservedDuration);
        Assert.Equal(1m, details.LatestMeasurement.CoverageFraction);
        Assert.Equal(Epoch.AddMinutes(1), details.LatestMeasurement.MeasuredAt);
    }

    [Fact]
    public void MeasuredPowerIntegratesWattHoursWithoutAnyVoltageConversion()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(1, CapacityUnit.WattHours));
        fixture.Observe(fixture.Telemetry(power: 1, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(power: 3, empty: true);
        fixture.Observe(end);
        var details = fixture.Service.GetDetails(fixture.Snapshot(end));

        Assert.Equal(decimal.Round(1m / 30m, 12), decimal.Round(details.LatestMeasurement!.FullChargeCapacity!.Value, 12));
        Assert.Equal(CapacityMeasurementMethod.PowerIntegration, details.LatestMeasurement.Method);
        Assert.Equal(CapacityUnit.WattHours, details.LatestMeasurement.Unit);
    }

    [Fact]
    public void SupportedFullCapacityFieldIsValidAtPartialStateOfChargeAndDoesNotUseManufacturerHealth()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(85);
        fixture.Observe(sample, percentage: 23);
        var details = fixture.Service.GetDetails(fixture.Snapshot(sample, percentage: 23));

        Assert.Equal(85m, details.LatestMeasurement!.FullChargeCapacity);
        Assert.Equal(85m, details.HealthPercentage);
        Assert.Equal(CapacityMeasurementMethod.DeviceReported, details.LatestMeasurement.Method);
        Assert.Null(details.LatestMeasurement.CoverageFraction); // Device learning coverage is unknown.
        Assert.Equal(TimeSpan.Zero, details.LatestMeasurement.ObservedDuration);
    }

    [Fact]
    public void RemainingCapacityIsNeverTreatedAsAFullCapacityOrScaledByPercentage()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(25) with { ReportedCapacityKind = CapacityFieldKind.RemainingCapacity };
        fixture.Observe(sample, percentage: 25);
        var details = fixture.Service.GetDetails(fixture.Snapshot(sample));

        Assert.Null(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        Assert.Equal("Capacity measurement unavailable", details.Status);
        Assert.Contains("remaining capacity", details.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void PercentageAloneNeverStartsOrEndsACapacityMeasurement(int percentage)
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        fixture.Observe(null, percentage);
        fixture.Clock.Advance(TimeSpan.FromHours(24));
        fixture.Observe(null, percentage);
        var details = fixture.Service.GetDetails(fixture.Snapshot(percentage: percentage));

        Assert.Null(details.HealthPercentage);
        Assert.Empty(details.History);
        Assert.Contains("percentage, category, voltage and runtime cannot supply capacity", details.Explanation);
        Assert.Equal(100m, details.Design!.Value);
    }

    [Fact]
    public void FullBoundaryWithoutMeasuredCurrentOrPowerCannotCreateUsableCapacity()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(empty: true);
        fixture.Observe(end);
        Assert.Null(fixture.Service.GetDetails(fixture.Snapshot(end)).LatestMeasurement);
    }

    [Theory]
    [InlineData("charge")]
    [InlineData("unknown-charge")]
    [InlineData("gap")]
    [InlineData("missing")]
    [InlineData("disconnect")]
    [InlineData("unknown-link")]
    [InlineData("identity")]
    [InlineData("uncertain-identity")]
    [InlineData("reconnect")]
    [InlineData("negative-current")]
    [InlineData("basis")]
    [InlineData("source")]
    [InlineData("method")]
    [InlineData("new-full-boundary")]
    public void DisruptedDischargesAreRetainedAsIncompleteWithoutAHealthResult(string disruption)
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        fixture.Observe(fixture.Telemetry(current: 100, full: true));
        fixture.Clock.Advance(disruption == "gap" ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(1));
        var sample = fixture.Telemetry(current: 100, empty: true);
        var snapshot = fixture.Snapshot(sample);
        snapshot = disruption switch
        {
            "charge" => fixture.Snapshot(sample, charging: ChargingState.Charging),
            "unknown-charge" => fixture.Snapshot(sample, charging: ChargingState.Unknown),
            "missing" => fixture.Snapshot(),
            "disconnect" => snapshot with { Connection = ConnectionState.Disconnected },
            "unknown-link" => snapshot with { Connection = ConnectionState.Unknown },
            "identity" => snapshot with { CapacityTelemetry = sample with { BatteryIdentity = "other-battery" } },
            "uncertain-identity" => snapshot with { CapacityTelemetry = sample with { IdentityVerified = false } },
            "reconnect" => snapshot with { CapacityTelemetry = sample with { ConnectionSession = "reconnected" } },
            "negative-current" => snapshot with { CapacityTelemetry = sample with { DischargeCurrentMilliamps = -1 } },
            "basis" => snapshot with { CapacityTelemetry = sample with { Basis = CapacityBasis.SingleCell } },
            "source" => snapshot with { CapacityTelemetry = sample with { Source = "Changed sample semantics" } },
            "method" => snapshot with { CapacityTelemetry = sample with { DischargeCurrentMilliamps = null, DischargePowerWatts = 1 } },
            "new-full-boundary" => snapshot with { CapacityTelemetry = sample with { FullBoundary = true } },
            _ => snapshot
        };
        fixture.Service.Observe([snapshot], fixture.Clock.GetUtcNow());
        var details = fixture.Service.GetDetails(snapshot);

        Assert.Null(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        var history = Assert.Single(details.History);
        Assert.False(history.Complete);
        Assert.Null(history.FullChargeCapacity);
        Assert.Null(history.CoverageFraction);
        Assert.False(string.IsNullOrWhiteSpace(history.Interruption));
    }

    [Fact]
    public void ZeroDischargeSamplesRemainMeasuredZeros()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        fixture.Observe(fixture.Telemetry(current: 0, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 0, empty: true);
        fixture.Observe(end);
        var result = fixture.Service.GetDetails(fixture.Snapshot(end));
        Assert.Equal(0m, result.LatestMeasurement!.FullChargeCapacity);
        Assert.Equal(0m, result.HealthPercentage);
    }

    [Fact]
    public void ExplicitCompleteZeroCapacityFieldCanCalculateZeroHealth()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(0);
        fixture.Observe(sample);
        Assert.Equal(0m, fixture.Service.GetDetails(fixture.Snapshot(sample)).HealthPercentage);
    }

    [Fact]
    public void ClockJumpCannotBeIntegratedAsDischargedEnergy()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 100, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Clock.ShiftWall(TimeSpan.FromHours(-1));
        var end = fixture.Telemetry(current: 100, empty: true);
        fixture.Observe(end);
        var result = fixture.Service.GetDetails(fixture.Snapshot(end));
        Assert.Null(result.LatestMeasurement);
        Assert.Contains("time", Assert.Single(result.History).Interruption!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSamplesExpirePassivelyWithoutAnyNewDeviceRead()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 100, full: true));
        var before = fixture.Service.Revision;
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(fixture.Service.CheckContinuity(fixture.Clock.GetUtcNow()));
        Assert.False(fixture.Service.CheckContinuity(fixture.Clock.GetUtcNow()));
        var details = fixture.Service.GetDetails(fixture.Snapshot());
        Assert.Null(details.LatestMeasurement);
        Assert.Contains("Missing capacity samples", Assert.Single(details.History).Interruption!);
        Assert.True(fixture.Service.Revision > before);
    }

    [Fact]
    public void PartialDischargeHasUnknownCoverageAndIsNeverFullChargeCapacity()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 1000, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var partial = fixture.Telemetry(current: 2000);
        fixture.Observe(partial);
        var details = fixture.Service.GetDetails(fixture.Snapshot(partial));
        Assert.Null(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        var row = Assert.Single(details.History);
        Assert.Equal(25m, row.ObservedDischargeCapacity);
        Assert.Null(row.FullChargeCapacity);
        Assert.Null(row.CoverageFraction);
        Assert.False(row.Complete);
    }

    [Fact]
    public void UnitAndPhysicalBasisMustBothMatchBeforeHealthCanBeCalculated()
    {
        using var fixture = new Fixture();
        var sample = fixture.Reported(80);
        fixture.Observe(sample);
        foreach (var design in new[] { fixture.Design(100, CapacityUnit.WattHours), fixture.Design(100) with { Basis = CapacityBasis.LoadOutput } })
        {
            fixture.Service.SetDesignCapacity(fixture.Snapshot(), design);
            var details = fixture.Service.GetDetails(fixture.Snapshot(sample));
            Assert.NotNull(details.LatestMeasurement);
            Assert.Null(details.HealthPercentage);
            Assert.Equal("Capacity comparison unavailable", details.Status);
        }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("disconnected")]
    [InlineData("unknown-link")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("unverified")]
    public void SavedHealthIsNotAssignedThroughOldOrUncertainIdentityEvidence(string invalidation)
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(80);
        fixture.Observe(sample);
        var snapshot = fixture.Snapshot(sample);
        if (invalidation == "expired") fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        snapshot = invalidation switch
        {
            "stale" => snapshot with { Battery = snapshot.Battery.AsStale("Synthetic stale sample") },
            "disconnected" => snapshot with { Connection = ConnectionState.Disconnected },
            "unknown-link" => snapshot with { Connection = ConnectionState.Unknown },
            "future" => snapshot with { CapacityTelemetry = sample with { SampledAt = Epoch.AddHours(1) } },
            "unverified" => snapshot with { CapacityTelemetry = sample with { IdentityVerified = false } },
            _ => snapshot
        };
        var details = fixture.Service.GetDetails(snapshot);
        Assert.False(details.IdentityVerified);
        Assert.Null(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        Assert.True(Assert.Single(details.History).Complete);
    }

    [Fact]
    public void NumericallyUnrepresentableRatioDoesNotCrashTheDetailsView()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(0.0000000000000000000000000001m));
        var sample = fixture.Reported(1_000_000_000m);
        fixture.Observe(sample);
        var details = fixture.Service.GetDetails(fixture.Snapshot(sample));
        Assert.NotNull(details.LatestMeasurement);
        Assert.Null(details.HealthPercentage);
        Assert.Contains("cannot be represented", details.Explanation);
    }

    [Fact]
    public void DesignSourceUnitsAndCompletedHistorySurviveRestartWithNoActiveCycleResumption()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(80);
        fixture.Observe(sample);
        fixture.Service.Dispose();
        using var reloaded = new CapacityHealthService(fixture.Path, fixture.Clock);
        var details = reloaded.GetDetails(fixture.Snapshot(sample));
        Assert.Equal(80m, details.HealthPercentage);
        Assert.Equal("Original test battery specification", details.Design!.Source);
        Assert.Equal(CapacityUnit.MilliampHours, details.Design.Unit);
        Assert.Equal(CapacityBasis.BatteryPack, details.Design.Basis);
        Assert.Equal(Epoch, details.Design.RecordedAt);
        Assert.Single(details.History);
    }

    [Fact]
    public void AbruptRestartRejectsPendingDischargeAndRetainsItsInterruptionRecord()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 100, full: true));
        // Simulate process loss by opening the committed ledger before graceful Dispose.
        using var restarted = new CapacityHealthService(fixture.Path, fixture.Clock);
        var details = restarted.GetDetails(fixture.Snapshot());
        var row = Assert.Single(details.History);
        Assert.False(row.Complete);
        Assert.Null(row.FullChargeCapacity);
        Assert.Contains("restarted", row.Interruption!);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 100, empty: true);
        restarted.Observe([fixture.Snapshot(end)], fixture.Clock.GetUtcNow());
        Assert.Null(restarted.GetDetails(fixture.Snapshot(end)).LatestMeasurement);
    }

    [Fact]
    public void CorruptPrimaryLoadsValidBackupWithoutOverwritingItWithTheCorruptBytes()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(80);
        fixture.Observe(sample);
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(101)); // Backup now has valid completed history.
        File.WriteAllText(fixture.Path, "{not valid json}");
        using var restored = new CapacityHealthService(fixture.Path, fixture.Clock);
        Assert.Equal(80m, restored.GetDetails(fixture.Snapshot(sample)).HealthPercentage);
        Assert.NotNull(restored.LastError);
        restored.SetDesignCapacity(fixture.Snapshot(), fixture.Design(105));
        Assert.Equal("{not valid json}", File.ReadAllText(fixture.Path + ".corrupt"));
        using var backup = JsonDocument.Parse(File.ReadAllText(fixture.Path + ".bak"));
        Assert.Equal(1, backup.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":1,\"Devices\":[null]}")]
    [InlineData("{\"SchemaVersion\":2,\"Devices\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Devices\":null}")]
    public void MalformedHistoryDoesNotThrowOrInventMeasurements(string content)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, content);
        using var restored = new CapacityHealthService(fixture.Path, fixture.Clock);
        Assert.NotNull(restored.LastError);
        Assert.Empty(restored.GetDetails(fixture.Snapshot()).History);
    }

    [Fact]
    public void RepeatedFullCapacityFieldIsDeduplicatedAndHistoryRemainsBounded()
    {
        using var fixture = new Fixture();
        var initial = fixture.Reported(80);
        fixture.Observe(initial);
        var firstBytes = File.ReadAllBytes(fixture.Path);
        fixture.Observe(initial);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Observe(fixture.Reported(80));
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.Path));
        for (var index = 0; index < 30; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Observe(fixture.Reported(index));
        }
        Assert.Equal(20, fixture.Service.GetDetails(fixture.Snapshot(fixture.Reported(29))).History.Count);
        var stored = File.ReadAllText(fixture.Path);
        Assert.DoesNotContain("synthetic-device", stored);
        Assert.DoesNotContain("synthetic-battery", stored);
    }

    [Fact]
    public void StorageFailureDoesNotPreventIndependentInMemoryCapacityCalculation()
    {
        using var fixture = new Fixture();
        using var blocked = new CapacityHealthService(fixture.Directory, fixture.Clock);
        blocked.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(80);
        blocked.Observe([fixture.Snapshot(sample)], fixture.Clock.GetUtcNow());
        var details = blocked.GetDetails(fixture.Snapshot(sample));
        Assert.Equal(80m, details.HealthPercentage);
        Assert.NotNull(details.StorageError);
    }

    [Fact]
    public void DeviceDesignDataCanSupplyProvenanceButCannotOverwriteAUserChoice()
    {
        using var fixture = new Fixture();
        var sample = fixture.Reported(80) with { DesignCapacity = fixture.Design(100) with { SourceKind = CapacitySourceKind.DeviceData } };
        fixture.Observe(sample);
        Assert.Equal(CapacitySourceKind.DeviceData, fixture.Service.GetDetails(fixture.Snapshot(sample)).Design!.SourceKind);
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(120));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Observe(sample with { SampledAt = fixture.Clock.GetUtcNow() });
        Assert.Equal(120m, fixture.Service.GetDetails(fixture.Snapshot(sample)).Design!.Value);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("source")]
    [InlineData("unit")]
    [InlineData("basis")]
    [InlineData("source-kind")]
    public void InvalidDesignValuesCannotBeSavedAsAHealthDenominator(string invalid)
    {
        using var fixture = new Fixture();
        var design = fixture.Design(100);
        design = invalid switch
        {
            "zero" => design with { Value = 0 },
            "negative" => design with { Value = -1 },
            "source" => design with { Source = " " },
            "unit" => design with { Unit = (CapacityUnit)255 },
            "basis" => design with { Basis = (CapacityBasis)255 },
            _ => design with { SourceKind = (CapacitySourceKind)255 }
        };
        Assert.Throws<ArgumentException>(() => fixture.Service.SetDesignCapacity(fixture.Snapshot(), design));
        Assert.Null(fixture.Service.GetDetails(fixture.Snapshot()).Design);
    }

    [Fact]
    public void RepeatedUnsupportedObservationsDoNotCreateHistoryOrChangeTheRevision()
    {
        using var fixture = new Fixture();
        fixture.Observe(null);
        var revision = fixture.Service.Revision;
        for (var index = 0; index < 20; index++) fixture.Observe(null);
        Assert.Equal(revision, fixture.Service.Revision);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public void ExplicitInterruptionRequiresFreshObservedIdentityBeforeRebindingSavedHealth()
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(80);
        fixture.Observe(sample);
        fixture.Service.Interrupt("System suspend", fixture.Clock.GetUtcNow());
        Assert.Null(fixture.Service.GetDetails(fixture.Snapshot(sample)).HealthPercentage);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = fixture.Reported(80);
        fixture.Observe(next);
        Assert.Equal(80m, fixture.Service.GetDetails(fixture.Snapshot(next)).HealthPercentage);
        Assert.Single(fixture.Service.GetDetails(fixture.Snapshot(next)).History);
    }

    [Fact]
    public void IntegrationNeverAssumesThatAnUnspecifiedSamplingCadenceIsCompleteCoverage()
    {
        using var fixture = new Fixture();
        var first = fixture.Telemetry(current: 100, full: true) with { MaximumSampleGap = null };
        fixture.Observe(first);
        Assert.Empty(fixture.Service.GetDetails(fixture.Snapshot(first)).History);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 100, empty: true) with { MaximumSampleGap = null };
        fixture.Observe(end);
        var details = fixture.Service.GetDetails(fixture.Snapshot(end));
        Assert.Null(details.LatestMeasurement);
        Assert.Contains("no cadence is assumed", details.Explanation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(3L)]
    public void MissingOrOutOfOrderSequencesRejectOtherwiseTimelySamples(long? nextSequence)
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 100, full: true) with { SampleSequence = 1 });
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 100, empty: true) with { SampleSequence = nextSequence };
        fixture.Observe(end);
        var details = fixture.Service.GetDetails(fixture.Snapshot(end));
        Assert.Null(details.LatestMeasurement);
        Assert.False(Assert.Single(details.History).Complete);
        Assert.Contains("sequence", details.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsecutiveSequencesAndSupportedCadenceAreRecordedWithACompleteCycle()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 100, full: true) with { SampleSequence = 10 });
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 100, empty: true) with { SampleSequence = 11 };
        fixture.Observe(end);
        var result = fixture.Service.GetDetails(fixture.Snapshot(end)).LatestMeasurement!;
        Assert.True(result.Complete);
        Assert.True(result.SequenceContinuityVerified);
        Assert.Equal(TimeSpan.FromSeconds(90), result.MaximumSampleGap);
    }

    [Fact]
    public void ChangedSamplingContractRejectsTheDischarge()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Telemetry(current: 100, full: true));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var end = fixture.Telemetry(current: 100, empty: true) with { MaximumSampleGap = TimeSpan.FromSeconds(80) };
        fixture.Observe(end);
        var details = fixture.Service.GetDetails(fixture.Snapshot(end));
        Assert.Null(details.LatestMeasurement);
        Assert.Contains("contract changed", Assert.Single(details.History).Interruption!);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("unknown-identity")]
    [InlineData("missing-device")]
    public void OldSnapshotCannotRebindSavedHealthAfterAnInvalidObservation(string invalidation)
    {
        using var fixture = new Fixture();
        fixture.Service.SetDesignCapacity(fixture.Snapshot(), fixture.Design(100));
        var sample = fixture.Reported(80);
        var original = fixture.Snapshot(sample);
        fixture.Observe(sample);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        if (invalidation == "missing-device") fixture.Service.Observe([], fixture.Clock.GetUtcNow());
        else fixture.Service.Observe([invalidation == "disconnected" ? original with { Connection = ConnectionState.Disconnected }
            : original with { CapacityTelemetry = sample with { IdentityVerified = false } }], fixture.Clock.GetUtcNow());
        Assert.Null(fixture.Service.GetDetails(original).HealthPercentage);
        fixture.Service.Observe([original], fixture.Clock.GetUtcNow()); // Replayed cached frame is not new identity evidence.
        Assert.Null(fixture.Service.GetDetails(original).HealthPercentage);
        Assert.True(Assert.Single(fixture.Service.GetDetails(original).History).Complete);
        var fresh = fixture.Reported(80);
        fixture.Observe(fresh);
        Assert.Equal(80m, fixture.Service.GetDetails(fixture.Snapshot(fresh)).HealthPercentage);
    }

    [Fact]
    public void ChangedSupportedFieldSourceIsRecordedEvenWhenCapacityDoesNotChange()
    {
        using var fixture = new Fixture();
        fixture.Observe(fixture.Reported(80));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = fixture.Reported(80) with { Source = "Another independently documented full-capacity field" };
        fixture.Observe(next);
        var details = fixture.Service.GetDetails(fixture.Snapshot(next));
        Assert.Equal(2, details.History.Count);
        Assert.Equal(next.Source, details.LatestMeasurement!.Source);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatteryCapacityTests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "health.json");
        public TestClock Clock { get; } = new();
        public CapacityHealthService Service { get; }
        public Fixture() { System.IO.Directory.CreateDirectory(Directory); Service = new(Path, Clock); }
        public DesignCapacity Design(decimal value, CapacityUnit unit = CapacityUnit.MilliampHours) => new(value, unit, CapacityBasis.BatteryPack, "Original test battery specification");
        public CapacityTelemetry Telemetry(decimal? current = null, decimal? power = null, bool full = false, bool empty = false) =>
            new("synthetic-battery", "synthetic-connection", Clock.GetUtcNow(), true, "Synthetic independently supported telemetry",
                DischargeCurrentMilliamps: current, DischargePowerWatts: power, FullBoundary: full, EmptyBoundary: empty,
                MaximumSampleGap: TimeSpan.FromSeconds(90));
        public CapacityTelemetry Reported(decimal value) => Telemetry() with { ReportedCapacity = value,
            ReportedCapacityUnit = CapacityUnit.MilliampHours, ReportedCapacityKind = CapacityFieldKind.FullChargeCapacity };
        public DeviceSnapshot Snapshot(CapacityTelemetry? telemetry = null, int percentage = 42, ChargingState charging = ChargingState.Discharging) =>
            new(Identity, BatteryReading.Current(percentage, charging, Clock.GetUtcNow()), ConnectionState.Connected, telemetry);
        public void Observe(CapacityTelemetry? telemetry, int percentage = 42) => Service.Observe([Snapshot(telemetry, percentage)], Clock.GetUtcNow());
        public void Dispose() { Service.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset utc = Epoch;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan value) { utc += value; timestamp += value.Ticks; }
        public void ShiftWall(TimeSpan value) => utc += value;
    }
}
