using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using System.Globalization;

namespace DeviceBatteryStatus.Core.Protocols;

public sealed class RazerBatteryProvider : IBatteryProvider
{
    public string Name => "Razer battery control reports";

    public static byte? GetExpectedInterfaceNumber(ushort productId) => RazerDeviceCatalog.TryGet(productId, out var model) ? model!.InterfaceNumber : null;

    public bool CanHandle(HidEndpoint endpoint)
    {
        if (endpoint.VendorId != 0x1532 || endpoint.FeatureReportLength != RazerProtocol.ReportLength)
            return false;
        // USB ancestry alone cannot distinguish a wireless receiver from a wired Razer device.
        // Keep documented wireless receiver paths; paired children are discovered independently.
        if (!RazerDeviceCatalog.TryGet(endpoint.ProductId, out _)) return false;
        var expectedInterface = GetExpectedInterfaceNumber(endpoint.ProductId);

        var marker = endpoint.DevicePath.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return true; // Non-composite HID paths have no MI component.
        return endpoint.DevicePath.Length >= marker + 6 &&
            byte.TryParse(endpoint.DevicePath.AsSpan(marker + 4, 2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var interfaceNumber) && interfaceNumber == expectedInterface;
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint,
        IHidTransport transport, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RazerDeviceCatalog.TryGet(endpoint.ProductId, out var model);
        var name = endpoint.ProductId == 0x00B3 ? "Razer HyperPolling receiver (paired mouse unknown)"
            : model?.Name ?? endpoint.ProductName ?? "Unknown Razer USB endpoint";
        var identity = new PeripheralIdentity($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}",
            name, DeviceVendor.Razer,
            endpoint.ReceiverId, $"1532:{endpoint.ProductId:X4}");

        if (!CanHandle(endpoint) || model is null)
        {
            var detail = endpoint.ProductId == 0x00B3
                ? "Paired mouse identity and radio link are unknown; no battery percentage is supplied. Manual polling control addresses the receiver's configuration."
                : "No documented protocol mapping for this Razer wireless endpoint.";
            return [new(identity, BatteryReading.Unavailable(ReadingState.Unsupported, detail))];
        }

        if (!model.IdentifiesPeripheralModel)
            return await ReadAssociatedReceiverAsync(endpoint, transport, observedAt, cancellationToken,
                discoveryTransaction: endpoint.ProductId == 0x00B3 ? null : model.TransactionId);

        try
        {
            var battery = await QueryAsync(transport, model.TransactionId,
                RazerProtocol.BatteryCommand, cancellationToken);
            if (battery.Status != RazerReplyStatus.Success)
            {
                var state = battery.Status == RazerReplyStatus.Unsupported
                    ? ReadingState.Unsupported : ReadingState.Failed;
                return [new(identity, BatteryReading.Unavailable(state,
                    $"Razer battery query: {battery.Status}. Receiver presence does not establish a connected peripheral."))];
            }

            var charging = ChargingState.Unknown;
            var sessionUsable = true;
            if (model.SupportsCharging)
            {
                try
                {
                    var response = await QueryAsync(transport, model.TransactionId,
                        RazerProtocol.ChargingCommand, cancellationToken);
                    if (response.Status == RazerReplyStatus.Success)
                        charging = response.Value switch
                        {
                            0 => ChargingState.Discharging,
                            1 => ChargingState.Charging,
                            _ => ChargingState.Unknown
                        };
                    if (response.Status == RazerReplyStatus.TimedOut) sessionUsable = false;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
                {
                    // Charging is independent: retain a successful battery sample, not an invented charge state.
                    sessionUsable = false;
                }
            }

            var reading = BatteryReading.Current(RazerProtocol.ToPercentage(battery.Value!.Value), charging, observedAt,
                "Reported battery value; the protocol supplies no battery sample timestamp. Radio link is unknown.");
            if (sessionUsable)
            {
                try
                {
                    byte discoveryTransaction = model.DeviceKind == RazerDeviceKind.Mouse ? (byte)0x01 : (byte)0x81;
                    var peers = await QueryAssociationAsync(transport, cancellationToken, discoveryTransaction);
                    if (peers.Status == RazerReplyStatus.Success)
                        return await ReadAssociatedReceiverAsync(endpoint, transport, observedAt, cancellationToken,
                            peers, discoveryTransaction);
                    if (peers.Status == RazerReplyStatus.Unsupported)
                    {
                        var link = await QueryConnectionAsync(transport, discoveryTransaction, cancellationToken);
                        if (link.Status == RazerReplyStatus.Success)
                            return [new(identity, link.Connection == ConnectionState.Disconnected
                                ? BatteryReading.Unavailable(ReadingState.Disconnected, "The peripheral reports its wireless link disconnected; battery data was discarded.")
                                : BatteryReading.Current(reading.Percentage!.Value, charging, observedAt,
                                    link.Connection == ConnectionState.Connected
                                        ? "Reported battery value; the wireless link was independently reported connected. No device battery timestamp is provided."
                                        : reading.Detail), link.Connection)];
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
                {
                    // The completed legacy sample remains useful, but a failed optional probe proves no live link.
                }
            }
            return [new(identity, reading)];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            return [new(identity, BatteryReading.Unavailable(ReadingState.Failed,
                $"Razer battery query failed: {exception.Message}"))];
        }
    }

    private static async Task<IReadOnlyList<DeviceSnapshot>> ReadAssociatedReceiverAsync(HidEndpoint endpoint,
        IHidTransport transport, DateTimeOffset observedAt, CancellationToken cancellationToken,
        RazerWirelessReply? association = null, byte? discoveryTransaction = null)
    {
        var receiverName = endpoint.ProductId == 0x00B3 ? "Razer HyperPolling receiver (paired mouse unknown)"
            : RazerDeviceCatalog.TryGet(endpoint.ProductId, out var receiverProfile)
                ? $"{receiverProfile!.Name} · receiver" : "Razer wireless receiver";
        var receiver = new PeripheralIdentity($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}",
            receiverName, DeviceVendor.Razer, endpoint.ReceiverId,
            $"1532:{endpoint.ProductId:X4}");
        PeripheralIdentity target = receiver;
        try
        {
            var initial = association ?? await QueryAssociationAsync(transport, cancellationToken, discoveryTransaction);
            if (initial.Status != RazerReplyStatus.Success)
                return [new(receiver, BatteryReading.Unavailable(initial.Status == RazerReplyStatus.Unsupported
                    ? ReadingState.Unsupported : ReadingState.Failed,
                    $"Receiver association query: {initial.Status}; paired identity and radio link are unknown."))];
            var connected = initial.Devices.Where(device => device.Connection == ConnectionState.Connected).ToArray();
            var snapshots = initial.Devices.Where(device => device.Connection == ConnectionState.Disconnected)
                .Select(device => new DeviceSnapshot(AssociatedIdentity(endpoint, device.ProductId),
                    BatteryReading.Unavailable(ReadingState.Disconnected, "Receiver reports this peripheral disconnected."),
                    ConnectionState.Disconnected)).ToList();
            if (connected.Length == 0)
            {
                if (snapshots.Count == 0)
                    snapshots.Add(new(receiver, BatteryReading.Unavailable(ReadingState.Disconnected,
                        "Receiver association reply reports no connected peripheral."), ConnectionState.Disconnected));
                return new ReceiverSnapshotBatch(snapshots, associationComplete: true);
            }
            if (connected.Length != 1 || endpoint.ProductId != 0x00B3 ||
                !RazerDeviceCatalog.TryGet(connected[0].ProductId, out var onlyChild) ||
                !onlyChild!.IdentifiesPeripheralModel || onlyChild.DeviceKind != RazerDeviceKind.Mouse)
            {
                return await ReadRoutedReceiverAsync(endpoint, transport, initial, observedAt, cancellationToken,
                    discoveryTransaction ?? 0x1F);
            }
            target = AssociatedIdentity(endpoint, connected[0].ProductId);
            var battery = await QueryAsync(transport, 0x1F, RazerProtocol.BatteryCommand, cancellationToken);
            if (battery.Status != RazerReplyStatus.Success)
            {
                snapshots.Add(new(target, BatteryReading.Unavailable(battery.Status == RazerReplyStatus.Unsupported
                    ? ReadingState.Unsupported : ReadingState.Failed, $"Paired peripheral battery query: {battery.Status}."),
                    ConnectionState.Connected));
                return new ReceiverSnapshotBatch(snapshots, associationComplete: true);
            }
            // A second device list binds the targetless battery reply to the same active child.
            var after = await QueryAssociationAsync(transport, cancellationToken, discoveryTransaction);
            var afterConnected = after.Devices.Where(device => device.Connection == ConnectionState.Connected).ToArray();
            if (after.Status != RazerReplyStatus.Success)
                return [new(target, BatteryReading.Unavailable(ReadingState.Failed,
                    "The receiver association could not be confirmed after the battery read."))];
            if (afterConnected.Length != 1 || afterConnected[0].ProductId != connected[0].ProductId)
            {
                snapshots.Add(new(target, BatteryReading.Unavailable(
                    afterConnected.All(device => device.ProductId != connected[0].ProductId)
                        ? ReadingState.Disconnected : ReadingState.Failed,
                    "Receiver association changed during the battery read; the value was discarded."),
                    afterConnected.All(device => device.ProductId != connected[0].ProductId)
                        ? ConnectionState.Disconnected : ConnectionState.Connected));
                return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
            }
            var charge = new AssociatedChargingResult(ChargingState.Unknown, true, false);
            if (!RazerDeviceCatalog.TryGet(connected[0].ProductId, out var childProfile) || childProfile!.SupportsCharging)
            {
                charge = await ReadAssociatedChargingAsync(transport, 0x1F, discoveryTransaction,
                    connected[0].ProductId, new HashSet<ushort> { connected[0].ProductId }, null, cancellationToken);
                if (charge.Association is not null)
                    ReconcileSnapshotLinks(snapshots, endpoint, charge.Association);
            }
            if (charge.Disconnected)
            {
                snapshots.Add(new(target, BatteryReading.Unavailable(ReadingState.Disconnected,
                    "Receiver reports this peripheral disconnected after its charging query; the battery value was discarded."),
                    ConnectionState.Disconnected));
                return new ReceiverSnapshotBatch(snapshots, associationComplete: charge.SessionUsable);
            }
            snapshots.Add(new(target, BatteryReading.Current(RazerProtocol.ToPercentage(battery.Value!.Value), charge.Charging, observedAt),
                ConnectionState.Connected));
            return new ReceiverSnapshotBatch(snapshots, associationComplete: charge.SessionUsable);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            return [new(target, BatteryReading.Unavailable(ReadingState.Failed,
                $"Receiver association/battery read failed: {exception.Message}"))];
        }
    }

    private static async Task<IReadOnlyList<DeviceSnapshot>> ReadRoutedReceiverAsync(HidEndpoint endpoint,
        IHidTransport transport, RazerWirelessReply initial, DateTimeOffset observedAt,
        CancellationToken cancellationToken, byte discoveryTransaction)
    {
        var connected = initial.Devices.Where(device => device.Connection == ConnectionState.Connected).ToArray();
        var snapshots = initial.Devices.Where(device => device.Connection == ConnectionState.Disconnected)
            .Select(device => new DeviceSnapshot(AssociatedIdentity(endpoint, device.ProductId),
                BatteryReading.Unavailable(ReadingState.Disconnected, "Receiver reports this peripheral disconnected."),
                ConnectionState.Disconnected)).ToList();
        RazerPrimaryDeviceReply primary;
        try { primary = await QueryPrimaryAsync(transport, discoveryTransaction, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            snapshots.AddRange(connected.Select(device => new DeviceSnapshot(AssociatedIdentity(endpoint, device.ProductId),
                BatteryReading.Unavailable(ReadingState.Failed, $"Receiver role query failed: {exception.Message}"),
                ConnectionState.Connected)));
            return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
        }
        if (primary.Status is not (RazerReplyStatus.Success or RazerReplyStatus.Unsupported))
        {
            snapshots.AddRange(connected.Select(device => new DeviceSnapshot(AssociatedIdentity(endpoint, device.ProductId),
                BatteryReading.Unavailable(ReadingState.Failed, $"Receiver role query: {primary.Status}."), ConnectionState.Connected)));
            return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
        }
        var roles = new Dictionary<ushort, byte>();
        bool legacySingle = primary.Status == RazerReplyStatus.Unsupported && connected.Length == 1 &&
            connected[0].ProductId == endpoint.ProductId && RazerDeviceCatalog.TryGet(endpoint.ProductId, out _);
        bool valid = legacySingle || (primary.Status == RazerReplyStatus.Success && primary.DeviceType is 1 or 2 &&
            initial.Devices.Any(device => device.ProductId == primary.ProductId));
        if (!legacySingle)
        {
            var pairedKinds = new List<byte>();
            foreach (var paired in initial.Devices)
            {
                byte? knownKind = RazerDeviceCatalog.TryGet(paired.ProductId, out var pairedProfile) && pairedProfile!.IdentifiesPeripheralModel
                    ? (byte)pairedProfile.DeviceKind : null;
                byte? reportedKind = paired.ProductId == primary.ProductId ? primary.DeviceType : knownKind;
                if (reportedKind is not (1 or 2) || (knownKind is not null && knownKind != reportedKind)) valid = false;
                else pairedKinds.Add(reportedKind.Value);
            }
            if (pairedKinds.Distinct().Count() != initial.Devices.Count) valid = false;
        }
        foreach (var device in connected)
        {
            byte? catalogKind = RazerDeviceCatalog.TryGet(device.ProductId, out var profile) && profile!.IdentifiesPeripheralModel
                ? (byte)profile.DeviceKind : null;
            byte? kind = device.ProductId == primary.ProductId ? primary.DeviceType : catalogKind;
            if (legacySingle) kind = catalogKind;
            if (kind is not (1 or 2) || (catalogKind is not null && kind != catalogKind)) valid = false;
            else roles[device.ProductId] = kind.Value;
        }
        if (roles.Values.Distinct().Count() != connected.Length) valid = false;
        if (!valid)
        {
            snapshots.AddRange(connected.Select(device => new DeviceSnapshot(AssociatedIdentity(endpoint, device.ProductId),
                BatteryReading.Unavailable(ReadingState.Unsupported,
                    "Receiver roles are unsupported or ambiguous; no untargeted battery value is attributed to these peripherals."),
                ConnectionState.Connected)));
            return new ReceiverSnapshotBatch(snapshots, associationComplete: true);
        }
        foreach (var device in connected)
        {
            var identity = AssociatedIdentity(endpoint, device.ProductId);
            try
            {
                var before = await QueryAssociationAsync(transport, cancellationToken, discoveryTransaction);
                if (before.Status != RazerReplyStatus.Success)
                    throw new InvalidDataException("Receiver association could not be checked before the routed read.");
                ReconcileSnapshotLinks(snapshots, endpoint, before);
                if (!before.Devices.Any(peer => peer.ProductId == device.ProductId && peer.Connection == ConnectionState.Connected))
                {
                    snapshots.Add(new(identity, BatteryReading.Unavailable(ReadingState.Disconnected,
                        "The selected peer disconnected before its routed battery read."), ConnectionState.Disconnected));
                    continue;
                }
                if (before.Devices.Any(peer => peer.Connection == ConnectionState.Connected && !roles.ContainsKey(peer.ProductId)))
                    throw new InvalidDataException("The receiver's connected peer set changed before the routed read.");
                // Manufacturer DualLink uses mouse namespace 0 and keyboard namespace 0x80, independent of master/slave order.
                byte transaction = legacySingle && RazerDeviceCatalog.TryGet(endpoint.ProductId, out var legacyProfile)
                    ? legacyProfile!.TransactionId : roles[device.ProductId] == 1 ? (byte)0x01 : (byte)0x81;
                var battery = await QueryAsync(transport, transaction, RazerProtocol.BatteryCommand, cancellationToken);
                if (battery.Status != RazerReplyStatus.Success)
                {
                    snapshots.Add(new(identity, BatteryReading.Unavailable(battery.Status == RazerReplyStatus.Unsupported
                        ? ReadingState.Unsupported : ReadingState.Failed, $"Routed battery query: {battery.Status}."), ConnectionState.Connected));
                    if (battery.Status == RazerReplyStatus.TimedOut)
                        return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
                    continue;
                }
                var after = await QueryAssociationAsync(transport, cancellationToken, discoveryTransaction);
                if (after.Status != RazerReplyStatus.Success)
                    throw new InvalidDataException("Receiver association could not be checked after the routed read.");
                ReconcileSnapshotLinks(snapshots, endpoint, after);
                if (!after.Devices.Any(peer => peer.ProductId == device.ProductId && peer.Connection == ConnectionState.Connected))
                {
                    snapshots.Add(new(identity, BatteryReading.Unavailable(ReadingState.Disconnected,
                        "The selected peer disconnected during its battery read; the cached value was discarded."), ConnectionState.Disconnected));
                    continue;
                }
                if (after.Devices.Any(peer => peer.Connection == ConnectionState.Connected && !roles.ContainsKey(peer.ProductId)))
                    throw new InvalidDataException("The receiver's connected peer set changed during the routed read.");
                if (!legacySingle)
                {
                    var roleAfter = await QueryPrimaryAsync(transport, discoveryTransaction, cancellationToken);
                    if (roleAfter.Status != RazerReplyStatus.Success || roleAfter.DeviceType != primary.DeviceType ||
                        roleAfter.ProductId != primary.ProductId)
                        throw new InvalidDataException("Receiver role identity changed during the routed battery read.");
                }
                var charge = new AssociatedChargingResult(ChargingState.Unknown, true, false);
                if (!RazerDeviceCatalog.TryGet(device.ProductId, out var child) || child!.SupportsCharging)
                {
                    charge = await ReadAssociatedChargingAsync(transport, transaction, discoveryTransaction,
                        device.ProductId, roles.Keys.ToHashSet(), legacySingle ? null : primary, cancellationToken);
                    if (charge.Association is not null)
                        ReconcileSnapshotLinks(snapshots, endpoint, charge.Association);
                }
                if (charge.Disconnected)
                {
                    snapshots.Add(new(identity, BatteryReading.Unavailable(ReadingState.Disconnected,
                        "The selected peer disconnected during its charging query; the battery value was discarded."),
                        ConnectionState.Disconnected));
                    if (!charge.SessionUsable) return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
                    continue;
                }
                snapshots.Add(new(identity, BatteryReading.Current(RazerProtocol.ToPercentage(battery.Value!.Value), charge.Charging, observedAt,
                    "Receiver device list and role namespace matched before and after this battery query; no device sample timestamp is provided."),
                    ConnectionState.Connected));
                if (!charge.SessionUsable) return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
            {
                snapshots.Add(new(identity, BatteryReading.Unavailable(ReadingState.Failed,
                    $"Routed peripheral query failed: {exception.Message}")));
                return new ReceiverSnapshotBatch(snapshots, associationComplete: false);
            }
        }
        return new ReceiverSnapshotBatch(snapshots, associationComplete: true);
    }

    private sealed record AssociatedChargingResult(ChargingState Charging, bool SessionUsable,
        bool Disconnected, RazerWirelessReply? Association = null);

    private static async Task<AssociatedChargingResult> ReadAssociatedChargingAsync(IHidTransport transport,
        byte transactionId, byte? discoveryTransaction, ushort productId, IReadOnlySet<ushort> expectedPeerIds,
        RazerPrimaryDeviceReply? expectedPrimary, CancellationToken cancellationToken)
    {
        RazerWirelessReply? after = null;
        try
        {
            var charge = await QueryAsync(transport, transactionId, RazerProtocol.ChargingCommand, cancellationToken);
            if (charge.Status != RazerReplyStatus.Success)
                return new(ChargingState.Unknown, charge.Status != RazerReplyStatus.TimedOut, false);

            // Charging replies carry no peripheral identity. Confirm the peer and namespace again before using one.
            after = await QueryAssociationAsync(transport, cancellationToken, discoveryTransaction);
            if (after.Status != RazerReplyStatus.Success)
                return new(ChargingState.Unknown, false, false);
            bool peerSetKnown = !after.Devices.Any(peer => peer.Connection == ConnectionState.Connected &&
                !expectedPeerIds.Contains(peer.ProductId));
            if (!after.Devices.Any(peer => peer.ProductId == productId && peer.Connection == ConnectionState.Connected))
                return new(ChargingState.Unknown, peerSetKnown, true, after);
            if (!peerSetKnown)
                return new(ChargingState.Unknown, false, false, after);
            if (expectedPrimary is not null)
            {
                var primary = await QueryPrimaryAsync(transport, discoveryTransaction!.Value, cancellationToken);
                if (primary.Status != RazerReplyStatus.Success || primary.DeviceType != expectedPrimary.DeviceType ||
                    primary.ProductId != expectedPrimary.ProductId)
                    return new(ChargingState.Unknown, false, false, after);
            }
            return new(charge.Value switch { 0 => ChargingState.Discharging, 1 => ChargingState.Charging, _ => ChargingState.Unknown },
                true, false, after);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            // The battery was already bound to this peer. Keep it, but neither invent charging nor reuse a failed session.
            return new(ChargingState.Unknown, false, false,
                after?.Status == RazerReplyStatus.Success ? after : null);
        }
    }

    private static void ReconcileSnapshotLinks(List<DeviceSnapshot> snapshots, HidEndpoint endpoint,
        RazerWirelessReply association)
    {
        var activeIds = association.Devices.Where(peer => peer.Connection == ConnectionState.Connected)
            .Select(peer => AssociatedIdentity(endpoint, peer.ProductId).Id).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < snapshots.Count; index++)
        {
            var previous = snapshots[index];
            if (activeIds.Contains(previous.Device.Id))
            {
                if (previous.Connection == ConnectionState.Disconnected)
                    snapshots[index] = new(previous.Device, BatteryReading.Unavailable(ReadingState.Unsupported,
                        "The peripheral reconnected during this refresh; a new battery sample is required after the disconnection."),
                        ConnectionState.Connected);
            }
            else if (previous.Connection == ConnectionState.Connected)
                snapshots[index] = new(previous.Device, BatteryReading.Unavailable(ReadingState.Disconnected,
                    "A later receiver device list reports this peripheral disconnected; the earlier value was discarded."),
                    ConnectionState.Disconnected);
        }
    }

    private static async Task<RazerPrimaryDeviceReply> QueryPrimaryAsync(IHidTransport transport,
        byte transactionId, CancellationToken cancellationToken)
    {
        RazerPrimaryDeviceReply? reply = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await transport.SetFeatureAsync(RazerWirelessProtocol.CreatePrimaryDeviceRequest(transactionId), cancellationToken);
            await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken);
            var report = await transport.GetFeatureAsync(new byte[RazerProtocol.ReportLength], cancellationToken);
            reply = RazerWirelessProtocol.ParsePrimaryDeviceReply(report, transactionId);
            if (reply.Status != RazerReplyStatus.Busy) return reply;
        }
        return reply!;
    }

    private static async Task<RazerConnectionReply> QueryConnectionAsync(IHidTransport transport,
        byte transactionId, CancellationToken cancellationToken)
    {
        await transport.SetFeatureAsync(RazerWirelessProtocol.CreateConnectionRequest(transactionId), cancellationToken);
        await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken);
        var report = await transport.GetFeatureAsync(new byte[RazerProtocol.ReportLength], cancellationToken);
        return RazerWirelessProtocol.ParseConnectionReply(report, transactionId);
    }

