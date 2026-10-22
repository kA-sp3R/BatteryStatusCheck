using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeviceBatteryStatus.Core.Diagnostics;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"device-battery-diagnostics-{Guid.NewGuid():N}");
    private string Logs => Path.Combine(directory, "logs");
    private static readonly DateTimeOffset SampleTime = new(2026, 10, 22, 1, 2, 3, TimeSpan.Zero);
    private static HidEndpoint Endpoint => new(@"\\?\hid#vid_1532&pid_00e6&mi_03#PRIVATE_SERIAL#container",
        0x1532, 0x00E6, 0xFF00, 1, 0, 0, 91, "PRIVATE_RECEIVER", SerialNumber: "PRIVATE_SERIAL");

    [Fact]
    public void EndpointMetadataPreservesInterfaceAndCapabilitiesWhileReplacingIdentifyingValues()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        log.RecordEndpoint("Selected HID interface", Endpoint);
        log.RecordEndpoint("Opened HID interface", Endpoint);
        log.RecordException("Read failed", new IOException($"CRC validation failed for {Endpoint.DevicePath}; serial=PRIVATE_SERIAL"));

        var entries = ReadEntries();
        var endpointEntries = entries.Where(e => e.GetProperty("category").GetString() == "hid").ToArray();
        Assert.Equal(2, endpointEntries.Length);
        var fields = endpointEntries[0].GetProperty("fields");
        Assert.Equal("1532", Field(fields, "vid"));
        Assert.Equal("00E6", Field(fields, "pid"));
        Assert.Equal("03", Field(fields, "interface"));
        Assert.Equal("91", Field(fields, "featureLength"));
        Assert.Equal("true", Field(fields, "serialPresent"));
        Assert.Matches("^[0-9A-F]{12}$", Field(fields, "endpointToken")!);
        Assert.Equal(Field(fields, "endpointToken"), Field(endpointEntries[1].GetProperty("fields"), "endpointToken"));
        Assert.Equal("1.1.0", entries[0].GetProperty("appVersion").GetString());
        Assert.Contains("CRC validation failed", AllText());
        Assert.DoesNotContain("PRIVATE_SERIAL", AllText());
        Assert.DoesNotContain("PRIVATE_RECEIVER", AllText());
        Assert.DoesNotContain(Endpoint.DevicePath, AllText());
    }

    [Fact]
    public void CorrelationTokensChangeAcrossSessionsRatherThanPersistentlyIdentifyingADevice()
    {
        string first;
        using (var log = new DiagnosticLog(Logs, "1.1.0"))
        {
            log.RecordEndpoint("Selected", Endpoint);
            first = Field(ReadEntries().Single(e => e.GetProperty("category").GetString() == "hid").GetProperty("fields"), "endpointToken")!;
        }
        using var secondLog = new DiagnosticLog(Path.Combine(directory, "second"), "1.1.0");
        secondLog.RecordEndpoint("Selected", Endpoint);
        var second = ReadEntries(Path.Combine(directory, "second")).Single(e => e.GetProperty("category").GetString() == "hid");
        Assert.NotEqual(first, Field(second.GetProperty("fields"), "endpointToken"));
    }

    [Fact]
    public void GenuineZeroCategoriesAndStaleSampleTimesRemainDistinctInDiagnostics()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        var device = new PeripheralIdentity("receiver/logitech/1/unit/PRIVATEUNIT", "Private desk alias", DeviceVendor.Logitech, "receiver", "4105");
        log.RecordSnapshot(new(device, BatteryReading.Current(0, ChargingState.Unknown, SampleTime), ConnectionState.Connected));
        log.RecordSnapshot(new(device, BatteryReading.CurrentCategory(BatteryCategory.Low, ChargingState.Unknown, SampleTime)));
        log.RecordSnapshot(new(device, BatteryReading.Current(0, ChargingState.Unknown, SampleTime).AsStale("CRC validation failed for PRIVATEUNIT")));

        var records = ReadEntries().Where(e => e.GetProperty("category").GetString() == "battery").Select(e => e.GetProperty("fields")).ToArray();
        Assert.Equal("0", Field(records[0], "percentage"));
        Assert.Null(Field(records[0], "category"));
        Assert.Equal("Connected", Field(records[0], "connection"));
        Assert.Equal("Low", Field(records[1], "category"));
        Assert.Null(Field(records[1], "percentage"));
        Assert.Equal("Stale", Field(records[2], "state"));
        Assert.All(records, record => Assert.Equal(SampleTime.ToString("O"), Field(record, "sampledAtUtc")));
        Assert.DoesNotContain("Private desk alias", AllText());
        Assert.DoesNotContain("PRIVATEUNIT", AllText());
    }

    [Fact]
    public void PairingSerialEmbeddedInCanonicalLogitechIdentityIsAlsoRedactedFromDetails()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        var device = new PeripheralIdentity("receiver/logitech/1/4105/ABCDEF12", "Wireless mouse", DeviceVendor.Logitech, "receiver", "4105");
        log.RecordSnapshot(new(device, BatteryReading.Unavailable(ReadingState.Failed, "Reply validation failed for ABCDEF12")));
        Assert.DoesNotContain("ABCDEF12", Field(ReadEntries().Last().GetProperty("fields"), "detail")!);
        Assert.Contains("Reply validation failed", AllText());
    }

    [Theory]
    [InlineData(@"File C:\Users\PRIVATE_ACCOUNT\AppData\Local\log.txt could not be opened", "PRIVATE_ACCOUNT")]
    [InlineData("File /home/PRIVATE_ACCOUNT/device/log failed", "PRIVATE_ACCOUNT")]
    [InlineData(@"File \\PRIVATE_SERVER\PRIVATE_SHARE\log.txt could not be opened", "PRIVATE_SERVER")]
    [InlineData("serial number=PRIVATE_SERIAL; CRC failed", "PRIVATE_SERIAL")]
    [InlineData("SN=PRIVATE_SERIAL; CRC failed", "PRIVATE_SERIAL")]
    [InlineData("unitId=AB12CD34; CRC failed", "AB12CD34")]
    [InlineData("Receiver 12345678-1234-1234-1234-1234567890ab failed", "12345678-1234-1234-1234-1234567890ab")]
    public void UnregisteredPrivatePathsAndCommonIdentifierMarkersAreRedacted(string detail, string secret)
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        log.RecordException("Validation failed", new IOException(detail));
        Assert.DoesNotContain(secret, AllText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Validation failed", AllText());
    }

    [Fact]
    public void SensitiveFieldValuesAndIdentifyingFieldKeysAreRedacted()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        log.Record("failure", "Validation failed", new Dictionary<string, string?>
        {
            ["serialNumber"] = "PRIVATE_SERIAL", ["devicePath"] = Endpoint.DevicePath,
            ["alias"] = "Private desk alias", ["authorization"] = "Bearer PRIVATE_TOKEN",
            [@"C:\Users\PRIVATE_ACCOUNT\private"] = "value"
        });
        Assert.DoesNotContain("PRIVATE_SERIAL", AllText());
        Assert.DoesNotContain("Private desk alias", AllText());
        Assert.DoesNotContain("PRIVATE_TOKEN", AllText());
        Assert.DoesNotContain("PRIVATE_ACCOUNT", AllText());
    }

    [Fact]
    public void RotationAndOversizedMessagesKeepAllOwnedStorageAndEntriesWithinTheirLimits()
    {
        var options = new DiagnosticLogOptions { MaximumFiles = 3, MaximumFileBytes = 1024, MaximumEntryBytes = 512 };
        using var log = new DiagnosticLog(Logs, "1.1.0", options);
        for (var i = 0; i < 80; i++)
            log.Record("failure", new string('X', 3000), new Dictionary<string, string?> { ["index"] = i.ToString() });
        log.Record("failure", new string('Y', 100_000));

        var files = Directory.GetFiles(Logs, "diagnostic-*.jsonl");
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        foreach (var file in files)
            foreach (var line in File.ReadLines(file))
            {
                Assert.True(Encoding.UTF8.GetByteCount(line) + 1 <= 512);
                using var parsed = JsonDocument.Parse(line);
                Assert.NotNull(parsed.RootElement.GetProperty("timestampUtc").GetString());
            }
        Assert.DoesNotContain(new string('Y', 100), AllText());
        Assert.Null(log.LastError);
    }

    [Fact]
    public void ReducedLimitsRemoveOversizedAndOutOfRangeOwnedLogsButPreserveOtherFiles()
    {
        Directory.CreateDirectory(Logs);
        File.WriteAllText(Path.Combine(Logs, "diagnostic-0000.jsonl"), new string('X', 4000));
        File.WriteAllText(Path.Combine(Logs, "diagnostic-0007.jsonl"), "legacy");
        File.WriteAllText(Path.Combine(Logs, "notes.txt"), "preserved");
        using var log = new DiagnosticLog(Logs, "1.1.0", new() { MaximumFiles = 2, MaximumFileBytes = 1024, MaximumEntryBytes = 512 });
        Assert.False(File.Exists(Path.Combine(Logs, "diagnostic-0007.jsonl")));
        Assert.True(new FileInfo(Path.Combine(Logs, "diagnostic-0000.jsonl")).Length <= 1024);
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(Logs, "notes.txt")));
    }

    [Fact]
    public void RetentionPrunesOldOwnedLogs()
    {
        Directory.CreateDirectory(Logs);
        var old = Path.Combine(Logs, "diagnostic-0002.jsonl");
        File.WriteAllText(old, "legacy");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        using var log = new DiagnosticLog(Logs, "1.1.0", new() { Retention = TimeSpan.FromDays(7) });
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task ConcurrentRecordsStayWholeJsonAndWithinTheStorageBudget()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0", new() { MaximumFiles = 3, MaximumFileBytes = 4096, MaximumEntryBytes = 512 });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) log.Record("concurrent", $"worker={worker}; index={i}");
        })));
        Assert.All(Directory.GetFiles(Logs), file => Assert.True(new FileInfo(file).Length <= 4096));
        Assert.InRange(ReadEntries().Length, 1, 100);
        Assert.Null(log.LastError);
    }

    [Fact]
    public void AStorageFailureDoesNotEscapeAndExposesOnlyAnUnidentifyingError()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Logs, "blocked");
        using var log = new DiagnosticLog(Logs, "1.1.0");
        var exception = Record.Exception(() => log.RecordEndpoint("Selected", Endpoint));
        Assert.Null(exception);
        Assert.NotNull(log.LastError);
        Assert.DoesNotContain(directory, log.LastError!);
        Assert.DoesNotContain("PRIVATE_SERIAL", log.LastError!);
    }

    [Fact]
    public void ExportContainsOnlyRedactedOwnedLogsAndPrivacyManifest()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        log.RecordEndpoint("Selected", Endpoint);
        log.RecordException("Reply validation failed", new InvalidDataException("CRC failed serial=PRIVATE_SERIAL"));
        File.WriteAllText(Path.Combine(Logs, "settings.json"), "PRIVATE_SETTINGS");
        File.WriteAllText(Path.Combine(Logs, "raw-reports.txt"), "PRIVATE_BYTES");
        var export = Path.Combine(directory, "report.zip");
        log.Export(export);

        using var archive = ZipFile.OpenRead(export);
        Assert.Contains(archive.Entries, e => e.FullName == "diagnostic-manifest.json");
        Assert.All(archive.Entries, entry => Assert.True(entry.FullName == "diagnostic-manifest.json" || entry.FullName.StartsWith("diagnostic-", StringComparison.Ordinal)));
        var all = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("PRIVATE_SERIAL", all);
        Assert.DoesNotContain("PRIVATE_SETTINGS", all);
        Assert.DoesNotContain("PRIVATE_BYTES", all);
        using var manifest = JsonDocument.Parse(ReadEntry(archive.GetEntry("diagnostic-manifest.json")!));
        Assert.True(manifest.RootElement.GetProperty("redacted").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("rawReportsIncluded").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("settingsIncluded").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("exportedRecords").GetInt32() >= 3);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ExportRedactsExistingRecordsAgainAndOmitsMalformedLines()
    {
        using var log = new DiagnosticLog(Logs, "1.1.0");
        var existing = new
        {
            timestampUtc = SampleTime, appVersion = "1.0.0", sessionId = "012345ABCDEF",
            category = @"C:\Users\PRIVATE_ACCOUNT\failure", message = "serial=PRIVATE_SERIAL; failed",
            fields = new Dictionary<string, string?> { ["serial"] = "PRIVATE_SERIAL", ["detail"] = "SN=PRIVATE_SERIAL" }
        };
        File.AppendAllText(Path.Combine(Logs, "diagnostic-0000.jsonl"), JsonSerializer.Serialize(existing) + "\nnot-json\n");
        var export = Path.Combine(directory, "report.zip");
        log.Export(export);
        using var archive = ZipFile.OpenRead(export);
        var all = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("PRIVATE_SERIAL", all);
        Assert.DoesNotContain("PRIVATE_ACCOUNT", all);
        using var manifest = JsonDocument.Parse(ReadEntry(archive.GetEntry("diagnostic-manifest.json")!));
        Assert.Equal(1, manifest.RootElement.GetProperty("omittedRecordsOrFiles").GetInt32());
    }

    [Fact]
    public void DisposedLoggingIsIdempotentAndExportCannotResumeIt()
    {
        var log = new DiagnosticLog(Logs, "1.1.0");
        log.Dispose();
        var before = AllText();
        log.Dispose();
        log.Record("ignored", "must not be written");
        log.RecordEndpoint("ignored", Endpoint);
        Assert.Equal(before, AllText());
        Assert.Throws<ObjectDisposedException>(() => log.Export(Path.Combine(directory, "report.zip")));
    }

    private string AllText() => string.Join("\n", Directory.GetFiles(Logs, "diagnostic-*.jsonl").Select(File.ReadAllText));
    private JsonElement[] ReadEntries(string? path = null) => Directory.GetFiles(path ?? Logs, "diagnostic-*.jsonl")
        .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    private static string? Field(JsonElement fields, string key) => fields.GetProperty(key).GetString();
    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
