using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeviceBatteryStatus.Core.Alerts;
using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Presentation;
using DeviceBatteryStatus.Core.Settings;
using Forms = System.Windows.Forms;

namespace DeviceBatteryStatus.App;

/// <summary>Edits existing application preferences. Preview uses synthetic data through a callback.</summary>
internal sealed class AlertSettingsWindow : Window
{
    private readonly AppSettings original;
    private readonly Action<AppSettings> save;
    private readonly Action<AppSettings> preview;
    private GamingAlertRule global;
    private readonly Dictionary<string, GamingAlertRule> overrides;
    private readonly Dictionary<string, DeviceSnapshot> devices;
    private readonly CheckBox master = Choice("Enable battery alerts for all devices");
    private readonly ComboBox scope = new() { DisplayMemberPath = "Label", MinHeight = 30 };
    private readonly CheckBox useOverride = Choice("Use custom rules for this device");
    private readonly CheckBox enabled = Choice("Enable alerts for this scope");
    private readonly ComboBox mode = new() { DisplayMemberPath = "Label", MinHeight = 30 };
    private readonly TextBox lowPercent = NumberBox();
    private readonly TextBox criticalPercent = NumberBox();
    private readonly TextBox lowRuntime = NumberBox();
    private readonly TextBox criticalRuntime = NumberBox();
    private readonly CheckBox categories = Choice("Alert on native Low / Critical categories");
    private readonly TextBox duration = NumberBox();
    private readonly ComboBox monitor = new() { DisplayMemberPath = "Label", MinHeight = 30 };
    private readonly CheckBox sound = Choice("Play an alert sound");
    private readonly ComboBox snooze = new() { DisplayMemberPath = "Label", MinHeight = 30 };
    private readonly TextBox cooldown = NumberBox();
    private readonly TextBox recoveryPercent = NumberBox();
    private readonly TextBox recoveryRuntime = NumberBox();
    private readonly TextBlock scopeNote = Note("");
    private readonly TextBlock runtimeNote = Note("");
    private readonly TextBlock error = Note("");
    private readonly StackPanel editor = new();
    private readonly FrameworkElement percentFields;
    private readonly FrameworkElement runtimeFields;
    private ScopeChoice current = new(null, "Global defaults");
    private GamingAlertRule loadedRule = new();
    private bool loading;

    private sealed record ScopeChoice(string? Id, string Label);
    private sealed record ModeChoice(GamingAlertMode Value, string Label);
    private sealed record MonitorChoice(string? Id, string Label);
    private sealed record SnoozeChoice(int Minutes, string Label);

    internal AlertSettingsWindow(AppSettings settings, IReadOnlyList<DeviceSnapshot> snapshots,
        Action<AppSettings> save, Action<AppSettings> preview)
    {
        original = settings.Normalize();
        this.save = save;
        this.preview = preview;
        var configured = GamingAlertSettings.FromLegacy(original).Normalize();
        global = configured.GlobalRule;
        overrides = new(configured.DeviceOverrides, StringComparer.Ordinal);
        devices = snapshots.GroupBy(item => item.Device.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        NameScope.SetNameScope(this, new NameScope());
        Title = "Device Battery Status · Gaming alerts";
        Width = 520; Height = 760; MinWidth = 440; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(8, 8, 8));
        Foreground = Brushes.WhiteSmoke;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };

        Register("AlertMasterBox", master); Register("AlertScopeBox", scope);
        Register("UseDeviceOverrideBox", useOverride); Register("AlertRuleEnabledBox", enabled);
        Register("AlertModeBox", mode); Register("AlertLowPercentBox", lowPercent);
        Register("AlertCriticalPercentBox", criticalPercent); Register("AlertLowRuntimeBox", lowRuntime);
        Register("AlertCriticalRuntimeBox", criticalRuntime); Register("AlertCategoriesBox", categories);
        Register("AlertDurationBox", duration); Register("AlertMonitorBox", monitor);
        Register("AlertSoundBox", sound); Register("AlertSnoozeBox", snooze);
        Register("AlertCooldownBox", cooldown); Register("AlertRecoveryPercentBox", recoveryPercent);
        Register("AlertRecoveryRuntimeBox", recoveryRuntime); Register("AlertErrorText", error);

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Gaming battery alerts", FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44)), Margin = new(0, 0, 0, 12) });
        master.IsChecked = original.LowBatteryNotifications;
        header.Children.Add(master);
        header.Children.Add(Note("One compact alert at a time. Alerts are click-through, do not activate, and dismiss automatically. Sound is off by default."));
        grid.Children.Add(header);

