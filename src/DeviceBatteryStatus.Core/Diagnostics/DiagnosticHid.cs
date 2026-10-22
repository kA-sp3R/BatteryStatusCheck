using System.Globalization;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Protocols;

namespace DeviceBatteryStatus.Core.Diagnostics;

/// <summary>Logs descriptor metadata, never serials or identifying paths.</summary>
public sealed class DiagnosticHidDiscovery(IHidDiscovery inner, DiagnosticLog log) : IHidDiscovery
{
    public IReadOnlyList<string> Warnings => inner.Warnings;

    public IReadOnlyList<HidEndpoint> Enumerate()
    {
        try
        {
            var endpoints = inner.Enumerate();
            foreach (var endpoint in endpoints) log.RecordEndpoint("Enumerated HID interface", endpoint);
            foreach (var warning in inner.Warnings) log.Record("discovery", warning);
            log.Record("discovery", "HID enumeration completed", new Dictionary<string, string?>
            {
                ["interfaceCount"] = endpoints.Count.ToString(CultureInfo.InvariantCulture),
                ["warningCount"] = inner.Warnings.Count.ToString(CultureInfo.InvariantCulture)
            });
            return endpoints;
        }
        catch (Exception exception)
        {
            log.RecordException("HID enumeration failed", exception);
            throw;
        }
    }
}

/// <summary>Observes existing I/O; it never adds, retries or changes a hardware command.</summary>
public sealed class DiagnosticHidTransportFactory(IHidTransportFactory inner, DiagnosticLog log) : IHidTransportFactory
{
    public async ValueTask<IHidTransport> OpenAsync(HidEndpoint endpoint, CancellationToken cancellationToken)
    {
        log.RecordEndpoint("Selected HID interface", endpoint);
        try
        {
            var transport = await inner.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
            log.RecordEndpoint("Opened HID interface", transport.Endpoint);
            return new DiagnosticTransport(transport, log);
        }
        catch (Exception exception)
        {
            log.RecordException("Opening selected HID interface failed", exception);
            throw;
        }
    }

    private sealed class DiagnosticTransport(IHidTransport inner, DiagnosticLog log) : IHidTransport
    {
        private readonly Dictionary<(byte Slot, byte Index), ushort> advertisedFeatures = [];
        private PendingRequest? pending;
        private bool disposed;
        public HidEndpoint Endpoint => inner.Endpoint;

