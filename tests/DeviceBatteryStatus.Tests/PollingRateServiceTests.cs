using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class PollingRateServiceTests
{
    private static readonly HidEndpoint Endpoint =
        new("receiver-path", 0x1532, 0x00B8, 0xFF00, 1, 20, 20, 91, "receiver-a");
    private static readonly PeripheralIdentity Peripheral =
        new("mouse-a", "Mouse A", DeviceVendor.Razer, "receiver-a", "1532:00B8");

    [Fact]
    public async Task ReadUsesOnlyTheSelectedReceiverAndItsBestSupportedCollection()
    {
        var discovery = new Discovery
        {
            Endpoints = [Endpoint with { DevicePath = "other-receiver", ReceiverId = "receiver-b", InputReportLength = 128 },
                Endpoint, Endpoint with { DevicePath = "preferred-collection", InputReportLength = 64, OutputReportLength = 64 }]
        };
        var transports = new Transports();
        var controller = new Controller();

        var result = await Service(discovery, transports, controller).ReadAsync(Peripheral, CancellationToken.None);

        Assert.Equal(PollingRateState.Supported, result.State);
        Assert.Equal("mouse-a", result.DeviceId);
        Assert.Equal("preferred-collection", Assert.Single(transports.Opened).DevicePath);
        Assert.Equal(1, controller.Reads);
        Assert.Equal(0, transports.Writes);
    }

    [Fact]
    public async Task UnsupportedDeviceNeverOpensAReceiverForReadOrApply()
    {
        var discovery = new Discovery();
        var transports = new Transports();
        var controller = new Controller { Handles = false };
        var service = Service(discovery, transports, controller);

        var read = await service.ReadAsync(Peripheral, CancellationToken.None);
        var applied = await service.ApplyAsync(Peripheral, 500, () => true, CancellationToken.None);

        Assert.Equal(PollingRateState.Unsupported, read.State);
        Assert.Equal(PollingRateState.Unsupported, applied.Info.State);
        Assert.False(applied.Verified);
        Assert.Empty(transports.Opened);
        Assert.Equal(0, controller.Reads);
        Assert.Equal(0, controller.Applies);
    }

    [Fact]
    public async Task EveryOperationReenumeratesAndCannotUseAHandleFromBeforeReceiverRemoval()
    {
        var discovery = new Discovery();
        var transports = new Transports();
        var controller = new Controller();
        var service = Service(discovery, transports, controller);
        await service.ReadAsync(Peripheral, CancellationToken.None);
        discovery.Endpoints = [];

        var result = await service.ApplyAsync(Peripheral, 500, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Disconnected, result.Info.State);
        Assert.Equal(2, discovery.Enumerations);
        Assert.Single(transports.Opened);
        Assert.Equal(0, transports.Writes);
        Assert.Equal(0, controller.Applies);
    }

    [Fact]
    public async Task IncompleteDiscoveryDoesNotClaimThatAnUnobservedReceiverWasDisconnected()
    {
        var discovery = new Discovery { Endpoints = [], Warnings = ["An interface could not be inspected."] };
        var transports = new Transports();

        var result = await Service(discovery, transports, new Controller()).ReadAsync(Peripheral, CancellationToken.None);

        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Null(result.CurrentRateHz);
        Assert.Empty(transports.Opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringEnumerationPropagatesEvenWhenTheReceiverIsAbsent(bool apply)
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = new Discovery { Endpoints = [], OnEnumerate = cancellation.Cancel };
        var transports = new Transports();
        var service = Service(discovery, transports, new Controller());

        if (apply)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ApplyAsync(Peripheral, 500, () => true, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ReadAsync(Peripheral, cancellation.Token));

        Assert.Equal(1, discovery.Enumerations);
        Assert.Empty(transports.Opened);
    }

    [Fact]
    public async Task DisabledManualControlCancelsWithoutDiscoveryOrOpeningAReceiver()
    {
        var discovery = new Discovery();
        var transports = new Transports();
        var controller = new Controller();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(discovery, transports, controller).ApplyAsync(Peripheral, 500, () => false, CancellationToken.None));

        Assert.Equal(0, discovery.Enumerations);
        Assert.Empty(transports.Opened);
        Assert.Equal(0, controller.Applies);
        Assert.Equal(0, transports.Writes);
    }

    [Fact]
    public async Task CancellationInsideThePostGateAuthorizationCheckDoesNotStartDiscovery()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = new Discovery();
        var transports = new Transports();
        var controller = new Controller();
        var checks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(discovery, transports, controller)
            .ApplyAsync(Peripheral, 500, () =>
            {
                if (++checks == 2) cancellation.Cancel();
                return true;
            }, cancellation.Token));

        Assert.Equal(2, checks);
        Assert.Equal(0, discovery.Enumerations);
        Assert.Empty(transports.Opened);
        Assert.Equal(0, controller.Applies);
    }

    [Fact]
    public async Task CancelingAnApplyWhileTheGateIsOccupiedNeverOpensOrWrites()
    {
        var gate = new HidOperationGate();
        using var occupied = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var discovery = new Discovery();
        var transports = new Transports();
        var controller = new Controller();
        var service = new PollingRateService(discovery, transports, [controller], gate);
        var apply = service.ApplyAsync(Peripheral, 500, () => true, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, discovery.Enumerations);
        Assert.Empty(transports.Opened);
        Assert.Equal(0, transports.Writes);
    }

    [Fact]
    public async Task DisablingControlWhileAnApplyIsQueuedCancelsBeforeDiscoveryOrOpen()
    {
        var gate = new HidOperationGate();
        var occupied = await gate.EnterAsync(CancellationToken.None);
        var enabled = true;
        var discovery = new Discovery();
        var transports = new Transports();
        var controller = new Controller();
        var service = new PollingRateService(discovery, transports, [controller], gate);
        var apply = service.ApplyAsync(Peripheral, 500, () => enabled, CancellationToken.None);

        enabled = false;
        occupied.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, discovery.Enumerations);
        Assert.Empty(transports.Opened);
        Assert.Equal(0, controller.Applies);
    }

    [Fact]
    public async Task AGuardRevokedDuringOpenIsCheckedAgainBeforeTheControllerCanWrite()
    {
        var enabled = true;
        var transports = new Transports { OnOpen = () => enabled = false };
        var controller = new Controller();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new Discovery(), transports, controller)
            .ApplyAsync(Peripheral, 500, () => enabled, CancellationToken.None));

        Assert.Single(transports.Opened);
        Assert.Equal(1, transports.Disposals);
        Assert.Equal(0, controller.Applies);
        Assert.Equal(0, transports.Writes);
    }

    [Fact]
    public async Task BatteryAndManualSettingsUseTheSameGateUntilTheBatterySessionIsFinished()
    {
        var gate = new HidOperationGate();
        var discovery = new Discovery();
        var transports = new Transports();
        var battery = new BlockingBattery();
        var monitor = new BatteryMonitor(discovery, transports, [battery], gate);
        var controller = new Controller();
        var service = new PollingRateService(discovery, transports, [controller], gate);
        var refresh = monitor.RefreshAsync(CancellationToken.None);
        await battery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var apply = service.ApplyAsync(Peripheral, 500, () => true, CancellationToken.None);

        try
        {
            Assert.False(apply.IsCompleted);
            Assert.Single(transports.Opened);
            Assert.Equal(0, transports.Writes);
        }
        finally { battery.Release.TrySetResult(true); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        var originalBattery = Assert.Single(monitor.Devices);
        var result = await apply.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Verified);
        Assert.Equal(2, transports.Opened.Count);
        Assert.Equal(1, transports.Writes);
        Assert.Equal(2, transports.Disposals);
        Assert.Same(originalBattery, Assert.Single(monitor.Devices));
    }

    [Fact]
    public async Task FailureAfterASettingWriteIsUnverifiedAndNeverRetried()
    {
        var transports = new Transports();
        var controller = new Controller { FailAfterWrite = true };

        var result = await Service(new Discovery(), transports, controller)
            .ApplyAsync(Peripheral, 500, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Null(result.Info.CurrentRateHz);
        Assert.Contains("could not be verified", result.Detail);
        Assert.Equal(1, controller.Applies);
        Assert.Equal(1, transports.Writes);
    }

    [Theory]
    [InlineData(PollingRateState.Supported, 1000, true)]
    [InlineData(PollingRateState.Failed, 500, true)]
    [InlineData(PollingRateState.Supported, 500, false)]
    public async Task AClaimedVerificationWithoutMatchingReadbackAndCapabilitiesIsRejected(
        PollingRateState state, int actualRate, bool hasSupportedRates)
    {
        var controller = new Controller
        {
            Result = new(true, new(Peripheral.Id, state, actualRate,
                hasSupportedRates ? [125, 500, 1000] : [], "Scripted readback."), "Claimed verified.")
        };

        var result = await Service(new Discovery(), new Transports(), controller)
            .ApplyAsync(Peripheral, 500, () => true, CancellationToken.None);

        Assert.False(result.Verified);
    }

    [Fact]
    public async Task NativeOpenFailureDoesNotReturnAValueOrRetry()
    {
        var transports = new Transports { OpenFailure = new UnauthorizedAccessException("Access denied.") };
        var controller = new Controller();

        var result = await Service(new Discovery(), transports, controller)
            .ApplyAsync(Peripheral, 500, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Null(result.Info.CurrentRateHz);
        Assert.Single(transports.Opened);
        Assert.Equal(0, controller.Applies);
        Assert.Equal(0, transports.Writes);
    }

    private static PollingRateService Service(Discovery discovery, Transports transports, Controller controller) =>
        new(discovery, transports, [controller], new HidOperationGate());

    private sealed class Discovery : IHidDiscovery
    {
        public IReadOnlyList<HidEndpoint> Endpoints { get; set; } = [Endpoint];
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public Action? OnEnumerate { get; init; }
        public int Enumerations { get; private set; }
        public IReadOnlyList<HidEndpoint> Enumerate() { Enumerations++; OnEnumerate?.Invoke(); return Endpoints; }
    }

    private sealed class Transports : IHidTransportFactory
    {
        public List<HidEndpoint> Opened { get; } = [];
        public Action? OnOpen { get; init; }
        public Exception? OpenFailure { get; init; }
        public int Writes { get; set; }
        public int Disposals { get; set; }

        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(endpoint);
            if (OpenFailure is not null) throw OpenFailure;
            OnOpen?.Invoke();
            return ValueTask.FromResult<IHidTransport>(new Transport(endpoint, this));
        }
    }

    private sealed class Transport(HidEndpoint endpoint, Transports owner) : IHidTransport
    {
        public HidEndpoint Endpoint { get; } = endpoint;
        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.Writes++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Controller : IPollingRateController
    {
        public bool Handles { get; init; } = true;
        public bool FailAfterWrite { get; init; }
        public PollingApplyResult? Result { get; init; }
        public int Reads { get; private set; }
        public int Applies { get; private set; }
        public bool CanHandle(HidEndpoint endpoint, PeripheralIdentity device) => Handles;
        public Task<PollingRateInfo> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new PollingRateInfo(device.Id, PollingRateState.Supported, 1000, [125, 500, 1000], "Read back."));
        }
        public async Task<PollingApplyResult> ApplyAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, int rateHz, Func<bool> canApply, CancellationToken cancellationToken)
        {
            Applies++;
            cancellationToken.ThrowIfCancellationRequested();
            if (!canApply()) throw new OperationCanceledException(cancellationToken);
            await transport.WriteAsync([0x01], cancellationToken);
            if (FailAfterWrite) throw new IOException("Readback failed after the write.");
            return Result ?? new(true,
                new(device.Id, PollingRateState.Supported, rateHz, [125, 500, 1000], "Matching readback."), "Verified by readback.");
        }
    }

    private sealed class BlockingBattery : IBatteryProvider
    {
        public string Name => "Blocking test provider";
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanHandle(HidEndpoint endpoint) => true;
        public async Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            DateTimeOffset observedAt, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return [new(Peripheral, BatteryReading.Current(67, ChargingState.Discharging, observedAt), ConnectionState.Connected)];
        }
    }
}
