using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class LogitechReceiverExpansionTests
{
    private static readonly DateTimeOffset SampledAt = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0xC518)]
    [InlineData(0xC51A)]
    [InlineData(0xC51B)]
    [InlineData(0xC521)]
    [InlineData(0xC525)]
    [InlineData(0xC526)]
    [InlineData(0xC52E)]
    [InlineData(0xC52F)]
    [InlineData(0xC531)]
    [InlineData(0xC534)]
    [InlineData(0xC535)]
    [InlineData(0xC537)]
    [InlineData(0xC548)]
    public void ExplicitDocumentedBoltAndNanoReceiversAreAdmitted(ushort productId)
    {
        var provider = new LogitechBatteryProvider();
        Assert.True(provider.CanHandle(Endpoint(productId)));
        Assert.False(provider.CanHandle(Endpoint(productId) with { VendorId = 0x1234 }));
        Assert.False(provider.CanHandle(Endpoint(productId) with { UsagePage = 0x0001 }));
    }

    [Theory]
    [InlineData(0xC517)] // 27 MHz, outside the project's 2.4 GHz scope.
    [InlineData(0xC542)] // Source documents that this receiver has no HID++ protocol.
    [InlineData(0xC08B)] // Direct/wired endpoint, not a supported receiver.
    [InlineData(0xFFFF)]
    public void UnrelatedOrNonHidppReceiversAreNotAdmittedByVendorAlone(ushort productId) =>
        Assert.False(new LogitechBatteryProvider().CanHandle(Endpoint(productId)));

    [Fact]
    public async Task BoltUsesPerSlotPairingSerialAndLittleEndianWirelessIdWithoutLegacySerialRegisters()
    {
        var endpoint = Endpoint(0xC548);
        await using var transport = new ExpansionTransport(endpoint, bolt: true, populatedSlots: [1, 2]);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Device.Id == "receiver-expanded/logitech/1/4081/12345601" && d.Battery.Percentage == 0);
        Assert.Contains(devices, d => d.Device.Id == "receiver-expanded/logitech/2/4081/12345602" && d.Battery.Percentage == 63);
        Assert.All(devices, d => Assert.Equal(ConnectionState.Connected, d.Connection));
        Assert.All(devices, d => Assert.Equal(SampledAt, d.Battery.SampledAt));
        Assert.True(Assert.IsType<ReceiverSnapshotBatch>(devices).AssociationComplete);
        Assert.Contains(transport.Requests, r => r[1] == 0xFF && r[4] == 0x56);
        Assert.DoesNotContain(transport.Requests, r => r[1] == 0xFF && r[4] is 0x03 or 0x20 or 0x30);
    }

    [Fact]
    public async Task AnEmptyBoltPairingTableDefinitivelyReportsNoPairedChildren()
    {
        var endpoint = Endpoint(0xC548);
        await using var transport = new ExpansionTransport(endpoint, bolt: true, populatedSlots: []);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Empty(devices);
        Assert.True(Assert.IsType<ReceiverSnapshotBatch>(devices).AssociationComplete);
        Assert.Equal(6, transport.Requests.Count);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x03)]
    public async Task UnsupportedPairingAddressesCannotProveAllChildrenWereRemoved(byte error)
    {
        var endpoint = Endpoint(0xC548);
        await using var transport = new ExpansionTransport(endpoint, bolt: true, populatedSlots: [], emptyPairingError: error);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Empty(devices);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(devices).AssociationComplete);
    }

    [Fact]
    public async Task BoltPairingFailureCannotClaimItsChildAssociationSweepWasComplete()
    {
        var endpoint = Endpoint(0xC548);
        await using var transport = new ExpansionTransport(endpoint, bolt: true, populatedSlots: [1], timeoutPairingSlot: 2);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Single(devices);
        Assert.False(devices is ReceiverSnapshotBatch { AssociationComplete: true });
        Assert.DoesNotContain(transport.Requests, r => r[1] == 0xFF && r[4] == 0x53);
    }

    [Fact]
    public async Task NanoPerSlotMetadataSeparatesTwoPeripheralsOnOneReceiver()
    {
        var endpoint = Endpoint(0xC534);
        await using var transport = new ExpansionTransport(endpoint, populatedSlots: [1, 2]);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Equal(2, devices.Count);
        Assert.Equal(2, devices.Select(d => d.Device.Id).Distinct().Count());
        Assert.Contains(devices, d => d.Device.Id.EndsWith("/1/4081/12345601", StringComparison.Ordinal));
        Assert.Contains(devices, d => d.Device.Id.EndsWith("/2/4081/12345602", StringComparison.Ordinal));
        Assert.True(Assert.IsType<ReceiverSnapshotBatch>(devices).AssociationComplete);
    }

    [Fact]
    public async Task LegacyNanoPerSlotIdentityAllowsGenuinePercentageReadsIncludingZero()
    {
        var endpoint = Endpoint(0xC534);
        await using var transport = new ExpansionTransport(endpoint, populatedSlots: [1, 2], legacy: true);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Device.Id.EndsWith("/1/4081/12345601", StringComparison.Ordinal) && d.Battery.Percentage == 0);
        Assert.Contains(devices, d => d.Device.Id.EndsWith("/2/4081/12345602", StringComparison.Ordinal) && d.Battery.Percentage == 63);
        Assert.Equal(2, transport.Requests.Count(r => r[2] == 0x81 && r[3] == 0x0D));
    }

    [Fact]
    public async Task NanoWithoutPairingMetadataUsesFreshSlotSpecificUnitIdentities()
    {
        var endpoint = Endpoint(0xC534);
        await using var transport = new ExpansionTransport(endpoint, populatedSlots: [1, 2],
            pairingMetadata: false, unitIdentities: true);

        var devices = await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Device.Id == "receiver-expanded/logitech/1/unit/ABCDEF01" && d.Battery.Percentage == 0);
        Assert.Contains(devices, d => d.Device.Id == "receiver-expanded/logitech/2/unit/ABCDEF02" && d.Battery.Percentage == 63);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(devices).AssociationComplete);
        Assert.DoesNotContain(transport.Requests, r => r[1] == 0xFF && r[4] == 0x04);
        Assert.DoesNotContain(transport.Requests, r => r[1] == 0xFF && r[2] == 0x83 && r[3] == 0xD5);
    }

    [Fact]
    public async Task AChangedFreshUnitIdentityExplicitlyDisconnectsTheOldChildEvenWhenOtherSlotsAreUncertain()
    {
        var endpoint = Endpoint(0xC534);
        var provider = new LogitechBatteryProvider();
        await using var first = new ExpansionTransport(endpoint, populatedSlots: [1], pairingMetadata: false, unitIdentities: true);
        await provider.ReadAsync(endpoint, first, SampledAt, CancellationToken.None);
        await using var replacement = new ExpansionTransport(endpoint, populatedSlots: [1], pairingMetadata: false,
            unitIdentities: true, unitIdentityPrefix: 0xBA);

        var devices = await provider.ReadAsync(endpoint, replacement, SampledAt.AddMinutes(1), CancellationToken.None);

        Assert.Contains(devices, d => d.Device.Id.EndsWith("/unit/ABCDEF01", StringComparison.Ordinal) &&
            d.Battery.State == ReadingState.Disconnected && d.Battery.Percentage is null);
        Assert.Contains(devices, d => d.Device.Id.EndsWith("/unit/BACDEF01", StringComparison.Ordinal) &&
            d.Battery.State == ReadingState.Current && d.Battery.Percentage == 0);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(devices).AssociationComplete);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FreshMatchingUnitIdentityPreservesAssociationWhenPairingMetadataAppearsOrDisappears(
        bool originalPairingMetadata, bool nextPairingMetadata)
    {
        var endpoint = Endpoint(0xC534);
        var provider = new LogitechBatteryProvider();
        await using var first = new ExpansionTransport(endpoint, populatedSlots: [1],
            pairingMetadata: originalPairingMetadata, unitIdentities: true);
        var original = Assert.Single(await provider.ReadAsync(endpoint, first, SampledAt, CancellationToken.None));
        await using var next = new ExpansionTransport(endpoint, populatedSlots: [1],
            pairingMetadata: nextPairingMetadata, unitIdentities: true);

        var current = Assert.Single(await provider.ReadAsync(endpoint, next, SampledAt.AddMinutes(1), CancellationToken.None));

        Assert.Equal(original.Device.Id, current.Device.Id);
        Assert.Equal(ReadingState.Current, current.Battery.State);
        Assert.Equal(0, current.Battery.Percentage);
        Assert.Equal("4081", current.Device.ModelId);
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0xC0)]
    public async Task HiddenOrObsoleteBatteryFeaturesAreNotQueried(byte flags)
    {
        var endpoint = Endpoint(0xC548);
        await using var transport = new ExpansionTransport(endpoint, bolt: true, populatedSlots: [1], unifiedFeatureFlags: flags);

        var device = Assert.Single(await new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None));

        Assert.Equal(ReadingState.Unsupported, device.Battery.State);
        Assert.Null(device.Battery.Percentage);
        Assert.DoesNotContain(transport.Requests, r => r[2] == 7);
    }

    [Fact]
    public async Task EmptyWirelessIdentityBytesCannotBecomeAPairedDeviceModel()
    {
        var endpoint = Endpoint(0xC548);
        await using var transport = new ExpansionTransport(endpoint, bolt: true, populatedSlots: [1], invalidWirelessIdentity: true);

        await Assert.ThrowsAsync<IOException>(() => providerRead());

        Task<IReadOnlyList<DeviceSnapshot>> providerRead() =>
            new LogitechBatteryProvider().ReadAsync(endpoint, transport, SampledAt, CancellationToken.None);
        Assert.DoesNotContain(transport.Requests, r => r[2] == 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NanoMissingSlotIdentityDoesNotAttributeGlobalReceiverDataToAPeripheral(bool legacy)
    {
        var endpoint = Endpoint(0xC52F);
        await using var transport = new ExpansionTransport(endpoint, populatedSlots: [1],
            pairingMetadata: false, legacy: legacy);

        var device = Assert.Single(await new LogitechBatteryProvider().ReadAsync(endpoint, transport,
            SampledAt, CancellationToken.None));

        Assert.Equal(ReadingState.Unsupported, device.Battery.State);
        Assert.Null(device.Battery.Percentage);
        Assert.DoesNotContain(transport.Requests, r => r[2] == 7 || r[2] == 0x81);
        Assert.DoesNotContain(transport.Requests, r => r[1] == 0xFF && r[4] == 0x04);
    }

    private static HidEndpoint Endpoint(ushort pid) =>
        new("fake-logitech-expanded", 0x046D, pid, 0xFF00, 1, 20, 20, 0, "receiver-expanded");

    private sealed class ExpansionTransport(HidEndpoint endpoint, byte[] populatedSlots, bool bolt = false,
        bool pairingMetadata = true, bool unitIdentities = false, bool legacy = false,
        byte? timeoutPairingSlot = null, byte emptyPairingError = 0x08,
        byte unifiedFeatureFlags = 0, byte unitIdentityPrefix = 0xAB, bool invalidWirelessIdentity = false) : IHidTransport
    {
        private readonly Queue<byte[]> replies = new();
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Requests { get; } = [];

        public ValueTask WriteAsync(byte[] request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.ToArray());
            if (request[1] == 0xFF && request[2] == 0x83 && request[3] == 0xB5)
            {
                byte sub = request[4];
                if (sub == 3 && !bolt) replies.Enqueue(Reply(request, [sub, 0, 0, 0, 0, 0, 2]));
                else if ((bolt && sub is >= 0x51 and <= 0x56) || (!bolt && sub is >= 0x20 and <= 0x25))
                {
                    byte slot = bolt ? (byte)(sub - 0x50) : (byte)(sub - 0x20 + 1);
                    if (slot == timeoutPairingSlot) return ValueTask.CompletedTask;
                    if (!pairingMetadata || !populatedSlots.Contains(slot)) replies.Enqueue(Error(request, emptyPairingError));
                    else replies.Enqueue(Reply(request, bolt
                        ? [sub, 2, invalidWirelessIdentity ? (byte)0 : (byte)0x81,
                            invalidWirelessIdentity ? (byte)0 : (byte)0x40, 0x12, 0x34, 0x56, slot]
                        : [sub, 0, 0, 0x40, 0x81, 0, 0, 2]));
                }
                else if (!bolt && sub is >= 0x30 and <= 0x35)
                    replies.Enqueue(Reply(request, [sub, 0x12, 0x34, 0x56, (byte)(sub - 0x30 + 1)]));
                else throw new InvalidOperationException("Unexpected receiver register access.");
            }
            else if (request[2] == 0 && (request[3] & 0xF0) == 0x10)
                replies.Enqueue(!populatedSlots.Contains(request[1]) ? Error(request, 0x08)
                    : legacy ? Error(request, 0x01) : Reply(request, [2, 0, request[6]]));
            else if (request[2] == 0 && (request[3] & 0xF0) == 0)
            {
                ushort feature = (ushort)((request[4] << 8) | request[5]);
                replies.Enqueue(Reply(request, feature switch
                {
                    0x0003 when unitIdentities => [3, 0, 0],
                    0x0003 or 0x0005 => [0, 0, 0],
                    0x1004 => [7, unifiedFeatureFlags, 0],
                    0x1000 => [0, 0, 0],
                    _ => throw new InvalidOperationException("Unexpected feature query.")
                }));
            }
            else if (request[2] == 3 && (request[3] & 0xF0) == 0)
                replies.Enqueue(Reply(request, [0, unitIdentityPrefix, 0xCD, 0xEF, request[1]]));
            else if (request[2] == 7 && (request[3] & 0xF0) == 0)
                replies.Enqueue(Reply(request, [0x0F, 2]));
            else if (request[2] == 7 && (request[3] & 0xF0) == 0x10)
                replies.Enqueue(Reply(request, [request[1] == 1 ? (byte)0 : (byte)63, 0, 0, 0]));
            else if (request[2] == 0x81 && request[3] == 0x0D && legacy)
                replies.Enqueue(Reply(request, [request[1] == 1 ? (byte)0 : (byte)63, 0, 0x30]));
            else throw new InvalidOperationException("Unexpected or non-read-only command.");
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return replies.Count == 0 ? throw new TimeoutException("Scripted receiver timeout.")
                : ValueTask.FromResult(replies.Dequeue());
        }

        private static byte[] Reply(byte[] request, byte[] parameters)
        {
            byte[] response = new byte[20];
            response[0] = 0x11; response[1] = request[1]; response[2] = request[2]; response[3] = request[3];
            parameters.CopyTo(response, 4);
            return response;
        }

        private static byte[] Error(byte[] request, byte code) =>
            [0x10, request[1], 0x8F, request[2], request[3], code, 0];
        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
