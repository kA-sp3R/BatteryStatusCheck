using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using System.Globalization;

namespace DeviceBatteryStatus.Core.Protocols;

public sealed class RazerBatteryProvider : IBatteryProvider
{
    // Only documented wireless USB product IDs: never infer a protocol from a vendor name.
    private static readonly IReadOnlyDictionary<ushort, Model> Models = new Dictionary<ushort, Model>
    {
        [0x007B] = new("Razer Viper Ultimate", 0xFF),
        [0x007D] = new("Razer DeathAdder V2 Pro", 0x3F),
        [0x0083] = new("Razer Basilisk X HyperSpeed", 0xFF, false),
        [0x0088] = new("Razer Basilisk Ultimate", 0x1F),
        [0x009C] = new("Razer DeathAdder V2 X HyperSpeed", 0x1F, false),
        [0x009F] = new("Razer Viper Mini Signature Edition", 0x1F),
        [0x00A6] = new("Razer Viper V2 Pro", 0x1F),
        [0x00AB] = new("Razer Basilisk V3 Pro", 0x1F),
        [0x00B0] = new("Razer Cobra Pro", 0x1F),
        [0x00B7] = new("Razer DeathAdder V3 Pro", 0x1F),
        [0x00B8] = new("Razer Viper V3 HyperSpeed", 0x1F, false),
        [0x00B9] = new("Razer Basilisk V3 X HyperSpeed", 0x1F, false),
        [0x00BF] = new("Razer DeathAdder V4 Pro", 0x1F),
        [0x00C1] = new("Razer Viper V3 Pro", 0x1F),
        [0x00C3] = new("Razer DeathAdder V3 Pro", 0x1F),
        [0x00C5] = new("Razer DeathAdder V3 HyperSpeed", 0x1F),
        [0x00DB] = new("Razer Cobra HyperSpeed", 0x1F),
        [0x00DF] = new("Razer Viper V3 Pro SE", 0x1F),
        [0x025C] = new("Razer BlackWidow V3 Pro", 0x9F, InterfaceNumber: 2),
        [0x0271] = new("Razer BlackWidow V3 Mini HyperSpeed", 0x9F, InterfaceNumber: 3),
        [0x0290] = new("Razer DeathStalker V2 Pro", 0x9F, InterfaceNumber: 2),
        [0x0296] = new("Razer DeathStalker V2 Pro TKL", 0x9F, InterfaceNumber: 2),
        [0x02BA] = new("Razer BlackWidow V4 Mini HyperSpeed", 0x9F, InterfaceNumber: 3),
        [0x02D5] = new("Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x9F, InterfaceNumber: 2)
    };

    public string Name => "Razer battery control reports";

    public static byte? GetExpectedInterfaceNumber(ushort productId) =>
        Models.TryGetValue(productId, out var model) ? model.InterfaceNumber : null;

    public bool CanHandle(HidEndpoint endpoint)
    {
        if (endpoint.VendorId != 0x1532 || endpoint.FeatureReportLength != RazerProtocol.ReportLength)
            return false;
        var expectedInterface = GetExpectedInterfaceNumber(endpoint.ProductId);
        if (expectedInterface is null) return true; // Unknown models report Unsupported without protocol I/O.

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
        Models.TryGetValue(endpoint.ProductId, out var model);
        var identity = new PeripheralIdentity($"{endpoint.ReceiverId}:razer:{endpoint.ProductId:X4}",
            model?.Name ?? endpoint.ProductName ?? "Unknown Razer receiver", DeviceVendor.Razer,
            endpoint.ReceiverId, $"1532:{endpoint.ProductId:X4}");

        if (!CanHandle(endpoint) || model is null)
        {
            var detail = endpoint.ProductId == 0x00B3
                ? "HyperPolling receiver detected; its paired peripheral identity is not implemented."
                : "No documented protocol mapping for this Razer wireless endpoint.";
            return [new(identity, BatteryReading.Unavailable(ReadingState.Unsupported, detail))];
        }

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
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
                {
                    // Charging is independent: retain a successful battery sample, not an invented charge state.
                }
            }

            return [new(identity, BatteryReading.Current(RazerProtocol.ToPercentage(battery.Value!.Value),
                charging, observedAt))];
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return [new(identity, BatteryReading.Unavailable(ReadingState.Failed,
                $"Razer battery query failed: {exception.Message}"))];
        }
    }

    private static async Task<RazerReply> QueryAsync(IHidTransport transport, byte transactionId,
        byte command, CancellationToken cancellationToken)
    {
        RazerReply? reply = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await transport.SetFeatureAsync(RazerProtocol.CreateRequest(transactionId, command), cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
            var response = await transport.GetFeatureAsync(new byte[RazerProtocol.ReportLength], cancellationToken);
            reply = RazerProtocol.ParseReply(response, transactionId, command);
            if (reply.Status != RazerReplyStatus.Busy) return reply;
        }
        return reply!;
    }

    private sealed record Model(string Name, byte TransactionId, bool SupportsCharging = true,
        byte InterfaceNumber = 0);
}
