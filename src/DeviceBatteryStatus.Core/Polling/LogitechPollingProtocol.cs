namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Device-advertised HID++ report-rate fields, not measured USB timing.</summary>
public static class LogitechPollingProtocol
{
    public const ushort AdjustableReportRate = 0x8060;
    public const ushort ExtendedReportRate = 0x8061;

    public static IReadOnlyList<int> DecodeSupportedRates(ushort feature, ReadOnlySpan<byte> parameters)
    {
        if (feature == AdjustableReportRate)
        {
            if (parameters.IsEmpty) throw new InvalidDataException("Truncated HID++ report-rate capabilities.");
            int flags = parameters[0];
            return Array.AsReadOnly(Enumerable.Range(1, 8)
                .Where(period => (flags & (1 << (period - 1))) != 0 && 1000 % period == 0)
                .Select(period => 1000 / period).Order().ToArray());
        }
        if (feature == ExtendedReportRate)
        {
            if (parameters.Length < 2) throw new InvalidDataException("Truncated HID++ extended report-rate capabilities.");
            int flags = (parameters[0] << 8) | parameters[1];
            return Array.AsReadOnly(Enumerable.Range(0, 7).Where(index => (flags & (1 << index)) != 0)
                .Select(index => 125 << index).ToArray());
        }
        throw new NotSupportedException("Unknown HID++ report-rate feature.");
    }

    public static bool TryDecodeRate(ushort feature, byte value, out int rateHz)
    {
        rateHz = 0;
        if (feature == AdjustableReportRate && value is >= 1 and <= 8 && 1000 % value == 0)
            rateHz = 1000 / value;
        else if (feature == ExtendedReportRate && value <= 6) rateHz = 125 << value;
        return rateHz > 0;
    }

    public static bool TryEncodeRate(ushort feature, int rateHz, out byte value)
    {
        for (byte candidate = 0; candidate <= 8; candidate++)
            if (TryDecodeRate(feature, candidate, out int decoded) && decoded == rateHz)
            {
                value = candidate;
                return true;
            }
        value = 0;
        return false;
    }
}
