namespace DeviceBatteryStatus.Core.Monitoring;

/// <summary>Opt-in production policy. Omitting this policy preserves uncached legacy refresh behavior.</summary>
public sealed record BatteryMonitorOptions
{
    public TimeSpan DiscoveryCacheLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (DiscoveryCacheLifetime <= TimeSpan.Zero || DiscoveryCacheLifetime > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(DiscoveryCacheLifetime));
        if (InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay ||
            MaximumRetryDelay > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(MaximumRetryDelay));
    }
}

/// <summary>Process-local operation counters, without device identifiers or physical performance claims.</summary>
public sealed record BatteryMonitorMetrics(long RefreshRequests, long Refreshes,
    long DiscoveryEnumerations, long TransportOpens, long ProviderReads,
    long BackoffSkips, long CoalescedRequests);