        public async ValueTask SetFeatureAsync(byte[] report, CancellationToken cancellationToken)
        {
            RecordReport("Feature request", report, outgoing: true);
            try { await inner.SetFeatureAsync(report, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { log.RecordException("Feature request failed", exception); throw; }
        }

        public async ValueTask<byte[]> GetFeatureAsync(byte[] request, CancellationToken cancellationToken)
        {
            try
            {
                var report = await inner.GetFeatureAsync(request, cancellationToken).ConfigureAwait(false);
                RecordReport("Feature reply received (validation pending)", report, outgoing: false);
                return report;
            }
            catch (Exception exception) { log.RecordException("Feature reply failed", exception); throw; }
        }

        public async ValueTask SetOutputReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            RecordReport("Control Output request", report, outgoing: true);
            try { await inner.SetOutputReportAsync(report, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { log.RecordException("Control Output request failed", exception); throw; }
        }

        public async ValueTask<byte[]> GetInputReportAsync(byte[] request, CancellationToken cancellationToken)
        {
            try
            {
                var report = await inner.GetInputReportAsync(request, cancellationToken).ConfigureAwait(false);
                RecordReport("Control Input reply received (validation pending)", report, outgoing: false);
                return report;
            }
            catch (Exception exception) { log.RecordException("Control Input reply failed", exception); throw; }
        }

        public async ValueTask WriteAsync(byte[] report, CancellationToken cancellationToken)
        {
            RecordReport("Output request", report, outgoing: true);
            try { await inner.WriteAsync(report, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) { log.RecordException("Output request failed", exception); throw; }
        }

        public async ValueTask<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                var report = await inner.ReadAsync(timeout, cancellationToken).ConfigureAwait(false);
                RecordReport("Input reply received (validation pending)", report, outgoing: false);
                return report;
            }
            catch (Exception exception) { log.RecordException("Input reply failed", exception); throw; }
        }

        private void RecordReport(string message, byte[]? report, bool outgoing)
        {
            try
            {
                if (report is null)
                {
                    log.Record("protocol", "No report buffer was supplied");
                    return;
                }
                RecordReportMetadata(message, report, outgoing);
            }
            catch (Exception exception) when (DiagnosticLog.IsDiagnosticFailure(exception))
            {
                // Metadata capture never changes whether the original I/O succeeds or what it returns.
                log.RecordException("Protocol diagnostic metadata unavailable", exception);
            }
        }

        private void RecordReportMetadata(string message, byte[] report, bool outgoing)
        {
            Dictionary<string, string?> fields = new()
            {
                ["vid"] = Endpoint.VendorId.ToString("X4"), ["pid"] = Endpoint.ProductId.ToString("X4"),
                ["length"] = report.Length.ToString(CultureInfo.InvariantCulture),
                ["reportId"] = report.Length > 0 ? Hex(report[0]) : "missing"
            };
            if (Endpoint.VendorId == 0x1532 && report.Length >= 9)
            {
                fields["transaction"] = Hex(report[2]); fields["commandClass"] = Hex(report[7]);
                fields["command"] = Hex(report[8]); fields["declaredLength"] = report[6].ToString(CultureInfo.InvariantCulture);
                if (!outgoing) fields["reportedStatus"] = Hex(report[1]);
            }
            else if (Endpoint.VendorId == 0x046D && report.Length >= 4 && report[0] is 0x10 or 0x11)
            {
                fields["slot"] = report[1].ToString(CultureInfo.InvariantCulture);
                fields["featureIndex"] = Hex(report[2]); fields["function"] = Hex((byte)(report[3] & 0xF0));
                if (outgoing)
                {
                    pending = new(report[1], report[2], report[3], report.Length >= 6 && report[2] == 0 &&
                        (report[3] & 0xF0) == 0 ? (ushort)((report[4] << 8) | report[5]) : null);
                    if (pending.QueriedFeature is ushort feature) fields["queriedFeatureId"] = feature.ToString("X4");
                }
                else RecordCapabilities(report);
            }
            else if (Endpoint.VendorId == 0x413C && report.Length >= 5)
            {
                // Header only: the identity/battery payload can include manufacturer-specific identifiers.
                fields["direction"] = Hex(report[1]); fields["command"] = Hex(report[2]);
                fields["declaredLength"] = report[3].ToString(CultureInfo.InvariantCulture);
            }
            log.Record("protocol", message, fields);
        }

        private void RecordCapabilities(byte[] report)
        {
            if (pending is not { } request || !LogitechHidppProtocol.TryParseReply(report,
                    request.Slot, request.Index, request.Function, out var reply) || reply is null)
                return;
            if (reply.ErrorCode is byte error)
            {
                log.Record("capability", "Correlated HID++ error", new Dictionary<string, string?>
                {
                    ["errorCode"] = Hex(error), ["slot"] = request.Slot.ToString(CultureInfo.InvariantCulture),
                    ["featureIndex"] = Hex(request.Index), ["function"] = Hex((byte)(request.Function & 0xF0))
                });
                return;
            }
            if (request.QueriedFeature is ushort queried && reply.Parameters.Length >= 3)
            {
                byte index = reply.Parameters[0];
                if (index != 0) advertisedFeatures[(request.Slot, index)] = queried;
                log.Record("capability", "Correlated HID++ feature discovery", new Dictionary<string, string?>
                {
                    ["featureId"] = queried.ToString("X4"), ["advertisedIndex"] = Hex(index),
                    ["featureFlags"] = Hex(reply.Parameters[1]), ["featureVersion"] = reply.Parameters[2].ToString(CultureInfo.InvariantCulture),
                    ["slot"] = request.Slot.ToString(CultureInfo.InvariantCulture)
                });
                return;
            }
            if (!advertisedFeatures.TryGetValue((request.Slot, request.Index), out ushort feature)) return;
            byte function = (byte)(request.Function & 0xF0);
            Dictionary<string, string?> fields = new()
            {
                ["featureId"] = feature.ToString("X4"), ["slot"] = request.Slot.ToString(CultureInfo.InvariantCulture)
            };
            switch (feature, function)
            {
                case (0x1004, 0x00) when reply.Parameters.Length >= 2:
                    fields["supportedCategoriesMask"] = Hex(reply.Parameters[0]);
                    fields["batteryCapabilitiesMask"] = Hex(reply.Parameters[1]);
                    break;
                case (0x1000, 0x10) when reply.Parameters.Length >= 2:
                    fields["levelCount"] = reply.Parameters[0].ToString(CultureInfo.InvariantCulture);
                    fields["batteryCapabilitiesMask"] = Hex(reply.Parameters[1]);
                    break;
                case (0x8060, 0x00) when reply.Parameters.Length >= 1:
                    fields["reportPeriodMask"] = Hex(reply.Parameters[0]);
                    break;
                case (0x8061, 0x10) when reply.Parameters.Length >= 2:
                    fields["reportRateMask"] = ((reply.Parameters[0] << 8) | reply.Parameters[1]).ToString("X4");
                    break;
                default: return; // In particular, never record unit-info or pairing/serial argument bytes.
            }
            log.Record("capability", "Correlated HID++ capabilities (device validation pending)", fields);
        }

        private static string Hex(byte value) => value.ToString("X2");
        private sealed record PendingRequest(byte Slot, byte Index, byte Function, ushort? QueriedFeature);

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { log.Record("hid", "Selected HID session closed"); }
        }
    }
}
