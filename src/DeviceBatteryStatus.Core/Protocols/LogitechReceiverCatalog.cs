namespace DeviceBatteryStatus.Core.Protocols;

internal enum LogitechReceiverKind { Unifying, Lightspeed, Bolt, Nano }

internal sealed record LogitechReceiverProfile(LogitechReceiverKind Kind, int DefaultSlots);

/// <summary>Explicit USB 2.4 GHz receiver IDs, independently described from Solaar's receiver table.</summary>
internal static class LogitechReceiverCatalog
{
    private static readonly IReadOnlyDictionary<ushort, LogitechReceiverProfile> Profiles = Build();

    public static bool TryGet(ushort productId, out LogitechReceiverProfile? profile) =>
        Profiles.TryGetValue(productId, out profile);

    private static IReadOnlyDictionary<ushort, LogitechReceiverProfile> Build()
    {
        Dictionary<ushort, LogitechReceiverProfile> profiles = [];
        foreach (ushort id in new ushort[] { 0xC52B, 0xC532 })
            profiles[id] = new(LogitechReceiverKind.Unifying, 6);
        foreach (ushort id in new ushort[] { 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D })
            profiles[id] = new(LogitechReceiverKind.Lightspeed, 1);
        foreach (ushort id in new ushort[] { 0xC518, 0xC51A, 0xC51B, 0xC521, 0xC525, 0xC526,
            0xC52E, 0xC52F, 0xC531, 0xC534, 0xC535, 0xC537 })
            profiles[id] = new(LogitechReceiverKind.Nano, id == 0xC534 ? 2 : 1);
        profiles[0xC548] = new(LogitechReceiverKind.Bolt, 6);
        return profiles;
    }
}
