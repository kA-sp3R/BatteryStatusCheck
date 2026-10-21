using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerDualLinkProviderTests
{
    private const ushort MousePid = 0x00A6;
    private const ushort KeyboardPid = 0x025C;
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(KeyboardPid, false)]
    [InlineData(KeyboardPid, true)]
    [InlineData(MousePid, false)]
    [InlineData(MousePid, true)]
    public async Task UnorderedWirelessEntriesUseExplicitRolesForDistinctMouseAndKeyboardReadings(
        ushort physicalProductId, bool keyboardFirst)
    {
        var endpoint = Endpoint(physicalProductId);
        await using var transport = new DualTransport(endpoint, new Scenario { KeyboardFirst = keyboardFirst });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.Equal(2, result.Count);
        var mouse = Assert.Single(result.Where(device => device.Device.ModelId == "1532:00A6"));
        var keyboard = Assert.Single(result.Where(device => device.Device.ModelId == "1532:025C"));
        AssertCurrent(mouse, 0, ChargingState.Discharging);
        AssertCurrent(keyboard, 50, ChargingState.Charging);
        Assert.NotEqual(mouse.Device.Id, keyboard.Device.Id);
        Assert.Contains("Viper V2 Pro", mouse.Device.Name);
        Assert.Contains("BlackWidow V3 Pro", keyboard.Device.Name);
        Assert.Equal(2, transport.RoutedBatteryTransactions.Count);
        Assert.Contains((byte)0x01, transport.RoutedBatteryTransactions);
        Assert.Contains((byte)0x81, transport.RoutedBatteryTransactions);
        Assert.DoesNotContain(result, device => device.Battery.Percentage == RazerProtocol.ToPercentage(200));
        Assert.All(transport.Requests, AssertReadOnlyRequest);
    }

    [Theory]
    [InlineData(RoutingFailure.PrimaryTypeMismatch)]
    [InlineData(RoutingFailure.PrimaryPidNotListed)]
    [InlineData(RoutingFailure.UnknownSecondary)]
    [InlineData(RoutingFailure.TwoMice)]
    [InlineData(RoutingFailure.PrimaryUnsupported)]
    public async Task IncompleteOrContradictoryRolesCannotReuseTheEarlierUntargetedBattery(RoutingFailure failure)
    {
        var endpoint = Endpoint(MousePid);
        await using var transport = new DualTransport(endpoint, new Scenario { RoutingFailure = failure });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.DoesNotContain(result, device => device.Battery.State == ReadingState.Current);
        Assert.All(result, device => Assert.Null(device.Battery.Percentage));
        Assert.Empty(transport.RoutedBatteryTransactions);
        Assert.All(transport.Requests, AssertReadOnlyRequest);
    }

    [Fact]
    public async Task ExplicitlyDisconnectedPeersCannotInheritACachedLegacyBattery()
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { AllDisconnected = true });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, device =>
        {
            Assert.Equal(ReadingState.Disconnected, device.Battery.State);
            Assert.Equal(ConnectionState.Disconnected, device.Connection);
            Assert.Null(device.Battery.Percentage);
            Assert.Null(device.Battery.SampledAt);
        });
        Assert.Empty(transport.RoutedBatteryTransactions);
    }

    [Fact]
    public async Task ADisconnectedMouseDoesNotPreventTheSeparatelyConnectedKeyboardFromBeingRead()
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { InitiallyDisconnectedPid = MousePid });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var mouse = Assert.Single(result.Where(device => device.Device.ModelId == "1532:00A6"));
        var keyboard = Assert.Single(result.Where(device => device.Device.ModelId == "1532:025C"));
        Assert.Equal(ReadingState.Disconnected, mouse.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, mouse.Connection);
        Assert.Null(mouse.Battery.Percentage);
        Assert.Null(mouse.Battery.SampledAt);
        AssertCurrent(keyboard, 50, ChargingState.Charging);
        Assert.Equal(new byte[] { 0x81 }, transport.RoutedBatteryTransactions);
    }

    [Fact]
    public async Task ADisconnectedPrimaryStillMustHaveAConsistentReportedRole()
    {
        var endpoint = Endpoint(MousePid);
        await using var transport = new DualTransport(endpoint, new Scenario
        {
            InitiallyDisconnectedPid = MousePid,
            RoutingFailure = RoutingFailure.PrimaryTypeMismatch
        });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.DoesNotContain(result, device => device.Battery.State == ReadingState.Current);
        Assert.All(result, device => Assert.Null(device.Battery.Percentage));
        Assert.Empty(transport.RoutedBatteryTransactions);
    }

    [Theory]
    [InlineData((byte)0x01, "1532:00A6")]
    [InlineData((byte)0x81, "1532:025C")]
    public async Task APeerDisconnectedAfterItsBatteryReplyCannotPublishThatCachedValue(byte transaction, string modelId)
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { DisconnectAfterBatteryTransaction = transaction });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var disconnected = Assert.Single(result.Where(device => device.Device.ModelId == modelId));
        Assert.Equal(ReadingState.Disconnected, disconnected.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, disconnected.Connection);
        Assert.Null(disconnected.Battery.Percentage);
        Assert.Null(disconnected.Battery.SampledAt);
        Assert.DoesNotContain(result, device => device.Device.ModelId == modelId && device.Battery.State == ReadingState.Current);
    }

    [Theory]
    [InlineData((byte)0x01, "1532:00A6")]
    [InlineData((byte)0x81, "1532:025C")]
    public async Task APeerDisconnectedDuringChargingCannotPublishItsPreviouslyConfirmedBattery(byte transaction, string modelId)
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { DisconnectAfterChargingTransaction = transaction });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var disconnected = Assert.Single(result.Where(device => device.Device.ModelId == modelId));
        Assert.Equal(ReadingState.Disconnected, disconnected.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, disconnected.Connection);
        Assert.Null(disconnected.Battery.Percentage);
        Assert.Null(disconnected.Battery.SampledAt);
        Assert.DoesNotContain(result, device => device.Device.ModelId == modelId && device.Battery.State == ReadingState.Current);
    }

    [Fact]
    public async Task AFinalDeviceListAlsoClearsAnEarlierPeerThatHasSinceDisconnected()
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { DisconnectMouseAfterKeyboardCharging = true });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var mouse = Assert.Single(result.Where(device => device.Device.ModelId == "1532:00A6"));
        Assert.Equal(ReadingState.Disconnected, mouse.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, mouse.Connection);
        Assert.Null(mouse.Battery.Percentage);
        Assert.Null(mouse.Battery.SampledAt);
        AssertCurrent(Assert.Single(result.Where(device => device.Device.ModelId == "1532:025C")), 50, ChargingState.Charging);
    }

    [Fact]
    public async Task APeerReconnectingLaterInTheBatchCannotReviveItsPreDisconnectionBattery()
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario
        {
            DisconnectAfterChargingTransaction = 0x01,
            ReconnectMouseAfterKeyboardCharging = true
        });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var mouse = Assert.Single(result.Where(device => device.Device.ModelId == "1532:00A6"));
        Assert.Equal(ReadingState.Unsupported, mouse.Battery.State);
        Assert.Equal(ConnectionState.Connected, mouse.Connection);
        Assert.Null(mouse.Battery.Percentage);
        Assert.Null(mouse.Battery.SampledAt);
        Assert.Contains("reconnected", mouse.Battery.Detail);
        AssertCurrent(Assert.Single(result.Where(device => device.Device.ModelId == "1532:025C")), 50, ChargingState.Charging);
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x81)]
    public async Task ChargingTimeoutKeepsTheBoundBatteryButDoesNotReuseTheClosedSession(byte transaction)
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { TimeoutOnChargingTransaction = transaction });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var modelId = transaction == 0x01 ? "1532:00A6" : "1532:025C";
        var retained = Assert.Single(result.Where(device => device.Device.ModelId == modelId));
        AssertCurrent(retained, transaction == 0x01 ? 0 : 50, ChargingState.Unknown);
        Assert.Equal(0, transport.CommandsAfterTimeout);
        Assert.Equal(transaction == 0x01 ? 1 : 2, transport.RoutedBatteryTransactions.Count);
        Assert.Equal(RazerProtocol.ChargingCommand, transport.Requests[^1][8]);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(result).AssociationComplete);
    }

    [Theory]
    [InlineData((byte)0x01, ChargingConfirmationFailure.AssociationFailed)]
    [InlineData((byte)0x01, ChargingConfirmationFailure.AssociationCorrupt)]
    [InlineData((byte)0x01, ChargingConfirmationFailure.AssociationTimeout)]
    [InlineData((byte)0x01, ChargingConfirmationFailure.PrimaryChanged)]
    [InlineData((byte)0x01, ChargingConfirmationFailure.PrimaryFailed)]
    [InlineData((byte)0x01, ChargingConfirmationFailure.PrimaryTimeout)]
    [InlineData((byte)0x81, ChargingConfirmationFailure.AssociationFailed)]
    [InlineData((byte)0x81, ChargingConfirmationFailure.AssociationCorrupt)]
    [InlineData((byte)0x81, ChargingConfirmationFailure.AssociationTimeout)]
    [InlineData((byte)0x81, ChargingConfirmationFailure.PrimaryChanged)]
    [InlineData((byte)0x81, ChargingConfirmationFailure.PrimaryFailed)]
    [InlineData((byte)0x81, ChargingConfirmationFailure.PrimaryTimeout)]
    public async Task FailedFinalAssociationOrRoleReadKeepsOnlyConfirmedBatteryWithUnknownCharging(
        byte transaction, ChargingConfirmationFailure failure)
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario
        {
            ChargingConfirmationTransaction = transaction,
            ChargingConfirmationFailure = failure
        });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        var modelId = transaction == 0x01 ? "1532:00A6" : "1532:025C";
        var retained = Assert.Single(result.Where(device => device.Device.ModelId == modelId));
        AssertCurrent(retained, transaction == 0x01 ? 0 : 50, ChargingState.Unknown);
        Assert.Equal(0, transport.CommandsAfterTimeout);
        Assert.Equal(transaction == 0x01 ? 1 : 2, transport.RoutedBatteryTransactions.Count);
        Assert.Equal(failure is ChargingConfirmationFailure.PrimaryChanged or ChargingConfirmationFailure.PrimaryFailed or
            ChargingConfirmationFailure.PrimaryTimeout ? 0xC5 : 0xBF, transport.Requests[^1][8]);
        Assert.False(Assert.IsType<ReceiverSnapshotBatch>(result).AssociationComplete);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeTimeoutStopsSessionReuseAndRetainsOnlyPreviouslyCompletedPeerReadings(int timeoutBatteryNumber)
    {
        var endpoint = Endpoint(KeyboardPid);
        await using var transport = new DualTransport(endpoint, new Scenario { TimeoutOnRoutedBatteryNumber = timeoutBatteryNumber });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.Equal(timeoutBatteryNumber - 1, result.Count(device => device.Battery.State == ReadingState.Current));
        Assert.Equal(timeoutBatteryNumber, transport.RoutedBatteryTransactions.Count);
        Assert.Equal(0, transport.CommandsAfterTimeout);
        Assert.All(result.Where(device => device.Battery.State == ReadingState.Current), device =>
        {
            Assert.Equal(SampleTime, device.Battery.SampledAt);
            Assert.Equal(ConnectionState.Connected, device.Connection);
            Assert.Contains(device.Device.ModelId == "1532:00A6" ? (byte)0x01 : (byte)0x81,
                transport.SuccessfulRoutedBatteryTransactions);
        });
        Assert.DoesNotContain(result, device => device.Battery.Percentage == RazerProtocol.ToPercentage(200));
    }

    [Fact]
    public async Task TimeoutWhileBindingPrimaryRolesDoesNotQueryAnyChildOrReuseTheClosedSession()
    {
        var endpoint = Endpoint(MousePid);
        await using var transport = new DualTransport(endpoint, new Scenario { TimeoutOnPrimary = true });

        var result = await new RazerBatteryProvider().ReadAsync(endpoint, transport, SampleTime, CancellationToken.None);

        Assert.DoesNotContain(result, device => device.Battery.State == ReadingState.Current);
        Assert.All(result, device => Assert.Null(device.Battery.Percentage));
        Assert.Empty(transport.RoutedBatteryTransactions);
        Assert.Equal(0, transport.CommandsAfterTimeout);
    }

    private static HidEndpoint Endpoint(ushort productId) => new(
        $"fake-usb-vid_1532&pid_{productId:X4}&mi_{(productId == KeyboardPid ? "02" : "00")}",
        0x1532, productId, 0xFF00, 1, 0, 0, 91, "shared-receiver");

    private static void AssertCurrent(DeviceSnapshot device, int percentage, ChargingState charging)
    {
        Assert.Equal(ReadingState.Current, device.Battery.State);
        Assert.Equal(percentage, device.Battery.Percentage);
        Assert.Null(device.Battery.Category);
        Assert.Equal(SampleTime, device.Battery.SampledAt);
        Assert.Equal(charging, device.Battery.Charging);
        Assert.Equal(ConnectionState.Connected, device.Connection);
        Assert.Equal("shared-receiver", device.Device.ReceiverId);
    }

    private static void AssertReadOnlyRequest(byte[] request)
    {
        Assert.True((request[7] == 0 && request[8] is 0xBF or 0xC5 or 0xB8) ||
                    (request[7] == 7 && request[8] is RazerProtocol.BatteryCommand or RazerProtocol.ChargingCommand),
            "Battery monitoring must only send documented read commands.");
    }

    public enum RoutingFailure { None, PrimaryTypeMismatch, PrimaryPidNotListed, UnknownSecondary, TwoMice, PrimaryUnsupported }
    public enum ChargingConfirmationFailure { None, AssociationFailed, AssociationCorrupt, AssociationTimeout, PrimaryChanged, PrimaryFailed, PrimaryTimeout }

    private sealed record Scenario
    {
        public bool KeyboardFirst { get; init; }
        public RoutingFailure RoutingFailure { get; init; }
        public bool AllDisconnected { get; init; }
        public ushort? InitiallyDisconnectedPid { get; init; }
        public byte? DisconnectAfterBatteryTransaction { get; init; }
        public byte? DisconnectAfterChargingTransaction { get; init; }
        public bool DisconnectMouseAfterKeyboardCharging { get; init; }
        public bool ReconnectMouseAfterKeyboardCharging { get; init; }
        public byte? TimeoutOnChargingTransaction { get; init; }
        public byte? ChargingConfirmationTransaction { get; init; }
        public ChargingConfirmationFailure ChargingConfirmationFailure { get; init; }
        public int TimeoutOnRoutedBatteryNumber { get; init; }
        public bool TimeoutOnPrimary { get; init; }
    }

    /// <summary>Independent report fixtures; this transport never enumerates or accesses hardware.</summary>
    private sealed class DualTransport(HidEndpoint endpoint, Scenario scenario) : IHidTransport
    {
        private byte[]? lastRequest;
        private bool timedOut;
        private readonly HashSet<byte> sampledTransactions = [];
        private readonly HashSet<byte> chargedTransactions = [];
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Requests { get; } = [];
        public List<byte> RoutedBatteryTransactions { get; } = [];
        public List<byte> SuccessfulRoutedBatteryTransactions { get; } = [];
        public int CommandsAfterTimeout { get; private set; }

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut) { CommandsAfterTimeout++; throw new IOException("The timed-out fake session is closed."); }
            lastRequest = report.ToArray();
            Requests.Add(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut) { CommandsAfterTimeout++; throw new IOException("The timed-out fake session is closed."); }
            var request = lastRequest ?? throw new InvalidOperationException("A feature request must be sent first.");
            var response = request.ToArray();
            response.AsSpan(9, 80).Clear();
            response[1] = 2;
            bool confirmingCharge = scenario.ChargingConfirmationTransaction is byte chargeTransaction &&
                chargedTransactions.Contains(chargeTransaction);
            bool corrupt = false;
            if (request[7] == 0 && request[8] == 0xBF)
            {
                if (confirmingCharge)
                {
                    if (scenario.ChargingConfirmationFailure == ChargingConfirmationFailure.AssociationTimeout) return Timeout();
                    if (scenario.ChargingConfirmationFailure == ChargingConfirmationFailure.AssociationFailed) response[1] = 3;
                    corrupt = scenario.ChargingConfirmationFailure == ChargingConfirmationFailure.AssociationCorrupt;
                }
                ushort second = scenario.RoutingFailure switch
                {
                    RoutingFailure.UnknownSecondary => 0xDEAD,
                    RoutingFailure.TwoMice => 0x00BF,
                    _ => KeyboardPid
                };
                ushort[] products = scenario.KeyboardFirst ? [second, MousePid] : [MousePid, second];
                response[9] = 2;
                for (var index = 0; index < products.Length; index++)
                {
                    ushort productId = products[index];
                    byte transaction = productId == MousePid ? (byte)0x01 : (byte)0x81;
                    bool disconnected = scenario.AllDisconnected || scenario.InitiallyDisconnectedPid == productId ||
                        scenario.DisconnectAfterBatteryTransaction == transaction && sampledTransactions.Contains(transaction) ||
                        scenario.DisconnectAfterChargingTransaction == transaction && chargedTransactions.Contains(transaction) ||
                        scenario.DisconnectMouseAfterKeyboardCharging && productId == MousePid && chargedTransactions.Contains(0x81);
                    if (scenario.ReconnectMouseAfterKeyboardCharging && productId == MousePid && chargedTransactions.Contains(0x81))
                        disconnected = false;
                    int offset = 10 + 3 * index;
                    response[offset] = disconnected ? (byte)0 : (byte)1;
                    response[offset + 1] = (byte)(productId >> 8);
                    response[offset + 2] = (byte)productId;
                }
            }
            else if (request[7] == 0 && request[8] == 0xC5)
            {
                if (scenario.TimeoutOnPrimary) return Timeout();
                if (confirmingCharge && scenario.ChargingConfirmationFailure == ChargingConfirmationFailure.PrimaryTimeout) return Timeout();
                if (scenario.RoutingFailure == RoutingFailure.PrimaryUnsupported) response[1] = 5;
                else if (confirmingCharge && scenario.ChargingConfirmationFailure == ChargingConfirmationFailure.PrimaryFailed) response[1] = 3;
                else
                {
                    ushort primary = scenario.RoutingFailure == RoutingFailure.PrimaryPidNotListed ? (ushort)0x009F : endpoint.ProductId;
                    response[9] = scenario.RoutingFailure == RoutingFailure.PrimaryTypeMismatch ? (byte)2
                        : primary == KeyboardPid ? (byte)2 : (byte)1;
                    if (confirmingCharge && scenario.ChargingConfirmationFailure == ChargingConfirmationFailure.PrimaryChanged)
                        response[9] = response[9] == 1 ? (byte)2 : (byte)1;
                    response[10] = (byte)(primary >> 8);
                    response[11] = (byte)primary;
                }
            }
            else if (request[7] == 0 && request[8] == 0xB8)
                response[9] = scenario.AllDisconnected ? (byte)0 : (byte)1;
            else if (request[7] == 7 && request[8] == RazerProtocol.BatteryCommand)
            {
                if (request[2] is 0x01 or 0x81)
                {
                    RoutedBatteryTransactions.Add(request[2]);
                    if (scenario.TimeoutOnRoutedBatteryNumber == RoutedBatteryTransactions.Count) return Timeout();
                    response[10] = request[2] == 0x01 ? (byte)0 : (byte)128;
                    sampledTransactions.Add(request[2]);
                    SuccessfulRoutedBatteryTransactions.Add(request[2]);
                }
                else response[10] = 200; // A valid legacy reply must not be copied into both discovered children.
            }
            else if (request[7] == 7 && request[8] == RazerProtocol.ChargingCommand)
            {
                if (scenario.TimeoutOnChargingTransaction == request[2]) return Timeout();
                response[10] = request[2] == 0x81 ? (byte)1 : (byte)0;
                if (request[2] is 0x01 or 0x81) chargedTransactions.Add(request[2]);
            }
            else throw new InvalidOperationException("Unexpected Razer command in the dual-link fixture.");
            response[89] = 0;
            for (var index = 3; index <= 88; index++) response[89] ^= response[index];
            if (corrupt) response[89] ^= 1;
            return ValueTask.FromResult(response);
        }

        private ValueTask<byte[]> Timeout()
        {
            timedOut = true;
            throw new TimeoutException("Scripted native timeout closes this session.");
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
