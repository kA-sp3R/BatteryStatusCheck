namespace DeviceBatteryStatus.Core.Hardware;

/// <summary>Reports include the Windows HID report-ID byte at offset zero.</summary>
public interface IHidTransport : IAsyncDisposable
{
    HidEndpoint Endpoint { get; }
    ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken);
    ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken);
    ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken);
    ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IHidTransportFactory
{
    ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken);
}

public interface IHidDiscovery
{
    IReadOnlyList<HidEndpoint> Enumerate();
    IReadOnlyList<string> Warnings => [];
}
