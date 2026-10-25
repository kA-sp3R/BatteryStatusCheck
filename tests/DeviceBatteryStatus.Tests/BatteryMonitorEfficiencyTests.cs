using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.Tests;

public sealed class BatteryMonitorEfficiencyTests
{
    private static readonly HidEndpoint Endpoint = new("synthetic", 0x1532, 1, 1, 1, 91, 91, 91, "receiver");
    private static readonly BatteryMonitorOptions Policy = new();
    private static DeviceSnapshot Current(HidEndpoint endpoint, DateTimeOffset now) => new(
        new("peripheral-" + endpoint.ReceiverId, "Synthetic mouse", DeviceVendor.Razer, endpoint.ReceiverId),
        BatteryReading.Current(0, ChargingState.Discharging, now), ConnectionState.Connected);

    [Fact]
    public async Task CachedDiscoveryDoesNotSuppressHealthyQueriesOrAdvanceTheirActualSampleTime()
    {
        var clock = new Clock();
        var discovery = new Discovery();
        var provider = new Provider();
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy, timeProvider: clock);
        var first = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.Equal(1, discovery.Calls);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(clock.GetUtcNow(), second.Battery.SampledAt);
        Assert.Equal(first.Battery.SampledAt!.Value.AddSeconds(30), second.Battery.SampledAt);
        Assert.Equal(0, second.Battery.Percentage);
        Assert.Equal(ReadingState.Current, second.Battery.State);
    }

    [Fact]
    public async Task DiscoveryReconcilesAfterItsBoundedLifetimeEvenWithoutDeviceEvents()
    {
        var clock = new Clock();
        var discovery = new Discovery();
        var monitor = new BatteryMonitor(discovery, new Factory(), [new Provider()], options: Policy, timeProvider: clock);
        await monitor.RefreshAsync(CancellationToken.None);
        discovery.Endpoints = [];
        clock.Advance(TimeSpan.FromMinutes(5));
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualRefreshOrDeviceInvalidationRestoresAReceiverBeforeItsRetryDeadline(bool manual)
    {
        var clock = new Clock();
        var discovery = new Discovery();
        var provider = new Provider { Failure = new IOException("Synthetic unavailable receiver") };
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy, timeProvider: clock);
        await monitor.RefreshAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        provider.Failure = null;
        if (!manual) monitor.InvalidateDiscovery();
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None, force: manual));
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(clock.GetUtcNow(), result.Battery.SampledAt);
    }

    [Fact]
    public async Task BackoffIsExponentialBoundedAndPreservesOriginalStaleZeroAndSampleTime()
    {
        var clock = new Clock();
        var provider = new Provider { Read = (endpoint, now, _) =>
            Task.FromResult<IReadOnlyList<DeviceSnapshot>>([Current(endpoint, now) with
            {
                CapacityTelemetry = new CapacityTelemetry("synthetic-battery", "synthetic-session", now,
                    true, "Synthetic capacity field", ReportedCapacity: 800,
                    ReportedCapacityUnit: CapacityUnit.MilliampHours,
                    ReportedCapacityKind: CapacityFieldKind.FullChargeCapacity)
            }]) };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy, timeProvider: clock);
        var first = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.NotNull(first.CapacityTelemetry);
        provider.Failure = new IOException("Synthetic timeout");
        clock.Advance(TimeSpan.FromSeconds(30));
        var failed = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        for (var index = 0; index < 5; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            var skipped = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
            Assert.Equal(ReadingState.Stale, skipped.Battery.State);
            Assert.Equal(first.Battery.SampledAt, skipped.Battery.SampledAt);
            Assert.Equal(0, skipped.Battery.Percentage);
            Assert.Null(skipped.CapacityTelemetry);
        }
        Assert.Equal(2, provider.Calls);
        Assert.Equal(5, monitor.Metrics.BackoffSkips);
        clock.Advance(TimeSpan.FromSeconds(5)); //30 seconds after failure
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(59));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(4, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(5, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(6, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(7, provider.Calls);
        Assert.Equal(failed.Battery.SampledAt, Assert.Single(monitor.Devices).Battery.SampledAt);
    }

    [Fact]
    public async Task AnUnresponsiveReceiverDoesNotDelayAHealthyReceiversNormalRefresh()
    {
        var clock = new Clock();
        var discovery = new Discovery { Endpoints = [Endpoint, Endpoint with { ReceiverId = "failed", DevicePath = "failed" }] };
        var calls = new Dictionary<string, int>();
        var provider = new Provider
        {
            Read = (endpoint, now, _) =>
            {
                calls[endpoint.ReceiverId] = calls.GetValueOrDefault(endpoint.ReceiverId) + 1;
                return endpoint.ReceiverId == "failed" ? throw new IOException("Synthetic timeout")
                    : Task.FromResult<IReadOnlyList<DeviceSnapshot>>([Current(endpoint, now)]);
            }
        };
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy, timeProvider: clock);
        for (var index = 0; index < 40; index++)
        {
            await monitor.RefreshAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(40, calls["receiver"]);
        Assert.Equal(21, calls["failed"]);
        Assert.Equal(4, discovery.Calls);
        Assert.Equal(61, monitor.Metrics.ProviderReads);
        Assert.Equal(19, monitor.Metrics.BackoffSkips);
    }

    [Theory]
    [InlineData(ReadingState.Unsupported)]
    [InlineData(ReadingState.Disconnected)]
    [InlineData(ReadingState.Failed)]
    public async Task UnavailableRepliesBackOffWithoutInventingCurrentMeasurements(ReadingState state)
    {
        var clock = new Clock();
        var provider = new Provider { Read = (endpoint, _, _) => Task.FromResult<IReadOnlyList<DeviceSnapshot>>(
            [Current(endpoint, clock.GetUtcNow()) with { Battery = BatteryReading.Unavailable(state, "Synthetic unavailable") }]) };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy, timeProvider: clock);
        var first = await monitor.RefreshAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(15)); // Before the initial 30-second retry deadline.
        var second = await monitor.RefreshAsync(CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(state, Assert.Single(second).Battery.State);
        Assert.Null(Assert.Single(second).Battery.SampledAt);
        Assert.Null(Assert.Single(second).Battery.Percentage);
    }

    [Fact]
    public async Task DiscoveryFailureBacksOffAndCannotProveAReceiverWasRemoved()
    {
        var clock = new Clock();
        var discovery = new Discovery();
        var provider = new Provider();
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy, timeProvider: clock);
        var initial = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        discovery.Failure = new IOException("Synthetic discovery unavailable");
        monitor.InvalidateDiscovery();
        var failed = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(ReadingState.Stale, failed.Battery.State);
        Assert.Equal(initial.Battery.SampledAt, failed.Battery.SampledAt);
        Assert.Equal(ConnectionState.Unknown, failed.Connection);
        discovery.Failure = null;
        var recovered = Assert.Single(await monitor.RefreshAsync(CancellationToken.None, force: true));
        Assert.Equal(3, discovery.Calls);
        Assert.Equal(ReadingState.Current, recovered.Battery.State);
        Assert.Null(monitor.DiscoveryError);
    }

    [Fact]
    public async Task IncompleteDiscoveryUsesAShortReconciliationAndCannotClaimRemoval()
    {
        var clock = new Clock();
        var discovery = new Discovery();
        var monitor = new BatteryMonitor(discovery, new Factory(), [new Provider()], options: Policy, timeProvider: clock);
        await monitor.RefreshAsync(CancellationToken.None);
        discovery.Endpoints = [];
        discovery.Warnings = ["Synthetic collection unavailable"];
        monitor.InvalidateDiscovery();
        var uncertain = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.Equal(ReadingState.Stale, uncertain.Battery.State);
        Assert.Equal(ConnectionState.Unknown, uncertain.Connection);
        discovery.Warnings = [];
        clock.Advance(TimeSpan.FromMinutes(1));
        var confirmed = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.Equal(ReadingState.Disconnected, confirmed.Battery.State);
        Assert.Null(monitor.DiscoveryError);
    }

    [Fact]
    public async Task ConcurrentBackgroundRequestsShareOneNativeOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { Read = async (endpoint, now, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return [Current(endpoint, now)];
        } };
        var discovery = new Discovery();
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy);
        var first = monitor.RefreshAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = Enumerable.Range(0, 12).Select(_ => monitor.RefreshAsync(CancellationToken.None)).ToArray();
        release.TrySetResult();
        await Task.WhenAll(joined.Append(first)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, discovery.Calls);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(12, monitor.Metrics.CoalescedRequests);
    }

    [Fact]
    public async Task MultipleForcedRequestsDuringARefreshScheduleExactlyOneImmediateFollowUp()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var provider = new Provider { Read = async (endpoint, now, _) =>
        {
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            try { entered.TrySetResult(); await release.Task; return [Current(endpoint, now)]; }
            finally { Interlocked.Decrement(ref active); }
        } };
        var discovery = new Discovery();
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy);
        var first = monitor.RefreshAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forced = Enumerable.Range(0, 10).Select(_ => monitor.RefreshAsync(CancellationToken.None, force: true)).ToArray();
        release.TrySetResult();
        await Task.WhenAll(forced.Append(first)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task InvalidationDuringEnumerationIsNotOverwrittenByThatEnumeration()
    {
        var discovery = new Discovery();
        var provider = new Provider();
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy);
        discovery.OnEnumerate = () => { if (discovery.Calls == 1) monitor.InvalidateDiscovery(); };
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CancelingAJoinedWaitDoesNotCancelTheInitiatingOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { Read = async (endpoint, now, token) =>
        {
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return [Current(endpoint, now)];
        } };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy);
        var first = monitor.RefreshAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var second = monitor.RefreshAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(first.IsCompleted);
        release.TrySetResult();
        Assert.Equal(ReadingState.Current, Assert.Single(await first.WaitAsync(TimeSpan.FromSeconds(10))).Battery.State);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task SuspendTokenCancellationDoesNotPublishALateIgnoringProviderReply()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { Read = async (endpoint, now, _) =>
        {
            entered.TrySetResult(); await release.Task; return [Current(endpoint, now)];
        } };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy);
        using var cancel = new CancellationTokenSource();
        var first = monitor.RefreshAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Empty(monitor.Devices);
        var next = await monitor.RefreshAsync(CancellationToken.None, force: true);
        Assert.Equal(ReadingState.Current, Assert.Single(next).Battery.State);
    }

    [Fact]
    public async Task TransportOpenFailureBacksOffBeforeProviderAndForceRecoversTheUnidentifiedPlaceholder()
    {
        var clock = new Clock();
        var provider = new Provider();
        var factory = new Factory { Failure = new UnauthorizedAccessException("Synthetic access denied") };
        var monitor = new BatteryMonitor(new Discovery(), factory, [provider], options: Policy, timeProvider: clock);
        var unknown = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.EndsWith(":unidentified", unknown.Device.Id);
        Assert.Equal(ReadingState.Failed, unknown.Battery.State);
        clock.Advance(TimeSpan.FromSeconds(10));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(0, provider.Calls);
        factory.Failure = null;
        var recovered = Assert.Single(await monitor.RefreshAsync(CancellationToken.None, force: true));
        Assert.Equal(2, factory.Calls);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("peripheral-receiver", recovered.Device.Id);
        Assert.Equal(ReadingState.Current, recovered.Battery.State);
    }

    [Fact]
    public async Task ReconciliationOfAChangedHidCollectionRetiresThatCollectionsRetryDeadline()
    {
        var clock = new Clock();
        var discovery = new Discovery();
        var provider = new Provider
        {
            Read = (endpoint, now, _) => endpoint.DevicePath == "synthetic"
                ? throw new IOException("Synthetic old path unavailable")
                : Task.FromResult<IReadOnlyList<DeviceSnapshot>>([Current(endpoint, now)])
        };
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy, timeProvider: clock);
        await monitor.RefreshAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(290));
        await monitor.RefreshAsync(CancellationToken.None); //Next retry would be350 seconds.
        Assert.Equal(2, provider.Calls);
        discovery.Endpoints = [Endpoint with { DevicePath = "replaced-collection" }];
        clock.Advance(TimeSpan.FromSeconds(10)); //Periodic cache reconciliation happens at300 seconds.
        var recovered = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(ReadingState.Current, recovered.Battery.State);
    }

    [Fact]
    public async Task ManualPollingAndBackgroundReadContinueSharingTheSameHidGate()
    {
        var gate = new HidOperationGate();
        using var manualLease = await gate.EnterAsync(CancellationToken.None);
        var provider = new Provider();
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], gate, Policy);
        var refresh = monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, provider.Calls);
        manualLease.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ABackwardMonotonicClockCannotMakeBackoffOrMetadataCacheIndefinite()
    {
        var clock = new Clock();
        var provider = new Provider { Failure = new IOException("Synthetic timeout") };
        var discovery = new Discovery();
        var monitor = new BatteryMonitor(discovery, new Factory(), [provider], options: Policy, timeProvider: clock);
        await monitor.RefreshAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(-1));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, discovery.Calls);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CapacityOnlyRepliesReachTheLedgerAndRetainNormalPollingWithoutInventingSoc()
    {
        var clock = new Clock();
        var provider = new Provider { Read = (endpoint, now, _) => Task.FromResult<IReadOnlyList<DeviceSnapshot>>(
            [Current(endpoint, now) with
            {
                Battery = BatteryReading.Unavailable(ReadingState.Unsupported, "SOC is not exposed."),
                CapacityTelemetry = new("synthetic-battery", "synthetic-session", now, true,
                    "Documented synthetic full-capacity field", ReportedCapacity: 800m,
                    ReportedCapacityUnit: CapacityUnit.MilliampHours,
                    ReportedCapacityKind: CapacityFieldKind.FullChargeCapacity)
            }]) };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy, timeProvider: clock);
        var first = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        var directory = Path.Combine(Path.GetTempPath(), "capacity-only-monitor-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ledger = new CapacityHealthService(Path.Combine(directory, "capacity.json"), clock);
            ledger.SetDesignCapacity(first, new(1000m, CapacityUnit.MilliampHours,
                CapacityBasis.BatteryPack, "Synthetic documented original capacity"));
            ledger.Observe([first], clock.GetUtcNow());
            Assert.Equal(80m, ledger.GetDetails(first).HealthPercentage);
            clock.Advance(TimeSpan.FromSeconds(10));
            var next = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
            Assert.Equal(2, provider.Calls); // A healthy capacity query bypasses the unavailable-SOC retry policy.
            Assert.Equal(0, monitor.Metrics.BackoffSkips);
            Assert.NotNull(next.CapacityTelemetry);
            Assert.Equal(clock.GetUtcNow(), next.CapacityTelemetry!.SampledAt);
            Assert.Equal(ReadingState.Unsupported, next.Battery.State);
            Assert.Null(next.Battery.Percentage);
            Assert.Null(next.Battery.SampledAt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unverified")]
    [InlineData("remaining")]
    [InlineData("missing-source")]
    public async Task InvalidCapacityDoesNotKeepAnUnavailableSocReceiverPollingAsHealthy(string failure)
    {
        var clock = new Clock();
        var provider = new Provider { Read = (endpoint, now, _) =>
        {
            var telemetry = new CapacityTelemetry("synthetic-battery", "synthetic-session", now, true,
                "Synthetic full-capacity field", ReportedCapacity: 800m,
                ReportedCapacityUnit: CapacityUnit.MilliampHours, ReportedCapacityKind: CapacityFieldKind.FullChargeCapacity);
            telemetry = failure switch
            {
                "stale" => telemetry with { SampledAt = now.AddMinutes(-3) },
                "unverified" => telemetry with { IdentityVerified = false },
                "remaining" => telemetry with { ReportedCapacityKind = CapacityFieldKind.RemainingCapacity },
                _ => telemetry with { Source = "" }
            };
            return Task.FromResult<IReadOnlyList<DeviceSnapshot>>([Current(endpoint, now) with
            { Battery = BatteryReading.Unavailable(ReadingState.Unsupported, "No SOC."), CapacityTelemetry = telemetry }]);
        } };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy, timeProvider: clock);
        await monitor.RefreshAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, monitor.Metrics.BackoffSkips);
    }

    [Theory]
    [InlineData(ReadingState.Stale, ConnectionState.Connected)]
    [InlineData(ReadingState.Disconnected, ConnectionState.Disconnected)]
    [InlineData(ReadingState.Failed, ConnectionState.Connected)]
    [InlineData(ReadingState.Unsupported, ConnectionState.Unknown)]
    public async Task UnavailableOrUnboundRepliesCannotRetainCapacityTelemetry(ReadingState state, ConnectionState connection)
    {
        var clock = new Clock();
        var provider = new Provider { Read = (endpoint, now, _) => Task.FromResult<IReadOnlyList<DeviceSnapshot>>(
            [Current(endpoint, now) with
            {
                Battery = state == ReadingState.Stale ? BatteryReading.Current(0, ChargingState.Discharging, now).AsStale("Old SOC")
                    : BatteryReading.Unavailable(state, "Unavailable"),
                Connection = connection,
                CapacityTelemetry = new("synthetic-battery", "synthetic-session", now, true,
                    "Synthetic full-capacity field", ReportedCapacity: 800m,
                    ReportedCapacityUnit: CapacityUnit.MilliampHours, ReportedCapacityKind: CapacityFieldKind.FullChargeCapacity)
            }]) };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy, timeProvider: clock);
        Assert.Null(Assert.Single(await monitor.RefreshAsync(CancellationToken.None)).CapacityTelemetry);
    }

    [Fact]
    public async Task InitiatorCancellationRetainsOwnershipUntilNativeCleanupBeforeAForcedResume()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { Read = async (endpoint, now, _) =>
        {
            entered.TrySetResult();
            await release.Task; // Models a native reply that finishes after cancellation was requested.
            return [Current(endpoint, now)];
        } };
        var monitor = new BatteryMonitor(new Discovery(), new Factory(), [provider], options: Policy);
        using var suspend = new CancellationTokenSource();
        var interrupted = monitor.RefreshAsync(suspend.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        suspend.Cancel();
        Assert.False(interrupted.IsCompleted); // The controller must not mistake cancellation for completed native cleanup.
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        var resumed = Assert.Single(await monitor.RefreshAsync(CancellationToken.None, force: true));
        Assert.Equal(ReadingState.Current, resumed.Battery.State);
        Assert.Equal(2, provider.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
        private long timestamp = TimeSpan.TicksPerHour;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) { now += amount; timestamp += amount.Ticks; }
    }
    private sealed class Discovery : IHidDiscovery
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HidEndpoint> Endpoints { get; set; } = [Endpoint];
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public Exception? Failure { get; set; }
        public Action? OnEnumerate { get; set; }
        public IReadOnlyList<HidEndpoint> Enumerate()
        {
            Calls++;
            OnEnumerate?.Invoke();
            return Failure is null ? Endpoints : throw Failure;
        }
    }
    private sealed class Provider : IBatteryProvider
    {
        public string Name => "Synthetic provider";
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Func<HidEndpoint, DateTimeOffset, CancellationToken, Task<IReadOnlyList<DeviceSnapshot>>>? Read { get; set; }
        public bool CanHandle(HidEndpoint endpoint) => true;
        public Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            DateTimeOffset observedAt, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Read?.Invoke(endpoint, observedAt, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<DeviceSnapshot>>([Current(endpoint, observedAt)]);
        }
    }
    private sealed class Factory : IHidTransportFactory
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null ? ValueTask.FromResult<IHidTransport>(new Transport(endpoint)) : throw Failure;
        }
    }
    private sealed class Transport(HidEndpoint endpoint) : IHidTransport
    {
        public HidEndpoint Endpoint => endpoint;
        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
