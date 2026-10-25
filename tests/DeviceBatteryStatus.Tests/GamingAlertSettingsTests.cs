using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Settings;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class GamingAlertSettingsTests
{
    [Fact]
    public void DefaultsKeepSoundOffAndExistingLowThresholdMigratesWithoutEnablingNotifications()
    {
        var old = new AppSettings { LowBatteryThreshold = 7, LowBatteryNotifications = false };
        var rule = GamingAlertSettings.FromLegacy(old).GlobalRule;
        Assert.Equal(7, rule.LowPercentage);
        Assert.Equal(6, rule.CriticalPercentage);
        Assert.False(rule.Sound);
        Assert.False(old.LowBatteryNotifications);
        Assert.Null(old.GamingAlerts);
    }

    [Fact]
    public void UserInputRejectsThresholdOrderingAndOutOfRangeValues()
    {
        GamingAlertRule[] invalid = [new() { LowPercentage = 20, CriticalPercentage = 20 }, new() { CriticalPercentage = -1 },
            new() { LowPercentage = 0 }, new() { LowPercentage = 51 }, new() { LowRuntimeMinutes = 15, CriticalRuntimeMinutes = 15 },
            new() { CriticalRuntimeMinutes = 0 }, new() { DurationSeconds = 2 }, new() { DurationSeconds = 31 },
            new() { CooldownMinutes = 0 }, new() { RecoveryPercentage = 0 }, new() { RecoveryRuntimeMinutes = 121 },
            new() { Mode = (GamingAlertMode)255 }, new() { DestinationMonitor = "bad\nmonitor" }];
        foreach (var rule in invalid)
        {
            Assert.False(rule.TryValidate(out var message));
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.True(rule.Normalize().TryValidate(out _));
        }
    }

    [Fact]
    public void NormalizationCopiesAndBoundsDeviceRulesAndPreservesPerDevicePresentation()
    {
        var source = Enumerable.Range(0, 100).ToDictionary(index => $"device-{index}", _ => new GamingAlertRule { LowPercentage = 30, CriticalPercentage = 7, DurationSeconds = 12, Sound = true, DestinationMonitor = "DISPLAY2" });
        source["bad\nid"] = new();
        var normalized = new AppSettings { GamingAlerts = new() { DeviceOverrides = source } }.Normalize();
        source["device-0"] = new() { LowPercentage = 1 };
        Assert.Equal(64, normalized.GamingAlerts!.DeviceOverrides.Count);
        Assert.False(normalized.GamingAlerts.DeviceOverrides.ContainsKey("bad\nid"));
        Assert.Equal(30, normalized.GamingAlerts.ForDevice("device-0").LowPercentage);
        Assert.Equal(12, normalized.GamingAlerts.ForDevice("device-0").DurationSeconds);
        Assert.True(normalized.GamingAlerts.ForDevice("device-0").Sound);
        Assert.False(normalized.GamingAlerts.GlobalRule.Sound);
    }

    [Fact]
    public void GlobalAndOverrideRulesRoundTripWithSnoozeMonitorAndExistingPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gaming-alert-settings-{Guid.NewGuid():N}");
        try
        {
            var until = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
            var settings = new AppSettings
            {
                RefreshIntervalSeconds = 120, TrayIconMode = TrayIconMode.SelectedDevices,
                TrayDeviceIds = ["mouse"], UpdateCheckMode = UpdateCheckMode.Off, StartWithWindows = true,
                GamingAlerts = new()
                {
                    GlobalRule = new() { Mode = GamingAlertMode.Both, LowPercentage = 25, CriticalPercentage = 5,
                        LowRuntimeMinutes = 90, CriticalRuntimeMinutes = 20, SnoozedUntil = until },
                    DeviceOverrides = new Dictionary<string, GamingAlertRule>
                    { ["mouse"] = new() { Mode = GamingAlertMode.Runtime, Sound = true, DestinationMonitor = "\\\\.\\DISPLAY2", DurationSeconds = 14, SnoozedUntil = until.AddMinutes(5), CooldownMinutes = 30 } }
                }
            };
            var store = new FileSettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(settings);
            var loaded = store.Load();
            Assert.Null(loaded.Error);
            Assert.Equal(settings.GamingAlerts.GlobalRule, loaded.Settings.GamingAlerts!.GlobalRule);
            Assert.Equal(settings.GamingAlerts.DeviceOverrides["mouse"], loaded.Settings.GamingAlerts.DeviceOverrides["mouse"]);
            Assert.Equal(120, loaded.Settings.RefreshIntervalSeconds);
            Assert.Equal(TrayIconMode.SelectedDevices, loaded.Settings.TrayIconMode);
            Assert.Equal(UpdateCheckMode.Off, loaded.Settings.UpdateCheckMode);
            Assert.True(loaded.Settings.StartWithWindows);
            Assert.Equal("mouse", Assert.Single(loaded.Settings.TrayDeviceIds));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
