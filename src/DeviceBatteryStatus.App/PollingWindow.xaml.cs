using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Polling;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.App;

public partial class PollingWindow : Window
{
    private readonly Func<PeripheralIdentity, CancellationToken, Task<PollingRateInfo>> read;
    private readonly Func<PeripheralIdentity, int, Func<bool>, CancellationToken, Task<PollingApplyResult>> apply;
    private readonly object operationGate = new();
    private CancellationTokenSource? activeOperation;
    private PollingRateInfo? detected;
    private int generation;
    private int enabled;
    private int closed;
    private bool busy;
    private bool initializing = true;

    public PollingWindow(IReadOnlyList<DeviceSnapshot> devices, AppSettings settings,
        Func<PeripheralIdentity, CancellationToken, Task<PollingRateInfo>> read,
        Func<PeripheralIdentity, int, Func<bool>, CancellationToken, Task<PollingApplyResult>> apply)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
        InitializeComponent();
        var choices = devices.Where(device => device.Device.Vendor is DeviceVendor.Razer or DeviceVendor.Logitech)
            .GroupBy(device => device.Device.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(device => new DeviceChoice(device.Device,
                BatteryPresentation.GetDisplayName(device.Device, settings)))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        DeviceBox.ItemsSource = choices;
        if (choices.Length > 0) DeviceBox.SelectedIndex = 0;
        initializing = false;
        DetailText.Text = choices.Length == 0
            ? "No Razer or Logitech peripheral is available. Reopen this window after a device is detected."
            : "Enable control, then click Detect to read the selected mouse's capabilities.";
        Closing += OnClosing;
        UpdateControls();
    }

