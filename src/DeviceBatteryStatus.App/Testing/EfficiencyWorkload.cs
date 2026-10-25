using System.Diagnostics;
using System.Runtime.InteropServices;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.App.Testing;

/// <summary>A short, deterministic fake-HID workload; it never opens a Windows device.</summary>
internal static class EfficiencyWorkload
{
    private const int Trials = 200;
    private const int TicksPerTrial = 40;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    internal sealed record ProcessSample(double CpuMilliseconds, long WorkingSetBytes,
        long PrivateBytes, long ManagedHeapBytes, long TotalAllocatedBytes, int ProcessHandles,
        int GdiHandles, int UserHandles, int Gen0Collections, int Gen1Collections, int Gen2Collections);

    internal sealed record RunResult(string Policy, int Trials, int ScheduledRefreshes,
        int DiscoveryEnumerations, int TransportOpens, int HealthyReads, int NonResponsiveReads,
        int FeatureQueries, double WallMilliseconds, double CpuMilliseconds, long AllocatedBytes,
        ProcessSample Before, ProcessSample After)
    {
        public double FeatureQueriesPerSimulatedMinute =>
            FeatureQueries / (Trials * TicksPerTrial * TickInterval.TotalMinutes);
    }

    internal static async Task<object> RunAsync(CancellationToken cancellationToken)
    {
        // Warm both policies before measurement. CPU and memory are still short-run,
        // process-wide observations, not a physical-device or endurance benchmark.
        await ExecuteAsync(optimized: false, 3, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(optimized: true, 3, cancellationToken).ConfigureAwait(false);
        var legacy = await MeasureAsync(optimized: false, cancellationToken).ConfigureAwait(false);
        var optimized = await MeasureAsync(optimized: true, cancellationToken).ConfigureAwait(false);
        if (legacy.HealthyReads != optimized.HealthyReads || legacy.HealthyReads != Trials * TicksPerTrial ||
            optimized.DiscoveryEnumerations >= legacy.DiscoveryEnumerations ||
            optimized.NonResponsiveReads >= legacy.NonResponsiveReads ||
            optimized.FeatureQueries >= legacy.FeatureQueries)
            throw new InvalidOperationException("Synthetic efficiency workload did not reduce redundant work while retaining healthy polling.");

        var recovery = await VerifyRecoveryAndFreshnessAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            schemaVersion = 1,
            workload = "deterministic-synthetic-healthy-and-nonresponsive-usb-receivers",
            physicalDevicesAccessed = false,
            normalApplicationControllerStarted = false,
            warmupTrialsPerPolicy = 3,
            measuredTrialsPerPolicy = Trials,
            simulatedMinutesPerTrial = TicksPerTrial * TickInterval.TotalMinutes,
            scheduledRefreshIntervalSeconds = TickInterval.TotalSeconds,
            receiverCount = 2,
            legacy,
            optimized,
            recovery,
            limitations = new[]
            {
                "Synthetic feature replies and immediate synthetic timeouts; no USB latency, real radio traffic or battery drain was measured.",
                "Simulated time advances without sleeping; this is a short software smoke workload, not a 20-minute physical run or endurance test.",
                "CPU, allocations, working/private memory and handles are process-wide before/after observations; other runtime/WPF work and JIT/GC/scheduler noise may contribute.",
                "Memory figures are snapshots, not peak or steady-state memory; CPU/memory deltas are reported without a performance pass threshold.",
                "Legacy is the same managed monitor with efficiency options omitted; optimized uses the new default cache/backoff policy. Healthy refresh cadence is identical.",
                "Policy measurements run sequentially after warmup; query/enumeration counts are deterministic, but one short CPU/memory sample does not establish a causal performance percentage."
            }
        };
    }

