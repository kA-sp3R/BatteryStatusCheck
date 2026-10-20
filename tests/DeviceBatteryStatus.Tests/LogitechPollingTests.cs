using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class LogitechPollingTests
{
    private static readonly HidEndpoint Endpoint = new("fake-logitech", 0x046D, 0xC547,
        0xFF00, 1, 20, 20, 0, "receiver-logitech");
    private static readonly PeripheralIdentity Mouse = new("receiver-logitech/logitech/1/unit/12345678",
        "Synthetic mouse", DeviceVendor.Logitech, Endpoint.ReceiverId);

    [Fact]
    public void CapabilitiesUseOnlyAdvertisedBitsAndKeepTheirExactUnits()
    {
        Assert.Equal(new[] { 125, 250, 500, 1000 },
            LogitechPollingProtocol.DecodeSupportedRates(0x8060, [0x8B]));
        Assert.Equal(new[] { 125, 200, 250, 500, 1000 },
            LogitechPollingProtocol.DecodeSupportedRates(0x8060, [0xFF]));
        Assert.Equal(new[] { 125, 500, 8000 },
            LogitechPollingProtocol.DecodeSupportedRates(0x8061, [0x00, 0x45]));
        Assert.Empty(LogitechPollingProtocol.DecodeSupportedRates(0x8061, [0x01, 0x00]));
    }

    [Fact]
    public void ExtendedZeroIsTheValid125HzEncodingAndLegacyFractionsAreNotRounded()
    {
        Assert.True(LogitechPollingProtocol.TryDecodeRate(0x8061, 0, out int rate));
        Assert.Equal(125, rate);
        Assert.True(LogitechPollingProtocol.TryEncodeRate(0x8061, 8000, out byte value));
        Assert.Equal(6, value);
        Assert.False(LogitechPollingProtocol.TryDecodeRate(0x8060, 3, out _));
        Assert.False(LogitechPollingProtocol.TryDecodeRate(0x8061, 7, out _));
        Assert.False(LogitechPollingProtocol.TryEncodeRate(0x8060, 8000, out _));
        Assert.Throws<InvalidDataException>(() => LogitechPollingProtocol.DecodeSupportedRates(0x8061, [0]));
    }

    [Theory]
    [InlineData("receiver-logitech/logitech/1/unknown/unknown")]
    [InlineData("receiver-logitech/logitech/1/4093/unknown")]
    [InlineData("receiver-logitech/logitech/01/unit/12345678")]
    [InlineData("receiver-logitech/logitech/0/unit/12345678")]
    [InlineData("receiver-logitech/logitech/7/unit/12345678")]
    [InlineData("receiver-logitech/logitech/1/unit/00000000")]
    [InlineData("other-receiver/logitech/1/unit/12345678")]
    public async Task UnknownOrMalformedIdentityNeverSendsAnyCommand(string id)
    {
        await using var transport = new FakeTransport();
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse with { Id = id }, 500, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ReadDiscoversActualExtendedOptionsWithoutChangingAnySetting()
    {
        await using var transport = new FakeTransport { CurrentCode = 6 };
        var info = await new LogitechPollingRateController().ReadAsync(Endpoint, transport, Mouse, CancellationToken.None);
        Assert.Equal(PollingRateState.Supported, info.State);
        Assert.Equal(8000, info.CurrentRateHz);
        Assert.Equal(new[] { 125, 250, 500, 1000, 2000, 4000, 8000 }, info.SupportedRatesHz);
        Assert.Equal(0, transport.SetCommands);
        Assert.Contains("unverified", info.Detail);
    }

    [Fact]
    public async Task DeliberateApplySendsExactlyOneSettingAndChecksTheSameMouseAgain()
    {
        await using var transport = new FakeTransport();
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 4000, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(4000, result.Info.CurrentRateHz);
        Assert.Equal(1, transport.SetCommands);
        Assert.Equal(5, transport.LastSetValue);
        Assert.Equal(3, transport.UnitReads);
        Assert.DoesNotContain(transport.Requests, report => report[2] == 4 && (report[3] & 0xF0) != 0x20);
    }

    [Fact]
    public async Task LegacyMillisecondsAreSetWithoutChangingProfiles()
    {
        await using var transport = new FakeTransport { Extended = false, CurrentCode = 1 };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 125, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(8, transport.LastSetValue);
        Assert.Equal(1, transport.SetCommands);
    }

    [Fact]
    public async Task AFullReceiverSlotAndPairingSerialAreRevalidated()
    {
        var device = Mouse with { Id = "receiver-logitech/logitech/1/4093/89ABCDEF", ModelId = "4093" };
        await using var transport = new FakeTransport();
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            device, 500, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(3, transport.PairingReads);
        Assert.Equal(0, transport.UnitReads);
        Assert.Equal(1, transport.SetCommands);
    }

    [Fact]
    public async Task AChangedPairingSerialPreventsSettingAReplacementPeripheral()
    {
        var device = Mouse with { Id = "receiver-logitech/logitech/1/4093/11223344", ModelId = "4093" };
        await using var transport = new FakeTransport();
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            device, 500, () => true, CancellationToken.None);
        Assert.Equal(PollingRateState.Disconnected, result.Info.State);
        Assert.False(result.Verified);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task AReplacedUnitBetweenReadAndWriteIsNotChanged()
    {
        await using var transport = new FakeTransport { ChangeUnitOnRead = 2 };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, CancellationToken.None);
        Assert.Equal(PollingRateState.Disconnected, result.Info.State);
        Assert.False(result.Verified);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task ReplacementAfterWriteDoesNotProduceFalseVerification()
    {
        await using var transport = new FakeTransport { ChangeUnitOnRead = 3 };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(1, transport.SetCommands);
        Assert.Contains("may have reached", result.Detail);
    }

    [Fact]
    public async Task DisabledControlSendsNoQueriesOrSettings()
    {
        await using var transport = new FakeTransport();
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => false, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RevokedAuthorizationImmediatelyBeforeWritePreventsTheSetting()
    {
        await using var transport = new FakeTransport();
        int checks = 0;
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => ++checks < 3, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(3, checks);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task CancellationInsideTheLastAuthorizationCheckPreventsTheSetting()
    {
        using var cancellation = new CancellationTokenSource();
        await using var transport = new FakeTransport();
        int checks = 0;
        bool CanApply()
        {
            if (++checks == 3) cancellation.Cancel();
            return true;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogitechPollingRateController()
            .ApplyAsync(Endpoint, transport, Mouse, 500, CanApply, cancellation.Token));
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task ARateNotInTheFreshCapabilitiesIsNeverWritten()
    {
        await using var transport = new FakeTransport { Capabilities = 0x0F };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 8000, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task ChangedCapabilitiesAreCheckedAgainBeforeTheWrite()
    {
        await using var transport = new FakeTransport { RemoveHighRatesOnSecondCapabilities = true };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 8000, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(0, transport.SetCommands);
        Assert.Equal(2, transport.CapabilityReads);
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(3, 1, 0)]
    [InlineData(3, 2, 1)]
    public async Task KeyboardsOnboardProfilesAndUnknownRateVersionsAreRefused(byte kind, byte mode, byte version)
    {
        await using var transport = new FakeTransport { Kind = kind, Mode = mode, RateVersion = version };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Unsupported, result.Info.State);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task ASetTimeoutDoesNotReuseTheClosedSessionOrRetryTheSetting()
    {
        await using var transport = new FakeTransport { TimeoutOnSetReply = true };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Equal(1, transport.SetCommands);
        Assert.Equal(0, transport.ReadsAfterTimeout);
        Assert.Contains("may have reached", result.Detail);
    }

    [Fact]
    public async Task ForeignSlotRepliesTimeOutWithoutFallbackOrSettings()
    {
        await using var transport = new FakeTransport { ForeignSlotReplies = true };
        var info = await new LogitechPollingRateController().ReadAsync(Endpoint, transport, Mouse, CancellationToken.None);
        Assert.Equal(PollingRateState.Failed, info.State);
        Assert.Single(transport.Requests);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task MalformedPostWriteReadbackRetainsAnUnverifiedMayHaveChangedResult()
    {
        await using var transport = new FakeTransport { InvalidRateAfterSet = true };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PollingRateState.Failed, result.Info.State);
        Assert.Equal(1, transport.SetCommands);
        Assert.Contains("may have reached", result.Detail);
    }

    [Fact]
    public async Task AReadbackMismatchDoesNotTriggerAnotherWriteOrRollback()
    {
        await using var transport = new FakeTransport { IgnoreSet = true };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(1000, result.Info.CurrentRateHz);
        Assert.Equal(1, transport.SetCommands);
    }

    [Fact]
    public async Task AlreadySelectedRateRequiresNoSettingWrite()
    {
        await using var transport = new FakeTransport();
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 1000, () => true, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(0, transport.SetCommands);
    }

    [Fact]
    public async Task CancellationAfterSettingReportsUnverifiedInsteadOfRetrying()
    {
        using var cancellation = new CancellationTokenSource();
        await using var transport = new FakeTransport { AfterSetting = cancellation.Cancel };
        var result = await new LogitechPollingRateController().ApplyAsync(Endpoint, transport,
            Mouse, 500, () => true, cancellation.Token);
        Assert.False(result.Verified);
        Assert.Equal(1, transport.SetCommands);
        Assert.Contains("may have reached", result.Detail);
    }

    private sealed class FakeTransport : IHidTransport
    {
        private byte[] request = [];
        private bool timedOut;
        public HidEndpoint Endpoint => LogitechPollingTests.Endpoint;
        public List<byte[]> Requests { get; } = [];
        public bool Extended { get; init; } = true;
        public byte CurrentCode { get; set; } = 3;
        public byte Capabilities { get; init; } = 0x7F;
        public byte Kind { get; init; } = 3;
        public byte Mode { get; init; } = 2;
        public byte RateVersion { get; init; }
        public int ChangeUnitOnRead { get; init; } = int.MaxValue;
        public bool RemoveHighRatesOnSecondCapabilities { get; init; }
        public bool TimeoutOnSetReply { get; init; }
        public bool InvalidRateAfterSet { get; init; }
        public bool IgnoreSet { get; init; }
        public bool ForeignSlotReplies { get; init; }
        public Action? AfterSetting { get; init; }
        public int SetCommands { get; private set; }
        public byte LastSetValue { get; private set; }
        public int UnitReads { get; private set; }
        public int PairingReads { get; private set; }
        public int CapabilityReads { get; private set; }
        public int ReadsAfterTimeout { get; private set; }
        private bool IsSet => (request[2] == 5 && (request[3] & 0xF0) == 0x30) ||
            (request[2] == 6 && (request[3] & 0xF0) == 0x20);

        public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = report.ToArray();
            Requests.Add(request);
            if (IsSet)
            {
                SetCommands++;
                LastSetValue = request[4];
                if (!IgnoreSet) CurrentCode = LastSetValue;
                AfterSetting?.Invoke();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut) ReadsAfterTimeout++;
            if (IsSet && TimeoutOnSetReply) { timedOut = true; throw new TimeoutException("Synthetic set reply timeout."); }
            byte[] reply = new byte[20];
            reply[0] = 0x11; reply[1] = ForeignSlotReplies ? (byte)2 : request[1];
            reply[2] = request[2]; reply[3] = request[3];
            int function = request[3] & 0xF0;
            if (request[2] == 0 && function == 0x10)
            { reply[4] = 4; reply[5] = 2; reply[6] = request[6]; }
            else if (request[2] == 0 && function == 0)
            {
                int feature = (request[4] << 8) | request[5];
                reply[4] = (byte)(feature switch { 0x0003 => 2, 0x0005 => 3, 0x8100 => 4,
                    0x8061 when Extended => 5, 0x8060 when !Extended => 6, _ => 0 });
                if (feature is 0x8060 or 0x8061) reply[6] = RateVersion;
            }
            else if (request[2] == 2 && function == 0)
            {
                UnitReads++;
                reply[4] = 1; reply[5] = UnitReads >= ChangeUnitOnRead ? (byte)0x99 : (byte)0x12;
                reply[6] = 0x34; reply[7] = 0x56; reply[8] = 0x78;
            }
            else if (request[1] == 0xFF && request[2] == 0x83 && request[3] == 0xB5)
            {
                reply[4] = request[4];
                if (request[4] == 0x20) { PairingReads++; reply[7] = 0x40; reply[8] = 0x93; reply[11] = 2; }
                if (request[4] == 0x30) { reply[5] = 0x89; reply[6] = 0xAB; reply[7] = 0xCD; reply[8] = 0xEF; }
            }
            else if (request[2] == 3 && function == 0x20) reply[4] = Kind;
            else if (request[2] == 4 && function == 0x20) reply[4] = Mode;
            else if ((request[2] == 5 && function == 0x10) || (request[2] == 6 && function == 0))
            {
                CapabilityReads++;
                byte flags = RemoveHighRatesOnSecondCapabilities && CapabilityReads > 1 ? (byte)0x0F : Capabilities;
                if (request[2] == 5) reply[5] = flags; else reply[4] = 0x8B;
            }
            else if ((request[2] == 5 && function == 0x20) || (request[2] == 6 && function == 0x10))
                reply[4] = InvalidRateAfterSet && SetCommands > 0 ? (byte)0xFE : CurrentCode;
            return ValueTask.FromResult(reply);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
