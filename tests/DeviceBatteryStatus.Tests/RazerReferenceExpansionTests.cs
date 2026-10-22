using System.Diagnostics;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerReferenceExpansionTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 22, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ViperV4WirelessUsesTheAuditedMouseInterfaceWithoutAdmittingItsWiredCounterpart()
    {
        Assert.True(RazerDeviceCatalog.TryGet(0x00E6, out var profile));
        Assert.Equal("Razer Viper V4 Pro", profile!.Name);
        Assert.Equal(RazerDeviceKind.Mouse, profile.DeviceKind);
        Assert.Equal(3, profile.InterfaceNumber);
        Assert.Equal(0x1F, profile.TransactionId);
        Assert.True(profile.SupportsCharging);
        Assert.False(RazerDeviceCatalog.TryGet(0x00E5, out _));
        var provider = new RazerBatteryProvider();
        Assert.True(provider.CanHandle(Endpoint(0x00E6, 3)));
        Assert.False(provider.CanHandle(Endpoint(0x00E6, 0)));
        Assert.False(provider.CanHandle(Endpoint(0x00E5, 3)));
        Assert.False(provider.CanHandle(Endpoint(0x00E6, 3) with { FeatureReportLength = 90 }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(128, 50)]
    [InlineData(255, 100)]
    public async Task NewWirelessMappingUsesActualBatteryAndMouseDiscoveryNamespace(byte raw, int expected)
    {
        var endpoint = Endpoint(0x00E6, 3);
        await using var transport = new ReferenceTransport(endpoint) { BatteryRaw = raw, LinkConnected = true };

        var snapshot = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            ObservedAt, CancellationToken.None));

        Assert.Equal(ReadingState.Current, snapshot.Battery.State);
        Assert.Equal(expected, snapshot.Battery.Percentage);
        Assert.Equal(ObservedAt, snapshot.Battery.SampledAt);
        Assert.Equal(ConnectionState.Connected, snapshot.Connection);
        Assert.Equal(new byte[] { 0x80, 0x84, 0xBF, 0xB8 }, transport.Requests.Select(r => r[8]).ToArray());
        Assert.Equal(new byte[] { 0x1F, 0x1F, 0x01, 0x01 }, transport.Requests.Select(r => r[2]).ToArray());
        Assert.All(transport.Requests, AssertReadOnly);
    }

    [Theory]
    [InlineData(ChargeFailure.Unsupported)]
    [InlineData(ChargeFailure.BadChecksum)]
    [InlineData(ChargeFailure.NativeTimeout)]
    public async Task SeparateChargingFailureCannotEraseTheNewReceiversGenuineZero(ChargeFailure failure)
    {
        var endpoint = Endpoint(0x00E6, 3);
        await using var transport = new ReferenceTransport(endpoint) { ChargingFailure = failure };

        var snapshot = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            ObservedAt, CancellationToken.None));

        Assert.Equal(ReadingState.Current, snapshot.Battery.State);
        Assert.Equal(0, snapshot.Battery.Percentage);
        Assert.Equal(ObservedAt, snapshot.Battery.SampledAt);
        Assert.Equal(ChargingState.Unknown, snapshot.Battery.Charging);
        Assert.Equal(ConnectionState.Unknown, snapshot.Connection);
        if (failure != ChargeFailure.Unsupported) Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, AssertReadOnly);
    }

    [Theory]
    [InlineData(0x00E6, 3)]
    [InlineData(0x00B3, 0)]
    public async Task AMouseOnInterfaceThreeRemainsAMouseWhenItsNativeProductIsAssociated(
        ushort physicalProductId, byte interfaceNumber)
    {
        var endpoint = Endpoint(physicalProductId, interfaceNumber);
        await using var transport = new ReferenceTransport(endpoint) { AssociateViperV4 = true };

        var snapshot = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            ObservedAt, CancellationToken.None));

        Assert.Equal(ReadingState.Current, snapshot.Battery.State);
        Assert.Equal(0, snapshot.Battery.Percentage);
        Assert.Equal(ConnectionState.Connected, snapshot.Connection);
        Assert.Equal("1532:00E6", snapshot.Device.ModelId);
        Assert.EndsWith("/wireless/00E6", snapshot.Device.Id);
        Assert.DoesNotContain(transport.Requests, r => r[2] == 0x81);
        Assert.All(transport.Requests, AssertReadOnly);
    }

    [Theory]
    [InlineData(0x0062)]
    [InlineData(0x0094)]
    public async Task SlowReceiverCommandsWaitForTheirSourceDocumentedResponseWindow(ushort productId)
    {
        var endpoint = Endpoint(productId, 0);
        // A receiver that is not ready at the old 60 ms interval would fail instead
        // of yielding a genuine measurement. Allow scheduler/clock granularity.
        await using var transport = new ReferenceTransport(endpoint) { MinimumWait = TimeSpan.FromMilliseconds(350) };

        var snapshot = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            ObservedAt, CancellationToken.None));

        Assert.Equal(ReadingState.Current, snapshot.Battery.State);
        Assert.Equal(0, snapshot.Battery.Percentage);
        Assert.Equal(new byte[] { 0x80, 0xBF, 0xB8 }, transport.Requests.Select(r => r[8]).ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(400), RazerDeviceCatalog.GetResponseDelay(productId));
        Assert.All(transport.Requests, AssertReadOnly);
    }

    [Theory]
    [InlineData(0x0062)]
    [InlineData(0x0094)]
    public async Task ManualPollingDetectionAlsoWaitsForTheSlowReceiversActualResponse(ushort productId)
    {
        var endpoint = Endpoint(productId, 0);
        var identity = new PeripheralIdentity($"{endpoint.ReceiverId}:razer:{productId:X4}", "Slow mouse",
            DeviceVendor.Razer, endpoint.ReceiverId, $"1532:{productId:X4}");
        await using var transport = new ReferenceTransport(endpoint)
        { MinimumWait = TimeSpan.FromMilliseconds(350) };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport, identity,
            CancellationToken.None);

        Assert.Equal(PollingRateState.Supported, result.State);
        Assert.Equal(1000, result.CurrentRateHz);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(RazerPollingProtocol.ReadCommand, request[8]);
        Assert.Equal(0, request[7]);
    }

    [Fact]
    public void ExplicitCatalogKindsPreserveKeyboardAndMasterNamespacesWhenInterfaceNumbersOverlap()
    {
        foreach (ushort keyboard in new ushort[] { 0x025C, 0x0271, 0x0290, 0x0296, 0x02BA, 0x02D5 })
        {
            Assert.True(RazerDeviceCatalog.TryGet(keyboard, out var profile));
            Assert.Equal(RazerDeviceKind.Keyboard, profile!.DeviceKind);
            Assert.True(profile.IdentifiesPeripheralModel);
        }
        foreach (ushort receiver in new ushort[] { 0x00A4, 0x00B3, 0x00CF, 0x00F1 })
        {
            Assert.True(RazerDeviceCatalog.TryGet(receiver, out var profile));
            Assert.Equal(RazerDeviceKind.Receiver, profile!.DeviceKind);
            Assert.False(profile.IdentifiesPeripheralModel);
        }
    }

    [Fact]
    public async Task CancelingTheSlowResponseWaitDoesNotFetchOrReuseTheReply()
    {
        var endpoint = Endpoint(0x0062, 0);
        await using var transport = new ReferenceTransport(endpoint);
        using var cancellation = new CancellationTokenSource();
        transport.AfterSend = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RazerBatteryProvider().ReadAsync(
            endpoint, transport, ObservedAt, cancellation.Token));

        Assert.Single(transport.Requests);
        Assert.Equal(0, transport.GetCount);
    }

    [Fact]
    public void InterfaceThreeMouseCanBeSelectedThroughHyperPollingWithoutEnablingUnprovenViperV4Writes()
    {
        var dongle = Endpoint(0x00B3, 0);
        var child = new PeripheralIdentity("reference-receiver:razer:00B3/wireless/00E6", "Viper V4 Pro",
            DeviceVendor.Razer, dongle.ReceiverId, "1532:00E6");
        var controller = new RazerPollingRateController();
        Assert.True(controller.CanHandle(dongle, child));
        var native = Endpoint(0x00E6, 3);
        Assert.False(controller.CanHandle(native, child with
        {
            Id = "reference-receiver:razer:00E6/wireless/00E6"
        }));
    }

    private static HidEndpoint Endpoint(ushort productId, byte usbInterface) =>
        new($"fake-hid-vid_1532&pid_{productId:X4}&mi_{usbInterface:X2}", 0x1532, productId,
            0xFF00, 1, 0, 0, RazerProtocol.ReportLength, "reference-receiver");

    private static void AssertReadOnly(byte[] request) => Assert.True(
        (request[7] == 7 && request[8] is 0x80 or 0x84) ||
        (request[7] == 0 && request[8] is 0xBF or 0xC5 or 0xB8));

    public enum ChargeFailure { None, Unsupported, BadChecksum, NativeTimeout }

    private sealed class ReferenceTransport : IHidTransport
    {
        public ReferenceTransport(HidEndpoint endpoint) => Endpoint = endpoint;
        public HidEndpoint Endpoint { get; }
        public List<byte[]> Requests { get; } = [];
        public byte BatteryRaw { get; init; }
        public bool LinkConnected { get; init; }
        public bool AssociateViperV4 { get; init; }
        public ChargeFailure ChargingFailure { get; init; }
        public TimeSpan MinimumWait { get; init; }
        public Action? AfterSend { get; set; }
        public int GetCount { get; private set; }
        private long sentAt;

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(report.ToArray());
            sentAt = Stopwatch.GetTimestamp();
            AfterSend?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            if (Stopwatch.GetElapsedTime(sentAt) < MinimumWait)
                throw new IOException("Synthetic receiver is not ready before its documented response delay.");
            var reply = Requests[^1].ToArray();
            reply[1] = 2;
            switch (reply[8])
            {
                case 0x80:
                    reply[10] = BatteryRaw;
                    break;
                case 0x84:
                    if (ChargingFailure == ChargeFailure.NativeTimeout) throw new TimeoutException("Synthetic native session closed.");
                    if (ChargingFailure == ChargeFailure.Unsupported) reply[1] = 5;
                    reply[10] = 1;
                    break;
                case 0xBF:
                    if (!AssociateViperV4) reply[1] = 5;
                    else
                    {
                        reply[9] = 1;
                        reply[10] = 1;
                        reply[11] = 0;
                        reply[12] = 0xE6;
                    }
                    break;
                case 0xC5:
                    reply[9] = 1; // Native Mouse role, independent of interface3.
                    reply[10] = 0;
                    reply[11] = 0xE6;
                    break;
                case 0x85:
                    reply[9] = 1; // Documented ordinary 1000 Hz read encoding; no setting command.
                    break;
                case 0xB8:
                    if (!LinkConnected) reply[1] = 5;
                    else reply[9] = 1;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected command in read-only reference fixture.");
            }
            RazerProtocolTests.SetChecksum(reply);
            if (reply[8] == 0x84 && ChargingFailure == ChargeFailure.BadChecksum) reply[89] ^= 1;
            return ValueTask.FromResult(reply);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new InvalidOperationException("No output report allowed.");
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new InvalidOperationException("No input report allowed.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
