using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>
/// Monitoring-only AW Pro commands documented by romain-ncls/awpro, revision d92f6df5.
/// See docs/research/alienware-awcc.md and THIRD_PARTY_NOTICES.md for provenance.
/// </summary>
public static class AlienwareProtocol
{
    public const ushort VendorId = 0x413C;
    public const ushort DongleProductId = 0xA529;
    public const ushort WiredProductId = 0xA528;
    public const ushort UsagePage = 0xFF13;
    public const int OutputReportLength = 62;
    // Upstream Linux receive-buffer size; Windows allocations use the endpoint's HID capabilities.
    public const int InputReportLength = 63;
    public const byte IdentityCommand = 0x00;
    public const byte LinkCommand = 0x09;
    public const byte BatteryCommand = 0x0A;

    /// <summary>Only documented GET commands are expressible; no settings or firmware writes.</summary>
    public static byte[] CreateRequest(byte command)
    {
        if (command is not (IdentityCommand or LinkCommand or BatteryCommand))
            throw new ArgumentOutOfRangeException(nameof(command), "Only monitoring GET commands are allowed.");

        var request = new byte[OutputReportLength];
        request[0] = 0x06;
        request[1] = 0xC0;
        request[2] = command;
        if (command == IdentityCommand)
        {
            request[3] = 4;
            request[5] = 0x00;
            request[6] = 0xF5;
            request[7] = 0x28;
            request[8] = 0x00;
        }
        return request;
    }

    public static ConnectionState ParseLink(ReadOnlySpan<byte> report)
    {
        ValidateReply(report, LinkCommand, 1);
        return report[5] switch
        {
            0xCC => ConnectionState.Connected,
            0xDD => ConnectionState.Disconnected,
            _ => ConnectionState.Unknown
        };
    }

    public static ushort ParseIdentityProductId(ReadOnlySpan<byte> report)
    {
        ValidateReply(report, IdentityCommand, 12);
        return (ushort)(report[9] | report[10] << 8);
    }

    public static BatteryReading ParseBattery(ReadOnlySpan<byte> report, DateTimeOffset sampledAt)
    {
        ValidateReply(report, BatteryCommand, 10);
        if (report[5] != 0)
            throw new InvalidDataException("Alienware battery reply has an unrecognized payload prefix.");
        if (report[6] > 100)
            throw new InvalidDataException("Alienware battery percentage is outside 0–100%.");
        var charging = report[7] switch
        {
            0 => ChargingState.Discharging,
            1 => ChargingState.Charging,
            _ => ChargingState.Unknown
        };
        return BatteryReading.Current(report[6], charging, sampledAt);
    }

    private static void ValidateReply(ReadOnlySpan<byte> report, byte command, byte payloadLength)
    {
        if (report.Length < 5 || report[0] != 0x07 || report[1] != 0xC0 || report[2] != command || report[4] != 0)
            throw new InvalidDataException("Alienware reply does not match the requested report and command.");
        if (report[3] != payloadLength || report.Length < 5 + payloadLength)
            throw new InvalidDataException("Alienware reply has an unrecognized or truncated payload length.");
    }
}
