namespace DeviceBatteryStatus.Core.Models;

public enum DeviceVendor { Razer, Logitech, Alienware }
public enum ConnectionState { Unknown, Connected, Disconnected }

public sealed record PeripheralIdentity(string Id, string Name, DeviceVendor Vendor,
    string ReceiverId, string? ModelId = null);

public sealed record DeviceSnapshot(PeripheralIdentity Device, BatteryReading Battery,
    ConnectionState Connection = ConnectionState.Unknown);
