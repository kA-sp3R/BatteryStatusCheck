using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class AlienwareBatteryProviderTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x413C, 0xA529, 0xFF13, true)]
    [InlineData(0x413C, 0xA528, 0xFF13, false)] // USB-C is outside the 2.4 GHz workflow.
    [InlineData(0x413C, 0xA530, 0xFF13, false)]
    [InlineData(0x187C, 0xA529, 0xFF13, false)]
    [InlineData(0x413C, 0xA529, 0xFF00, false)]
    public void OnlyTheDocumentedDongleVendorCollectionMatches(
        ushort vendor, ushort product, ushort usagePage, bool expected)
    {
        var endpoint = Endpoint() with { VendorId = vendor, ProductId = product, UsagePage = usagePage };

        Assert.Equal(expected, new AlienwareBatteryProvider().CanHandle(endpoint));
    }

    [Fact]
    public async Task ACurrentZeroRequiresHeadsetIdentityAndConnectedLinkBeforeAndAfterTheBatteryQuery()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, percentage: 0);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09, 0x00, 0x0A, 0x09 }, transport.Commands);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal(DeviceVendor.Alienware, result.Device.Vendor);
        Assert.Equal(endpoint.ReceiverId, result.Device.ReceiverId);
        Assert.Equal("413C:A528", result.Device.ModelId);
    }

    [Fact]
    public async Task InitialLinkDownIsDisconnectedWithoutQueryingIdentityOrBattery()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, initialLink: 0xDD);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09 }, transport.Commands);
        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Theory]
    [InlineData(0xDD, ReadingState.Disconnected, ConnectionState.Disconnected)]
    [InlineData(0x42, ReadingState.Failed, ConnectionState.Unknown)]
    public async Task LinkLossOrUnknownStateAfterBatteryReadCannotPublishThatBatteryAsCurrent(
        byte finalLink, ReadingState expectedState, ConnectionState expectedConnection)
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, finalLink: finalLink);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09, 0x00, 0x0A, 0x09 }, transport.Commands);
        Assert.Equal(expectedState, result.Battery.State);
        Assert.Equal(expectedConnection, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Fact]
    public async Task UnknownInitialLinkIsAFailedReadWithoutClaimingConnectionOrPercentage()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, initialLink: 0x42);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09 }, transport.Commands);
        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Fact]
    public async Task UsbTransportProductIdIsNotAcceptedAsProofOfThePairedHeadsetFamily()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, identityProductId: 0xA529);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09, 0x00 }, transport.Commands);
        Assert.Equal(ReadingState.Unsupported, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Fact]
    public async Task BatteryTimeoutAfterConnectedLinkIsFailedAndNeverSynthesizesZero()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, timeoutCommand: 0x0A);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09, 0x00, 0x0A }, transport.Commands);
        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Fact]
    public async Task FailedBatteryReadDoesNotReuseTheSessionToInferDisconnection()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, finalLink: 0xDD, timeoutCommand: 0x0A);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(new byte[] { 0x09, 0x00, 0x0A }, transport.Commands);
        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x0A, false)]
    [InlineData(0x00, true)]
    [InlineData(0x0A, true)]
    public async Task FailedQueryDoesNotReuseANativeSessionClosedByTimeoutOrIoFailure(byte command, bool ioFailure)
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, timeoutCommand: command,
            closeOnFailure: true, ioFailure: ioFailure);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
        byte[] expectedCommands = command == 0x00 ? [0x09, 0x00] : [0x09, 0x00, 0x0A];
        Assert.Equal(expectedCommands, transport.Commands);
        Assert.Equal(transport.Commands.Count, transport.OutputAttempts);
    }

    [Theory]
    [InlineData(61, 63)]
    [InlineData(62, 16)]
    [InlineData(16385, 63)]
    [InlineData(62, 16385)]
    public async Task UnusableDescriptorLengthsRemainUnsupportedWithoutSendingCommands(int outputLength, int inputLength)
    {
        var endpoint = Endpoint() with { OutputReportLength = outputLength, InputReportLength = inputLength };
        await using var transport = new ControlTransport(endpoint);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Unsupported, result.Battery.State);
        Assert.Null(result.Battery.Percentage);
        Assert.Empty(transport.Commands);
    }

    [Theory]
    [InlineData(62, 17)]
    [InlineData(64, 64)]
    public async Task DescriptorPaddingDoesNotAssumeTheLinuxReplyBufferIsTheWindowsReportSize(
        int outputLength, int inputLength)
    {
        var endpoint = Endpoint() with { OutputReportLength = outputLength, InputReportLength = inputLength };
        await using var transport = new ControlTransport(endpoint);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(64, result.Battery.Percentage);
    }

    [Fact]
    public async Task MalformedBatteryReplyBecomesFailedWithNoInventedPercentageOrConnection()
    {
        var endpoint = Endpoint();
        await using var transport = new ControlTransport(endpoint, malformedBattery: true);

        var result = Assert.Single(await new AlienwareBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Null(result.Battery.Percentage);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Equal(new byte[] { 0x09, 0x00, 0x0A }, transport.Commands);
    }

    [Fact]
    public async Task MonitorRetainsTheLastGenuineZeroAsStaleAfterALaterAlienwareReadFails()
    {
        var endpoint = Endpoint();
        var sessions = new Queue<IHidTransport>([
            new ControlTransport(endpoint, percentage: 0),
            new ControlTransport(endpoint, timeoutCommand: 0x0A)
        ]);
        var monitor = new BatteryMonitor(new FixedDiscovery(endpoint), new SessionFactory(sessions),
            [new AlienwareBatteryProvider()]);

        var first = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));
        var next = Assert.Single(await monitor.RefreshAsync(CancellationToken.None));

        Assert.Equal(ReadingState.Current, first.Battery.State);
        Assert.Equal(0, first.Battery.Percentage);
        Assert.Equal(ReadingState.Stale, next.Battery.State);
        Assert.Equal(0, next.Battery.Percentage);
        Assert.Equal(first.Battery.SampledAt, next.Battery.SampledAt);
        Assert.Equal(ConnectionState.Unknown, next.Connection);
        Assert.Equal(first.Device.Id, next.Device.Id);
    }

    private static HidEndpoint Endpoint() =>
        new("fake-alienware-usb", 0x413C, 0xA529, 0xFF13, 1, 63, 62, 0, "receiver-awpro");

    private sealed class FixedDiscovery(HidEndpoint endpoint) : IHidDiscovery
    {
        public IReadOnlyList<HidEndpoint> Enumerate() => [endpoint];
    }

    private sealed class SessionFactory(Queue<IHidTransport> sessions) : IHidTransportFactory
    {
        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sessions.Dequeue());
    }

    private sealed class ControlTransport(HidEndpoint endpoint, byte initialLink = 0xCC, byte finalLink = 0xCC,
        byte percentage = 64, byte charging = 0, ushort identityProductId = 0xA528,
        byte? timeoutCommand = null, bool closeOnFailure = false, bool ioFailure = false,
        bool malformedBattery = false) : IHidTransport
    {
        private byte? activeCommand;
        private int linkRequests;
        private bool closed;
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte> Commands { get; } = [];
        public int OutputAttempts { get; private set; }

        public ValueTask SetOutputReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutputAttempts++;
            ObjectDisposedException.ThrowIf(closed, this);
            Assert.Equal(62, report.Length);
            Assert.Equal((byte)0x06, report[0]);
            Assert.Equal((byte)0xC0, report[1]);
            activeCommand = report[2];
            Commands.Add(report[2]);
            if (activeCommand == 0x00)
                Assert.Equal(new byte[] { 4, 0, 0, 0xF5, 0x28, 0 }, report[3..9]);
            else Assert.All(report[3..], value => Assert.Equal((byte)0, value));
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> GetInputReportAsync(byte[] request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(closed, this);
            Assert.Equal(Endpoint.InputReportLength, request.Length);
            Assert.Equal((byte)0x07, request[0]);
            Assert.All(request[1..], value => Assert.Equal((byte)0, value));
            if (activeCommand == timeoutCommand)
            {
                closed = closeOnFailure;
                if (ioFailure) throw new IOException("Scripted Alienware I/O failure.");
                throw new TimeoutException("No scripted Alienware reply.");
            }

            var response = activeCommand switch
            {
                0x09 => AlienwareFixtures.Link(++linkRequests == 1 ? initialLink : finalLink),
                0x00 => AlienwareFixtures.Identity(identityProductId),
                0x0A => AlienwareFixtures.Battery(percentage, charging),
                _ => throw new InvalidOperationException("Unexpected monitoring command.")
            };
            if (malformedBattery && activeCommand == 0x0A) response[3] = 0;
            var descriptorSized = new byte[Endpoint.InputReportLength];
            Array.Copy(response, descriptorSized, Math.Min(response.Length, descriptorSized.Length));
            return ValueTask.FromResult(descriptorSized);
        }

        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alienware replies use HID Input reports, not Feature reports.");
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alienware requests use HID Output reports, not Feature reports.");
        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This provider must use control Output reports.");
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This provider must use control Input reports.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
