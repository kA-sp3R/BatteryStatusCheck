using System.Text.Json;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class SettingsExtensionsTests
{
    [Fact]
    public void PreviousSettingsDefaultToOneIconAndNotifyOnlyWithoutLosingPreferences()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"RefreshIntervalSeconds":45,"PreferredDeviceId":"mouse","CustomNames":{"mouse":"Desk"}}""")!.Normalize();
        Assert.Equal(TrayIconMode.Single, settings.TrayIconMode);
        Assert.Empty(settings.TrayDeviceIds);
        Assert.Equal(UpdateCheckMode.NotifyOnly, settings.UpdateCheckMode);
        Assert.Equal("Desk", settings.CustomNames["mouse"]);
        Assert.Equal("mouse", settings.PreferredDeviceId);
        Assert.Equal(45, settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void SelectedDeviceIdsAreValidDistinctBoundedAndIndependentlyCopied()
    {
        var ids = new List<string> { " mouse ", "mouse", "", "bad\nvalue" };
        ids.AddRange(Enumerable.Range(0, 30).Select(i => "device" + i));
        var normalized = new AppSettings { TrayIconMode = TrayIconMode.SelectedDevices, TrayDeviceIds = ids }.Normalize();
        ids.Clear();
        Assert.Equal(16, normalized.TrayDeviceIds.Count);
        Assert.Equal("mouse", normalized.TrayDeviceIds[0]);
        Assert.DoesNotContain("bad\nvalue", normalized.TrayDeviceIds);
        Assert.Equal(16, normalized.TrayDeviceIds.Distinct().Count());
    }

    [Fact]
    public void InvalidNewModesAndNullCollectionsUseSafeDefaults()
    {
        var normalized = new AppSettings { TrayIconMode = (TrayIconMode)999, UpdateCheckMode = (UpdateCheckMode)999,
            TrayDeviceIds = null! }.Normalize();
        Assert.Equal(TrayIconMode.Single, normalized.TrayIconMode);
        Assert.Equal(UpdateCheckMode.NotifyOnly, normalized.UpdateCheckMode);
        Assert.Empty(normalized.TrayDeviceIds);
    }

    [Fact]
    public void NewSettingsRoundTripWithExistingNamesAndStartupPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "battery-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings { TrayIconMode = TrayIconMode.SelectedDevices, TrayDeviceIds = ["a", "b"],
                UpdateCheckMode = UpdateCheckMode.Off, StartWithWindows = true, CustomNames = new Dictionary<string,string> { ["a"] = "Mouse" } });
            var loaded = store.Load();
            Assert.Null(loaded.Error);
            Assert.Equal(UpdateCheckMode.Off, loaded.Settings.UpdateCheckMode);
            Assert.Equal(TrayIconMode.SelectedDevices, loaded.Settings.TrayIconMode);
            Assert.Equal(new[] { "a", "b" }, loaded.Settings.TrayDeviceIds);
            Assert.Equal("Mouse", loaded.Settings.CustomNames["a"]);
            Assert.True(loaded.Settings.StartWithWindows);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
