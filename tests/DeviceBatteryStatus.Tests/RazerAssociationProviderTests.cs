using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerAssociationProviderTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(128, 50)]
    [InlineData(255, 100)]
    public async Task AConfirmedHyperPollingChildReportsItsActualBatteryIncludingGenuineZero(byte raw, int expected)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint) { BatteryRaw = raw };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(expected, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal("receiver-associated:razer:00B3/wireless/00C1", result.Device.Id);
        Assert.Equal("1532:00C1", result.Device.ModelId);
        Assert.Equal("Razer Viper V3 Pro", result.Device.Name);
        Assert.Equal(new byte[] { 0xBF, 0x80, 0xBF, 0x84, 0xBF }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.All(transport.Commands, request => Assert.Equal(0x1F, request[2]));
    }

    [Theory]
    [InlineData((byte)1, (byte)0x01)]
    [InlineData((byte)2, (byte)0x81)]
    public async Task AnUnknownWirelessProductNeedsAnExplicitRoleAndKeepsItsNativeIdentity(byte primaryType, byte transaction)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint) { ChildProduct = 0xBEEF, PrimaryType = primaryType };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal("receiver-associated:razer:00B3/wireless/BEEF", result.Device.Id);
        Assert.Equal("1532:BEEF", result.Device.ModelId);
        Assert.Equal("Razer wireless peripheral (1532:BEEF)", result.Device.Name);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ChargingState.Charging, result.Battery.Charging);
        Assert.Equal(new byte[] { 0xBF, 0xC5, 0xBF, 0x80, 0xBF, 0xC5, 0x84, 0xBF, 0xC5 },
            transport.Commands.Select(request => request[8]).ToArray());
        Assert.All(transport.Commands.Where(request => request[7] == 7), request => Assert.Equal(transaction, request[2]));
    }

    [Fact]
    public async Task AnUnknownHyperPollingChildCannotBeAssumedToUseTheMouseNamespace()
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint) { ChildProduct = 0xBEEF };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Unsupported, result.Battery.State);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.SampledAt);
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.DoesNotContain(transport.Commands, request => request[7] == 7);
    }

    [Fact]
    public async Task ExplicitUnsupportedModernAssociationUsesTheDocumentedLegacyRequestOnly()
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (_, request) => request[2] == 0x1F ? DeviceListStatus(request, 5) : null
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        var list = transport.Commands.Where(request => request[8] == 0xBF).ToArray();
        Assert.Equal(new byte[] { 0x1F, 0xFF, 0x1F, 0xFF, 0x1F, 0xFF }, list.Select(request => request[2]).ToArray());
        Assert.All(list.Where(request => request[2] == 0xFF), request =>
        {
            Assert.Equal(0x31, request[6]);
            Assert.Equal(0x10, request[9]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoConnectedChildNeverStartsAnUntargetedBatteryRead(bool unoccupied)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (_, request) => unoccupied ? DeviceList(request) : DeviceList(request, (0x00C1, false))
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.SampledAt);
        Assert.Single(transport.Commands);
        Assert.Equal(0xBF, transport.Commands[0][8]);
    }

    [Fact]
    public async Task MultipleConnectedChildrenRemainSeparateAndCannotReceiveTheSameUntargetedBatteryValue()
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (_, request) => DeviceList(request, (0x00C1, true), (0x025C, true))
        };

        var results = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, results.Select(result => result.Device.Id).Distinct().Count());
        Assert.All(results, result =>
        {
            Assert.Equal(ReadingState.Unsupported, result.Battery.State);
            Assert.Equal(ConnectionState.Connected, result.Connection);
            Assert.Null(result.Battery.Percentage);
            Assert.Null(result.Battery.SampledAt);
        });
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.DoesNotContain(transport.Commands, request => request[7] == 7);
    }

    [Fact]
    public async Task AChangedProductDuringTheBatteryReadDiscardsTheValueInsteadOfAttributingItToEitherProduct()
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            BatteryRaw = 220,
            ListOverride = (index, request) => index == 2 ? DeviceList(request, (0x00BF, true)) : null
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.EndsWith("/wireless/00C1", result.Device.Id);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.SampledAt);
        Assert.Equal(new byte[] { 0xBF, 0x80, 0xBF }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APeripheralDisconnectedDuringChargingCannotPublishItsEarlierBattery(bool replaced)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (index, request) => index == 3
                ? replaced ? DeviceList(request, (0x00BF, true)) : DeviceList(request, (0x00C1, false)) : null
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.SampledAt);
        Assert.Equal(new byte[] { 0xBF, 0x80, 0xBF, 0x84, 0xBF }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Theory]
    [InlineData(ChargeConfirmationFailure.Timeout)]
    [InlineData(ChargeConfirmationFailure.Corrupt)]
    [InlineData(ChargeConfirmationFailure.Failed)]
    [InlineData(ChargeConfirmationFailure.Unsupported)]
    public async Task FailedChargingAssociationKeepsConfirmedZeroWithoutClaimingCharging(ChargeConfirmationFailure failure)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (index, request) =>
            {
                if (index < 3) return null;
                if (failure == ChargeConfirmationFailure.Timeout) throw new TimeoutException("Charging association closed the session.");
                if (failure != ChargeConfirmationFailure.Corrupt)
                    return DeviceListStatus(request, failure == ChargeConfirmationFailure.Unsupported ? (byte)5 : (byte)3);
                var reply = DeviceList(request, (0x00C1, true));
                reply[89] ^= 1;
                return reply;
            }
        };

        var results = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(results).AssociationComplete);
        Assert.Equal(failure == ChargeConfirmationFailure.Unsupported ? 6 : 5, transport.Commands.Count);
        Assert.Equal(0xBF, transport.Commands[^1][8]);
    }

    [Fact]
    public async Task ANewPeerDuringChargingCannotSupplyTheOriginalPeersChargingState()
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (index, request) => index == 3 ? DeviceList(request, (0x00C1, true), (0x025C, true)) : null
        };

        var results = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(results).AssociationComplete);
        Assert.Equal(5, transport.Commands.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnAssociationTimeoutIsFailedAndCannotReuseTheSession(int failedList)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (index, _) => index == failedList ? throw new TimeoutException("Session closed.") : null
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.SampledAt);
        Assert.Equal(failedList == 1 ? 1 : 3, transport.Commands.Count);
        Assert.Equal(0xBF, transport.Commands[^1][8]);
        Assert.All(transport.Commands, request => Assert.Equal(0x1F, request[2]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MalformedOrUncorrelatedAssociationCannotSupplyACurrentBattery(int failedList)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (index, request) =>
            {
                if (index != failedList) return null;
                var reply = DeviceList(request, (0x00C1, true));
                reply[89] ^= 1;
                return reply;
            }
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Null(result.Battery.Percentage);
        Assert.Equal(failedList == 1 ? 1 : 3, transport.Commands.Count);
    }

    [Theory]
    [InlineData(3, ReadingState.Failed, 1)]
    [InlineData(4, ReadingState.Failed, 1)]
    [InlineData(5, ReadingState.Unsupported, 2)]
    public async Task UnsupportedAndFailedDeviceListsCannotTurnTheirEmptyPayloadIntoZeroBattery(
        byte status, ReadingState expectedState, int expectedQueries)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint)
        {
            ListOverride = (_, request) => DeviceListStatus(request, status)
        };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(expectedState, result.Battery.State);
        Assert.Null(result.Battery.Percentage);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Equal(expectedQueries, transport.Commands.Count);
        Assert.All(transport.Commands, request => Assert.Equal(0xBF, request[8]));
    }

    [Theory]
    [InlineData(ChargeFailure.Unsupported)]
    [InlineData(ChargeFailure.Corrupt)]
    [InlineData(ChargeFailure.Timeout)]
    [InlineData(ChargeFailure.TimeoutReply)]
    public async Task AChargingFailurePreservesAnAlreadyConfirmedZeroBatteryAndEndsTheSession(ChargeFailure failure)
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint) { Failure = failure };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal(4, transport.Commands.Count);
        Assert.Equal(0x84, transport.Commands[^1][8]);
    }

    [Fact]
    public async Task AReplaceableBatteryChildDoesNotAcquireAChargingClaimFromItsReceiver()
    {
        var endpoint = Endpoint();
        await using var transport = new ReceiverTransport(endpoint) { ChildProduct = 0x00B8 };

        var result = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(ChargingState.Unknown, result.Battery.Charging);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.DoesNotContain(transport.Commands, request => request[8] == 0x84);
    }

    [Fact]
    public async Task RePairingAReceiverChangesThePublishedChildIdentity()
    {
        var endpoint = Endpoint();
        var provider = new RazerBatteryProvider();
        await using var firstTransport = new ReceiverTransport(endpoint) { ChildProduct = 0x00C1 };
        await using var secondTransport = new ReceiverTransport(endpoint) { ChildProduct = 0x00BF };

        var first = Assert.Single(await provider.ReadAsync(endpoint, firstTransport, SampleTime, CancellationToken.None));
        var second = Assert.Single(await provider.ReadAsync(endpoint, secondTransport, SampleTime.AddSeconds(1), CancellationToken.None));

        Assert.NotEqual(first.Device.Id, second.Device.Id);
        Assert.Equal(first.Device.ReceiverId, second.Device.ReceiverId);
        Assert.Equal("1532:00C1", first.Device.ModelId);
        Assert.Equal("1532:00BF", second.Device.ModelId);
        Assert.Equal("Razer DeathAdder V4 Pro", second.Device.Name);
    }

    public enum ChargeFailure { None, Unsupported, Corrupt, Timeout, TimeoutReply }
    public enum ChargeConfirmationFailure { Timeout, Corrupt, Failed, Unsupported }

    private static HidEndpoint Endpoint() =>
        new("fake-usb-vid_1532&mi_00", 0x1532, 0x00B3, 0xFF00, 1, 0, 0, 91, "receiver-associated");

    private static byte[] DeviceList(byte[] request, params (ushort ProductId, bool Connected)[] children)
    {
        var reply = request.ToArray();
        reply[1] = 2;
        Array.Clear(reply, 9, 80);
        reply[9] = (byte)children.Length;
        for (int index = 0; index < children.Length; index++)
        {
            int offset = 10 + index * 3;
            reply[offset] = children[index].Connected ? (byte)1 : (byte)0;
            reply[offset + 1] = (byte)(children[index].ProductId >> 8);
            reply[offset + 2] = (byte)children[index].ProductId;
        }
        RazerProtocolTests.SetChecksum(reply);
        return reply;
    }

    private static byte[] DeviceListStatus(byte[] request, byte status)
    {
        var reply = request.ToArray();
        reply[1] = status;
        RazerProtocolTests.SetChecksum(reply);
        return reply;
    }

    private sealed class ReceiverTransport(HidEndpoint endpoint) : IHidTransport
    {
        private byte[]? pending;
        private int associationReads;
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Commands { get; } = [];
        public ushort ChildProduct { get; init; } = 0x00C1;
        public byte? PrimaryType { get; init; }
        public byte BatteryRaw { get; init; }
        public ChargeFailure Failure { get; init; }
        public Func<int, byte[], byte[]?>? ListOverride { get; init; }

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
            if (request[8] == 0xBF)
            {
                associationReads++;
                return ValueTask.FromResult(ListOverride?.Invoke(associationReads, request) ?? DeviceList(request, (ChildProduct, true)));
            }
            if (request[8] == 0xC5)
            {
                if (PrimaryType is null) return ValueTask.FromResult(DeviceListStatus(request, 5));
                var primary = request.ToArray();
                primary[1] = 2;
                primary[9] = PrimaryType.Value;
                primary[10] = (byte)(ChildProduct >> 8);
                primary[11] = (byte)ChildProduct;
                RazerProtocolTests.SetChecksum(primary);
                return ValueTask.FromResult(primary);
            }
            if (request[8] is not (0x80 or 0x84)) throw new InvalidOperationException("Unexpected non-battery command.");
            if (request[8] == 0x84 && Failure == ChargeFailure.Timeout) throw new TimeoutException("Charging query closed the session.");
            var reply = request.ToArray();
            reply[1] = request[8] == 0x84 ? Failure switch
            {
                ChargeFailure.Unsupported => (byte)5,
                ChargeFailure.TimeoutReply => (byte)4,
                _ => (byte)2
            } : (byte)2;
            reply[10] = request[8] == 0x80 ? BatteryRaw : (byte)1;
            RazerProtocolTests.SetChecksum(reply);
            if (request[8] == 0x84 && Failure == ChargeFailure.Corrupt) reply[89] ^= 1;
            return ValueTask.FromResult(reply);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
