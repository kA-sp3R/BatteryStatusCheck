using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DeviceBatteryStatus.App.ViewModels;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Runtime;

namespace DeviceBatteryStatus.App;

public partial class PopupWindow : Window
{
    private nint anchorMonitor;
    private bool allowClose;
    private bool showing;
    private long suppressTrayToggleUntil;
    private IReadOnlyList<DeviceRowViewModel> displayedRows = Array.Empty<DeviceRowViewModel>();
    public uint CurrentDpi { get; private set; } = 96;
    internal int PresentationUpdateCount { get; private set; }

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event Action<string>? DeviceDetailsRequested;

    public PopupWindow() => InitializeComponent();

    public void SetDevices(IReadOnlyList<DeviceSnapshot> devices, AppSettings settings,
        DateTimeOffset now, string? status, IReadOnlyDictionary<string, RuntimeEstimate>? runtimeMap = null)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        var rowsChanged = displayedRows.Count != devices.Count;
        if (!rowsChanged)
            for (var index = 0; index < devices.Count; index++)
                if (!displayedRows[index].Matches(devices[index], settings, now, runtimeMap?.GetValueOrDefault(devices[index].Device.Id))) { rowsChanged = true; break; }
        if (rowsChanged)
        {
            var rows = new DeviceRowViewModel[devices.Count];
            for (var index = 0; index < devices.Count; index++)
                rows[index] = index < displayedRows.Count && displayedRows[index].Matches(devices[index], settings, now, runtimeMap?.GetValueOrDefault(devices[index].Device.Id))
                    ? displayedRows[index] : new DeviceRowViewModel(devices[index], settings, now, runtimeMap?.GetValueOrDefault(devices[index].Device.Id));
            displayedRows = rows;
            DeviceList.ItemsSource = rows;
            PresentationUpdateCount++;
        }
        EmptyState.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceScroll.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var statusText = status ?? (devices.Count == 0 ? "No peripherals to display"
            : $"{devices.Count} peripheral{(devices.Count == 1 ? "" : "s")} · battery monitoring");
        if (!string.Equals(StatusLabel.Text, statusText, StringComparison.Ordinal))
        {
            StatusLabel.Text = statusText;
            StatusLabel.ToolTip = statusText;
        }
        if (IsVisible && rowsChanged)
        {
            UpdateLayout();
            PositionAtAnchor(moveToMonitor: false);
        }
    }

    public void ToggleNearTray()
    {
        Dispatcher.VerifyAccess();
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (Environment.TickCount64 < suppressTrayToggleUntil)
        {
            // The same taskbar mouse click can deactivate this window before NotifyIcon raises MouseClick.
            suppressTrayToggleUntil = 0;
            return;
        }

        anchorMonitor = NativePlacement.GetCursorPos(out var point)
            ? NativePlacement.MonitorFromPoint(point, 2) : nint.Zero;
        showing = true;
        try
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            PositionAtAnchor(moveToMonitor: true);
            Opacity = 1;
            Activate();
            RefreshButton.Focus();
        }
        finally { showing = false; }
    }

    /// <summary>Only the application controller uses this during its explicit shutdown.</summary>
    public void CloseForShutdown()
    {
        allowClose = true;
        if (IsVisible) Hide();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        CurrentDpi = (uint)Math.Round(newDpi.DpiScaleX * 96);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Hide();
        e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (allowClose || showing || !IsVisible) return;
        if ((NativePlacement.GetAsyncKeyState(1) & 0x8000) != 0 &&
            NativePlacement.GetCursorPos(out var cursor))
        {
            var monitor = NativePlacement.MonitorFromPoint(cursor, 2);
            var info = new NativePlacement.MonitorInfo { Size = Marshal.SizeOf<NativePlacement.MonitorInfo>() };
            if (NativePlacement.GetMonitorInfoW(monitor, ref info) &&
                (cursor.X < info.Work.Left || cursor.X >= info.Work.Right ||
                 cursor.Y < info.Work.Top || cursor.Y >= info.Work.Bottom))
                suppressTrayToggleUntil = Environment.TickCount64 + 350;
        }
        Hide();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeviceDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceRowViewModel row }) return;
        Hide();
        DeviceDetailsRequested?.Invoke(row.DeviceId);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PositionAtAnchor(bool moveToMonitor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (anchorMonitor == nint.Zero) anchorMonitor = NativePlacement.MonitorFromWindow(handle, 2);
        var info = new NativePlacement.MonitorInfo { Size = Marshal.SizeOf<NativePlacement.MonitorInfo>() };
        if (!NativePlacement.GetMonitorInfoW(anchorMonitor, ref info))
        {
            // A removed display falls back to WPF's primary work area.
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 10);
            Top = Math.Max(area.Top, area.Bottom - ActualHeight - 10);
            CurrentDpi = Math.Max(96u, NativePlacement.GetDpiForWindow(handle));
            return;
        }

        var dpi = NativePlacement.GetDpiForMonitor(anchorMonitor, 0, out var monitorDpi, out _) == 0
            && monitorDpi != 0 ? monitorDpi : 96u;
        var margin = (int)Math.Round(10 * dpi / 96.0);
        if (moveToMonitor)
        {
            // Establish the target monitor before measuring at its DPI. Stay transparent during this move.
            NativePlacement.SetWindowPos(handle, nint.Zero, info.Work.Left + margin, info.Work.Top + margin,
                0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
            UpdateLayout();
        }
        var actualDpi = NativePlacement.GetDpiForWindow(handle);
        CurrentDpi = actualDpi == 0 ? dpi : actualDpi;
        var scale = CurrentDpi / 96.0;
        margin = (int)Math.Round(10 * scale);
        var workWidth = Math.Max(1, info.Work.Right - info.Work.Left);
        var workHeight = Math.Max(1, info.Work.Bottom - info.Work.Top);
        MaxHeight = Math.Min(480, Math.Max(1, (workHeight - margin * 2) / scale));
        DeviceScroll.MaxHeight = Math.Max(1, MaxHeight - 76);
        UpdateLayout();

        var width = Math.Min(workWidth, Math.Max(1, (int)Math.Ceiling(ActualWidth * scale)));
        var height = Math.Min(workHeight, Math.Max(1, (int)Math.Ceiling(ActualHeight * scale)));
        var left = info.Work.Right - width - margin;
        var top = info.Work.Bottom - height - margin;
        // Respect taskbars on the top or left as well as the usual bottom/right notification area.
        if (info.Work.Top > info.Monitor.Top) top = info.Work.Top + margin;
        if (info.Work.Left > info.Monitor.Left) left = info.Work.Left + margin;
        left = Math.Clamp(left, info.Work.Left, info.Work.Right - width);
        top = Math.Clamp(top, info.Work.Top, info.Work.Bottom - height);
        NativePlacement.SetWindowPos(handle, nint.Zero, left, top, width, height, 0x0014); // NOZORDER | NOACTIVATE
    }

    private static class NativePlacement
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo { internal int Size; internal Rect Monitor; internal Rect Work; internal uint Flags; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromPoint(Point point, uint flags);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(nint monitor, uint dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
            int width, int height, uint flags);
    }
}
