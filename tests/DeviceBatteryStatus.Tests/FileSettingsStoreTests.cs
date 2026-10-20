using System.Text.Json;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class FileSettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"device-battery-settings-tests-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(directory, "settings.json");

    [Fact]
    public void MissingSettingsUseDefaultsWithoutCreatingAFile()
    {
        var loaded = new FileSettingsStore(SettingsPath).Load();

        Assert.Null(loaded.Error);
        Assert.Null(loaded.CorruptBackupPath);
        Assert.Equal(30, loaded.Settings.RefreshIntervalSeconds);
        Assert.Equal(20, loaded.Settings.LowBatteryThreshold);
        Assert.False(loaded.Settings.StartWithWindows);
        Assert.Null(loaded.Settings.PreferredDeviceId);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void PreferencesRoundTripWithoutPersistingDeviceMeasurements()
    {
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            LowBatteryNotifications = false,
            LowBatteryThreshold = 12,
            PreferredDeviceId = "receiver/mouse",
            StartWithWindows = true,
            CustomNames = new Dictionary<string, string> { ["receiver/mouse"] = "Work mouse" }
        };
        var store = new FileSettingsStore(SettingsPath);

        store.Save(settings);
        var loaded = store.Load();

        Assert.Null(loaded.Error);
        Assert.Equal(120, loaded.Settings.RefreshIntervalSeconds);
        Assert.False(loaded.Settings.LowBatteryNotifications);
        Assert.Equal(12, loaded.Settings.LowBatteryThreshold);
        Assert.Equal("receiver/mouse", loaded.Settings.PreferredDeviceId);
        Assert.True(loaded.Settings.StartWithWindows);
        Assert.Equal("Work mouse", loaded.Settings.CustomNames["receiver/mouse"]);
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.False(document.RootElement.TryGetProperty("Percentage", out _));
        Assert.False(document.RootElement.TryGetProperty("SampledAt", out _));
        Assert.False(document.RootElement.TryGetProperty("Devices", out _));
    }

    [Fact]
    public void UpdatingSettingsKeepsThePriorFileAsARecoverableBackup()
    {
        var store = new FileSettingsStore(SettingsPath);
        store.Save(new AppSettings { RefreshIntervalSeconds = 30, PreferredDeviceId = "old-device" });
        var original = File.ReadAllText(SettingsPath);

        store.Save(new AppSettings { RefreshIntervalSeconds = 300, PreferredDeviceId = "new-device" });

        Assert.Equal(original, File.ReadAllText(SettingsPath + ".bak"));
        Assert.Equal(300, store.Load().Settings.RefreshIntervalSeconds);
        Assert.Equal("new-device", store.Load().Settings.PreferredDeviceId);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void InvalidSettingsKeepTheOriginalAndAnExactCorruptBackup(string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, content);
        var store = new FileSettingsStore(SettingsPath);

        var loaded = store.Load();

        Assert.NotNull(loaded.Error);
        Assert.NotNull(loaded.CorruptBackupPath);
        Assert.Equal(content, File.ReadAllText(SettingsPath));
        Assert.Equal(content, File.ReadAllText(loaded.CorruptBackupPath!));
        Assert.Equal(30, loaded.Settings.RefreshIntervalSeconds);

        store.Save(loaded.Settings);

        Assert.Equal(content, File.ReadAllText(loaded.CorruptBackupPath!));
        Assert.Null(store.Load().Error);
    }

    [Fact]
    public void NormalizationBoundsPreferencesAndCopiesNamesIndependently()
    {
        var names = new Dictionary<string, string>
        {
            ["receiver/mouse"] = "  Work\nmouse  ",
            ["receiver/empty"] = " \t ",
            ["bad\nid"] = "Ignored"
        };
        var normalized = new AppSettings
        {
            RefreshIntervalSeconds = 1,
            LowBatteryThreshold = 100,
            PreferredDeviceId = "  receiver/mouse  ",
            CustomNames = names
        }.Normalize();
        names["receiver/mouse"] = "Changed later";

        Assert.Equal(10, normalized.RefreshIntervalSeconds);
        Assert.Equal(50, normalized.LowBatteryThreshold);
        Assert.Equal("receiver/mouse", normalized.PreferredDeviceId);
        Assert.Equal("Workmouse", normalized.CustomNames["receiver/mouse"]);
        Assert.Single(normalized.CustomNames);
    }

    [Fact]
    public void LongNamesAreLimitedWithoutSplittingUnicodeTextElements()
    {
        var repeated = string.Concat(Enumerable.Repeat("\U0001F50B", 70));

        var normalized = AppSettings.NormalizeName(repeated);

        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F50B", 60)), normalized);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
