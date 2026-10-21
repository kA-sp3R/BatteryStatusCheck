using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.Core.Monitoring;

/// <summary>Serializes refreshes and retains failed samples as explicitly stale.</summary>
public sealed class BatteryMonitor(IHidDiscovery discovery, IHidTransportFactory transports,
    IReadOnlyList<IBatteryProvider> providers, HidOperationGate? operationGate = null)
{
    private readonly HidOperationGate gate = operationGate ?? new();
    private readonly Dictionary<string, DeviceSnapshot> devices = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceSnapshot> published = Array.Empty<DeviceSnapshot>();
    public IReadOnlyList<DeviceSnapshot> Devices => Volatile.Read(ref published);
    public string? DiscoveryError { get; private set; }

    public async Task<IReadOnlyList<DeviceSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        using (await gate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<HidEndpoint> endpoints;
            try
            {
                endpoints = discovery.Enumerate();
                cancellationToken.ThrowIfCancellationRequested();
                DiscoveryError = discovery.Warnings.Count == 0 ? null
                    : $"Receiver discovery is incomplete: {string.Join("; ", discovery.Warnings)}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiscoveryError = $"Receiver discovery failed: {ex.Message}";
                foreach (var old in devices.Values.ToArray())
                    devices[old.Device.Id] = WithFailure(old, DiscoveryError);
                return Publish();
            }

            var candidates = endpoints.SelectMany(endpoint => providers
                    .Where(provider => provider.CanHandle(endpoint))
                    .Select(provider => (Endpoint: endpoint, Provider: provider)))
                .GroupBy(x => (x.Endpoint.ReceiverId, x.Provider.Name))
                .Select(group => group.OrderByDescending(x => x.Endpoint.InputReportLength)
                    .ThenByDescending(x => x.Endpoint.OutputReportLength).First()).ToArray();

            var presentReceivers = endpoints.Select(x => x.ReceiverId).ToHashSet(StringComparer.Ordinal);
            var queryableReceivers = candidates.Select(x => x.Endpoint.ReceiverId).ToHashSet(StringComparer.Ordinal);
            foreach (var old in devices.Values.ToArray())
                if (!presentReceivers.Contains(old.Device.ReceiverId))
                    devices[old.Device.Id] = DiscoveryError is not null
                        ? WithFailure(old, "Receiver absence cannot be confirmed because discovery is incomplete.")
                        : old with
                        {
                            Battery = BatteryReading.Unavailable(ReadingState.Disconnected, "USB receiver removed."),
                            Connection = ConnectionState.Disconnected
                        };
                else if (!queryableReceivers.Contains(old.Device.ReceiverId))
                    devices[old.Device.Id] = WithFailure(old, "Receiver is present but its supported HID collection is unavailable.");

            // One session per receiver: vendors share a request/response channel across collections.
            foreach (var (endpoint, provider) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prior = devices.Values.Where(x => x.Device.ReceiverId == endpoint.ReceiverId).ToArray();
                try
                {
                    await using var transport = await transports.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    var snapshots = await provider.ReadAsync(endpoint, transport, DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (snapshots.Count > 0 && snapshots.All(x => !x.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal)))
                    {
                        foreach (var placeholder in prior.Where(x => x.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal)))
                            devices.Remove(placeholder.Device.Id);
                        prior = prior.Where(x => !x.Device.Id.EndsWith(":unidentified", StringComparison.Ordinal)).ToArray();
                    }
                    foreach (var incoming in snapshots)
                    {
                        devices.TryGetValue(incoming.Device.Id, out var previous);
                        devices[incoming.Device.Id] = incoming.Battery.State == ReadingState.Failed && previous is not null
                            ? WithFailure(previous, incoming.Battery.Detail ?? "Read failed.") with
                            {
                                Device = incoming.Device,
                                Connection = incoming.Connection
                            }
                            : incoming;
                    }
                    foreach (var missing in prior.Where(x => snapshots.All(y => y.Device.Id != x.Device.Id)))
                        devices[missing.Device.Id] = snapshots is ReceiverSnapshotBatch { AssociationComplete: true }
                            ? missing with
                            {
                                Battery = BatteryReading.Unavailable(ReadingState.Disconnected,
                                    "A completed receiver association scan no longer reports this peripheral."),
                                Connection = ConnectionState.Disconnected
                            }
                            : missing.Battery.State == ReadingState.Disconnected && missing.Connection == ConnectionState.Disconnected
                                ? missing
                                : WithFailure(missing, "Receiver did not return this peripheral in this refresh.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (prior.Length > 0)
                    {
                        foreach (var old in prior) devices[old.Device.Id] = WithFailure(old, ex.Message);
                    }
                    else
                    {
                        var vendor = endpoint.VendorId switch
                        {
                            0x1532 => DeviceVendor.Razer,
                            0x413C => DeviceVendor.Alienware,
                            _ => DeviceVendor.Logitech
                        };
                        var id = new PeripheralIdentity($"{endpoint.ReceiverId}:unidentified",
                            $"{provider.Name} receiver (peripheral unidentified)", vendor, endpoint.ReceiverId);
                        devices[id.Id] = new(id, BatteryReading.Unavailable(ReadingState.Failed, ex.Message));
                    }
                }
            }
            return Publish();
        }
    }

    private IReadOnlyList<DeviceSnapshot> Publish()
    {
        IReadOnlyList<DeviceSnapshot> next = Array.AsReadOnly(devices.Values.OrderBy(x => x.Device.Name).ToArray());
        Volatile.Write(ref published, next);
        return next;
    }

    private static DeviceSnapshot WithFailure(DeviceSnapshot previous, string detail) => previous with
    {
        Battery = previous.Battery.HasMeasurement
            ? previous.Battery.AsStale(detail)
            : BatteryReading.Unavailable(ReadingState.Failed, detail),
        Connection = ConnectionState.Unknown
    };
}
