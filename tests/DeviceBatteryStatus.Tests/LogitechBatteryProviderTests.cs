using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class LogitechBatteryProviderTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BatteryIsQueriedThroughThePairedSlotAndAssociatedWithThatPeripheral()
    {
        var endpoint = Endpoint(0xC52B);
        await using var transport = new ReceiverTransport(endpoint, slots: 2, targetSlot: 2,
            injectOtherSlotPing: true);

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal("receiver-a/logitech/2/4081/12345678", result.Device.Id);
        Assert.Equal("4081", result.Device.ModelId);
        Assert.Equal(endpoint.ReceiverId, result.Device.ReceiverId);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(0, result.Battery.Percentage);
        Assert.Equal(ChargingState.Discharging, result.Battery.Charging);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
        Assert.Contains(transport.Requests, request => request[1] == 0xFF && request[4] == 0x20);
        Assert.Contains(transport.Requests, request => request[1] == 0xFF && request[4] == 0x21);
        Assert.All(transport.Requests.Where(request => request[1] != 0xFF),
            request => Assert.Equal((byte)2, request[1]));
        Assert.Contains(transport.Requests, request => request[1] == 2 && request[2] == 7 && (request[3] & 0xF0) == 0x10);
    }

    [Theory]
    [InlineData(0x04, ReadingState.Disconnected, ConnectionState.Disconnected)]
    [InlineData(0x08, ReadingState.Disconnected, ConnectionState.Disconnected)]
    [InlineData(0x09, ReadingState.Failed, ConnectionState.Unknown)]
    public async Task ExplicitPingErrorsDistinguishDisconnectedFromOtherFailures(
        byte errorCode, ReadingState expectedReading, ConnectionState expectedConnection)
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, pingError: errorCode);

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(expectedReading, result.Battery.State);
        Assert.Equal(expectedConnection, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.DoesNotContain(transport.Requests, request => request[2] == 7);
    }

    [Fact]
    public async Task PingTimeoutMeansUnknownConnectionAndFailedReadingWithoutAFallbackValue()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, timeoutPing: true);

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Equal(ConnectionState.Unknown, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.DoesNotContain(transport.Requests, request => request[2] == 7);
    }

    [Theory]
    [InlineData(100, 0x02, 32, 32, null)]
    [InlineData(100, 0x02, 0, null, BatteryCategory.Unknown)]
    [InlineData(4, 0x02, 50, null, BatteryCategory.Good)]
    [InlineData(100, 0x00, 50, null, BatteryCategory.Good)]
    [InlineData(100, 0x04, 50, null, BatteryCategory.Good)]
    public async Task BatteryStatusRequiresAdvertisedMileageBeforeDisplayingItsNumericLevel(
        byte levelCount, byte flags, byte reportedLevel, int? expectedPercentage, BatteryCategory? expectedCategory)
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, useBatteryStatus: true,
            statusLevelCount: levelCount, statusFlags: flags, statusLevel: reportedLevel);

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(expectedPercentage, result.Battery.Percentage);
        Assert.Equal(expectedCategory, result.Battery.Category);
        Assert.Equal(ChargingState.Discharging, result.Battery.Charging);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Contains(transport.Requests, request => request[2] == 8 && (request[3] & 0xF0) == 0x10);
    }

    [Fact]
    public async Task NativeUnifiedCategoryIsCurrentWhenNoPercentageFeatureIsAvailable()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, scenario: new BatteryScenario
        {
            UnifiedPercentage = false,
            UnifiedLevel = 2
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Low);
        Assert.Contains("not advertised", result.Battery.Detail);
        Assert.Contains(transport.Requests, IsStatusDiscovery);
        Assert.DoesNotContain(transport.Requests, request => request[2] == 0x81);
        Assert.DoesNotContain(transport.Requests, request => request[2] == 0 && request[4] == 0x1F);
    }

    [Fact]
    public async Task AdvertisedAlternativeMileageSuppliesAnActualPercentage()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, statusLevel: 37, scenario: new BatteryScenario
        {
            UnifiedPercentage = false,
            StatusPresent = true
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(37, result.Battery.Percentage);
        Assert.Null(result.Battery.Category);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(100, 0)]
    public async Task AlternativeCoarseLevelDoesNotReplaceTheNativeUnifiedCategory(byte count, byte flags)
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, statusLevelCount: count, statusFlags: flags,
            statusLevel: 50, scenario: new BatteryScenario { UnifiedPercentage = false, UnifiedLevel = 2, StatusPresent = true });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Low);
    }

    [Fact]
    public async Task AReportedAlternativeCategoryCanImproveAnUnknownUnifiedCategory()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, statusLevelCount: 4, statusLevel: 25,
            scenario: new BatteryScenario { UnifiedPercentage = false, UnifiedLevel = 0, StatusPresent = true });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Low);
    }

    [Fact]
    public async Task AnAlternativeMileageUnknownSentinelCannotEraseAKnownUnifiedCategory()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, statusLevel: 0,
            scenario: new BatteryScenario { UnifiedPercentage = false, UnifiedLevel = 2, StatusPresent = true });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Low);
    }

    [Theory]
    [InlineData(AlternativeFailure.DiscoveryProtocolError)]
    [InlineData(AlternativeFailure.CapabilitiesProtocolError)]
    [InlineData(AlternativeFailure.BatteryProtocolError)]
    [InlineData(AlternativeFailure.MalformedBattery)]
    public async Task OptionalPercentageFailuresPreserveTheFreshCategoryAndItsSampleTime(AlternativeFailure failure)
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, scenario: new BatteryScenario
        {
            UnifiedPercentage = false,
            StatusPresent = true,
            Failure = failure
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Good);
        Assert.Contains("did not provide a percentage", result.Battery.Detail);
    }

    [Theory]
    [InlineData(AlternativeFailure.DiscoveryTimeout)]
    [InlineData(AlternativeFailure.CapabilitiesTimeout)]
    [InlineData(AlternativeFailure.BatteryTimeout)]
    [InlineData(AlternativeFailure.BatteryIoError)]
    public async Task OptionalTimeoutOrIoFailureKeepsTheSampleAndStopsBeforeReusingTheSession(AlternativeFailure failure)
    {
        var endpoint = Endpoint(0xC52B);
        await using var transport = new ReceiverTransport(endpoint, slots: 2, scenario: new BatteryScenario
        {
            UnifiedPercentage = false,
            StatusPresent = true,
            Failure = failure
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Good);
        Assert.Contains("HID session was stopped", result.Battery.Detail);
        Assert.DoesNotContain(transport.Requests, request => request[1] == 0xFF && request[4] == 0x21);
    }

    [Fact]
    public async Task ExplicitDisconnectionDuringTheAlternativeQueryInvalidatesTheCategory()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, scenario: new BatteryScenario
        {
            UnifiedPercentage = false,
            StatusPresent = true,
            Failure = AlternativeFailure.Disconnected
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Disconnected, result.Battery.State);
        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.Category);
        Assert.Null(result.Battery.SampledAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(101)]
    [InlineData(255)]
    public async Task InvalidMileageResolutionCannotAuthorizeAPercentage(byte count)
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, useBatteryStatus: true,
            statusLevelCount: count, statusLevel: 50);

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(ReadingState.Failed, result.Battery.State);
        Assert.Null(result.Battery.Percentage);
        Assert.Null(result.Battery.Category);
        Assert.DoesNotContain(transport.Requests, request => request[2] == 8 && (request[3] & 0xF0) == 0);
    }

    [Fact]
    public async Task MalformedOptionalCapabilitiesCannotEraseACompletedCategorySample()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, statusLevelCount: 255, scenario: new BatteryScenario
        {
            UnifiedPercentage = false,
            StatusPresent = true
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.Good);
    }

    [Fact]
    public async Task LegacyPercentageRegisterIsPreferredAndPreservesGenuineZero()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, scenario: new BatteryScenario
        {
            Legacy = true,
            LegacyPercentage = 0
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        Assert.Equal(0, result.Battery.Percentage);
        Assert.Null(result.Battery.Category);
        Assert.DoesNotContain(transport.Requests, request => request[2] == 0x81 && request[3] == 0x07);
    }

    [Fact]
    public async Task LegacyCategoryRegisterIsReadOnlyWhenItsPercentageRegisterIsUnsupported()
    {
        var endpoint = Endpoint(0xC539);
        await using var transport = new ReceiverTransport(endpoint, scenario: new BatteryScenario
        {
            Legacy = true,
            LegacyLevel = 7
        });

        var result = Assert.Single(await new LogitechBatteryProvider().ReadAsync(
            endpoint, transport, SampleTime, CancellationToken.None));

        AssertCurrentCategory(result, BatteryCategory.High);
        Assert.Contains(transport.Requests, request => request[2] == 0x81 && request[3] == 0x0D);
        Assert.Contains(transport.Requests, request => request[2] == 0x81 && request[3] == 0x07);
        Assert.DoesNotContain(transport.Requests, request => request[1] != 0xFF && request[2] != 0 && request[2] != 0x81);
    }

    private static void AssertCurrentCategory(DeviceSnapshot result, BatteryCategory category)
    {
        Assert.Equal(ReadingState.Current, result.Battery.State);
        Assert.Equal(ConnectionState.Connected, result.Connection);
        Assert.Equal(category, result.Battery.Category);
        Assert.Null(result.Battery.Percentage);
        Assert.Equal(SampleTime, result.Battery.SampledAt);
    }

    private static bool IsStatusDiscovery(byte[] request) =>
        request[1] != 0xFF && request[2] == 0 && (request[3] & 0xF0) == 0 && request[4] == 0x10 && request[5] == 0;

    public enum AlternativeFailure
    {
        None, DiscoveryProtocolError, CapabilitiesProtocolError, BatteryProtocolError, MalformedBattery,
        DiscoveryTimeout, CapabilitiesTimeout, BatteryTimeout, BatteryIoError, Disconnected
    }

    private sealed record BatteryScenario
    {
        public bool UnifiedPercentage { get; init; } = true;
        public byte UnifiedLevel { get; init; } = 4;
        public bool StatusPresent { get; init; }
        public AlternativeFailure Failure { get; init; }
        public bool Legacy { get; init; }
        public byte? LegacyPercentage { get; init; }
        public byte LegacyLevel { get; init; } = 5;
    }

    private static HidEndpoint Endpoint(ushort productId) =>
        new("fake-usb-hidpp", 0x046D, productId, 0xFF00, 1, 20, 20, 0, "receiver-a");

    /// <summary>A scripted HID++ receiver, not a native transport or hardware simulator.</summary>
    private sealed class ReceiverTransport(HidEndpoint endpoint, byte slots = 1, byte targetSlot = 1,
        byte? pingError = null, bool timeoutPing = false, bool injectOtherSlotPing = false,
        bool useBatteryStatus = false, byte statusLevelCount = 100, byte statusFlags = 2, byte statusLevel = 0,
        BatteryScenario? scenario = null) : IHidTransport
    {
        private readonly Queue<byte[]> replies = new();
        private readonly BatteryScenario battery = scenario ?? new();
        private bool ioError;
        public HidEndpoint Endpoint { get; } = endpoint;
        public List<byte[]> Requests { get; } = [];

        public ValueTask WriteAsync(byte[] request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.ToArray());

            if (request[1] == 0xFF && request[2] == 0x83 && request[3] == 0xB5)
            {
                byte register = request[4];
                if (register == 0x03)
                    replies.Enqueue(Reply(request, [register, 0, 0, 0, 0, 0, slots]));
                else if (register is >= 0x20 and <= 0x25)
                    replies.Enqueue(register == 0x20 + targetSlot - 1
                        ? Reply(request, [register, 0, 0, 0x40, 0x81, 0, 0, 0])
                        : Error(request, 0x08));
                else if (register == 0x30 + targetSlot - 1)
                    replies.Enqueue(Reply(request, [register, 0x12, 0x34, 0x56, 0x78]));
                else throw new InvalidOperationException("Unexpected receiver register query.");
            }
            else if (request[1] == targetSlot && request[2] == 0 && (request[3] & 0xF0) == 0x10)
            {
                if (timeoutPing) return ValueTask.CompletedTask;
                if (pingError is not null) replies.Enqueue(Error(request, pingError.Value));
                else if (battery.Legacy) replies.Enqueue(Error(request, 0x01));
                else
                {
                    if (injectOtherSlotPing)
                    {
                        var otherSlot = Reply(request, [2, 0, request[6]]);
                        otherSlot[1] = 1;
                        replies.Enqueue(otherSlot);
                    }
                    replies.Enqueue(Reply(request, [2, 0, request[6]]));
                }
            }
            else if (request[1] == targetSlot && request[2] == 0 && (request[3] & 0xF0) == 0)
            {
                // Unit ID and device name are optional; the paired serial above remains authoritative.
                ushort featureId = (ushort)((request[4] << 8) | request[5]);
                if (featureId == 0x1000)
                {
                    if (battery.Failure == AlternativeFailure.DiscoveryTimeout) return ValueTask.CompletedTask;
                    if (battery.Failure == AlternativeFailure.DiscoveryProtocolError)
                    {
                        replies.Enqueue(FeatureError(request, 0x09));
                        return ValueTask.CompletedTask;
                    }
                }
                replies.Enqueue(Reply(request, featureId switch
                {
                    0x0003 or 0x0005 => [0, 0, 0],
                    0x1004 => useBatteryStatus ? [0, 0, 0] : [7, 0, 0],
                    0x1000 => useBatteryStatus || battery.StatusPresent ? [8, 0, 0] : [0, 0, 0],
                    _ => throw new InvalidOperationException("Unexpected feature discovery query.")
                }));
            }
            else if (request[1] == targetSlot && request[2] == 7)
                replies.Enqueue(Reply(request, (request[3] & 0xF0) switch
                {
                    0 => [0x0F, battery.UnifiedPercentage ? (byte)2 : (byte)0],
                    0x10 => [0, battery.UnifiedLevel, 0, 0], // SOC-capable zero is genuine.
                    _ => throw new InvalidOperationException("Unexpected unified-battery query.")
                }));
            else if (request[1] == targetSlot && request[2] == 8 && (useBatteryStatus || battery.StatusPresent))
            {
                int function = request[3] & 0xF0;
                if ((function == 0x10 && battery.Failure == AlternativeFailure.CapabilitiesTimeout) ||
                    (function == 0 && battery.Failure == AlternativeFailure.BatteryTimeout))
                    return ValueTask.CompletedTask;
                if (function == 0 && battery.Failure == AlternativeFailure.BatteryIoError)
                {
                    ioError = true;
                    return ValueTask.CompletedTask;
                }
                if ((function == 0x10 && battery.Failure == AlternativeFailure.CapabilitiesProtocolError) ||
                    (function == 0 && battery.Failure == AlternativeFailure.BatteryProtocolError))
                    replies.Enqueue(FeatureError(request, 0x09));
                else if (function == 0 && battery.Failure == AlternativeFailure.Disconnected)
                    replies.Enqueue(Error(request, 0x08));
                else replies.Enqueue(Reply(request, function switch
                {
                    0x10 => [statusLevelCount, statusFlags],
                    0 when battery.Failure == AlternativeFailure.MalformedBattery => [101, 0, 0],
                    0 => [statusLevel, 0, 0],
                    _ => throw new InvalidOperationException("Unexpected battery-status query.")
                }));
            }
            else if (request[1] == targetSlot && request[2] == 0x81 && battery.Legacy)
                replies.Enqueue(request[3] switch
                {
                    0x0D when battery.LegacyPercentage is not null => Reply(request, [battery.LegacyPercentage.Value, 0, 0x30]),
                    0x0D => Error(request, 0x02),
                    0x07 => Reply(request, [battery.LegacyLevel, 0, 0]),
                    _ => throw new InvalidOperationException("Unexpected legacy battery register.")
                });
            else throw new InvalidOperationException("Query was routed to an unpaired slot or unsupported address.");

            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ioError) throw new IOException("Scripted alternative battery I/O failure.");
            return replies.Count == 0
                ? throw new TimeoutException("No scripted HID++ reply.")
                : ValueTask.FromResult(replies.Dequeue());
        }

        private static byte[] Reply(byte[] request, byte[] parameters)
        {
            var report = new byte[20];
            report[0] = 0x11;
            report[1] = request[1];
            report[2] = request[2];
            report[3] = request[3];
            parameters.CopyTo(report, 4);
            return report;
        }

        private static byte[] Error(byte[] request, byte code) =>
            [0x10, request[1], 0x8F, request[2], request[3], code, 0];

        private static byte[] FeatureError(byte[] request, byte code) =>
            [0x10, request[1], 0xFF, request[2], request[3], code, 0];

        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
