using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.Core.Diagnostics;

public interface IDiagnosticSink
{
    void Record(string category, string message, IReadOnlyDictionary<string, string?>? fields = null);
}

public sealed record DiagnosticLogOptions
{
    public int MaximumFileBytes { get; init; } = 256 * 1024;
    public int MaximumFiles { get; init; } = 4;
    public int MaximumEntryBytes { get; init; } = 8192;
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    internal void Validate()
    {
        if (MaximumFileBytes is < 1024 or > 4 * 1024 * 1024 || MaximumFiles is < 1 or > 16 ||
            MaximumEntryBytes < 256 || MaximumEntryBytes > MaximumFileBytes ||
            Retention < TimeSpan.FromMinutes(1) || Retention > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(DiagnosticLogOptions));
    }
}

/// <summary>Bounded, privacy-preserving local JSONL diagnostics. Storage failures never stop monitoring.</summary>
public sealed class DiagnosticLog : IDiagnosticSink, IDisposable
{
    private readonly object gate = new();
    private readonly DiagnosticLogOptions options;
    private readonly TimeProvider clock;
    private readonly byte[] tokenKey = RandomNumberGenerator.GetBytes(32);
    private readonly HashSet<string> sensitiveValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly string version;
    private readonly string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
    private bool strictPrivacy;
    private bool disposed;
    private string? lastError;
    private DateTimeOffset? lastPrunedAt;
    private static readonly TimeSpan PruningInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public string DirectoryPath { get; }
    public string? LastError { get { lock (gate) return lastError; } }

    public DiagnosticLog(string directory, string appVersion, DiagnosticLogOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.options = options ?? new();
        this.options.Validate();
        clock = timeProvider ?? TimeProvider.System;
        DirectoryPath = Path.GetFullPath(directory);
        // Version is machine metadata, never an arbitrary command line, path or build description.
        version = Regex.IsMatch(appVersion ?? "", @"^[0-9A-Za-z.+-]{1,80}$", RegexOptions.CultureInvariant, RegexTimeout)
            ? appVersion! : "unknown";
        Record("application", "Diagnostic session started", new Dictionary<string, string?>
        {
            ["platform"] = OperatingSystem.IsWindows() ? "Windows" : "Other",
            ["redacted"] = "true",
            ["rawReportsIncluded"] = "false"
        });
    }

