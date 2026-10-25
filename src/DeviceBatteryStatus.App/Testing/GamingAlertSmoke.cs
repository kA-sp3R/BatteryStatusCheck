using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeviceBatteryStatus.App.Alerts;
using DeviceBatteryStatus.App.Tray;
using DeviceBatteryStatus.App.ViewModels;
using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Runtime;
using DeviceBatteryStatus.Core.Settings;
using Drawing = System.Drawing;

namespace DeviceBatteryStatus.App.Testing;

/// <summary>CI-only synthetic fixtures. No application controller, HID provider or game process is created.</summary>
internal static class GamingAlertSmoke
{
    internal static async Task<object> RunAsync(string output, List<Window> windows,
        Action<string> starting, Action<string, string> passed)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new AppSettings();
        var estimator = new RuntimeEstimator();
        var start = now.AddHours(-1);
        DeviceSnapshot snapshot = Snapshot("runtime", 90, start);

        starting("Runtime estimation, boundaries and popup/tray rendering");
        estimator.Observe([snapshot], settings, start);
        Require(estimator.Get(snapshot, settings, start).State == RuntimeEstimateState.Calculating,
            "An initial percentage must not produce an immediate runtime estimate.");
        for (var minute = 1; minute <= 60; minute++)
        {
            var at = start.AddMinutes(minute);
            snapshot = Snapshot("runtime", 90 - minute / 2, at);
            estimator.Observe([snapshot], settings, at);
        }
        var estimate = estimator.Get(snapshot, settings, now);
        Require(estimate is { State: RuntimeEstimateState.Available, IsConfident: true, Remaining: not null,
            LowerBound: not null, UpperBound: not null } && estimate.Coverage >= TimeSpan.FromMinutes(30),
            "A stable hour of synthetic discharge must establish a bounded, confident estimate.");
        var category = new DeviceSnapshot(new("gaming:category", "Synthetic category device", DeviceVendor.Logitech,
                "gaming:category:receiver", "synthetic-category"),
            BatteryReading.CurrentCategory(BatteryCategory.Low, ChargingState.Discharging, now), ConnectionState.Connected);
        var unavailable = estimator.Get(category, settings, now);
        Require(unavailable.State == RuntimeEstimateState.Unavailable && unavailable.Remaining is null,
            "A native category must never become a percentage or runtime.");
        var runtimeMap = new Dictionary<string, RuntimeEstimate>
        {
            [snapshot.Device.Id] = estimate, [category.Device.Id] = unavailable
        };
        var popup = new PopupWindow();
        windows.Add(popup);
        popup.SetDevices([snapshot, category], settings, now, "Synthetic runtime; no hardware queried", runtimeMap);
        popup.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var rows = Control<ItemsControl>(popup, "DeviceList").Items.Cast<DeviceRowViewModel>().ToArray();
        Require(rows[0].RuntimeText == estimate.Text && rows[0].ToolTip.Contains(estimate.Detail, StringComparison.Ordinal) &&
            rows[1].RuntimeText == "Unavailable" && !rows[1].PercentageText.Contains('%'),
            "The popup must show the actual runtime and preserve category-only presentation.");
        Capture(popup, Path.Combine(output, "runtime-popup.png"));
        using (var tray = new TrayIconManager(() => { }, () => { }, () => { }, () => { }))
        {
            var selected = settings with { TrayIconMode = TrayIconMode.SelectedDevices,
                TrayDeviceIds = [snapshot.Device.Id, category.Device.Id] };
            tray.Update([snapshot, category], selected, now, runtimeMap);
            Require(tray.DisplayedPresentations.Any(item => item.Presentation.Tooltip.Contains(estimate.Text, StringComparison.Ordinal)) &&
                tray.DisplayedPresentations.Any(item => item.Presentation.Tooltip.Contains("Runtime: Unavailable", StringComparison.Ordinal)),
                "Per-device tray tooltips must include runtime or its honest unavailable status.");
            var before = tray.Metrics;
            var changedMap = new Dictionary<string, RuntimeEstimate>(runtimeMap)
                { [snapshot.Device.Id] = estimate with { Text = "About 3 hours remaining" } };
            tray.Update([snapshot, category], selected, now, changedMap);
            Require(tray.Metrics.TooltipAssignments > before.TooltipAssignments && tray.Metrics.IconRenders == before.IconRenders,
                "A runtime-only tooltip change must not regenerate an unchanged percentage icon.");
        }
        popup.CloseForShutdown();
        Require(estimator.Get(snapshot, settings, now.AddMinutes(3)).State == RuntimeEstimateState.Unavailable,
            "An independently stale sample must invalidate runtime without a new hardware query.");
        var charging = snapshot with { Battery = BatteryReading.Current(60, ChargingState.Charging, now.AddMinutes(3)) };
        estimator.Observe([charging], settings, now.AddMinutes(3));
        var resumed = Snapshot("runtime", 59, now.AddMinutes(4));
        estimator.Observe([resumed], settings, now.AddMinutes(4));
        Require(estimator.Get(resumed, settings, now.AddMinutes(4)).State == RuntimeEstimateState.Calculating,
            "Charging must end the previous estimation period.");
        var disconnected = resumed with { Connection = ConnectionState.Disconnected };
        Require(estimator.Get(disconnected, settings, now.AddMinutes(4)).State == RuntimeEstimateState.Unavailable,
            "Disconnected time must not produce an estimate.");
        passed("Runtime estimation, boundaries and popup/tray rendering",
            "A synthetic 60-minute discharge produced a confident range; category, stale, charging and disconnected boundaries remained honest. Actual popup and selected tray tooltips rendered runtime; changing runtime alone did not regenerate icons.");

