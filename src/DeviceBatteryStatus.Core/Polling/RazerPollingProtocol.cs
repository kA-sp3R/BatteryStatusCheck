namespace DeviceBatteryStatus.Core.Polling;

/// <summary>The ordinary Razer 125/500/1000 Hz protocol, including Windows' report-ID prefix.</summary>
public static class RazerPollingProtocol
{
    public const int ReportLength = 91;
    public const byte ReadCommand = 0x85;
    public const byte WriteCommand = 0x05;

    public static byte[] CreateReadRequest(byte transactionId) => CreateRequest(transactionId, ReadCommand, 0);

    public static byte[] CreateWriteRequest(byte transactionId, int rateHz) =>
        CreateRequest(transactionId, WriteCommand, rateHz switch
        {
            125 => 0x08,
            500 => 0x02,
            1000 => 0x01,
            _ => throw new ArgumentOutOfRangeException(nameof(rateHz), "This protocol supports only 125, 500 and 1000 Hz.")
        });

    public static int ParseReadReply(ReadOnlySpan<byte> report, byte transactionId)
    {
        ValidateReply(report, transactionId, ReadCommand);
        return report[9] switch
        {
            0x08 => 125,
            0x02 => 500,
            0x01 => 1000,
            _ => throw new InvalidDataException("The Razer receiver returned an unknown ordinary polling-rate encoding.")
        };
    }

    public static void ValidateWriteReply(ReadOnlySpan<byte> report, byte transactionId) =>
        ValidateReply(report, transactionId, WriteCommand);

    private static byte[] CreateRequest(byte transactionId, byte command, byte value)
    {
        if (transactionId == 0) throw new ArgumentOutOfRangeException(nameof(transactionId));
        var report = new byte[ReportLength];
        report[2] = transactionId;
        report[6] = 1;
        report[8] = command;
        report[9] = value;
        report[89] = CalculateChecksum(report);
        return report;
    }

    private static void ValidateReply(ReadOnlySpan<byte> report, byte transactionId, byte command)
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
        if (report[6] != 1)
            throw new InvalidDataException("Unexpected payload length in a Razer polling-rate response.");
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (var offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        return checksum;
    }
}
