using DeviceBatteryStatus.Core.Diagnostics;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

/// <summary>Software-only storage workloads: no application launch, HID or network access.</summary>
public sealed class StorageBoundsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "device-battery-storage-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(directory, "settings.json");
    private string Logs => Path.Combine(directory, "logs");

    [Fact]
    public void RepeatedInvalidLoadsKeepOneExactCorruptSnapshotAndMigrateOnlyOwnedLegacyCopies()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, "{invalid-json}");
        for (var index = 0; index < 3; index++) File.WriteAllText(SettingsPath + ".corrupt-" + Guid.NewGuid().ToString("N"), "older");
        var notes = SettingsPath + ".corrupt-notes";
        File.WriteAllText(notes, "user notes");
        var store = new FileSettingsStore(SettingsPath);
        for (var index = 0; index < 10; index++)
        {
            var result = store.Load();
            Assert.NotNull(result.Error);
            Assert.Equal(SettingsPath + ".corrupt", result.CorruptBackupPath);
            Assert.Equal("{invalid-json}", File.ReadAllText(result.CorruptBackupPath!));
        }
        Assert.Equal("{invalid-json}", File.ReadAllText(SettingsPath));
        Assert.Equal("user notes", File.ReadAllText(notes));
        Assert.Equal(notes, Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void TheLatestCorruptSnapshotReplacesThePreviousOneWithoutChangingTheOriginal()
    {
        Directory.CreateDirectory(directory);
        var store = new FileSettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath, "{");
        store.Load();
        File.WriteAllText(SettingsPath, "[invalid]");
        var result = store.Load();
        Assert.Equal("[invalid]", File.ReadAllText(result.CorruptBackupPath!));
        Assert.Equal("[invalid]", File.ReadAllText(SettingsPath));
        Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt*"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void OversizedSettingsAreNotParsedOrCopiedAndLeaveTheOriginalAndBackupIntact()
    {
        Directory.CreateDirectory(directory);
        var oversized = "{\"RefreshIntervalSeconds\":120}" + new string(' ', 1024 * 1024);
        File.WriteAllText(SettingsPath, oversized);
        File.WriteAllText(SettingsPath + ".bak", "recoverable existing settings");
        var result = new FileSettingsStore(SettingsPath).Load();
        Assert.Contains("1 MiB", result.Error);
        Assert.Null(result.CorruptBackupPath);
        Assert.Equal(30, result.Settings.RefreshIntervalSeconds);
        Assert.Equal(oversized, File.ReadAllText(SettingsPath));
        Assert.Equal("recoverable existing settings", File.ReadAllText(SettingsPath + ".bak"));
        Assert.Empty(Directory.GetFiles(directory, "settings.json.corrupt*"));
    }

    [Fact]
    public void OversizedSavePreservesBothSavedGenerationsAndRemovesItsTemporaryFile()
    {
        var store = new FileSettingsStore(SettingsPath);
        store.Save(new AppSettings { RefreshIntervalSeconds = 60 });
        store.Save(new AppSettings { RefreshIntervalSeconds = 120 });
        var primary = File.ReadAllText(SettingsPath);
        var backup = File.ReadAllText(SettingsPath + ".bak");
        // One text element can have many combining marks: the displayed-name limit
        // does not imply that serialized preferences fit the disk storage limit.
        var largeName = "x" + new string('\u0301', 200_000);
        var settings = new AppSettings { CustomNames = new Dictionary<string, string> { ["device"] = largeName } };
        var error = Assert.Throws<IOException>(() => store.Save(settings));
        Assert.Contains("storage limit", error.Message);
        Assert.Equal(primary, File.ReadAllText(SettingsPath));
        Assert.Equal(backup, File.ReadAllText(SettingsPath + ".bak"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void NormalDiagnosticWritesReuseTheRetentionScanForFiveMinutes()
    {
        var clock = new Clock();
        using var log = new DiagnosticLog(Logs, "1.2.0", timeProvider: clock);
        var expired = AddExpiredLog(clock);
        for (var index = 0; index < 100; index++) log.Record("synthetic", "a normal log entry");
        Assert.True(File.Exists(expired));
        clock.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1);
        log.Record("synthetic", "still before the next retention pass");
        Assert.True(File.Exists(expired));
        clock.Now += TimeSpan.FromMilliseconds(1);
        log.Record("synthetic", "the next retention pass is due");
        Assert.False(File.Exists(expired));
        Assert.Null(log.LastError);
    }

    [Fact]
    public void ExportForcesARetentionScanWithoutWaitingForTheNextAppendDeadline()
    {
        var clock = new Clock();
        using var log = new DiagnosticLog(Logs, "1.2.0", timeProvider: clock);
        var expired = AddExpiredLog(clock);
        log.Export(Path.Combine(directory, "diagnostics.zip"));
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(Path.Combine(directory, "diagnostics.zip")));
    }

    [Fact]
    public void ABackwardClockChangeDoesNotPostponeRetention()
    {
        var clock = new Clock();
        using var log = new DiagnosticLog(Logs, "1.2.0", timeProvider: clock);
        var expired = AddExpiredLog(clock);
        clock.Now -= TimeSpan.FromHours(1);
        log.Record("synthetic", "wall clock moved backwards");
        Assert.False(File.Exists(expired));
        Assert.Null(log.LastError);
    }

    [Fact]
    public void RotationForcesPruningAndRetainsTheConfiguredPerFileAndFileCountBounds()
    {
        var clock = new Clock();
        using var log = new DiagnosticLog(Logs, "1.2.0",
            new() { MaximumFiles = 3, MaximumFileBytes = 1024, MaximumEntryBytes = 512 }, clock);
        var expired = AddExpiredLog(clock, "diagnostic-0007.jsonl");
        for (var index = 0; index < 30; index++) log.Record("synthetic", new string('x', 300));
        Assert.False(File.Exists(expired));
        var files = Directory.GetFiles(Logs, "diagnostic-*.jsonl");
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        Assert.Null(log.LastError);
    }

    private string AddExpiredLog(Clock clock, string name = "diagnostic-0002.jsonl")
    {
        var path = Path.Combine(Logs, name);
        File.WriteAllText(path, "expired synthetic log");
        File.SetLastWriteTimeUtc(path, clock.Now.UtcDateTime - TimeSpan.FromDays(9));
        return path;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
