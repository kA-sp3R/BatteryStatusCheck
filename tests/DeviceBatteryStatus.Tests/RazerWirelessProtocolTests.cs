using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerWirelessProtocolTests
{
    [Theory]
    [InlineData(false, 0x1F, 0x50, 0)]
    [InlineData(true, 0xFF, 0x31, 0x10)]
    public void DeviceListRequestsAreReadOnlyAndMatchTheDocumentedProfiles(
        bool legacy, byte transaction, byte size, byte firstArgument)
    {
        var request = RazerWirelessProtocol.CreateDeviceListRequest(legacy);

        Assert.Equal(91, request.Length);
        Assert.Equal(0, request[0]);
        Assert.Equal(0, request[1]);
        Assert.Equal(transaction, request[2]);
        Assert.Equal(size, request[6]);
        Assert.Equal(0, request[7]);
        Assert.Equal(0xBF, request[8]);
        Assert.Equal(firstArgument, request[9]);
        Assert.All(request.AsSpan(10, 79).ToArray(), value => Assert.Equal(0, value));
        var expected = request.ToArray();
        RazerProtocolTests.SetChecksum(expected);
        Assert.Equal(expected[89], request[89]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AConnectedReplyReportsTheDevicePidAndIndependentLinkState(bool legacy)
    {
        var reply = RazerWirelessProtocol.ParseDeviceListReply(Reply([1, 1, 0, 0xA6], legacy), legacy);

        Assert.Equal(RazerReplyStatus.Success, reply.Status);
        var device = Assert.Single(reply.Devices);
        Assert.Equal(0x00A6, device.ProductId);
        Assert.Equal(ConnectionState.Connected, device.Connection);
    }

    [Fact]
    public void AReportedPidDoesNotMeanThePeripheralIsConnected()
    {
        var reply = RazerWirelessProtocol.ParseDeviceListReply(Reply([1, 0, 0, 0xA6]));

        var device = Assert.Single(reply.Devices);
        Assert.Equal(0x00A6, device.ProductId);
        Assert.Equal(ConnectionState.Disconnected, device.Connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplicitUnoccupiedSlotDoesNotInventADevice(bool legacy)
    {
        var reply = RazerWirelessProtocol.ParseDeviceListReply(Reply([1, 0, 0xFF, 0xFF], legacy), legacy);

        Assert.Equal(RazerReplyStatus.Success, reply.Status);
        Assert.Empty(reply.Devices);
    }

    [Fact]
    public void ZeroCountIsAnExplicitEmptyList()
    {
        var reply = RazerWirelessProtocol.ParseDeviceListReply(Reply([0]));

        Assert.Equal(RazerReplyStatus.Success, reply.Status);
        Assert.Empty(reply.Devices);
    }

    [Fact]
    public void MultipleEntriesRemainSeparateWithoutSelectingOneAsTheBatteryTarget()
    {
        var reply = RazerWirelessProtocol.ParseDeviceListReply(Reply([3, 1, 0, 0xA6, 0, 0, 0xB7, 1, 2, 0x96]));

        Assert.Equal(3, reply.Devices.Count);
        Assert.Equal(0x00A6, reply.Devices[0].ProductId);
        Assert.Equal(ConnectionState.Connected, reply.Devices[0].Connection);
        Assert.Equal(0x00B7, reply.Devices[1].ProductId);
        Assert.Equal(ConnectionState.Disconnected, reply.Devices[1].Connection);
        Assert.Equal(0x0296, reply.Devices[2].ProductId);
        Assert.Equal(ConnectionState.Connected, reply.Devices[2].Connection);
    }

    [Theory]
    [InlineData(1, RazerReplyStatus.Busy)]
    [InlineData(3, RazerReplyStatus.Failed)]
    [InlineData(4, RazerReplyStatus.TimedOut)]
    [InlineData(5, RazerReplyStatus.Unsupported)]
    public void AFailedOrUnsupportedQueryCannotLookLikeAnEmptySuccessfulList(byte status, RazerReplyStatus expected)
    {
        var report = Reply([0]);
        report[1] = status;

        var reply = RazerWirelessProtocol.ParseDeviceListReply(report);

        Assert.Equal(expected, reply.Status);
        Assert.Empty(reply.Devices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(92)]
    public void TruncatedOrOversizedFeatureReportsAreRejected(int length) =>
        Assert.Throws<InvalidDataException>(() => RazerWirelessProtocol.ParseDeviceListReply(new byte[length]));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0xFF)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 0x31)]
    [InlineData(7, 7)]
    [InlineData(8, 0x80)]
    [InlineData(90, 1)]
    public void UnrelatedOrMalformedHeadersCannotProduceConnectionEvidence(int offset, byte value)
    {
        var report = Reply([1, 1, 0, 0xA6]);
        report[offset] = value;
        RazerProtocolTests.SetChecksum(report);

        Assert.Throws<InvalidDataException>(() => RazerWirelessProtocol.ParseDeviceListReply(report));
    }

    [Fact]
    public void CorruptPayloadCannotProduceConnectionEvidence()
    {
        var report = Reply([1, 1, 0, 0xA6]);
        report[12] ^= 1;

        Assert.Throws<InvalidDataException>(() => RazerWirelessProtocol.ParseDeviceListReply(report));
    }

    [Theory]
    [InlineData(false, 27)]
    [InlineData(true, 17)]
    public void CountMustFitWithinTheDeclaredPayload(bool legacy, byte count) =>
        Assert.Throws<InvalidDataException>(() => RazerWirelessProtocol.ParseDeviceListReply(Reply([count], legacy), legacy));

    [Fact]
    public void UnknownConnectionFlagsCannotBeTreatedAsDisconnected() =>
        Assert.Throws<InvalidDataException>(() =>
            RazerWirelessProtocol.ParseDeviceListReply(Reply([1, 2, 0, 0xA6])));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0xFF, 0xFF)]
    public void InvalidProductIdsCannotBecomePeripheralIdentities(byte connection, byte high, byte low) =>
        Assert.Throws<InvalidDataException>(() =>
            RazerWirelessProtocol.ParseDeviceListReply(Reply([1, connection, high, low])));

    [Fact]
    public void DuplicateProductIdsCannotSilentlyMergeTwoPeripheralRecords() =>
        Assert.Throws<InvalidDataException>(() =>
            RazerWirelessProtocol.ParseDeviceListReply(Reply([2, 1, 0, 0xA6, 0, 0, 0xA6])));

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x1F)]
    [InlineData(0x80)]
    [InlineData(0x9F)]
    [InlineData(0xE0)]
    [InlineData(0xFF)]
    public void AddressedDeviceListQueriesCorrelateTheExactCallerSelectedNamespace(byte transaction)
    {
        var report = RazerWirelessProtocol.CreateDeviceListRequest(transactionId: transaction);
        report[1] = 2;
        report[9] = 1;
        report[10] = 1;
        report[12] = 0xA6;
        RazerProtocolTests.SetChecksum(report);

        var reply = RazerWirelessProtocol.ParseDeviceListReply(report, transactionId: transaction);

        Assert.Equal(0x00A6, Assert.Single(reply.Devices).ProductId);
        Assert.Throws<InvalidDataException>(() => RazerWirelessProtocol.ParseDeviceListReply(
            report, transactionId: (byte)(transaction ^ 0x80)));
    }

    [Theory]
    [InlineData(false, 26)]
    [InlineData(true, 16)]
    public void MaximumCompleteDeviceListFitsWithoutReadingChecksumOrPadding(bool legacy, byte count)
    {
        var arguments = new byte[1 + count * 3];
        arguments[0] = count;
        for (int index = 0; index < count; index++)
        {
            arguments[1 + index * 3] = 1;
            arguments[2 + index * 3] = 1;
            arguments[3 + index * 3] = (byte)index;
        }

        var reply = RazerWirelessProtocol.ParseDeviceListReply(Reply(arguments, legacy), legacy);

        Assert.Equal(count, reply.Devices.Count);
        Assert.All(reply.Devices, device => Assert.Equal(ConnectionState.Connected, device.Connection));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void PrimaryDeviceTypeAndPidComeFromTheReportedFields(byte type)
    {
        var request = RazerWirelessProtocol.CreatePrimaryDeviceRequest(0xE1);
        Assert.Equal(3, request[6]);
        Assert.Equal(0, request[7]);
        Assert.Equal(0xC5, request[8]);
        var reply = RazerWirelessProtocol.ParsePrimaryDeviceReply(Complete(request, [type, 2, 0x96]), 0xE1);

        Assert.Equal(RazerReplyStatus.Success, reply.Status);
        Assert.Equal(type, reply.DeviceType);
        Assert.Equal((ushort)0x0296, reply.ProductId);
    }

    [Theory]
    [InlineData(0, 0, 0xA6)]
    [InlineData(8, 0, 0xA6)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0xFF, 0xFF)]
    public void AnInvalidPrimaryIdentityCannotDeterminePeripheralRouting(byte type, byte high, byte low) =>
        Assert.Throws<InvalidDataException>(() => RazerWirelessProtocol.ParsePrimaryDeviceReply(
            Complete(RazerWirelessProtocol.CreatePrimaryDeviceRequest(0xE1), [type, high, low]), 0xE1));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NonSuccessPrimaryQueriesHaveNoIdentityFields(byte status)
    {
        var report = Complete(RazerWirelessProtocol.CreatePrimaryDeviceRequest(0xE1), [1, 0, 0xA6]);
        report[1] = status;

        var reply = RazerWirelessProtocol.ParsePrimaryDeviceReply(report, 0xE1);

        Assert.NotEqual(RazerReplyStatus.Success, reply.Status);
        Assert.Null(reply.DeviceType);
        Assert.Null(reply.ProductId);
    }

    [Theory]
    [InlineData(0, ConnectionState.Disconnected)]
    [InlineData(1, ConnectionState.Connected)]
    [InlineData(2, ConnectionState.Unknown)]
    [InlineData(255, ConnectionState.Unknown)]
    public void ConnectionQueriesKeepUndefinedBytesUnknown(byte flag, ConnectionState expected)
    {
        var request = RazerWirelessProtocol.CreateConnectionRequest(0x9F);
        Assert.Equal(1, request[6]);
        Assert.Equal(0, request[7]);
        Assert.Equal(0xB8, request[8]);

        var reply = RazerWirelessProtocol.ParseConnectionReply(Complete(request, [flag]), 0x9F);

        Assert.Equal(RazerReplyStatus.Success, reply.Status);
        Assert.Equal(expected, reply.Connection);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NonSuccessConnectionQueriesCannotEstablishAConnectedOrDisconnectedPeripheral(byte status)
    {
        var report = Complete(RazerWirelessProtocol.CreateConnectionRequest(0x1F), [1]);
        report[1] = status;

        var reply = RazerWirelessProtocol.ParseConnectionReply(report, 0x1F);

        Assert.NotEqual(RazerReplyStatus.Success, reply.Status);
        Assert.Equal(ConnectionState.Unknown, reply.Connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimaryAndConnectionQueriesRequireExactTransactionCommandAndPayload(bool primary)
    {
        var report = primary
            ? Complete(RazerWirelessProtocol.CreatePrimaryDeviceRequest(0x1F), [1, 0, 0xA6])
            : Complete(RazerWirelessProtocol.CreateConnectionRequest(0x1F), [1]);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (primary) RazerWirelessProtocol.ParsePrimaryDeviceReply(report, 0x9F);
            else RazerWirelessProtocol.ParseConnectionReply(report, 0x9F);
        });
        var wrongSize = report.ToArray();
        wrongSize[6]++;
        RazerProtocolTests.SetChecksum(wrongSize);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (primary) RazerWirelessProtocol.ParsePrimaryDeviceReply(wrongSize, 0x1F);
            else RazerWirelessProtocol.ParseConnectionReply(wrongSize, 0x1F);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            if (primary) RazerWirelessProtocol.ParseConnectionReply(report, 0x1F);
            else RazerWirelessProtocol.ParsePrimaryDeviceReply(report, 0x1F);
        });
    }

    private static byte[] Complete(byte[] request, byte[] arguments)
    {
        request[1] = 2;
        arguments.CopyTo(request, 9);
        RazerProtocolTests.SetChecksum(request);
        return request;
    }

    private static byte[] Reply(byte[] arguments, bool legacy = false)
    {
        var report = RazerWirelessProtocol.CreateDeviceListRequest(legacy);
        report[1] = 2;
        report.AsSpan(9, 80).Clear();
        arguments.CopyTo(report, 9);
        RazerProtocolTests.SetChecksum(report);
        return report;
    }
}
