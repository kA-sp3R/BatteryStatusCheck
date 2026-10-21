using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class PollingHostModeServiceTests
{
    private static readonly HidEndpoint Endpoint =
        new("fake-logitech-receiver", 0x046D, 0xC539, 0xFF00, 1, 20, 20, 0, "receiver-a");
    private static readonly PeripheralIdentity Peripheral =
        new("receiver-a/logitech/1/4081/12345678", "Test mouse", DeviceVendor.Logitech, "receiver-a", "4081");

    [Fact]
    public async Task DetectReportsTheAdditionalOptInRequirementWithoutChangingModeOrRate()
    {
        var transport = new Transports();
        var controller = new HostController();

        var result = await Service(transport, controller).ReadAsync(Peripheral, CancellationToken.None);

        Assert.Equal(PollingRateState.RequiresHostMode, result.State);
        Assert.Null(result.CurrentRateHz);
        Assert.Equal(new[] { 500, 1000 }, result.SupportedRatesHz);
        Assert.Equal(1, controller.Reads);
        Assert.Equal(0, controller.StandardApplies);
        Assert.Equal(0, controller.HostModeApplies);
        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task ApplyWithoutTheSecondaryOptInNeverCallsTheExtendedController()
    {
        var transport = new Transports();
        var controller = new HostController();

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.RequiresHostMode, result.Info.State);
        Assert.Equal(1, controller.StandardApplies);
        Assert.Equal(0, controller.HostModeApplies);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task AProvidedButFalseSecondaryGuardDoesNotAuthorizeAnyModeOrRateWrite()
    {
        var transport = new Transports();
        var controller = new HostController();

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None, () => false);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.RequiresHostMode, result.Info.State);
        Assert.Equal(0, controller.StandardApplies);
        Assert.Equal(1, controller.HostModeApplies);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ExplicitSecondaryOptInForwardsBothLiveGuardsToTheExtendedController()
    {
        var transport = new Transports();
        var controller = new HostController();
        Func<bool> primary = () => true;
        Func<bool> secondary = () => true;

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, primary, CancellationToken.None, secondary);

        Assert.True(result.Verified);
        Assert.Equal(PollingRateState.Supported, result.Info.State);
        Assert.Equal(500, result.Info.CurrentRateHz);
        Assert.Same(primary, controller.PrimaryGuard);
        Assert.Same(secondary, controller.SecondaryGuard);
        Assert.Equal(0, controller.StandardApplies);
        Assert.Equal(1, controller.HostModeApplies);
        Assert.Equal(new byte[] { 1, 2 }, transport.Writes);
    }

    [Fact]
    public async Task RevokingSecondaryPermissionWhileOpeningIsObservedByTheForwardedGuard()
    {
        var allowed = true;
        var transport = new Transports { OnOpen = () => allowed = false };
        var controller = new HostController();
        Func<bool> secondary = () => allowed;

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None, secondary);

        Assert.False(result.Verified);
        Assert.Same(secondary, controller.SecondaryGuard);
        Assert.Equal(1, controller.HostModeApplies);
        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task RevokingSecondaryPermissionImmediatelyBeforeTheModeWritePreventsTheWrite()
    {
        var allowed = true;
        var transport = new Transports();
        var controller = new HostController { BeforeModeWrite = () => allowed = false };

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None, () => allowed);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.RequiresHostMode, result.Info.State);
        Assert.Empty(transport.Writes);
        Assert.Equal(1, controller.HostModeApplies);
    }

    [Fact]
    public async Task RevokingPrimaryPermissionInsideTheControllerCannotUseAnEarlierServiceCheck()
    {
        var allowed = true;
        var transport = new Transports();
        var controller = new HostController { BeforeModeWrite = () => allowed = false };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => allowed, CancellationToken.None, () => true));

        Assert.Empty(transport.Writes);
        Assert.Equal(1, controller.HostModeApplies);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task SecondaryRevocationAfterModeChangeLeavesTheRateUnverifiedWithoutRetry()
    {
        var allowed = true;
        var transport = new Transports();
        var controller = new HostController { BeforeRateWrite = () => allowed = false };

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None, () => allowed);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Null(result.Info.CurrentRateHz);
        Assert.Equal(new byte[] { 1 }, transport.Writes);
        Assert.Equal(1, controller.HostModeApplies);
    }

    [Fact]
    public async Task RequiresHostModeCannotBeClaimedAsVerifiedEvenWithAMatchingNumericRate()
    {
        var transport = new Transports();
        var controller = new HostController
        {
            ForcedResult = new(true, new(Peripheral.Id, PollingRateState.RequiresHostMode,
                500, [500, 1000], "Unconfirmed software mode."), "Controller claims verification.")
        };

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None, () => true);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.RequiresHostMode, result.Info.State);
        Assert.Contains("could not be verified", result.Detail);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task AControllerWithoutTheExtendedInterfaceKeepsItsOrdinaryApplyPath()
    {
        var transport = new Transports();
        var controller = new OrdinaryController();
        var secondaryChecks = 0;

        var result = await Service(transport, controller).ApplyAsync(
            Peripheral, 500, () => true, CancellationToken.None, () => { secondaryChecks++; return false; });

        Assert.True(result.Verified);
        Assert.Equal(1, controller.Applies);
        Assert.Equal(0, secondaryChecks);
        Assert.Equal(new byte[] { 2 }, transport.Writes);
    }

    private static PollingRateService Service(Transports transports, IPollingRateController controller) =>
        new(new Discovery(), transports, [controller], new HidOperationGate());

    private static PollingRateInfo Required() => new(Peripheral.Id, PollingRateState.RequiresHostMode,
        null, [500, 1000], "A separate software-mode opt-in is required.");

    private static PollingApplyResult Verified(int rate) => new(true,
        new(Peripheral.Id, PollingRateState.Supported, rate, [500, 1000], "Matching configured rate."), "Readback verified.");

    private sealed class Discovery : IHidDiscovery
    {
        public IReadOnlyList<HidEndpoint> Enumerate() => [Endpoint];
    }

    private sealed class Transports : IHidTransportFactory
    {
        public Action? OnOpen { get; init; }
        public List<byte> Writes { get; } = [];
        public int Disposals { get; set; }
        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            owner.Writes.Add(report[0]);
            return ValueTask.CompletedTask;
        }
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class HostController : IHostModePollingController
    {
        public Action? BeforeModeWrite { get; init; }
        public Action? BeforeRateWrite { get; init; }
        public PollingApplyResult? ForcedResult { get; init; }
        public Func<bool>? PrimaryGuard { get; private set; }
        public Func<bool>? SecondaryGuard { get; private set; }
        public int Reads { get; private set; }
        public int StandardApplies { get; private set; }
        public int HostModeApplies { get; private set; }
        public bool CanHandle(HidEndpoint endpoint, PeripheralIdentity device) => endpoint == Endpoint && device == Peripheral;
        public Task<PollingRateInfo> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Required());
        }
        public Task<PollingApplyResult> ApplyAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, int rateHz, Func<bool> canApply, CancellationToken cancellationToken)
        {
            StandardApplies++;
            return Task.FromResult(new PollingApplyResult(false, Required(), "Separate opt-in was not supplied."));
        }
        public async Task<PollingApplyResult> ApplyWithHostModeSwitchAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, int rateHz, Func<bool> canApply, Func<bool> canSwitchToHostMode,
            CancellationToken cancellationToken)
        {
            HostModeApplies++;
            PrimaryGuard = canApply;
            SecondaryGuard = canSwitchToHostMode;
            if (ForcedResult is not null) return ForcedResult;
            BeforeModeWrite?.Invoke();
            EnsurePrimary(canApply, cancellationToken);
            if (!canSwitchToHostMode()) return new(false, Required(), "Mode change is not authorized.");
            EnsurePrimary(canApply, cancellationToken);
            await transport.WriteAsync([1], cancellationToken);
            BeforeRateWrite?.Invoke();
            EnsurePrimary(canApply, cancellationToken);
            if (!canSwitchToHostMode()) return new(false,
                new(device.Id, PollingRateState.Failed, null, [], "Mode may already have changed."),
                "Secondary permission was revoked before the rate write.");
            EnsurePrimary(canApply, cancellationToken);
            await transport.WriteAsync([2], cancellationToken);
            return Verified(rateHz);
        }
    }

    private sealed class OrdinaryController : IPollingRateController
    {
        public int Applies { get; private set; }
        public bool CanHandle(HidEndpoint endpoint, PeripheralIdentity device) => endpoint == Endpoint && device == Peripheral;
        public Task<PollingRateInfo> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, CancellationToken cancellationToken) => Task.FromResult(Verified(1000).Info);
        public async Task<PollingApplyResult> ApplyAsync(HidEndpoint endpoint, IHidTransport transport,
            PeripheralIdentity device, int rateHz, Func<bool> canApply, CancellationToken cancellationToken)
        {
            Applies++;
            EnsurePrimary(canApply, cancellationToken);
            await transport.WriteAsync([2], cancellationToken);
            return Verified(rateHz);
        }
    }

    private static void EnsurePrimary(Func<bool> guard, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!guard()) throw new OperationCanceledException(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
