using DeviceBatteryStatus.Core.Models;
using DeviceBatteryStatus.Core.Runtime;

namespace DeviceBatteryStatus.Core.Alerts;

/// <summary>A current alert candidate; creating or queueing it does not consume its delivery state.</summary>
public sealed record GamingBatteryAlert(string DeviceId, string Name, int? Percentage, BatteryCategory? Category,
    string RuntimeText, GamingAlertSeverity Severity, GamingAlertRule Rule,
    IReadOnlyList<GamingAlertCondition> Conditions, DateTimeOffset CreatedAt)
{
    public RuntimeEstimate? Runtime { get; init; }
    public bool IsPreview { get; init; }
    internal IReadOnlyDictionary<GamingAlertCondition, GamingAlertSeverity> Signals { get; init; } =
        new Dictionary<GamingAlertCondition, GamingAlertSeverity>();
}
