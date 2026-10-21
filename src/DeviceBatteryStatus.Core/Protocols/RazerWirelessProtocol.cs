using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

public sealed record RazerWirelessDevice(ushort ProductId, ConnectionState Connection);
public sealed record RazerWirelessReply(RazerReplyStatus Status, IReadOnlyList<RazerWirelessDevice> Devices);
public sealed record RazerPrimaryDeviceReply(RazerReplyStatus Status, byte? DeviceType, ushort? ProductId);
public sealed record RazerConnectionReply(RazerReplyStatus Status, ConnectionState Connection);

/// <summary>Read-only receiver association reports; a receiver's presence never establishes a radio link.</summary>
public static class RazerWirelessProtocol
{
    // Protocol facts independently checked against secbird 95dd41f7 (00:BF/80, tx1F)
    // and ginzberg/razer-hyperpolling-captures 04262218 (00:BF/49, txFF, argument10).
    public static byte[] CreateDeviceListRequest(bool legacy = false, byte? transactionId = null)
    {
        var report = CreateRequest(transactionId ?? (legacy ? (byte)0xFF : (byte)0x1F),
            0xBF, legacy ? (byte)0x31 : (byte)0x50);
        if (legacy) report[9] = 0x10;
        report[89] = Checksum(report);
        return report;
    }

    public static RazerWirelessReply ParseDeviceListReply(ReadOnlySpan<byte> report, bool legacy = false,
        byte? transactionId = null)
    {
        var status = ParseStatus(report, transactionId ?? (legacy ? (byte)0xFF : (byte)0x1F),
            0xBF, legacy ? (byte)0x31 : (byte)0x50);
        if (status != RazerReplyStatus.Success) return new(status, Array.Empty<RazerWirelessDevice>());

        int count = report[9];
        if (1 + count * 3 > report[6])
            throw new InvalidDataException("Truncated Razer wireless-list entries.");
        List<RazerWirelessDevice> devices = [];
        HashSet<ushort> productIds = [];
        for (int index = 0; index < count; index++)
        {
            int offset = 10 + index * 3;
            byte connected = report[offset];
            ushort productId = (ushort)((report[offset + 1] << 8) | report[offset + 2]);
            if (connected > 1)
                throw new InvalidDataException("Unknown Razer wireless connection flag.");
            if (productId == 0xFFFF && connected == 0) continue; // Explicit unoccupied record.
            if (productId is 0 or 0xFFFF || !productIds.Add(productId))
                throw new InvalidDataException("Invalid or ambiguous Razer wireless device identity.");
            devices.Add(new(productId, connected == 1 ? ConnectionState.Connected : ConnectionState.Disconnected));
        }
        return new(status, devices.AsReadOnly());
    }

    // Primary manufacturer evidence, downloaded but never executed:
    // apps.razer.com/synapse/products/179/mw/6120.ef5d145fafdfd0069d53.js,
    // SHA256 1bc185f4b6407b0c5aab6939b827a9f3ccba88e21966c222607bc0e92742beb8.
    // 00:C5 returns type (Mouse=1, Keyboard=2) and a big-endian product ID.
    public static byte[] CreatePrimaryDeviceRequest(byte transactionId) => CreateRequest(transactionId, 0xC5, 3);

    public static RazerPrimaryDeviceReply ParsePrimaryDeviceReply(ReadOnlySpan<byte> report, byte transactionId)
    {
        var status = ParseStatus(report, transactionId, 0xC5, 3);
        if (status != RazerReplyStatus.Success) return new(status, null, null);
        byte type = report[9];
        ushort productId = (ushort)((report[10] << 8) | report[11]);
        if (type is 0 or > 7 || productId is 0 or 0xFFFF)
            throw new InvalidDataException("Razer primary-device identity is invalid.");
        return new(status, type, productId);
    }

    // The same manufacturer middleware defines 00:B8 as connected=data[0],
    // and its consumer recognizes exactly 1 as connected. Unknown bytes stay unknown.
    public static byte[] CreateConnectionRequest(byte transactionId) => CreateRequest(transactionId, 0xB8, 1);

    public static RazerConnectionReply ParseConnectionReply(ReadOnlySpan<byte> report, byte transactionId)
    {
        var status = ParseStatus(report, transactionId, 0xB8, 1);
        return new(status, status != RazerReplyStatus.Success ? ConnectionState.Unknown : report[9] switch
        {
            0 => ConnectionState.Disconnected,
            1 => ConnectionState.Connected,
            _ => ConnectionState.Unknown
        });
    }

    private static byte[] CreateRequest(byte transactionId, byte command, byte payloadLength)
    {
        var report = new byte[RazerProtocol.ReportLength];
        report[2] = transactionId;
        report[6] = payloadLength;
        report[8] = command;
        report[89] = Checksum(report);
        return report;
    }

    private static RazerReplyStatus ParseStatus(ReadOnlySpan<byte> report, byte transactionId,
        byte command, byte payloadLength)
    {
        if (report.Length != RazerProtocol.ReportLength || report[0] != 0 || report[90] != 0)
            throw new InvalidDataException("Unexpected Razer wireless report framing.");
        if (report[2] != transactionId || report[3] != 0 || report[4] != 0 || report[5] != 0 ||
            report[7] != 0 || report[8] != command || report[6] > 80)
            throw new InvalidDataException("Razer response does not match the wireless request.");
        if (report[89] != Checksum(report))
            throw new InvalidDataException("Razer wireless checksum is invalid.");
        var status = report[1] switch
        {
            1 => RazerReplyStatus.Busy,
            2 => RazerReplyStatus.Success,
            3 => RazerReplyStatus.Failed,
            4 => RazerReplyStatus.TimedOut,
            5 => RazerReplyStatus.Unsupported,
            _ => throw new InvalidDataException("Unknown Razer wireless status.")
        };
        if (status == RazerReplyStatus.Success && report[6] != payloadLength)
            throw new InvalidDataException("Unexpected Razer wireless payload length.");
        return status;
    }

    private static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (int offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        return checksum;
    }
}
