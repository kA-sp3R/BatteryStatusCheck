namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Razer polling configuration reports, including Windows' report-ID prefix.</summary>
public static class RazerPollingProtocol
{
    public const int ReportLength = 91;
    public const byte ReadCommand = 0x85;
    public const byte WriteCommand = 0x05;
    public const byte HighRateReadCommand = 0xC0;
    public const byte HighRateWriteCommand = 0x40;

    public static byte[] CreateReadRequest(byte transactionId) => CreateRequest(transactionId, ReadCommand, 1, 0);

    public static byte[] CreateWriteRequest(byte transactionId, int rateHz) =>
        CreateRequest(transactionId, WriteCommand, 1, rateHz switch
        {
            125 => 0x08,
            500 => 0x02,
            1000 => 0x01,
            _ => throw new ArgumentOutOfRangeException(nameof(rateHz), "This protocol supports only 125, 500 and 1000 Hz.")
        });

    public static int ParseReadReply(ReadOnlySpan<byte> report, byte transactionId)
    {
        ValidateReply(report, transactionId, ReadCommand, 1, 1);
        return report[9] switch
        {
            0x08 => 125,
            0x02 => 500,
            0x01 => 1000,
            _ => throw new InvalidDataException("The Razer receiver returned an unknown ordinary polling-rate encoding.")
        };
    }

    public static void ValidateWriteReply(ReadOnlySpan<byte> report, byte transactionId) =>
        ValidateReply(report, transactionId, WriteCommand, 1, 1);

    public static byte[] CreateHighRateReadRequest(byte transactionId) =>
        CreateRequest(transactionId, HighRateReadCommand, 1, 0);

    public static byte[] CreateHighRateWriteRequest(byte transactionId, byte phase, int rateHz)
    {
        if (phase > 1) throw new ArgumentOutOfRangeException(nameof(phase));
        byte encoding = rateHz switch
        {
            125 => 0x40,
            500 => 0x10,
            1000 => 0x08,
            2000 => 0x04,
            4000 => 0x02,
            8000 => 0x01,
            _ => throw new ArgumentOutOfRangeException(nameof(rateHz), "This receiver profile does not document that polling rate.")
        };
        return CreateRequest(transactionId, HighRateWriteCommand, 2, phase, encoding);
    }

    public static int ParseHighRateReadReply(ReadOnlySpan<byte> report, byte transactionId)
    {
        // Upstream requests one byte but reads argument 1. Accept only that framing
        // and a two-byte payload, while validating the complete fixed-size report.
        ValidateReply(report, transactionId, HighRateReadCommand, 1, 2);
        return report[10] switch
        {
            0x40 => 125,
            0x10 => 500,
            0x08 => 1000,
            0x04 => 2000,
            0x02 => 4000,
            0x01 => 8000,
            _ => throw new InvalidDataException("The Razer receiver returned an unsupported high-rate polling encoding.")
        };
    }

    public static void ValidateHighRateWriteReply(ReadOnlySpan<byte> report, byte transactionId) =>
        ValidateReply(report, transactionId, HighRateWriteCommand, 2, 2);

    private static byte[] CreateRequest(byte transactionId, byte command, byte payloadLength,
        byte firstValue, byte secondValue = 0)
    {
        if (transactionId == 0) throw new ArgumentOutOfRangeException(nameof(transactionId));
        var report = new byte[ReportLength];
        report[2] = transactionId;
        report[6] = payloadLength;
        report[8] = command;
        report[9] = firstValue;
        report[10] = secondValue;
        report[89] = CalculateChecksum(report);
        return report;
    }

    private static void ValidateReply(ReadOnlySpan<byte> report, byte transactionId, byte command,
        byte minimumPayloadLength, byte maximumPayloadLength)
    {
        if (report.Length != ReportLength || report[0] != 0)
            throw new InvalidDataException("Unexpected Razer polling feature-report framing.");
        if (report[2] != transactionId || report[3] != 0 || report[4] != 0 ||
            report[5] != 0 || report[7] != 0 || report[8] != command || report[6] > 80)
            throw new InvalidDataException("Razer polling response does not match this request.");
        if (report[89] != CalculateChecksum(report))
            throw new InvalidDataException("Razer polling response checksum is invalid.");
        if (report[1] == 0x05)
            throw new NotSupportedException("The Razer receiver rejected this polling-rate command as unsupported.");
        if (report[1] == 0x04)
            throw new TimeoutException("The Razer receiver reported a polling-command timeout; radio connection is not established.");
        if (report[1] != 0x02)
            throw new IOException($"The Razer receiver did not acknowledge the polling command successfully (status {report[1]:X2}).");
        if (report[6] < minimumPayloadLength || report[6] > maximumPayloadLength)
            throw new InvalidDataException("Unexpected payload length in a Razer polling-rate response.");
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (var offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        return checksum;
    }
}
