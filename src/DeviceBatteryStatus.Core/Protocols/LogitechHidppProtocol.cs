using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>HID++ framing and device-reported battery fields; no voltage/level estimates.</summary>
public static class LogitechHidppProtocol
{
    public static byte[] CreateRequest(byte slot, byte feature, byte function,
        ReadOnlySpan<byte> parameters, bool longReport = false)
    {
        int capacity = longReport ? 16 : 3;
        if (parameters.Length > capacity) throw new ArgumentOutOfRangeException(nameof(parameters));
        byte[] report = new byte[longReport ? 20 : 7];
        report[0] = longReport ? (byte)0x11 : (byte)0x10;
        report[1] = slot; report[2] = feature; report[3] = function;
        parameters.CopyTo(report.AsSpan(4));
        return report;
    }

    public static bool TryParseReply(ReadOnlySpan<byte> report, byte slot, byte feature,
        byte function, out LogitechReply? reply)
    {
        reply = null;
        if (report.Length < 7 || (report[0] != 0x10 && report[0] != 0x11) ||
            (report[0] == 0x11 && report.Length < 20) || report[1] != slot) return false;
        if ((report[2] == 0x8F || report[2] == 0xFF) && report[3] == feature && report[4] == function)
        {
            reply = new LogitechReply([], report[5], report[2] == 0x8F);
            return true;
        }
        if (report[2] != feature || report[3] != function) return false;
        int end = report[0] == 0x10 ? 7 : 20;
        reply = new LogitechReply(report[4..end].ToArray());
        return true;
    }

    public static BatteryReading ParseBattery1000(ReadOnlySpan<byte> parameters, DateTimeOffset sampledAt)
    {
        if (parameters.Length < 3 || parameters[0] > 100 || parameters[2] > 7)
            return Invalid("Malformed HID++ battery-status reply.");
        if (parameters[2] >= 5) return Invalid("The device reports a battery/charging fault.");
        // This feature can report zero as 'unknown' while charging; an in-use zero is genuine.
        if (parameters[0] == 0 && parameters[2] != 0)
            return BatteryReading.Unavailable(ReadingState.Unsupported,
                "HID++ battery status does not provide a percentage in this charging state.",
                MapBatteryStatus(parameters[2]));
        return BatteryReading.Current(parameters[0], MapBatteryStatus(parameters[2]), sampledAt);
    }

    public static BatteryReading ParseUnifiedBattery(ReadOnlySpan<byte> parameters,
        bool supportsStateOfCharge, DateTimeOffset sampledAt)
    {
        if (parameters.Length < 4 || parameters[2] > 4)
            return Invalid("Malformed HID++ unified-battery reply.");
        if (parameters[2] == 4) return Invalid("The device reports a charging error.");
        ChargingState charging = parameters[2] switch
        {
            0 => ChargingState.Discharging,
            1 or 2 => ChargingState.Charging,
            3 => ChargingState.Full,
            _ => ChargingState.Unknown
        };
        if (!supportsStateOfCharge)
            return BatteryReading.Unavailable(ReadingState.Unsupported,
                "This device reports battery categories, not a percentage; no estimate is substituted.", charging);
        if (parameters[0] > 100) return Invalid("HID++ state of charge is outside 0–100%.");
        return BatteryReading.Current(parameters[0], charging, sampledAt);
    }

    public static BatteryReading ParseBattery0D(ReadOnlySpan<byte> parameters, DateTimeOffset sampledAt)
    {
        if (parameters.Length < 3 || parameters[0] > 100)
            return Invalid("Malformed HID++ 1.0 battery-charge reply.");
        ChargingState charging = (parameters[2] & 0xF0) switch
        {
            0x30 => ChargingState.Discharging,
            0x50 => ChargingState.Charging,
            0x90 => ChargingState.Full,
            _ => ChargingState.Unknown
        };
        return BatteryReading.Current(parameters[0], charging, sampledAt);
    }

    private static ChargingState MapBatteryStatus(byte status) => status switch
    {
        0 => ChargingState.Discharging,
        1 or 2 or 4 => ChargingState.Charging,
        3 => ChargingState.Full,
        _ => ChargingState.Unknown
    };

    private static BatteryReading Invalid(string detail) => BatteryReading.Unavailable(ReadingState.Failed, detail);
}

public sealed record LogitechReply(byte[] Parameters, byte? ErrorCode = null, bool LegacyError = false);
