namespace DeviceBatteryStatus.Core.Runtime;

public enum RuntimeEstimateState { Unavailable, Calculating, Available }

/// <summary>An observational, rounded discharge estimate, separate from battery capacity or health.</summary>
public sealed record RuntimeEstimate(RuntimeEstimateState State, string Text, string Detail,
    TimeSpan? Remaining = null, TimeSpan? LowerBound = null, TimeSpan? UpperBound = null,
    bool IsConfident = false, DateTimeOffset? BasedOnSampleAt = null,
    int SampleCount = 0, TimeSpan Coverage = default, double? DischargePercentagePerHour = null);