        var content = new StackPanel { Margin = new Thickness(0, 6, 8, 0) };
        content.Children.Add(Field("Apply rules to", scope));
        content.Children.Add(useOverride);
        content.Children.Add(scopeNote);
        editor.Children.Add(enabled);
        editor.Children.Add(Field("Alert triggers", mode));
        percentFields = Field("Low / critical (%)", Pair(lowPercent, criticalPercent));
        runtimeFields = Field("Low / critical (minutes)", Pair(lowRuntime, criticalRuntime));
        editor.Children.Add(percentFields); editor.Children.Add(runtimeFields);
        editor.Children.Add(runtimeNote);
        editor.Children.Add(categories);
        editor.Children.Add(Note("Native categories remain categories. They never become percentages or runtime estimates. Category alerts are independent of the trigger mode."));
        editor.Children.Add(Field("Display duration (seconds)", duration));
        editor.Children.Add(Field("Destination monitor", monitor));
        editor.Children.Add(Note("A missing saved monitor falls back to the primary monitor. Fullscreen desktop presentation can defer an alert; it is rechecked before later display."));
        editor.Children.Add(sound);
        editor.Children.Add(Field("Temporarily snooze", snooze));
        editor.Children.Add(Note("A global snooze pauses device overrides too. Each device can also have its own snooze."));
        editor.Children.Add(Field("Cooldown (minutes)", cooldown));
        editor.Children.Add(Field("Recovery margin (%)", recoveryPercent));
        editor.Children.Add(Field("Runtime recovery (minutes)", recoveryRuntime));
        editor.Children.Add(Note("Low thresholds must exceed critical thresholds. Recovery margins rearm an alert after a genuine recovery; cooldown prevents repeated alerts from small fluctuations. Confirmed charging suppresses alerts."));
        content.Children.Add(editor);
        error.Foreground = Brushes.Gold;
        content.Children.Add(error);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var previewButton = new Button { Content = "Preview synthetic alert", Margin = new(0, 0, 8, 8) };
        Register("AlertPreviewButton", previewButton);
        previewButton.Click += (_, _) => Preview();
        buttons.Children.Add(previewButton);
        var cancelButton = new Button { Content = "Cancel", Margin = new(0, 0, 8, 8) };
        cancelButton.Click += (_, _) => Close(); buttons.Children.Add(cancelButton);
        var saveButton = new Button { Content = "Save", Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44)),
            IsDefault = true, Margin = new(0, 0, 0, 8) };
        Register("AlertSaveButton", saveButton);
        saveButton.Click += (_, _) => Save(); buttons.Children.Add(saveButton);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;

        mode.ItemsSource = new[] { new ModeChoice(GamingAlertMode.Percentage, "Percentage alerts"),
            new ModeChoice(GamingAlertMode.Runtime, "Remaining-runtime alerts"), new ModeChoice(GamingAlertMode.Both, "Percentage and runtime alerts") };
        LoadMonitors();
        var choices = new List<ScopeChoice> { current };
        choices.AddRange(devices.Values.Select(item => new ScopeChoice(item.Device.Id,
            BatteryPresentation.GetDisplayName(item.Device, original))));
        var unavailable = 0;
        choices.AddRange(overrides.Keys.Where(id => !devices.ContainsKey(id))
            .Select(id => new ScopeChoice(id, $"Saved device {++unavailable} (unavailable)")));
        scope.ItemsSource = choices;
        scope.SelectedIndex = 0;
        scope.SelectionChanged += ScopeChanged;
        useOverride.Checked += OverrideChanged; useOverride.Unchecked += OverrideChanged;
        enabled.Checked += (_, _) => UpdateAvailability(); enabled.Unchecked += (_, _) => UpdateAvailability();
        mode.SelectionChanged += (_, _) => UpdateAvailability();
        LoadScope();
    }

    private void LoadMonitors()
    {
        var choices = new List<MonitorChoice> { new(null, "Primary monitor (default)") };
        try
        {
            var index = 0;
            choices.AddRange(Forms.Screen.AllScreens.Select(screen => new MonitorChoice(screen.DeviceName,
                $"Monitor {++index}{(screen.Primary ? " (primary)" : "")} · {screen.Bounds.Width} × {screen.Bounds.Height}")));
        }
        catch (Exception) { /* The primary fallback remains available if display metadata cannot be read. */ }
        monitor.ItemsSource = choices;
    }

    private void LoadScope()
    {
        loading = true;
        try
        {
            var isGlobal = current.Id is null;
            var custom = !isGlobal && overrides.TryGetValue(current.Id!, out _);
            useOverride.Visibility = isGlobal ? Visibility.Collapsed : Visibility.Visible;
            useOverride.IsChecked = custom;
            loadedRule = custom ? overrides[current.Id!] : global;
            enabled.IsChecked = loadedRule.Enabled;
            mode.SelectedItem = mode.Items.Cast<ModeChoice>().First(item => item.Value == loadedRule.Mode);
            lowPercent.Text = Invariant(loadedRule.LowPercentage); criticalPercent.Text = Invariant(loadedRule.CriticalPercentage);
            lowRuntime.Text = Invariant(loadedRule.LowRuntimeMinutes); criticalRuntime.Text = Invariant(loadedRule.CriticalRuntimeMinutes);
            categories.IsChecked = loadedRule.NativeCategoryAlerts; duration.Text = Invariant(loadedRule.DurationSeconds);
            cooldown.Text = Invariant(loadedRule.CooldownMinutes); recoveryPercent.Text = Invariant(loadedRule.RecoveryPercentage);
            recoveryRuntime.Text = Invariant(loadedRule.RecoveryRuntimeMinutes); sound.IsChecked = loadedRule.Sound;
            var monitorChoices = monitor.Items.Cast<MonitorChoice>().ToList();
            if (loadedRule.DestinationMonitor is not null && monitorChoices.All(item => item.Id != loadedRule.DestinationMonitor))
                monitorChoices.Add(new(loadedRule.DestinationMonitor, "Saved monitor unavailable (primary fallback)"));
            monitor.ItemsSource = monitorChoices;
            monitor.SelectedItem = monitorChoices.First(item => item.Id == loadedRule.DestinationMonitor);
            var snoozes = new List<SnoozeChoice> { new(0, "Off"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "60 minutes") };
            if (loadedRule.SnoozedUntil is { } until && until > DateTimeOffset.UtcNow)
                snoozes.Add(new(-1, $"Keep until {until.ToLocalTime():HH:mm} (saved)"));
            snooze.ItemsSource = snoozes;
            snooze.SelectedIndex = snoozes.Count > 4 ? 4 : 0;
        }
        finally { loading = false; }
        UpdateAvailability();
    }

    private void ScopeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loading || scope.SelectedItem is not ScopeChoice selected || selected == current) return;
        if (!CaptureRule())
        {
            loading = true; scope.SelectedItem = current; loading = false;
            return;
        }
        current = selected; error.Text = ""; LoadScope();
    }

    private void OverrideChanged(object sender, RoutedEventArgs args)
    {
        if (loading || current.Id is null) return;
        if (useOverride.IsChecked == true && !overrides.ContainsKey(current.Id) && overrides.Count >= 64)
        {
            loading = true; useOverride.IsChecked = false; loading = false;
            error.Text = "Up to 64 devices can have custom alert rules. Remove an existing override first.";
            return;
        }
        if (useOverride.IsChecked == true) overrides[current.Id] = global with { SnoozedUntil = null };
        else overrides.Remove(current.Id);
        LoadScope();
    }

    private void UpdateAvailability()
    {
        if (loading) return;
        var editable = current.Id is null || useOverride.IsChecked == true;
        editor.IsEnabled = editable;
        var categoryOnly = current.Id is not null && devices.TryGetValue(current.Id, out var device) &&
            device.Battery.Category is not null && device.Battery.Percentage is null;
        var chosen = (mode.SelectedItem as ModeChoice)?.Value ?? GamingAlertMode.Percentage;
        percentFields.IsEnabled = !categoryOnly && chosen != GamingAlertMode.Runtime;
        runtimeFields.IsEnabled = !categoryOnly && chosen != GamingAlertMode.Percentage;
        mode.IsEnabled = !categoryOnly;
        scopeNote.Text = current.Id is null ? "Global defaults apply to devices without custom rules. The master switch above disables every device."
            : editable ? "Custom rules for this device. Global snoozing and the master switch still apply."
            : "This device inherits the global defaults. Enable custom rules to change them.";
        runtimeNote.Text = categoryOnly
            ? "This device currently reports a native category. Percentage and runtime controls are unavailable for those readings; native Low/Critical alerts remain available."
            : "Runtime thresholds require a fresh estimate with sufficient confidence. Percentage alerts still work when enabled, and native category alerts are independent of this mode. Runtime uses existing samples without extra device queries.";
    }

    private bool CaptureRule()
    {
        if (current.Id is not null && useOverride.IsChecked != true) { overrides.Remove(current.Id); return true; }
        if (!TryNumber(lowPercent, "Low battery threshold", out var low) ||
            !TryNumber(criticalPercent, "Critical battery threshold", out var critical) ||
            !TryNumber(lowRuntime, "Low runtime threshold", out var runtimeLow) ||
            !TryNumber(criticalRuntime, "Critical runtime threshold", out var runtimeCritical) ||
            !TryNumber(duration, "Display duration", out var seconds) || !TryNumber(cooldown, "Cooldown", out var minutes) ||
            !TryNumber(recoveryPercent, "Percentage recovery margin", out var percentageMargin) ||
            !TryNumber(recoveryRuntime, "Runtime recovery margin", out var runtimeMargin)) return false;
        var selectedSnooze = (snooze.SelectedItem as SnoozeChoice)?.Minutes ?? 0;
        var rule = loadedRule with
        {
            Enabled = enabled.IsChecked == true, Mode = (mode.SelectedItem as ModeChoice)?.Value ?? GamingAlertMode.Percentage,
            LowPercentage = low, CriticalPercentage = critical, LowRuntimeMinutes = runtimeLow, CriticalRuntimeMinutes = runtimeCritical,
            NativeCategoryAlerts = categories.IsChecked == true, DurationSeconds = seconds,
            DestinationMonitor = (monitor.SelectedItem as MonitorChoice)?.Id, Sound = sound.IsChecked == true,
            SnoozedUntil = selectedSnooze < 0 ? loadedRule.SnoozedUntil : selectedSnooze == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(selectedSnooze),
            CooldownMinutes = minutes, RecoveryPercentage = percentageMargin, RecoveryRuntimeMinutes = runtimeMargin
        };
        if (!rule.TryValidate(out var message)) { error.Text = message; return false; }
        if (current.Id is null) global = rule;
        else overrides[current.Id] = rule;
        loadedRule = rule;
        return true;
    }

    private AppSettings? Collect()
    {
        if (!CaptureRule()) return null;
        return (original with
        {
            LowBatteryNotifications = master.IsChecked == true,
            LowBatteryThreshold = global.LowPercentage,
            GamingAlerts = new GamingAlertSettings { GlobalRule = global,
                DeviceOverrides = new Dictionary<string, GamingAlertRule>(overrides, StringComparer.Ordinal) }
        }).Normalize();
    }

    private void Save()
    {
        var updated = Collect(); if (updated is null) return;
        try { save(updated); Close(); }
        catch (Exception exception) { error.Text = $"Alert preferences were not saved. {exception.Message}"; }
    }

    private void Preview()
    {
        var updated = Collect(); if (updated is null) return;
        var configured = GamingAlertSettings.FromLegacy(updated);
        var previewRule = current.Id is null ? configured.GlobalRule : configured.ForDevice(current.Id);
        // The preview callback reads GlobalRule. Project the selected scope into an unsaved copy;
        // changing scopes must never replace the actual saved global defaults with an override.
        var previewSettings = updated with { GamingAlerts = configured with { GlobalRule = previewRule } };
        try { preview(previewSettings); error.Text = "Synthetic preview requested for this scope. Preferences have not been saved."; }
        catch (Exception exception) { error.Text = $"The synthetic preview could not be shown. {exception.Message}"; }
    }

    private bool TryNumber(TextBox box, string label, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
        error.Text = $"{label} must be a whole number."; box.Focus(); return false;
    }

    private void Register(string name, FrameworkElement element)
    {
        element.Name = name; RegisterName(name, element);
        System.Windows.Automation.AutomationProperties.SetName(element, name);
    }
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static CheckBox Choice(string text) => new() { Content = text, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 0, 0, 10) };
    private static TextBox NumberBox() => new() { Width = 76, MaxLength = 4, HorizontalAlignment = HorizontalAlignment.Left };
    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap,
        FontSize = 11, Margin = new Thickness(0, 0, 0, 12) };
    private static FrameworkElement Pair(FrameworkElement low, FrameworkElement critical)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(low); panel.Children.Add(new TextBlock { Text = " / ", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(critical); return panel;
    }
    private static FrameworkElement Field(string label, FrameworkElement element)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(185) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Silver, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(element, 1); row.Children.Add(element); return row;
    }
}
