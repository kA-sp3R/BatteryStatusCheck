using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DeviceBatteryStatus.App.Tray;
using DeviceBatteryStatus.Core.Diagnostics;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
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
    private readonly HashSet<Task> manualOperations = [];
    private readonly FileSettingsStore store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryStatus", "settings.json"));
    private readonly StartupRegistration startup = new();
    private readonly NotificationPolicy notifications = new();
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
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.1.0";
    private readonly PeriodicTimer pollTimer;
    private AppSettings settings;
    private IReadOnlyList<DeviceSnapshot> snapshots = [];
    private SettingsWindow? settingsWindow;
    private PollingWindow? pollingWindow;
    private Task? polling;
    private Task? activeRefresh;
    private DateTimeOffset? invalidBefore;
    private DateTimeOffset? lastRefresh;
    private string? warning;
    private string? settingsWarning;
    private string? displayError;
    private bool refreshing;
    private volatile bool stopping;
    private bool cleanedUp;

    public ApplicationController(Application application)
    {
        this.application = application;
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryStatus");
        diagnostics = new(Path.Combine(dataDirectory, "logs"), CurrentVersion);
        updates = new(Path.Combine(dataDirectory, "updates"));
        var discovery = new DiagnosticHidDiscovery(new WindowsHidDiscovery(), diagnostics);
        var transports = new DiagnosticHidTransportFactory(new WindowsHidTransportFactory(), diagnostics);
        var hidOperations = new HidOperationGate();
        monitor = new(discovery, transports,
            [new RazerBatteryProvider(), new LogitechBatteryProvider(), new AlienwareBatteryProvider()], hidOperations);
        pollingRates = new(discovery, transports,
            [new RazerPollingRateController(), new LogitechPollingRateController()], hidOperations);
        var loaded = store.Load();
        settings = loaded.Settings;
        settingsWarning = loaded.Error;
        if (loaded.Error is not null) diagnostics.Record("settings", "Preferences could not be loaded; defaults are in use. Detailed errors are displayed locally, not logged.");
        pollTimer = new(TimeSpan.FromSeconds(settings.RefreshIntervalSeconds));
        tray = new(TogglePopup, ShowSettings, RefreshFromMenu, ExitFromMenu);
        popup.RefreshRequested += (_, _) => RefreshFromMenu();
        popup.SettingsRequested += (_, _) => ShowSettings();
        popup.ExitRequested += (_, _) => ExitFromMenu();
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
        polling = PollAsync();
        if (showPopup) TogglePopup();
        updateTimer.Start();
        StartUpdateCheck();
    }

    private async Task PollAsync()
    {
        try
        {
            await RefreshAsync();
            while (await pollTimer.WaitForNextTickAsync(shutdown.Token)) await RefreshAsync();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private Task RefreshAsync()
    {
        if (stopping) return Task.CompletedTask;
        if (activeRefresh is { IsCompleted: false }) return activeRefresh;
        return activeRefresh = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        refreshing = true;
        UpdatePresentation();
        try
        {
            snapshots = await Task.Run(() => monitor.RefreshAsync(shutdown.Token), shutdown.Token);
            lastRefresh = DateTimeOffset.Now;
            warning = monitor.DiscoveryError;
            foreach (var snapshot in snapshots) diagnostics.RecordSnapshot(snapshot);
            if (warning is not null) diagnostics.Record("discovery", warning);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            diagnostics.RecordException("Battery refresh", exception);
            warning = $"Refresh failed: {exception.Message}";
            MarkCurrentReadingsStale("The latest refresh failed.");
        }
        finally
        {
            refreshing = false;
            if (!stopping) UpdatePresentation();
        }
    }

    private void UpdatePresentation(bool includeHiddenPopup = false)
    {
        if (stopping) return;
        var now = DateTimeOffset.UtcNow;
        snapshots = snapshots.Select(snapshot => snapshot.Battery.State == ReadingState.Current &&
            (!BatteryPresentation.IsFreshCurrent(snapshot, settings, now) ||
             (invalidBefore is not null && snapshot.Battery.SampledAt < invalidBefore))
                ? snapshot with { Battery = snapshot.Battery.AsStale("Waiting for a fresh reading."), Connection = ConnectionState.Unknown }
                : snapshot).ToArray();
        displayError = null;
        try { tray.Update(snapshots, settings, now); }
        catch (Exception exception) { diagnostics.RecordException("Tray display", exception); displayError = $"Tray display unavailable: {exception.Message}"; }
        foreach (var alert in notifications.Evaluate(snapshots, settings, now))
        {
            try { tray.ShowLowBattery(alert); }
            catch (Exception exception) { diagnostics.RecordException("Tray notification", exception); displayError = $"Notification could not be requested: {exception.Message}"; }
        }
        var status = warning ?? displayError ?? settingsWarning ?? (refreshing ? "Refreshing…" : lastRefresh is not null
            ? $"Updated {lastRefresh:t}" : "Waiting for devices…");
        if (popup.IsVisible || includeHiddenPopup)
        {
            try { popup.SetDevices(snapshots, settings, now, status); }
            catch (Exception exception) { diagnostics.RecordException("Popup presentation", exception); displayError = $"Popup display unavailable: {exception.Message}"; }
        }
    }

    private void MarkCurrentReadingsStale(string reason) => snapshots = snapshots.Select(snapshot =>
        snapshot.Battery.State == ReadingState.Current
            ? snapshot with { Battery = snapshot.Battery.AsStale(reason), Connection = ConnectionState.Unknown }
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

    private void ShowSettings()
    {
        if (stopping) return;
        popup.Hide();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        var enabled = settings.StartWithWindows;
        string? startupError = null;
        try { enabled = startup.IsEnabled(); }
        catch (Exception exception) { startupError = $"Windows startup could not be read: {exception.Message}"; }
        settingsWindow = new SettingsWindow(settings, snapshots, startup.CanRegister, enabled, startupError, ApplySettings, ShowPollingControls, ShowUpdates, OpenDiagnosticLogs, ExportDiagnosticLogs);
        if (settingsWarning is not null) settingsWindow.ShowNotice(settingsWarning);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
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
        pollTimer.Period = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
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
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Suspend or PowerModes.Resume) || stopping || application.Dispatcher.HasShutdownStarted) return;
        // SystemEvents runs outside the dispatcher: revoke write permission before queuing UI work.
        lock (powerGate)
        {
            if (stopping) return;
            powerCancellation.Cancel();
        }
        try
        {
            application.Dispatcher.BeginInvoke(e.Mode == PowerModes.Resume
                ? new Action(ResumeAfterSleep) : new Action(() => pollingWindow?.CancelOperations()));
        }
        catch (InvalidOperationException) when (application.Dispatcher.HasShutdownStarted) { }
    }

    private async void ResumeAfterSleep()
    {
        if (stopping) return;
        pollingWindow?.CancelOperations();
        lock (powerGate)
        {
            powerCancellation.Dispose();
            powerCancellation = new();
        }
        invalidBefore = DateTimeOffset.UtcNow;
        MarkCurrentReadingsStale("Refreshing after sleep.");
        UpdatePresentation();
        var refreshAlreadyRunning = activeRefresh is { IsCompleted: false };
        try
        {
            await RefreshAsync();
            if (refreshAlreadyRunning && !stopping) await RefreshAsync();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async void ExitFromMenu()
    {
        if (stopping) return;
        StopImmediately();
        try
        {
            var pending = manualOperations.ToList();
            if (polling is not null) pending.Add(polling);
            if (activeRefresh is not null) pending.Add(activeRefresh);
            if (updateCheck is not null) pending.Add(updateCheck);
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { diagnostics.RecordException("Pending operation during exit", exception); }
        finally
        {
            pollTimer.Dispose();
            shutdown.Dispose();
            lock (powerGate) powerCancellation.Dispose();
            popup.CloseForShutdown();
            settingsWindow?.Close();
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
        Cleanup("Stop presentation timer", presentationTimer.Stop);
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
