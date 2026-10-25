using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DeviceBatteryStatus.App.Alerts;
using DeviceBatteryStatus.App.Tray;
using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Diagnostics;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
using DeviceBatteryStatus.Core.Runtime;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Updates;
using DeviceBatteryStatus.Windows.Hardware;
using DeviceBatteryStatus.Windows.Integration;
using Microsoft.Win32;

namespace DeviceBatteryStatus.App;

/// <summary>Owns the polling loop, optional device tray icons, diagnostics, updates and UI state on the WPF dispatcher.</summary>
internal sealed class ApplicationController
{
    private readonly Application application;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object powerGate = new();
    private CancellationTokenSource powerCancellation = new();
    private readonly BatteryMonitor monitor;
    private readonly PollingRateService pollingRates;
    private readonly CapacityHealthService health;
    private readonly Dictionary<string, DeviceDetailsWindow> detailsWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceSnapshot> loggedSnapshots = new(StringComparer.Ordinal);
    private DeviceChangeWatcher? deviceChanges;
    private readonly HashSet<Task> manualOperations = [];
    private readonly FileSettingsStore store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryStatus", "settings.json"));
    private readonly StartupRegistration startup = new();
    private readonly RuntimeEstimator runtime = new();
    private readonly GamingAlertPolicy gamingAlerts = new();
    private readonly GamingAlertPresenter alertPresenter;
    private IReadOnlyDictionary<string, RuntimeEstimate> runtimeEstimates = new Dictionary<string, RuntimeEstimate>();
    private readonly PopupWindow popup = new();
    private readonly DispatcherTimer presentationTimer;
    private readonly TrayIconManager tray;
    private readonly DiagnosticLog diagnostics;
    private readonly GitHubUpdateService updates;
    private readonly DispatcherTimer updateTimer;
    private CancellationTokenSource updateCancellation = new();
    private Task? updateCheck;
    private AvailableUpdate? availableUpdate;
    private string? notifiedUpdateVersion;
    private UpdateWindow? updateWindow;
    internal static string CurrentVersion => typeof(ApplicationController).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.2.0";
    private readonly DispatcherTimer pollTimer;
    private AppSettings settings;
    private IReadOnlyList<DeviceSnapshot> snapshots = [];
    private SettingsWindow? settingsWindow;
    private AlertSettingsWindow? alertSettingsWindow;
    private PollingWindow? pollingWindow;
    private Task? activeRefresh;
    private DateTimeOffset? invalidBefore;
    private DateTimeOffset? lastRefresh;
    private string? warning;
    private string? settingsWarning;
    private string? displayError;
    private bool refreshing;
    private bool refreshQueued;
    private bool forceQueued;
    private volatile bool suspended;
    private long powerEventGeneration;
    private volatile bool stopping;
    private bool cleanedUp;

