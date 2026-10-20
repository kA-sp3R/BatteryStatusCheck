using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class BatteryMonitorTests
{
    private static readonly HidEndpoint Endpoint =
        new("fake-usb-endpoint", 0x1532, 0x00B8, 0xFF00, 1, 0, 0, 91, "receiver-a");
    private static readonly PeripheralIdentity Peripheral =
        new("receiver-a/peripheral-a", "Test peripheral", DeviceVendor.Razer, "receiver-a");
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadFailureRetainsGenuineZeroAsStaleWithoutRefreshingItsTimestamp()
    {
        var discovery = new StubDiscovery();
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(0)]);
        provider.Results.Enqueue(() => [Failed()]);
        var monitor = new BatteryMonitor(discovery, new StubTransportFactory(), [provider]);

        await monitor.RefreshAsync(CancellationToken.None);
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Stale, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
    }

    [Fact]
    public async Task RemovingReceiverClearsBatteryPercentageAndMarksDeviceDisconnected()
    {
        var discovery = new StubDiscovery();
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(54)]);
        var monitor = new BatteryMonitor(discovery, new StubTransportFactory(), [provider]);

        await monitor.RefreshAsync(CancellationToken.None);
        discovery.Endpoints = [];
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.SampledAt);
        Assert.Equal(1, provider.Reads);
    }

    [Fact]
    public async Task DiscoveryFailurePreservesStaleDataInsteadOfClaimingAllReceiversWereRemoved()
    {
        var discovery = new StubDiscovery();
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(54)]);
        var monitor = new BatteryMonitor(discovery, new StubTransportFactory(), [provider]);

        await monitor.RefreshAsync(CancellationToken.None);
        discovery.Failure = new IOException("Device enumeration unavailable.");
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Stale, result.Battery.State);
        Assert.Equal(54, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Contains("Device enumeration unavailable", monitor.DiscoveryError);
        Assert.Equal(1, provider.Reads);
    }

    [Fact]
    public async Task PartialDiscoveryCannotProveAnAbsentReceiverWasDisconnected()
    {
        var discovery = new StubDiscovery();
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(0)]);
        var monitor = new BatteryMonitor(discovery, new StubTransportFactory(), [provider]);
        await monitor.RefreshAsync(CancellationToken.None);

        discovery.Endpoints = [];
        discovery.Warnings = ["A USB HID collection could not be inspected."];
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Stale, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Contains("incomplete", monitor.DiscoveryError);
        Assert.Equal(1, provider.Reads);

        discovery.Warnings = [];
        var confirmedAbsent = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Null(monitor.DiscoveryError);
        Assert.Equal(ReadingState.Disconnected, confirmedAbsent.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, confirmedAbsent.Connection);
        Assert.Null(confirmedAbsent.Battery.Percentage);
    }

    [Fact]
    public async Task PublishingANewRefreshDoesNotMutateThePreviouslyReturnedSnapshot()
    {
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(54)]);
        provider.Results.Enqueue(() => [Failed()]);
        var monitor = new BatteryMonitor(new StubDiscovery(), new StubTransportFactory(), [provider]);

        var original = await monitor.RefreshAsync(CancellationToken.None);
        var updated = await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal(ReadingState.Current, Assert.Single(original).Battery.State);
        Assert.Equal(ReadingState.Stale, Assert.Single(updated).Battery.State);
        Assert.Same(updated, monitor.Devices);
    }

    [Fact]
    public async Task TransportFailurePreservesPreviousMeasurementAsStale()
    {
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(0)]);
        var transports = new StubTransportFactory();
        var monitor = new BatteryMonitor(new StubDiscovery(), transports, [provider]);

        await monitor.RefreshAsync(CancellationToken.None);
        transports.Failure = new UnauthorizedAccessException("Receiver access denied.");
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Stale, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
    }

    [Fact]
    public async Task ReceiverPresenceWithFailedFirstQueryDoesNotMeanConnectedOrZeroPercent()
    {
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Failed()]);
        var monitor = new BatteryMonitor(new StubDiscovery(), new StubTransportFactory(), [provider]);

        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Fact]
    public async Task OmittedPeripheralDoesNotKeepItsPreviousMeasurementCurrent()
    {
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(80)]);
        provider.Results.Enqueue(() => []);
        var monitor = new BatteryMonitor(new StubDiscovery(), new StubTransportFactory(), [provider]);

        await monitor.RefreshAsync(CancellationToken.None);
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Stale, result.Battery.State);
        Assert.Equal(80, result.Battery.Percentage);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
    }

    [Fact]
    public async Task ReceiverWithoutAQueryableCollectionDoesNotKeepAnOldMeasurementCurrent()
    {
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(80)]);
        var monitor = new BatteryMonitor(new StubDiscovery(), new StubTransportFactory(), [provider]);
        await monitor.RefreshAsync(CancellationToken.None);

        provider.HandlesEndpoints = false;
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Stale, result.Battery.State);
        Assert.Equal(80, result.Battery.Percentage);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Equal(1, provider.Reads);
    }

    [Fact]
    public async Task IdentifiedPeripheralReplacesTheInitialAccessFailurePlaceholder()
    {
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(61)]);
        var transports = new StubTransportFactory { Failure = new UnauthorizedAccessException("Access denied.") };
        var monitor = new BatteryMonitor(new StubDiscovery(), transports, [provider]);
        var placeholder = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        Assert.Equal(ReadingState.Failed, placeholder.Battery.State);

        transports.Failure = null;
        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(Peripheral.Id, result.Device.Id);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(61, result.Battery.Percentage);
    }

    [Fact]
    public async Task SuccessfulRefreshAfterDiscoveryFailureRestoresCurrentDataAndClearsError()
    {
        var discovery = new StubDiscovery();
        var provider = new StubProvider();
        provider.Results.Enqueue(() => [Current(80)]);
        provider.Results.Enqueue(() => [new(Peripheral,
            BatteryReading.Current(79, ChargingState.Discharging, SampleTime.AddMinutes(1)),
            ConnectionState.Connected)]);
        var monitor = new BatteryMonitor(discovery, new StubTransportFactory(), [provider]);
        await monitor.RefreshAsync(CancellationToken.None);
        discovery.Failure = new IOException("Temporary failure.");
        await monitor.RefreshAsync(CancellationToken.None);
        discovery.Failure = null;

        var result = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Null(monitor.DiscoveryError);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(79, result.Battery.Percentage);
        Assert.Equal(SampleTime.AddMinutes(1), result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Connected, result.Connection);
    }

    private static DeviceSnapshot Current(int percentage) => new(Peripheral,
        BatteryReading.Current(percentage, ChargingState.Charging, SampleTime), ConnectionState.Connected);

    private static DeviceSnapshot Failed() => new(Peripheral,
        BatteryReading.Unavailable(ReadingState.Failed, "No matching response."));

    private sealed class StubDiscovery : IHidDiscovery
    {
        public IReadOnlyList<HidEndpoint> Endpoints { get; set; } = [Endpoint];
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public Exception? Failure { get; set; }
        public IReadOnlyList<HidEndpoint> Enumerate() => Failure is not null ? throw Failure : Endpoints;
    }

    private sealed class StubProvider : IBatteryProvider
    {
        public string Name => "Test provider";
        public int Reads { get; private set; }
        public bool HandlesEndpoints { get; set; } = true;
        public Queue<Func<IReadOnlyList<DeviceSnapshot>>> Results { get; } = new();
        public bool CanHandle(HidEndpoint endpoint) => HandlesEndpoints && endpoint.VendorId == 0x1532;
        public Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            DateTimeOffset observedAt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Results.Dequeue()());
        }
    }

    private sealed class StubTransportFactory : IHidTransportFactory
    {
        public Exception? Failure { get; set; }
        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken) =>
            Failure is not null ? throw Failure : ValueTask.FromResult<IHidTransport>(new EmptyTransport(endpoint));
    }

    private sealed class EmptyTransport(HidEndpoint endpoint) : IHidTransport
    {
        public HidEndpoint Endpoint { get; } = endpoint;
        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
