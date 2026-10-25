using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.Core.Monitoring;

/// <summary>
/// Coordinates read-only receiver sessions with manual HID operations. Production policy caches
/// discovery and backs off unsuccessful receivers; freshness remains a separate presentation concern.
/// </summary>
public sealed class BatteryMonitor
{
    private readonly IHidDiscovery discovery;
    private readonly IHidTransportFactory transports;
    private readonly IReadOnlyList<IBatteryProvider> providers;
    private readonly HidOperationGate gate;
    private readonly BatteryMonitorOptions? options;
    private readonly TimeProvider time;
    private readonly Dictionary<string, DeviceSnapshot> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ReceiverId, string Provider), Retry> retries = [];
    private IReadOnlyList<DeviceSnapshot> published = Array.Empty<DeviceSnapshot>();
    private readonly object refreshLock = new();
    private TaskCompletionSource<IReadOnlyList<DeviceSnapshot>>? running;
    private bool pendingFollowUp;
    private long discoveryGeneration;
    private long appliedGeneration = -1;
    private CachedDiscovery? cached;
    private Retry? discoveryRetry;
    private long refreshRequests, refreshes, enumerations, opens, reads, skips, coalesced;

    public BatteryMonitor(IHidDiscovery discovery, IHidTransportFactory transports,
        IReadOnlyList<IBatteryProvider> providers, HidOperationGate? operationGate = null,
        BatteryMonitorOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(providers);
        options?.Validate();
        this.discovery = discovery;
        this.transports = transports;
        this.providers = providers;
        gate = operationGate ?? new();
        this.options = options;
        time = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<DeviceSnapshot> Devices => Volatile.Read(ref published);
    public string? DiscoveryError { get; private set; }
    public BatteryMonitorMetrics Metrics => new(Interlocked.Read(ref refreshRequests),
        Interlocked.Read(ref refreshes), Interlocked.Read(ref enumerations), Interlocked.Read(ref opens),
        Interlocked.Read(ref reads), Interlocked.Read(ref skips), Interlocked.Read(ref coalesced));

    /// <summary>
    /// Windows device-change notifications, manual refresh and resume invalidate metadata and retry
    /// deadlines. An invalidation during a refresh causes one coalesced follow-up, never overlapping I/O.
    /// </summary>
    public void InvalidateDiscovery()
    {
        lock (refreshLock)
        {
            Interlocked.Increment(ref discoveryGeneration);
            if (running is not null) pendingFollowUp = true;
        }
    }

    /// <summary>
    /// With a production policy, overlapping callers share work. The initiating token controls that
    /// operation (for app shutdown/suspend); a joining caller may cancel only its own wait.
    /// </summary>
    public Task<IReadOnlyList<DeviceSnapshot>> RefreshAsync(CancellationToken cancellationToken, bool force = false)
    {
        Interlocked.Increment(ref refreshRequests);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<DeviceSnapshot>>(cancellationToken);
        if (force) InvalidateDiscovery();
        if (options is null) return RefreshCoreAsync(cancellationToken);
        TaskCompletionSource<IReadOnlyList<DeviceSnapshot>> completion;
        bool start;
        lock (refreshLock)
        {
            if (running is not null)
            {
                Interlocked.Increment(ref coalesced);
                completion = running;
                start = false;
            }
            else
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                running = completion;
                pendingFollowUp = false;
                start = true;
            }
        }
        if (start) _ = RunCoalescedAsync(completion, cancellationToken);
        // The initiator owns the real operation lifetime: do not report completion
        // before canceled native work has unwound. Resume can then safely start a
        // fresh generation instead of joining the still-finishing canceled one.
        return start ? completion.Task : completion.Task.WaitAsync(cancellationToken);
    }

    private async Task RunCoalescedAsync(TaskCompletionSource<IReadOnlyList<DeviceSnapshot>> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                lock (refreshLock)
                {
                    if (pendingFollowUp)
                    {
                        pendingFollowUp = false;
                        continue;
                    }
                    // Clear ownership under the same lock used by invalidation/joining callers.
                    // A later request starts new work instead of joining a task already finishing.
                    running = null;
                }
                completion.TrySetResult(result);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseOwnership(completion);
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            ReleaseOwnership(completion);
            completion.TrySetException(exception);
        }
        finally { ReleaseOwnership(completion); }
    }

    private void ReleaseOwnership(TaskCompletionSource<IReadOnlyList<DeviceSnapshot>> completion)
    {
        lock (refreshLock)
            if (ReferenceEquals(running, completion)) running = null;
    }

    private async Task<IReadOnlyList<DeviceSnapshot>> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        using (await gate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref refreshes);
            cancellationToken.ThrowIfCancellationRequested();
            var generation = Volatile.Read(ref discoveryGeneration);
            if (generation != appliedGeneration)
            {
                cached = null;
                discoveryRetry = null;
                retries.Clear();
                appliedGeneration = generation;
            }
            var timestamp = time.GetTimestamp();
            if (options is not null && discoveryRetry is not null && !Due(discoveryRetry, timestamp))
                return Publish();
            var lifetime = DiscoveryError is null ? options?.DiscoveryCacheLifetime : options?.InitialRetryDelay;
            if (options is null || cached is null || lifetime is null || Elapsed(cached.Timestamp, timestamp, lifetime.Value))
            {
                try
                {
                    Interlocked.Increment(ref enumerations);
                    var endpoints = discovery.Enumerate();
                    cancellationToken.ThrowIfCancellationRequested();
                    DiscoveryError = discovery.Warnings.Count == 0 ? null
                        : $"Receiver discovery is incomplete: {string.Join("; ", discovery.Warnings)}";
                    var candidates = endpoints.SelectMany(endpoint => providers
                            .Where(provider => provider.CanHandle(endpoint))
                            .Select(provider => new Candidate(endpoint, provider)))
                        .GroupBy(x => (x.Endpoint.ReceiverId, x.Provider.Name))
                        .Select(group => group.OrderByDescending(x => x.Endpoint.InputReportLength)
                            .ThenByDescending(x => x.Endpoint.OutputReportLength).First()).ToArray();
                    // An endpoint/collection change clears only that receiver's obsolete retry state.
                    foreach (var key in retries.Keys.ToArray())
                        if (cached is null || !candidates.Any(next => next.Key == key &&
                            cached.Candidates.Any(old => old.Key == key && old.Endpoint == next.Endpoint)))
                            retries.Remove(key);
                    cached = new(endpoints.Select(x => x.ReceiverId).ToHashSet(StringComparer.Ordinal),
                        candidates.Select(x => x.Endpoint.ReceiverId).ToHashSet(StringComparer.Ordinal), candidates, timestamp);
                    discoveryRetry = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DiscoveryError = $"Receiver discovery failed: {ex.Message}";
                    cached = null;
                    if (options is not null) discoveryRetry = NextRetry(discoveryRetry, timestamp);
                    foreach (var old in devices.Values.ToArray())
                        devices[old.Device.Id] = WithFailure(old, DiscoveryError);
                    return Publish();
                }
            }

            var activeDiscovery = cached ?? throw new InvalidOperationException("Discovery did not produce a snapshot.");
            foreach (var old in devices.Values.ToArray())
                if (!activeDiscovery.PresentReceivers.Contains(old.Device.ReceiverId))
                    devices[old.Device.Id] = DiscoveryError is not null
                        ? WithFailure(old, "Receiver absence cannot be confirmed because discovery is incomplete.")
                        : old with
                        {
                            Battery = BatteryReading.Unavailable(ReadingState.Disconnected, "USB receiver removed."),
                            Connection = ConnectionState.Disconnected,
                            CapacityTelemetry = null
                        };
                else if (!activeDiscovery.QueryableReceivers.Contains(old.Device.ReceiverId))
                    devices[old.Device.Id] = WithFailure(old, "Receiver is present but its supported HID collection is unavailable.");

            // One session per receiver: vendors share a request/response channel across collections.
            foreach (var candidate in activeDiscovery.Candidates)
            {
                var (endpoint, provider) = candidate;
                cancellationToken.ThrowIfCancellationRequested();
                timestamp = time.GetTimestamp();
                if (options is not null && retries.TryGetValue(candidate.Key, out var retry) && !Due(retry, timestamp))
                {
                    Interlocked.Increment(ref skips);
                    continue; // No sample timestamp or telemetry is refreshed by skipping a query.
                }
                var prior = devices.Values.Where(x => x.Device.ReceiverId == endpoint.ReceiverId).ToArray();
                try
                {
                    Interlocked.Increment(ref opens);
                    await using var transport = await transports.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref reads);
                    var snapshots = await provider.ReadAsync(endpoint, transport, time.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (snapshots.Count > 0 && snapshots.All(x => !x.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal)))
                    {
                        foreach (var placeholder in prior.Where(x => x.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal)))
                            devices.Remove(placeholder.Device.Id);
                        prior = prior.Where(x => !x.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal)).ToArray();
                    }
                    foreach (var incoming in snapshots)
                    {
                        var usable = incoming.CapacityTelemetry is not null &&
                            (incoming.Connection != ConnectionState.Connected ||
                             incoming.Battery.State is not (ReadingState.Current or ReadingState.Unsupported))
                            ? incoming with { CapacityTelemetry = null } : incoming;
                        devices.TryGetValue(incoming.Device.Id, out var previous);
                        devices[incoming.Device.Id] = incoming.Battery.State == ReadingState.Failed && previous is not null
                            ? WithFailure(previous, incoming.Battery.Detail ?? "Read failed.") with
                            {
                                Device = incoming.Device,
                                Connection = incoming.Connection
                            }
                            : usable;
                    }
                    foreach (var missing in prior.Where(x => snapshots.All(y => y.Device.Id != x.Device.Id)))
                        devices[missing.Device.Id] = snapshots is ReceiverSnapshotBatch { AssociationComplete: true }
                            ? missing with
                            {
                                Battery = BatteryReading.Unavailable(ReadingState.Disconnected,
                                    "A completed receiver association scan no longer reports this peripheral."),
                                Connection = ConnectionState.Disconnected,
                                CapacityTelemetry = null
                            }
                            : missing.Battery.State == ReadingState.Disconnected && missing.Connection == ConnectionState.Disconnected
                                ? missing
                                : WithFailure(missing, "Receiver did not return this peripheral in this refresh.");
                    if (options is not null)
                    {
                        if (snapshots.Any(x => x.Battery.State == ReadingState.Current || HasFreshCapacityTelemetry(x, time.GetUtcNow())))
                            retries.Remove(candidate.Key);
                        else retries[candidate.Key] = NextRetry(retries.GetValueOrDefault(candidate.Key), timestamp);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (options is not null) retries[candidate.Key] = NextRetry(retries.GetValueOrDefault(candidate.Key), timestamp);
                    if (prior.Length > 0)
                    {
                        foreach (var old in prior) devices[old.Device.Id] = WithFailure(old, ex.Message);
                    }
                    else
                    {
                        var vendor = endpoint.VendorId switch
                        {
                            0x1532 => DeviceVendor.Razer,
                            0x413C => DeviceVendor.Alienware,
                            _ => DeviceVendor.Logitech
                        };
                        var id = new PeripheralIdentity($"{endpoint.ReceiverId}:unidentified",
                            $"{provider.Name} receiver (peripheral unidentified)", vendor, endpoint.ReceiverId);
                        devices[id.Id] = new(id, BatteryReading.Unavailable(ReadingState.Failed, ex.Message));
                    }
                }
            }
            return Publish();
        }
    }

    // This classifies query availability only. The capacity ledger independently
    // validates complete cycles, identity continuity, units and measurement basis.
    // A supported capacity field can work even when no SOC percentage is exposed.
    private static bool HasFreshCapacityTelemetry(DeviceSnapshot snapshot, DateTimeOffset now)
    {
        var sample = snapshot.CapacityTelemetry;
        if (snapshot.Connection != ConnectionState.Connected ||
            snapshot.Battery.State is not (ReadingState.Current or ReadingState.Unsupported) ||
            sample is not { IdentityVerified: true } ||
            string.IsNullOrWhiteSpace(sample.BatteryIdentity) || sample.BatteryIdentity.Length > 512 ||
            string.IsNullOrWhiteSpace(sample.ConnectionSession) || sample.ConnectionSession.Length > 512 ||
            string.IsNullOrWhiteSpace(sample.Source) || sample.Source.Length > 256 || sample.Source.Any(char.IsControl) ||
            !Enum.IsDefined(sample.Basis) || !Enum.IsDefined(sample.ReportedCapacityKind)) return false;
        var maximumGap = sample.MaximumSampleGap ?? TimeSpan.FromMinutes(2);
        if (maximumGap <= TimeSpan.Zero || maximumGap > TimeSpan.FromMinutes(2) ||
            sample.SampledAt > now + TimeSpan.FromSeconds(2) || now - sample.SampledAt > maximumGap ||
            sample.SampleSequence is < 0 ||
            sample.DischargeCurrentMilliamps is { } current && !ValidCapacityValue(current) ||
            sample.DischargePowerWatts is { } power && !ValidCapacityValue(power) ||
            sample.ReportedCapacity is { } reported && (!ValidCapacityValue(reported) ||
                sample.ReportedCapacityUnit is null || !Enum.IsDefined(sample.ReportedCapacityUnit.Value))) return false;
        if (sample.ReportedCapacityKind == CapacityFieldKind.FullChargeCapacity)
            return sample.ReportedCapacity is not null && sample.ReportedCapacityUnit is not null;
        return snapshot.Battery.Charging == ChargingState.Discharging && sample.MaximumSampleGap is not null &&
            (sample.DischargeCurrentMilliamps is not null || sample.DischargePowerWatts is not null);
    }

    private static bool ValidCapacityValue(decimal value) => value is >= 0m and <= 1_000_000_000m;

    private bool Due(Retry retry, long timestamp) => Elapsed(retry.Timestamp, timestamp, retry.Delay);
    private bool Elapsed(long start, long end, TimeSpan interval)
    {
        var elapsed = time.GetElapsedTime(start, end);
        return elapsed < TimeSpan.Zero || elapsed >= interval;
    }
    private Retry NextRetry(Retry? previous, long timestamp) => new(timestamp,
        previous is null ? options!.InitialRetryDelay
            : TimeSpan.FromTicks(Math.Min(options!.MaximumRetryDelay.Ticks,
                previous.Delay.Ticks > options.MaximumRetryDelay.Ticks / 2 ? options.MaximumRetryDelay.Ticks : previous.Delay.Ticks * 2)));

    private IReadOnlyList<DeviceSnapshot> Publish()
    {
        // Backoff ticks and unchanged unavailable states can reuse an immutable published list.
        var previous = Devices;
        var unchanged = previous.Count == devices.Count;
        for (var index = 0; unchanged && index < previous.Count; index++)
        {
            var old = previous[index];
            unchanged = devices.TryGetValue(old.Device.Id, out var current) && current == old;
        }
        if (unchanged) return previous;
        IReadOnlyList<DeviceSnapshot> next = Array.AsReadOnly(devices.Values.OrderBy(x => x.Device.Name).ToArray());
        Volatile.Write(ref published, next);
        return next;
    }

    private static DeviceSnapshot WithFailure(DeviceSnapshot previous, string detail)
    {
        if (previous.Connection == ConnectionState.Unknown && previous.CapacityTelemetry is null &&
            previous.Battery.Detail == detail && previous.Battery.State ==
                (previous.Battery.HasMeasurement ? ReadingState.Stale : ReadingState.Failed))
            return previous;
        return previous with
        {
            Battery = previous.Battery.HasMeasurement
                ? previous.Battery.AsStale(detail)
                : BatteryReading.Unavailable(ReadingState.Failed, detail),
            Connection = ConnectionState.Unknown,
            CapacityTelemetry = null
        };
    }

    private sealed record Candidate(HidEndpoint Endpoint, IBatteryProvider Provider)
    {
        public (string ReceiverId, string Provider) Key => (Endpoint.ReceiverId, Provider.Name);
    }
    private sealed record Retry(long Timestamp, TimeSpan Delay);
    private sealed record CachedDiscovery(HashSet<string> PresentReceivers, HashSet<string> QueryableReceivers,
        Candidate[] Candidates, long Timestamp);
}
