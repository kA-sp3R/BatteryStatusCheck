using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeviceBatteryStatus.App.Tray;
using DeviceBatteryStatus.App.ViewModels;
using DeviceBatteryStatus.Core.Diagnostics;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Updates;
using Drawing = System.Drawing;

namespace DeviceBatteryStatus.App.Testing;

/// <summary>
/// Internal Windows CI entry point. It never constructs the application controller,
/// enumerates HID, contacts a server, changes startup registration or installs anything.
/// </summary>
internal static class SoftwareSmokeTest
{
    private const int ExpectedChecks = 12;
    private sealed record Check(string Name, bool Passed, string Detail);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };

    internal static async Task<int> RunAsync(string reportDirectory)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var checks = new List<Check>();
        var artifacts = new List<string>();
        var windows = new List<Window>();
        string? output = null;
        string? failure = null;
        string currentCheck = "Initialize smoke workspace";
        var assembly = Assembly.GetEntryAssembly();
        var assemblyVersion = assembly?.GetName().Version?.ToString() ?? "unknown";
        var version = assembly?.GetName().Version?.ToString(3) ?? "unknown";
        var informationalVersion = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var revision = Regex.Match(informationalVersion, @"(?:\+|\.)([0-9a-fA-F]{40})(?:$|[.+])",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var sourceCommit = revision.Success ? revision.Groups[1].Value.ToLowerInvariant() : "unknown";
        var flavor = "unknown";
        var metrics = new Dictionary<string, int>();
        object? efficiency = null;
        try
        {
            Application.Current.Dispatcher.VerifyAccess();
            Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "The smoke process must use STA.");
            output = Path.GetFullPath(reportDirectory);
            Directory.CreateDirectory(output);
            using (var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "DeviceBatteryStatus.runtimeconfig.json"))))
            {
                var options = runtime.RootElement.GetProperty("runtimeOptions");
                flavor = options.TryGetProperty("includedFrameworks", out _)
                    ? "self-contained" : options.TryGetProperty("frameworks", out _)
                        ? "framework-dependent" : throw new InvalidDataException("Runtime package flavor is unknown.");
            }
            Require(RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "The smoke process must use the packaged x64 architecture.");
            var now = DateTimeOffset.UtcNow;
            var devices = SyntheticDevices(now);
            var settings = new AppSettings();

            currentCheck = "Popup binding and rendering";
            var popup = new PopupWindow();
            windows.Add(popup);
            popup.SetDevices(devices, settings, now, "Synthetic smoke data; no hardware queried");
            popup.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            popup.UpdateLayout();
            var list = Control<ItemsControl>(popup, "DeviceList");
            var rows = list.Items.Cast<DeviceRowViewModel>().ToArray();
            Require(rows.Length == devices.Count, "The popup did not bind every synthetic device.");
            Require(rows[0].PercentageText == "0%", "A genuine zero must remain visible.");
            Require(rows[1].PercentageText.Contains("Low", StringComparison.Ordinal) &&
                !rows[1].PercentageText.Contains('%') && rows[1].ProgressVisibility == Visibility.Collapsed,
                "The category must remain a category without a percentage bar.");
            Require(rows[2].PercentageText.Contains("stale", StringComparison.OrdinalIgnoreCase) &&
                rows[2].ToolTip.Contains("37%", StringComparison.Ordinal), "The stale measurement must retain its value and label.");
            Require(rows[3].StatusText.Contains("Unsupported", StringComparison.Ordinal), "Unsupported state must remain visible.");
            var renderedTexts = Descendants<TextBlock>(popup).Select(text => text.Text).ToArray();
            Require(renderedTexts.Contains("0%") && renderedTexts.Any(text => text.Contains("Low", StringComparison.Ordinal)),
                "The rendered popup did not expose its bound zero/category text.");
            SaveWindow(popup, Path.Combine(output, "popup.png"));
            artifacts.Add("popup.png");
            checks.Add(new(currentCheck, true, "Actual WPF templates rendered genuine 0%, native category, stale and unsupported rows."));
            popup.CloseForShutdown();

            currentCheck = "Settings defaults and callbacks";
            AppSettings? saved = null;
            var pollingCalls = 0;
            var updateCalls = 0;
            var openLogCalls = 0;
            var exportLogCalls = 0;
            var settingsWindow = new SettingsWindow(settings, devices, false, false, null,
                (value, startupChanged) =>
                {
                    Require(!startupChanged, "The smoke must never request startup registration.");
                    saved = value;
                }, () => pollingCalls++, () => updateCalls++, () => openLogCalls++, () => exportLogCalls++);
            windows.Add(settingsWindow);
            settingsWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            settingsWindow.UpdateLayout();
            var trayMode = Control<ComboBox>(settingsWindow, "TrayModeBox");
            var updateMode = Control<ComboBox>(settingsWindow, "UpdateModeBox");
            Require(trayMode.SelectedIndex == 0 && updateMode.SelectedIndex == 0,
                "Defaults must be single icon and notify-only updates.");
            Require(Control<StackPanel>(settingsWindow, "TraySelectionPanel").Visibility == Visibility.Collapsed,
                "Device icon selection must be opt-in.");
            Click(Control<Button>(settingsWindow, "PollingButton"));
            Click(Control<Button>(settingsWindow, "UpdatesButton"));
            Click(Control<Button>(settingsWindow, "OpenLogsButton"));
            Click(Control<Button>(settingsWindow, "ExportLogsButton"));
            Require(pollingCalls == 1 && updateCalls == 1 && openLogCalls == 1 && exportLogCalls == 1,
                "Settings actions did not reach their isolated callbacks.");
            updateMode.SelectedIndex = 2;
            Click(Control<Button>(settingsWindow, "UpdatesButton"));
            Require(updateCalls == 1, "An unsaved update-mode change must not trigger an update check.");
            SaveWindow(settingsWindow, Path.Combine(output, "settings.png"));
            artifacts.Add("settings.png");
            checks.Add(new(currentCheck, true, "Settings default to single/notify-only; polling, updates, log opening/export reach fake callbacks only."));

            currentCheck = "Settings save round trip";
            trayMode.SelectedIndex = 1;
            Require(Control<StackPanel>(settingsWindow, "TraySelectionPanel").Visibility == Visibility.Visible &&
                !Control<ComboBox>(settingsWindow, "PreferredBox").IsEnabled,
                "Selecting separate icons must reveal device choices without using summary preference.");
            var choices = Control<StackPanel>(settingsWindow, "TrayDevicesPanel").Children.OfType<CheckBox>().ToArray();
            Require(choices.Length >= 2, "The settings window did not offer detected devices for tray selection.");
            choices[0].IsChecked = true;
            choices[1].IsChecked = true;
            updateMode.SelectedIndex = 1;
            Control<TextBox>(settingsWindow, "IntervalBox").Text = "45";
            settingsWindow.UpdateLayout();
            SaveWindow(settingsWindow, Path.Combine(output, "settings-selected.png"));
            artifacts.Add("settings-selected.png");
            var saveButton = Descendants<Button>(settingsWindow).Single(button => button.Content is string text && text == "Save");
            Click(saveButton);
            Require(saved is { TrayIconMode: TrayIconMode.SelectedDevices, UpdateCheckMode: UpdateCheckMode.Manual,
                RefreshIntervalSeconds: 45 } && saved.TrayDeviceIds.SequenceEqual(devices.Take(2).Select(device => device.Device.Id)),
                "Saving must preserve the selected device IDs and explicit update preference.");
            Require(settings.TrayIconMode == TrayIconMode.Single && settings.TrayDeviceIds.Count == 0,
                "Editing settings must not mutate the original snapshot.");
            checks.Add(new(currentCheck, true, "Actual Save handler retained two selected IDs/manual updates in memory without registry or settings-file writes."));

            currentCheck = "Update window off mode";
            using (var noNetwork = new NoNetworkHandler())
            using (var service = new GitHubUpdateService(Path.Combine(output, "update-staging"), noNetwork))
            {
                var installCalls = 0;
                var updateErrors = new List<Exception>();
                var updateWindow = new UpdateWindow(service, version, () => UpdateCheckMode.Off,
                    _ => installCalls++, exception => updateErrors.Add(exception));
                windows.Add(updateWindow);
                updateWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                updateWindow.UpdateLayout();
                var updateButtons = Descendants<Button>(updateWindow).Where(button => button.Content is string text &&
                    text is "Check now" or "Download and verify" or "Install verified update").ToArray();
                Require(updateButtons.Length == 3 && updateButtons.All(button => !button.IsEnabled),
                    "Off mode must disable checking, downloading and installing.");
                Require(noNetwork.RequestCount == 0 && installCalls == 0 && updateErrors.Count == 0,
                    "Opening the disabled update window must not contact a server or install anything.");
                Require(Descendants<TextBlock>(updateWindow).Any(text => text.Text.Contains("checks are off", StringComparison.OrdinalIgnoreCase)),
                    "The update window must explain the disabled preference.");
                SaveWindow(updateWindow, Path.Combine(output, "updates-off.png"));
                artifacts.Add("updates-off.png");
                updateWindow.Close();
            }
            checks.Add(new(currentCheck, true, "Actual update window rendered Off mode, disabled all three actions and issued zero fake HTTP requests or installer callbacks."));

            currentCheck = "Centered circular tray renderings";
            foreach (var example in new[]
            {
                (Name: "tray-0-48.png", Value: (int?)0, Color: BatteryColor.Red),
                (Name: "tray-100-48.png", Value: (int?)100, Color: BatteryColor.Green),
                (Name: "tray-neutral-48.png", Value: (int?)null, Color: BatteryColor.Neutral)
            })
            {
                using var icon = TrayIconRenderer.Render(48, 48, example.Value, example.Color);
                using var bitmap = icon.ToBitmap();
                Require(bitmap.Width == 48 && bitmap.Height == 48, "The icon dimensions changed.");
                Require(bitmap.GetPixel(0, 0).A < 40 && bitmap.GetPixel(47, 0).A < 40 &&
                    bitmap.GetPixel(0, 47).A < 40 && bitmap.GetPixel(47, 47).A < 40,
                    "The circular icon's corners must remain transparent.");
                CheckGlyphCentered(bitmap, example.Color == BatteryColor.Neutral);
                bitmap.Save(Path.Combine(output, example.Name), Drawing.Imaging.ImageFormat.Png);
                artifacts.Add(example.Name);
            }
            using (var small = TrayIconRenderer.Render(16, 16, 100, BatteryColor.Green))
            using (var large = TrayIconRenderer.Render(64, 64, 0, BatteryColor.Red))
            {
                Require(small.Width == 16 && large.Width == 64, "DPI-scaled rendering did not return the requested size.");
            }
            checks.Add(new(currentCheck, true, "Native GDI icon rendering preserved transparent circular corners and centered 0/100/neutral glyphs; 16/48/64 px exercised."));

            currentCheck = "Tray lifecycle, selection and simulated shell recovery";
            var separate = settings with
            {
                TrayIconMode = TrayIconMode.SelectedDevices,
                TrayDeviceIds = devices.Select(device => device.Device.Id).Append("smoke:missing").ToArray()
            };
            using (var manager = CreateTrayManager())
            {
                manager.Update(devices, settings, now);
                Require(manager.ActiveIconCount == 1, "The default mode must own exactly one icon.");
                manager.Update(devices, separate, now);
                Require(manager.ActiveIconCount == 5, "Separate mode must own one icon for each selected ID, including an unavailable device.");
                manager.UpdateDpi(144);
                manager.UpdateDpi(192);
                var notifiedWindows = SimulateTaskbarCreated();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(notifiedWindows > 0 && manager.ActiveIconCount == 5,
                    "Simulated shell recovery changed tray ownership.");
                manager.Update(devices, separate with { TrayDeviceIds = [devices[0].Device.Id] }, now);
                Require(manager.ActiveIconCount == 1, "Removing selected devices must dispose their icons.");
                manager.Update(devices, separate with { TrayDeviceIds = [] }, now);
                Require(manager.ActiveIconCount == 1, "An empty selection must retain an accessible summary icon.");
                manager.Dispose();
                Require(manager.ActiveIconCount == 0, "Disposing the manager must release every icon.");
                metrics["taskbarCreatedWindowsMessaged"] = notifiedWindows;
            }
            checks.Add(new(currentCheck, true, "Native NotifyIcon ownership exercised for single/selected/missing IDs, programmatic DPI changes and process-local TaskbarCreated dispatch. Real Explorer restart remains a manual check."));

            currentCheck = "Bounded GDI and USER ownership";
            using var resourceProbe = new GuiResourceProbe();
            await SettleResourcesAsync();
            var before = ReadGuiResources();
            for (var iteration = 0; iteration < 20; iteration++)
            {
                using var manager = CreateTrayManager();
                manager.Update(devices, separate, now);
                manager.UpdateDpi(iteration % 2 == 0 ? 144u : 192u);
                manager.Update(devices, settings, now);
                manager.Dispose();
                Require(manager.ActiveIconCount == 0, "A disposed manager retained tray icons.");
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            await SettleResourcesAsync();
            var after = ReadGuiResources();
            metrics["gdiBefore"] = before.Gdi;
            metrics["gdiAfter"] = after.Gdi;
            metrics["userBefore"] = before.User;
            metrics["userAfter"] = after.User;
            metrics["trayLifecycleIterations"] = 20;
            Require(after.Gdi <= before.Gdi + 16 && after.User <= before.User + 16,
                $"Repeated icon lifecycle leaked GUI handles: GDI {before.Gdi}->{after.Gdi}; USER {before.User}->{after.User}.");
            checks.Add(new(currentCheck, true, $"20 create/update/DPI/dispose cycles; GDI {before.Gdi}->{after.Gdi}, USER {before.User}->{after.User}; allowance 16 handles for runtime caches."));

            currentCheck = "Redacted diagnostic export";
            const string serial = "SMOKE-SERIAL-DO-NOT-EXPORT";
            const string path = @"\\?\hid#vid_1532&pid_00b3#SMOKE-SERIAL-DO-NOT-EXPORT#{11111111-2222-3333-4444-555555555555}";
            using (var log = new DiagnosticLog(Path.Combine(output, "diagnostic-storage"), version))
            {
                log.RecordEndpoint("smoke-selection", new HidEndpoint(path, 0x1532, 0x00B3,
                    0xFF00, 1, 0, 0, 91, "SMOKE-PRIVATE-RECEIVER", SerialNumber: serial));
                foreach (var device in devices) log.RecordSnapshot(device);
                log.RecordException("validation", new InvalidDataException($"Reply checksum failed for {path}; serial {serial}."));
                log.Export(Path.Combine(output, "diagnostics-smoke.zip"));
            }
            using (var archive = ZipFile.OpenRead(Path.Combine(output, "diagnostics-smoke.zip")))
            {
                Require(archive.Entries.Count > 0 && archive.Entries.Any(entry => entry.Name == "diagnostic-manifest.json"),
                    "Export omitted its diagnostic manifest.");
                var text = new StringBuilder();
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    text.Append(reader.ReadToEnd());
                }
                var exported = text.ToString();
                Require(!exported.Contains(serial, StringComparison.Ordinal) && !exported.Contains(path, StringComparison.Ordinal) &&
                    !exported.Contains("SMOKE-PRIVATE-RECEIVER", StringComparison.Ordinal), "Diagnostic export leaked an identifying test value.");
                Require(exported.Contains(version, StringComparison.Ordinal), "Diagnostic export omitted the app version.");
            }
            artifacts.Add("diagnostics-smoke.zip");
            checks.Add(new(currentCheck, true, "Actual bounded logger/export produced a ZIP with version/snapshot/protocol-failure records and no synthetic serial, receiver ID or HID path."));

            currentCheck = "Capacity measurement, rejection and restart history";
            var capacityFixture = VerifyCapacityHistory(output);
            checks.Add(new(currentCheck, true, "Synthetic measured-current and measured-power integration used complete explicit boundaries, source/units/basis, retained accepted history across restart and rejected interrupted/incomplete/uncertain measurements."));

            currentCheck = "Capacity details rendering and design entry";
            var capacityPresentation = await VerifyCapacityDetailsAsync(capacityFixture, devices[0], output, windows);
            artifacts.Add("capacity-details.png");
            artifacts.Add("capacity-unavailable.png");
            checks.Add(new(currentCheck, true, "Actual compact details rendered design/full capacity/health/date and unavailable telemetry explanation; design entry saved through its real handler without creating a capacity measurement."));

            currentCheck = "Changed-only presentation and device-change debounce";
            var presentation = await VerifyChangedPresentationAsync(devices, settings, separate, now, windows);
            checks.Add(new(currentCheck, true, "Repeated unchanged popup/tray updates reused presentation and native resources; alias/value/freshness/DPI changes still updated, and synthetic PnP requests coalesced without HID access."));

            currentCheck = "Reproducible synthetic efficiency workload";
            using (var workloadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var monitorWorkload = await Task.Run(() => EfficiencyWorkload.RunAsync(workloadTimeout.Token));
                efficiency = new { schemaVersion = 1, mode = "synthetic-software-efficiency-smoke", sourceCommit,
                    appVersion = version, flavor, repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"),
                    workflowRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                    workflowAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                    measuredAtUtc = DateTimeOffset.UtcNow, physicalDevicesAccessed = false,
                    normalApplicationControllerStarted = false, monitorWorkload, presentation, capacityPresentation };
                await File.WriteAllTextAsync(Path.Combine(output, "efficiency-workload.json"),
                    JsonSerializer.Serialize(efficiency, JsonOptions) + Environment.NewLine);
            }
            artifacts.Add("efficiency-workload.json");
            checks.Add(new(currentCheck, true, "Equal synthetic healthy/non-responsive receiver traces retained every healthy poll while reducing discovery, failed queries and opens; short process CPU/memory/allocation/handle observations recorded without physical or endurance claims."));
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            checks.Add(new(currentCheck, false, exception.Message));
        }
        finally
        {
            foreach (var window in windows)
            {
                try
                {
                    if (window is PopupWindow popup) popup.CloseForShutdown();
                    else window.Close();
                }
                catch { /* The original smoke failure is retained in the report. */ }
            }
            if (output is not null)
            {
                try
                {
                    var report = new
                    {
                        schemaVersion = 1,
                        mode = "hardware-free-windows-application-smoke",
                        appVersion = version,
                        assemblyVersion,
                        informationalVersion,
                        sourceCommit,
                        repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"),
                        workflowRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                        workflowAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                        flavor,
                        runtimeIdentifier = "win-x64",
                        startedAt,
                        completedAt = DateTimeOffset.UtcNow,
                        passed = failure is null && checks.Count == ExpectedChecks,
                        applicationLaunched = true,
                        normalApplicationControllerStarted = false,
                        hardwareAccessed = false,
                        networkAccessed = false,
                        startupRegistrationAccessed = false,
                        userSettingsPersisted = false,
                        counters = new { total = ExpectedChecks, executed = checks.Count,
                            passed = checks.Count(check => check.Passed), failed = checks.Count(check => !check.Passed) },
                        checks,
                        metrics,
                        efficiency,
                        artifacts,
                        failure,
                        verificationBoundary = new[]
                        {
                            "Synthetic in-memory devices only; no HID discovery or commands.",
                            "Update/log/settings callbacks are fake; no update network or installer execution.",
                            "App startup registration and saved preferences are not accessed; Windows may maintain its own notification-area metadata.",
                            "TaskbarCreated is delivered only to this process; real Explorer restart and tray placement are unverified.",
                            "Programmatic DPI paths are exercised; physical mixed-monitor behavior is unverified.",
                            "Capacity storage belongs only to synthetic fixtures inside this report directory, not real user capacity history.",
                            "Synthetic device-change requests exercise coalescing; actual USB plug/unplug, Windows sleep/resume and real Explorer restart remain unverified.",
                            "The short fake-HID workload reports process-wide observations and deterministic query counts, not physical-device battery drain or extended performance/endurance."
                        }
                    };
                    await File.WriteAllTextAsync(Path.Combine(output, "application-smoke.json"),
                        JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
                }
                catch (Exception reportException)
                {
                    Console.Error.WriteLine($"Smoke report could not be written ({reportException.GetType().Name}).");
                    failure ??= "Smoke report could not be written.";
                }
            }
        }
        return failure is null && checks.Count == ExpectedChecks ? 0 : 1;
    }

    private static IReadOnlyList<DeviceSnapshot> SyntheticDevices(DateTimeOffset now) =>
    [
        new(new("smoke:zero", "Razer Viper smoke mouse", DeviceVendor.Razer, "smoke:receiver:mouse", "1532:00C1"),
            BatteryReading.Current(0, ChargingState.Discharging, now), ConnectionState.Connected),
        new(new("smoke:category", "Logitech category smoke device", DeviceVendor.Logitech, "smoke:receiver:category", "046D:TEST"),
            BatteryReading.CurrentCategory(BatteryCategory.Low, ChargingState.Unknown, now), ConnectionState.Connected),
        new(new("smoke:stale", "Stale smoke device", DeviceVendor.Razer, "smoke:receiver:stale", "1532:00A6"),
            BatteryReading.Current(37, ChargingState.Unknown, now.AddMinutes(-10)).AsStale("Synthetic stale sample.")),
        new(new("smoke:unsupported", "Unsupported smoke device", DeviceVendor.Alienware, "smoke:receiver:unsupported", "413C:TEST"),
            BatteryReading.Unavailable(ReadingState.Unsupported, "Synthetic unsupported protocol; no query sent."))
    ];

    private static TrayIconManager CreateTrayManager() => new(() => { }, () => { }, () => { }, () => { });

    private sealed record CapacitySmokeFixture(string StorePath, DeviceSnapshot Snapshot, SmokeClock Clock);

    private static CapacitySmokeFixture VerifyCapacityHistory(string output)
    {
        var clock = new SmokeClock();
        var path = Path.Combine(output, "synthetic-capacity-history.json");
        foreach (var name in new[] { "synthetic-capacity-history.json", "synthetic-capacity-pending.json", "synthetic-capacity-restarted.json" })
        {
            File.Delete(Path.Combine(output, name));
            File.Delete(Path.Combine(output, name + ".bak"));
        }
        var current = CapacitySnapshot("current", clock.GetUtcNow(), current: 500m, full: true, sequence: 0);
        using (var health = new CapacityHealthService(path, clock))
        {
            health.SetDesignCapacity(current, new(1000m, CapacityUnit.MilliampHours, CapacityBasis.BatteryPack,
                "Synthetic original design specification; not a real device claim"));
            health.Observe([current], clock.GetUtcNow());
            for (var minute = 1; minute <= 60; minute++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                current = CapacitySnapshot("current", clock.GetUtcNow(), current: 500m, empty: minute == 60, sequence: minute);
                health.Observe([current], clock.GetUtcNow());
            }
            var complete = health.GetDetails(current);
            Require(complete.LatestMeasurement is { Complete: true, Method: CapacityMeasurementMethod.CurrentIntegration,
                    SampleCount: 61, Unit: CapacityUnit.MilliampHours } &&
                complete.LatestMeasurement.FullChargeCapacity is { } usable && Math.Abs(usable - 500m) < .0001m &&
                complete.HealthPercentage is { } ratio && Math.Abs(ratio - 50m) < .0001m &&
                complete.LatestMeasurement.CoverageFraction == 1m &&
                complete.LatestMeasurement.MaximumSampleGap == TimeSpan.FromSeconds(90) &&
                complete.LatestMeasurement.SequenceContinuityVerified,
                "Measured current must integrate to usable mAh over a complete explicit cycle independently of SOC.");
            health.SetDesignCapacity(current, new(1m, CapacityUnit.WattHours, CapacityBasis.BatteryPack, "Synthetic incompatible units"));
            Require(health.GetDetails(current).HealthPercentage is null, "mAh and Wh cannot be compared or nominally converted.");
            health.SetDesignCapacity(current, new(1000m, CapacityUnit.MilliampHours, CapacityBasis.SingleCell, "Synthetic incompatible basis"));
            Require(health.GetDetails(current).HealthPercentage is null, "Pack and single-cell capacity cannot be compared.");
            health.SetDesignCapacity(current, new(1000m, CapacityUnit.MilliampHours, CapacityBasis.BatteryPack,
                "Synthetic original design specification; not a real device claim"));

            var power = CapacitySnapshot("power", clock.GetUtcNow(), power: 2m, full: true);
            health.SetDesignCapacity(power, new(1m, CapacityUnit.WattHours, CapacityBasis.BatteryPack, "Synthetic Wh design specification"));
            health.Observe([power], clock.GetUtcNow());
            for (var minute = 1; minute <= 15; minute++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                power = CapacitySnapshot("power", clock.GetUtcNow(), power: 2m, empty: minute == 15);
                health.Observe([power], clock.GetUtcNow());
            }
            var powerResult = health.GetDetails(power);
            Require(powerResult.LatestMeasurement is { Complete: true, Method: CapacityMeasurementMethod.PowerIntegration,
                    Unit: CapacityUnit.WattHours } && powerResult.LatestMeasurement.FullChargeCapacity is { } wh &&
                Math.Abs(wh - .5m) < .0001m && powerResult.HealthPercentage is { } powerRatio && Math.Abs(powerRatio - 50m) < .0001m,
                "Measured watts must integrate to Wh without using voltage, runtime alone or an assumed draw.");

            var reported = CapacitySnapshot("reported", clock.GetUtcNow(), reported: 400m, kind: CapacityFieldKind.FullChargeCapacity);
            health.SetDesignCapacity(reported, new(1000m, CapacityUnit.MilliampHours, CapacityBasis.BatteryPack, "Synthetic reported-field design"));
            health.Observe([reported], clock.GetUtcNow());
            Require(health.GetDetails(reported) is { HealthPercentage: 40m,
                LatestMeasurement.Method: CapacityMeasurementMethod.DeviceReported },
                "An explicitly supported FULL-capacity field may be accepted at a partial SOC without multiplying by that SOC.");
            var remaining = CapacitySnapshot("remaining", clock.GetUtcNow(), reported: 400m, kind: CapacityFieldKind.RemainingCapacity);
            health.SetDesignCapacity(remaining, new(1000m, CapacityUnit.MilliampHours, CapacityBasis.BatteryPack, "Synthetic remaining-field design"));
            health.Observe([remaining], clock.GetUtcNow());
            Require(health.GetDetails(remaining) is { LatestMeasurement: null, HealthPercentage: null },
                "Remaining capacity is not a usable full-charge-capacity measurement.");

            foreach (var interruption in new[] { "gap", "charging", "reconnection", "identity", "missing", "sequence" })
            {
                var start = CapacitySnapshot(interruption, clock.GetUtcNow(), current: 100m, full: true, sequence: 0);
                health.Observe([start], clock.GetUtcNow());
                clock.Advance(TimeSpan.FromMinutes(interruption == "gap" ? 3 : 1));
                var end = CapacitySnapshot(interruption, clock.GetUtcNow(), current: 100m, empty: true, sequence: 1);
                end = interruption switch
                {
                    "charging" => end with { Battery = BatteryReading.Current(42, ChargingState.Charging, clock.GetUtcNow()) },
                    "reconnection" => end with { CapacityTelemetry = end.CapacityTelemetry! with { ConnectionSession = "synthetic:new-session" } },
                    "identity" => end with { CapacityTelemetry = end.CapacityTelemetry! with { IdentityVerified = false } },
                    "missing" => end with { CapacityTelemetry = null },
                    "sequence" => end with { CapacityTelemetry = end.CapacityTelemetry! with { SampleSequence = 2 } },
                    _ => end
                };
                health.Observe([end], clock.GetUtcNow());
                var rejected = health.GetDetails(end);
                Require(rejected.HealthPercentage is null && rejected.LatestMeasurement is null &&
                    rejected.History.Any(item => !item.Complete && item.FullChargeCapacity is null && !string.IsNullOrWhiteSpace(item.Interruption)),
                    $"A {interruption} interruption must reject the incomplete usable-capacity measurement.");
            }
        }

        // Complete history is loaded by a genuinely new service instance. A copied
        // pending marker models interrupted process termination without racing writes.
        current = current with { Battery = BatteryReading.Current(42, ChargingState.Discharging, clock.GetUtcNow()),
            CapacityTelemetry = current.CapacityTelemetry! with { SampledAt = clock.GetUtcNow() } };
        using (var reopened = new CapacityHealthService(path, clock))
            Require(reopened.GetDetails(current).HealthPercentage is { } retained && Math.Abs(retained - 50m) < .0001m,
                "An accepted capacity measurement and design source must survive application restart.");
        var pendingPath = Path.Combine(output, "synthetic-capacity-pending.json");
        var crashPath = Path.Combine(output, "synthetic-capacity-restarted.json");
        var pending = CapacitySnapshot("restart", clock.GetUtcNow(), current: 100m, full: true);
        using (var started = new CapacityHealthService(pendingPath, clock))
        {
            started.Observe([pending], clock.GetUtcNow());
            File.Copy(pendingPath, crashPath, overwrite: true);
        }
        using (var restarted = new CapacityHealthService(crashPath, clock))
        {
            var result = restarted.GetDetails(pending);
            Require(result.LatestMeasurement is null && result.HealthPercentage is null &&
                result.History.Any(item => !item.Complete && item.Interruption?.Contains("restart", StringComparison.OrdinalIgnoreCase) == true),
                "An incomplete cycle must be invalidated across process restart rather than resumed across missing telemetry.");
        }
        return new(path, current, clock);
    }

    private static DeviceSnapshot CapacitySnapshot(string id, DateTimeOffset now, decimal? current = null,
        decimal? power = null, bool full = false, bool empty = false, decimal? reported = null,
        CapacityFieldKind kind = CapacityFieldKind.None, long? sequence = null) =>
        new(new($"capacity:{id}", "Synthetic capacity fixture", DeviceVendor.Razer, $"capacity:receiver:{id}"),
            BatteryReading.Current(42, ChargingState.Discharging, now), ConnectionState.Connected,
            new($"synthetic-battery:{id}", $"synthetic-session:{id}", now, true,
                "Synthetic measured telemetry; not evidence of real-device support", DischargeCurrentMilliamps: current,
                DischargePowerWatts: power, ReportedCapacity: reported,
                ReportedCapacityUnit: reported is null ? null : CapacityUnit.MilliampHours,
                ReportedCapacityKind: kind, FullBoundary: full, EmptyBoundary: empty,
                MaximumSampleGap: TimeSpan.FromSeconds(90), SampleSequence: sequence));

    private static async Task<object> VerifyCapacityDetailsAsync(CapacitySmokeFixture fixture, DeviceSnapshot unavailable,
        string output, List<Window> windows)
    {
        using var health = new CapacityHealthService(fixture.StorePath, fixture.Clock);
        var failures = new List<Exception>();
        var details = new DeviceDetailsWindow(fixture.Snapshot, health, failures.Add);
        windows.Add(details);
        details.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(Control<TextBlock>(details, "DesignCapacityText").Text.Contains("1000 mAh", StringComparison.Ordinal) &&
            Control<TextBlock>(details, "FullChargeCapacityText").Text.Contains("500 mAh", StringComparison.Ordinal) &&
            Control<TextBlock>(details, "HealthPercentageText").Text == "50%" &&
            Control<TextBlock>(details, "MeasurementDateText").Text.Contains("2026", StringComparison.Ordinal),
            "The details view omitted design capacity, measured full capacity, independently calculated health or measurement date.");
        SaveWindow(details, Path.Combine(output, "capacity-details.png"));
        details.Close();

        var unsupported = new DeviceDetailsWindow(unavailable, health, failures.Add);
        windows.Add(unsupported);
        unsupported.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var detailsBefore = unsupported.PresentationUpdateCount;
        for (var index = 0; index < 200; index++) unsupported.Update(unavailable);
        var detailsAfter = unsupported.PresentationUpdateCount;
        Require(detailsAfter == detailsBefore, "Unchanged unavailable-capacity details rebuilt their presentation.");
        Require(Control<TextBlock>(unsupported, "CapacityStatusText").Text == "Capacity measurement unavailable" &&
            Control<TextBlock>(unsupported, "ExplanationText").Text.Contains("current/power", StringComparison.OrdinalIgnoreCase),
            "Unsupported telemetry must explain the missing capacity/current/power/boundary/identity information.");
        Control<TextBox>(unsupported, "DesignValueBox").Text = "600";
        Control<ComboBox>(unsupported, "DesignUnitBox").SelectedIndex = 0;
        Control<ComboBox>(unsupported, "DesignBasisBox").SelectedIndex = 0;
        Control<TextBox>(unsupported, "DesignSourceBox").Text = "Synthetic user-entered battery specification";
        Click(Control<Button>(unsupported, "SaveDesignButton"));
        Require(health.GetDetails(unavailable).Design is { Value: 600m, Unit: CapacityUnit.MilliampHours,
                SourceKind: CapacitySourceKind.UserEntered } &&
            Control<TextBlock>(unsupported, "HealthPercentageText").Text == "Unavailable" && failures.Count == 0,
            "User design entry must retain source/units without inventing a capacity or health result.");
        SaveWindow(unsupported, Path.Combine(output, "capacity-unavailable.png"));
        unsupported.Close();
        using var reloaded = new CapacityHealthService(fixture.StorePath, fixture.Clock);
        Require(reloaded.GetDetails(unavailable).Design is { Value: 600m, SourceKind: CapacitySourceKind.UserEntered },
            "The user-entered capacity specification was not durably retained.");
        return new { unchangedIterations = 200, updatesBefore = detailsBefore,
            updatesAfterUnchanged = detailsAfter, updatesAfterDesignSave = unsupported.PresentationUpdateCount };
    }

    private static async Task<object> VerifyChangedPresentationAsync(IReadOnlyList<DeviceSnapshot> devices,
        AppSettings settings, AppSettings separate, DateTimeOffset now, List<Window> windows)
    {
        var popup = new PopupWindow();
        windows.Add(popup);
        popup.SetDevices(devices, settings, now, "Synthetic unchanged presentation");
        popup.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var source = Control<ItemsControl>(popup, "DeviceList").ItemsSource;
        var popupBefore = popup.PresentationUpdateCount;
        using var manager = CreateTrayManager();
        manager.Update(devices, separate, now);
        manager.UpdateDpi(96);
        var trayBefore = manager.Metrics;
        for (var index = 0; index < 200; index++)
        {
            popup.SetDevices(devices, settings, now, "Synthetic unchanged presentation");
            manager.Update(devices, separate, now);
        }
        var unchanged = manager.Metrics;
        Require(popup.PresentationUpdateCount == popupBefore && ReferenceEquals(source, Control<ItemsControl>(popup, "DeviceList").ItemsSource),
            "Unchanged popup content unnecessarily replaced its displayed rows.");
        Require(unchanged.IconRenders == trayBefore.IconRenders && unchanged.IconAssignments == trayBefore.IconAssignments &&
            unchanged.TooltipAssignments == trayBefore.TooltipAssignments && unchanged.MenuCreations == trayBefore.MenuCreations &&
            unchanged.SelectionReconciliations == trayBefore.SelectionReconciliations && unchanged.DpiProbes <= trayBefore.DpiProbes + 1,
            "Unchanged tray content regenerated icons, menus or selections, or repeatedly probed DPI.");
        var alias = separate with { CustomNames = new Dictionary<string, string> { [devices[0].Device.Id] = "Renamed synthetic mouse" } };
        manager.Update(devices, alias, now);
        var renamed = manager.Metrics;
        Require(renamed.TooltipAssignments > unchanged.TooltipAssignments && renamed.IconRenders == unchanged.IconRenders,
            "An alias-only change must update tooltip text without re-rendering an unchanged percentage icon.");
        var changed = devices.Select((device, index) => index == 0 ? device with
            { Battery = BatteryReading.Current(5, ChargingState.Discharging, now) } : device).ToArray();
        manager.Update(changed, alias, now);
        var changedMetrics = manager.Metrics;
        Require(changedMetrics.IconRenders > renamed.IconRenders, "A changed percentage did not regenerate its displayed icon.");
        popup.SetDevices(changed, alias, now.AddMinutes(3), "Synthetic stale transition");
        manager.Update(changed, alias, now.AddMinutes(3));
        var stale = manager.Metrics;
        Require(stale.IconRenders > changedMetrics.IconRenders &&
            Control<ItemsControl>(popup, "DeviceList").Items.Cast<DeviceRowViewModel>().First().PercentageText.Contains("stale", StringComparison.OrdinalIgnoreCase),
            "An independent freshness transition must invalidate cached current presentation.");
        manager.UpdateDpi(144);
        var dpiChanged = manager.Metrics;
        Require(dpiChanged.IconRenders > stale.IconRenders, "A DPI change did not regenerate native icon dimensions.");
        popup.CloseForShutdown();

        var events = 0;
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var watcher = new DeviceChangeWatcher(receiveNativeNotifications: false))
        {
            watcher.Changed += () => { events++; flushed.TrySetResult(); };
            for (var index = 0; index < 20; index++) watcher.QueueChange();
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(events == 1, "Synthetic device-change requests were not coalesced into one refresh signal.");
            watcher.QueueChange();
            watcher.Dispose();
            await Task.Delay(450);
            Require(events == 1, "A disposed device-change watcher dispatched a queued callback.");
        }
        return new { unchangedIterations = 200, popupUpdatesBefore = popupBefore,
            popupUpdatesAfterUnchanged = popupBefore, trayBefore, afterUnchanged = unchanged,
            afterAlias = renamed, afterPercentage = changedMetrics, afterFreshness = stale, afterDpi = dpiChanged,
            syntheticDeviceChangeRequests = 20, coalescedDeviceChangeCallbacks = events,
            baselineNote = "The previous release already cached icon bitmaps and menus. Zero redundant renders/menu creations verifies preservation; selection and shared-DPI probe counts exercise the new reductions.",
            limitations = "Native property-assignment counters do not trace Shell_NotifyIcon syscalls. Actual USB events and physical mixed-DPI behavior were not exercised." };
    }

    private sealed class SmokeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan span) { now += span; timestamp += span.Ticks; }
    }

    private static T Control<T>(FrameworkElement parent, string name) where T : class =>
        parent.FindName(name) as T ?? throw new InvalidOperationException($"Expected {name} control was not found.");

    private static void Click(Button button)
    {
        Require(button.IsEnabled, "The expected settings action was disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = window.Content as FrameworkElement ?? throw new InvalidOperationException("Window content is unavailable.");
        Require(content.ActualWidth > 0 && content.ActualHeight > 0, "The window did not complete WPF layout.");
        // Render the live root, which has no parent layout offset. Rendering or
        // brushing its parented content applies the content's margin offset again
        // and can clip the bottom/right even when the bitmap includes that margin.
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        var captureBounds = new Rect(0, 0, width, height);
        Require(width > 0 && height > 0 && captureBounds.Contains(
            content.TransformToAncestor(window).TransformBounds(new Rect(content.RenderSize))),
            "The capture canvas must contain the complete window content.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        if (window is SettingsWindow)
        {
            foreach (var label in new[] { "Save", "Cancel" })
                AssertCaptured(Descendants<Button>(window).Single(button => button.Content is string text && text == label), label);
        }
        else if (window is UpdateWindow)
        {
            AssertCaptured(Descendants<TextBlock>(window).Single(text => text.Text.StartsWith(
                "Install closes this app", StringComparison.Ordinal)), "update installation disclosure");
        }
        else if (window is DeviceDetailsWindow)
        {
            foreach (var name in new[] { "DesignCapacityText", "FullChargeCapacityText", "HealthPercentageText", "MeasurementDateText" })
                AssertCaptured(Control<TextBlock>(window, name), name);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);

        void AssertCaptured(FrameworkElement element, string description)
        {
            var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            Require(element.IsVisible && bounds.Width > 0 && bounds.Height > 0 && captureBounds.Contains(bounds),
                $"The {description} footer must fit completely inside the captured window.");
            var x = (int)Math.Floor(bounds.X);
            var y = (int)Math.Floor(bounds.Y);
            var pixelWidth = (int)Math.Ceiling(bounds.Right) - x;
            var pixelHeight = (int)Math.Ceiling(bounds.Bottom) - y;
            var stride = pixelWidth * 4;
            var pixels = new byte[stride * pixelHeight];
            bitmap.CopyPixels(new Int32Rect(x, y, pixelWidth, pixelHeight), pixels, stride, 0);
            var foregroundFound = false;
            for (var index = 0; index < pixels.Length; index += 4)
                if (pixels[index + 3] > 0 && (pixels[index] > 100 || pixels[index + 1] > 100 || pixels[index + 2] > 100))
                {
                    foregroundFound = true;
                    break;
                }
            Require(foregroundFound, $"The captured {description} footer must contain rendered foreground pixels.");
        }
    }

    private static void CheckGlyphCentered(Drawing.Bitmap bitmap, bool lightGlyph)
    {
        var count = 0;
        double sumX = 0, sumY = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.A < 160 || (lightGlyph
                ? color.R < 180 || color.G < 180 || color.B < 180
                : color.R > 60 || color.G > 60 || color.B > 60)) continue;
            count++; sumX += x; sumY += y;
        }
        Require(count > 5, "The tray icon glyph was empty.");
        Require(sumX / count > bitmap.Width * 0.33 && sumX / count < bitmap.Width * 0.67 &&
            sumY / count > bitmap.Height * 0.33 && sumY / count < bitmap.Height * 0.67,
            "The tray glyph's ink was not centered within the circular background.");
    }

    private static async Task SettleResourcesAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static (int Gdi, int User) ReadGuiResources()
    {
        using var process = Process.GetCurrentProcess();
        var gdi = (int)GetGuiResources(process.Handle, 0);
        var user = (int)GetGuiResources(process.Handle, 1);
        Require(gdi > 0 && user > 0, "Windows did not return GUI ownership counters for the live resource probe.");
        return (gdi, user);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("The isolated update-window smoke must never issue an HTTP request.");
        }
    }

    private sealed class GuiResourceProbe : IDisposable
    {
        private readonly Drawing.Icon icon = TrayIconRenderer.Render(16, 16, null, BatteryColor.Neutral);
        private readonly nint dc = CreateCompatibleDC(nint.Zero);
        public GuiResourceProbe()
        {
            if (dc != nint.Zero) return;
            icon.Dispose();
            throw new InvalidOperationException("The smoke GUI counter probe could not allocate a device context.");
        }
        public void Dispose()
        {
            DeleteDC(dc);
            icon.Dispose();
        }
    }

    private static int SimulateTaskbarCreated()
    {
        uint message = RegisterWindowMessageW("TaskbarCreated");
        Require(message != 0, "The shell recovery message could not be registered.");
        var targets = new List<nint>();
        var currentProcessId = (uint)Environment.ProcessId;
        EnumWindowsProc callback = (window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var name = new StringBuilder(256);
            GetClassNameW(window, name, name.Capacity);
            if (owner == currentProcessId && name.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
                targets.Add(window);
            return true;
        };
        EnumThreadWindows(GetCurrentThreadId(), callback, nint.Zero);
        GC.KeepAlive(callback);
        foreach (var window in targets)
            Require(SendMessageTimeoutW(window, message, nint.Zero, nint.Zero, 2, 1000, out _) != nint.Zero,
                "A process-owned tray window did not handle the simulated shell recovery message.");
        return targets.Count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate bool EnumWindowsProc(nint window, nint data);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint window,
        uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
