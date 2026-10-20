namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>
/// Separates implementation availability from Windows runtime and physical verification.
/// </summary>
public static class AlienwareSupport
{
    public const string DeviceName = "Alienware Pro Wireless Gaming Headset";
    public const bool BatteryMonitoringImplemented = true;
    public const bool WindowsTransportVerified = false;
    public const bool HardwareVerified = false;
    public const string Limitation =
        "Alienware Pro has a source-backed battery and radio-link reader for the 413C:A529 " +
        "2.4 GHz dongle. Windows application runtime and physical hardware compatibility " +
        "remain unverified; wired USB-C monitoring is excluded.";
}
