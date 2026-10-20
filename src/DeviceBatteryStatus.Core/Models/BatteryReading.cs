namespace DeviceBatteryStatus.Core.Models;

public enum ReadingState { Current, Stale, Disconnected, Unsupported, Failed }
public enum ChargingState { Unknown, Discharging, Charging, Full }

/// <summary>Only Current and Stale readings can contain a measured percentage.</summary>
public sealed record BatteryReading
{
    public ReadingState State { get; }
    public int? Percentage { get; }
    public ChargingState Charging { get; }
    public DateTimeOffset? SampledAt { get; }
    public string? Detail { get; }

    private BatteryReading(ReadingState state, int? percentage, ChargingState charging,
        DateTimeOffset? sampledAt, string? detail)
    {
        if (percentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        if (state is ReadingState.Current or ReadingState.Stale)
        {
            if (percentage is null || sampledAt is null)
                throw new ArgumentException("Measured readings require a percentage and sample time.");
        }
        else if (percentage is not null) throw new ArgumentException("Unavailable readings cannot have a percentage.");
        State = state; Percentage = percentage; Charging = charging; SampledAt = sampledAt; Detail = detail;
    }

    public static BatteryReading Current(int percentage, ChargingState charging, DateTimeOffset sampledAt) =>
        new(ReadingState.Current, percentage, charging, sampledAt, null);

    public static BatteryReading Unavailable(ReadingState state, string detail,
        ChargingState charging = ChargingState.Unknown) =>
        state is ReadingState.Current or ReadingState.Stale
            ? throw new ArgumentException("Use a measured reading for Current or Stale.", nameof(state))
            : new(state, null, charging, null, detail);

    public BatteryReading AsStale(string detail) => Percentage is null || SampledAt is null
        ? throw new InvalidOperationException("There is no measured value to retain.")
        : new(ReadingState.Stale, Percentage, ChargingState.Unknown, SampledAt, detail);
}