        starting("Gaming alert preferences, validation and isolated persistence");
        var original = settings with { TrayIconMode = TrayIconMode.SelectedDevices, TrayDeviceIds = [snapshot.Device.Id],
            UpdateCheckMode = UpdateCheckMode.Manual, CustomNames = new Dictionary<string, string> { [snapshot.Device.Id] = "Synthetic alias" } };
        AppSettings? saved = null;
        AppSettings? previewed = null;
        var previewCalls = 0;
        var editor = new AlertSettingsWindow(original, [snapshot, category], value => saved = value,
            value => { previewed = value; previewCalls++; });
        windows.Add(editor);
        editor.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(Control<CheckBox>(editor, "AlertSoundBox").IsChecked == false,
            "Sound must remain off by default.");
        Control<TextBox>(editor, "AlertLowPercentBox").Text = "10";
        Control<TextBox>(editor, "AlertCriticalPercentBox").Text = "20";
        Click(Control<Button>(editor, "AlertSaveButton"));
        Require(saved is null && !string.IsNullOrWhiteSpace(Control<TextBlock>(editor, "AlertErrorText").Text),
            "Invalid critical/low ordering must not be silently normalized and saved.");
        Control<TextBox>(editor, "AlertLowPercentBox").Text = "25";
        Control<TextBox>(editor, "AlertCriticalPercentBox").Text = "8";
        Control<ComboBox>(editor, "AlertModeBox").SelectedIndex = 2;
        Control<TextBox>(editor, "AlertLowRuntimeBox").Text = "90";
        Control<TextBox>(editor, "AlertCriticalRuntimeBox").Text = "20";
        Control<TextBox>(editor, "AlertDurationBox").Text = "4";
        Control<TextBox>(editor, "AlertCooldownBox").Text = "2";
        Control<TextBox>(editor, "AlertRecoveryPercentBox").Text = "6";
        Control<TextBox>(editor, "AlertRecoveryRuntimeBox").Text = "12";
        Click(Control<Button>(editor, "AlertPreviewButton"));
        Require(previewCalls == 1 && previewed?.GamingAlerts?.GlobalRule is
                { Mode: GamingAlertMode.Both, LowPercentage: 25, CriticalPercentage: 8, DurationSeconds: 4 } && saved is null && editor.IsVisible,
            "Preview must pass validated synthetic configuration without saving or closing the editor.");
        Control<ComboBox>(editor, "AlertScopeBox").SelectedIndex = 1;
        Control<CheckBox>(editor, "UseDeviceOverrideBox").IsChecked = true;
        Control<TextBox>(editor, "AlertLowPercentBox").Text = "18";
        Control<TextBox>(editor, "AlertCriticalPercentBox").Text = "6";
        Control<TextBox>(editor, "AlertDurationBox").Text = "5";
        Control<ComboBox>(editor, "AlertSnoozeBox").SelectedIndex = 2;
        Control<ComboBox>(editor, "AlertScopeBox").SelectedIndex = 2;
        Control<CheckBox>(editor, "UseDeviceOverrideBox").IsChecked = true;
        Require(!Control<ComboBox>(editor, "AlertModeBox").IsEnabled &&
            Control<CheckBox>(editor, "AlertCategoriesBox").IsEnabled,
            "Category-only devices must explain unavailable percentage/runtime options while retaining native alerts.");
        Control<ComboBox>(editor, "AlertScopeBox").SelectedIndex = 0;
        editor.UpdateLayout();
        Capture(editor, Path.Combine(output, "gaming-alert-settings.png"));
        Click(Control<Button>(editor, "AlertSaveButton"));
        Require(saved?.GamingAlerts is not null && original.GamingAlerts is null,
            "Saving gaming preferences must preserve the original immutable snapshot.");
        var store = new FileSettingsStore(Path.Combine(output, "synthetic-gaming-settings", "settings.json"));
        store.Save(saved!);
        var loaded = store.Load();
        var persisted = loaded.Settings;
        Require(loaded.Error is null && persisted.GamingAlerts?.GlobalRule is
                { Mode: GamingAlertMode.Both, LowPercentage: 25, CriticalPercentage: 8, LowRuntimeMinutes: 90,
                    CriticalRuntimeMinutes: 20, DurationSeconds: 4, Sound: false, CooldownMinutes: 2,
                    RecoveryPercentage: 6, RecoveryRuntimeMinutes: 12 } &&
            persisted.GamingAlerts.DeviceOverrides[snapshot.Device.Id] is
                { LowPercentage: 18, CriticalPercentage: 6, DurationSeconds: 5, SnoozedUntil: not null } &&
            persisted.TrayIconMode == original.TrayIconMode && persisted.UpdateCheckMode == original.UpdateCheckMode &&
            persisted.CustomNames[snapshot.Device.Id] == "Synthetic alias",
            "Existing settings storage must retain global rules, independent overrides, snooze and unrelated preferences.");
        passed("Gaming alert preferences, validation and isolated persistence",
            "Actual settings controls rejected invalid ordering, previewed without saving, edited global and device rules and disabled invented category runtime controls. Existing FileSettingsStore round-tripped rules/snooze while preserving tray/update/alias preferences in a synthetic report-directory fixture.");

