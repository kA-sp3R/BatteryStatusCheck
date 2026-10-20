using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Polling;

public enum PollingRateState { Supported, Unsupported, Disconnected, Failed }

public sealed record PollingRateInfo(string DeviceId, PollingRateState State, int? CurrentRateHz,
    IReadOnlyList<int> SupportedRatesHz, string Detail);

public sealed record PollingApplyResult(bool Verified, PollingRateInfo Info, string Detail);

/// <summary>Manual mouse/receiver configuration, independent of battery monitoring.</summary>
public interface IPollingRateController
{
    bool CanHandle(HidEndpoint endpoint, PeripheralIdentity device);
    Task<PollingRateInfo> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, CancellationToken cancellationToken);
    Task<PollingApplyResult> ApplyAsync(HidEndpoint endpoint, IHidTransport transport,
        PeripheralIdentity device, int rateHz, Func<bool> canApply, CancellationToken cancellationToken);
}
