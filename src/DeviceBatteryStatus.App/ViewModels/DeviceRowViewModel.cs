using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Runtime;

namespace DeviceBatteryStatus.App.ViewModels;

/// <summary>An immutable display of one snapshot; it never changes hardware data.</summary>
public sealed class DeviceRowViewModel
{
    private static readonly Brush Green = FrozenBrush(0x44, 0xD6, 0x2C);
    private static readonly Brush Yellow = FrozenBrush(0xFF, 0xD1, 0x5C);
    private static readonly Brush Red = FrozenBrush(0xF0, 0x5C, 0x60);
    private static readonly Brush Neutral = FrozenBrush(0x91, 0x9A, 0x91);
    private static readonly Brush StaleProgress = FrozenBrush(0x4F, 0x5C, 0x50);
    private readonly PeripheralIdentity identity;
    private readonly BatteryReading reading;
    private readonly ConnectionState connectionState;
    private readonly bool fresh;
    private readonly string runtimeDetail;

    public string DeviceId => identity.Id;
    public string Name { get; }
    public string PercentageText { get; }
    public string StatusText { get; }
    public string RuntimeText { get; }
    public string RuntimeDisplayText => $"Runtime: {RuntimeText}";
    public string ToolTip { get; }
    public string VendorName { get; }
    public string DeviceGlyph { get; }
    public string DeviceKind { get; }
    public string DeviceIconToolTip { get; }
    public string AccessibilityText { get; }
    public Brush PercentageBrush { get; }
    public Brush ProgressBrush { get; }
    public double ProgressValue { get; }
    public Visibility ProgressVisibility { get; }

    public DeviceRowViewModel(DeviceSnapshot snapshot, AppSettings settings, DateTimeOffset now, RuntimeEstimate? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        identity = snapshot.Device;
        reading = snapshot.Battery;
        connectionState = snapshot.Connection;
        var battery = snapshot.Battery;
        (RuntimeText, runtimeDetail) = GetRuntimePresentation(snapshot, settings, now, runtime);
        Name = BatteryPresentation.GetDisplayName(snapshot.Device, settings);
        VendorName = snapshot.Device.Vendor.ToString();
        (DeviceGlyph, DeviceKind) = GetDeviceIcon(snapshot.Device);
        DeviceIconToolTip = $"{VendorName} {DeviceKind.ToLowerInvariant()}";

        fresh = BatteryPresentation.IsFreshCurrent(snapshot, settings, now);
        var stale = battery.State == ReadingState.Stale ||
            (battery.State == ReadingState.Current && !fresh);
        var hasPercentage = battery.Percentage is not null;
        PercentageText = BatteryPresentation.GetBatteryValueText(snapshot, settings, now);
        PercentageBrush = fresh && hasPercentage ? battery.Percentage switch
        {
            <= 10 => Red,
            <= 30 => Yellow,
            _ => Green
        } : Neutral;
        ProgressBrush = fresh ? PercentageBrush : StaleProgress;
        var showProgress = (fresh || stale) && hasPercentage;
        ProgressValue = showProgress ? battery.Percentage!.Value : 0;
        ProgressVisibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

        var readingText = stale ? "Stale reading" : battery.State switch
        {
            ReadingState.Current => "Current reading",
            ReadingState.Disconnected => "Disconnected",
            ReadingState.Unsupported => "Unsupported",
            ReadingState.Failed => "Read failed",
            _ => "Unavailable"
        };
        var connection = snapshot.Connection switch
        {
            ConnectionState.Connected => stale ? "Last link: connected" : "Connected",
            ConnectionState.Disconnected => "Disconnected",
            _ => "Connection unknown"
        };
        var charging = battery.Charging switch
        {
            ChargingState.Charging => stale ? "Charging at last sample" : "Charging",
            ChargingState.Discharging => stale ? "Not charging at last sample" : "Not charging",
            ChargingState.Full => stale ? "Fully charged at last sample" : "Fully charged",
            _ => "Charging unknown"
        };

        var status = new List<string>();
        if (!fresh) status.Add(readingText);
        if (!string.Equals(readingText, connection, StringComparison.OrdinalIgnoreCase) || fresh)
            status.Add(connection);
        if (battery.Charging != ChargingState.Unknown) status.Add(charging);
        StatusText = string.Join(" · ", status);

        var lines = new List<string> { Name, $"{VendorName} · {readingText}", connection, charging };
        if (hasPercentage)
            lines.Add(stale ? $"Last measured battery: {battery.Percentage}% (stale)"
                : $"Measured battery: {battery.Percentage}%");
        if (battery.Category is { } category)
            lines.Add(stale ? $"Last reported battery category: {category} (stale)"
                : $"Reported battery category: {category}");
        if (battery.SampledAt is { } sampledAt)
            lines.Add($"Last sample: {sampledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture)}");
        lines.Add($"Estimated remaining runtime: {RuntimeText}");
        if (!string.IsNullOrWhiteSpace(runtimeDetail)) lines.Add(runtimeDetail);
        if (!string.IsNullOrWhiteSpace(battery.Detail)) lines.Add(battery.Detail);
        if (battery.State == ReadingState.Current && !fresh)
            lines.Add("This sample is no longer current. Refresh to request a new reading.");
        if (!string.Equals(Name, snapshot.Device.Name, StringComparison.Ordinal))
            lines.Add($"Device name: {snapshot.Device.Name}");
        ToolTip = string.Join(Environment.NewLine, lines);
        AccessibilityText = $"{Name}, {VendorName} {DeviceKind}, {PercentageText}, {readingText}, {connection}, {charging}, runtime {RuntimeText}";
    }

