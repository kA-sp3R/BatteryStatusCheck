using DeviceBatteryStatus.Core.Polling;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerPollingProtocolTests
{
    [Theory]
    [InlineData(0x08, 125)]
    [InlineData(0x02, 500)]
    [InlineData(0x01, 1000)]
    public void OrdinaryReadDecodesOnlyDocumentedRates(byte encoding, int expected)
    {
        var reply = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), encoding);
        Assert.Equal(expected, RazerPollingProtocol.ParseReadReply(reply, 0x1F));
    }

    [Theory]
    [InlineData(125, 0x08)]
    [InlineData(500, 0x02)]
    [InlineData(1000, 0x01)]
    public void OrdinaryWriteUsesExactRateEncodingWithoutRounding(int rate, byte encoding)
    {
        var request = RazerPollingProtocol.CreateWriteRequest(0x3F, rate);
        Assert.Equal(91, request.Length);
        Assert.Equal(0, request[0]);
        Assert.Equal(0x3F, request[2]);
        Assert.Equal(1, request[6]);
        Assert.Equal(0, request[7]);
        Assert.Equal(0x05, request[8]);
        Assert.Equal(encoding, request[9]);
        Assert.Equal(Checksum(request), request[89]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(2000)]
    [InlineData(8000)]
    public void OrdinaryWriteRejectsUnsupportedRates(int rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerPollingProtocol.CreateWriteRequest(0x1F, rate));

    [Theory]
    [InlineData(0)]
    [InlineData(0x04)]
    [InlineData(0x20)]
    [InlineData(0x40)]
    public void UnknownOrHighRateEncodingIsNotGuessed(byte encoding)
    {
        var reply = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), encoding);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseReadReply(reply, 0x1F));
    }

    [Theory]
    [InlineData(0)] // Report ID.
    [InlineData(2)] // Transaction.
    [InlineData(3)] // Packet count.
    [InlineData(5)] // Protocol type.
    [InlineData(6)] // Payload size.
    [InlineData(7)] // Command class.
    [InlineData(8)] // Command.
    public void ReadRejectsMismatchedFramingEvenWithValidChecksum(int offset)
    {
        var reply = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), 1);
        reply[offset] ^= 1;
        reply[89] = Checksum(reply);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseReadReply(reply, 0x1F));
    }

    [Fact]
    public void ReadRejectsCorruptedChecksumAndTruncatedReply()
    {
        var reply = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), 1);
        reply[89] ^= 1;
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseReadReply(reply, 0x1F));
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseReadReply(reply[..90], 0x1F));
    }

    [Fact]
    public void AcknowledgementMustMatchTheWriteRatherThanAStaleGetReply()
    {
        var staleGet = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), 1);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ValidateWriteReply(staleGet, 0x1F));
    }

    [Fact]
    public void UnsupportedCommandAndTimeoutRemainDistinct()
    {
        var unsupported = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), 1, 5);
        var timeout = Reply(RazerPollingProtocol.CreateReadRequest(0x1F), 1, 4);
        Assert.Throws<NotSupportedException>(() => RazerPollingProtocol.ParseReadReply(unsupported, 0x1F));
        Assert.Throws<TimeoutException>(() => RazerPollingProtocol.ParseReadReply(timeout, 0x1F));
    }

    internal static byte[] Reply(byte[] request, byte encoding, byte status = 2)
    {
        var reply = request.ToArray();
        reply[1] = status;
        reply[9] = encoding;
        reply[89] = Checksum(reply);
        return reply;
    }

    private static byte Checksum(byte[] report)
    {
        byte checksum = 0;
        for (var offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        return checksum;
    }
}
