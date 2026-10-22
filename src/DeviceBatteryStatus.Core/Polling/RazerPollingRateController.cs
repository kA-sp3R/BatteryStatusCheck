using System.Globalization;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Manual, source-mapped polling configuration for Razer wireless receivers.</summary>
public sealed class RazerPollingRateController : IPollingRateController
{
    private static readonly IReadOnlyList<int> OrdinaryRates = Array.AsReadOnly(new[] { 125, 500, 1000 });
    private static readonly IReadOnlyList<int> HighRates = Array.AsReadOnly(new[] { 125, 500, 1000, 2000, 4000, 8000 });
    private static readonly IReadOnlyDictionary<ushort, Transactions> Models = new Dictionary<ushort, Transactions>
    {
        [0x0025] = new(0xFF, 0xFF), // Mamba 2012 wireless
        [0x003F] = new(0xFF, 0xFF), // Naga Epic Chroma dock
        [0x0045] = new(0xFF, 0x3F), // Mamba Chroma: GET and SET intentionally differ.
        [0x005A] = new(0x3F, 0x3F), // Lancehead wireless
        [0x0062] = new(0x1F, 0x1F), // Atheris receiver
        [0x006F] = new(0x3F, 0x3F), // Lancehead Wireless receiver
        [0x0072] = new(0x3F, 0x3F), // Mamba Wireless receiver
        [0x0077] = new(0x1F, 0x1F), // Pro Click receiver
        [0x007B] = new(0xFF, 0xFF), // Viper Ultimate
        [0x007D] = new(0x3F, 0x3F), // DeathAdder V2 Pro
        [0x0083] = new(0xFF, 0xFF), // Basilisk X HyperSpeed
        [0x0088] = new(0xFF, 0x1F), // Basilisk Ultimate: GET and SET intentionally differ.
        [0x0090] = new(0x1F, 0x1F), // Naga Pro wireless
        [0x0094] = new(0x1F, 0x1F), // Orochi V2 receiver
        [0x009A] = new(0x1F, 0x1F), // Pro Click Mini receiver
        [0x009C] = new(0x1F, 0x1F), // DeathAdder V2 X HyperSpeed
        [0x00A6] = new(0x1F, 0x1F), // Viper V2 Pro through its ordinary receiver
        [0x00A8] = new(0x1F, 0x1F), // Naga V2 Pro wireless
        [0x00AB] = new(0x1F, 0x1F), // Basilisk V3 Pro through its ordinary receiver
        [0x00B0] = new(0x1F, 0x1F), // Cobra Pro
        [0x00B4] = new(0x1F, 0x1F), // Naga V2 HyperSpeed receiver
        [0x00B7] = new(0x1F, 0x1F), // DeathAdder V3 Pro through its ordinary receiver
        [0x00B8] = new(0x1F, 0x1F), // Viper V3 HyperSpeed
        [0x00B9] = new(0x1F, 0x1F), // Basilisk V3 X HyperSpeed
        [0x00C3] = new(0x1F, 0x1F), // DeathAdder V3 Pro alternate receiver ID
        [0x00C5] = new(0x1F, 0x1F), // DeathAdder V3 HyperSpeed
        [0x00C8] = new(0x1F, 0x1F), // Pro Click V2 Vertical Edition wireless
        [0x00CD] = new(0x1F, 0x1F), // Basilisk V3 Pro 35K wireless
        [0x00D1] = new(0x1F, 0x1F), // Pro Click V2 wireless
        [0x00D4] = new(0x1F, 0x1F), // Basilisk Mobile receiver
        [0x00D7] = new(0x1F, 0x1F), // Basilisk V3 Pro 35K Phantom Green wireless
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
        ((string.Equals(device.Id, $"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}", StringComparison.Ordinal) &&
          string.Equals(device.ModelId, $"1532:{endpoint.ProductId:X4}", StringComparison.OrdinalIgnoreCase)) ||
         TryBoundChild(endpoint, device, out _));

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
            await RequireBoundAssociationAsync(endpoint, transport, device, cancellationToken).ConfigureAwait(false);
            var rate = await QueryRateAsync(transport, profile, cancellationToken).ConfigureAwait(false);
            await RequireBoundAssociationAsync(endpoint, transport, device, cancellationToken).ConfigureAwait(false);
            return Supported(device, profile, rate, ConfigurationDetail(endpoint, device, profile));
        }
        catch (AssociationException exception)
        {
            return new(device.Id, exception.State, null, Array.Empty<int>(), exception.Message);
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
                return new(true, current, $"The receiver already reports the selected rate; no setting write was needed. {ConfigurationDetail(endpoint, device, profile)}");

            await RequireBoundAssociationAsync(endpoint, transport, device, cancellationToken).ConfigureAwait(false);
            if (TryBoundChild(endpoint, device, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!canApply())
                    return new(false, current, "Polling adjustment was disabled during the association check; no setting was written.");
                cancellationToken.ThrowIfCancellationRequested();
            }

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
                await RequireBoundAssociationAsync(endpoint, transport, device, cancellationToken).ConfigureAwait(false);
                if (TryBoundChild(endpoint, device, out _))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!canApply())
                        return Unverified(device,
                            "Polling adjustment was disabled during the association check after the first setting command. The setting may have changed or been partially applied; the second command was not sent. No retry or rollback was sent.");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await SendSettingAsync(transport, secondRequest, secondTransaction, true, cancellationToken).ConfigureAwait(false);
            }

            var actualRate = await QueryRateAsync(transport, profile, cancellationToken).ConfigureAwait(false);
            await RequireBoundAssociationAsync(endpoint, transport, device, cancellationToken).ConfigureAwait(false);
            var verified = actualRate == rateHz;
            var detail = verified
                ? $"{(profile.HighRate ? "Both required setting commands were acknowledged" : "The setting command was acknowledged")}, and the receiver readback reports {actualRate} Hz. {ConfigurationDetail(endpoint, device, profile)}"
                : $"Requested {rateHz} Hz, but the receiver readback reports {actualRate} Hz. No setting-write retry or rollback was sent. {ConfigurationDetail(endpoint, device, profile)}";
            return new(verified, Supported(device, profile, actualRate, detail), detail);
        }
        catch (AssociationException exception)
        {
            var detail = writeStarted
                ? $"{exception.Message} A setting command was attempted; the setting may have changed or been partially applied. No retry or rollback was sent."
                : $"{exception.Message} No setting was written.";
            return new(false, new(device.Id, exception.State, null, Array.Empty<int>(), detail), detail);
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
        await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken).ConfigureAwait(false);
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
        await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken).ConfigureAwait(false);
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

    private static string ConfigurationDetail(HidEndpoint endpoint, PeripheralIdentity device, Transactions profile) => endpoint.ProductId == 0x00B3
        ? TryBoundChild(endpoint, device, out ushort child)
            ? $"Receiver-scoped HyperPolling configuration, bound to freshly reported wireless product 1532:{child:X4}. Options use the documented receiver profile, not negotiated peripheral capabilities. Apply sends both required commands even if the reported rate is unchanged. Physical polling frequency is unverified."
            : "Receiver-scoped HyperPolling configuration; paired mouse model and radio link are unknown. Options use the documented receiver profile, not negotiated firmware capabilities. Apply sends both required commands even if the reported rate is unchanged. Physical polling frequency is unverified."
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

    private static bool TryBoundChild(HidEndpoint endpoint, PeripheralIdentity device, out ushort productId)
    {
        productId = 0;
        string prefix = $"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}/wireless/";
        if (!device.Id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string suffix = device.Id[prefix.Length..];
        if (!(suffix.Length == 4 && ushort.TryParse(suffix, NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture, out productId) && productId is not (0 or 0xFFFF) &&
               suffix == productId.ToString("X4", CultureInfo.InvariantCulture) &&
               string.Equals(device.ModelId, $"1532:{productId:X4}", StringComparison.OrdinalIgnoreCase))) return false;
        bool knownMouse = RazerDeviceCatalog.TryGet(productId, out var profile) &&
                          profile!.IdentifiesPeripheralModel && profile.DeviceKind == RazerDeviceKind.Mouse;
        return endpoint.ProductId == 0x00B3
            ? !RazerDeviceCatalog.TryGet(productId, out _) || knownMouse
            : productId == endpoint.ProductId && Models.ContainsKey(endpoint.ProductId) && knownMouse;
    }

    private static async Task RequireBoundAssociationAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, CancellationToken cancellationToken)
    {
        if (!TryBoundChild(endpoint, device, out ushort expectedProduct)) return;
        bool ordinaryReceiver = endpoint.ProductId != 0x00B3;
        byte? associationTransaction = ordinaryReceiver ? (byte)0x01 : null;
        var reply = await QueryWirelessDevicesAsync(transport, false, cancellationToken, associationTransaction).ConfigureAwait(false);
        if (!ordinaryReceiver && reply.Status == RazerReplyStatus.Unsupported)
            reply = await QueryWirelessDevicesAsync(transport, true, cancellationToken).ConfigureAwait(false);
        if (reply.Status != RazerReplyStatus.Success)
            throw new AssociationException(reply.Status == RazerReplyStatus.Unsupported
                    ? PollingRateState.Unsupported : PollingRateState.Failed,
                $"The receiver could not freshly validate its selected wireless peripheral ({reply.Status}).");
        if (reply.Devices.Any(child => child.Connection == ConnectionState.Unknown))
            throw new AssociationException(PollingRateState.Failed, "The receiver returned an unknown wireless link state.");
        var connected = reply.Devices.Where(child => child.Connection == ConnectionState.Connected).ToArray();
        if (connected.Length > 1)
            throw new AssociationException(PollingRateState.Unsupported,
                "Multiple wireless peripherals are connected; a receiver-wide polling command cannot be attributed to the selected peripheral.");
        if (connected.Length == 0 || connected[0].ProductId != expectedProduct)
            throw new AssociationException(PollingRateState.Disconnected,
                "The selected wireless peripheral is disconnected or the receiver now addresses a different product.");
        if (ordinaryReceiver || !RazerDeviceCatalog.TryGet(expectedProduct, out _))
        {
            // A sole BF entry proves the product/link, not its mouse/keyboard role.
            // Require the manufacturer's C5 role report before using a mouse-only rate command.
            var primary = await QueryPrimaryDeviceAsync(transport, cancellationToken,
                associationTransaction ?? 0x1F).ConfigureAwait(false);
            // A documented same-model mouse plus its sole connected BF entry also
            // proves the role on older firmware that explicitly lacks C5.
            if (ordinaryReceiver && primary.Status == RazerReplyStatus.Unsupported) return;
            if (primary.Status != RazerReplyStatus.Success)
                throw new AssociationException(primary.Status == RazerReplyStatus.Unsupported
                        ? PollingRateState.Unsupported : PollingRateState.Failed,
                    $"The receiver could not freshly prove the selected wireless product's mouse role ({primary.Status}).");
            if (primary.ProductId != expectedProduct)
                throw new AssociationException(PollingRateState.Disconnected,
                    "The receiver's primary product changed; its mouse-only polling command cannot be attributed to the selected peripheral.");
            if (primary.DeviceType != 1)
                throw new AssociationException(PollingRateState.Unsupported,
                    "The selected wireless product does not report the mouse role required for this polling command.");
        }
    }

    private static async Task<RazerPrimaryDeviceReply> QueryPrimaryDeviceAsync(IHidTransport transport,
        CancellationToken cancellationToken, byte transaction = 0x1F)
    {
        RazerPrimaryDeviceReply? reply = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await transport.SetFeatureAsync(RazerWirelessProtocol.CreatePrimaryDeviceRequest(transaction), cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken).ConfigureAwait(false);
            var response = await transport.GetFeatureAsync(new byte[RazerPollingProtocol.ReportLength], cancellationToken)
                .ConfigureAwait(false);
            reply = RazerWirelessProtocol.ParsePrimaryDeviceReply(response, transaction);
            if (reply.Status != RazerReplyStatus.Busy) return reply;
        }
        return reply!;
    }

    private static async Task<RazerWirelessReply> QueryWirelessDevicesAsync(IHidTransport transport, bool legacy,
        CancellationToken cancellationToken, byte? transactionId = null)
    {
        RazerWirelessReply? reply = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await transport.SetFeatureAsync(RazerWirelessProtocol.CreateDeviceListRequest(legacy, transactionId), cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken).ConfigureAwait(false);
            var response = await transport.GetFeatureAsync(new byte[RazerPollingProtocol.ReportLength], cancellationToken)
                .ConfigureAwait(false);
            reply = RazerWirelessProtocol.ParseDeviceListReply(response, legacy, transactionId);
            if (reply.Status != RazerReplyStatus.Busy) return reply;
        }
        return reply!;
    }

    private sealed class AssociationException(PollingRateState state, string message) : Exception(message)
    {
        public PollingRateState State { get; } = state;
    }
}
