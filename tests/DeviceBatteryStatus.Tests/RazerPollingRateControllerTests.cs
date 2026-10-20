using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerPollingRateControllerTests
{
    [Theory]
    [InlineData(0x007B, true)]
    [InlineData(0x0088, true)]
    [InlineData(0x00A6, true)]
    [InlineData(0x00DF, true)]
    [InlineData(0x009F, false)]
    [InlineData(0x00B3, false)]
    [InlineData(0x00BF, false)]
    [InlineData(0x00C1, false)]
    [InlineData(0x025C, false)]
    [InlineData(0xFFFF, false)]
    public void OnlyDocumentedOrdinaryWirelessReceiverMappingsAreHandled(ushort productId, bool expected)
    {
        var endpoint = Endpoint(productId);
        Assert.Equal(expected, new RazerPollingRateController().CanHandle(endpoint, Device(endpoint)));
    }

    [Fact]
    public void IdentityAndCollectionMustBelongToTheSameReceiver()
    {
        var endpoint = Endpoint();
        var device = Device(endpoint);
        var controller = new RazerPollingRateController();
        Assert.False(controller.CanHandle(endpoint, device with { ReceiverId = "receiver-b" }));
        Assert.False(controller.CanHandle(endpoint, device with { Id = "unrelated-device" }));
        Assert.False(controller.CanHandle(endpoint, device with { ModelId = "1532:007B" }));
        Assert.False(controller.CanHandle(endpoint with { DevicePath = "usb&mi_01" }, device));
        Assert.False(controller.CanHandle(endpoint with { DevicePath = "usb&mi_G0" }, device));
        Assert.False(controller.CanHandle(endpoint with { FeatureReportLength = 90 }, device));
    }

    [Fact]
    public async Task UnsupportedHyperPollingAssociationSendsNoProtocolCommands()
    {
        var endpoint = Endpoint(0x00B3);
        await using var transport = new FeatureTransport(endpoint);
        var controller = new RazerPollingRateController();
        var read = await controller.ReadAsync(endpoint, transport, Device(endpoint), CancellationToken.None);
        var apply = await controller.ApplyAsync(endpoint, transport, Device(endpoint), 1000, () => true, CancellationToken.None);
        Assert.Equal(PollingRateState.Unsupported, read.State);
        Assert.False(apply.Verified);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task OpenTransportMustMatchTheSelectedReceiver()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint with { ReceiverId = "receiver-b" });
        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport, Device(endpoint), CancellationToken.None);
        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task BasiliskUltimateUsesDifferentPollingGetAndSetTransactions()
    {
        var endpoint = Endpoint(0x0088);
        await using var transport = new FeatureTransport(endpoint);
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 1000, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(1000, result.Info.CurrentRateHz);
        Assert.Equal(new byte[] { 0xFF, 0x1F, 0xFF }, transport.Commands.Select(command => command[2]).ToArray());
        Assert.Equal(new byte[] { 0x85, 0x05, 0x85 }, transport.Commands.Select(command => command[8]).ToArray());
        Assert.Equal(1, transport.SettingWrites);
    }

    [Fact]
    public async Task InvalidRateIsRejectedAfterFreshReadWithoutASettingWrite()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint);
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 250, () => throw new InvalidOperationException("No permission check is needed for an invalid rate."),
            CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(new[] { 125, 500, 1000 }, result.Info.SupportedRatesHz);
        Assert.Single(transport.Commands);
        Assert.Equal(0, transport.SettingWrites);
    }

    [Fact]
    public async Task RevokedOptInAfterFreshReadPreventsTheSettingWrite()
    {
        var endpoint = Endpoint();
        var enabled = true;
        await using var transport = new FeatureTransport(endpoint)
        {
            AfterReadReply = () => enabled = false
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 1000, () => enabled, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(0, transport.SettingWrites);
        Assert.Single(transport.Commands);
    }

    [Fact]
    public async Task CancellationDuringTheLastPermissionCheckPreventsTheSettingWrite()
    {
        var endpoint = Endpoint();
        using var cancellation = new CancellationTokenSource();
        await using var transport = new FeatureTransport(endpoint);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RazerPollingRateController().ApplyAsync(endpoint,
            transport, Device(endpoint), 1000, () => { cancellation.Cancel(); return true; }, cancellation.Token));
        Assert.Equal(0, transport.SettingWrites);
    }

    [Fact]
    public async Task AlreadyReportedRateNeedsNoSettingWrite()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint);
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 500, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(0, transport.SettingWrites);
        Assert.Single(transport.Commands);
    }

    [Fact]
    public async Task AcknowledgementAloneDoesNotVerifyTheRequestedRate()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint) { UpdateRateOnWrite = false };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 1000, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(500, result.Info.CurrentRateHz);
        Assert.Equal(1, transport.SettingWrites);
        Assert.Equal(3, transport.Commands.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedReadOrUnknownRateReturnsFailedRatherThanEscapingTheController(bool malformedFrame)
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint)
        {
            ResponseOverride = request => malformedFrame ? new byte[12] : RazerPollingProtocolTests.Reply(request, 0x04)
        };
        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport, Device(endpoint), CancellationToken.None);
        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Null(result.CurrentRateHz);
        Assert.Empty(result.SupportedRatesHz);
    }

    [Fact]
    public async Task ReceiverTimeoutDoesNotProveThatThePeripheralDisconnected()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint)
        {
            ResponseOverride = request => RazerPollingProtocolTests.Reply(request, 2, 4)
        };
        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport, Device(endpoint), CancellationToken.None);
        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Null(result.CurrentRateHz);
    }

    [Fact]
    public async Task MalformedWriteAcknowledgementPreservesTheMayHaveChangedWarningWithoutRetry()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint)
        {
            ResponseOverride = request => request[8] == 0x05 ? new byte[91] : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 1000, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Contains("may have changed", result.Detail);
        Assert.Equal(1, transport.SettingWrites);
        Assert.Equal(2, transport.Commands.Count);
    }

    [Fact]
    public async Task TimedOutWriteAcknowledgementIsNotRetriedOrFollowedByAnotherCommand()
    {
        var endpoint = Endpoint();
        await using var transport = new FeatureTransport(endpoint)
        {
            ResponseOverride = request => request[8] == 0x05 ? throw new TimeoutException("Session closed.") : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 1000, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Contains("may have changed", result.Detail);
        Assert.Equal(1, transport.SettingWrites);
        Assert.Equal(2, transport.Commands.Count);
    }

    [Fact]
    public async Task CancellationAfterTheSettingWriteRemainsExplicitlyUnverified()
    {
        var endpoint = Endpoint();
        using var cancellation = new CancellationTokenSource();
        await using var transport = new FeatureTransport(endpoint) { AfterSettingWrite = cancellation.Cancel };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport,
            Device(endpoint), 1000, () => true, cancellation.Token);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Contains("may have changed", result.Detail);
        Assert.Equal(1, transport.SettingWrites);
        Assert.Equal(2, transport.Commands.Count);
    }

    private static HidEndpoint Endpoint(ushort productId = 0x00A6) =>
        new("fake-usb-vid_1532&mi_00", 0x1532, productId, 0xFF00, 1, 0, 0, 91, "receiver-a");

    private static PeripheralIdentity Device(HidEndpoint endpoint) =>
        new($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}", "Source model", DeviceVendor.Razer,
            endpoint.ReceiverId, $"1532:{endpoint.ProductId:X4}");

    private sealed class FeatureTransport(HidEndpoint endpoint) : IHidTransport
    {
        private byte[]? request;
        private byte currentEncoding = 2;
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Commands { get; } = [];
        public int SettingWrites { get; private set; }
        public bool UpdateRateOnWrite { get; init; } = true;
        public Func<byte[], byte[]?>? ResponseOverride { get; init; }
        public Action? AfterReadReply { get; init; }
        public Action? AfterSettingWrite { get; init; }

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = report.ToArray();
            Commands.Add(request);
            if (report[8] == RazerPollingProtocol.WriteCommand)
            {
                SettingWrites++;
                if (UpdateRateOnWrite) currentEncoding = report[9];
                AfterSettingWrite?.Invoke();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = request ?? throw new InvalidOperationException("No feature command sent.");
            var reply = ResponseOverride?.Invoke(pending) ?? RazerPollingProtocolTests.Reply(pending,
                pending[8] == RazerPollingProtocol.ReadCommand ? currentEncoding : pending[9]);
            if (pending[8] == RazerPollingProtocol.ReadCommand) AfterReadReply?.Invoke();
            return ValueTask.FromResult(reply);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Only feature reports are expected.");

        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Only feature reports are expected.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
