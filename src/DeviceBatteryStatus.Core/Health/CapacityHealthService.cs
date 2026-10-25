using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Health;

/// <summary>
/// Passive capacity ledger. Receives already acquired telemetry; never discovers or queries hardware.
/// Complete records survive restarts; an active discharge is explicitly invalidated on restart.
/// </summary>
public sealed class CapacityHealthService : IDisposable
{
    private const int MaximumDevices = 64;
    private const int MaximumHistory = 20;
    private const long MaximumStoreBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultMaximumGap = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, StoredDevice> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cycle> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapacityTelemetry> lastTelemetry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> reasons = new(StringComparer.Ordinal);
    private readonly HashSet<string> interruptedBindings = new(StringComparer.Ordinal);
    private long revision;
    private bool validPrimary;
    private bool disposed;
    public string? LastError { get; private set; }
    public long Revision => Interlocked.Read(ref revision);

    public CapacityHealthService(string filePath, TimeProvider? timeProvider = null)
    {
        path = Path.GetFullPath(filePath);
        clock = timeProvider ?? TimeProvider.System;
        Load();
    }

    public void SetDesignCapacity(DeviceSnapshot snapshot, DesignCapacity? design)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            ThrowIfDisposed();
            if (design is not null)
            {
                ValidateDesign(design);
                design = design with { Source = design.Source.Trim(), RecordedAt = design.RecordedAt ?? clock.GetUtcNow() };
            }
            var device = GetOrCreate(Key(snapshot.Device.Id));
            if (device.Design == design) return;
            device.Design = design;
            Save();
            Interlocked.Increment(ref revision);
        }
    }

    public CapacityHealthDetails GetDetails(DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            CheckContinuityCore(clock.GetUtcNow());
            var key = Key(snapshot.Device.Id);
            devices.TryGetValue(key, out var device);
            var telemetry = snapshot.CapacityTelemetry;
            var verified = HasVerifiedIdentity(telemetry) && !interruptedBindings.Contains(key) && ValidateSample(snapshot, clock.GetUtcNow()) is null &&
                (!lastTelemetry.TryGetValue(key, out var accepted) ||
                 accepted.BatteryIdentity == telemetry!.BatteryIdentity && accepted.ConnectionSession == telemetry.ConnectionSession);
            var batteryKey = verified ? Key(telemetry!.BatteryIdentity!) : null;
            var history = device?.History.ToList() ?? [];
            if (active.TryGetValue(key, out var cycle)) history.Add(cycle.Measurement(false, "Measurement in progress"));
            var latest = verified ? device?.History.LastOrDefault(x => x.Complete && x.BatteryIdentityKey == batteryKey) : null;
            var explanation = reasons.GetValueOrDefault(key) ?? MissingInformation(snapshot);
            var status = active.ContainsKey(key) ? "Collecting usable capacity" : "Capacity measurement unavailable";
            decimal? health = null;
            if (latest is not null)
            {
                if (device?.Design is null)
                {
                    status = "Design capacity required";
                    explanation = "Full-charge capacity is available. Enter the original design capacity with the same units and measurement basis.";
                }
                else if (latest.Unit != device.Design.Unit || latest.Basis != device.Design.Basis)
                {
                    status = "Capacity comparison unavailable";
                    explanation = "Design and usable capacity have different units or measurement bases. No voltage-based or nominal conversion is applied.";
                }
                else
                {
                    try
                    {
                        health = CalculateHealth(latest.FullChargeCapacity!.Value, latest.Unit, latest.Basis, device.Design);
                        status = active.ContainsKey(key) ? "Collecting usable capacity; previous result retained" : "Capacity health available";
                        explanation = latest.Method == CapacityMeasurementMethod.DeviceReported
                            ? "Calculated independently from a supported full-charge-capacity field and original design capacity. The date is the field-observation date; device training coverage is unknown."
                            : "Calculated from a complete measured full-to-empty discharge and original design capacity. Sample integration is numerical, not a manufacturer health API.";
                    }
                    catch (ArgumentException)
                    {
                        status = "Capacity comparison unavailable";
                        explanation = "The capacity ratio cannot be represented. Check the design value and its unit/basis; no health result was fabricated.";
                    }
                }
            }
            if (!verified && history.Count > 0)
                explanation += " Saved history is retained, but cannot be assigned to the current battery until its identity is established.";
            return new(device?.Design, latest, health, status, explanation, history.AsReadOnly(), verified, LastError);
        }
    }

    public static decimal CalculateHealth(decimal usableCapacity, CapacityUnit unit, CapacityBasis basis, DesignCapacity design)
    {
        ValidateDesign(design);
        if (!ValidValue(usableCapacity) || unit != design.Unit || basis != design.Basis)
            throw new ArgumentException("Capacity values require the same unit and measurement basis.");
        try
        {
            // Capacity is bounded above, so multiplying first is safe and avoids
            // losing a representable percentage to an intermediate rounded zero.
            var health = usableCapacity * 100m / design.Value;
            if (usableCapacity > 0 && health == 0)
                throw new ArgumentException("The positive capacity ratio cannot be represented.");
            return health; // Intentionally not clamped to 100%.
        }
        catch (OverflowException exception) { throw new ArgumentException("The capacity ratio cannot be represented.", exception); }
    }

    public void Observe(IEnumerable<DeviceSnapshot> snapshots, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        lock (gate)
        {
            ThrowIfDisposed();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var changed = false;
            var detailsChanged = false;
            foreach (var snapshot in snapshots)
            {
                var key = Key(snapshot.Device.Id);
                seen.Add(key);
                var sample = snapshot.CapacityTelemetry;
                var problem = ValidateSample(snapshot, observedAt);
                if (problem is not null)
                {
                    detailsChanged |= reasons.GetValueOrDefault(key) != problem || active.ContainsKey(key);
                    reasons[key] = problem;
                    changed |= Reject(key, problem);
                    interruptedBindings.Add(key);
                    continue;
                }
                if (lastTelemetry.TryGetValue(key, out var previous) && previous == sample) continue;
                detailsChanged = true;
                if (previous is not null && sample!.SampledAt <= previous.SampledAt)
                {
                    reasons[key] = "Capacity sample time did not advance; the discharge was rejected.";
                    changed |= Reject(key, reasons[key]);
                    interruptedBindings.Add(key);
                    continue;
                }
                lastTelemetry[key] = sample!;
                interruptedBindings.Remove(key);
                if (!devices.ContainsKey(key) && devices.Count >= MaximumDevices)
                {
                    reasons[key] = "Capacity history device limit reached; collection is unavailable for this device.";
                    continue;
                }
                var device = GetOrCreate(key);
                if (device.Design is null && sample!.DesignCapacity is { SourceKind: CapacitySourceKind.DeviceData } detected)
                {
                    try
                    {
                        ValidateDesign(detected);
                        device.Design = detected with { RecordedAt = detected.RecordedAt ?? sample.SampledAt };
                        changed = true;
                    }
                    catch (ArgumentException) { /* Invalid optional design data cannot invalidate a supported capacity field. */ }
                }
                if (sample!.ReportedCapacityKind == CapacityFieldKind.FullChargeCapacity && sample.ReportedCapacity is { } reported && sample.ReportedCapacityUnit is { } reportedUnit)
                {
                    changed |= Reject(key, "A supported full-charge-capacity field superseded an incomplete discharge.");
                    var measurement = new CapacityMeasurement(reported, null, reportedUnit, sample.Basis,
                        CapacityMeasurementMethod.DeviceReported, sample.SampledAt, sample.SampledAt,
                        TimeSpan.Zero, true, 1, null, sample.Source, Key(sample.BatteryIdentity!));
                    var last = device.History.LastOrDefault();
                    if (last is null || !last.Complete || last.FullChargeCapacity != reported || last.Unit != reportedUnit ||
                        last.Basis != sample.Basis || last.BatteryIdentityKey != measurement.BatteryIdentityKey || last.Method != measurement.Method || last.Source != measurement.Source)
                    {
                        Add(device, measurement);
                        changed = true;
                    }
                    reasons[key] = "Supported device-reported full-charge capacity is available.";
                    continue;
                }
                changed |= Integrate(key, device, snapshot, sample);
            }
            foreach (var missing in lastTelemetry.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                detailsChanged |= !interruptedBindings.Contains(missing) || active.ContainsKey(missing);
                reasons[missing] = "Device missing from refresh; battery continuity cannot be established.";
                changed |= Reject(missing, reasons[missing]);
                interruptedBindings.Add(missing);
            }
            // Persist only configuration, cycle start, completed/rejected cycles and changed full-capacity fields.
            if (changed) Save();
            if (detailsChanged) Interlocked.Increment(ref revision);
        }
    }

    public void Interrupt(string reason, DateTimeOffset observedAt)
    {
        lock (gate)
        {
            if (disposed) return;
            var detail = string.IsNullOrWhiteSpace(reason) ? "Capacity collection interrupted." : reason.Trim();
            if (detail.Length > 256) detail = detail[..256];
            var changed = false;
            var bindingsChanged = lastTelemetry.Keys.Any(key => !interruptedBindings.Contains(key));
            foreach (var key in lastTelemetry.Keys) interruptedBindings.Add(key);
            foreach (var key in active.Keys.ToArray())
            {
                reasons[key] = detail;
                changed |= Reject(key, detail);
            }
            if (changed) Save();
            if (changed || bindingsChanged) Interlocked.Increment(ref revision);
        }
    }

    /// <summary>Checks missing samples without issuing a query; call from existing freshness checks.</summary>
    public bool CheckContinuity(DateTimeOffset observedAt)
    {
        lock (gate) return !disposed && CheckContinuityCore(observedAt);
    }

    private bool CheckContinuityCore(DateTimeOffset observedAt)
    {
        var changed = false;
        var tick = clock.GetTimestamp();
        foreach (var pair in active.ToArray())
        {
            var elapsed = clock.GetElapsedTime(pair.Value.LastTick, tick);
            var wallElapsed = observedAt - pair.Value.LastObservedAt;
            var reason = elapsed < TimeSpan.Zero || wallElapsed < TimeSpan.Zero || (elapsed - wallElapsed).Duration() > TimeSpan.FromSeconds(2)
                ? "Clock discontinuity interrupted measured discharge."
                : elapsed > pair.Value.MaximumGap || wallElapsed > pair.Value.MaximumGap || observedAt - pair.Value.LastAt > pair.Value.MaximumGap
                    ? "Missing capacity samples exceeded the supported sample interval."
                    : null;
            if (reason is null) continue;
            reasons[pair.Key] = reason;
            changed |= Reject(pair.Key, reason);
        }
        if (changed)
        {
            Save();
            Interlocked.Increment(ref revision);
        }
        return changed;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Interrupt("Application stopped before a complete discharge.", clock.GetUtcNow());
            disposed = true;
        }
    }

    private bool Integrate(string key, StoredDevice device, DeviceSnapshot snapshot, CapacityTelemetry sample)
    {
        if (snapshot.Battery.Charging != ChargingState.Discharging)
        {
            reasons[key] = snapshot.Battery.Charging == ChargingState.Charging
                ? "Charging interrupted the discharge measurement."
                : "Measured discharge requires a known discharging state; charging state is uncertain.";
            return Reject(key, reasons[key]);
        }
        var rate = sample.DischargeCurrentMilliamps ?? sample.DischargePowerWatts;
        var unit = sample.DischargeCurrentMilliamps is not null ? CapacityUnit.MilliampHours : CapacityUnit.WattHours;
        var method = sample.DischargeCurrentMilliamps is not null ? CapacityMeasurementMethod.CurrentIntegration : CapacityMeasurementMethod.PowerIntegration;
        if (rate is null)
        {
            reasons[key] = MissingInformation(snapshot);
            return Reject(key, reasons[key]);
        }
        if (sample.MaximumSampleGap is null)
        {
            reasons[key] = "Discharge integration requires a documented maximum sampling interval; no cadence is assumed.";
            return Reject(key, reasons[key]);
        }
        var tick = clock.GetTimestamp();
        if (!active.TryGetValue(key, out var cycle))
        {
            if (!sample.FullBoundary || sample.EmptyBoundary)
            {
                reasons[key] = "Waiting for a documented full-charge boundary followed by uninterrupted measured discharge to a documented empty boundary. Current charge level is not full usable capacity.";
                return false;
            }
            cycle = new(sample, tick, rate.Value, unit, method, clock.GetUtcNow());
            active[key] = cycle;
            device.Pending = cycle.Measurement(false, "Application restarted before a complete discharge.");
            reasons[key] = "Collecting a complete full-to-empty discharge; partial discharge is not full-charge capacity.";
            return true;
        }
        var elapsed = clock.GetElapsedTime(cycle.LastTick, tick);
        var sampleElapsed = sample.SampledAt - cycle.LastAt;
        var maximumGap = sample.MaximumSampleGap ?? DefaultMaximumGap;
        if (cycle.MaximumGap < maximumGap) maximumGap = cycle.MaximumGap;
        var invalid = cycle.BatteryKey != Key(sample.BatteryIdentity!) ? "Battery identity changed during discharge."
            : cycle.Session != sample.ConnectionSession ? "Reconnection interrupted battery continuity."
            : cycle.Unit != unit || cycle.Basis != sample.Basis || cycle.Method != method || cycle.Source != sample.Source ? "Capacity units, measurement basis or telemetry source changed during discharge."
            : cycle.MaximumGap != sample.MaximumSampleGap ? "The supported sampling-interval contract changed during discharge."
            : (cycle.LastSequence is null) != (sample.SampleSequence is null) ? "Sample-sequence support changed during discharge."
            : cycle.LastSequence is { } lastSequence && (lastSequence == long.MaxValue || sample.SampleSequence != lastSequence + 1) ? "Missing or out-of-order capacity samples interrupted sequence continuity."
            : elapsed <= TimeSpan.Zero || sampleElapsed <= TimeSpan.Zero ? "Capacity sample time did not advance."
            : elapsed > maximumGap || sampleElapsed > maximumGap ? "Missing capacity samples exceeded the supported sample interval."
            : (elapsed - sampleElapsed).Duration() > TimeSpan.FromSeconds(2) ? "Clock discontinuity interrupted measured discharge."
            : sample.FullBoundary ? "A new full boundary interrupted the previous discharge."
            : null;
        if (invalid is not null)
        {
            reasons[key] = invalid;
            return Reject(key, invalid);
        }
        var rateSum = cycle.PreviousRate + rate.Value;
        var increment = rateSum * elapsed.Ticks / (2m * TimeSpan.TicksPerHour);
        if (rateSum > 0 && increment == 0)
        {
            reasons[key] = "Positive discharged capacity cannot be represented; the discharge was rejected rather than reported as zero.";
            return Reject(key, reasons[key]);
        }
        cycle.Integral += increment;
        if (!ValidValue(cycle.Integral))
        {
            reasons[key] = "Integrated capacity exceeds the supported measurement range; the discharge was rejected.";
            return Reject(key, reasons[key]);
        }
        cycle.PreviousRate = rate.Value;
        cycle.LastSequence = sample.SampleSequence;
        cycle.MaximumGap = sample.MaximumSampleGap ?? DefaultMaximumGap;
        cycle.LastTick = tick;
        cycle.LastAt = sample.SampledAt;
        cycle.LastObservedAt = clock.GetUtcNow();
        cycle.Duration += elapsed;
        cycle.Count++;
        if (!sample.EmptyBoundary) return false;
        Add(device, cycle.Measurement(true, null));
        device.Pending = null;
        active.Remove(key);
        reasons[key] = "A complete measured full-to-empty usable-capacity result is available.";
        return true;
    }

    private static string? ValidateSample(DeviceSnapshot snapshot, DateTimeOffset observedAt)
    {
        var sample = snapshot.CapacityTelemetry;
        if (sample is null) return MissingInformation(snapshot);
        if (snapshot.Connection != ConnectionState.Connected || snapshot.Battery.State is ReadingState.Stale or ReadingState.Disconnected or ReadingState.Failed)
            return "Capacity measurement requires fresh telemetry and an established peripheral connection.";
        if (!HasVerifiedIdentity(sample)) return "A stable individual battery identity and uninterrupted connection session are missing or uncertain.";
        var maximumGap = sample.MaximumSampleGap ?? DefaultMaximumGap;
        if (maximumGap <= TimeSpan.Zero || maximumGap > DefaultMaximumGap)
            return "The capacity telemetry sampling interval is unsupported.";
        if (sample.SampledAt > observedAt + TimeSpan.FromSeconds(2) || observedAt - sample.SampledAt > maximumGap)
            return "Capacity telemetry is stale or has an invalid sample timestamp.";
        if (!Enum.IsDefined(sample.Basis) || !Enum.IsDefined(sample.ReportedCapacityKind) ||
            string.IsNullOrWhiteSpace(sample.Source) || sample.Source.Length > 256 || sample.Source.Any(char.IsControl))
            return "Capacity telemetry lacks a valid documented source or measurement basis.";
        if (sample.DischargeCurrentMilliamps is { } current && !ValidValue(current) ||
            sample.DischargePowerWatts is { } power && !ValidValue(power))
            return "Measured discharge current or power is invalid; charging or an unsupported sign convention cannot be integrated.";
        if (sample.ReportedCapacity is { } reported && (!ValidValue(reported) || sample.ReportedCapacityUnit is null || !Enum.IsDefined(sample.ReportedCapacityUnit.Value)))
            return "The reported capacity field or unit is invalid.";
        if (sample.ReportedCapacityKind == CapacityFieldKind.FullChargeCapacity && sample.ReportedCapacity is null)
            return "The supported full-charge-capacity field has no usable value.";
        if (sample.SampleSequence is < 0) return "The capacity sample sequence is invalid.";
        return null;
    }

    private static string MissingInformation(DeviceSnapshot snapshot) => snapshot.CapacityTelemetry?.ReportedCapacityKind == CapacityFieldKind.RemainingCapacity
        ? "Only remaining capacity is reported. A supported full-charge-capacity field or measured discharge current/power with full/empty boundaries and a stable battery identity is required."
        : "No supported full-charge-capacity field or measured discharge current/power with documented full/empty boundaries and a stable battery identity is available. Battery percentage, category, voltage and runtime cannot supply capacity.";

    private static bool HasVerifiedIdentity(CapacityTelemetry? sample) => sample is { IdentityVerified: true } &&
        !string.IsNullOrWhiteSpace(sample.BatteryIdentity) && sample.BatteryIdentity.Length <= 512 &&
        !string.IsNullOrWhiteSpace(sample.ConnectionSession) && sample.ConnectionSession.Length <= 512;

    private bool Reject(string key, string detail)
    {
        if (!active.Remove(key, out var cycle)) return false;
        var device = GetOrCreate(key);
        Add(device, cycle.Measurement(false, detail));
        device.Pending = null;
        return true;
    }

    private StoredDevice GetOrCreate(string key)
    {
        if (devices.TryGetValue(key, out var found)) return found;
        if (devices.Count >= MaximumDevices) throw new InvalidOperationException("The capacity history device limit has been reached.");
        return devices[key] = new(key, null, [], null);
    }

    private static void Add(StoredDevice device, CapacityMeasurement measurement)
    {
        device.History.Add(measurement);
        if (device.History.Count > MaximumHistory) device.History.RemoveRange(0, device.History.Count - MaximumHistory);
    }

    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ValidValue(decimal value) => value is >= 0m and <= 1_000_000_000m;
    private static void ValidateDesign(DesignCapacity design)
    {
        if (!ValidValue(design.Value) || design.Value == 0 || !Enum.IsDefined(design.Unit) || !Enum.IsDefined(design.Basis) ||
            !Enum.IsDefined(design.SourceKind) || string.IsNullOrWhiteSpace(design.Source) || design.Source.Length > 256 || design.Source.Any(char.IsControl))
            throw new ArgumentException("Design capacity requires a positive value, explicit units/basis and a source of at most256 characters.");
    }

    private void Load()
    {
        StoreDocument? loaded = null;
        var hadInvalidPrimary = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                using var stream = File.OpenRead(candidate);
                if (stream.Length > MaximumStoreBytes) throw new JsonException("Capacity history exceeds its storage bound.");
                loaded = JsonSerializer.Deserialize<StoreDocument>(stream, JsonOptions) ?? throw new JsonException("Missing capacity history.");
                ValidateDocument(loaded);
                validPrimary = candidate == path;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                LastError = "Capacity history could not be read; the original file is retained. A valid backup is used when available.";
                if (candidate == path) hadInvalidPrimary = true;
                loaded = null;
            }
        }
        if (hadInvalidPrimary)
        {
            try { if (new FileInfo(path).Length <= MaximumStoreBytes) File.Copy(path, path + ".corrupt", true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        if (loaded is null) return;
        foreach (var device in loaded.Devices)
        {
            devices[device.Key] = device;
            if (device.Pending is not null)
            {
                Add(device, device.Pending with { Complete = false, FullChargeCapacity = null,
                    Interruption = "Application restarted before a complete discharge; the partial cycle was rejected." });
                device.Pending = null;
            }
        }
        if (loaded.Devices.Any(x => x.History.LastOrDefault()?.Interruption?.StartsWith("Application restarted", StringComparison.Ordinal) == true)) Save();
    }

    private static void ValidateDocument(StoreDocument document)
    {
        if (document.SchemaVersion != 1 || document.Devices is null || document.Devices.Count > MaximumDevices || document.Devices.Any(x => x is null) ||
            document.Devices.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != document.Devices.Count)
            throw new JsonException("Invalid capacity history document.");
        foreach (var device in document.Devices)
        {
            if (!IsKey(device.Key) || device.History is null || device.History.Count > MaximumHistory) throw new JsonException("Invalid capacity device.");
            if (device.Design is not null) ValidateDesign(device.Design);
            if (device.Pending?.Complete == true) throw new JsonException("A pending cycle cannot be a completed measurement.");
            foreach (var measurement in device.History.Concat(device.Pending is null ? [] : new[] { device.Pending }))
            {
                if (measurement is null || !IsKey(measurement.BatteryIdentityKey) || !Enum.IsDefined(measurement.Unit) || !Enum.IsDefined(measurement.Basis) ||
                    !Enum.IsDefined(measurement.Method) || measurement.SampleCount < 1 || measurement.ObservedDuration < TimeSpan.Zero ||
                    measurement.MeasuredAt < measurement.StartedAt || string.IsNullOrWhiteSpace(measurement.Source) || measurement.Source.Length > 256 ||
                    measurement.FullChargeCapacity is { } full && !ValidValue(full) ||
                    measurement.ObservedDischargeCapacity is { } partial && !ValidValue(partial) ||
                    measurement.Complete != (measurement.FullChargeCapacity is not null) ||
                    measurement.Complete && measurement.Interruption is not null || !measurement.Complete && string.IsNullOrWhiteSpace(measurement.Interruption) ||
                    measurement.Method == CapacityMeasurementMethod.CurrentIntegration && measurement.Unit != CapacityUnit.MilliampHours ||
                    measurement.Method == CapacityMeasurementMethod.PowerIntegration && measurement.Unit != CapacityUnit.WattHours ||
                    measurement.Complete && measurement.Method != CapacityMeasurementMethod.DeviceReported &&
                        (measurement.SampleCount < 2 || measurement.ObservedDuration <= TimeSpan.Zero || measurement.ObservedDischargeCapacity != measurement.FullChargeCapacity ||
                         measurement.MaximumSampleGap is null || measurement.MaximumSampleGap <= TimeSpan.Zero || measurement.MaximumSampleGap > DefaultMaximumGap) ||
                    measurement.Method == CapacityMeasurementMethod.DeviceReported &&
                        (!measurement.Complete || measurement.SampleCount != 1 || measurement.ObservedDuration != TimeSpan.Zero || measurement.ObservedDischargeCapacity is not null))
                    throw new JsonException("Invalid capacity measurement.");
            }
        }
    }

    private static bool IsKey(string? key) => key is { Length: 64 } && key.All(x => x is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void Save()
    {
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".capacity-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoreDocument(1, devices.Values.ToList()), JsonOptions);
                if (stream.Length > MaximumStoreBytes) throw new IOException("Capacity history exceeds its storage bound.");
                stream.Flush(true);
            }
            if (validPrimary && File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path, true);
            validPrimary = true;
            LastError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = "Capacity history could not be saved. Previous valid history is retained; this session's changes may not survive restart.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class Cycle(CapacityTelemetry first, long tick, decimal rate, CapacityUnit unit, CapacityMeasurementMethod method, DateTimeOffset observedAt)
    {
        public readonly string BatteryKey = Key(first.BatteryIdentity!);
        public readonly string Session = first.ConnectionSession!;
        public readonly CapacityBasis Basis = first.Basis;
        public readonly string Source = first.Source;
        public readonly DateTimeOffset StartedAt = first.SampledAt;
        public readonly CapacityUnit Unit = unit;
        public readonly CapacityMeasurementMethod Method = method;
        public DateTimeOffset LastAt = first.SampledAt;
        public DateTimeOffset LastObservedAt = observedAt;
        public long LastTick = tick;
        public decimal PreviousRate = rate;
        public decimal Integral;
        public int Count = 1;
        public TimeSpan Duration;
        public TimeSpan MaximumGap = first.MaximumSampleGap ?? DefaultMaximumGap;
        public long? LastSequence = first.SampleSequence;
        public readonly bool HasSequence = first.SampleSequence is not null;
        public CapacityMeasurement Measurement(bool complete, string? interruption) => new(
            complete ? Integral : null, Count > 1 ? Integral : null, Unit, Basis, Method,
            StartedAt, LastAt, Duration, complete, Count, interruption, Source, BatteryKey, MaximumGap, HasSequence);
    }

    private sealed class StoreDocument
    {
        [JsonConstructor]
        public StoreDocument(int schemaVersion, List<StoredDevice> devices) { SchemaVersion = schemaVersion; Devices = devices; }
        public int SchemaVersion { get; }
        public List<StoredDevice> Devices { get; }
    }

    private sealed class StoredDevice
    {
        [JsonConstructor]
        public StoredDevice(string key, DesignCapacity? design, List<CapacityMeasurement> history, CapacityMeasurement? pending)
        { Key = key; Design = design; History = history; Pending = pending; }
        public string Key { get; }
        public DesignCapacity? Design { get; set; }
        public List<CapacityMeasurement> History { get; }
        public CapacityMeasurement? Pending { get; set; }
    }
}
