namespace DeviceBatteryStatus.Core.Protocols;

public enum RazerReplyStatus { Busy, Success, Failed, TimedOut, Unsupported }

public sealed record RazerReply(RazerReplyStatus Status, byte? Value);

/// <summary>Razer's 90-byte control message, prefixed by the Windows HID report ID.</summary>
public static class RazerProtocol
{
    public const int ReportLength = 91;
    public const byte BatteryCommand = 0x80;
    public const byte ChargingCommand = 0x84;

    public static byte[] CreateRequest(byte transactionId, byte command)
    {
        if (command is not (BatteryCommand or ChargingCommand))
            throw new ArgumentOutOfRangeException(nameof(command));
        if (transactionId == 0) throw new ArgumentOutOfRangeException(nameof(transactionId));

        var report = new byte[ReportLength];
        report[2] = transactionId;
        report[6] = 2;
        report[7] = 7;
        report[8] = command;
        report[89] = CalculateChecksum(report);
        return report;
    }

    public static RazerReply ParseReply(ReadOnlySpan<byte> report, byte transactionId, byte command)
    {
        if (report.Length != ReportLength || report[0] != 0)
            throw new InvalidDataException("Unexpected Razer feature-report framing.");
        if (report[2] != transactionId || report[3] != 0 || report[4] != 0 ||
            report[5] != 0 || report[7] != 7 || report[8] != command || report[6] > 80)
            throw new InvalidDataException("Razer response does not match the battery request.");
        if (report[89] != CalculateChecksum(report))
            throw new InvalidDataException("Razer response checksum is invalid.");

        var status = report[1] switch
        {
            1 => RazerReplyStatus.Busy,
            2 => RazerReplyStatus.Success,
            3 => RazerReplyStatus.Failed,
            4 => RazerReplyStatus.TimedOut,
            5 => RazerReplyStatus.Unsupported,
            _ => throw new InvalidDataException("Unknown Razer command status.")
        };
        if (status == RazerReplyStatus.Success && (report[6] != 2 || report[9] != 0))
            throw new InvalidDataException("Razer battery response has an unexpected payload length.");

        return new(status, status == RazerReplyStatus.Success ? report[10] : null);
    }

    public static int ToPercentage(byte rawValue) =>
        (int)Math.Round(rawValue * 100.0 / 255.0, MidpointRounding.AwayFromZero);

    private static byte CalculateChecksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (var index = 3; index <= 88; index++) checksum ^= report[index];
        return checksum;
    }
}
