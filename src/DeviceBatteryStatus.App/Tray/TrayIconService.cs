using System.Runtime.InteropServices;
using DeviceBatteryStatus.Core.Presentation;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfPoint = System.Windows.Point;
using Dispatcher = System.Windows.Threading.Dispatcher;

namespace DeviceBatteryStatus.App.Tray;

/// <summary>Owns one system-tray icon. Create, update, and dispose on the WPF UI dispatcher.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private Drawing.Icon? ownedIcon;
    private Drawing.Icon? fallbackIcon;
    private TrayPresentation presentation = new(null, BatteryColor.Neutral, "Device Battery Status — no current reading", null);
    private (int? Percentage, BatteryColor Color, int Width, int Height)? rendered;
    private uint dpi;
    private uint taskbarDpi;
    private bool disposed;

    public TrayIconService(Action togglePopup, Action showSettings, Action refresh, Action exit)
    {
        ArgumentNullException.ThrowIfNull(togglePopup);
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(exit);
        menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(0x10, 0x10, 0x10),
            ForeColor = Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE)
        };
        menu.Items.Add("Open", null, (_, _) => togglePopup());
        menu.Items.Add("Refresh", null, (_, _) => refresh());
        menu.Items.Add("Settings", null, (_, _) => showSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        notifyIcon = new Forms.NotifyIcon { ContextMenuStrip = menu };
        // NotifyIcon suppresses MouseClick for the second click of a double-click. Subscribe
        // only to this event, so double-clicks do not open and immediately close the popup.
        notifyIcon.MouseClick += (_, args) =>
        {
            if (!disposed && args.Button == Forms.MouseButtons.Left) togglePopup();
        };
        notifyIcon.BalloonTipClicked += (_, _) => { if (!disposed) togglePopup(); };
        try
        {
            taskbarDpi = ReadTaskbarDpi();
            dpi = taskbarDpi == 0 ? 96 : taskbarDpi;
            var width = Math.Clamp(GetSystemMetricsForDpi(49, dpi), 16, 128);
            var height = Math.Clamp(GetSystemMetricsForDpi(50, dpi), 16, 128);
            // Keep this separately owned neutral icon for allocation/update failures later.
            fallbackIcon = TrayIconRenderer.Render(width, height, null, BatteryColor.Neutral);
            notifyIcon.Icon = fallbackIcon;
            rendered = (null, BatteryColor.Neutral, width, height);
            Update(presentation);
        }
        catch
        {
            Dispose();
            throw;
        }
        // NotifyIcon handles Explorer's TaskbarCreated message and restores its icon itself.
    }

    public void Update(TrayPresentation value)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(value);
        presentation = value;
        try
        {
            var observedDpi = ReadTaskbarDpi();
            if (observedDpi != 0 && observedDpi != taskbarDpi)
            {
                taskbarDpi = observedDpi;
                dpi = observedDpi;
            }
            var percentage = value.Percentage is >= 0 and <= 100 && value.Color != BatteryColor.Neutral
                ? value.Percentage : null;
            var color = percentage is null ? BatteryColor.Neutral : value.Color;
            var width = Math.Clamp(GetSystemMetricsForDpi(49, dpi), 16, 128); // SM_CXSMICON
            var height = Math.Clamp(GetSystemMetricsForDpi(50, dpi), 16, 128); // SM_CYSMICON
            var key = (percentage, color, width, height);
            if (rendered != key)
            {
                var next = TrayIconRenderer.Render(width, height, percentage, color);
                try { notifyIcon.Icon = next; }
                catch { next.Dispose(); throw; }
                var previous = ownedIcon;
                ownedIcon = next;
                rendered = key;
                previous?.Dispose();
            }
            var tooltip = LimitText(value.Tooltip, 127, "Device Battery Status");
            if (notifyIcon.Text != tooltip) notifyIcon.Text = tooltip;
            if (!notifyIcon.Visible) notifyIcon.Visible = true;
        }
        catch
        {
            ShowUnavailableFallback();
            throw;
        }
    }

    /// <summary>Call when the tray/popup monitor changes DPI; unchanged images are reused.</summary>
    public void UpdateDpi(uint value)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        value = Math.Clamp(value == 0 ? 96u : value, 48u, 768u);
        if (value == dpi) return;
        dpi = value;
        Update(presentation);
    }

    public void ShowLowBattery(LowBatteryAlert alert)
    {
        dispatcher.VerifyAccess();
        if (disposed || alert.Percentage is < 0 or > 100) return;
        var text = LimitText($"{alert.Name}: {alert.Percentage}% battery remaining.", 255, "Low battery");
        notifyIcon.ShowBalloonTip(5000, "Device Battery Status", text, Forms.ToolTipIcon.Warning);
        // Windows notification preferences may suppress this request; delivery is not guaranteed.
    }

    /// <summary>Returns physical screen pixels. The WPF popup must convert using its monitor DPI.</summary>
    public WpfPoint GetAnchor()
    {
        dispatcher.VerifyAccess();
        return GetPhysicalCursorPos(out var point) ? new WpfPoint(point.X, point.Y) : new WpfPoint(0, 0);
    }

    private static string LimitText(string? text, int maximum, string fallback)
    {
        text = text?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(text)) return fallback;
        if (text.Length <= maximum) return text;
        var cut = maximum - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    private static uint ReadTaskbarDpi()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        return taskbar == IntPtr.Zero ? GetDpiForSystem() : GetDpiForWindow(taskbar);
    }

    private void ShowUnavailableFallback()
    {
        rendered = null; // A successful future update must recreate/reattach the actual presentation.
        var previous = ownedIcon;
        ownedIcon = null;
        try
        {
            if (fallbackIcon is null) throw new InvalidOperationException("No neutral tray icon is available.");
            notifyIcon.Icon = fallbackIcon;
            notifyIcon.Text = "Device Battery Status: display unavailable";
            notifyIcon.Visible = true;
        }
        catch
        {
            // If even the cached neutral icon cannot be attached, stop displaying the old percentage.
            try { notifyIcon.Visible = false; }
            catch { /* Preserve the original update exception for the controller's error report. */ }
        }
        finally { previous?.Dispose(); }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
        ownedIcon?.Dispose();
        ownedIcon = null;
        fallbackIcon?.Dispose();
        fallbackIcon = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);
}
