using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Polling;

/// <summary>Serializes manual settings with battery monitoring and resolves fresh receiver handles.</summary>
public sealed class PollingRateService(IHidDiscovery discovery, IHidTransportFactory transports,
    IReadOnlyList<IPollingRateController> controllers, HidOperationGate gate)
{
    public async Task<PollingRateInfo> ReadAsync(PeripheralIdentity device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (await gate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selection = Resolve(device);
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.Unavailable is not null) return selection.Unavailable;
                await using var transport = await transports.OpenAsync(selection.Endpoint!, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var info = await selection.Controller!.ReadAsync(selection.Endpoint!, transport, device,
                    cancellationToken).ConfigureAwait(false);
                return info.DeviceId == device.Id ? info
                    : Unavailable(device, PollingRateState.Failed, "The polling-rate reply did not identify the requested peripheral.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Reading the polling rate was canceled.", exception, cancellationToken);
        }
        catch (Exception exception)
        {
            return Unavailable(device, PollingRateState.Failed,
                $"Polling-rate information could not be read: {exception.Message}");
        }
    }

    public async Task<PollingApplyResult> ApplyAsync(PeripheralIdentity device, int rateHz,
        Func<bool> canApply, CancellationToken cancellationToken, Func<bool>? canSwitchToHostMode = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(canApply);
        try
        {
            EnsureAllowed(canApply, cancellationToken);
            if (rateHz <= 0)
                return NotApplied(Unavailable(device, PollingRateState.Unsupported, "Select a supported positive polling rate."));
            using (await gate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                EnsureAllowed(canApply, cancellationToken);
                var selection = Resolve(device);
                EnsureAllowed(canApply, cancellationToken);
                if (selection.Unavailable is not null) return NotApplied(selection.Unavailable);
                await using var transport = await transports.OpenAsync(selection.Endpoint!, cancellationToken).ConfigureAwait(false);
                EnsureAllowed(canApply, cancellationToken);
                // The controller must validate its target/options and recheck this guard before each setting command.
                var result = canSwitchToHostMode is not null && selection.Controller is IHostModePollingController hostMode
                    ? await hostMode.ApplyWithHostModeSwitchAsync(selection.Endpoint!, transport, device,
                        rateHz, canApply, canSwitchToHostMode, cancellationToken).ConfigureAwait(false)
                    : await selection.Controller!.ApplyAsync(selection.Endpoint!, transport, device,
                        rateHz, canApply, cancellationToken).ConfigureAwait(false);
                if (result.Info.DeviceId != device.Id)
                    return NotApplied(Unavailable(device, PollingRateState.Failed,
                        "The setting result did not identify the requested peripheral; the change could not be verified."));
                if (result.Verified && (result.Info.State != PollingRateState.Supported || result.Info.CurrentRateHz != rateHz ||
                    !result.Info.SupportedRatesHz.Contains(rateHz)))
                    return new(false, result.Info,
                        "The controller did not return a supported readback matching the requested rate; the change could not be verified.");
                return result;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The polling-rate operation was canceled.", exception, cancellationToken);
        }
        catch (Exception exception)
        {
            return NotApplied(Unavailable(device, PollingRateState.Failed,
                $"The polling-rate change could not be verified: {exception.Message} No retry was attempted."));
        }
    }

    private Selection Resolve(PeripheralIdentity device)
    {
        if (string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.ReceiverId))
            return new(null, null, Unavailable(device, PollingRateState.Unsupported, "A peripheral and receiver identity are required."));
        var endpoints = discovery.Enumerate().Where(endpoint =>
            string.Equals(endpoint.ReceiverId, device.ReceiverId, StringComparison.Ordinal)).ToArray();
        if (endpoints.Length == 0)
            return new(null, null, Unavailable(device,
                discovery.Warnings.Count == 0 ? PollingRateState.Disconnected : PollingRateState.Failed,
                discovery.Warnings.Count == 0 ? "The selected peripheral's USB receiver is no longer present."
                    : "Receiver discovery is incomplete; the selected receiver's presence could not be established."));
        var candidates = endpoints.SelectMany(endpoint => controllers
                .Where(controller => controller.CanHandle(endpoint, device))
                .Select(controller => (Endpoint: endpoint, Controller: controller)))
            .OrderByDescending(candidate => candidate.Endpoint.InputReportLength)
            .ThenByDescending(candidate => candidate.Endpoint.OutputReportLength)
            .ThenByDescending(candidate => candidate.Endpoint.FeatureReportLength)
            .ThenBy(candidate => candidate.Endpoint.DevicePath, StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length == 0
            ? new(null, null, Unavailable(device, PollingRateState.Unsupported,
                "No implemented polling-rate controller supports this peripheral and receiver collection."))
            : new(candidates[0].Endpoint, candidates[0].Controller, null);
    }

    private static void EnsureAllowed(Func<bool> canApply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!canApply())
            throw new OperationCanceledException("Manual polling-rate control is disabled; no setting write was authorized.", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static PollingRateInfo Unavailable(PeripheralIdentity device, PollingRateState state, string detail) =>
        new(device.Id, state, null, [], detail);

    private static PollingApplyResult NotApplied(PollingRateInfo info) => new(false, info, info.Detail);

    private sealed record Selection(HidEndpoint? Endpoint, IPollingRateController? Controller, PollingRateInfo? Unavailable);
}
