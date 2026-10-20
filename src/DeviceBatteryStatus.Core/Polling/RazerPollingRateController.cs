using System.Globalization;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Explicit, single-write polling adjustment for exact ordinary-protocol wireless receivers.</summary>
public sealed class RazerPollingRateController : IPollingRateController
{
    private static readonly IReadOnlyList<int> OrdinaryRates = Array.AsReadOnly(new[] { 125, 500, 1000 });
    private static readonly IReadOnlyDictionary<ushort, Transactions> Models = new Dictionary<ushort, Transactions>
    {
        [0x007B] = new(0xFF, 0xFF), // Viper Ultimate
        [0x007D] = new(0x3F, 0x3F), // DeathAdder V2 Pro
        [0x0083] = new(0xFF, 0xFF), // Basilisk X HyperSpeed
        [0x0088] = new(0xFF, 0x1F), // Basilisk Ultimate: GET and SET intentionally differ.
        [0x009C] = new(0x1F, 0x1F), // DeathAdder V2 X HyperSpeed
        [0x00A6] = new(0x1F, 0x1F), // Viper V2 Pro through its ordinary receiver
        [0x00AB] = new(0x1F, 0x1F), // Basilisk V3 Pro through its ordinary receiver
        [0x00B0] = new(0x1F, 0x1F), // Cobra Pro
        [0x00B7] = new(0x1F, 0x1F), // DeathAdder V3 Pro through its ordinary receiver
        [0x00B8] = new(0x1F, 0x1F), // Viper V3 HyperSpeed
        [0x00B9] = new(0x1F, 0x1F), // Basilisk V3 X HyperSpeed
        [0x00C3] = new(0x1F, 0x1F), // DeathAdder V3 Pro alternate receiver ID
        [0x00C5] = new(0x1F, 0x1F), // DeathAdder V3 HyperSpeed
        [0x00DB] = new(0x1F, 0x1F), // Cobra HyperSpeed
        [0x00DF] = new(0x1F, 0x1F)  // Viper V3 Pro SE: ordinary protocol, not C1's banked protocol.
    };

    public bool CanHandle(HidEndpoint endpoint, PeripheralIdentity device) =>
        endpoint.VendorId == 0x1532 && device.Vendor == DeviceVendor.Razer &&
        endpoint.FeatureReportLength == RazerPollingProtocol.ReportLength && Models.ContainsKey(endpoint.ProductId) &&
        IsControlCollection(endpoint.DevicePath) &&
        string.Equals(device.ReceiverId, endpoint.ReceiverId, StringComparison.Ordinal) &&
        string.Equals(device.Id, $"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}", StringComparison.Ordinal) &&
        string.Equals(device.ModelId, $"1532:{endpoint.ProductId:X4}", StringComparison.OrdinalIgnoreCase);