    internal bool Matches(DeviceSnapshot snapshot, AppSettings settings, DateTimeOffset now, RuntimeEstimate? runtime = null) =>
        identity == snapshot.Device && reading == snapshot.Battery && connectionState == snapshot.Connection &&
        fresh == BatteryPresentation.IsFreshCurrent(snapshot, settings, now) &&
        (RuntimeText, runtimeDetail) == GetRuntimePresentation(snapshot, settings, now, runtime) &&
        string.Equals(Name, BatteryPresentation.GetDisplayName(snapshot.Device, settings), StringComparison.Ordinal);

    private static (string Text, string Detail) GetRuntimePresentation(DeviceSnapshot snapshot, AppSettings settings,
        DateTimeOffset now, RuntimeEstimate? runtime)
    {
        // Presentation has its own freshness guard: an estimate cached by a caller
        // cannot outlive the battery sample or become a category-based estimate.
        if (snapshot.Battery.Percentage is null)
            return ("Unavailable", "A reported battery percentage is required; native categories cannot supply a runtime estimate.");
        if (!BatteryPresentation.IsFreshPercentage(snapshot, settings, now))
            return ("Unavailable", "A fresh timestamped percentage is required; stale readings are excluded.");
        if (snapshot.Connection != ConnectionState.Connected || snapshot.Battery.Charging != ChargingState.Discharging)
            return ("Unavailable", "Runtime requires a confirmed peripheral connection and a known uninterrupted discharge period.");
        return (runtime?.Text ?? "Unavailable", runtime?.Detail ??
            "Runtime requires fresh percentage samples from a known discharging peripheral.");
    }

    private static (string Glyph, string Kind) GetDeviceIcon(PeripheralIdentity device)
    {
        // Use original source identity, never a user's custom name, to choose a device category.
        if (device.Vendor == DeviceVendor.Alienware &&
            string.Equals(device.ModelId, "413C:A528", StringComparison.OrdinalIgnoreCase))
            return ("\uE7F6", "Headset");
        if (device.Vendor == DeviceVendor.Razer)
        {
            if (HasFamily(device.Name, "BlackWidow", "DeathStalker")) return ("\uE765", "Keyboard");
            if (HasFamily(device.Name, "Viper", "DeathAdder", "Basilisk", "Cobra")) return ("\uE962", "Mouse");
        }
        if (device.Vendor == DeviceVendor.Logitech &&
            HasFamily(device.Name, "PRO X SUPERLIGHT", "G PRO Wireless", "G502", "G703", "G903", "G305"))
            return ("\uE962", "Mouse");
        return ("\uE975", "Peripheral");
    }

    private static bool HasFamily(string sourceName, params string[] families) =>
        families.Any(family => sourceName.Contains(family, StringComparison.OrdinalIgnoreCase));

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
