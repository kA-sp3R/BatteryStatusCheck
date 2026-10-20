using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class AlienwareProtocolTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CapturedBatteryReplyDecodesSixtyFourPercentDischarging()
    {
        var reading = AlienwareProtocol.ParseBattery(AlienwareFixtures.CapturedDischarging(), SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(64, reading.Percentage);
        Assert.Equal(ChargingState.Discharging, reading.Charging);
        Assert.Equal(SampleTime, reading.SampledAt);
    }

    [Fact]
    public void CapturedChargingReplyPreservesOneHundredPercentAndChargingState()
    {
        var reading = AlienwareProtocol.ParseBattery(AlienwareFixtures.CapturedCharging(), SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(100, reading.Percentage);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void AValidSyntheticZeroIsAMeasurementRatherThanUnavailable()
    {
        var reading = AlienwareProtocol.ParseBattery(AlienwareFixtures.Battery(0), SampleTime);

        Assert.Equal(ReadingState.Current, reading.State);
        Assert.Equal(0, reading.Percentage);
        Assert.Equal(ChargingState.Discharging, reading.Charging);
    }

    [Fact]
    public void UnknownChargingByteDoesNotInventChargingOrDiscardTheMeasuredPercentage()
    {
        var reading = AlienwareProtocol.ParseBattery(AlienwareFixtures.Battery(64, 2), SampleTime);

        Assert.Equal(64, reading.Percentage);
        Assert.Equal(ChargingState.Unknown, reading.Charging);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(255)]
    public void OutOfRangePercentageIsRejectedRatherThanClamped(byte percentage) =>
        Assert.Throws<InvalidDataException>(() =>
            AlienwareProtocol.ParseBattery(AlienwareFixtures.Battery(percentage), SampleTime));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(14)]
    public void TruncatedBatteryReportCannotUseZeroPaddingAsAMeasurement(int length)
    {
        var truncated = AlienwareFixtures.CapturedDischarging()[..length];

        Assert.Throws<InvalidDataException>(() => AlienwareProtocol.ParseBattery(truncated, SampleTime));
    }

    [Theory]
    [InlineData(0, 0x08)] // Notification report, not a query response.
    [InlineData(1, 0x40)] // SET direction, not a GET reply.
    [InlineData(2, 0x09)] // Link response, not a battery response.
    [InlineData(3, 0x09)] // Declared payload too short.
    [InlineData(3, 0x0B)] // Unrecognized battery payload shape.
    [InlineData(3, 0xFF)] // Declared payload exceeds the report.
    [InlineData(4, 0x01)] // Unexpected reserved header byte.
    [InlineData(5, 0x01)] // Unrecognized battery payload prefix.
    public void MalformedOrUnrelatedBatteryReportIsRejected(int offset, byte value)
    {
        var report = AlienwareFixtures.CapturedDischarging();
        report[offset] = value;

        Assert.Throws<InvalidDataException>(() => AlienwareProtocol.ParseBattery(report, SampleTime));
    }

    [Theory]
    [InlineData(0xCC, ConnectionState.Connected)]
    [InlineData(0xDD, ConnectionState.Disconnected)]
    [InlineData(0x42, ConnectionState.Unknown)]
    public void OnlyTheDocumentedLinkStatesMeanConnectedOrDisconnected(byte value, ConnectionState expected) =>
        Assert.Equal(expected, AlienwareProtocol.ParseLink(AlienwareFixtures.Link(value)));

    [Fact]
    public void DongleIdentityReplyIdentifiesTheHeadsetFamilyRatherThanTheUsbTransport() =>
        Assert.Equal((ushort)0xA528,
            AlienwareProtocol.ParseIdentityProductId(AlienwareFixtures.Identity(0xA528)));

    [Fact]
    public void TruncatedIdentityCannotSupplyAProductIdFromPadding()
    {
        var report = AlienwareFixtures.Identity(0xA528)[..16];

        Assert.Throws<InvalidDataException>(() => AlienwareProtocol.ParseIdentityProductId(report));
    }

    [Fact]
    public void LinkStateRequiresTheDeclaredPayloadInsteadOfAnUnrelatedZeroFilledReport()
    {
        var report = AlienwareFixtures.Link(0xCC);
        report[3] = 0;

        Assert.Throws<InvalidDataException>(() => AlienwareProtocol.ParseLink(report));
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    public void BatteryAndLinkRequestsUseReadOnlyGetFrames(byte command)
    {
        var request = AlienwareProtocol.CreateRequest(command);

        Assert.Equal(62, request.Length);
        Assert.Equal(new byte[] { 0x06, 0xC0, command, 0, 0 }, request[..5]);
        Assert.All(request[5..], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void IdentityQueryCarriesTheExactCapturedSubcommandParameters()
    {
        var request = AlienwareProtocol.CreateRequest(0x00);

        Assert.Equal(62, request.Length);
        Assert.Equal(new byte[] { 0x06, 0xC0, 0x00, 0x04, 0x00, 0x00, 0xF5, 0x28, 0x00 }, request[..9]);
        Assert.All(request[9..], value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(0x15)]
    [InlineData(0x75)]
    [InlineData(0x8D)]
    [InlineData(0xFF)]
    public void BatteryProviderProtocolCannotConstructUnrelatedSettingsCommands(byte command) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AlienwareProtocol.CreateRequest(command));
}

internal static class AlienwareFixtures
{
    // Captured wire data documented by romain-ncls/awpro, MIT licensed,
    // commit d92f6df573b0a5b4c5551fcfc8d3311c435e7a88, src/protocol.rs.
    // These fixtures exercise decoding; they are not local hardware observations.
    internal static byte[] CapturedDischarging() => Padded(
        [0x07, 0xC0, 0x0A, 0x0A, 0x00, 0x00, 0x40, 0x00, 0x00, 0x04, 0x0F, 0x40, 0x00, 0x02, 0x03]);

    internal static byte[] CapturedCharging() => Padded(
        [0x07, 0xC0, 0x0A, 0x0A, 0x00, 0x00, 0x64, 0x01, 0x00, 0x36, 0x10, 0x64, 0x00, 0x02, 0x2F]);

    internal static byte[] Battery(byte percentage, byte charging = 0)
    {
        var report = CapturedDischarging();
        report[6] = percentage;
        report[7] = charging;
        report[11] = percentage;
        return report;
    }

    internal static byte[] Link(byte state) => Padded([0x07, 0xC0, 0x09, 0x01, 0x00, state]);

    internal static byte[] Identity(ushort productId) => Padded(
        [0x07, 0xC0, 0x00, 0x0C, 0x00, 0x04, 0x01, 0x01, 0x00,
            (byte)productId, (byte)(productId >> 8), 0xFF, 0x01, 0x03, 0, 0, 0]);

    private static byte[] Padded(byte[] prefix)
    {
        var report = new byte[63];
        prefix.CopyTo(report, 0);
        return report;
    }
}
