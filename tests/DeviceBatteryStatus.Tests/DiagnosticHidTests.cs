using System.Text.Json;
using DeviceBatteryStatus.Core.Diagnostics;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class DiagnosticHidTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"device-battery-diagnostic-hid-{Guid.NewGuid():N}");
    private static HidEndpoint Endpoint(ushort vendor = 0x1532, ushort product = 0x00E6) =>
        new(@"\\?\hid#vid_1532&pid_00e6&mi_03#PRIVATE_SERIAL#container", vendor, product,
            0xFF00, 1, 64, 64, 91, "PRIVATE_RECEIVER", SerialNumber: "PRIVATE_SERIAL");

    [Fact]
    public void DiscoveryPreservesEndpointsWarningsAndOneEnumerationWithoutLoggingSerials()
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var endpoint = Endpoint();
        var inner = new Discovery { Endpoints = [endpoint], WarningValues = ["Unsupported interface serial=PRIVATE_SERIAL"] };
        var discovery = new DiagnosticHidDiscovery(inner, log);
        Assert.Same(inner.Endpoints, discovery.Enumerate());
        Assert.Same(inner.WarningValues, discovery.Warnings);
        Assert.Equal(1, inner.Enumerations);
        Assert.Contains("Unsupported interface", AllText());
        Assert.DoesNotContain("PRIVATE_SERIAL", AllText());
    }

    [Fact]
    public void DiscoveryFailureRetainsOriginalExceptionAndRedactsItsIdentifyingPath()
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var failure = new IOException(@"Could not enumerate C:\Users\PRIVATE_ACCOUNT\device.txt");
        var discovery = new DiagnosticHidDiscovery(new Discovery { Failure = failure }, log);
        Assert.Same(failure, Assert.Throws<IOException>(() => discovery.Enumerate()));
        Assert.Contains("HID enumeration failed", AllText());
        Assert.DoesNotContain("PRIVATE_ACCOUNT", AllText());
    }

    [Fact]
    public async Task AllTransportMethodsPreserveBuffersTimeoutsTokensAndExactlyOneUnderlyingCall()
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var inner = new Transport(Endpoint());
        var factory = new Factory(inner);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var transport = await new DiagnosticHidTransportFactory(factory, log).OpenAsync(inner.Endpoint, token);
        var request = new byte[] { 0, 0, 0x1F, 0, 0, 0, 2, 7, 0x80, 0, 0 };
        var feature = new byte[] { 0, 2, 0x1F, 0, 0, 0, 2, 7, 0x80, 0, 0xAB };
        inner.Replies.Enqueue(feature);
        var input = new byte[] { 7, 0xC0, 0x0A, 0x0A, 0, 0, 0xAB };
        inner.Replies.Enqueue(input);
        var read = new byte[] { 0x10, 1, 6, 9, 0xAB, 0xCD, 0xEF };
        inner.Replies.Enqueue(read);
        await transport.SetFeatureAsync(request, token);
        Assert.Same(feature, await transport.GetFeatureAsync(request, token));
        await transport.SetOutputReportAsync(request, token);
        Assert.Same(input, await transport.GetInputReportAsync(request, token));
        await transport.WriteAsync(request, token);
        Assert.Same(read, await transport.ReadAsync(TimeSpan.FromMilliseconds(123), token));
        await transport.DisposeAsync();
        await transport.DisposeAsync();

        Assert.Same(inner.Endpoint, transport.Endpoint);
        Assert.Equal(1, factory.Opens);
        Assert.Equal(token, factory.Token);
        Assert.Equal(new[] { "SetFeature", "GetFeature", "SetOutput", "GetInput", "Write", "Read", "Dispose" }, inner.Calls.Select(call => call.Kind));
        Assert.All(inner.Calls.Where(call => call.Kind is not "Dispose"), call => Assert.Equal(token, call.Token));
        Assert.All(inner.Calls.Where(call => call.Buffer is not null), call => Assert.Same(request, call.Buffer));
        Assert.Equal(TimeSpan.FromMilliseconds(123), inner.ReadTimeout);
        Assert.DoesNotContain("PRIVATE_SERIAL", AllText());
    }

    [Fact]
    public async Task FailedLoggingStorageCannotPreventSuccessfulUnderlyingIo()
    {
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "not a directory");
        using var log = new DiagnosticLog(blocked, "1.1.0");
        var inner = new Transport(Endpoint());
        await using var transport = await new DiagnosticHidTransportFactory(new Factory(inner), log).OpenAsync(inner.Endpoint, CancellationToken.None);
        var request = new byte[] { 0, 0, 0x1F, 0, 0, 0, 2, 7, 0x80 };
        var reply = new byte[] { 0, 2, 0x1F };
        inner.Replies.Enqueue(reply);
        await transport.SetFeatureAsync(request, CancellationToken.None);
        Assert.Same(reply, await transport.GetFeatureAsync(request, CancellationToken.None));
        Assert.NotNull(log.LastError);
        Assert.Equal(new[] { "SetFeature", "GetFeature" }, inner.Calls.Select(call => call.Kind));
    }

    [Theory]
    [InlineData("SetFeature")]
    [InlineData("GetFeature")]
    [InlineData("SetOutput")]
    [InlineData("GetInput")]
    [InlineData("Write")]
    [InlineData("Read")]
    public async Task TransportFailuresKeepTheOriginalExceptionAndCannotAddAnIoRetry(string operation)
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var inner = new Transport(Endpoint()) { Failure = new TimeoutException("Read timed out serial=PRIVATE_SERIAL") };
        await using var transport = await new DiagnosticHidTransportFactory(new Factory(inner), log).OpenAsync(inner.Endpoint, CancellationToken.None);
        var request = new byte[] { 0 };
        var thrown = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            switch (operation)
            {
                case "SetFeature": await transport.SetFeatureAsync(request, CancellationToken.None); break;
                case "GetFeature": await transport.GetFeatureAsync(request, CancellationToken.None); break;
                case "SetOutput": await transport.SetOutputReportAsync(request, CancellationToken.None); break;
                case "GetInput": await transport.GetInputReportAsync(request, CancellationToken.None); break;
                case "Write": await transport.WriteAsync(request, CancellationToken.None); break;
                case "Read": await transport.ReadAsync(TimeSpan.FromMilliseconds(7), CancellationToken.None); break;
            }
        });
        Assert.Same(inner.Failure, thrown);
        Assert.Single(inner.Calls);
        Assert.Contains("failed", AllText());
        Assert.DoesNotContain("PRIVATE_SERIAL", AllText());
        inner.Failure = null;
    }

    [Fact]
    public async Task OpenFailureKeepsItsOriginalCauseWithoutRecordingAnIdentifyingPath()
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var factory = new Factory(new Transport(Endpoint())) { Failure = new UnauthorizedAccessException(@"Access denied C:\Users\PRIVATE_ACCOUNT\device.txt") };
        var thrown = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await new DiagnosticHidTransportFactory(factory, log).OpenAsync(Endpoint(), CancellationToken.None));
        Assert.Same(factory.Failure, thrown);
        Assert.Equal(1, factory.Opens);
        Assert.DoesNotContain("PRIVATE_ACCOUNT", AllText());
    }

    [Fact]
    public async Task OnlyCorrelatedWhitelistedHidppCapabilityBytesAreRecorded()
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var inner = new Transport(Endpoint(0x046D, 0xC548));
        await using var transport = await new DiagnosticHidTransportFactory(new Factory(inner), log).OpenAsync(inner.Endpoint, CancellationToken.None);
        await Exchange(transport, inner, LogitechHidppProtocol.CreateRequest(1, 0, 9, [0x10, 0x04]), Reply(1, 0, 9, [6, 0, 1]));
        await Exchange(transport, inner, LogitechHidppProtocol.CreateRequest(1, 6, 9, []), Reply(1, 6, 9, [0x0F, 0x02]));
        // Unit-info feature is known, but its identifying argument bytes must never enter the log.
        await Exchange(transport, inner, LogitechHidppProtocol.CreateRequest(1, 0, 9, [0, 3]), Reply(1, 0, 9, [10, 0, 1]));
        await Exchange(transport, inner, LogitechHidppProtocol.CreateRequest(1, 10, 9, []), Reply(1, 10, 9, [1, 0xAB, 0xCD, 0xEF, 0x42]));

        var capabilities = Records().Where(record => record.GetProperty("category").GetString() == "capability").ToArray();
        var discovery = capabilities.Single(record => Get(record, "featureId") == "1004" && record.GetProperty("message").GetString() == "Correlated HID++ feature discovery");
        Assert.Equal("06", Get(discovery, "advertisedIndex"));
        var mask = capabilities.Single(record => record.GetProperty("message").GetString()!.Contains("capabilities", StringComparison.Ordinal));
        Assert.Equal("1004", Get(mask, "featureId"));
        Assert.Equal("0F", Get(mask, "supportedCategoriesMask"));
        Assert.Equal("02", Get(mask, "batteryCapabilitiesMask"));
        var unitReply = Records().Last(record => record.GetProperty("category").GetString() == "protocol");
        Assert.All(unitReply.GetProperty("fields").EnumerateObject(), field =>
            Assert.Contains(field.Name, new[] { "vid", "pid", "length", "reportId", "slot", "featureIndex", "function" }));
        Assert.DoesNotContain("ABCDEF42", unitReply.GetRawText());
        Assert.DoesNotContain("PRIVATE_SERIAL", AllText());
        Assert.Equal(8, inner.Calls.Count);
    }

    [Fact]
    public async Task AnUncorrelatedReplyCannotAdvertiseCapabilitiesToDiagnostics()
    {
        using var log = new DiagnosticLog(directory, "1.1.0");
        var inner = new Transport(Endpoint(0x046D, 0xC548));
        await using var transport = await new DiagnosticHidTransportFactory(new Factory(inner), log).OpenAsync(inner.Endpoint, CancellationToken.None);
        await Exchange(transport, inner, LogitechHidppProtocol.CreateRequest(1, 0, 9, [0x10, 0x04]), Reply(2, 0, 9, [6, 0, 1]));
        await Exchange(transport, inner, LogitechHidppProtocol.CreateRequest(1, 6, 9, []), Reply(1, 6, 9, [0x0F, 0x02]));
        Assert.DoesNotContain(Records(), record => record.GetProperty("category").GetString() == "capability");
    }

    private static async Task Exchange(IHidTransport transport, Transport inner, byte[] request, byte[] reply)
    {
        inner.Replies.Enqueue(reply);
        await transport.WriteAsync(request, CancellationToken.None);
        Assert.Same(reply, await transport.ReadAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }
    private static byte[] Reply(byte slot, byte index, byte function, byte[] parameters)
    {
        var reply = new byte[20];
        reply[0] = 0x11; reply[1] = slot; reply[2] = index; reply[3] = function;
        parameters.CopyTo(reply, 4);
        return reply;
    }
    private static string? Get(JsonElement record, string key) => record.GetProperty("fields").GetProperty(key).GetString();
    private string AllText() => string.Join("\n", Directory.GetFiles(directory, "diagnostic-*.jsonl").Select(File.ReadAllText));
    private JsonElement[] Records() => Directory.GetFiles(directory, "diagnostic-*.jsonl").SelectMany(File.ReadLines)
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();

    private sealed class Discovery : IHidDiscovery
    {
        public IReadOnlyList<HidEndpoint> Endpoints { get; init; } = [];
        public IReadOnlyList<string> WarningValues { get; init; } = [];
        public Exception? Failure { get; init; }
        public int Enumerations { get; private set; }
        public IReadOnlyList<string> Warnings => WarningValues;
        public IReadOnlyList<HidEndpoint> Enumerate()
        {
            Enumerations++;
            return Failure is null ? Endpoints : throw Failure;
        }
    }
    private sealed class Factory(Transport transport) : IHidTransportFactory
    {
        public int Opens { get; private set; }
        public Exception? Failure { get; init; }
        public CancellationToken Token { get; private set; }
        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
        {
            Opens++;
            Token = cancellationToken;
            return Failure is null ? ValueTask.FromResult<IHidTransport>(transport) : ValueTask.FromException<IHidTransport>(Failure);
        }
    }
    private sealed class Transport(HidEndpoint endpoint) : IHidTransport
    {
        public HidEndpoint Endpoint { get; } = endpoint;
        public Queue<byte[]> Replies { get; } = new();
        public List<(string Kind, byte[]? Buffer, CancellationToken Token)> Calls { get; } = [];
        public Exception? Failure { get; set; }
        public TimeSpan? ReadTimeout { get; private set; }
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken token) => Write("SetFeature", report, token);
        public ValueTask<byte[]> GetFeatureAsync(byte[] report, CancellationToken token) => Read("GetFeature", report, token);
        public ValueTask SetOutputReportAsync(byte[] report, CancellationToken token) => Write("SetOutput", report, token);
        public ValueTask<byte[]> GetInputReportAsync(byte[] report, CancellationToken token) => Read("GetInput", report, token);
        public ValueTask WriteAsync(byte[] report, CancellationToken token) => Write("Write", report, token);
        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken token)
        {
            ReadTimeout = timeout;
            return Read("Read", null, token);
        }
        private ValueTask Write(string kind, byte[] report, CancellationToken token)
        {
            Calls.Add((kind, report, token));
            return Failure is null ? ValueTask.CompletedTask : ValueTask.FromException(Failure);
        }
        private ValueTask<byte[]> Read(string kind, byte[]? report, CancellationToken token)
        {
            Calls.Add((kind, report, token));
            return Failure is null ? ValueTask.FromResult(Replies.Dequeue()) : ValueTask.FromException<byte[]>(Failure);
        }
        public ValueTask DisposeAsync()
        {
            Calls.Add(("Dispose", null, default));
            return ValueTask.CompletedTask;
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
