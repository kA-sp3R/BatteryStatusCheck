namespace DeviceBatteryStatus.Core.Models;

public enum ReadingState { Current, Stale, Disconnected, Unsupported, Failed }
public enum ChargingState { Unknown, Discharging, Charging, Full }
public enum BatteryCategory { Unknown, Critical, Low, Good, High, Full }

/// <summary>Retains the device's reported percentage or category without estimating a percentage.</summary>
public sealed record BatteryReading
{
    public ReadingState State { get; }
    public int? Percentage { get; }
    public BatteryCategory? Category { get; }
    public bool HasMeasurement => Percentage is not null || Category is not null;
    public ChargingState Charging { get; }
    public DateTimeOffset? SampledAt { get; }
    public string? Detail { get; }

    private BatteryReading(ReadingState state, int? percentage, BatteryCategory? category,
        ChargingState charging, DateTimeOffset? sampledAt, string? detail)
    {
        if (percentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        if (category is not null && !Enum.IsDefined(category.Value))
            throw new ArgumentOutOfRangeException(nameof(category));
        if (percentage is not null && category is not null)
            throw new ArgumentException("A reading cannot represent both a percentage and a category.");
        if (state is ReadingState.Current or ReadingState.Stale)
        {
            if ((percentage is null && category is null) || sampledAt is null)
                throw new ArgumentException("Measured readings require a reported value and sample time.");
        }
        else if (percentage is not null || category is not null)
            throw new ArgumentException("Unavailable readings cannot have a reported battery value.");
        State = state; Percentage = percentage; Category = category;
        Charging = charging; SampledAt = sampledAt; Detail = detail;
    }

    public static BatteryReading Current(int percentage, ChargingState charging, DateTimeOffset sampledAt,
        string? detail = null) =>
        new(ReadingState.Current, percentage, null, charging, sampledAt, detail);

    public static BatteryReading CurrentCategory(BatteryCategory category, ChargingState charging,
        DateTimeOffset sampledAt, string? detail = null) =>
        new(ReadingState.Current, null, category, charging, sampledAt, detail);

    public static BatteryReading Unavailable(ReadingState state, string detail,
        ChargingState charging = ChargingState.Unknown) =>
        state is ReadingState.Current or ReadingState.Stale
            ? throw new ArgumentException("Use a measured reading for Current or Stale.", nameof(state))
            : new(state, null, null, charging, null, detail);

    public BatteryReading AsStale(string detail) => !HasMeasurement || SampledAt is null
        ? throw new InvalidOperationException("There is no measured value to retain.")
        : new(ReadingState.Stale, Percentage, Category, ChargingState.Unknown, SampledAt, detail);
}
