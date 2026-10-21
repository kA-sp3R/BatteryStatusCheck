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

    public static BatteryReading ParseBattery1000(ReadOnlySpan<byte> parameters, DateTimeOffset sampledAt,
        bool supportsMileage = true)
    {
        if (parameters.Length < 3 || parameters[0] > 100 || parameters[2] > 7)
            return Invalid("Malformed HID++ battery-status reply.");
        if (parameters[2] >= 5) return Invalid("The device reports a battery/charging fault.");
        // The 0x1000 specification defines zero as unknown, including when the
        // device advertises mileage. This sentinel is specific to this feature.
        if (parameters[0] == 0)
            return BatteryReading.CurrentCategory(parameters[2] == 3 ? BatteryCategory.Full : BatteryCategory.Unknown,
                MapBatteryStatus(parameters[2]), sampledAt,
                parameters[2] == 3
                    ? "HID++ 0x1000 reports charge complete; the Full category does not imply a reported 100%."
                    : "HID++ 0x1000 battery level 0 means unknown; it is not a reported 0%.");
        if (!supportsMileage)
        {
            // HID++ 2.0's normative coarse-level ranges apply when mileage is
            // disabled or resolution is below ten levels. Do not use a battery
            // curve, or the Linux/Windows compatibility exception at level 30.
            BatteryCategory category = parameters[0] switch
            {
                <= 10 => BatteryCategory.Critical,
                <= 30 => BatteryCategory.Low,
                <= 80 => BatteryCategory.Good,
                _ => BatteryCategory.Full
            };
            return BatteryReading.CurrentCategory(category, MapBatteryStatus(parameters[2]), sampledAt,
                $"HID++ 0x1000 device-reported category (protocol level {parameters[0]}); no percentage is available.");
        }
        return BatteryReading.Current(parameters[0], MapBatteryStatus(parameters[2]), sampledAt);
    }

    public static BatteryReading ParseUnifiedBattery(ReadOnlySpan<byte> parameters,
        bool supportsStateOfCharge, DateTimeOffset sampledAt, byte supportedCategories = 0x0F)
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
        {
            // 0x1004 defines these category bits independently from the percentage
            // field. Respect the category mask advertised by the same device.
            int categories = parameters[1] & supportedCategories & 0x0F;
            BatteryCategory category = categories switch
            {
                _ when (categories & 0x08) != 0 => BatteryCategory.Full,
                _ when (categories & 0x04) != 0 => BatteryCategory.Good,
                _ when (categories & 0x02) != 0 => BatteryCategory.Low,
                _ when (categories & 0x01) != 0 => BatteryCategory.Critical,
                _ => BatteryCategory.Unknown
            };
            return BatteryReading.CurrentCategory(category, charging, sampledAt,
                "HID++ 0x1004 device-reported battery category; no percentage is available.");
        }
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

    public static BatteryReading ParseBattery07(ReadOnlySpan<byte> parameters, DateTimeOffset sampledAt)
    {
        if (parameters.Length < 2 || parameters[0] > 7)
            return Invalid("Malformed HID++ 1.0 battery-status reply.");
        if (parameters[1] == 0x23) return Invalid("The device reports a charging error.");
        ChargingState charging = parameters[1] switch
        {
            0x00 => ChargingState.Discharging,
            0x21 or 0x24 or 0x25 => ChargingState.Charging,
            0x22 or 0x26 => ChargingState.Full,
            _ => ChargingState.Unknown
        };
        BatteryCategory category = parameters[0] switch
        {
            1 or 2 => BatteryCategory.Critical,
            3 or 4 => BatteryCategory.Low,
            5 or 6 => BatteryCategory.Good,
            7 => BatteryCategory.High,
            _ => BatteryCategory.Unknown
        };
        return BatteryReading.CurrentCategory(category, charging, sampledAt,
            "HID++ 1.0 register 0x07 device-reported battery category; no percentage is available.");
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
