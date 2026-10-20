namespace DeviceBatteryStatus.Core.Hardware;

/// <summary>Reports include the Windows HID report-ID byte at offset zero.</summary>
public interface IHidTransport : IAsyncDisposable
{
    HidEndpoint Endpoint { get; }
    ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken);
    ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken);
    // These control transfers use HID Input/Output report types, not Feature reports.
    ValueTask<byte[]> GetInputReportAsync(byte[] request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This transport does not implement control Input reports.");
    ValueTask SetOutputReportAsync(byte[] report, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This transport does not implement control Output reports.");
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
