using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerProtocolTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(128, 50)]
    [InlineData(255, 100)]
    public void ValidReplyPreservesActualMeasuredRange(byte rawValue, int expectedPercentage)
    {
        var reply = RazerProtocol.ParseReply(BatteryReply(rawValue), 0x1F, 0x80);

        Assert.Equal(RazerReplyStatus.Success, reply.Status);
        Assert.Equal(rawValue, reply.Value);
        Assert.Equal(expectedPercentage, RazerProtocol.ToPercentage(reply.Value!.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(92)]
    public void TruncatedOrOversizedReportsCannotBecomeMeasurements(int length) =>
        Assert.Throws<InvalidDataException>(() =>
            RazerProtocol.ParseReply(new byte[length], 0x1F, 0x80));

    [Theory]
    [InlineData(0, 1)] // Wrong Windows report ID.
    [InlineData(2, 0x3F)] // Reply from another transaction.
    [InlineData(3, 1)] // Unexpected remaining packets.
    [InlineData(5, 1)] // Unsupported protocol framing.
    [InlineData(6, 1)] // Battery payload must contain both argument bytes.
    [InlineData(7, 6)] // Reply to a different command class.
    [InlineData(8, 0x84)] // Charging reply is not a battery reply.
    public void AnUnrelatedReplyIsRejectedEvenWhenItsChecksumIsValid(int offset, byte value)
    {
        var report = BatteryReply(128);
        report[offset] = value;
        SetChecksum(report);

        Assert.Throws<InvalidDataException>(() => RazerProtocol.ParseReply(report, 0x1F, 0x80));
    }

    [Fact]
    public void CorruptPayloadIsRejected()
    {
        var report = BatteryReply(128);
        report[10] ^= 1;

        Assert.Throws<InvalidDataException>(() => RazerProtocol.ParseReply(report, 0x1F, 0x80));
    }

    [Theory]
    [InlineData(1, RazerReplyStatus.Busy)]
    [InlineData(3, RazerReplyStatus.Failed)]
    [InlineData(4, RazerReplyStatus.TimedOut)]
    [InlineData(5, RazerReplyStatus.Unsupported)]
    public void NonSuccessStatusNeverTurnsTheZeroFilledPayloadIntoZeroPercent(byte status, RazerReplyStatus expected)
    {
        var report = BatteryReply(0);
        report[1] = status;

        var reply = RazerProtocol.ParseReply(report, 0x1F, 0x80);

        Assert.Equal(expected, reply.Status);
        Assert.Null(reply.Value);
    }

    internal static byte[] BatteryReply(byte value)
    {
        var report = new byte[91];
        report[1] = 2;
        report[2] = 0x1F;
        report[6] = 2;
        report[7] = 7;
        report[8] = 0x80;
        report[10] = value;
        SetChecksum(report);
        return report;
    }

    internal static void SetChecksum(byte[] report)
    {
        byte checksum = 0;
        for (var offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        report[89] = checksum;
    }
}
