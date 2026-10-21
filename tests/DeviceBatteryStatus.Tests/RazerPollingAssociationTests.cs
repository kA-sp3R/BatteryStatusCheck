using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class RazerPollingAssociationTests
{
    [Theory]
    [InlineData(0x00C1)]
    [InlineData(0xBEEF)] // A reported product need not have a guessed model name.
    public async Task AChildBoundApplyRequiresTheSameUniqueWirelessProductThroughoutBothPhases(ushort productId)
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint) { ChildProduct = productId };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint, productId),
            8000, () => true, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(8000, result.Info.CurrentRateHz);
        Assert.Equal(2, transport.SettingWrites);
        Assert.Equal(5, transport.AssociationReads);
        Assert.Equal(productId == 0xBEEF ? 5 : 0, transport.RoleReads);
        Assert.Equal(productId == 0xBEEF
                ? new byte[] { 0xBF, 0xC5, 0xC0, 0xBF, 0xC5, 0xBF, 0xC5, 0x40, 0xBF, 0xC5, 0x40, 0xC0, 0xBF, 0xC5 }
                : new byte[] { 0xBF, 0xC0, 0xBF, 0xBF, 0x40, 0xBF, 0x40, 0xC0, 0xBF },
            transport.Commands.Select(request => request[8]).ToArray());
        Assert.Contains($"1532:{productId:X4}", result.Detail);
        Assert.Contains("receiver profile", result.Detail);
    }

    [Theory]
    [InlineData(0x025C)]
    [InlineData(0x0271)]
    [InlineData(0x0290)]
    public async Task AReportedKeyboardCannotReceiveMousePollingCommands(ushort productId)
    {
        var endpoint = Endpoint();
        var child = Child(endpoint, productId);
        var controller = new RazerPollingRateController();
        await using var transport = new AssociationTransport(endpoint) { ChildProduct = productId };

        Assert.False(controller.CanHandle(endpoint, child));
        var result = await controller.ApplyAsync(endpoint, transport, child, 8000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Empty(transport.Commands);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    public async Task AnUnknownProductMustKeepReportingTheMouseRoleThroughoutTheApply(int changedAt, int expectedWrites)
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0xBEEF,
            RoleOverride = (index, request) => index == changedAt ? PrimaryDevice(request, 2, 0xBEEF) : null
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint, 0xBEEF),
            8000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Equal(expectedWrites, transport.SettingWrites);
        Assert.Equal(changedAt, transport.RoleReads);
        Assert.Equal(0xC5, transport.Commands[^1][8]);
        if (expectedWrites > 0) Assert.Contains("may have changed", result.Detail);
    }

    [Fact]
    public async Task AnUnknownProductCannotUseAnotherPrimaryProductsMouseRole()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0xBEEF,
            RoleOverride = (_, request) => PrimaryDevice(request, 1, 0x00C1)
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport,
            Child(endpoint, 0xBEEF), CancellationToken.None);

        Assert.Equal(PollingRateState.Disconnected, result.State);
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Theory]
    [InlineData(3, PollingRateState.Failed)]
    [InlineData(4, PollingRateState.Failed)]
    [InlineData(5, PollingRateState.Unsupported)]
    public async Task AnUnsuccessfulUnknownRoleQueryStopsBeforeAnyRateCommand(byte status, PollingRateState expected)
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0xBEEF,
            RoleOverride = (_, request) => DeviceListStatus(request, status)
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport,
            Child(endpoint, 0xBEEF), CancellationToken.None);

        Assert.Equal(expected, result.State);
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.Equal(0, transport.SettingWrites);
    }

    [Fact]
    public async Task ANativeUnknownRoleTimeoutEndsTheSessionWithoutReuse()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0xBEEF,
            RoleOverride = (_, _) => throw new TimeoutException("Session closed.")
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport,
            Child(endpoint, 0xBEEF), CancellationToken.None);

        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Fact]
    public async Task ACorruptUnknownRoleReplyCannotAuthorizeAMouseCommand()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0xBEEF,
            RoleOverride = (_, request) =>
            {
                var reply = PrimaryDevice(request, 1, 0xBEEF);
                reply[89] ^= 1;
                return reply;
            }
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport,
            Child(endpoint, 0xBEEF), CancellationToken.None);

        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Theory]
    [InlineData(0x00A6, 0x1F, 0x1F)]
    [InlineData(0x007D, 0x3F, 0x3F)]
    [InlineData(0x0088, 0xFF, 0x1F)]
    public async Task AnOrdinaryReceiversSameModelAssociatedChildKeepsItsDocumentedRateProfile(
        ushort productId, byte readTransaction, byte writeTransaction)
    {
        var endpoint = Endpoint(productId);
        var child = Child(endpoint, productId);
        var controller = new RazerPollingRateController();
        await using var transport = new AssociationTransport(endpoint) { ChildProduct = productId };

        Assert.True(controller.CanHandle(endpoint, child));
        var result = await controller.ApplyAsync(endpoint, transport, child, 1000, () => true, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(1000, result.Info.CurrentRateHz);
        Assert.Equal(1, transport.SettingWrites);
        Assert.Equal(4, transport.AssociationReads);
        Assert.Equal(4, transport.RoleReads);
        Assert.Equal(new byte[] { 0xBF, 0xC5, 0x85, 0xBF, 0xC5, 0xBF, 0xC5, 0x05, 0x85, 0xBF, 0xC5 },
            transport.Commands.Select(request => request[8]).ToArray());
        Assert.All(transport.Commands.Where(request => request[8] is 0xBF or 0xC5),
            request => Assert.Equal(0x01, request[2]));
        Assert.Equal(new byte[] { readTransaction, writeTransaction, readTransaction },
            transport.Commands.Where(request => request[8] is 0x85 or 0x05).Select(request => request[2]).ToArray());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public async Task AChangedOrdinaryReceiverAssociationCannotReceiveMoreWritesOrBeVerified(int changedAt, int expectedWrites)
    {
        var endpoint = Endpoint(0x00A6);
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0x00A6,
            ListOverride = (index, request) => index == changedAt ? DeviceList(request, (0x00C1, true)) : null
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint, 0x00A6),
            1000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Disconnected, result.Info.State);
        Assert.Equal(expectedWrites, transport.SettingWrites);
        Assert.Equal(changedAt, transport.AssociationReads);
    }

    [Fact]
    public async Task MultipleConnectedOrdinaryReceiverPeersCannotAuthorizeAReceiverWideRateCommand()
    {
        var endpoint = Endpoint(0x00A6);
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0x00A6,
            ListOverride = (_, request) => DeviceList(request, (0x00A6, true), (0x025C, true))
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint, 0x00A6),
            1000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Single(transport.Commands);
        Assert.Equal(0, transport.SettingWrites);
    }

    [Fact]
    public async Task AnExplicitlyUnsupportedOrdinaryRoleQueryCanUseTheKnownSameModelMouseEvidence()
    {
        var endpoint = Endpoint(0x00A6);
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0x00A6,
            RoleOverride = (_, request) => DeviceListStatus(request, 5)
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint, 0x00A6),
            1000, () => true, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(1, transport.SettingWrites);
        Assert.Equal(4, transport.RoleReads);
        Assert.Equal(4, transport.AssociationReads);
    }

    [Fact]
    public async Task AnOrdinaryRoleTimeoutCannotUseTheKnownModelFallbackOrReuseTheSession()
    {
        var endpoint = Endpoint(0x00A6);
        await using var transport = new AssociationTransport(endpoint)
        {
            ChildProduct = 0x00A6,
            RoleOverride = (_, request) => DeviceListStatus(request, 4)
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport,
            Child(endpoint, 0x00A6), CancellationToken.None);

        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Equal(new byte[] { 0xBF, 0xC5 }, transport.Commands.Select(request => request[8]).ToArray());
    }

    [Fact]
    public void OrdinaryAssociatedControlRequiresTheCanonicalSameKnownMouseProduct()
    {
        var endpoint = Endpoint(0x00A6);
        var controller = new RazerPollingRateController();

        Assert.True(controller.CanHandle(endpoint, Child(endpoint, 0x00A6)));
        Assert.False(controller.CanHandle(endpoint, Child(endpoint, 0x00C1)));
        Assert.False(controller.CanHandle(endpoint, Child(endpoint, 0xBEEF)));
        Assert.False(controller.CanHandle(endpoint, Child(endpoint, 0x025C)));
        Assert.False(controller.CanHandle(Endpoint(0x00A4), Child(Endpoint(0x00A4), 0x00A4)));
    }

    [Theory]
    [InlineData(1, 0)] // Before initial rate read.
    [InlineData(2, 0)] // After initial rate read.
    [InlineData(3, 0)] // Immediately before first write.
    [InlineData(4, 1)] // Between the two writes.
    [InlineData(5, 2)] // After readback; rate match alone is insufficient.
    public async Task AChangedWirelessProductCannotReceiveMoreWritesOrBeReportedAsVerified(int changedAt, int expectedWrites)
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ListOverride = (index, request) => index == changedAt ? DeviceList(request, (0x00BF, true)) : null
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint),
            8000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Disconnected, result.Info.State);
        Assert.Null(result.Info.CurrentRateHz);
        Assert.Equal(expectedWrites, transport.SettingWrites);
        Assert.Equal(changedAt, transport.AssociationReads);
        if (expectedWrites > 0) Assert.Contains("may have changed", result.Detail);
    }

    [Fact]
    public async Task MultipleConnectedProductsPreventUnaddressedReceiverRateWrites()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ListOverride = (_, request) => DeviceList(request, (0x00C1, true), (0x025C, true))
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint),
            8000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Equal(0, transport.SettingWrites);
        Assert.Single(transport.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitDisconnectedOrUnoccupiedListsDoNotBecomeAConnectedSelectedProduct(bool unoccupied)
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ListOverride = (_, request) => unoccupied ? DeviceList(request) : DeviceList(request, (0x00C1, false))
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport, Child(endpoint), CancellationToken.None);

        Assert.Equal(PollingRateState.Disconnected, result.State);
        Assert.Null(result.CurrentRateHz);
        Assert.Equal(0, transport.SettingWrites);
        Assert.Single(transport.Commands);
    }

    [Fact]
    public async Task AnAssociationTimeoutEndsTheSessionWithoutLegacyFallbackOrRateCommands()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ListOverride = (_, _) => throw new TimeoutException("Session closed.")
        };

        var result = await new RazerPollingRateController().ReadAsync(endpoint, transport, Child(endpoint), CancellationToken.None);

        Assert.Equal(PollingRateState.Failed, result.State);
        Assert.Single(transport.Commands);
        Assert.Equal(0x1F, transport.Commands[0][2]);
    }

    [Fact]
    public async Task MalformedAssociationDoesNotFallBackOrChangeAnyConfiguration()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ListOverride = (_, request) =>
            {
                var reply = DeviceList(request, (0x00C1, true));
                reply[89] ^= 1;
                return reply;
            }
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint),
            8000, () => true, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Single(transport.Commands);
    }

    [Fact]
    public async Task ExplicitUnsupportedModernListCanUseTheDocumentedLegacyList()
    {
        var endpoint = Endpoint();
        await using var transport = new AssociationTransport(endpoint)
        {
            ListOverride = (_, request) => request[2] == 0x1F ? DeviceListStatus(request, 5) : null
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint),
            8000, () => true, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(2, transport.SettingWrites);
        Assert.Equal(10, transport.AssociationReads);
        var queries = transport.Commands.Where(request => request[8] == 0xBF).ToArray();
        Assert.Equal(new byte[] { 0x1F, 0xFF, 0x1F, 0xFF, 0x1F, 0xFF, 0x1F, 0xFF, 0x1F, 0xFF },
            queries.Select(request => request[2]).ToArray());
        Assert.All(queries.Where(request => request[2] == 0xFF), request => Assert.Equal(0x10, request[9]));
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public async Task OptInRevokedDuringAPhaseAssociationQueryPreventsThatWrite(int revokedAt, int expectedWrites)
    {
        var endpoint = Endpoint();
        bool enabled = true;
        await using var transport = new AssociationTransport(endpoint)
        {
            AfterListReply = index => { if (index == revokedAt) enabled = false; }
        };

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, Child(endpoint),
            8000, () => enabled, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(expectedWrites, transport.SettingWrites);
        Assert.Equal(revokedAt, transport.AssociationReads);
    }

    [Fact]
    public async Task CancellationDuringThePreWriteAssociationQueryCannotStartASettingCommand()
    {
        var endpoint = Endpoint();
        using var cancellation = new CancellationTokenSource();
        await using var transport = new AssociationTransport(endpoint)
        {
            AfterListReply = index => { if (index == 3) cancellation.Cancel(); }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RazerPollingRateController().ApplyAsync(
            endpoint, transport, Child(endpoint), 8000, () => true, cancellation.Token));

        Assert.Equal(0, transport.SettingWrites);
        Assert.Equal(3, transport.AssociationReads);
    }

    [Fact]
    public void ChildIdentityMustBeCanonicalAndBelongToTheSameReceiverAndReportedProduct()
    {
        var endpoint = Endpoint();
        var child = Child(endpoint);
        var controller = new RazerPollingRateController();
        Assert.True(controller.CanHandle(endpoint, child));
        Assert.False(controller.CanHandle(endpoint, child with { ReceiverId = "another-receiver" }));
        Assert.False(controller.CanHandle(endpoint, child with { ModelId = "1532:00BF" }));
        Assert.False(controller.CanHandle(endpoint, child with { Id = "receiver-hyper:razer:00B3/wireless/0000" }));
        Assert.False(controller.CanHandle(endpoint, child with { Id = "receiver-hyper:razer:00B3/wireless/00c1" }));
        Assert.False(controller.CanHandle(endpoint with { ProductId = 0x00A6 }, child));
    }

    [Theory]
    [InlineData(0x0025, 0xFF, 0xFF)]
    [InlineData(0x003F, 0xFF, 0xFF)]
    [InlineData(0x0045, 0xFF, 0x3F)]
    [InlineData(0x005A, 0x3F, 0x3F)]
    [InlineData(0x0062, 0x1F, 0x1F)]
    [InlineData(0x006F, 0x3F, 0x3F)]
    [InlineData(0x0072, 0x3F, 0x3F)]
    [InlineData(0x0077, 0x1F, 0x1F)]
    [InlineData(0x0090, 0x1F, 0x1F)]
    [InlineData(0x0094, 0x1F, 0x1F)]
    [InlineData(0x009A, 0x1F, 0x1F)]
    [InlineData(0x00A8, 0x1F, 0x1F)]
    [InlineData(0x00B4, 0x1F, 0x1F)]
    [InlineData(0x00C8, 0x1F, 0x1F)]
    [InlineData(0x00CD, 0x1F, 0x1F)]
    [InlineData(0x00D1, 0x1F, 0x1F)]
    [InlineData(0x00D4, 0x1F, 0x1F)]
    [InlineData(0x00D7, 0x1F, 0x1F)]
    public async Task ExpandedOrdinaryProfilesUseTheirActualReadAndWriteTransactions(
        ushort productId, byte readTransaction, byte writeTransaction)
    {
        var endpoint = Endpoint(productId);
        var identity = new PeripheralIdentity($"{endpoint.ReceiverId}:razer:{productId:X4}", "Selected receiver",
            DeviceVendor.Razer, endpoint.ReceiverId, $"1532:{productId:X4}");
        await using var transport = new AssociationTransport(endpoint);

        var result = await new RazerPollingRateController().ApplyAsync(endpoint, transport, identity,
            1000, () => true, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(1000, result.Info.CurrentRateHz);
        Assert.Equal(new[] { 125, 500, 1000 }, result.Info.SupportedRatesHz);
        Assert.Equal(new byte[] { 0x85, 0x05, 0x85 }, transport.Commands.Select(request => request[8]).ToArray());
        Assert.Equal(new byte[] { readTransaction, writeTransaction, readTransaction },
            transport.Commands.Select(request => request[2]).ToArray());
        Assert.Equal(1, transport.SettingWrites);
    }

    private static HidEndpoint Endpoint(ushort productId = 0x00B3) =>
        new("fake-usb-vid_1532&mi_00", 0x1532, productId, 0xFF00, 1, 0, 0, 91, "receiver-hyper");

    private static PeripheralIdentity Child(HidEndpoint endpoint, ushort productId = 0x00C1) =>
        new($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}/wireless/{productId:X4}", "Reported wireless peripheral",
            DeviceVendor.Razer, endpoint.ReceiverId, $"1532:{productId:X4}");

    private static byte[] DeviceList(byte[] request, params (ushort ProductId, bool Connected)[] children)
    {
        var reply = request.ToArray();
        reply[1] = 2;
        Array.Clear(reply, 9, 80);
        reply[9] = (byte)children.Length;
        for (int index = 0; index < children.Length; index++)
        {
            int offset = 10 + index * 3;
            reply[offset] = children[index].Connected ? (byte)1 : (byte)0;
            reply[offset + 1] = (byte)(children[index].ProductId >> 8);
            reply[offset + 2] = (byte)children[index].ProductId;
        }
        reply[89] = Checksum(reply);
        return reply;
    }

    private static byte[] DeviceListStatus(byte[] request, byte status)
    {
        var reply = request.ToArray();
        reply[1] = status;
        reply[89] = Checksum(reply);
        return reply;
    }

    private static byte[] PrimaryDevice(byte[] request, byte type, ushort productId)
    {
        var reply = request.ToArray();
        reply[1] = 2;
        reply[9] = type;
        reply[10] = (byte)(productId >> 8);
        reply[11] = (byte)productId;
        reply[89] = Checksum(reply);
        return reply;
    }

    private static byte Checksum(byte[] report)
    {
        byte checksum = 0;
        for (int offset = 3; offset <= 88; offset++) checksum ^= report[offset];
        return checksum;
    }

    private sealed class AssociationTransport(HidEndpoint endpoint) : IHidTransport
    {
        private byte[]? pending;
        private byte highRate = 0x08;
        private byte ordinaryRate = 0x02;
        public HidEndpoint Endpoint { get; } = endpoint;
        public ushort ChildProduct { get; init; } = 0x00C1;
        public List<byte[]> Commands { get; } = [];
        public int AssociationReads { get; private set; }
        public int RoleReads { get; private set; }
        public int SettingWrites { get; private set; }
        public Func<int, byte[], byte[]?>? ListOverride { get; init; }
        public Func<int, byte[], byte[]?>? RoleOverride { get; init; }
        public Action<int>? AfterListReply { get; init; }

        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending = report.ToArray();
            Commands.Add(pending);
            if (report[8] == 0x40)
            {
                SettingWrites++;
                if (report[9] == 1) highRate = report[10];
            }
            else if (report[8] == 0x05)
            {
                SettingWrites++;
                ordinaryRate = report[9];
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = pending ?? throw new InvalidOperationException("No feature request was sent.");
            if (request[8] == 0xBF)
            {
                AssociationReads++;
                var reply = ListOverride?.Invoke(AssociationReads, request) ?? DeviceList(request, (ChildProduct, true));
                AfterListReply?.Invoke(AssociationReads);
                return ValueTask.FromResult(reply);
            }
            if (request[8] == 0xC5)
            {
                RoleReads++;
                return ValueTask.FromResult(RoleOverride?.Invoke(RoleReads, request) ?? PrimaryDevice(request, 1, ChildProduct));
            }
            var result = request.ToArray();
            result[1] = 2;
            if (request[8] == 0xC0)
            {
                result[6] = 2;
                result[10] = highRate;
            }
            else if (request[8] == 0x85) result[9] = ordinaryRate;
            else if (request[8] is not (0x40 or 0x05)) throw new InvalidOperationException("Unexpected feature command.");
            result[89] = Checksum(result);
            return ValueTask.FromResult(result);
        }

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
