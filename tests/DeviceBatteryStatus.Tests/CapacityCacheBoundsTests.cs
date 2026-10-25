using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Models;
using Xunit;

namespace DeviceBatteryStatus.Tests;

/// <summary>Synthetic long-session identity churn, independent of any physical capacity support.</summary>
public sealed class CapacityCacheBoundsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "capacity-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private string StorePath => Path.Combine(directory, "capacity-health.json");

    [Fact]
    public void AThousandUnsupportedIdentitiesKeepOnlyBoundedAdvisoriesAndCreateNoCapacityHistory()
    {
        using var service = new CapacityHealthService(StorePath, clock);
        service.Observe(Enumerable.Range(0, 1000).Select(index => Snapshot("unsupported-" + index)), clock.GetUtcNow());
        Assert.Equal(64, Field<Dictionary<string, string>>(service, "reasons").Count);
        Assert.Equal(64, Field<HashSet<string>>(service, "interruptedBindings").Count);
        Assert.Empty(Field<Dictionary<string, CapacityTelemetry>>(service, "lastTelemetry"));
        Assert.Empty(Field<IDictionary>(service, "devices"));
        Assert.False(File.Exists(StorePath));
        var details = service.GetDetails(Snapshot("unsupported-0"));
        Assert.Equal("Capacity measurement unavailable", details.Status);
        Assert.Empty(details.History);
        Assert.Null(details.HealthPercentage);
    }

    [Fact]
    public void ValidOverflowIdentitiesDoNotEnterAcceptedTelemetryOrAlterThe64StoredHistories()
    {
        using var service = new CapacityHealthService(StorePath, clock);
        var accepted = Enumerable.Range(0, 64).Select(index => Snapshot("known-" + index, Reported("known-" + index))).ToArray();
        service.Observe(accepted, clock.GetUtcNow());
        var before = File.ReadAllBytes(StorePath);
        var overflow = Enumerable.Range(0, 1000).Select(index => Snapshot("overflow-" + index, Reported("overflow-" + index)));
        service.Observe(accepted.Concat(overflow), clock.GetUtcNow());
        Assert.Equal(64, Field<Dictionary<string, CapacityTelemetry>>(service, "lastTelemetry").Count);
        Assert.Equal(128, Field<Dictionary<string, string>>(service, "reasons").Count);
        Assert.InRange(Field<HashSet<string>>(service, "interruptedBindings").Count, 0, 128);
        Assert.Equal(64, Field<IDictionary>(service, "devices").Count);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        using var document = JsonDocument.Parse(before);
        Assert.Equal(64, document.RootElement.GetProperty("Devices").GetArrayLength());
        foreach (var snapshot in accepted)
        {
            var details = service.GetDetails(snapshot);
            Assert.Equal(80m, details.LatestMeasurement!.FullChargeCapacity);
            Assert.True(Assert.Single(details.History).Complete);
        }
        var rejected = Snapshot("overflow-0", Reported("overflow-0"));
        Assert.Empty(service.GetDetails(rejected).History);
        Assert.Null(service.GetDetails(rejected).LatestMeasurement);
    }

    [Fact]
    public void UnknownChurnNeverEvictsAKnownBatteryIdentityInterruptionGuard()
    {
        using var service = new CapacityHealthService(StorePath, clock);
        var known = Snapshot("known", Reported("known"));
        service.SetDesignCapacity(known, Design());
        service.Observe([known], clock.GetUtcNow());
        Assert.Equal(80m, service.GetDetails(known).HealthPercentage);
        service.Observe([known with { Connection = ConnectionState.Disconnected }], clock.GetUtcNow());
        // Replaying the old accepted frame must not restore identity after interruption.
        service.Observe(new[] { known }.Concat(Enumerable.Range(0, 1000).Select(index => Snapshot("unsupported-" + index))), clock.GetUtcNow());
        Assert.Contains(Key("known"), Field<HashSet<string>>(service, "interruptedBindings"));
        var interrupted = service.GetDetails(known);
        Assert.False(interrupted.IdentityVerified);
        Assert.Null(interrupted.HealthPercentage);
        Assert.True(Assert.Single(interrupted.History).Complete);
        clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = Snapshot("known", Reported("known"));
        service.Observe([fresh], clock.GetUtcNow());
        Assert.Equal(80m, service.GetDetails(fresh).HealthPercentage);
    }

    [Fact]
    public void UnknownChurnDoesNotEvictOrInterruptAnObservedKnownCapacityCycle()
    {
        using var service = new CapacityHealthService(StorePath, clock);
        var start = Snapshot("known", Telemetry("known", full: true, current: 1000));
        service.SetDesignCapacity(start, Design(50));
        service.Observe([start], clock.GetUtcNow());
        service.Observe(new[] { start }.Concat(Enumerable.Range(0, 1000).Select(index => Snapshot("unsupported-" + index))), clock.GetUtcNow());
        Assert.Equal(1, Field<IDictionary>(service, "active").Count);
        clock.Advance(TimeSpan.FromMinutes(1));
        var end = Snapshot("known", Telemetry("known", empty: true, current: 2000));
        service.Observe([end], clock.GetUtcNow());
        var details = service.GetDetails(end);
        Assert.Equal(25m, details.LatestMeasurement!.FullChargeCapacity);
        Assert.Equal(50m, details.HealthPercentage);
        Assert.True(Assert.Single(details.History).Complete);
    }

    [Fact]
    public void EvictingAnUnknownFailureCannotTurnAnEmptyBoundaryIntoACompleteMeasurement()
    {
        using var service = new CapacityHealthService(StorePath, clock);
        var uncertain = Snapshot("returning", Telemetry("returning", full: true) with { IdentityVerified = false });
        service.Observe([uncertain], clock.GetUtcNow());
        service.Observe(Enumerable.Range(0, 1000).Select(index => Snapshot("unsupported-" + index)), clock.GetUtcNow());
        Assert.DoesNotContain(Key("returning"), Field<HashSet<string>>(service, "interruptedBindings"));
        var emptyOnly = Snapshot("returning", Telemetry("returning", empty: true));
        service.SetDesignCapacity(emptyOnly, Design(50));
        service.Observe([emptyOnly], clock.GetUtcNow());
        Assert.Null(service.GetDetails(emptyOnly).LatestMeasurement);
        Assert.Null(service.GetDetails(emptyOnly).HealthPercentage);
        Assert.Empty(Field<IDictionary>(service, "active"));
        clock.Advance(TimeSpan.FromSeconds(30));
        var start = Snapshot("returning", Telemetry("returning", full: true, current: 1000));
        service.Observe([start], clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        var end = Snapshot("returning", Telemetry("returning", empty: true, current: 2000));
        service.Observe([end], clock.GetUtcNow());
        Assert.Equal(50m, service.GetDetails(end).HealthPercentage);
        Assert.True(Assert.Single(service.GetDetails(end).History).Complete);
    }

    private CapacityTelemetry Telemetry(string id, bool full = false, bool empty = false, decimal current = 1000) =>
        new("battery-" + id, "session-" + id, clock.GetUtcNow(), true, "Synthetic documented test telemetry",
            DischargeCurrentMilliamps: current, FullBoundary: full, EmptyBoundary: empty, MaximumSampleGap: TimeSpan.FromSeconds(90));
    private CapacityTelemetry Reported(string id) => Telemetry(id) with { ReportedCapacity = 80,
        ReportedCapacityKind = CapacityFieldKind.FullChargeCapacity, ReportedCapacityUnit = CapacityUnit.MilliampHours };
    private DeviceSnapshot Snapshot(string id, CapacityTelemetry? telemetry = null) => new(
        new(id, "Synthetic device", DeviceVendor.Logitech, "synthetic-receiver"),
        BatteryReading.Current(42, ChargingState.Discharging, clock.GetUtcNow()), ConnectionState.Connected, telemetry);
    private static DesignCapacity Design(decimal value = 100) => new(value, CapacityUnit.MilliampHours,
        CapacityBasis.BatteryPack, "Synthetic original specification");
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    // Inspect private collection counts instead of adding a production-only diagnostic API.
    private static T Field<T>(CapacityHealthService service, string name) =>
        (T)typeof(CapacityHealthService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan value) { utc += value; timestamp += value.Ticks; }
    }
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