    private static PeripheralIdentity AssociatedIdentity(HidEndpoint endpoint, ushort productId)
    {
        var name = RazerDeviceCatalog.TryGet(productId, out var profile) && profile!.IdentifiesPeripheralModel
            ? profile.Name : $"Razer wireless peripheral (1532:{productId:X4})";
        return new($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}/wireless/{productId:X4}", name,
            DeviceVendor.Razer, endpoint.ReceiverId, $"1532:{productId:X4}");
    }

    private static async Task<RazerWirelessReply> QueryAssociationAsync(IHidTransport transport,
        CancellationToken cancellationToken, byte? transactionId = null)
    {
        bool legacy = false;
        RazerWirelessReply? reply = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await transport.SetFeatureAsync(RazerWirelessProtocol.CreateDeviceListRequest(legacy, transactionId), cancellationToken);
            await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken);
            var response = await transport.GetFeatureAsync(new byte[RazerProtocol.ReportLength], cancellationToken);
            reply = RazerWirelessProtocol.ParseDeviceListReply(response, legacy, transactionId);
            if (!legacy && transactionId is null && reply.Status == RazerReplyStatus.Unsupported)
            {
                legacy = true; // Only an explicit unsupported reply permits the documented legacy request.
                continue;
            }
            if (reply.Status != RazerReplyStatus.Busy) return reply;
        }
        return reply!;
    }

    private static async Task<RazerReply> QueryAsync(IHidTransport transport, byte transactionId,
        byte command, CancellationToken cancellationToken)
    {
        RazerReply? reply = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await transport.SetFeatureAsync(RazerProtocol.CreateRequest(transactionId, command), cancellationToken);
            await Task.Delay(RazerDeviceCatalog.GetResponseDelay(transport.Endpoint.ProductId), cancellationToken);
            var response = await transport.GetFeatureAsync(new byte[RazerProtocol.ReportLength], cancellationToken);
            reply = RazerProtocol.ParseReply(response, transactionId, command);
            if (reply.Status != RazerReplyStatus.Busy) return reply;
        }
        return reply!;
    }

}
