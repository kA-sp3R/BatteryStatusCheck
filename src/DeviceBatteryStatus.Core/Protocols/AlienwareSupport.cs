namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>
/// Capability information only. No verified Pro headset receiver identity or battery
/// protocol is available, so this class deliberately performs no device matching or I/O.
/// </summary>
public static class AlienwareSupport
{
    public const string DeviceName = "Alienware Pro Wireless Gaming Headset";
    public const bool BatteryMonitoringImplemented = false;
    public const string Limitation =
        "Unsupported: the Alienware Pro headset's 2.4 GHz receiver identity and battery protocol " +
        "have not been verified. No battery percentage, charging state, or peripheral " +
        "connection state can be reported.";
}
