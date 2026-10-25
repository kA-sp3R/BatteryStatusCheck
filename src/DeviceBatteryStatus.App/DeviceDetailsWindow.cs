using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeviceBatteryStatus.Core.Health;
using DeviceBatteryStatus.Core.Models;

namespace DeviceBatteryStatus.App;

/// <summary>Capacity configuration and retained evidence; it never queries a device.</summary>
internal sealed class DeviceDetailsWindow : Window
{
    private readonly CapacityHealthService health;
    private readonly Action<Exception> reportError;
    private DeviceSnapshot snapshot;
    private DeviceSnapshot? displayedSnapshot;
    private long displayedRevision = long.MinValue;
    internal int PresentationUpdateCount { get; private set; }
    private readonly TextBlock deviceName = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock capacityStatus = new() { FontWeight = FontWeights.SemiBold, Foreground = Brushes.Gold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock explanation = new() { Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock designText = ValueText();
    private readonly TextBlock fullChargeText = ValueText();
    private readonly TextBlock healthText = ValueText();
    private readonly TextBlock measurementDate = ValueText();
    private readonly TextBlock coverage = ValueText();
    private readonly TextBlock identity = ValueText();
    private readonly TextBlock history = ValueText();
    private readonly TextBox designValue = new() { MaxLength = 20, MinWidth = 90 };
    private readonly ComboBox designUnit = new() { MinHeight = 28, DisplayMemberPath = "Label" };
    private readonly ComboBox designBasis = new() { MinHeight = 28, DisplayMemberPath = "Label" };
    private readonly TextBox designSource = new() { MaxLength = 256, TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
    private readonly TextBlock error = new() { Foreground = Brushes.Gold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private sealed record UnitChoice(CapacityUnit Value, string Label);
    private sealed record BasisChoice(CapacityBasis Value, string Label);

    internal DeviceDetailsWindow(DeviceSnapshot snapshot, CapacityHealthService health, Action<Exception> reportError)
    {
        this.snapshot = snapshot;
        this.health = health;
        this.reportError = reportError;
        NameScope.SetNameScope(this, new NameScope());
        Title = "Device Battery Status · Device details";
        Width = 470;
        Height = 690;
        MinWidth = 410;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(8, 8, 8));
        Foreground = Brushes.WhiteSmoke;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { Close(); args.Handled = true; } };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Device details", FontSize = 22, Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44)), Margin = new(0, 0, 0, 10) });
        panel.Children.Add(deviceName);
        panel.Children.Add(new TextBlock { Text = "Capacity health is independent of the current battery percentage.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 14) });
        panel.Children.Add(capacityStatus);
        explanation.Margin = new Thickness(0, 5, 0, 12);
        panel.Children.Add(explanation);
        panel.Children.Add(Row("Design capacity", designText));
        panel.Children.Add(Row("Full-charge capacity", fullChargeText));
        panel.Children.Add(Row("Capacity health", healthText));
        panel.Children.Add(Row("Measurement date", measurementDate));
        panel.Children.Add(Row("Coverage / method", coverage));
        identity.Margin = new Thickness(0, 5, 0, 12);
        panel.Children.Add(identity);

        panel.Children.Add(new TextBlock { Text = "Design capacity", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44)), Margin = new(0, 5, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Enter an original design specification. Match both units and battery basis to the measured capacity; percentage, voltage and runtime alone cannot supply capacity.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 8) });
        designUnit.ItemsSource = new[] { new UnitChoice(CapacityUnit.MilliampHours, "mAh"), new UnitChoice(CapacityUnit.WattHours, "Wh") };
        designBasis.ItemsSource = new[] { new BasisChoice(CapacityBasis.BatteryPack, "Whole pack at battery terminals"),
            new BasisChoice(CapacityBasis.SingleCell, "Single cell at cell terminals"), new BasisChoice(CapacityBasis.LoadOutput, "Device load output") };
        designUnit.SelectedIndex = 0;
        designBasis.SelectedIndex = 0;
        panel.Children.Add(EditorRow("Value", designValue));
        panel.Children.Add(EditorRow("Units", designUnit));
        panel.Children.Add(EditorRow("Basis", designBasis));
        panel.Children.Add(EditorRow("Source / reference", designSource));
        var actions = new WrapPanel { Margin = new(0, 7, 0, 0) };
        var save = new Button { Content = "Save design capacity", Margin = new(0, 0, 8, 5) };
        var clear = new Button { Content = "Clear design", Margin = new(0, 0, 0, 5) };
        save.Click += (_, _) => SaveDesign();
        clear.Click += (_, _) => ClearDesign();
        actions.Children.Add(save);
        actions.Children.Add(clear);
        panel.Children.Add(actions);
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock { Text = "Measurement history", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44)), Margin = new(0, 14, 0, 7) });
        panel.Children.Add(new TextBlock { Text = "Retained evidence can include earlier battery identities. Only a matching complete measurement is applied to current health.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(history);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Register("DesignValueBox", designValue);
        Register("DesignUnitBox", designUnit);
        Register("DesignBasisBox", designBasis);
        Register("DesignSourceBox", designSource);
        Register("SaveDesignButton", save);
        Register("ClearDesignButton", clear);
        Register("CapacityStatusText", capacityStatus);
        Register("ExplanationText", explanation);
        Register("DesignCapacityText", designText);
        Register("FullChargeCapacityText", fullChargeText);
        Register("HealthPercentageText", healthText);
        Register("MeasurementDateText", measurementDate);
        Register("CoverageText", coverage);
        Register("HistoryText", history);
        Register("DesignErrorText", error);
        LoadDesignEditor();
        Update(snapshot);
    }

    internal void Update(DeviceSnapshot value)
    {
        Dispatcher.VerifyAccess();
        if (!string.Equals(value.Device.Id, snapshot.Device.Id, StringComparison.Ordinal))
            throw new ArgumentException("A device-details window cannot switch battery identities.", nameof(value));
        var revision = health.Revision;
        // Current adapters expose no capacity telemetry. Reuse that unchanged
        // display until the ledger changes, including design/history/storage errors.
        // Telemetry-capable paths still run the service's clock-sensitive validation
        // on freshness ticks, so cached views cannot keep expired capacity current.
        if (value.CapacityTelemetry is null && displayedSnapshot == value && displayedRevision == revision)
        {
            snapshot = value;
            return;
        }
        snapshot = value;
        var details = health.GetDetails(value);
        displayedSnapshot = value;
        displayedRevision = revision;
        PresentationUpdateCount++;
        SetText(deviceName, value.Device.Name);
        SetText(capacityStatus, details.Status);
        SetText(explanation, details.Explanation);
        SetText(designText, details.Design is { } design
            ? $"{Format(design.Value, design.Unit)} · {BasisLabel(design.Basis)}\n{SourceLabel(design.SourceKind)}: {design.Source}"
            : "Not supplied");
        var measurement = details.LatestMeasurement;
        SetText(fullChargeText, measurement?.FullChargeCapacity is { } full
            ? $"{Format(full, measurement.Unit)} · {BasisLabel(measurement.Basis)}\n{MethodLabel(measurement.Method)}; {measurement.Source}"
            : "Unavailable — no accepted full-capacity measurement");
        SetText(healthText, details.HealthPercentage is { } ratio
            ? $"{ratio.ToString(ratio is > 0 and < 0.1m ? "G29" : "0.#", CultureInfo.CurrentCulture)}%" : "Unavailable");
        SetText(measurementDate, measurement is not null
            ? measurement.MeasuredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.CurrentCulture) : "No accepted measurement");
        SetText(coverage, measurement is null ? "No complete measurement"
            : measurement.Method == CapacityMeasurementMethod.DeviceReported
                ? $"Device-reported full-charge capacity · {measurement.SampleCount} sample(s). No discharge-cycle coverage was measured."
                : $"{MethodLabel(measurement.Method)} · {measurement.SampleCount} sample(s)\n" +
              $"{measurement.ObservedDuration.TotalHours.ToString("0.##", CultureInfo.CurrentCulture)} h observed; " +
              (measurement.CoverageFraction is { } fraction ? $"{(fraction * 100).ToString("0.#", CultureInfo.CurrentCulture)}% coverage" : "coverage not established") +
              (string.IsNullOrWhiteSpace(measurement.Interruption) ? "; no interruptions" : $"; {measurement.Interruption}"));
        SetText(identity, details.IdentityVerified ? "Capacity telemetry identifies the current battery."
            : "Current battery identity is not verified. Retained measurements are not applied to an uncertain identity.");
        SetText(history, details.History.Count == 0 ? "No measurements recorded. Incomplete cycles never become full-charge capacity."
            : string.Join(Environment.NewLine + Environment.NewLine, details.History.Take(12).Select(item =>
                $"{item.MeasuredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)} · " +
                (item.Complete && item.FullChargeCapacity is { } capacity
                    ? $"{(item == measurement ? "Current full-capacity evidence" : "Retained complete capacity")}: {Format(capacity, item.Unit)}"
                    : "Incomplete — excluded from health") +
                $"\n{MethodLabel(item.Method)} · {BasisLabel(item.Basis)} · {item.SampleCount} sample(s)" +
                (item.Method == CapacityMeasurementMethod.DeviceReported ? "\nFull-charge field; no measured discharge cycle."
                    : $"\n{item.ObservedDuration.TotalHours.ToString("0.##", CultureInfo.CurrentCulture)} h observed; " +
                      (item.CoverageFraction is { } observedFraction ? $"{(observedFraction * 100).ToString("0.#", CultureInfo.CurrentCulture)}% cycle coverage"
                          : "full-cycle coverage unknown") +
                      (!item.Complete && item.ObservedDischargeCapacity is { } partial ? $"; partial discharge {Format(partial, item.Unit)}" : "")) +
                (string.IsNullOrWhiteSpace(item.Interruption) ? "" : $"\nInterrupted: {item.Interruption}"))) +
              (details.History.Count > 12 ? $"\n\n{details.History.Count - 12} older entries also retained." : ""));
        if (!string.IsNullOrWhiteSpace(details.StorageError)) SetText(error, details.StorageError);
    }

    private void LoadDesignEditor()
    {
        var design = health.GetDetails(snapshot).Design;
        designValue.Text = design?.Value.ToString(CultureInfo.CurrentCulture) ?? "";
        designSource.Text = design?.Source ?? "";
        designUnit.SelectedItem = designUnit.Items.Cast<UnitChoice>().First(choice => choice.Value == (design?.Unit ?? CapacityUnit.MilliampHours));
        designBasis.SelectedItem = designBasis.Items.Cast<BasisChoice>().First(choice => choice.Value == (design?.Basis ?? CapacityBasis.BatteryPack));
    }

    private void SaveDesign()
    {
        if (!decimal.TryParse(designValue.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value <= 0)
        {
            SetText(error, "Enter a positive design capacity in the selected units.");
            return;
        }
        if (string.IsNullOrWhiteSpace(designSource.Text))
        {
            SetText(error, "Record the specification or source for the original design capacity.");
            return;
        }
        try
        {
            var unit = ((UnitChoice)designUnit.SelectedItem).Value;
            var basis = ((BasisChoice)designBasis.SelectedItem).Value;
            health.SetDesignCapacity(snapshot, new(value, unit, basis, designSource.Text.Trim(), CapacitySourceKind.UserEntered, DateTimeOffset.UtcNow));
            SetText(error, "Design capacity saved. It does not create a full-charge measurement.");
            displayedRevision = long.MinValue;
            Update(snapshot);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ClearDesign()
    {
        try
        {
            health.SetDesignCapacity(snapshot, null);
            LoadDesignEditor();
            SetText(error, "Design capacity cleared; measurement history is retained.");
            displayedRevision = long.MinValue;
            Update(snapshot);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception)
    {
        SetText(error, $"Capacity preferences could not be saved: {exception.Message}");
        reportError(exception);
    }

    private void Register(string name, FrameworkElement element)
    {
        element.Name = name;
        RegisterName(name, element);
    }

    private static TextBlock ValueText() => new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private static void SetText(TextBlock element, string value) { if (!string.Equals(element.Text, value, StringComparison.Ordinal)) element.Text = value; }
    private static string Format(decimal value, CapacityUnit unit) =>
        $"{value.ToString(value is > 0 and < 0.001m ? "G29" : "0.###", CultureInfo.CurrentCulture)} {(unit == CapacityUnit.MilliampHours ? "mAh" : "Wh")}";
    private static string BasisLabel(CapacityBasis basis) => basis switch
    {
        CapacityBasis.BatteryPack => "whole pack at battery terminals",
        CapacityBasis.SingleCell => "single cell at cell terminals",
        _ => "device load output"
    };
    private static string SourceLabel(CapacitySourceKind source) => source switch { CapacitySourceKind.DocumentedSpecification => "Specification", CapacitySourceKind.DeviceData => "Device data", _ => "User-entered source" };
    private static string MethodLabel(CapacityMeasurementMethod method) => method switch { CapacityMeasurementMethod.DeviceReported => "Device-reported full-charge field", CapacityMeasurementMethod.CurrentIntegration => "Calculated from discharge current", _ => "Calculated from discharge power" };

    private static Grid Row(string label, FrameworkElement value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Silver, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static Grid EditorRow(string label, FrameworkElement editor) => Row(label, editor);
}