    public ApplicationController(Application application)
    {
        this.application = application;
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryStatus");
        diagnostics = new(Path.Combine(dataDirectory, "logs"), CurrentVersion);
        alertPresenter = new(reportError: exception => diagnostics.RecordException("Gaming alert display", exception));
        alertPresenter.Shown += (alert, at) => gamingAlerts.Acknowledge(alert, at);
        updates = new(Path.Combine(dataDirectory, "updates"));
        health = new(Path.Combine(dataDirectory, "capacity-health.json"));
        var discovery = new DiagnosticHidDiscovery(new WindowsHidDiscovery(), diagnostics);
        var transports = new DiagnosticHidTransportFactory(new WindowsHidTransportFactory(), diagnostics);
        var hidOperations = new HidOperationGate();
        monitor = new(discovery, transports,
            [new RazerBatteryProvider(), new LogitechBatteryProvider(), new AlienwareBatteryProvider()], hidOperations, new BatteryMonitorOptions());
        pollingRates = new(discovery, transports,
            [new RazerPollingRateController(), new LogitechPollingRateController()], hidOperations);
        var loaded = store.Load();
        settings = loaded.Settings;
        settingsWarning = loaded.Error;
        if (loaded.Error is not null) diagnostics.Record("settings", "Preferences could not be loaded; defaults are in use. Detailed errors are displayed locally, not logged.");
        pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds) };
        pollTimer.Tick += (_, _) => RefreshFromTimer();
        tray = new(TogglePopup, ShowSettings, RefreshFromMenu, ExitFromMenu);
        popup.RefreshRequested += (_, _) => RefreshFromMenu();
        popup.SettingsRequested += (_, _) => ShowSettings();
        popup.ExitRequested += (_, _) => ExitFromMenu();
        popup.DeviceDetailsRequested += ShowDeviceDetails;
        presentationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        presentationTimer.Tick += (_, _) => UpdatePresentation();
        updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromDays(1) };
        updateTimer.Tick += (_, _) => StartUpdateCheck();
    }

    public void Start(bool showPopup)
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        presentationTimer.Start();
        UpdatePresentation();
        pollTimer.Start();
        RefreshFromTimer();
        try
        {
            deviceChanges = new DeviceChangeWatcher();
            deviceChanges.Changed += OnDevicesChanged;
            if (!deviceChanges.InterfaceNotificationsRegistered)
                diagnostics.Record("discovery", "Interface notifications unavailable; device-node notifications and bounded reconciliation remain enabled.");
        }
        catch (Exception exception) { diagnostics.RecordException("Device-change notifications", exception); }
        if (showPopup) TogglePopup();
        updateTimer.Start();
        StartUpdateCheck();
    }

    private async void RefreshFromTimer()
    {
        if (stopping || suspended) return;
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || suspended) { }
        catch (Exception exception) { diagnostics.RecordException("Scheduled refresh", exception); }
    }

    private Task RefreshAsync(bool force = false)
    {
        if (stopping || suspended) return Task.CompletedTask;
        if (activeRefresh is { IsCompleted: false })
        {
            // Scheduled ticks join current work. Manual/PnP/resume requests need
            // exactly one fresh follow-up, rather than being lost or queued forever.
            if (force) { refreshQueued = true; forceQueued = true; }
            return activeRefresh;
        }
        return activeRefresh = DrainRefreshesAsync(force);
    }

    private async Task DrainRefreshesAsync(bool force)
    {
        do
        {
            refreshQueued = false;
            forceQueued = false;
            await RefreshCoreAsync(force);
            force = forceQueued;
        } while (refreshQueued && !stopping && !suspended);
    }

    private async Task RefreshCoreAsync(bool force)
    {
        refreshing = true;
        UpdatePresentation();
        CancellationTokenSource linked;
        lock (powerGate)
            linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, powerCancellation.Token);
        using var refreshLifetime = linked;
        try
        {
            var result = await Task.Run(() => monitor.RefreshAsync(linked.Token, force), linked.Token);
            if (stopping || suspended || linked.IsCancellationRequested) return;
            snapshots = result;
            ExpireCurrentReadings(DateTimeOffset.UtcNow);
            try { health.Observe(snapshots, DateTimeOffset.UtcNow); }
            catch (Exception exception) { diagnostics.RecordException("Capacity collection", exception); }
            try { runtime.Observe(snapshots, settings, DateTimeOffset.UtcNow); }
            catch (Exception exception) { diagnostics.RecordException("Runtime estimate collection", exception); runtime.Interrupt(); }
            lastRefresh = DateTimeOffset.Now;
            warning = monitor.DiscoveryError;
            foreach (var snapshot in snapshots)
            {
                if (loggedSnapshots.TryGetValue(snapshot.Device.Id, out var logged) && logged == snapshot) continue;
                diagnostics.RecordSnapshot(snapshot);
                loggedSnapshots[snapshot.Device.Id] = snapshot;
            }
            if (warning is not null) diagnostics.Record("discovery", warning);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!stopping) MarkCurrentReadingsStale("Refresh interrupted; waiting for a new reading.");
        }
        catch (Exception exception)
        {
            diagnostics.RecordException("Battery refresh", exception);
            warning = $"Refresh failed: {exception.Message}";
            InterruptCapacity("Battery refresh failed.");
            runtime.Interrupt("Battery refresh failed.");
            MarkCurrentReadingsStale("The latest refresh failed.");
        }
        finally
        {
            refreshing = false;
            if (!stopping) UpdatePresentation();
        }
    }

    private void ExpireCurrentReadings(DateTimeOffset now)
    {
        DeviceSnapshot[]? changed = null;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (snapshot.Battery.State != ReadingState.Current ||
                (BatteryPresentation.IsFreshCurrent(snapshot, settings, now) &&
                 (invalidBefore is null || snapshot.Battery.SampledAt >= invalidBefore))) continue;
            changed ??= snapshots.ToArray();
            changed[index] = snapshot with
            {
                Battery = snapshot.Battery.AsStale("Waiting for a fresh reading."),
                Connection = ConnectionState.Unknown, CapacityTelemetry = null
            };
        }
        if (changed is not null) snapshots = changed;
    }

    private void UpdatePresentation(bool includeHiddenPopup = false)
    {
        if (stopping) return;
        var now = DateTimeOffset.UtcNow;
        try { health.CheckContinuity(now); }
        catch (Exception exception) { diagnostics.RecordException("Capacity freshness", exception); }
        ExpireCurrentReadings(now);
        var estimates = new Dictionary<string, RuntimeEstimate>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            try { estimates[snapshot.Device.Id] = runtime.Get(snapshot, settings, now); }
            catch (Exception exception) { diagnostics.RecordException("Runtime estimate presentation", exception); }
        }
        runtimeEstimates = estimates;
        displayError = null;
        try { tray.Update(snapshots, settings, now, runtimeEstimates); }
        catch (Exception exception) { diagnostics.RecordException("Tray display", exception); displayError = $"Tray display unavailable: {exception.Message}"; }
        // Gaming alerts replace legacy low-battery balloons. The independent
        // freshness tick also rechecks queued warnings, cooldown and snoozing.
        try
        {
            if (suspended) alertPresenter.Clear();
            else
            {
                foreach (var alert in gamingAlerts.Evaluate(snapshots, settings, runtimeEstimates, now))
                    alertPresenter.Queue(alert, now);
                alertPresenter.Tick(now, alert => gamingAlerts.Revalidate(alert, snapshots, settings, runtimeEstimates, now));
            }
        }
        catch (Exception exception) { diagnostics.RecordException("Gaming battery alerts", exception); displayError = $"Battery alert unavailable: {exception.Message}"; }
        foreach (var pair in detailsWindows)
        {
            if (!pair.Value.IsVisible) continue;
            var snapshot = snapshots.FirstOrDefault(value => value.Device.Id == pair.Key);
            if (snapshot is not null)
            {
                try { pair.Value.Update(snapshot); }
                catch (Exception exception) { diagnostics.RecordException("Capacity details presentation", exception); }
            }
        }
        var status = warning ?? displayError ?? settingsWarning ?? (refreshing ? "Refreshing…" : lastRefresh is not null
            ? $"Updated {lastRefresh:t}" : "Waiting for devices…");
        if (popup.IsVisible || includeHiddenPopup)
        {
            try { popup.SetDevices(snapshots, settings, now, status, runtimeEstimates); }
            catch (Exception exception) { diagnostics.RecordException("Popup presentation", exception); displayError = $"Popup display unavailable: {exception.Message}"; }
        }
    }

    private void MarkCurrentReadingsStale(string reason) => snapshots = snapshots.Select(snapshot =>
        snapshot.Battery.State == ReadingState.Current
            ? snapshot with { Battery = snapshot.Battery.AsStale(reason), Connection = ConnectionState.Unknown, CapacityTelemetry = null }
            : snapshot).ToArray();

    private void TogglePopup()
    {
        if (stopping) return;
        UpdatePresentation(includeHiddenPopup: true);
        try
        {
            popup.ToggleNearTray();
            tray.UpdateDpi(popup.CurrentDpi);
        }
        catch (Exception exception) { diagnostics.RecordException("Open popup", exception); warning = $"The popup could not be opened: {exception.Message}"; }
    }

    private void InterruptCapacity(string reason)
    {
        try { health.Interrupt(reason, DateTimeOffset.UtcNow); }
        catch (Exception exception) { diagnostics.RecordException("Capacity collection interruption", exception); }
    }

    private void ShowDeviceDetails(string deviceId)
    {
        if (stopping) return;
        if (detailsWindows.TryGetValue(deviceId, out var existing)) { existing.Activate(); return; }
        var snapshot = snapshots.FirstOrDefault(value => value.Device.Id == deviceId);
        if (snapshot is null) return;
        var window = new DeviceDetailsWindow(snapshot, health,
            exception => diagnostics.RecordException("Capacity details", exception));
        detailsWindows.Add(deviceId, window);
        window.Closed += (_, _) => detailsWindows.Remove(deviceId);
        window.Show();
    }

    private void OnDevicesChanged()
    {
        if (stopping) return;
        InterruptCapacity("Windows device topology changed; battery continuity must be established again.");
        runtime.Interrupt("Windows device topology changed; peripheral association must be established again.");
        alertPresenter.Clear();
        invalidBefore = DateTimeOffset.UtcNow;
        MarkCurrentReadingsStale("Device change detected; refreshing association.");
        if (!suspended) RefreshFromMenu();
    }

    private void ShowSettings()
    {
        if (stopping) return;
        popup.Hide();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        var enabled = settings.StartWithWindows;
        string? startupError = null;
        try { enabled = startup.IsEnabled(); }
        catch (Exception exception) { startupError = $"Windows startup could not be read: {exception.Message}"; }
        if (alertSettingsWindow is not null) { alertSettingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, snapshots, startup.CanRegister, enabled, startupError, ApplySettings, ShowPollingControls, ShowUpdates, OpenDiagnosticLogs, ExportDiagnosticLogs, ShowGamingAlerts, () => PreviewGamingAlert(settings));
        if (settingsWarning is not null) settingsWindow.ShowNotice(settingsWarning);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    private void ShowGamingAlerts()
    {
        if (stopping) return;
        popup.Hide();
        if (alertSettingsWindow is not null) { alertSettingsWindow.Activate(); return; }
        if (pollingWindow is not null || updateWindow is not null)
        {
            settingsWindow?.ShowNotice("Close the polling-rate or update window before editing gaming alerts.");
            return;
        }
        // The main preferences editor guards unsaved changes before invoking
        // this callback. Close its captured settings snapshot before saving here.
        settingsWindow?.Close();
        alertSettingsWindow = new(settings, snapshots, updated => ApplySettings(updated, false), PreviewGamingAlert);
        alertSettingsWindow.Closed += (_, _) => alertSettingsWindow = null;
        alertSettingsWindow.Show();
    }

    private void PreviewGamingAlert(AppSettings previewSettings)
    {
        if (stopping || suspended) return;
        var rule = GamingAlertSettings.FromLegacy(previewSettings).GlobalRule;
        var estimate = new RuntimeEstimate(RuntimeEstimateState.Available, "About 2 hours remaining",
            "Synthetic preview data; no physical device was queried.", TimeSpan.FromHours(2),
            TimeSpan.FromHours(1.5), TimeSpan.FromHours(2.5), true);
        var candidate = new GamingBatteryAlert("synthetic-preview", "Viper V3 Pro · synthetic preview", 15, null,
            estimate.Text, GamingAlertSeverity.Low, rule, [GamingAlertCondition.Percentage], DateTimeOffset.UtcNow)
            { Runtime = estimate, IsPreview = true };
        alertPresenter.Preview(candidate, DateTimeOffset.UtcNow);
    }

    private void ShowPollingControls()
    {
        if (stopping) return;
        if (pollingWindow is not null) { pollingWindow.Activate(); return; }
        pollingWindow = new PollingWindow(snapshots, settings,
            (device, cancellationToken) => RunManualOperationAsync(
                token => pollingRates.ReadAsync(device, token), cancellationToken),
            (device, rateHz, canApply, canSwitchToHostMode, cancellationToken) => RunManualOperationAsync(
                token => pollingRates.ApplyAsync(device, rateHz,
                    () => !token.IsCancellationRequested && canApply(), token,
                    canSwitchToHostMode is null ? null : () => !token.IsCancellationRequested && canSwitchToHostMode()), cancellationToken))
        { Owner = settingsWindow };
        pollingWindow.Closed += (_, _) => pollingWindow = null;
        pollingWindow.Show();
    }

    private async Task<T> RunManualOperationAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (stopping || suspended) throw new OperationCanceledException("Manual controls are suspended.");
        InterruptCapacity("Manual polling-rate operation interrupted capacity collection.");
        runtime.Interrupt("Manual polling-rate operation may change consumption; starting a new discharge trend.");
        CancellationTokenSource linked;
        lock (powerGate)
            linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellationToken, powerCancellation.Token);
        using var operationLifetime = linked;
        var task = Task.Run(() => operation(linked.Token), linked.Token);
        manualOperations.Add(task);
        try
        {
            var result = await task;
            var info = result is PollingRateInfo readInfo ? readInfo : result is PollingApplyResult applied ? applied.Info : null;
            if (info is not null)
                diagnostics.Record("polling", result is PollingApplyResult applyResult ? applyResult.Detail : info.Detail, new Dictionary<string, string?>
                {
                    ["state"] = info.State.ToString(), ["reportedRateHz"] = info.CurrentRateHz?.ToString(),
                    ["supportedRatesHz"] = string.Join(",", info.SupportedRatesHz)
                });
            return result;
        }
        catch (Exception exception) { diagnostics.RecordException("Manual polling operation", exception); throw; }
        finally { manualOperations.Remove(task); }
    }

    private void ApplySettings(AppSettings updated, bool changeStartup)
    {
        updated = updated.Normalize();
        var previousStartup = changeStartup && startup.IsEnabled();
        var startupChanged = changeStartup && updated.StartWithWindows != previousStartup;
        if (startupChanged) startup.SetEnabled(updated.StartWithWindows);
        try { store.Save(updated); }
        catch (Exception exception)
        {
            if (startupChanged)
            {
                try { startup.SetEnabled(previousStartup); }
                catch (Exception rollback)
                {
                    throw new IOException($"Preferences could not be saved: {exception.Message} Windows startup could not be restored: {rollback.Message}", exception);
                }
            }
            throw;
        }
        var previousUpdateMode = settings.UpdateCheckMode;
        settings = updated;
        if (previousUpdateMode != updated.UpdateCheckMode)
        {
            updateCancellation.Cancel();
            updateCancellation.Dispose();
            updateCancellation = new();
            updateCheck = null;
            availableUpdate = null;
            updateWindow?.Close();
            if (updated.UpdateCheckMode == UpdateCheckMode.NotifyOnly) StartUpdateCheck();
        }
        diagnostics.Record("settings", "Preferences applied", new Dictionary<string, string?>
        {
            ["trayMode"] = settings.TrayIconMode.ToString(), ["selectedTrayDevices"] = settings.TrayDeviceIds.Count.ToString(),
            ["updateMode"] = settings.UpdateCheckMode.ToString()
        });
        pollTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        warning = null;
        settingsWarning = null;
        UpdatePresentation();
    }

    private void OpenDiagnosticLogs()
    {
        Directory.CreateDirectory(diagnostics.DirectoryPath);
        Process.Start(new ProcessStartInfo(diagnostics.DirectoryPath) { UseShellExecute = true });
    }

    private async void ExportDiagnosticLogs()
    {
        if (stopping) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export redacted diagnostic report", Filter = "Diagnostic ZIP (*.zip)|*.zip",
            FileName = $"DeviceBatteryStatus-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip", AddExtension = true
        };
        if (dialog.ShowDialog(settingsWindow) != true) return;
        try
        {
            await Task.Run(() => diagnostics.Export(dialog.FileName));
            if (!stopping) settingsWindow?.ShowNotice("Redacted diagnostic report exported. Review it before sharing.");
        }
        catch (Exception exception)
        {
            diagnostics.RecordException("Diagnostic export", exception);
            if (!stopping) settingsWindow?.ShowNotice($"The diagnostic report could not be exported. {exception.Message}");
        }
    }

    private void StartUpdateCheck()
    {
        if (stopping || settings.UpdateCheckMode != UpdateCheckMode.NotifyOnly || updateCheck is { IsCompleted: false }) return;
        updateCheck = CheckUpdatesAutomaticallyAsync(updateCancellation.Token);
    }

    private async Task CheckUpdatesAutomaticallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await updates.CheckAsync(CurrentVersion, settings.UpdateCheckMode, manual: false, cancellationToken);
            if (stopping || cancellationToken.IsCancellationRequested || settings.UpdateCheckMode != UpdateCheckMode.NotifyOnly) return;
            diagnostics.Record("updates", result.Detail, new Dictionary<string, string?> { ["state"] = result.State.ToString() });
            availableUpdate = result.Update;
            if (availableUpdate is not null && notifiedUpdateVersion != availableUpdate.Version)
            {
                notifiedUpdateVersion = availableUpdate.Version;
                tray.ShowUpdateAvailable(availableUpdate.Version, ShowUpdates);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { diagnostics.RecordException("Automatic update check", exception); }
    }

    private void ShowUpdates()
    {
        if (stopping) return;
        popup.Hide();
        if (updateWindow is not null) { updateWindow.Activate(); return; }
        updateWindow = new(updates, CurrentVersion, () => stopping ? UpdateCheckMode.Off : settings.UpdateCheckMode,
            InstallUpdate, exception => diagnostics.RecordException("Update operation", exception), availableUpdate)
        { Owner = settingsWindow };
        updateWindow.Closed += (_, _) => updateWindow = null;
        updateWindow.Show();
    }

    private void InstallUpdate(StagedInstaller staged)
    {
        if (stopping || settings.UpdateCheckMode == UpdateCheckMode.Off) return;
        if (application is not App app) throw new InvalidOperationException("Application shutdown cannot start this installer.");
        // OnExit releases the instance mutex first. Revalidate with a file lease
        // immediately before CreateProcess; no network, shell association or automatic retry.
        app.AfterShutdown = () =>
        {
            using var verified = updates.VerifyForInstallAsync(staged, CancellationToken.None).GetAwaiter().GetResult();
            Process.Start(new ProcessStartInfo(verified.Path) { UseShellExecute = false });
        };
        ExitFromMenu();
    }

    private async void RefreshFromMenu()
    {
        try { await RefreshAsync(force: true); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || suspended) { }
        catch (Exception exception) { diagnostics.RecordException("Requested refresh", exception); }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Suspend or PowerModes.Resume) || stopping || application.Dispatcher.HasShutdownStarted) return;
        // SystemEvents runs outside the dispatcher: revoke write permission before queuing UI work.
        long generation;
        lock (powerGate)
        {
            if (stopping) return;
            generation = ++powerEventGeneration;
            suspended = true; // A resume must establish its new lifetime on the dispatcher first.
            powerCancellation.Cancel();
        }
        try
        {
            application.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Mode == PowerModes.Resume) ResumeAfterSleep(generation);
                else SuspendForSleep(generation);
            }));
        }
        catch (InvalidOperationException) when (application.Dispatcher.HasShutdownStarted) { }
    }

    private void SuspendForSleep(long generation)
    {
        lock (powerGate)
        {
            if (stopping || generation != powerEventGeneration) return;
            suspended = true;
            powerCancellation.Cancel();
        }
        pollTimer.Stop();
        presentationTimer.Stop();
        pollingWindow?.CancelOperations();
        InterruptCapacity("System sleep interrupted capacity collection.");
        runtime.Interrupt("System sleep interrupted discharge observations.");
        alertPresenter.Clear();
        invalidBefore = DateTimeOffset.UtcNow;
        MarkCurrentReadingsStale("System sleeping; waiting for a new reading.");
        UpdatePresentation();
    }

    private async void ResumeAfterSleep(long generation)
    {
        if (stopping) return;
        lock (powerGate)
        {
            if (stopping || generation != powerEventGeneration) return;
            powerCancellation.Dispose();
            powerCancellation = new();
            suspended = false;
        }
        pollingWindow?.CancelOperations();
        invalidBefore = DateTimeOffset.UtcNow;
        MarkCurrentReadingsStale("Refreshing after sleep.");
        InterruptCapacity("System resumed; a new complete capacity cycle is required.");
        runtime.Interrupt("System resumed; starting a new observed discharge trend.");
        presentationTimer.Start();
        pollTimer.Start();
        UpdatePresentation();
        try { await RefreshAsync(force: true); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || suspended) { }
        catch (Exception exception) { diagnostics.RecordException("Resume refresh", exception); }
    }

    private async void ExitFromMenu()
    {
        if (stopping) return;
        StopImmediately();
        try
        {
            var pending = manualOperations.ToList();
            if (activeRefresh is not null) pending.Add(activeRefresh);
            if (updateCheck is not null) pending.Add(updateCheck);
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { diagnostics.RecordException("Pending operation during exit", exception); }
        finally
        {
            shutdown.Dispose();
            lock (powerGate) powerCancellation.Dispose();
            popup.CloseForShutdown();
            settingsWindow?.Close();
            alertSettingsWindow?.Close();
            application.Shutdown();
        }
    }

    public void StopImmediately()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        stopping = true;
        void Cleanup(string operation, Action action)
        {
            try { action(); }
            catch (Exception exception) { diagnostics.RecordException(operation, exception); }
        }
        Cleanup("Cancel application operations", shutdown.Cancel);
        Cleanup("Cancel manual polling", () => pollingWindow?.CancelOperations());
        Cleanup("Stop battery timer", pollTimer.Stop);
        Cleanup("Stop presentation timer", presentationTimer.Stop);
        Cleanup("Dispose gaming alerts", alertPresenter.Dispose);
        Cleanup("Close alert preferences", () => alertSettingsWindow?.Close());
        Cleanup("Interrupt runtime estimates", () => runtime.Interrupt("Application stopped."));
        Cleanup("Dispose device notifications", () => deviceChanges?.Dispose());
        Cleanup("Interrupt capacity collection", () => health.Interrupt("Application stopped before a complete cycle.", DateTimeOffset.UtcNow));
        Cleanup("Close device details", () => { foreach (var window in detailsWindows.Values.ToArray()) window.Close(); });
        Cleanup("Dispose capacity history", health.Dispose);
        Cleanup("Stop update timer", updateTimer.Stop);
        Cleanup("Cancel update operations", updateCancellation.Cancel);
        Cleanup("Close update window", () => updateWindow?.Close());
        Cleanup("Dispose update HTTP service", updates.Dispose);
        diagnostics.Record("application", "Application stopping");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Cleanup("Hide popup", popup.Hide);
        Cleanup("Dispose tray icons", tray.Dispose);
        diagnostics.Dispose();
    }
}
