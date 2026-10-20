using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerHighRateTests
{
    private static readonly int[] Rates = [125, 500, 1000, 2000, 4000, 8000];

    [Fact]
    public void HighRateGetUsesItsOwnCommandAndOneByteRequestPayload()
    {
        var request = RazerPollingProtocol.CreateHighRateReadRequest(0x1F);
        Assert.Equal(91, request.Length);
        Assert.Equal(0, request[0]);
        Assert.Equal(0x1F, request[2]);
        Assert.Equal(1, request[6]);
        Assert.Equal(0, request[7]);
        Assert.Equal(0xC0, request[8]);
        Assert.Equal(0, request[9]);
        Assert.Equal(Checksum(request), request[89]);
    }

    [Theory]
    [InlineData(0x40, 125)]
    [InlineData(0x10, 500)]
    [InlineData(0x08, 1000)]
    [InlineData(0x04, 2000)]
    [InlineData(0x02, 4000)]
    [InlineData(0x01, 8000)]
    public void HighRateCodecUsesArgumentOneAndAcceptsSupportedReplyFraming(byte encoding, int rate)
    {
        foreach (byte size in new byte[] { 1, 2 })
        {
            var reply = Reply(RazerPollingProtocol.CreateHighRateReadRequest(0x1F), encoding, size);
            Assert.Equal(rate, RazerPollingProtocol.ParseHighRateReadReply(reply, 0x1F));
        }
        foreach (byte phase in new byte[] { 0, 1 })
        {
            var request = RazerPollingProtocol.CreateHighRateWriteRequest(0x1F, phase, rate);
            Assert.Equal(2, request[6]);
            Assert.Equal(0, request[7]);
            Assert.Equal(0x40, request[8]);
            Assert.Equal(phase, request[9]);
            Assert.Equal(encoding, request[10]);
            Assert.Equal(Checksum(request), request[89]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(16000)]
    public void HighRateWritesRejectRatesOutsideDocumentedProfile(int rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerPollingProtocol.CreateHighRateWriteRequest(0x1F, 0, rate));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void HighRateGetRejectsUnsupportedPayloadLengthsWithValidChecksum(byte size)
    {
        var reply = Reply(RazerPollingProtocol.CreateHighRateReadRequest(0x1F), 1, size);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseHighRateReadReply(reply, 0x1F));
    }

    [Fact]
    public void HighRateWritesRejectAnUnknownPhase() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerPollingProtocol.CreateHighRateWriteRequest(0x1F, 2, 8000));

    [Theory]
    [InlineData(0)]
    [InlineData(0x20)]
    [InlineData(0x80)]
    public void UnknownHighRateEncodingNeverFallsBackToTheOrdinaryRateByte(byte encoding)
    {
        var reply = Reply(RazerPollingProtocol.CreateHighRateReadRequest(0x1F), encoding);
        reply[9] = 1; // This is a valid ordinary encoding, but is not the high-rate field.
        reply[89] = Checksum(reply);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseHighRateReadReply(reply, 0x1F));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void HighRateAcknowledgementsRejectMismatchedFramingEvenWithAValidChecksum(int offset)
    {
        var reply = Reply(RazerPollingProtocol.CreateHighRateWriteRequest(0x1F, 1, 8000), 1);
        reply[offset] ^= 1;
        reply[89] = Checksum(reply);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ValidateHighRateWriteReply(reply, 0x1F));
    }

    [Fact]
    public void HighRateReplyChecksumAndCompleteFrameAreRequired()
    {
        var reply = Reply(RazerPollingProtocol.CreateHighRateReadRequest(0x1F), 1);
        reply[89] ^= 1;
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseHighRateReadReply(reply, 0x1F));
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseHighRateReadReply(reply[..90], 0x1F));
    }

    [Fact]
    public void OrdinaryAndHighRateRepliesCannotBeInterchanged()
    {
        var ordinary = RazerPollingProtocolTests.Reply(RazerPollingProtocol.CreateReadRequest(0x1F), 1);
        var high = Reply(RazerPollingProtocol.CreateHighRateReadRequest(0x1F), 1);
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseHighRateReadReply(ordinary, 0x1F));
        Assert.Throws<InvalidDataException>(() => RazerPollingProtocol.ParseReadReply(high, 0x1F));
    }

    [Theory]
    [InlineData(0x009F, 0x1F)]
    [InlineData(0x00C1, 0x1F)]
    [InlineData(0x00BF, 0x1F)]
    [InlineData(0x00B3, 0xFF)]
    public async Task HighRateApplyUsesTheDocumentedTwoPhasesAndFreshEffectiveRateReadback(ushort pid, byte secondTransaction)
    {
        var endpoint = Endpoint(pid);
        await using var transport = new HighTransport(endpoint);
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(8000, result.Info.CurrentRateHz);
        Assert.Equal(Rates, result.Info.SupportedRatesHz);
        Assert.Equal(new byte[] { 0xC0, 0x40, 0x40, 0xC0 }, transport.Commands.Select(report => report[8]).ToArray());
        Assert.Equal(new byte[] { 0x1F, 0x1F, secondTransaction, 0x1F }, transport.Commands.Select(report => report[2]).ToArray());
        Assert.Equal(new byte[] { 0, 1 }, transport.Commands.Where(report => report[8] == 0x40).Select(report => report[9]).ToArray());
        Assert.Equal(2, transport.SettingWrites);
    }

    [Fact]
    public async Task GenericReceiverControlDoesNotInventBatteryOrPairedMouseIdentity()
    {
        var endpoint = Endpoint(0x00B3);
        await using var transport = new HighTransport(endpoint);
        var snapshot = Assert.Single(await new RazerBatteryProvider().ReadAsync(endpoint, transport,
            DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(ReadingState.Unsupported, snapshot.Battery.State);
        Assert.Null(snapshot.Battery.Percentage);
        Assert.Equal(ConnectionState.Unknown, snapshot.Connection);
        Assert.Empty(transport.Commands);

        var info = await new RazerPollingRateController().ReadAsync(endpoint, transport, snapshot.Device, CancellationToken.None);
        Assert.Equal(PollingRateState.Supported, info.State);
        Assert.Equal(snapshot.Device.Id, info.DeviceId);
        Assert.Equal(Rates, info.SupportedRatesHz);
        Assert.Contains("receiver", info.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paired", info.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", info.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, transport.SettingWrites);
    }

    [Fact]
    public async Task GenericReceiverControlStillRequiresExactIdentityCollectionAndOpenSession()
    {
        var endpoint = Endpoint(0x00B3);
        var identity = Device(endpoint);
        var controller = new RazerPollingRateController();
        Assert.False(controller.CanHandle(endpoint, identity with { ReceiverId = "other-receiver" }));
        Assert.False(controller.CanHandle(endpoint, identity with { ModelId = "1532:00C1" }));
        Assert.False(controller.CanHandle(endpoint with { DevicePath = "usb&mi_01" }, identity));
        await using var transport = new HighTransport(endpoint with { DevicePath = "different-usb&mi_00" });
        var info = await controller.ReadAsync(endpoint, transport, identity, CancellationToken.None);
        Assert.Equal(PollingRateState.Failed, info.State);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task AlreadyReportedHighRateStillAppliesBothRequiredPhases()
    {
        var endpoint = Endpoint(0x00BF);
        await using var transport = new HighTransport(endpoint);
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            1000, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(1000, result.Info.CurrentRateHz);
        Assert.Equal(2, transport.SettingWrites);
        Assert.Equal(new byte[] { 0xC0, 0x40, 0x40, 0xC0 }, transport.Commands.Select(report => report[8]).ToArray());
    }

    [Fact]
    public async Task AnExplicitSameRateApplyCanCompleteTheSequenceAfterPhaseZeroChangedTheReportedRate()
    {
        var endpoint = Endpoint(0x00B3);
        var failFirstPhaseOneAcknowledgement = true;
        await using var transport = new HighTransport(endpoint)
        {
            UpdateRateOnPhaseZero = true,
            ResponseOverride = request =>
            {
                if (request[8] != 0x40 || request[9] != 1 || !failFirstPhaseOneAcknowledgement) return null;
                failFirstPhaseOneAcknowledgement = false;
                return Reply(request, request[10], status: 3);
            }
        };
        var controller = new RazerPollingRateController();
        var identity = Device(endpoint);

        var interrupted = await controller.ApplyAsync(endpoint, transport, identity,
            8000, () => true, CancellationToken.None);
        AssertUnverified(interrupted, transport, 2);
        Assert.Equal(3, transport.Commands.Count); // The failed operation did not retry or query again.

        var detected = await controller.ReadAsync(endpoint, transport, identity, CancellationToken.None);
        Assert.Equal(8000, detected.CurrentRateHz); // Effective rate cannot establish that both phases completed.
        Assert.Equal(2, transport.SettingWrites);
        Assert.Equal(4, transport.Commands.Count);

        var reapplied = await controller.ApplyAsync(endpoint, transport, identity,
            8000, () => true, CancellationToken.None);
        Assert.True(reapplied.Verified);
        Assert.Equal(8000, reapplied.Info.CurrentRateHz);
        Assert.Equal(4, transport.SettingWrites);
        Assert.Equal(8, transport.Commands.Count);
        Assert.Equal(new byte[] { 0xC0, 0x40, 0x40, 0xC0 },
            transport.Commands.Skip(4).Select(report => report[8]).ToArray());
    }

    [Theory]
    [InlineData(250)]
    [InlineData(16000)]
    public async Task AHighRateReceiverRejectsRatesOutsideItsDocumentedProfileWithoutWriting(int rate)
    {
        var endpoint = Endpoint(0x00C1);
        await using var transport = new HighTransport(endpoint);
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            rate, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(Rates, result.Info.SupportedRatesHz);
        Assert.Equal(0, transport.SettingWrites);
        Assert.Single(transport.Commands);
    }

    [Fact]
    public async Task AReadbackTimeoutAfterBothAcknowledgementsIsUnverifiedWithoutFallbackQueries()
    {
        var endpoint = Endpoint(0x00B3);
        var reads = 0;
        await using var transport = new HighTransport(endpoint)
        {
            ResponseOverride = request => request[8] == 0xC0 && ++reads == 2
                ? throw new TimeoutException("The readback session is closed.") : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, CancellationToken.None);
        AssertUnverified(result, transport, 2);
        Assert.Equal(4, transport.Commands.Count);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MalformedPhaseAcknowledgementStopsTheSequenceWithoutRetry(int failedPhase)
    {
        var endpoint = Endpoint(0x00C1);
        await using var transport = new HighTransport(endpoint)
        {
            ResponseOverride = request => request[8] == 0x40 && request[9] == failedPhase ? new byte[91] : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, CancellationToken.None);
        AssertUnverified(result, transport, failedPhase + 1);
        Assert.Equal(failedPhase + 2, transport.Commands.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task APhaseTimeoutNeverReusesTheSessionOrRetriesAnySetting(int failedPhase)
    {
        var endpoint = Endpoint(0x00B3);
        await using var transport = new HighTransport(endpoint)
        {
            ResponseOverride = request => request[8] == 0x40 && request[9] == failedPhase
                ? throw new TimeoutException("The native session is closed.") : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, CancellationToken.None);
        AssertUnverified(result, transport, failedPhase + 1);
        Assert.Equal(failedPhase + 2, transport.Commands.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AnUnsuccessfulFirstAcknowledgementCannotAuthorizeTheSecondPhase(byte status)
    {
        var endpoint = Endpoint(0x009F);
        await using var transport = new HighTransport(endpoint)
        {
            ResponseOverride = request => request[8] == 0x40 ? Reply(request, request[10], status: status) : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, CancellationToken.None);
        AssertUnverified(result, transport, 1);
        Assert.Equal(2, transport.Commands.Count);
    }

    [Fact]
    public async Task RevokingPermissionAfterPhaseZeroPreventsPhaseOneAndReportsAnUncertainChange()
    {
        var endpoint = Endpoint(0x00BF);
        var enabled = true;
        await using var transport = new HighTransport(endpoint)
        {
            AfterReply = request => { if (request[8] == 0x40 && request[9] == 0) enabled = false; }
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => enabled, CancellationToken.None);
        AssertUnverified(result, transport, 1);
        Assert.Equal(2, transport.Commands.Count);
    }

    [Fact]
    public async Task CancellationInsideTheSecondPhaseGuardPreventsItsWrite()
    {
        var endpoint = Endpoint(0x00C1);
        using var cancellation = new CancellationTokenSource();
        await using var transport = new HighTransport(endpoint);
        bool Permit()
        {
            if (transport.SettingWrites == 1) cancellation.Cancel();
            return true;
        }
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, Permit, cancellation.Token);
        AssertUnverified(result, transport, 1);
        Assert.Equal(2, transport.Commands.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancellationAfterEitherWriteCannotClaimVerifiedOrSendMoreCommands(int canceledPhase)
    {
        var endpoint = Endpoint(0x00C1);
        using var cancellation = new CancellationTokenSource();
        await using var transport = new HighTransport(endpoint)
        {
            AfterFeatureWrite = request => { if (request[8] == 0x40 && request[9] == canceledPhase) cancellation.Cancel(); }
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, cancellation.Token);
        AssertUnverified(result, transport, canceledPhase + 1);
        Assert.Equal(canceledPhase + 2, transport.Commands.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcknowledgementsWithoutUndocumentedPayloadEchoStillRequireMatchingReadback(bool readbackMatches)
    {
        var endpoint = Endpoint(0x00C1);
        await using var transport = new HighTransport(endpoint)
        {
            UpdateRateOnPhaseOne = readbackMatches,
            ResponseOverride = request => request[8] == 0x40 ? Reply(request, 0, zeroAcknowledgementPayload: true) : null
        };
        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Device(endpoint),
            8000, () => true, CancellationToken.None);
        Assert.Equal(readbackMatches, result.Verified);
        Assert.Equal(readbackMatches ? 8000 : 1000, result.Info.CurrentRateHz);
        Assert.Equal(2, transport.SettingWrites);
        Assert.Equal(4, transport.Commands.Count);
    }

    private static void AssertUnverified(PollingApplyResult result, HighTransport transport, int settingWrites)
    {
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Null(result.Info.CurrentRateHz);
        Assert.Contains("may have changed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(settingWrites, transport.SettingWrites);
    }

    private static HidEndpoint Endpoint(ushort pid) =>
        new("fake-usb-vid_1532&mi_00", 0x1532, pid, 0xFF00, 1, 0, 0, 91, "receiver-high");

    private static PeripheralIdentity Device(HidEndpoint endpoint) =>
        new($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}", "Synthetic receiver", DeviceVendor.Razer,
            endpoint.ReceiverId, $"1532:{endpoint.ProductId:X4}");

    private static byte[] Reply(byte[] request, byte encoding, byte size = 2, byte status = 2,
        bool zeroAcknowledgementPayload = false)
    {
        var reply = request.ToArray();
        reply[1] = status;
        reply[6] = size;
        if (request[8] == 0xC0) reply[9] = 0x7F; // The effective rate is argument 1, not argument 0.
        if (zeroAcknowledgementPayload) reply[9] = 0;
        reply[10] = encoding;
        reply[89] = Checksum(reply);
        return reply;
    }

    private static byte Checksum(byte[] report)
    {
        byte checksum = 0;
        for (var offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        return checksum;
    }

    private sealed class HighTransport(HidEndpoint endpoint) : IHidTransport
    {
        private byte[]? request;
        private byte currentEncoding = 0x08;
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Commands { get; } = [];
        public int SettingWrites { get; private set; }
        public bool UpdateRateOnPhaseZero { get; init; }
        public bool UpdateRateOnPhaseOne { get; init; } = true;
        public Func<byte[], byte[]?>? ResponseOverride { get; init; }
        public Action<byte[]>? AfterReply { get; init; }
        public Action<byte[]>? AfterFeatureWrite { get; init; }

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = report.ToArray();
            Commands.Add(request);
            if (request[8] == 0x40)
            {
                SettingWrites++;
                if (request[9] == 0 && UpdateRateOnPhaseZero) currentEncoding = request[10];
                if (request[9] == 1 && UpdateRateOnPhaseOne) currentEncoding = request[10];
            }
            AfterFeatureWrite?.Invoke(request);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = request ?? throw new InvalidOperationException("No feature request was sent.");
            var reply = ResponseOverride?.Invoke(pending) ?? Reply(pending,
                pending[8] == 0xC0 ? currentEncoding : pending[10]);
            AfterReply?.Invoke(pending);
            return ValueTask.FromResult(reply);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
