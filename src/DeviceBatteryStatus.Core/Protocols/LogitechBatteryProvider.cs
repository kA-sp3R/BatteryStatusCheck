using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>Read-only battery queries through identified USB 2.4 GHz HID++ receivers.</summary>
public sealed class LogitechBatteryProvider : IBatteryProvider
{
    private static readonly HashSet<ushort> Unifying = [0xC52B, 0xC532];
    private static readonly HashSet<ushort> Lightspeed =
        [0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D];
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(700);
    private readonly Dictionary<string, (string PairingKey, PeripheralIdentity Identity)> knownDevices = [];
    private int softwareId = 7;

    public string Name => "Logitech HID++";

    public bool CanHandle(HidEndpoint endpoint) => endpoint.VendorId == 0x046D &&
        (Unifying.Contains(endpoint.ProductId) || Lightspeed.Contains(endpoint.ProductId)) &&
        endpoint.UsagePage == 0xFF00 && endpoint.Usage is 1 or 2 &&
        endpoint.OutputReportLength >= 7 && endpoint.InputReportLength >= 20;

    public async Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        List<DeviceSnapshot> devices = [];
        if (!CanHandle(endpoint)) return devices;

        try
        {
            int slots = Unifying.Contains(endpoint.ProductId) ? 6 : 1;
            LogitechReply receiverInfo = await ExchangeAsync(transport, 0xFF, 0x83, 0xB5,
                [0x03], cancellationToken, expectedEcho: 0x03);
            if (receiverInfo is { ErrorCode: null } && receiverInfo.Parameters.Length >= 7 &&
                receiverInfo.Parameters[6] is >= 1 and <= 6) slots = receiverInfo.Parameters[6];

            for (byte slot = 1; slot <= slots; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte pairingOffset = (byte)(0x20 + slot - 1);
                LogitechReply pairing = await ExchangeAsync(transport, 0xFF, 0x83, 0xB5,
                    [pairingOffset], cancellationToken, expectedEcho: pairingOffset);
                bool paired = pairing is { ErrorCode: null } && pairing.Parameters.Length >= 8;
                // Unifying pairing registers distinguish an empty slot from an asleep paired device.
                if (!paired && Unifying.Contains(endpoint.ProductId))
                {
                    if (!IsUnknownPairing(pairing))
                        throw new IOException($"Cannot identify the peripheral paired to Logitech slot {slot}.");
                    continue;
                }
                if (!paired && slot > 1) continue;

                string? wirelessId = paired ? Convert.ToHexString(pairing!.Parameters.AsSpan(3, 2)) : null;
                string? serial = null;
                if (paired)
                {
                    byte serialOffset = (byte)(0x30 + slot - 1);
                    LogitechReply extended = await ExchangeAsync(transport, 0xFF, 0x83, 0xB5,
                        [serialOffset], cancellationToken, expectedEcho: serialOffset);
                    if (extended is { ErrorCode: null } && extended.Parameters.Length >= 5)
                    {
                        string value = Convert.ToHexString(extended.Parameters.AsSpan(1, 4));
                        if (value != "00000000" && value != "FFFFFFFF") serial = value;
                    }
                }
                string displayName = $"Logitech peripheral · slot {slot}";
                string identity = $"{endpoint.ReceiverId}/logitech/{slot}/{wirelessId ?? "unknown"}/{serial ?? "unknown"}";
                PeripheralIdentity peripheral = new(identity, displayName, DeviceVendor.Logitech,
                    endpoint.ReceiverId, wirelessId);
                string slotKey = $"{endpoint.ReceiverId}/{slot}";
                string pairingKey = $"{wirelessId}/{serial}";
                if (serial is not null && knownDevices.TryGetValue(slotKey, out var known) && known.PairingKey == pairingKey)
                    peripheral = known.Identity;

                try
                {
                    byte marker = (byte)RandomNumberGenerator.GetInt32(1, 256);
                    byte pingFunction = NextFunction(0x10);
                    LogitechReply ping = await ExchangeAsync(transport, slot, 0, pingFunction,
                        [0, 0, marker], cancellationToken, pingMarker: marker);
                    if (ping.ErrorCode is not null && !(ping.LegacyError && ping.ErrorCode == 0x01))
                    {
                        BatteryReading failure = ErrorReading(ping, "Peripheral ping");
                        devices.Add(new(peripheral, failure, failure.State == ReadingState.Disconnected
                            ? ConnectionState.Disconnected : ConnectionState.Unknown));
                        continue;
                    }
                    bool legacy = ping.LegacyError && ping.ErrorCode == 0x01;
                    if (!legacy && (ping.Parameters.Length < 3 || ping.Parameters[0] < 2))
                    {
                        devices.Add(new(peripheral, BatteryReading.Unavailable(ReadingState.Unsupported,
                            "This receiver/device does not expose a supported HID++ protocol version."), ConnectionState.Connected));
                        continue;
                    }
                    if (!legacy)
                    {
                        (string? name, string? unitId) = await ReadIdentityAsync(transport, slot, cancellationToken);
                        if (name is not null) peripheral = peripheral with { Name = name };
                        if (unitId is not null && serial is null)
                            peripheral = peripheral with { Id = $"{endpoint.ReceiverId}/logitech/{slot}/unit/{unitId}" };
                    }
                    knownDevices[slotKey] = (pairingKey, peripheral);
                    BatteryQueryResult query = legacy
                        ? new(await ReadLegacyBatteryAsync(transport, slot, observedAt, cancellationToken))
                        : await ReadFeatureBatteryAsync(transport, slot, observedAt, cancellationToken);
                    BatteryReading battery = query.Reading;
                    devices.Add(new(peripheral, battery, battery.State == ReadingState.Disconnected
                        ? ConnectionState.Disconnected : ConnectionState.Connected));
                    if (query.StopSession) return devices;
                }
                catch (LogitechProtocolException ex)
                {
                    BatteryReading failure = ErrorReading(ex.Reply, "Feature discovery");
                    devices.Add(new(peripheral, failure, failure.State == ReadingState.Disconnected
                        ? ConnectionState.Disconnected : ConnectionState.Unknown));
                }
                catch (TimeoutException)
                {
                    devices.Add(new(peripheral, BatteryReading.Unavailable(ReadingState.Failed,
                        "HID++ request timed out; receiver presence does not prove the peripheral is connected.")));
                    return devices; // A timed-out native session is closed; do not reuse it.
                }
                catch (IOException ex)
                {
                    devices.Add(new(peripheral, BatteryReading.Unavailable(ReadingState.Failed, ex.Message)));
                    return devices;
                }
            }
            return devices;
        }
        catch (Exception ex) when (devices.Count > 0 && (ex is IOException or TimeoutException))
        {
            // Keep already measured slots; the monitor marks omitted prior slots stale.
            return devices;
        }
    }

    private async Task<BatteryQueryResult> ReadFeatureBatteryAsync(IHidTransport transport, byte slot,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        BatteryReading? levelOnly = null;
        byte? unified = await FeatureIndexAsync(transport, slot, 0x1004, cancellationToken);
        if (unified is not null)
        {
            LogitechReply capabilities = await FeatureAsync(transport, slot, unified.Value, 0, [], cancellationToken);
            if (capabilities.ErrorCode is not null) return new(ErrorReading(capabilities, "Battery capabilities"));
            if (capabilities.Parameters.Length < 2) return new(Malformed());
            LogitechReply battery = await FeatureAsync(transport, slot, unified.Value, 0x10, [], cancellationToken);
            if (battery.ErrorCode is not null) return new(ErrorReading(battery, "Unified battery"));
            BatteryReading result = LogitechHidppProtocol.ParseUnifiedBattery(battery.Parameters,
                (capabilities.Parameters[1] & 0x02) != 0, observedAt, capabilities.Parameters[0]);
            if (result.State != ReadingState.Current || result.Percentage is not null) return new(result);
            levelOnly = result;
        }

        // A category is already a valid current reading. The second feature is
        // optional: it can improve precision, but its failure must not erase it.
        try
        {
            byte? status = await FeatureIndexAsync(transport, slot, 0x1000, cancellationToken);
            if (status is not null)
            {
                // A byte-sized level is not evidence of mileage unless the device
                // advertises both sufficient resolution and the mileage flag.
                LogitechReply capabilities = await FeatureAsync(transport, slot, status.Value, 0x10, [], cancellationToken);
                if (capabilities.ErrorCode is not null)
                    return PreferCategory(levelOnly, ErrorReading(capabilities, "Battery-status capabilities"));
                if (capabilities.Parameters.Length < 2) return PreferCategory(levelOnly, Malformed());
                if (capabilities.Parameters[0] is < 2 or > 100)
                    return PreferCategory(levelOnly, BatteryReading.Unavailable(ReadingState.Failed,
                        "HID++ battery-status capabilities report an invalid level count."));
                bool supportsMileage = capabilities.Parameters[0] >= 10 && (capabilities.Parameters[1] & 0x02) != 0;
                LogitechReply battery = await FeatureAsync(transport, slot, status.Value, 0, [], cancellationToken);
                if (battery.ErrorCode is not null) return PreferCategory(levelOnly, ErrorReading(battery, "Battery status"));
                var reading = LogitechHidppProtocol.ParseBattery1000(battery.Parameters, observedAt, supportsMileage);
                return PreferCategory(levelOnly, reading);
            }
            return new(levelOnly is not null
                ? RetainCategory(levelOnly, "not advertised; no alternative percentage is available.")
                : BatteryReading.Unavailable(ReadingState.Unsupported,
                    "No supported battery feature (0x1004/0x1000). Voltage values are not converted to percentages."));
        }
        catch (LogitechProtocolException ex) when (levelOnly is not null)
        {
            return PreferCategory(levelOnly, ErrorReading(ex.Reply, "Alternative battery feature discovery"));
        }
        catch (Exception ex) when (levelOnly is not null && ex is IOException or TimeoutException or InvalidDataException)
        {
            // Native timeouts close the session. Keep this slot's completed
            // sample, then stop before another slot can reuse that transport.
            string outcome = ex is TimeoutException
                ? "query timed out; the completed category was retained and the HID session was stopped."
                : $"query failed ({ex.Message}); the completed category was retained and the HID session was stopped.";
            return new(RetainCategory(levelOnly, outcome), StopSession: true);
        }
    }

    private static BatteryQueryResult PreferCategory(BatteryReading? category, BatteryReading alternative)
    {
        if (category is null || alternative.State == ReadingState.Disconnected ||
            (alternative.State == ReadingState.Current && (alternative.Percentage is not null ||
                (category.Category == BatteryCategory.Unknown && alternative.Category != BatteryCategory.Unknown))))
            return new(alternative);
        return new(RetainCategory(category, alternative.State == ReadingState.Current
            ? "reports a category only; no alternative percentage is available."
            : $"did not provide a percentage: {alternative.Detail}"));
    }

    private static BatteryReading RetainCategory(BatteryReading category, string alternativeOutcome) =>
        BatteryReading.CurrentCategory(category.Category!.Value, category.Charging, category.SampledAt!.Value,
            $"{category.Detail} Alternative HID++ 0x1000 {alternativeOutcome}");

    private sealed record BatteryQueryResult(BatteryReading Reading, bool StopSession = false);

    private static async Task<BatteryReading> ReadLegacyBatteryAsync(IHidTransport transport, byte slot,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        LogitechReply battery = await ExchangeAsync(transport, slot, 0x81, 0x0D, [], cancellationToken);
        if (battery.ErrorCode is null) return LogitechHidppProtocol.ParseBattery0D(battery.Parameters, observedAt);
        if (battery.ErrorCode is not (0x01 or 0x02 or 0x03))
            return ErrorReading(battery, "HID++ 1.0 battery charge");

        LogitechReply category = await ExchangeAsync(transport, slot, 0x81, 0x07, [], cancellationToken);
        if (category.ErrorCode is null) return LogitechHidppProtocol.ParseBattery07(category.Parameters, observedAt);
        if (category.ErrorCode is 0x01 or 0x02 or 0x03)
            return BatteryReading.Unavailable(ReadingState.Unsupported,
                "This HID++ 1.0 peripheral exposes neither percentage register 0x0D nor category register 0x07.");
        return ErrorReading(category, "HID++ 1.0 battery category");
    }

    private async Task<(string? Name, string? UnitId)> ReadIdentityAsync(IHidTransport transport,
        byte slot, CancellationToken cancellationToken)
    {
        string? name = null, unitId = null;
        byte? fwFeature = await FeatureIndexAsync(transport, slot, 0x0003, cancellationToken);
        if (fwFeature is not null)
        {
            LogitechReply info = await FeatureAsync(transport, slot, fwFeature.Value, 0, [], cancellationToken);
            if (info.ErrorCode is null && info.Parameters.Length >= 5)
            {
                string candidate = Convert.ToHexString(info.Parameters.AsSpan(1, 4));
                if (candidate != "00000000" && candidate != "FFFFFFFF") unitId = candidate;
            }
        }
        byte? nameFeature = await FeatureIndexAsync(transport, slot, 0x0005, cancellationToken);
        if (nameFeature is not null)
        {
            LogitechReply length = await FeatureAsync(transport, slot, nameFeature.Value, 0, [], cancellationToken);
            if (length.ErrorCode is null && length.Parameters.Length > 0 && length.Parameters[0] is > 0 and <= 96)
            {
                List<byte> bytes = [];
                int expected = length.Parameters[0];
                while (bytes.Count < expected)
                {
                    LogitechReply chunk = await FeatureAsync(transport, slot, nameFeature.Value, 0x10,
                        [(byte)bytes.Count], cancellationToken);
                    if (chunk.ErrorCode is not null || chunk.Parameters.Length == 0) break;
                    bytes.AddRange(chunk.Parameters.Take(expected - bytes.Count));
                }
                if (bytes.Count == expected)
                {
                    string candidate = Encoding.UTF8.GetString(bytes.ToArray());
                    if (!candidate.Any(char.IsControl)) name = candidate;
                }
            }
        }
        return (name, unitId);
    }

    private async Task<byte?> FeatureIndexAsync(IHidTransport transport, byte slot, ushort feature,
        CancellationToken cancellationToken)
    {
        LogitechReply reply = await FeatureAsync(transport, slot, 0, 0,
            [(byte)(feature >> 8), (byte)feature], cancellationToken);
        if (reply.ErrorCode is not null) throw new LogitechProtocolException(reply);
        if (reply.Parameters.Length < 3) throw new IOException("Truncated HID++ feature discovery reply.");
        return reply.Parameters[0] == 0 || (reply.Parameters[1] & 0x80) != 0 ? null : reply.Parameters[0];
    }

    private Task<LogitechReply> FeatureAsync(IHidTransport transport, byte slot, byte feature, byte function,
        byte[] parameters, CancellationToken cancellationToken) =>
        ExchangeAsync(transport, slot, feature, NextFunction(function), parameters, cancellationToken);

    private byte NextFunction(byte function) => (byte)((function & 0xF0) | (8 + (Interlocked.Increment(ref softwareId) & 7)));

    private static async Task<LogitechReply> ExchangeAsync(IHidTransport transport, byte slot, byte feature,
        byte function, byte[] parameters, CancellationToken cancellationToken,
        byte? expectedEcho = null, byte? pingMarker = null)
    {
        byte[] request = LogitechHidppProtocol.CreateRequest(slot, feature, function, parameters);
        await transport.WriteAsync(request, cancellationToken);
        Stopwatch elapsed = Stopwatch.StartNew();
        // Bound both time and unrelated packet count when another HID++ client is active.
        for (int packets = 0; packets < 64; packets++)
        {
            var remaining = ReplyTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            byte[] report = await transport.ReadAsync(remaining, cancellationToken);
            if (!LogitechHidppProtocol.TryParseReply(report, slot, feature, function, out LogitechReply? reply)) continue;
            if (reply!.ErrorCode is not null) return reply;
            if (expectedEcho is not null && (reply.Parameters.Length == 0 || reply.Parameters[0] != expectedEcho)) continue;
            if (pingMarker is not null && (reply.Parameters.Length < 3 || reply.Parameters[2] != pingMarker)) continue;
            return reply;
        }
        throw new TimeoutException("No matching HID++ reply before the deadline.");
    }

    private static bool IsUnknownPairing(LogitechReply reply) => reply.LegacyError && reply.ErrorCode is 0x02 or 0x03 or 0x08;

    private static BatteryReading ErrorReading(LogitechReply reply, string operation) =>
        BatteryReading.Unavailable(reply.LegacyError && reply.ErrorCode is 0x04 or 0x08
            ? ReadingState.Disconnected : ReadingState.Failed,
            $"{operation}: HID++ {(reply.LegacyError ? "1.0" : "2.0")} error 0x{reply.ErrorCode:X2}.");

    private static BatteryReading Malformed() => BatteryReading.Unavailable(ReadingState.Failed,
        "Truncated HID++ battery capabilities reply.");

    private sealed class LogitechProtocolException(LogitechReply reply)
        : IOException($"HID++ protocol error 0x{reply.ErrorCode:X2}.")
    {
        public LogitechReply Reply { get; } = reply;
    }
}
