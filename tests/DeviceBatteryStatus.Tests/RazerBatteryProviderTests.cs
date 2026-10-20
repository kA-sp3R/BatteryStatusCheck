using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerBatteryProviderTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x00A6, "&mi_00", true)]
    [InlineData(0x00A6, "&mi_01", false)]
    [InlineData(0x025C, "&mi_02", true)]
    [InlineData(0x025C, "&mi_00", false)]
    [InlineData(0x0271, "&MI_03", true)]
    [InlineData(0x0271, "&mi_02", false)]
    [InlineData(0x02BA, "&mi_03", true)]
    [InlineData(0x02BA, "&mi_G3", false)]
    [InlineData(0x00A6, "&mi_", false)]
    [InlineData(0x00A6, "&mi_0", false)]
    public void CompositeReceiverQueriesOnlyTheModelSpecificControlInterface(
        ushort productId, string interfaceComponent, bool expected)
    {
        var endpoint = Endpoint(productId) with
        {
            DevicePath = $"fake-usb-vid_1532&pid_{productId:X4}{interfaceComponent}"
        };

        Assert.Equal(expected, new RazerBatteryProvider().CanHandle(endpoint));
    }

    [Fact]
    public async Task SupportedReceiverMayReturnAGenuineZeroPercent()
    {
        var endpoint = Endpoint(0x00B8);
        await using var transport = new FeatureTransport(endpoint, _ => RazerProtocolTests.BatteryReply(0));

        var devices = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal(ReadingState.Current, device.Battery.State);
        Assert.Equal(0, device.Battery.Percentage);
        Assert.Equal(ChargingState.Unknown, device.Battery.Charging);
        Assert.Equal(SampleTime, device.Battery.SampledAt);
        Assert.Equal(endpoint.ReceiverId, device.Device.ReceiverId);
    }

    [Fact]
    public async Task UnknownHyperPollingAssociationProducesNoMeasurementAndNoProtocolCommands()
    {
        var endpoint = Endpoint(0x00B3);
        await using var transport = new FeatureTransport(endpoint, _ => throw new InvalidOperationException("No query allowed."));

        var devices = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal(ReadingState.Unsupported, device.Battery.State);
        Assert.Null(device.Battery.Percentage);
        Assert.Equal(0, transport.FeatureWrites);
    }

    [Fact]
    public async Task InvalidReplyDoesNotSupplyAFallbackPercentage()
    {
        var endpoint = Endpoint(0x00B8);
        await using var transport = new FeatureTransport(endpoint, _ => new byte[91]);

        var device = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, device.Battery.State);
        Assert.Null(device.Battery.Percentage);
    }

    [Fact]
    public async Task FailedChargingQueryDoesNotDiscardAValidBatteryMeasurementOrInventCharging()
    {
        var endpoint = Endpoint(0x00A6);
        await using var transport = new FeatureTransport(endpoint, request => request[8] == 0x80
            ? RazerProtocolTests.BatteryReply(0)
            : throw new TimeoutException("No charging response."));

        var device = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, device.Battery.State);
        Assert.Equal(0, device.Battery.Percentage);
        Assert.Equal(ChargingState.Unknown, device.Battery.Charging);
    }

    [Fact]
    public async Task ReceiverTimeoutMeansFailedReadRatherThanProofOfDisconnection()
    {
        var endpoint = Endpoint(0x00B8);
        await using var transport = new FeatureTransport(endpoint, _ => throw new TimeoutException("No response."));

        var device = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, device.Battery.State);
        Assert.Null(device.Battery.Percentage);
    }

    private static HidEndpoint Endpoint(ushort productId) =>
        new("fake-usb-endpoint", 0x1532, productId, 0xFF00, 1, 0, 0, 91, "receiver-a");

    private sealed class FeatureTransport(HidEndpoint endpoint, Func<byte[], byte[]> response) : IHidTransport
    {
        private byte[]? request;
        public HidEndpoint Endpoint { get; } = endpoint;
        public int FeatureWrites { get; private set; }

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FeatureWrites++;
            request = report.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(response(request ?? throw new InvalidOperationException("No feature request sent.")));
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake supports feature reports only.");

        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake supports feature reports only.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
