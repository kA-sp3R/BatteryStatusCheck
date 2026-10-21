using DeviceBatteryStatus.Core.Protocols;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerDeviceCatalogTests
{
    [Theory]
    [InlineData(0x0025, 0xFF, true)]
    [InlineData(0x003F, 0xFF, true)]
    [InlineData(0x0045, 0xFF, true)]
    [InlineData(0x005A, 0x3F, true)]
    [InlineData(0x0062, 0x1F, false)]
    [InlineData(0x006F, 0x1F, true)]
    [InlineData(0x0072, 0x3F, true)]
    [InlineData(0x0077, 0x1F, true)]
    [InlineData(0x0090, 0x1F, true)]
    [InlineData(0x0094, 0x1F, false)]
    [InlineData(0x009A, 0x1F, true)]
    [InlineData(0x00A8, 0x1F, true)]
    [InlineData(0x00B4, 0x1F, false)]
    [InlineData(0x00C8, 0x1F, true)]
    [InlineData(0x00CD, 0x1F, true)]
    [InlineData(0x00D1, 0x1F, true)]
    [InlineData(0x00D4, 0x1F, false)]
    [InlineData(0x00D7, 0x1F, true)]
    public void AdditionalReceiverProfilesSelectTheirDocumentedBatteryTransactionAndChargingSupport(
        ushort productId, byte transactionId, bool supportsCharging)
    {
        Assert.True(RazerDeviceCatalog.TryGet(productId, out var profile));
        Assert.Equal(transactionId, profile!.TransactionId);
        Assert.Equal(supportsCharging, profile.SupportsCharging);
        Assert.Equal(0, profile.InterfaceNumber);
        Assert.True(profile.IdentifiesPeripheralModel);
    }

    [Theory]
    [InlineData(0x0024)] // Mamba 2012 wired.
    [InlineData(0x003E)] // Naga Epic Chroma wired.
    [InlineData(0x0044)] // Mamba Chroma wired.
    [InlineData(0x0059)] // Lancehead wired.
    [InlineData(0x0070)] // Lancehead Wireless attached by USB cable.
    [InlineData(0x0073)] // Mamba Wireless attached by USB cable.
    [InlineData(0x0080)] // Pro Click wired.
    [InlineData(0x008F)] // Naga Pro wired.
    [InlineData(0x0095)] // Orochi V2 Bluetooth.
    [InlineData(0x00A7)] // Naga V2 Pro wired.
    [InlineData(0x00C7)] // Pro Click V2 Vertical wired.
    [InlineData(0x00CC)] // Basilisk V3 Pro 35K wired.
    [InlineData(0x00D0)] // Pro Click V2 wired.
    [InlineData(0x00D3)] // Basilisk Mobile wired.
    [InlineData(0x00D6)] // Basilisk V3 Pro 35K Phantom wired.
    [InlineData(0xFFFF)] // Unknown endpoint.
    public void WiredBluetoothAndUnknownCounterpartsAreNotPromotedToReceivers(ushort productId)
    {
        Assert.False(RazerDeviceCatalog.TryGet(productId, out var profile));
        Assert.Null(profile);
    }

    [Theory]
    [InlineData(0x0226)] // Huntsman Elite.
    [InlineData(0x0227)] // Huntsman.
    [InlineData(0x0243)] // Huntsman Tournament Edition.
    [InlineData(0x0257)] // Huntsman Mini.
    [InlineData(0x0266)] // Huntsman V2 Analog.
    [InlineData(0x0269)] // Huntsman Mini JP.
    [InlineData(0x026B)] // Huntsman V2 Tenkeyless.
    [InlineData(0x026C)] // Huntsman V2.
    [InlineData(0x0282)] // Huntsman Mini Analog.
    [InlineData(0x02A6)] // Huntsman V3 Pro.
    [InlineData(0x02A7)] // Huntsman V3 Pro TKL.
    [InlineData(0x02B0)] // Huntsman V3 Pro Mini.
    [InlineData(0x02CF)] // Huntsman V3 Pro 8KHz; high polling does not imply wireless.
    public void DocumentedUsbOnlyHuntsmanVariantsDoNotBecomeWirelessBatteryDevices(ushort productId) =>
        Assert.False(RazerDeviceCatalog.TryGet(productId, out _));

    [Theory]
    [InlineData(0x025C, 2)]
    [InlineData(0x0271, 3)]
    [InlineData(0x0290, 2)]
    [InlineData(0x0296, 2)]
    [InlineData(0x02BA, 3)]
    [InlineData(0x02D5, 2)]
    public void WirelessKeyboardProfilesRetainTheirControlInterface(ushort productId, byte interfaceNumber)
    {
        Assert.True(RazerDeviceCatalog.TryGet(productId, out var profile));
        Assert.Equal(0x9F, profile!.TransactionId);
        Assert.Equal(interfaceNumber, profile.InterfaceNumber);
    }

    [Fact]
    public void GenericHyperPollingProfileDoesNotInventAPairedMouseModel()
    {
        Assert.True(RazerDeviceCatalog.TryGet(0x00B3, out var profile));
        Assert.False(profile!.IdentifiesPeripheralModel);
        Assert.Contains("model unknown", profile.Name);
        Assert.Equal(0x1F, profile.TransactionId);
        Assert.Equal(0, profile.InterfaceNumber);
    }

    [Theory]
    [InlineData(0x00A4, 0)]
    [InlineData(0x00CF, 0)]
    [InlineData(0x00F1, 3)]
    public void AuditedMasterReceiversUseTheirOwnNamespaceAndControlInterfaceWithoutClaimingAPeripheralModel(
        ushort productId, byte controlInterface)
    {
        Assert.True(RazerDeviceCatalog.TryGet(productId, out var profile));
        Assert.False(profile!.IdentifiesPeripheralModel);
        Assert.Contains("paired model unknown", profile.Name);
        Assert.Equal(0xE1, profile.TransactionId);
        Assert.Equal(controlInterface, profile.InterfaceNumber);

        var endpoint = new HidEndpoint($"fake-usb-vid_1532&mi_{controlInterface:X2}", 0x1532, productId,
            0xFF00, 1, 0, 0, 91, "receiver-dock");
        var provider = new RazerBatteryProvider();
        Assert.True(provider.CanHandle(endpoint));
        Assert.False(provider.CanHandle(endpoint with
        {
            DevicePath = $"fake-usb-vid_1532&mi_{(controlInterface == 0 ? 3 : 0):X2}"
        }));

        var placeholder = new PeripheralIdentity($"{endpoint.ReceiverId}:razer:{productId:X4}", profile.Name,
            DeviceVendor.Razer, endpoint.ReceiverId, $"1532:{productId:X4}");
        Assert.False(new RazerPollingRateController().CanHandle(endpoint, placeholder));
    }
}