    private void Enable_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        InvalidateOperation();
        Volatile.Write(ref enabled, EnableBox.IsChecked == true ? 1 : 0);
        ResetCapabilities(EnableBox.IsChecked == true
            ? "Click Detect to read the current configuration and rate options."
            : "Polling-rate control is disabled.");
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        InvalidateOperation();
        ResetCapabilities(Volatile.Read(ref enabled) == 1
            ? "Selection changed. Click Detect for this mouse."
            : "Enable control, then click Detect for this mouse.");
    }

    private void Rate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing) UpdateControls(); // Selecting a rate never sends a command.
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Volatile.Read(ref enabled) != 1 || DeviceBox.SelectedItem is not DeviceChoice selected) return;
        detected = null;
        RateBox.ItemsSource = null;
        CurrentText.Text = "Detecting current rate…";
        DetailText.Text = "Reading current configuration. No polling-rate change is being sent.";
        var (operation, version) = BeginOperation();
        try
        {
            var info = await read(selected.Identity, operation.Token);
            if (!IsCurrent(version, operation.Token)) return;
            if (!string.Equals(info.DeviceId, selected.Identity.Id, StringComparison.Ordinal))
                throw new InvalidDataException("The polling-rate response belongs to a different device.");
            ShowReportedRate(info);
            var rates = info.SupportedRatesHz.Where(rate => rate > 0).Distinct().Order().ToArray();
            if (info.State == PollingRateState.Supported && info.CurrentRateHz is int current && rates.Contains(current))
            {
                detected = info with { SupportedRatesHz = Array.AsReadOnly(rates) };
                var choices = rates.Select(rate => new RateChoice(rate)).ToArray();
                RateBox.ItemsSource = choices;
                RateBox.SelectedItem = choices.First(rate => rate.Hertz == current);
            }
            else if (info.State == PollingRateState.Supported)
                DetailText.Text = "Supported rates could not be confirmed against the reported current rate. No change is available.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(version, operation.Token)) ResetCapabilities("Detection was canceled. Click Detect to try again.");
        }
        catch (Exception exception)
        {
            if (IsCurrent(version, operation.Token)) ResetCapabilities($"Detection failed. {exception.Message}");
        }
        finally { EndOperation(operation, version); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Volatile.Read(ref enabled) != 1 || DeviceBox.SelectedItem is not DeviceChoice selected ||
            RateBox.SelectedItem is not RateChoice requested || detected is not { State: PollingRateState.Supported } capabilities ||
            capabilities.DeviceId != selected.Identity.Id || !capabilities.SupportedRatesHz.Contains(requested.Hertz)) return;

        // Consume this detected permission before awaiting. Every Apply requires a deliberate new Detect afterwards.
        detected = null;
        RateBox.ItemsSource = null;
        DetailText.Text = $"Applying {requested.Hertz.ToString(CultureInfo.InvariantCulture)} Hz…";
        var (operation, version) = BeginOperation();
        var token = operation.Token;
        // The controller may invoke this immediately before a hardware write on a worker thread.
        // It must never read a WPF control or mutable UI selection from that thread.
        bool CanApply() => IsCurrent(version, token);
        try
        {
            var result = await apply(selected.Identity, requested.Hertz, CanApply, token);
            if (!IsCurrent(version, token)) return;
            if (!string.Equals(result.Info.DeviceId, selected.Identity.Id, StringComparison.Ordinal))
                throw new InvalidDataException("The polling-rate result belongs to a different device.");
            ShowReportedRate(result.Info);
            var verified = result.Verified && result.Info.State == PollingRateState.Supported &&
                result.Info.CurrentRateHz == requested.Hertz;
            DetailText.Text = $"{(verified ? "Configuration verified." : "Change was not verified.")} {result.Detail} Click Detect before another change.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(version, token)) ResetCapabilities("Apply was canceled. The current rate is unknown; click Detect again.");
        }
        catch (Exception exception)
        {
            if (IsCurrent(version, token)) ResetCapabilities($"Change was not verified. Click Detect again. {exception.Message}");
        }
        finally { EndOperation(operation, version); }
    }

    private void ShowReportedRate(PollingRateInfo info)
    {
        CurrentText.Text = info.State == PollingRateState.Supported && info.CurrentRateHz is > 0
            ? $"Reported: {info.CurrentRateHz.Value.ToString(CultureInfo.InvariantCulture)} Hz"
            : "Current rate unavailable";
        DetailText.Text = info.Detail;
    }

    private (CancellationTokenSource Operation, int Version) BeginOperation()
    {
        var operation = new CancellationTokenSource();
        int version;
        lock (operationGate)
        {
            version = Interlocked.Increment(ref generation);
            activeOperation = operation;
        }
        busy = true;
        UpdateControls();
        return (operation, version);
    }

    private void EndOperation(CancellationTokenSource operation, int version)
    {
        lock (operationGate)
        {
            if (ReferenceEquals(activeOperation, operation)) activeOperation = null;
            operation.Dispose();
        }
        if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref generation) != version) return;
        busy = false;
        UpdateControls();
    }

    private bool IsCurrent(int version, CancellationToken token) => !token.IsCancellationRequested &&
        Volatile.Read(ref generation) == version && Volatile.Read(ref enabled) == 1 && Volatile.Read(ref closed) == 0;

    private int InvalidateOperation()
    {
        lock (operationGate)
        {
            var version = Interlocked.Increment(ref generation);
            activeOperation?.Cancel();
            return version;
        }
    }

    private void ResetCapabilities(string detail)
    {
        detected = null;
        busy = false;
        RateBox.ItemsSource = null;
        CurrentText.Text = "Current rate unavailable";
        DetailText.Text = detail;
        UpdateControls();
    }

    /// <summary>Revokes this session's permission immediately and resets its UI on the dispatcher.</summary>
    public void CancelOperations()
    {
        Volatile.Write(ref enabled, 0);
        var version = InvalidateOperation();
        void Reset()
        {
            if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref generation) != version) return;
            initializing = true;
            EnableBox.IsChecked = false;
            initializing = false;
            ResetCapabilities("Control was reset. Enable it and click Detect before making a change.");
        }
        if (Dispatcher.CheckAccess()) Reset();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Reset));
    }

    private void UpdateControls()
    {
        var canUse = Volatile.Read(ref enabled) == 1 && Volatile.Read(ref closed) == 0;
        DetectButton.IsEnabled = canUse && !busy && DeviceBox.SelectedItem is DeviceChoice;
        RatePanel.Visibility = detected is null ? Visibility.Collapsed : Visibility.Visible;
        RateBox.IsEnabled = canUse && !busy && detected is not null;
        ApplyButton.IsEnabled = canUse && !busy && detected is not null && RateBox.SelectedItem is RateChoice selected &&
            detected.SupportedRatesHz.Contains(selected.Hertz);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Volatile.Write(ref closed, 1);
        Volatile.Write(ref enabled, 0);
        InvalidateOperation();
        detected = null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private sealed record DeviceChoice(PeripheralIdentity Identity, string Name);
    private sealed record RateChoice(int Hertz)
    {
        public string Name => $"{Hertz.ToString(CultureInfo.InvariantCulture)} Hz";
    }
}
