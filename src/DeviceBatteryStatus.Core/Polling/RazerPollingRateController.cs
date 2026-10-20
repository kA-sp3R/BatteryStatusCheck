using System.Globalization;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Manual, source-mapped polling configuration for Razer wireless receivers.</summary>
public sealed class RazerPollingRateController : IPollingRateController
{
    private static readonly IReadOnlyList<int> OrdinaryRates = Array.AsReadOnly(new[] { 125, 500, 1000 });
    private static readonly IReadOnlyList<int> HighRates = Array.AsReadOnly(new[] { 125, 500, 1000, 2000, 4000, 8000 });
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
        [0x00DF] = new(0x1F, 0x1F), // Viper V3 Pro SE uses the ordinary protocol.
        [0x009F] = new(0x1F, 0x1F, true, 0x1F), // Viper Mini Signature Edition
        [0x00C1] = new(0x1F, 0x1F, true, 0x1F), // Viper V3 Pro
        [0x00BF] = new(0x1F, 0x1F, true, 0x1F), // DeathAdder V4 Pro
        [0x00B3] = new(0x1F, 0x1F, true, 0xFF)  // HyperPolling: second setting phase has a distinct transaction.
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
            var profile = Models[endpoint.ProductId];
            var rate = await QueryRateAsync(transport, profile, cancellationToken).ConfigureAwait(false);
            return Supported(device, profile, rate, ConfigurationDetail(endpoint, profile));
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
            return new(false, current, $"{rateHz} Hz is not an option in this receiver's documented polling profile; no setting was written.");

        var writeStarted = false;
        try
        {
            var profile = Models[endpoint.ProductId];
            var request = profile.HighRate
                ? RazerPollingProtocol.CreateHighRateWriteRequest(profile.Write, 0, rateHz)
                : RazerPollingProtocol.CreateWriteRequest(profile.Write, rateHz);
            cancellationToken.ThrowIfCancellationRequested();
            if (!canApply())
                return new(false, current, "Polling adjustment was disabled or the selected device changed; no setting was written.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!profile.HighRate && current.CurrentRateHz == rateHz)
                return new(true, current, $"The receiver already reports the selected rate; no setting write was needed. {ConfigurationDetail(endpoint, profile)}");

            // Ordinary receivers need one command. High-rate receivers need the
            // documented phase-0 then phase-1 commands for a single deliberate Apply.
            writeStarted = true;
            await SendSettingAsync(transport, request, profile.Write, profile.HighRate, cancellationToken).ConfigureAwait(false);

            if (profile.SecondWrite is byte secondTransaction)
            {
                var secondRequest = RazerPollingProtocol.CreateHighRateWriteRequest(secondTransaction, 1, rateHz);
                cancellationToken.ThrowIfCancellationRequested();
                if (!canApply())
                    return Unverified(device,
                        "Polling adjustment was disabled or the selection changed after the first setting command. The setting may have changed or been partially applied; the second command was not sent. No retry or rollback was sent.");
                cancellationToken.ThrowIfCancellationRequested();
                await SendSettingAsync(transport, secondRequest, secondTransaction, true, cancellationToken).ConfigureAwait(false);
            }

            var actualRate = await QueryRateAsync(transport, profile, cancellationToken).ConfigureAwait(false);
            var verified = actualRate == rateHz;
            var detail = verified
                ? $"{(profile.HighRate ? "Both required setting commands were acknowledged" : "The setting command was acknowledged")}, and the receiver readback reports {actualRate} Hz. {ConfigurationDetail(endpoint, profile)}"
                : $"Requested {rateHz} Hz, but the receiver readback reports {actualRate} Hz. No setting-write retry or rollback was sent. {ConfigurationDetail(endpoint, profile)}";
            return new(verified, Supported(device, profile, actualRate, detail), detail);
        }
        catch (OperationCanceledException) when (writeStarted)
        {
            return Unverified(device,
                "Polling adjustment was cancelled after a setting command began. The setting may have changed or been partially applied; readback is unverified. No write retry or rollback was sent.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unverified(device, writeStarted
                ? $"A setting command was attempted, but verification failed: {exception.Message} The setting may have changed or been partially applied. No write retry or rollback was sent."
                : $"Polling adjustment failed before a setting write: {exception.Message}");
        }
    }

    private static async Task<int> QueryRateAsync(IHidTransport transport, Transactions profile,
        CancellationToken cancellationToken)
    {
        var request = profile.HighRate ? RazerPollingProtocol.CreateHighRateReadRequest(profile.Read)
            : RazerPollingProtocol.CreateReadRequest(profile.Read);
        await transport.SetFeatureAsync(request, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken).ConfigureAwait(false);
        var reply = await transport.GetFeatureAsync(new byte[RazerPollingProtocol.ReportLength], cancellationToken)
            .ConfigureAwait(false);
        return profile.HighRate ? RazerPollingProtocol.ParseHighRateReadReply(reply, profile.Read)
            : RazerPollingProtocol.ParseReadReply(reply, profile.Read);
    }

    private static async Task SendSettingAsync(IHidTransport transport, byte[] request,
        byte transactionId, bool highRate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transport.SetFeatureAsync(request, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken).ConfigureAwait(false);
        var acknowledgement = await transport.GetFeatureAsync(new byte[RazerPollingProtocol.ReportLength], cancellationToken)
            .ConfigureAwait(false);
        if (highRate) RazerPollingProtocol.ValidateHighRateWriteReply(acknowledgement, transactionId);
        else RazerPollingProtocol.ValidateWriteReply(acknowledgement, transactionId);
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

    private static PollingRateInfo Supported(PeripheralIdentity device, Transactions profile, int rate, string detail) =>
        new(device.Id, PollingRateState.Supported, rate, profile.HighRate ? HighRates : OrdinaryRates, detail);

    private static string ConfigurationDetail(HidEndpoint endpoint, Transactions profile) => endpoint.ProductId == 0x00B3
        ? "Receiver-scoped HyperPolling configuration; paired mouse model and radio link are unknown. Options use the documented receiver profile, not negotiated firmware capabilities. Apply sends both required commands even if the reported rate is unchanged. Physical polling frequency is unverified."
        : profile.HighRate
            ? "High-rate options use this receiver's documented source profile, not negotiated firmware capabilities. Apply sends both required commands even if the reported rate is unchanged. Radio link, receiver/firmware compatibility and physical polling frequency are unverified."
            : "Receiver-reported polling configuration; 125, 500 and 1000 Hz are documented for this receiver. Radio link and physical polling frequency are unverified.";

    private static PollingRateInfo Failed(PeripheralIdentity device, string detail) =>
        new(device.Id, PollingRateState.Failed, null, Array.Empty<int>(), detail);

    private static PollingApplyResult Unverified(PeripheralIdentity device, string detail) =>
        new(false, Failed(device, detail), detail);

    private static PollingRateInfo Unsupported(HidEndpoint endpoint, PeripheralIdentity device) =>
        new(device.Id, PollingRateState.Unsupported, null, Array.Empty<int>(),
            "No documented wireless receiver polling profile matches this receiver, collection and selected identity; no polling command was sent.");

    private sealed record Transactions(byte Read, byte Write, bool HighRate = false, byte? SecondWrite = null);
}
