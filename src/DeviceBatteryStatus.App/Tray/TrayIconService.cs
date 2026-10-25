using System.Runtime.InteropServices;
using DeviceBatteryStatus.Core.Presentation;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Dispatcher = System.Windows.Threading.Dispatcher;

namespace DeviceBatteryStatus.App.Tray;

/// <summary>Owns one system-tray icon. Create, update, and dispose on the WPF UI dispatcher.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly bool managedDpi;
    private Drawing.Icon? ownedIcon;
    private Drawing.Icon? fallbackIcon;
    private TrayPresentation presentation = new(null, BatteryColor.Neutral, "Device Battery Status — no current reading", null);
    private (int? Percentage, BatteryColor Color, int Width, int Height)? rendered;
    private uint dpi;
    private uint taskbarDpi;
    private uint dimensionsDpi;
    private int iconWidth;
    private int iconHeight;
    private long presentationUpdates;
    private long iconRenders;
    private long iconAssignments;
    private long tooltipAssignments;
    private long dpiProbes;
    private long dpiMetricQueries;
    private bool disposed;
    private Action? notificationClick;

    public TrayIconService(Action togglePopup, Action showSettings, Action refresh, Action exit)
        : this(togglePopup, showSettings, refresh, exit, 96, false) { }

    internal TrayIconService(Action togglePopup, Action showSettings, Action refresh, Action exit,
        uint initialDpi, bool manageDpiExternally)
    {
        ArgumentNullException.ThrowIfNull(togglePopup);
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(exit);
        managedDpi = manageDpiExternally;
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
        notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (disposed) return;
            var action = notificationClick;
            notificationClick = null;
            if (action is null) togglePopup();
            else action();
        };
        try
        {
            taskbarDpi = managedDpi ? initialDpi : ProbeTaskbarDpi();
            dpi = taskbarDpi == 0 ? 96 : taskbarDpi;
            UpdateDimensions();
            // Keep this separately owned neutral icon for allocation/update failures later.
            fallbackIcon = RenderIcon(null, BatteryColor.Neutral);
            notifyIcon.Icon = fallbackIcon;
            iconAssignments++;
            rendered = (null, BatteryColor.Neutral, iconWidth, iconHeight);
            Update(presentation);
        }
        catch
        {
            try { Dispose(); }
            catch { /* Preserve the original construction failure. */ }
            throw;
        }
        // NotifyIcon handles Explorer's TaskbarCreated message and restores its icon itself.
    }

    public void Update(TrayPresentation value)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(value);
        presentationUpdates++;
        presentation = value;
        try
        {
            var observedDpi = managedDpi ? taskbarDpi : ProbeTaskbarDpi();
            if (observedDpi != 0 && observedDpi != taskbarDpi)
            {
                taskbarDpi = observedDpi;
                dpi = observedDpi;
            }
            var percentage = value.Percentage is >= 0 and <= 100 && value.Color != BatteryColor.Neutral
                ? value.Percentage : null;
            var color = percentage is null ? BatteryColor.Neutral : value.Color;
            UpdateDimensions();
            var key = (percentage, color, iconWidth, iconHeight);
            if (rendered != key)
            {
                var next = RenderIcon(percentage, color);
                try { notifyIcon.Icon = next; iconAssignments++; }
                catch { next.Dispose(); throw; }
                var previous = ownedIcon;
                ownedIcon = next;
                rendered = key;
                previous?.Dispose();
            }
            var tooltip = LimitText(value.Tooltip, 127, "Device Battery Status");
            if (notifyIcon.Text != tooltip) { notifyIcon.Text = tooltip; tooltipAssignments++; }
            if (!notifyIcon.Visible) notifyIcon.Visible = true;
        }
        catch
        {
            ShowUnavailableFallback();
            throw;
        }
    }

    internal TrayActivityMetrics Metrics
    {
        get
        {
            dispatcher.VerifyAccess();
            return new(presentationUpdates, iconRenders, iconAssignments, tooltipAssignments,
                dpiProbes, dpiMetricQueries, 1, 0, 0);
        }
    }

    private uint ProbeTaskbarDpi()
    {
        dpiProbes++;
        return ReadTaskbarDpi();
    }

    private void UpdateDimensions()
    {
        if (dimensionsDpi == dpi) return;
        iconWidth = Math.Clamp(GetSystemMetricsForDpi(49, dpi), 16, 128); // SM_CXSMICON
        iconHeight = Math.Clamp(GetSystemMetricsForDpi(50, dpi), 16, 128); // SM_CYSMICON
        dpiMetricQueries += 2;
        dimensionsDpi = dpi;
    }

    private Drawing.Icon RenderIcon(int? percentage, BatteryColor color)
    {
        var icon = TrayIconRenderer.Render(iconWidth, iconHeight, percentage, color);
        iconRenders++;
        return icon;
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
        notificationClick = null;
        notifyIcon.ShowBalloonTip(5000, "Device Battery Status", text, Forms.ToolTipIcon.Warning);
        // Windows notification preferences may suppress this request; delivery is not guaranteed.
    }

    public void ShowInformation(string title, string message, Action onClick)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(onClick);
        notificationClick = onClick;
        try
        {
            notifyIcon.ShowBalloonTip(5000,
                LimitText(title, 63, "Device Battery Status"),
                LimitText(message, 255, "Open Settings for details."), Forms.ToolTipIcon.Info);
        }
        catch
        {
            notificationClick = null;
            throw;
        }
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

    internal static uint ReadTaskbarDpi()
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
            iconAssignments++;
            if (notifyIcon.Text != "Device Battery Status: display unavailable")
            {
                notifyIcon.Text = "Device Battery Status: display unavailable";
                tooltipAssignments++;
            }
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
        notificationClick = null;
        // Detach every owned resource even when a shell call fails during shutdown.
        var image = ownedIcon;
        var fallback = fallbackIcon;
        ownedIcon = null;
        fallbackIcon = null;
        try { notifyIcon.Visible = false; }
        finally
        {
            try { notifyIcon.Dispose(); }
            finally
            {
                try { menu.Dispose(); }
                finally
                {
                    try { image?.Dispose(); }
                    finally { fallback?.Dispose(); }
                }
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}

/// <summary>Dispatcher-owned counters for actual render/native-update work, not an estimate of OS resources.</summary>
internal readonly record struct TrayActivityMetrics(long PresentationUpdates, long IconRenders,
    long IconAssignments, long TooltipAssignments, long DpiProbes, long DpiMetricQueries,
    long MenuCreations, long SelectionReconciliations, long UnchangedSelections)
{
    public static TrayActivityMetrics operator +(TrayActivityMetrics left, TrayActivityMetrics right) =>
        new(left.PresentationUpdates + right.PresentationUpdates, left.IconRenders + right.IconRenders,
            left.IconAssignments + right.IconAssignments, left.TooltipAssignments + right.TooltipAssignments,
            left.DpiProbes + right.DpiProbes, left.DpiMetricQueries + right.DpiMetricQueries,
            left.MenuCreations + right.MenuCreations, left.SelectionReconciliations + right.SelectionReconciliations,
            left.UnchangedSelections + right.UnchangedSelections);
}
