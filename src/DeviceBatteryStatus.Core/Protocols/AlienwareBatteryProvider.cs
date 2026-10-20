using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

/// <summary>Source-backed AW Pro dongle reader; Windows transport and hardware remain unverified.</summary>
public sealed class AlienwareBatteryProvider : IBatteryProvider
{
    private const int QueryAttempts = 20;
    private static readonly TimeSpan QueryDelay = TimeSpan.FromMilliseconds(20);

    public string Name => "Alienware Pro headset control reports";

    public bool CanHandle(HidEndpoint endpoint) =>
        endpoint.VendorId == AlienwareProtocol.VendorId &&
        endpoint.ProductId == AlienwareProtocol.DongleProductId &&
        endpoint.UsagePage == AlienwareProtocol.UsagePage;

    public async Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint,
        IHidTransport transport, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = new PeripheralIdentity($"{endpoint.ReceiverId}:alienware:pro-headset",
            AlienwareSupport.DeviceName, DeviceVendor.Alienware, endpoint.ReceiverId, "413C:A528");
        if (!CanHandle(endpoint))
            return [new(identity, BatteryReading.Unavailable(ReadingState.Unsupported,
                "Only the documented 413C:A529 2.4 GHz dongle vendor collection is implemented."))];
        if (endpoint.OutputReportLength is < AlienwareProtocol.OutputReportLength or > 16384 ||
            endpoint.InputReportLength is < 17 or > 16384)
            return [new(identity, BatteryReading.Unavailable(ReadingState.Unsupported,
                "The HID descriptor cannot hold the documented 62-byte requests and complete identity/battery replies."))];

        try
        {
            var initialLink = AlienwareProtocol.ParseLink(await QueryAsync(transport,
                AlienwareProtocol.LinkCommand, cancellationToken));
            if (initialLink != ConnectionState.Connected) return [UnavailableLink(identity, initialLink)];

            var productId = AlienwareProtocol.ParseIdentityProductId(await QueryAsync(transport,
                AlienwareProtocol.IdentityCommand, cancellationToken));
            if (productId != AlienwareProtocol.WiredProductId)
                return [new(identity, BatteryReading.Unavailable(ReadingState.Unsupported,
                    $"Receiver answered, but its peripheral identity {productId:X4} is not the documented Pro headset A528."))];

            var battery = AlienwareProtocol.ParseBattery(await QueryAsync(transport,
                AlienwareProtocol.BatteryCommand, cancellationToken), observedAt);
            var finalLink = AlienwareProtocol.ParseLink(await QueryAsync(transport,
                AlienwareProtocol.LinkCommand, cancellationToken));
            if (finalLink != ConnectionState.Connected) return [UnavailableLink(identity, finalLink)];

            return [new(identity, battery, ConnectionState.Connected)];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            // Native timeouts may close this session. Keep the original failure and unknown
            // link state; a later refresh opens a new session instead of reusing a failed one.
            return [new(identity, BatteryReading.Unavailable(ReadingState.Failed,
                $"Alienware Pro battery query failed: {exception.Message}"))];
        }
    }

    private static DeviceSnapshot UnavailableLink(PeripheralIdentity identity, ConnectionState connection) =>
        new(identity, BatteryReading.Unavailable(connection == ConnectionState.Disconnected
                ? ReadingState.Disconnected : ReadingState.Failed,
            connection == ConnectionState.Disconnected
                ? "The receiver reports that its 2.4 GHz headset link is down. The dongle may remain plugged in."
                : "The receiver returned an unrecognized radio-link state; no current battery is reported."), connection);

    private static async Task<byte[]> QueryAsync(IHidTransport transport, byte command,
        CancellationToken cancellationToken)
    {
        await transport.SetOutputReportAsync(AlienwareProtocol.CreateRequest(command), cancellationToken);
        for (var attempt = 0; attempt < QueryAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(QueryDelay, cancellationToken);
            // Upstream's 63-byte Linux buffer is not evidence of the Windows descriptor maximum.
            var request = new byte[transport.Endpoint.InputReportLength];
            request[0] = 0x07;
            var reply = await transport.GetInputReportAsync(request, cancellationToken);
            if (reply.Length >= 3 && reply[0] == 0x07 && reply[1] == 0xC0 && reply[2] == command)
                return reply; // The command-specific decoder checks the remaining framing and length.
        }
        throw new TimeoutException($"No matching Alienware Pro reply to monitoring command {command:X2}.");
    }
}