    private static async Task<RunResult> MeasureAsync(bool optimized, CancellationToken cancellationToken)
    {
        var before = ReadProcess();
        var stopwatch = Stopwatch.StartNew();
        var counts = await ExecuteAsync(optimized, Trials, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var after = ReadProcess();
        return new(optimized ? "cached discovery and bounded receiver backoff" : "legacy refresh policy",
            Trials, Trials * TicksPerTrial, counts.Enumerations, counts.Opens, counts.Healthy,
            counts.NonResponsive, counts.Queries, stopwatch.Elapsed.TotalMilliseconds,
            Math.Max(0, after.CpuMilliseconds - before.CpuMilliseconds),
            Math.Max(0, after.TotalAllocatedBytes - before.TotalAllocatedBytes), before, after);
    }

    private static async Task<(int Enumerations, int Opens, int Healthy, int NonResponsive, int Queries)>
        ExecuteAsync(bool optimized, int trials, CancellationToken cancellationToken)
    {
        var enumerations = 0;
        var opens = 0;
        var healthy = 0;
        var nonResponsive = 0;
        var queries = 0;
        for (var trial = 0; trial < trials; trial++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clock = new WorkloadClock();
            var discovery = new WorkloadDiscovery();
            var transport = new WorkloadTransports();
            var provider = new WorkloadProvider();
            var monitor = new BatteryMonitor(discovery, transport, [provider],
                options: optimized ? new BatteryMonitorOptions() : null, timeProvider: clock);
            for (var tick = 0; tick < TicksPerTrial; tick++)
            {
                await monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
                clock.Advance(TickInterval);
            }
            enumerations += discovery.Enumerations;
            opens += transport.Opens;
            healthy += provider.HealthyReads;
            nonResponsive += provider.NonResponsiveReads;
            queries += transport.FeatureQueries;
        }
        return (enumerations, opens, healthy, nonResponsive, queries);
    }

    private static async Task<object> VerifyRecoveryAndFreshnessAsync(CancellationToken cancellationToken)
    {
        var clock = new WorkloadClock();
        var discovery = new WorkloadDiscovery();
        var transports = new WorkloadTransports();
        var provider = new WorkloadProvider();
        var monitor = new BatteryMonitor(discovery, transports, [provider], options: new(), timeProvider: clock);
        await monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var failedReads = provider.NonResponsiveReads;
        clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (provider.NonResponsiveReads != failedReads)
            throw new InvalidOperationException("An unavailable receiver was queried before its retry delay.");
        provider.Recovered = true;
        await monitor.RefreshAsync(cancellationToken, force: true).ConfigureAwait(false);
        if (provider.NonResponsiveReads != failedReads + 1 ||
            !monitor.Devices.Any(device => device.Device.Id == "efficiency:failed" && device.Battery.State == ReadingState.Current))
            throw new InvalidOperationException("Manual refresh did not immediately recover a backed-off receiver.");
        provider.Recovered = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var stale = monitor.Devices.Single(device => device.Device.Id == "efficiency:failed");
        if (stale.Battery.State != ReadingState.Stale || stale.Battery.SampledAt is null)
            throw new InvalidOperationException("A failed receiver lost its last genuine sample or presented it as current.");
        var originalSample = stale.Battery.SampledAt;
        clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        stale = monitor.Devices.Single(device => device.Device.Id == "efficiency:failed");
        if (stale.Battery.State != ReadingState.Stale || stale.Battery.SampledAt != originalSample)
            throw new InvalidOperationException("Backoff refreshed the age of an old battery measurement.");
        provider.Recovered = true;
        monitor.InvalidateDiscovery();
        await monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (monitor.Devices.Single(device => device.Device.Id == "efficiency:failed").Battery.State != ReadingState.Current)
            throw new InvalidOperationException("Device-change invalidation did not promptly recover the receiver.");
        return new { manualRefreshBypassedBackoff = true, deviceChangeBypassedBackoff = true,
            staleStateAndOriginalSamplePreserved = true };
    }

    private static ProcessSample ReadProcess()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64,
            process.PrivateMemorySize64, GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: true), process.HandleCount,
            checked((int)GetGuiResources(process.Handle, 0)), checked((int)GetGuiResources(process.Handle, 1)),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private sealed class WorkloadClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) { now += value; timestamp += value.Ticks; }
    }

    private sealed class WorkloadDiscovery : IHidDiscovery
    {
        public int Enumerations { get; private set; }
        public IReadOnlyList<HidEndpoint> Enumerate()
        {
            Enumerations++;
            return [new("synthetic:healthy", 0x1532, 1, 0xFF00, 1, 0, 0, 91, "efficiency:healthy"),
                new("synthetic:failed", 0x1532, 2, 0xFF00, 1, 0, 0, 91, "efficiency:failed")];
        }
    }

    private sealed class WorkloadTransports : IHidTransportFactory
    {
        public int Opens { get; private set; }
        public int FeatureQueries { get; private set; }
        public ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opens++;
            return ValueTask.FromResult<IHidTransport>(new WorkloadTransport(endpoint, this));
        }
        private sealed class WorkloadTransport(HidEndpoint endpoint, WorkloadTransports owner) : IHidTransport
        {
            public HidEndpoint Endpoint => endpoint;
            public ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.FeatureQueries++;
                return ValueTask.FromResult(new byte[] { 0, 42 });
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The synthetic workload must never issue configuration writes.");
            public ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The synthetic workload must never issue configuration writes.");
            public ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The synthetic workload uses only its fake feature reply.");
        }
    }

    private sealed class WorkloadProvider : IBatteryProvider
    {
        public string Name => "Synthetic efficiency fixture";
        public int HealthyReads { get; private set; }
        public int NonResponsiveReads { get; private set; }
        public bool Recovered { get; set; }
        public bool CanHandle(HidEndpoint endpoint) => endpoint.DevicePath.StartsWith("synthetic:", StringComparison.Ordinal);
        public async Task<IReadOnlyList<DeviceSnapshot>> ReadAsync(HidEndpoint endpoint, IHidTransport transport,
            DateTimeOffset observedAt, CancellationToken cancellationToken)
        {
            var reply = await transport.GetFeatureAsync([0, 0x80], cancellationToken).ConfigureAwait(false);
            if (endpoint.ProductId == 1) HealthyReads++;
            else
            {
                NonResponsiveReads++;
                if (!Recovered) throw new TimeoutException("Synthetic non-responsive receiver; no hardware was accessed.");
            }
            return [new(new(endpoint.ReceiverId, "Synthetic workload peripheral", DeviceVendor.Razer,
                    endpoint.ReceiverId), BatteryReading.Current(reply[1], ChargingState.Discharging, observedAt),
                ConnectionState.Connected)];
        }
    }

    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
