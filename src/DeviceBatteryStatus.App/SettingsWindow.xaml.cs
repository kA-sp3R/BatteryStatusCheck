using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings original;
    private readonly Action<AppSettings, bool> save;
    private readonly bool initialStartup;
    private readonly List<(string Id, TextBox Editor)> names = [];

    public SettingsWindow(AppSettings settings, IReadOnlyList<DeviceSnapshot> devices, bool canRegister,
        bool startupEnabled, string? startupError, Action<AppSettings, bool> save)
    {
        InitializeComponent();
        original = settings;
        this.save = save;
        initialStartup = startupEnabled;
        IntervalBox.Text = settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        ThresholdBox.Text = settings.LowBatteryThreshold.ToString(CultureInfo.InvariantCulture);
        NotifyBox.IsChecked = settings.LowBatteryNotifications;
        StartupBox.IsChecked = startupEnabled;
        StartupBox.IsEnabled = canRegister && startupError is null;
        StartupNote.Text = startupError ?? (canRegister ? "Off unless you enable it here." : "Available when running DeviceBatteryStatus.exe.");

        var choices = new List<DeviceChoice> { new(null, "Lowest valid battery") };
        choices.AddRange(devices.Select(device => new DeviceChoice(device.Device.Id,
            BatteryPresentation.GetDisplayName(device.Device, settings))));
        if (settings.PreferredDeviceId is not null && choices.All(choice => choice.Id != settings.PreferredDeviceId))
            choices.Add(new(settings.PreferredDeviceId, "Preferred device (unavailable)"));
        PreferredBox.ItemsSource = choices;
        PreferredBox.SelectedItem = choices.First(choice => choice.Id == settings.PreferredDeviceId);

        var identities = devices.Select(device => device.Device).ToDictionary(device => device.Id, StringComparer.Ordinal);
        foreach (var id in identities.Keys.Concat(settings.CustomNames.Keys).Distinct(StringComparer.Ordinal))
        {
            identities.TryGetValue(id, out var identity);
            var label = new TextBlock
            {
                Text = identity?.Name ?? "Saved device (unavailable)",
                Foreground = System.Windows.Media.Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var editor = new TextBox
            {
                Text = settings.CustomNames.GetValueOrDefault(id) ?? "",
                MaxLength = 120,
                ToolTip = "Optional custom name. Leave empty to use the device name.",
                Margin = new Thickness(0, 0, 0, 12)
            };
            System.Windows.Automation.AutomationProperties.SetName(editor, $"Custom name for {label.Text}");
            AliasPanel.Children.Add(label);
            AliasPanel.Children.Add(editor);
            names.Add((id, editor));
        }
        EmptyNames.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval is < 10 or > 3600)
        {
            ErrorText.Text = "Choose a refresh interval from 10 to 3600 seconds.";
            IntervalBox.Focus();
            return;
        }
        if (!int.TryParse(ThresholdBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) || threshold is < 1 or > 50)
        {
            ErrorText.Text = "Choose a low-battery threshold from 1% to 50%.";
            ThresholdBox.Focus();
            return;
        }
        var customNames = names.ToDictionary(entry => entry.Id, entry => entry.Editor.Text, StringComparer.Ordinal);
        var updated = original with
        {
            RefreshIntervalSeconds = interval,
            LowBatteryNotifications = NotifyBox.IsChecked == true,
            LowBatteryThreshold = threshold,
            PreferredDeviceId = (PreferredBox.SelectedItem as DeviceChoice)?.Id,
            StartWithWindows = StartupBox.IsEnabled ? StartupBox.IsChecked == true : original.StartWithWindows,
            CustomNames = customNames
        };
        var changeStartup = StartupBox.IsEnabled && (StartupBox.IsChecked == true) != initialStartup;
        try { save(updated.Normalize(), changeStartup); Close(); }
        catch (Exception exception) { ErrorText.Text = $"Settings were not applied. {exception.Message}"; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    public void ShowNotice(string message) => ErrorText.Text = message;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private sealed record DeviceChoice(string? Id, string Name);
}