        starting("Gaming threshold crossing, cooldown, recovery and revalidation");
        var policy = new GamingAlertPolicy();
        var rule = new GamingAlertRule { CooldownMinutes = 1, DurationSeconds = 3 };
        var alertSettings = settings with { GamingAlerts = new() { GlobalRule = rule } };
        var noRuntime = new Dictionary<string, RuntimeEstimate>();
        var low = Snapshot("low", 15, now);
        var lowAlert = policy.Evaluate([low], alertSettings, noRuntime, now).Single();
        Require(lowAlert.Severity == GamingAlertSeverity.Low && lowAlert.RuntimeText == "Unavailable",
            "A valid low percentage must alert even when runtime is unavailable.");
        policy.Acknowledge(lowAlert, now);
        Require(policy.Evaluate([low], alertSettings, noRuntime, now).Count == 0 &&
            policy.Revalidate(lowAlert, [low], alertSettings, noRuntime, now) is not null,
            "Acknowledgement must suppress duplicate delivery without invalidating the active alert.");
        var jitter = Snapshot("low", 21, now.AddMinutes(2));
        policy.Evaluate([jitter], alertSettings, noRuntime, now.AddMinutes(2));
        var lowAgain = Snapshot("low", 15, now.AddMinutes(3));
        Require(policy.Evaluate([lowAgain], alertSettings, noRuntime, now.AddMinutes(3)).Count == 0,
            "A small threshold fluctuation must not rearm a low alert after cooldown.");
        var recovered = Snapshot("low", 30, now.AddMinutes(4));
        policy.Evaluate([recovered], alertSettings, noRuntime, now.AddMinutes(4));
        lowAgain = Snapshot("low", 15, now.AddMinutes(5));
        var rearmed = policy.Evaluate([lowAgain], alertSettings, noRuntime, now.AddMinutes(5)).Single();
        policy.Acknowledge(rearmed, now.AddMinutes(5));
        var critical = Snapshot("low", 5, now.AddMinutes(5).AddSeconds(30));
        Require(policy.Evaluate([critical], alertSettings, noRuntime, now.AddMinutes(5).AddSeconds(30)).Single().Severity == GamingAlertSeverity.Critical,
            "Critical escalation must be delivered despite the preceding low-alert cooldown.");
        var staleLow = low with { Battery = low.Battery.AsStale("Synthetic stale alert") };
        var chargingLow = low with { Battery = BatteryReading.Current(15, ChargingState.Charging, now) };
        var snoozed = alertSettings with { GamingAlerts = new() { GlobalRule = rule with { SnoozedUntil = now.AddMinutes(15) } } };
        Require(policy.Revalidate(lowAlert, [staleLow], alertSettings, noRuntime, now) is null &&
            policy.Revalidate(lowAlert, [chargingLow], alertSettings, noRuntime, now) is null &&
            policy.Revalidate(lowAlert, [low], snoozed, noRuntime, now) is null,
            "Queued/visible alerts must be rejected when stale, charging or newly snoozed.");
        var native = new GamingAlertPolicy().Evaluate([category], alertSettings, noRuntime, now).Single();
        Require(native.Percentage is null && native.Category == BatteryCategory.Low && native.RuntimeText == "Unavailable",
            "Native low categories must alert without an invented percentage or runtime.");
        var runtimeRule = alertSettings with { GamingAlerts = new() { GlobalRule = rule with { Mode = GamingAlertMode.Runtime } } };
        var confident = new RuntimeEstimate(RuntimeEstimateState.Available, "Under 15 minutes remaining", "Synthetic confidence fixture",
            TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), true, now);
        var runtimeOnly = new Dictionary<string, RuntimeEstimate> { [low.Device.Id] = confident with { IsConfident = false } };
        Require(new GamingAlertPolicy().Evaluate([low], runtimeRule, runtimeOnly, now).Count == 0,
            "Runtime thresholds must exclude estimates without sufficient confidence.");
        runtimeOnly[low.Device.Id] = confident;
        Require(new GamingAlertPolicy().Evaluate([low], runtimeRule, runtimeOnly, now).Single().Severity == GamingAlertSeverity.Critical,
            "A fresh confident runtime range wholly below the critical threshold must trigger.");
        passed("Gaming threshold crossing, cooldown, recovery and revalidation",
            "Synthetic percentages/native categories and confident conservative runtime bounds triggered correctly. Acknowledgement, jitter, recovery, critical escalation, stale samples, charging and snooze were checked through the actual policy.");

        starting("Native gaming alert rendering, work area and input safety");
        var environment = new FakeEnvironment();
        var errors = new List<Exception>();
        var candidate = lowAlert with { Rule = rule with { DestinationMonitor = "synthetic-secondary" },
            RuntimeText = "About 2 hours remaining" };
        GamingAlertSafetyState safety;
        using (var presenter = new GamingAlertPresenter(environment, errors.Add))
        {
            presenter.Queue(candidate, now);
            presenter.Tick(now, value => value);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var alert = presenter.ActiveWindow ?? throw new InvalidOperationException("The gaming alert did not render.");
            safety = alert.GetSafetyState();
            Require(safety.NoActivate && safety.ClickThrough && safety.ToolWindow && safety.Layered &&
                (safety.ExtendedStyle & AlertNative.AppWindow) == 0 &&
                safety.MouseActivateResult.ToInt64() == AlertNative.NoActivateResult &&
                safety.HitTestResult.ToInt64() == AlertNative.TransparentHitTest &&
                !alert.ShowActivated && !alert.Focusable && !alert.ShowInTaskbar,
                "The actual native popup must carry no-activate/click-through styles and reject mouse activation/hit testing.");
            Require(safety.ForegroundBeforeShow == safety.ForegroundAfterShow && safety.ForegroundAfterShow != safety.WindowHandle,
                "Showing the alert changed the desktop foreground window.");
            Require(safety.Monitor.DeviceName == "synthetic-secondary" &&
                environment.Secondary.WorkArea.Contains(safety.NativeBounds) &&
                safety.NativeBounds.Right <= environment.Secondary.WorkArea.Right &&
                safety.NativeBounds.Bottom <= environment.Secondary.WorkArea.Bottom,
                "The alert must fit inside the selected synthetic monitor work area.");
            Require(Control<TextBlock>(alert, "AlertDeviceName").Text == candidate.Name &&
                Control<TextBlock>(alert, "AlertBatteryValue").Text.Contains("15%", StringComparison.Ordinal) &&
                Control<TextBlock>(alert, "AlertRuntime").Text == candidate.RuntimeText,
                "Actual compact alert rendering omitted name, measured value or runtime.");
            Capture(alert, Path.Combine(output, "gaming-alert.png"));
            Require(environment.SoundCalls == 0 && errors.Count == 0,
                "Default sound must remain silent and native rendering must report no errors.");
        }
        passed("Native gaming alert rendering, work area and input safety",
            "Actual WPF/HWND rendering retained the foreground window, no-activate/tool/layered/click-through styles, MA_NOACTIVATE and HTTRANSPARENT hooks. Placement fit a synthetic selected work area. This is a desktop-window safety check, not real-game compatibility proof.");

        starting("Gaming alert coalescing, fullscreen deferral and relevance");
        environment.Defer = true;
        var shown = 0;
        var revalidations = 0;
        using (var presenter = new GamingAlertPresenter(environment, errors.Add))
        {
            presenter.Shown += (_, _) => shown++;
            for (var repeat = 0; repeat < 20; repeat++) presenter.Queue(candidate, now);
            presenter.Tick(now, value => { revalidations++; return value; });
            Require(presenter.ActiveWindow is null && presenter.PendingCount == 1 && shown == 0,
                "Repeated blocked events must coalesce without showing or consuming delivery.");
            environment.Defer = false;
            presenter.Tick(now.AddSeconds(1), value => { revalidations++; return value; });
            Require(presenter.ActiveWindow is not null && presenter.PendingCount == 0 && shown == 1,
                "A retained relevant alert must display after the synthetic fullscreen condition ends.");
            var active = presenter.ActiveWindow;
            presenter.Queue(candidate with { Percentage = 14 }, now.AddSeconds(2));
            presenter.Tick(now.AddSeconds(2), value => value);
            Require(ReferenceEquals(active, presenter.ActiveWindow) && shown == 1,
                "A repeated event must update the single existing alert without another delivery.");
            presenter.Tick(now.AddSeconds(5), _ => null);
            Require(presenter.ActiveWindow is null && presenter.PendingCount == 0,
                "Repeated updates must not prolong the original display duration.");
            environment.Defer = true;
            presenter.Queue(candidate, now.AddSeconds(6));
            presenter.Tick(now.AddSeconds(6), _ => null);
            environment.Defer = false;
            presenter.Tick(now.AddSeconds(7), value => value);
            Require(presenter.ActiveWindow is null && presenter.PendingCount == 0 && shown == 1,
                "A queued alert that became irrelevant must not reappear after fullscreen ends.");
            environment.Defer = true;
            presenter.Queue(candidate, now.AddSeconds(8));
            presenter.Tick(now.AddMinutes(11), value => value);
            Require(presenter.PendingCount == 0,
                "Blocked alert retention must be bounded.");
        }
        passed("Gaming alert coalescing, fullscreen deferral and relevance",
            "A fake desktop environment coalesced 20 events into one retained alert, displayed only after deferral ended, updated without prolonging duration and discarded irrelevant/expired queued events. No game was launched.");

        starting("Gaming preview, category rendering, dismissal and disposal");
        var previewAcknowledged = 0;
        environment.Defer = true;
        var soundsBefore = environment.SoundCalls;
        using (var presenter = new GamingAlertPresenter(environment, errors.Add))
        {
            presenter.Shown += (_, _) => previewAcknowledged++;
            presenter.Preview(candidate with { Rule = rule with { Sound = true } }, now);
            Require(presenter.ActiveCandidate?.IsPreview == true && presenter.ActiveWindow is not null &&
                previewAcknowledged == 0 && environment.SoundCalls == soundsBefore + 1,
                "Synthetic preview must render independently without acknowledging a real threshold; sound uses the fake environment only.");
            presenter.Tick(now.AddSeconds(4), _ => null);
            Require(presenter.ActiveWindow is null, "Synthetic preview must dismiss automatically.");
            presenter.Preview(native, now.AddSeconds(5));
            Require(Control<TextBlock>(presenter.ActiveWindow!, "AlertBatteryValue").Text.Contains("Low", StringComparison.Ordinal) &&
                !Control<TextBlock>(presenter.ActiveWindow!, "AlertBatteryValue").Text.Contains('%') &&
                Control<TextBlock>(presenter.ActiveWindow!, "AlertRuntime").Text == "Runtime unavailable",
                "Category previews must retain the native label and unavailable runtime.");
            presenter.Clear();
            Require(presenter.ActiveWindow is null && presenter.PendingCount == 0,
                "Clearing an alert presenter must close the window and release queued state.");
        }
        Require(errors.Count == 0, "Synthetic alert execution reported an unexpected error.");
        passed("Gaming preview, category rendering, dismissal and disposal",
            "Synthetic preview bypassed fake fullscreen deferral without consuming real alert delivery, preserved native categories, auto-dismissed and cleared/disposed its window. Enabled sound invoked only an in-memory fake callback; no audio device was used.");

        return new
        {
            mode = "synthetic-desktop-gaming-alert-smoke", physicalDevicesAccessed = false, gamesLaunched = false,
            runtime = new { estimate.State, estimate.Text, estimate.IsConfident, estimate.SampleCount, estimate.Coverage,
                estimate.Remaining, estimate.LowerBound, estimate.UpperBound },
            nativeSafety = new { safety.NoActivate, safety.ClickThrough, safety.ToolWindow, safety.Layered,
                foregroundUnchanged = safety.ForegroundBeforeShow == safety.ForegroundAfterShow,
                mouseActivateResult = safety.MouseActivateResult.ToInt64(), hitTestResult = safety.HitTestResult.ToInt64(),
                bounds = new { safety.NativeBounds.X, safety.NativeBounds.Y, safety.NativeBounds.Width, safety.NativeBounds.Height },
                monitor = "synthetic-selected-work-area", safety.Monitor.Dpi },
            repeatedEvents = 20, coalescedDeliveries = shown, revalidations, previewAcknowledged,
            soundCallbacks = environment.SoundCalls, audioHardwareAccessed = false,
            settingsStorage = "Synthetic fixture beneath the smoke report directory only",
            limits = "No physical-device trend, real game, exclusive-fullscreen compatibility, actual monitor switching, hardware DPI transition or user-input delivery was tested. Native safety styles and direct message-hook responses are measured in the CI desktop only."
        };
    }

    private static DeviceSnapshot Snapshot(string id, int percentage, DateTimeOffset at) =>
        new(new($"gaming:{id}", "Synthetic Viper gaming mouse", DeviceVendor.Razer, $"gaming:receiver:{id}", "synthetic-model"),
            BatteryReading.Current(percentage, ChargingState.Discharging, at), ConnectionState.Connected);

    private sealed class FakeEnvironment : IGamingAlertEnvironment
    {
        internal AlertMonitor Primary { get; } = new("synthetic-primary", "Synthetic primary", true,
            new Drawing.Rectangle(0, 0, 1600, 900), new Drawing.Rectangle(0, 0, 1600, 860), 96);
        internal AlertMonitor Secondary { get; } = new("synthetic-secondary", "Synthetic secondary", false,
            new Drawing.Rectangle(-1600, 0, 1600, 900), new Drawing.Rectangle(-1600, 0, 1600, 860), 144);
        internal bool Defer { get; set; }
        internal int SoundCalls { get; private set; }
        public AlertMonitor ResolveMonitor(string? destinationMonitor) => destinationMonitor == Secondary.DeviceName ? Secondary : Primary;
        public bool ShouldDefer(AlertMonitor monitor) => Defer;
        public IntPtr ForegroundWindow => AlertNative.GetForegroundWindow();
        public void PlaySound() => SoundCalls++;
    }

    private static T Control<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Expected {name} control was not found.");
    private static void Click(Button button)
    {
        Require(button.IsEnabled, "A required synthetic UI action was disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        Require(window.ActualWidth > 0 && window.ActualHeight > 0, "The synthetic window did not finish layout.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
