using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Models;
using Drawing = System.Drawing;

namespace DeviceBatteryStatus.App.Alerts;

internal sealed record GamingAlertSafetyState(IntPtr WindowHandle, long ExtendedStyle,
    IntPtr ForegroundBeforeShow, IntPtr ForegroundAfterShow, Drawing.Rectangle NativeBounds,
    IntPtr MouseActivateResult, IntPtr HitTestResult, AlertMonitor Monitor)
{
    public bool NoActivate => (ExtendedStyle & AlertNative.NoActivate) != 0;
    public bool ClickThrough => (ExtendedStyle & AlertNative.Transparent) != 0;
    public bool ToolWindow => (ExtendedStyle & AlertNative.ToolWindow) != 0;
    public bool Layered => (ExtendedStyle & AlertNative.Layered) != 0;
}

/// <summary>A non-interactive ordinary desktop window. It never activates or handles keyboard input.</summary>
internal sealed class GamingAlertWindow : Window
{
    private readonly TextBlock severity = new() { FontSize = 10, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock deviceName = new() { FontSize = 14, FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock batteryValue = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock runtime = new() { FontSize = 12, Foreground = Brushes.Silver,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly Border accent;
    private HwndSource? source;
    private IntPtr handle;
    private AlertMonitor monitor;
    private IntPtr foregroundBeforeShow;
    private IntPtr foregroundAfterShow;
    private bool closed;
    internal GamingBatteryAlert Candidate { get; private set; }

    internal GamingAlertWindow(GamingBatteryAlert candidate, AlertMonitor monitor)
    {
        Candidate = candidate;
        this.monitor = monitor;
        Title = "Device Battery Status · Battery alert";
        Width = 360;
        Height = 112;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsTabStop = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        NameScope.SetNameScope(this, new NameScope());
        Register("AlertDeviceName", deviceName);
        Register("AlertBatteryValue", batteryValue);
        Register("AlertRuntime", runtime);
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 10), IsHitTestVisible = false, Focusable = false };
        panel.Children.Add(severity);
        deviceName.Margin = new Thickness(0, 4, 0, 3);
        panel.Children.Add(deviceName);
        panel.Children.Add(batteryValue);
        runtime.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(runtime);
        accent = new Border { BorderThickness = new Thickness(3, 1, 1, 1), CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromRgb(8, 8, 8)), Child = panel, IsHitTestVisible = false };
        Content = accent;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowMessage);
            var style = AlertNative.ReadExtendedStyle(handle);
            AlertNative.WriteExtendedStyle(handle, (style | AlertNative.NoActivate | AlertNative.Transparent |
                AlertNative.ToolWindow | AlertNative.Layered) & ~AlertNative.AppWindow);
            Place();
        };
        DpiChanged += (_, arguments) =>
        {
            monitor = monitor with { Dpi = (uint)Math.Clamp(Math.Round(arguments.NewDpi.PixelsPerInchX), 48, 768) };
            Place();
        };
        Closed += (_, _) => { closed = true; source?.RemoveHook(WindowMessage); source = null; handle = IntPtr.Zero; };
        UpdateCandidate(candidate, monitor);
    }

    internal void ShowWithoutActivation()
    {
        Dispatcher.VerifyAccess();
        foregroundBeforeShow = AlertNative.GetForegroundWindow();
        Show();
        Place();
        foregroundAfterShow = AlertNative.GetForegroundWindow();
    }

    internal void UpdateCandidate(GamingBatteryAlert candidate, AlertMonitor destination)
    {
        Dispatcher.VerifyAccess();
        Candidate = candidate;
        var changedMonitor = monitor != destination;
        monitor = destination;
        SetText(deviceName, candidate.Name);
        SetText(batteryValue, candidate.Percentage is { } percentage ? $"{percentage}% battery"
            : candidate.Category is { } category && category != BatteryCategory.Unknown ? $"{category} · reported category" : "Battery unavailable");
        var runtimeText = candidate.RuntimeText;
        SetText(runtime, string.IsNullOrWhiteSpace(runtimeText) || runtimeText == "Unavailable" ? "Runtime unavailable"
            : runtimeText == "Calculating…" ? "Runtime · Calculating…" : runtimeText);
        SetText(severity, candidate.Severity == GamingAlertSeverity.Critical ? "CRITICAL BATTERY" : "LOW BATTERY");
        var color = candidate.Severity == GamingAlertSeverity.Critical ? Color.FromRgb(240, 92, 96) : Color.FromRgb(255, 209, 92);
        var emphasis = new SolidColorBrush(color); emphasis.Freeze();
        severity.Foreground = emphasis;
        batteryValue.Foreground = emphasis;
        deviceName.Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44));
        accent.BorderBrush = emphasis;
        if (changedMonitor && handle != IntPtr.Zero) Place();
    }

    internal GamingAlertSafetyState GetSafetyState()
    {
        Dispatcher.VerifyAccess();
        if (handle == IntPtr.Zero) throw new InvalidOperationException("The gaming alert has no native window.");
        if (!AlertNative.GetWindowRect(handle, out var bounds)) throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        // Sending these messages to our own window checks the actual message hook,
        // without synthesizing keyboard/mouse input or interacting with a game.
        var mouse = AlertNative.SendMessage(handle, AlertNative.MouseActivate, IntPtr.Zero, IntPtr.Zero);
        var hit = AlertNative.SendMessage(handle, AlertNative.NonClientHitTest, IntPtr.Zero, IntPtr.Zero);
        return new(handle, AlertNative.ReadExtendedStyle(handle), foregroundBeforeShow, foregroundAfterShow,
            bounds.Rectangle, mouse, hit, monitor);
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (message == AlertNative.MouseActivate) { handled = true; return new IntPtr(AlertNative.NoActivateResult); }
        if (message == AlertNative.NonClientHitTest) { handled = true; return new IntPtr(AlertNative.TransparentHitTest); }
        return IntPtr.Zero;
    }

    private void Place()
    {
        if (handle == IntPtr.Zero || closed) return;
        var scale = Math.Clamp(monitor.Dpi, 48u, 768u) / 96.0;
        var area = monitor.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("The selected monitor has no usable work area.");
        var margin = Math.Clamp((int)Math.Round(12 * scale), 0, Math.Max(0, Math.Min(area.Width, area.Height) / 8));
        var width = Math.Max(1, Math.Min((int)Math.Ceiling(360 * scale), area.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Ceiling(112 * scale), area.Height - margin * 2));
        Width = width / scale;
        Height = height / scale;
        var x = Math.Max(area.Left, area.Right - margin - width);
        var y = Math.Max(area.Top, area.Bottom - margin - height);
        // HWND_TOPMOST and SWP_NOACTIVATE keep the desktop popup visible without
        // changing the foreground window or minimizing anything underneath it.
        if (!AlertNative.SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0200))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Gaming alert placement failed.");
    }

    private void Register(string name, FrameworkElement element) { element.Name = name; RegisterName(name, element); }
    private static void SetText(TextBlock block, string value) { if (block.Text != value) block.Text = value; }
}
