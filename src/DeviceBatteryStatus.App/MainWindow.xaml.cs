using System.ComponentModel;
using System.Windows;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Monitoring;
using DeviceBatteryStatus.Core.Protocols;
using DeviceBatteryStatus.Windows.Hardware;

namespace DeviceBatteryStatus.App;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly BatteryMonitor monitor = new(new WindowsHidDiscovery(), new WindowsHidTransportFactory(),
        [new RazerBatteryProvider(), new LogitechBatteryProvider()]);
    private Task? polling;
    private Task? activeRefresh;
    private bool closing;
    private bool allowClose;

    public MainWindow()
    {
        InitializeComponent();
        SupportText.Text = $"Hardware compatibility is unverified. Razer link state is unknown; a fresh control reply may contain firmware-cached data. {AlienwareSupport.Limitation} Tray and settings arrive in Milestone 2.";
        Loaded += (_, _) => polling = PollAsync();
        Closing += OnClosing;
    }

    private async Task PollAsync()
    {
        try
        {
            await RefreshAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(shutdown.Token)) await RefreshAsync();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private Task RefreshAsync()
    {
        if (closing) return Task.CompletedTask;
        if (activeRefresh is { IsCompleted: false }) return activeRefresh;
        return activeRefresh = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        if (!RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Refreshing receiver and peripheral information…";
        try
        {
            // Discovery and native I/O stay off the WPF dispatcher.
            var snapshots = await Task.Run(() => monitor.RefreshAsync(shutdown.Token), shutdown.Token);
            DeviceGrid.ItemsSource = snapshots.Select(x => new DeviceRow(x)).ToArray();
            StatusText.Text = monitor.DiscoveryError ?? (snapshots.Count == 0
                ? "No implemented receiver endpoint found. This does not establish that no wireless devices are attached."
                : $"Last refresh: {DateTimeOffset.Now:t}. Stale values retain their original sample time; unavailable readings have no percentage.");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            StatusText.Text = $"Refresh failed: {exception.Message}";
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        IsEnabled = false;
        shutdown.Cancel();
        try
        {
            if (polling is not null) await polling;
            if (activeRefresh is not null) await activeRefresh;
        }
        catch (OperationCanceledException) { }
        finally
        {
            shutdown.Dispose();
            allowClose = true;
            Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private sealed class DeviceRow(DeviceSnapshot snapshot)
    {
        public string Name => snapshot.Device.Name;
        public string Percentage => snapshot.Battery.Percentage is int value
            ? $"{value}%{(snapshot.Battery.State == ReadingState.Stale ? "*" : "")}" : "—";
        public string State => snapshot.Battery.State.ToString();
        public string Charging => snapshot.Battery.Charging.ToString();
        public string Connection => snapshot.Connection.ToString();
        public string SampleTime => snapshot.Battery.SampledAt?.ToLocalTime().ToString("g") ?? "—";
        public string Detail => snapshot.Battery.Detail ?? (snapshot.Connection == ConnectionState.Unknown
            ? "Measured reply received; peripheral radio-link state is unknown." : "");
    }
}
