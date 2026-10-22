using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Dispatcher = System.Windows.Threading.Dispatcher;

namespace DeviceBatteryStatus.App.Tray;

/// <summary>Owns a bounded set of independently disposed notification-area icons on the UI dispatcher.</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, TrayIconService> icons = new(StringComparer.Ordinal);
    private readonly Action togglePopup;
    private readonly Action showSettings;
    private readonly Action refresh;
    private readonly Action exit;
    private uint? dpi;
    private bool disposed;

    public TrayIconManager(Action togglePopup, Action showSettings, Action refresh, Action exit)
    {
        ArgumentNullException.ThrowIfNull(togglePopup);
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(exit);
        this.togglePopup = togglePopup;
        this.showSettings = showSettings;
        this.refresh = refresh;
        this.exit = exit;
        icons.Add(TrayIconSelection.SummaryKey, CreateIcon());
    }

    public int ActiveIconCount
    {
        get { dispatcher.VerifyAccess(); return icons.Count; }
    }

    public void Update(IReadOnlyList<DeviceSnapshot> devices, AppSettings settings, DateTimeOffset now)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        var selected = TrayIconSelection.Create(devices, settings, now);
        var desired = selected.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        Exception? failure = null;
        var obsolete = icons.Keys.Where(key => !desired.Contains(key)).ToArray();
        // Retain one neutral fallback until a replacement exists. Allocation failures
        // must not strand an otherwise running app without its Settings/Exit menu.
        string? fallbackKey = obsolete.Length == icons.Count && icons.Count > 0 ? obsolete[0] : null;
        foreach (var key in obsolete.Where(key => key != fallbackKey))
            RemoveIcon(key, ref failure);
        if (fallbackKey is not null)
        {
            try
            {
                icons[fallbackKey].Update(new(null, BatteryColor.Neutral,
                    "Device Battery Status: preparing selected tray icons", null));
            }
            catch (Exception exception) { failure ??= exception; }
        }
        foreach (var item in selected)
        {
            try
            {
                if (!icons.TryGetValue(item.Key, out var icon))
                {
                    icon = CreateIcon();
                    icons.Add(item.Key, icon);
                }
                // Retire the temporary fallback as soon as the first replacement
                // exists, keeping the set bounded even while a mode changes.
                if (fallbackKey is not null)
                {
                    RemoveIcon(fallbackKey, ref failure);
                    fallbackKey = null;
                }
                icon.Update(item.Presentation);
            }
            catch (Exception exception)
            {
                // Other selected icons must still be updated when one allocation or
                // shell call fails; each existing icon handles its own neutral fallback.
                failure ??= exception;
            }
        }
        if (fallbackKey is not null)
        {
            try
            {
                icons[fallbackKey].Update(new(null, BatteryColor.Neutral,
                    "Device Battery Status: selected tray icons unavailable", null));
            }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
            throw new InvalidOperationException("One or more tray icons could not be updated.", failure);
    }

    private void RemoveIcon(string key, ref Exception? failure)
    {
        var previous = icons[key];
        icons.Remove(key);
        try { previous.Dispose(); }
        catch (Exception exception) { failure ??= exception; }
    }

    public void UpdateDpi(uint value)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        dpi = Math.Clamp(value == 0 ? 96u : value, 48u, 768u);
        Exception? failure = null;
        foreach (var icon in icons.Values)
        {
            try { icon.UpdateDpi(dpi.Value); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
            throw new InvalidOperationException("One or more tray icons could not update their DPI.", failure);
    }

    public void ShowLowBattery(LowBatteryAlert alert)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(alert);
        // A selected device gets its own notification. An unselected device still
        // uses one existing icon, so enabling multiple icons does not drop alerts.
        var icon = icons.GetValueOrDefault(TrayIconSelection.DeviceKey(alert.DeviceId))
            ?? icons.GetValueOrDefault(TrayIconSelection.SummaryKey) ?? icons.Values.FirstOrDefault();
        icon?.ShowLowBattery(alert);
    }

    public void ShowUpdateAvailable(string version, Action openUpdates)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(openUpdates);
        var icon = icons.GetValueOrDefault(TrayIconSelection.SummaryKey) ?? icons.Values.FirstOrDefault();
        icon?.ShowInformation("Device Battery Status update",
            $"Version {version} is available. Click to review it; installing requires your action.", openUpdates);
    }

    private TrayIconService CreateIcon()
    {
        var icon = new TrayIconService(togglePopup, showSettings, refresh, exit);
        try
        {
            if (dpi is { } currentDpi) icon.UpdateDpi(currentDpi);
            return icon;
        }
        catch
        {
            try { icon.Dispose(); }
            catch { /* Preserve the original allocation/DPI failure. */ }
            throw;
        }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        Exception? failure = null;
        foreach (var icon in icons.Values)
        {
            try { icon.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
        }
        icons.Clear();
        if (failure is not null)
            throw new InvalidOperationException("One or more tray icons could not be disposed.", failure);
    }
}
