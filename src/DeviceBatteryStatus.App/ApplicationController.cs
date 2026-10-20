using System.Windows;
using System.Windows.Threading;
using DeviceBatteryStatus.App.Tray;
using DeviceBatteryStatus.Core.Hardware;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Protocols;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Windows.Hardware;
using DeviceBatteryStatus.Windows.Integration;
using Microsoft.Win32;

namespace DeviceBatteryStatus.App;

/// <summary>Owns one polling loop, one tray icon, and UI state on the WPF dispatcher.</summary>
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
    private readonly TrayIconService tray;
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
        var discovery = new WindowsHidDiscovery();
        var transports = new WindowsHidTransportFactory();
        var hidOperations = new HidOperationGate();
        monitor = new(discovery, transports,
            [new RazerBatteryProvider(), new LogitechBatteryProvider(), new AlienwareBatteryProvider()], hidOperations);
        pollingRates = new(discovery, transports,
            [new RazerPollingRateController(), new LogitechPollingRateController()], hidOperations);
        var loaded = store.Load();
        settings = loaded.Settings;
        settingsWarning = loaded.Error;
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
    }

    public void Start(bool showPopup)
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        presentationTimer.Start();
        UpdatePresentation();
        polling = PollAsync();
        if (showPopup) TogglePopup();
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
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
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
        try { tray.Update(BatteryPresentation.CreateTray(snapshots, settings, now)); }
        catch (Exception exception) { displayError = $"Tray display unavailable: {exception.Message}"; }
        foreach (var alert in notifications.Evaluate(snapshots, settings, now))
        {
            try { tray.ShowLowBattery(alert); }
            catch (Exception exception) { displayError = $"Notification could not be requested: {exception.Message}"; }
        }
        var status = warning ?? displayError ?? settingsWarning ?? (refreshing ? "Refreshing…" : lastRefresh is not null
            ? $"Updated {lastRefresh:t}" : "Waiting for devices…");
        if (popup.IsVisible || includeHiddenPopup)
        {
            try { popup.SetDevices(snapshots, settings, now, status); }
            catch (Exception exception) { displayError = $"Popup display unavailable: {exception.Message}"; }
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
        catch (Exception exception) { warning = $"The popup could not be opened: {exception.Message}"; }
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
        settingsWindow = new SettingsWindow(settings, snapshots, startup.CanRegister, enabled, startupError, ApplySettings, ShowPollingControls);
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
            (device, rateHz, canApply, cancellationToken) => RunManualOperationAsync(
                token => pollingRates.ApplyAsync(device, rateHz,
                    () => !token.IsCancellationRequested && canApply(), token), cancellationToken))
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
        try { return await task; }
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
        settings = updated;
        pollTimer.Period = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        warning = null;
        settingsWarning = null;
        UpdatePresentation();
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
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) { }
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
        shutdown.Cancel();
        pollingWindow?.CancelOperations();
        presentationTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        popup.Hide();
        tray.Dispose();
    }
}
