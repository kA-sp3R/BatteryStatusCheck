using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class LogitechHidppProtocolTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReplyFromAnotherPeripheralSlotCannotBeAttributedToTheRequestedDevice()
    {
        byte[] reply = [0x10, 2, 0x05, 0x0B, 0, 0, 0];

        Assert.False(LogitechHidppProtocol.TryParseReply(reply, 1, 0x05, 0x0B, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void ReplyToAnotherFeatureOrFunctionCannotSupplyABatteryValue()
    {
        byte[] reply = [0x10, 1, 0x05, 0x0B, 0, 0, 0];

        Assert.False(LogitechHidppProtocol.TryParseReply(reply, 1, 0x06, 0x0B, out _));
        Assert.False(LogitechHidppProtocol.TryParseReply(reply, 1, 0x05, 0x0C, out _));
    }

    [Fact]
    public void MatchingShortReplySuppliesOnlyItsDeclaredParameters()
    {
        byte[] report = [0x10, 1, 0x05, 0x0B, 0, 71, 0];

        Assert.True(LogitechHidppProtocol.TryParseReply(report, 1, 0x05, 0x0B, out var reply));
        Assert.Equal(new byte[] { 0, 71, 0 }, reply!.Parameters);
        Assert.Null(reply.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(19)]
    public void TruncatedLongReportIsRejected(int length)
    {
        var report = new byte[length];
        if (length > 0) report[0] = 0x11;
        if (length > 3) { report[1] = 1; report[2] = 0x05; report[3] = 0x0B; }

        Assert.False(LogitechHidppProtocol.TryParseReply(report, 1, 0x05, 0x0B, out _));
    }

    [Fact]
    public void ProtocolErrorReplyIsNotInterpretedAsBatteryParameters()
    {
        byte[] report = [0x10, 1, 0x8F, 0x81, 0x0D, 0x08, 0];

        Assert.True(LogitechHidppProtocol.TryParseReply(report, 1, 0x81, 0x0D, out var reply));
        Assert.True(reply!.LegacyError);
        Assert.Equal((byte)0x08, reply.ErrorCode);
        Assert.Empty(reply.Parameters);
    }

    [Fact]
    public void InUseZeroFromBatteryStatusRemainsAGenuineZero()
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([0, 0, 0], SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(0, reading.Percentage);
        Assert.Equal(ChargingState.Discharging, reading.Charging);
    }

    [Fact]
    public void ProtocolSpecificUnknownZeroWhileChargingDoesNotInventAPercentage()
    {
        var reading = LogitechHidppProtocol.ParseBattery1000([0, 0, 1], SampleTime);

        Assert.Equal(ReadingState.Unsupported, reading.State);
        Assert.Null(reading.Percentage);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void ACategoryOnlyDeviceDoesNotGetAnEstimatedPercentage()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([50, 4, 0, 0], false, SampleTime);

        Assert.Equal(ReadingState.Unsupported, reading.State);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void ExactUnifiedStateOfChargeZeroIsValidEvenWhileCharging()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([0, 1, 1, 1], true, SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(0, reading.Percentage);
        Assert.Equal(ChargingState.Charging, reading.Charging);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Fact]
    public void ACategoryOnlyDeviceCanReportChargingWithoutSupplyingAPercentage()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([0, 4, 1, 0], false, SampleTime);

        Assert.Equal(ReadingState.Unsupported, reading.State);
        Assert.Null(reading.Percentage);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void DeviceReportedChargingFaultHasNoBatteryPercentage()
    {
        var reading = LogitechHidppProtocol.ParseUnifiedBattery([62, 4, 4, 0], true, SampleTime);

        Assert.Equal(ReadingState.Failed, reading.State);
        Assert.Null(reading.Percentage);
    }

    [Fact]
    public void OutOfRangeBatteryValueIsRejectedRatherThanClamped()
    {
        Assert.Equal(ReadingState.Failed,
            LogitechHidppProtocol.ParseUnifiedBattery([255, 4, 0, 0], true, SampleTime).State);
        Assert.Equal(ReadingState.Failed,
            LogitechHidppProtocol.ParseBattery1000([101, 0, 0], SampleTime).State);
        Assert.Equal(ReadingState.Failed,
            LogitechHidppProtocol.ParseBattery0D([101, 0, 0x30], SampleTime).State);
    }

    [Fact]
    public void TruncatedBatteryPayloadCannotBecomeZeroPercent()
    {
        Assert.Null(LogitechHidppProtocol.ParseUnifiedBattery([0, 0, 0], true, SampleTime).Percentage);
        Assert.Null(LogitechHidppProtocol.ParseBattery1000([0, 0], SampleTime).Percentage);
        Assert.Null(LogitechHidppProtocol.ParseBattery0D([0], SampleTime).Percentage);
    }

    [Fact]
    public void LegacyPercentageRegisterPreservesZeroAndChargingState()
    {
        var reading = LogitechHidppProtocol.ParseBattery0D([0, 0, 0x30], SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(0, reading.Percentage);
        Assert.Equal(ChargingState.Discharging, reading.Charging);
    }
}
