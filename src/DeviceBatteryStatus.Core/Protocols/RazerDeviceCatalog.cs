namespace DeviceBatteryStatus.Core.Protocols;

public sealed record RazerDeviceProfile(ushort ProductId, string Name, byte TransactionId,
    bool SupportsCharging = true, byte InterfaceNumber = 0, bool IdentifiesPeripheralModel = true);

/// <summary>Documented USB 2.4 GHz receiver paths, never wired or Bluetooth counterparts.</summary>
public static class RazerDeviceCatalog
{
    // Protocol facts: OpenRazer 477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d,
    // mouse.py/keyboards.py and charge_level/charge_status in the corresponding drivers.
    // Dock receiver identities/interfaces: official Synapse product MW 164, 207 and 241.
    // Independently maintained data; no upstream driver code is included.
    private static readonly IReadOnlyList<RazerDeviceProfile> Profiles = Array.AsReadOnly(new[]
    {
        new RazerDeviceProfile(0x0025, "Razer Mamba 2012", 0xFF),
        new RazerDeviceProfile(0x003F, "Razer Naga Epic Chroma", 0xFF),
        new RazerDeviceProfile(0x0045, "Razer Mamba Chroma", 0xFF),
        new RazerDeviceProfile(0x005A, "Razer Lancehead", 0x3F),
        new RazerDeviceProfile(0x0062, "Razer Atheris", 0x1F, false),
        new RazerDeviceProfile(0x006F, "Razer Lancehead Wireless", 0x1F),
        new RazerDeviceProfile(0x0072, "Razer Mamba Wireless", 0x3F),
        new RazerDeviceProfile(0x0077, "Razer Pro Click", 0x1F),
        new RazerDeviceProfile(0x007B, "Razer Viper Ultimate", 0xFF),
        new RazerDeviceProfile(0x007D, "Razer DeathAdder V2 Pro", 0x3F),
        new RazerDeviceProfile(0x0083, "Razer Basilisk X HyperSpeed", 0xFF, false),
        new RazerDeviceProfile(0x0088, "Razer Basilisk Ultimate", 0x1F),
        new RazerDeviceProfile(0x0090, "Razer Naga Pro", 0x1F),
        new RazerDeviceProfile(0x0094, "Razer Orochi V2", 0x1F, false),
        new RazerDeviceProfile(0x009A, "Razer Pro Click Mini", 0x1F),
        new RazerDeviceProfile(0x009C, "Razer DeathAdder V2 X HyperSpeed", 0x1F, false),
        new RazerDeviceProfile(0x009F, "Razer Viper Mini Signature Edition", 0x1F),
        new RazerDeviceProfile(0x00A4, "Razer Mouse Dock Pro wireless receiver (paired model unknown)", 0xE1,
            IdentifiesPeripheralModel: false),
        new RazerDeviceProfile(0x00A6, "Razer Viper V2 Pro", 0x1F),
        new RazerDeviceProfile(0x00A8, "Razer Naga V2 Pro", 0x1F),
        new RazerDeviceProfile(0x00AB, "Razer Basilisk V3 Pro", 0x1F),
        new RazerDeviceProfile(0x00B0, "Razer Cobra Pro", 0x1F),
        new RazerDeviceProfile(0x00B3, "Razer HyperPolling paired mouse (model unknown)", 0x1F,
            IdentifiesPeripheralModel: false),
        new RazerDeviceProfile(0x00B4, "Razer Naga V2 HyperSpeed", 0x1F, false),
        new RazerDeviceProfile(0x00B7, "Razer DeathAdder V3 Pro", 0x1F),
        new RazerDeviceProfile(0x00B8, "Razer Viper V3 HyperSpeed", 0x1F, false),
        new RazerDeviceProfile(0x00B9, "Razer Basilisk V3 X HyperSpeed", 0x1F, false),
        new RazerDeviceProfile(0x00BF, "Razer DeathAdder V4 Pro", 0x1F),
        new RazerDeviceProfile(0x00C1, "Razer Viper V3 Pro", 0x1F),
        new RazerDeviceProfile(0x00C3, "Razer DeathAdder V3 Pro", 0x1F),
        new RazerDeviceProfile(0x00C5, "Razer DeathAdder V3 HyperSpeed", 0x1F),
        new RazerDeviceProfile(0x00C8, "Razer Pro Click V2 Vertical Edition", 0x1F),
        new RazerDeviceProfile(0x00CD, "Razer Basilisk V3 Pro 35K", 0x1F),
        new RazerDeviceProfile(0x00CF, "Razer HyperFlux V2 wireless receiver (paired model unknown)", 0xE1,
            IdentifiesPeripheralModel: false),
        new RazerDeviceProfile(0x00D1, "Razer Pro Click V2", 0x1F),
        new RazerDeviceProfile(0x00D4, "Razer Basilisk Mobile", 0x1F, false),
        new RazerDeviceProfile(0x00D7, "Razer Basilisk V3 Pro 35K Phantom Green Edition", 0x1F),
        new RazerDeviceProfile(0x00DB, "Razer Cobra HyperSpeed", 0x1F),
        new RazerDeviceProfile(0x00DF, "Razer Viper V3 Pro SE", 0x1F),
        new RazerDeviceProfile(0x00F1, "Razer Mouse Dock V2 Pro wireless receiver (paired model unknown)", 0xE1,
            InterfaceNumber: 3, IdentifiesPeripheralModel: false),
        new RazerDeviceProfile(0x025C, "Razer BlackWidow V3 Pro", 0x9F, InterfaceNumber: 2),
        new RazerDeviceProfile(0x0271, "Razer BlackWidow V3 Mini HyperSpeed", 0x9F, InterfaceNumber: 3),
        new RazerDeviceProfile(0x0290, "Razer DeathStalker V2 Pro", 0x9F, InterfaceNumber: 2),
        new RazerDeviceProfile(0x0296, "Razer DeathStalker V2 Pro TKL", 0x9F, InterfaceNumber: 2),
        new RazerDeviceProfile(0x02BA, "Razer BlackWidow V4 Mini HyperSpeed", 0x9F, InterfaceNumber: 3),
        new RazerDeviceProfile(0x02D5, "Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x9F, InterfaceNumber: 2)
    });

    private static readonly IReadOnlyDictionary<ushort, RazerDeviceProfile> ByProductId =
        Profiles.ToDictionary(profile => profile.ProductId);

    public static IReadOnlyList<RazerDeviceProfile> All => Profiles;

    public static bool TryGet(ushort productId, out RazerDeviceProfile? profile) =>
        ByProductId.TryGetValue(productId, out profile);
}
