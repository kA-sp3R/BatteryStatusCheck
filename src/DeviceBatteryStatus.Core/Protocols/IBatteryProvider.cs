using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Protocols;

public interface IBatteryProvider
{
    string Name { get; }
    bool CanHandle(HidEndpoint endpoint);
    Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
        DateTimeOffset observedAt, CancellationToken cancellationToken);
}