    public void Record(string category, string message, IReadOnlyDictionary<string, string?>? fields = null)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var pruned = PruneExpired();
                var entry = new DiagnosticEntry(clock.GetUtcNow(), version, sessionId,
                    Redact(category, 48), Redact(message, 1536), SanitizeFields(fields));
                var bytes = Encode(entry);
                if (bytes.Length > options.MaximumEntryBytes)
                    bytes = Encode(entry with { Message = Redact(message, 96), Fields = new() { ["truncated"] = "true" } });
                if (bytes.Length > options.MaximumEntryBytes) return;
                var current = LogPath(0);
                RequireRegularFile(current);
                if (File.Exists(current) && new FileInfo(current).Length + bytes.Length > options.MaximumFileBytes)
                {
                    if (!pruned) PruneExpired(force: true);
                    if (File.Exists(current) && new FileInfo(current).Length + bytes.Length > options.MaximumFileBytes) Rotate();
                }
                using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                lastError = null;
            }
            catch (Exception exception) when (IsDiagnosticFailure(exception))
            {
                // Avoid exception text here: a failed path may contain a Windows account name.
                lastError = $"Diagnostic storage unavailable ({exception.GetType().Name}).";
            }
        }
    }

    public void RecordEndpoint(string eventName, HidEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (gate)
        {
            try
            {
                Register(endpoint.DevicePath);
                Register(endpoint.SerialNumber);
                Register(endpoint.ReceiverId);
                var match = endpoint.DevicePath.Length <= 8192
                    ? Regex.Match(endpoint.DevicePath, @"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)
                    : Match.Empty;
                Record("hid", eventName, new Dictionary<string, string?>
                {
                    ["endpointToken"] = Token(endpoint.DevicePath), ["receiverToken"] = Token(endpoint.ReceiverId),
                    ["vid"] = endpoint.VendorId.ToString("X4"), ["pid"] = endpoint.ProductId.ToString("X4"),
                    ["usagePage"] = endpoint.UsagePage.ToString("X4"), ["usage"] = endpoint.Usage.ToString("X4"),
                    ["interface"] = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "not-composite",
                    ["inputLength"] = endpoint.InputReportLength.ToString(CultureInfo.InvariantCulture),
                    ["outputLength"] = endpoint.OutputReportLength.ToString(CultureInfo.InvariantCulture),
                    ["featureLength"] = endpoint.FeatureReportLength.ToString(CultureInfo.InvariantCulture),
                    ["serialPresent"] = string.IsNullOrEmpty(endpoint.SerialNumber) ? "false" : "true"
                });
            }
            catch (Exception exception) when (IsDiagnosticFailure(exception))
            { lastError = $"Diagnostic metadata unavailable ({exception.GetType().Name})."; }
        }
    }

    public void RecordSnapshot(DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            try
            {
                Register(snapshot.Device.Id);
                Register(snapshot.Device.ReceiverId);
                Register(snapshot.Device.Name);
                RegisterCanonicalParts(snapshot.Device.Id);
                Record("battery", "Device reading observed", new Dictionary<string, string?>
                {
                    ["deviceToken"] = Token(snapshot.Device.Id), ["receiverToken"] = Token(snapshot.Device.ReceiverId),
                    ["vendor"] = snapshot.Device.Vendor.ToString(), ["modelId"] = SafeModel(snapshot.Device.ModelId),
                    ["state"] = snapshot.Battery.State.ToString(), ["connection"] = snapshot.Connection.ToString(),
                    ["percentage"] = snapshot.Battery.Percentage?.ToString(CultureInfo.InvariantCulture),
                    ["category"] = snapshot.Battery.Category?.ToString(), ["charging"] = snapshot.Battery.Charging.ToString(),
                    ["sampledAtUtc"] = snapshot.Battery.SampledAt?.ToUniversalTime().ToString("O"),
                    ["detail"] = snapshot.Battery.Detail
                });
            }
            catch (Exception exception) when (IsDiagnosticFailure(exception))
            { lastError = $"Diagnostic metadata unavailable ({exception.GetType().Name})."; }
        }
    }
    public void RecordException(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Record("failure", operation, new Dictionary<string, string?>
        {
            ["exceptionType"] = exception.GetType().Name,
            ["detail"] = exception.Message
            // No stack traces: debug/source paths can identify users even without a HID serial.
        });
    }

    /// <summary>Exports only bounded, re-redacted diagnostic records; settings and raw HID bytes are never included.</summary>
    public void Export(string destinationZip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZip);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = Path.GetFullPath(destinationZip);
            if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a .zip diagnostic report.", nameof(destinationZip));
            var logs = Enumerable.Range(0, options.MaximumFiles).Select(LogPath).ToArray();
            if (logs.Any(path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The diagnostic report must be separate from its logs.", nameof(destinationZip));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            int exported = 0, omitted = 0;
            try
            {
                if (Directory.Exists(DirectoryPath)) PruneExpired(force: true);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    foreach (var path in logs.Where(File.Exists))
                    {
                        try
                        {
                            RequireRegularFile(path);
                            if (new FileInfo(path).Length > options.MaximumFileBytes) { omitted++; continue; }
                            var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                            foreach (var line in File.ReadLines(path))
                            {
                                if (Encoding.UTF8.GetByteCount(line) > options.MaximumEntryBytes) { omitted++; continue; }
                                try
                                {
                                    var record = JsonSerializer.Deserialize<DiagnosticEntry>(line, JsonOptions);
                                    if (record is null || !ValidSession(record.SessionId)) { omitted++; continue; }
                                    record = record with
                                    {
                                        AppVersion = Regex.IsMatch(record.AppVersion ?? "", @"^[0-9A-Za-z.+-]{1,80}$", RegexOptions.CultureInvariant, RegexTimeout) ? record.AppVersion : "unknown",
                                        Category = Redact(record.Category, 48), Message = Redact(record.Message, 1536),
                                        Fields = SanitizeFields(record.Fields)
                                    };
                                    writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
                                    exported++;
                                }
                                catch (Exception exception) when (IsDiagnosticFailure(exception)) { omitted++; }
                            }
                        }
                        catch (Exception exception) when (IsStorageFailure(exception)) { omitted++; }
                    }
                    var manifest = archive.CreateEntry("diagnostic-manifest.json", CompressionLevel.Optimal);
                    using var manifestWriter = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                    manifestWriter.Write(JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1, appVersion = version, exportedAtUtc = clock.GetUtcNow(),
                        redacted = true, rawReportsIncluded = false, settingsIncluded = false,
                        exportedRecords = exported, omittedRecordsOrFiles = omitted,
                        maximumFiles = options.MaximumFiles, maximumFileBytes = options.MaximumFileBytes,
                        retentionDays = options.Retention.TotalDays,
                        note = "Device, receiver and endpoint tokens are random-session keyed identifiers; they are not serial numbers. Host sample receipt time does not establish firmware sample age."
                    }, JsonOptions));
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch (Exception exception) when (IsStorageFailure(exception)) { } }
        }
    }

    private Dictionary<string, string?> SanitizeFields(IReadOnlyDictionary<string, string?>? fields)
    {
        Dictionary<string, string?> clean = new(StringComparer.Ordinal);
        if (fields is null || strictPrivacy) return clean;
        foreach (var field in fields.Take(24))
        {
            // Keys are schema labels, never a place to preserve paths or arbitrary identifiers.
            var key = Regex.IsMatch(field.Key ?? "", @"^[A-Za-z][A-Za-z0-9]{0,47}$", RegexOptions.CultureInvariant, RegexTimeout)
                ? field.Key : "redactedField";
            if (string.IsNullOrEmpty(key)) continue;
            if (Regex.IsMatch(key, @"serial(?!Present)|path|receiverId|deviceId|alias|name|password|secret|authorization|cookie", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                clean[key] = "[redacted]";
            else if (key.EndsWith("Token", StringComparison.OrdinalIgnoreCase))
                clean[key] = ValidSession(field.Value) ? field.Value : "[redacted]";
            else if (key == "modelId") clean[key] = SafeModel(field.Value);
            else clean[key] = field.Value is null ? null : Redact(field.Value, 256);
        }
        return clean;
    }

    private string Redact(string? value, int limit)
    {
        if (strictPrivacy) return "[detail omitted]";
        value ??= "";
        // Oversized exception/foreign text is omitted rather than partially exposing a long identifier.
        if (value.Length > 16384) return "[detail omitted]";
        // Registered identifiers are replaced before truncation so long paths cannot leak a leading fragment.
        foreach (var secret in sensitiveValues.OrderByDescending(x => x.Length))
            value = value.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        try
        {
            value = Regex.Replace(value, @"(?:\\\\[?.]\\|(?:HID|USB)\\)[^\s\""']+", "[redacted-device-path]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            value = Regex.Replace(value, @"\b[A-Za-z]:[\\/][^\r\n\""']*", "[redacted-file-path]", RegexOptions.CultureInvariant, RegexTimeout);
            value = Regex.Replace(value, @"\\\\[^\\\s]+\\[^\r\n\""']*", "[redacted-file-path]", RegexOptions.CultureInvariant, RegexTimeout);
            value = Regex.Replace(value, @"(?<![A-Za-z0-9])/(?:home|Users|tmp|workspace|var)/[^\r\n\""']*", "[redacted-file-path]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            value = Regex.Replace(value, @"\b(?:serial(?:[\s_-]*(?:number|no\.?|id))?|s/?n|receiverId|deviceId|unitId)\s*[:=]\s*[^\s,;\""']+", "identifier=[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            // Settings JSON paths and early errors can contain canonical identities
            // before discovery has registered their serial/unit components.
            value = Regex.Replace(value, @"/logitech/[1-6]/(?:(?:unit|serial)/|(?:[0-9A-F]{4}|unknown)/)[A-Za-z0-9_-]+", "[redacted-device-id]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            value = Regex.Replace(value, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b[0-9a-f]{24,}\b", "[redacted-id]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException) { return "[detail omitted]"; }
        return Clean(value, limit);
    }

    private void Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || disposed) return;
        if (sensitiveValues.Count >= 1024 || value.Length > 8192) { strictPrivacy = true; return; }
        sensitiveValues.Add(value);
    }

    private void RegisterCanonicalParts(string value)
    {
        if (value.Length > 8192 || strictPrivacy) return;
        foreach (var pattern in new[] { @"/(?:unit|serial)/([^/]+)", @"/logitech/[1-6]/(?:[0-9A-F]{4}|unknown)/([^/]+)$" })
        {
            var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            if (match.Success && !match.Groups[1].Value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                Register(match.Groups[1].Value);
        }
    }

    private string Token(string value) => disposed || strictPrivacy ? "[redacted]" :
        Convert.ToHexString(HMACSHA256.HashData(tokenKey, Encoding.UTF8.GetBytes(value))).Substring(0, 12);

    private static bool ValidSession(string? value) => value is not null &&
        Regex.IsMatch(value, @"^[0-9A-F]{12}$", RegexOptions.CultureInvariant, RegexTimeout);

    private static string? SafeModel(string? value) => value is null ? null :
        Regex.IsMatch(value, @"^[0-9A-Fa-f]{4}(?::[0-9A-Fa-f]{4})?$", RegexOptions.CultureInvariant, RegexTimeout)
            ? value.ToUpperInvariant() : "[redacted]";

    private static string Clean(string? value, int limit)
    {
        var clean = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray());
        if (clean.Length <= limit) return clean;
        int length = limit;
        if (length > 0 && char.IsHighSurrogate(clean[length - 1])) length--;
        return clean[..length] + "…";
    }

    private static byte[] Encode(DiagnosticEntry entry) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
    private string LogPath(int index) => Path.Combine(DirectoryPath, $"diagnostic-{index:D4}.jsonl");
    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    internal static bool IsDiagnosticFailure(Exception exception) => IsStorageFailure(exception) ||
        exception is ArgumentException or InvalidOperationException or NotSupportedException or JsonException or RegexMatchTimeoutException;
    private static void RequireRegularFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked diagnostic files are not supported.");
    }

    private bool PruneExpired(bool force = false)
    {
        var now = clock.GetUtcNow();
        var interval = options.Retention < PruningInterval ? options.Retention : PruningInterval;
        if (!force && lastPrunedAt is { } previous && now >= previous && now - previous < interval) return false;
        // Startup, export and rotation always scan. Normal appends reuse that result,
        // and a wall-clock rollback revokes it rather than postponing retention indefinitely.
        var cutoff = now.UtcDateTime - options.Retention;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "diagnostic-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != "diagnostic-0000".Length ||
                !int.TryParse(name.AsSpan("diagnostic-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
            RequireRegularFile(path);
            if (index >= options.MaximumFiles || new FileInfo(path).Length > options.MaximumFileBytes ||
                File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
        }
        lastPrunedAt = now;
        return true;
    }

    private void Rotate()
    {
        File.Delete(LogPath(options.MaximumFiles - 1));
        for (int index = options.MaximumFiles - 2; index >= 0; index--)
            if (File.Exists(LogPath(index))) File.Move(LogPath(index), LogPath(index + 1), overwrite: true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Record("application", "Diagnostic session ended");
            disposed = true;
            sensitiveValues.Clear();
            CryptographicOperations.ZeroMemory(tokenKey);
        }
    }

    private sealed record DiagnosticEntry(DateTimeOffset TimestampUtc, string AppVersion, string SessionId,
        string Category, string Message, Dictionary<string, string?> Fields);
}
