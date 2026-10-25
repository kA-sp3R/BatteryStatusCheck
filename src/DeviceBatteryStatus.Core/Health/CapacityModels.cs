namespace DeviceBatteryStatus.Core.Health;

public enum CapacityUnit { MilliampHours, WattHours }
public enum CapacityBasis { BatteryPack, SingleCell, LoadOutput }
public enum CapacitySourceKind { UserEntered, DocumentedSpecification, DeviceData }
public enum CapacityMeasurementMethod { DeviceReported, CurrentIntegration, PowerIntegration }
public enum CapacityFieldKind { None, FullChargeCapacity, RemainingCapacity }

/// <summary>Original capacity; units and pack/cell basis are never inferred or converted.</summary>
public sealed record DesignCapacity(decimal Value, CapacityUnit Unit, CapacityBasis Basis,
    string Source, CapacitySourceKind SourceKind = CapacitySourceKind.UserEntered,
    DateTimeOffset? RecordedAt = null);

/// <summary>
/// Optional, independently documented capacity telemetry. IdentityVerified means a known
/// individual battery, not merely a receiver, model, slot or replaceable peripheral.
/// ConnectionSession identifies uninterrupted device connection, not an opened HID handle.
/// Boundaries must come from documented telemetry; percentage and voltage are not boundaries.
/// Current/power are measured at the declared pack/cell/output boundary, never assumed draw.
/// Integration requires an explicit supported MaximumSampleGap contract. When SampleSequence
/// is supplied it must be consecutive throughout the discharge, including full/empty boundaries.
/// No current provider supplies this record without corresponding protocol evidence.
/// </summary>
public sealed record CapacityTelemetry(string? BatteryIdentity, string? ConnectionSession,
    DateTimeOffset SampledAt, bool IdentityVerified, string Source,
    CapacityBasis Basis = CapacityBasis.BatteryPack,
    decimal? DischargeCurrentMilliamps = null, decimal? DischargePowerWatts = null,
    decimal? ReportedCapacity = null, CapacityUnit? ReportedCapacityUnit = null,
    CapacityFieldKind ReportedCapacityKind = CapacityFieldKind.None,
    bool FullBoundary = false, bool EmptyBoundary = false,
    TimeSpan? MaximumSampleGap = null, DesignCapacity? DesignCapacity = null,
    long? SampleSequence = null);

/// <summary>Incomplete observed discharge is distinct from complete usable full-charge capacity.</summary>
public sealed record CapacityMeasurement(decimal? FullChargeCapacity, decimal? ObservedDischargeCapacity,
    CapacityUnit Unit, CapacityBasis Basis, CapacityMeasurementMethod Method,
    DateTimeOffset StartedAt, DateTimeOffset MeasuredAt, TimeSpan ObservedDuration,
    bool Complete, int SampleCount, string? Interruption, string Source, string BatteryIdentityKey,
    TimeSpan? MaximumSampleGap = null, bool SequenceContinuityVerified = false)
{
    // A partial discharge has an unknown fraction of a full cycle; never infer it from SOC.
    public decimal? CoverageFraction => Complete && Method != CapacityMeasurementMethod.DeviceReported ? 1m : null;
}

public sealed record CapacityHealthDetails(DesignCapacity? Design, CapacityMeasurement? LatestMeasurement,
    decimal? HealthPercentage, string Status, string Explanation,
    IReadOnlyList<CapacityMeasurement> History, bool IdentityVerified, string? StorageError = null);
