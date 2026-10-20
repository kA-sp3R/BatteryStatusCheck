namespace DeviceBatteryStatus.Core.Hardware;

public sealed record HidEndpoint(string DevicePath, ushort VendorId, ushort ProductId,
    ushort UsagePage, ushort Usage, int InputReportLength, int OutputReportLength,
    int FeatureReportLength, string ReceiverId, string? ProductName = null,
    string? SerialNumber = null);
