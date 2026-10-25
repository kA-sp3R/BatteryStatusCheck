using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Runtime;
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
    private DeviceSnapshot[]? previousDevices;
    private AppSettings? previousSettings;
    private IReadOnlyList<TrayIconItem>? previousSelection;
    private DateTimeOffset previousPresentationTime;
    private bool previousRuntimeEnabled;
    private readonly Dictionary<string, string> previousRuntimeTexts = new(StringComparer.Ordinal);
    private DateTimeOffset nextFreshnessChange;
    private uint observedTaskbarDpi;
    private long lastDpiProbe;
    private long dpiProbes;
    private long selectionReconciliations;
    private long unchangedSelections;
    private TrayActivityMetrics retiredMetrics;
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
        observedTaskbarDpi = TrayIconService.ReadTaskbarDpi();
        dpiProbes++;
        lastDpiProbe = Environment.TickCount64;
        dpi = observedTaskbarDpi == 0 ? 96u : observedTaskbarDpi;
        icons.Add(TrayIconSelection.SummaryKey, CreateIcon());
    }

    public int ActiveIconCount
    {
        get { dispatcher.VerifyAccess(); return icons.Count; }
    }

    public void Update(IReadOnlyList<DeviceSnapshot> devices, AppSettings settings, DateTimeOffset now,
        IReadOnlyDictionary<string, RuntimeEstimate>? runtimeMap = null)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        RefreshTaskbarDpi();
        // The monitor publishes immutable records, but callers can supply a mutable
        // collection. Keep a small copy so mutation cannot hide a presentation change.
        if (previousDevices is not null && previousSettings is not null && previousSelection is not null &&
            now >= previousPresentationTime && now < nextFreshnessChange &&
            SameDisplaySettings(settings, previousSettings) && SameDevices(devices, previousDevices) &&
            SameRuntimeInputs(devices, settings, now, runtimeMap))
        {
            unchangedSelections++;
            return;
        }
        selectionReconciliations++;
        var selected = WithRuntimeTooltips(TrayIconSelection.Create(devices, settings, now), devices, settings, now, runtimeMap);
        if (previousSelection is not null && selected.SequenceEqual(previousSelection))
        {
            unchangedSelections++;
            RememberInputs(devices, settings, selected, now, runtimeMap);
            return;
        }
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
        {
            InvalidatePresentationCache();
            throw new InvalidOperationException("One or more tray icons could not be updated.", failure);
        }
        RememberInputs(devices, settings, selected, now, runtimeMap);
    }

    internal TrayActivityMetrics Metrics
    {
        get
        {
            dispatcher.VerifyAccess();
            var result = retiredMetrics + new TrayActivityMetrics(0, 0, 0, 0, dpiProbes, 0, 0,
                selectionReconciliations, unchangedSelections);
            foreach (var icon in icons.Values) result += icon.Metrics;
            return result;
        }
    }

    private void RefreshTaskbarDpi()
    {
        // Reuse the application's presentation ticks. One probe covers all selected
        // icons; no new timer, per-icon shell query, or icon allocation is necessary.
        var timestamp = Environment.TickCount64;
        if (timestamp - lastDpiProbe < 5000) return;
        lastDpiProbe = timestamp;
        var observed = TrayIconService.ReadTaskbarDpi();
        dpiProbes++;
        if (observed == 0 || observed == observedTaskbarDpi) return;
        observedTaskbarDpi = observed;
        UpdateDpi(observed);
    }

    private void RememberInputs(IReadOnlyList<DeviceSnapshot> devices, AppSettings settings,
        IReadOnlyList<TrayIconItem> selection, DateTimeOffset now, IReadOnlyDictionary<string, RuntimeEstimate>? runtimeMap)
    {
        previousDevices = devices.ToArray();
        previousSettings = settings with
        {
            TrayDeviceIds = (settings.TrayDeviceIds ?? Array.Empty<string>()).ToArray(),
            CustomNames = settings.CustomNames is null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(settings.CustomNames, StringComparer.Ordinal)
        };
        previousSelection = selection;
        previousPresentationTime = now;
        previousRuntimeEnabled = runtimeMap is not null;
        previousRuntimeTexts.Clear();
        if (runtimeMap is not null)
            foreach (var snapshot in devices)
                previousRuntimeTexts[snapshot.Device.Id] = RuntimeText(snapshot, settings, now, runtimeMap);
        nextFreshnessChange = DateTimeOffset.MaxValue;
        var age = TimeSpan.FromSeconds(2 * Math.Clamp(settings.RefreshIntervalSeconds, 10, 3600) + 5);
        foreach (var snapshot in devices)
        {
            if (snapshot.Connection == ConnectionState.Disconnected || snapshot.Battery.State != ReadingState.Current ||
                !snapshot.Battery.HasMeasurement || snapshot.Battery.SampledAt is not { } sampled) continue;
            var transition = sampled > now ? sampled
                : sampled.UtcTicks > DateTimeOffset.MaxValue.UtcTicks - age.Ticks - 1 ? DateTimeOffset.MaxValue
                : sampled + age + TimeSpan.FromTicks(1);
            if (transition > now && transition < nextFreshnessChange) nextFreshnessChange = transition;
        }
    }

    internal IReadOnlyList<TrayIconItem> DisplayedPresentations => previousSelection ?? Array.Empty<TrayIconItem>();

    private bool SameRuntimeInputs(IReadOnlyList<DeviceSnapshot> devices, AppSettings settings, DateTimeOffset now,
        IReadOnlyDictionary<string, RuntimeEstimate>? runtimeMap)
    {
        if (previousRuntimeEnabled != (runtimeMap is not null)) return false;
        if (runtimeMap is null) return true;
        foreach (var snapshot in devices)
            if (!previousRuntimeTexts.TryGetValue(snapshot.Device.Id, out var text) ||
                text != RuntimeText(snapshot, settings, now, runtimeMap)) return false;
        return true;
    }

    private static IReadOnlyList<TrayIconItem> WithRuntimeTooltips(IReadOnlyList<TrayIconItem> items,
        IReadOnlyList<DeviceSnapshot> devices, AppSettings settings, DateTimeOffset now,
        IReadOnlyDictionary<string, RuntimeEstimate>? runtimeMap)
    {
        if (runtimeMap is null) return items;
        var result = new TrayIconItem[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var id = item.Presentation.DeviceId;
            DeviceSnapshot? snapshot = null;
            if (id is not null)
            {
                // Scan until uniqueness is decided without allocating a query
                // and matching array for every selected icon.
                for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
                {
                    var candidate = devices[deviceIndex];
                    if (candidate.Device.Id != id) continue;
                    if (snapshot is not null) { snapshot = null; break; }
                    snapshot = candidate;
                }
            }
            if (snapshot is null) { result[index] = item; continue; }
            var name = BatteryPresentation.GetDisplayName(snapshot.Device, settings);
            if (name.Length > 32) name = name[..(char.IsHighSurrogate(name[30]) ? 30 : 31)] + "…";
            var runtime = RuntimeText(snapshot, settings, now, runtimeMap);
            var disconnected = snapshot.Connection == ConnectionState.Disconnected ||
                snapshot.Battery.State == ReadingState.Disconnected;
            var value = disconnected ? "Disconnected" : BatteryPresentation.GetBatteryValueText(snapshot, settings, now);
            var sampled = snapshot.Battery.SampledAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture);
            var state = disconnected ? "" : $" · {BatteryPresentation.GetStatusText(snapshot, settings, now)}";
            var tooltip = $"{name}: {value} · Runtime: {runtime}{state}" + (sampled is null ? "" : $" · {sampled}");
            result[index] = item with { Presentation = item.Presentation with { Tooltip = tooltip } };
        }
        return Array.AsReadOnly(result);
    }

    private static string RuntimeText(DeviceSnapshot snapshot, AppSettings settings, DateTimeOffset now,
        IReadOnlyDictionary<string, RuntimeEstimate> runtimeMap) =>
        !BatteryPresentation.IsFreshPercentage(snapshot, settings, now) ||
        snapshot.Connection != ConnectionState.Connected || snapshot.Battery.Charging != ChargingState.Discharging
            ? "Unavailable" : runtimeMap.GetValueOrDefault(snapshot.Device.Id)?.Text ?? "Unavailable";

    private static bool SameDevices(IReadOnlyList<DeviceSnapshot> left, DeviceSnapshot[] right)
    {
        if (left.Count != right.Length) return false;
        for (var index = 0; index < right.Length; index++)
        {
            var current = left[index];
            var previous = right[index];
            // Capacity/current telemetry is not displayed in a tray icon. Its
            // collection must not force battery presentation work on its own.
            if (current.Device != previous.Device || current.Battery != previous.Battery ||
                current.Connection != previous.Connection) return false;
        }
        return true;
    }

    private static bool SameDisplaySettings(AppSettings left, AppSettings right)
    {
        if (left.RefreshIntervalSeconds != right.RefreshIntervalSeconds || left.PreferredDeviceId != right.PreferredDeviceId ||
            left.TrayIconMode != right.TrayIconMode) return false;
        var leftIds = left.TrayDeviceIds ?? Array.Empty<string>();
        if (leftIds.Count != right.TrayDeviceIds.Count) return false;
        for (var index = 0; index < leftIds.Count; index++)
            if (leftIds[index] != right.TrayDeviceIds[index]) return false;
        if (left.CustomNames is null) return right.CustomNames.Count == 0;
        if (left.CustomNames.Count != right.CustomNames.Count) return false;
        foreach (var pair in left.CustomNames)
            if (!right.CustomNames.TryGetValue(pair.Key, out var name) || name != pair.Value) return false;
        return true;
    }

    private void InvalidatePresentationCache()
    {
        previousDevices = null;
        previousSettings = null;
        previousSelection = null;
        previousRuntimeTexts.Clear();
        previousRuntimeEnabled = false;
    }

    private void RemoveIcon(string key, ref Exception? failure)
    {
        var previous = icons[key];
        icons.Remove(key);
        try { previous.Dispose(); }
        catch (Exception exception) { failure ??= exception; }
        finally { retiredMetrics += previous.Metrics; }
    }

    public void UpdateDpi(uint value)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        value = Math.Clamp(value == 0 ? 96u : value, 48u, 768u);
        if (dpi == value) return;
        dpi = value;
        Exception? failure = null;
        foreach (var icon in icons.Values)
        {
            try { icon.UpdateDpi(dpi.Value); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
        {
            InvalidatePresentationCache();
            throw new InvalidOperationException("One or more tray icons could not update their DPI.", failure);
        }
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
        var icon = new TrayIconService(togglePopup, showSettings, refresh, exit, dpi ?? 96u, true);
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
            finally { retiredMetrics += icon.Metrics; }
        }
        icons.Clear();
        InvalidatePresentationCache();
        if (failure is not null)
            throw new InvalidOperationException("One or more tray icons could not be disposed.", failure);
    }
}
