using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerConnectionProviderTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, ReadingState.Disconnected, ConnectionState.Disconnected)]
    [InlineData(1, ReadingState.Current, ConnectionState.Connected)]
    [InlineData(2, ReadingState.Current, ConnectionState.Unknown)]
    [InlineData(255, ReadingState.Current, ConnectionState.Unknown)]
    public async Task AnIndependentLinkReplyControlsConnectionWithoutInventingOrDiscardingAReportedZero(
        byte link, ReadingState state, ConnectionState connection)
    {
        var endpoint = Endpoint(0x00B8);
        await using var transport = new LegacyTransport(endpoint) { Link = link };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(state, result.Battery.State);
        Assert.Equal(connection, result.Connection);
        Assert.Equal(state == ReadingState.Current ? 0 : (int?)null, result.Battery.Percentage);
        Assert.Equal(state == ReadingState.Current ? SampleTime : (DateTimeOffset?)null, result.Battery.SampledAt);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.Equal(new byte[] { 0x80, 0xBF, 0xB8 }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.Equal(new byte[] { 0x1F, 0x01, 0x01 }, transport.Commands.Select(request => request[2]).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATimeoutOrMalformedOptionalAssociationRetainsTheCompletedSampleWithoutReusingTheSession(bool corrupt)
    {
        var endpoint = Endpoint(0x00B8);
        await using var transport = new LegacyTransport(endpoint) { AssociationCorrupt = corrupt, AssociationTimeout = !corrupt };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Equal(new byte[] { 0x80, 0xBF }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.Equal(0x01, transport.Commands[^1][2]);
        Assert.DoesNotContain(transport.Commands, request => request[2] == 0xFF);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AFailedOptionalAssociationDoesNotStartAnotherDiscoveryProtocol(byte status)
    {
        var endpoint = Endpoint(0x00B8);
        await using var transport = new LegacyTransport(endpoint) { AssociationStatus = status };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Equal(new byte[] { 0x80, 0xBF }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Fact]
    public async Task AChargingTimeoutStopsBeforeAnyOptionalAssociationOrLinkProbe()
    {
        var endpoint = Endpoint(0x00C1);
        await using var transport = new LegacyTransport(endpoint) { ChargingTimeout = true };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Equal(new byte[] { 0x80, 0x84 }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Fact]
    public async Task KeyboardDiscoveryUsesItsOwnNamespaceInsteadOfTheBatteryTransaction()
    {
        var endpoint = Endpoint(0x025C);
        await using var transport = new LegacyTransport(endpoint) { Link = 1 };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal(new byte[] { 0x80, 0x84, 0xBF, 0xB8 }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.Equal(new byte[] { 0x9F, 0x9F, 0x81, 0x81 }, transport.Commands.Select(request => request[2]).ToArray());
    }

    private static HidEndpoint Endpoint(ushort productId)
    {
        byte control = RazerBatteryProvider.GetExpectedInterfaceNumber(productId)!.Value;
        return new($"fake-usb-vid_1532&mi_{control:X2}", 0x1532, productId, 0xFF00, 1, 0, 0, 91, "receiver-legacy");
    }

    private sealed class LegacyTransport(HidEndpoint endpoint) : IHidTransport
    {
        private byte[]? pending;
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Commands { get; } = [];
        public byte Link { get; init; }
        public byte AssociationStatus { get; init; } = 5;
        public bool AssociationTimeout { get; init; }
        public bool AssociationCorrupt { get; init; }
        public bool ChargingTimeout { get; init; }

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending = report.ToArray();
            Commands.Add(pending);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = pending ?? throw new InvalidOperationException("No feature request was sent.");
            if (request[8] == 0xBF && AssociationTimeout) throw new TimeoutException("Association query closed the session.");
            if (request[8] == 0x84 && ChargingTimeout) throw new TimeoutException("Charging query closed the session.");
            var reply = request.ToArray();
            reply[1] = request[8] == 0xBF ? AssociationStatus : (byte)2;
            if (request[8] == 0xB8) reply[9] = Link;
            else if (request[8] is 0x80 or 0x84) reply[10] = 0;
            else if (request[8] != 0xBF) throw new InvalidOperationException("Unexpected protocol command.");
            RazerProtocolTests.SetChecksum(reply);
            if (request[8] == 0xBF && AssociationCorrupt) reply[89] ^= 1;
            return ValueTask.FromResult(reply);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