    public async Task<PollingRateInfo> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(endpoint, device)) return Unsupported(endpoint, device);
        if (!MatchesTransport(endpoint, transport.Endpoint))
            return Failed(device, "The open HID session does not match this receiver and peripheral identity.");

        try
        {
            var rate = await QueryRateAsync(transport, Models[endpoint.ProductId].Read, cancellationToken)
                .ConfigureAwait(false);
            return Supported(device, rate,
                "Receiver-reported polling configuration; 125, 500 and 1000 Hz are documented for this receiver. Radio link and physical polling frequency are unverified.");
        }
        catch (NotSupportedException exception)
        {
            return new(device.Id, PollingRateState.Unsupported, null, Array.Empty<int>(), exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            return Failed(device, exception.Message);
        }
    }

    public async Task<PollingApplyResult> ApplyAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, int rateHz, Func<bool> canApply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canApply);
        cancellationToken.ThrowIfCancellationRequested();
        var current = await ReadAsync(endpoint, transport, device, cancellationToken).ConfigureAwait(false);
        if (current.State != PollingRateState.Supported)
            return new(false, current, "The current receiver capabilities could not be validated; no setting was written.");
        if (!current.SupportedRatesHz.Contains(rateHz))
            return new(false, current, $"{rateHz} Hz is not supported by this receiver; no setting was written.");

        var writeStarted = false;
        try
        {
            var transactions = Models[endpoint.ProductId];
            var request = RazerPollingProtocol.CreateWriteRequest(transactions.Write, rateHz);
            cancellationToken.ThrowIfCancellationRequested();
            if (!canApply())
                return new(false, current, "Polling adjustment was disabled or the selected device changed; no setting was written.");
            cancellationToken.ThrowIfCancellationRequested();
            if (current.CurrentRateHz == rateHz)
                return new(true, current, "The receiver already reports the selected rate; no setting write was needed.");

            // There is exactly one configuration write, with no automatic retry or bank switching.
            writeStarted = true;
            await transport.SetFeatureAsync(request, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken).ConfigureAwait(false);
            var acknowledgement = await transport.GetFeatureAsync(new byte[RazerPollingProtocol.ReportLength], cancellationToken)
                .ConfigureAwait(false);
            RazerPollingProtocol.ValidateWriteReply(acknowledgement, transactions.Write);

            var actualRate = await QueryRateAsync(transport, transactions.Read, cancellationToken).ConfigureAwait(false);
            var verified = actualRate == rateHz;
            var detail = verified
                ? $"The receiver readback reports {actualRate} Hz. Physical polling frequency has not been measured."
                : $"Requested {rateHz} Hz, but the receiver readback reports {actualRate} Hz. No setting-write retry was sent.";
            return new(verified, Supported(device, actualRate, detail), detail);
        }
        catch (OperationCanceledException) when (writeStarted)
        {
            return Unverified(device,
                "Polling adjustment was cancelled after the setting write began. The setting may have changed; readback is unverified. No write retry was sent.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unverified(device, writeStarted
                ? $"A setting write was attempted, but verification failed: {exception.Message} The setting may have changed. No write retry was sent."
                : $"Polling adjustment failed before a setting write: {exception.Message}");
        }
    }

    private static async Task<int> QueryRateAsync(IHidTransport transport, byte transactionId,
        CancellationToken cancellationToken)
    {
        await transport.SetFeatureAsync(RazerPollingProtocol.CreateReadRequest(transactionId), cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken).ConfigureAwait(false);
        var reply = await transport.GetFeatureAsync(new byte[RazerPollingProtocol.ReportLength], cancellationToken)
            .ConfigureAwait(false);
        return RazerPollingProtocol.ParseReadReply(reply, transactionId);
    }

    private static bool IsControlCollection(string path)
    {
        var marker = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        return marker < 0 || (path.Length >= marker + 6 &&
            byte.TryParse(path.AsSpan(marker + 4, 2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var interfaceNumber) && interfaceNumber == 0);
    }

    private static bool MatchesTransport(HidEndpoint requested, HidEndpoint actual) =>
        requested.VendorId == actual.VendorId && requested.ProductId == actual.ProductId &&
        requested.FeatureReportLength == actual.FeatureReportLength &&
        string.Equals(requested.DevicePath, actual.DevicePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.ReceiverId, actual.ReceiverId, StringComparison.Ordinal);

    private static PollingRateInfo Supported(PeripheralIdentity device, int rate, string detail) =>
        new(device.Id, PollingRateState.Supported, rate, OrdinaryRates, detail);

    private static PollingRateInfo Failed(PeripheralIdentity device, string detail) =>
        new(device.Id, PollingRateState.Failed, null, Array.Empty<int>(), detail);

    private static PollingApplyResult Unverified(PeripheralIdentity device, string detail) =>
        new(false, Failed(device, detail), detail);

    private static PollingRateInfo Unsupported(HidEndpoint endpoint, PeripheralIdentity device) =>
        new(device.Id, PollingRateState.Unsupported, null, Array.Empty<int>(), endpoint.ProductId switch
        {
            0x00B3 => "Generic HyperPolling receivers have no trustworthy paired-device association here; no polling command was sent.",
            0x009F or 0x00C1 or 0x00BF => "This receiver uses a banked polling-rate protocol. A safe single-write adjustment has not been established; no polling command was sent.",
            _ => "No documented ordinary-protocol wireless mouse mapping matches this receiver, collection and peripheral identity; no polling command was sent."
        });

    private sealed record Transactions(byte Read, byte Write);
}
