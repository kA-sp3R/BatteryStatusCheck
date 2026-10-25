using System.Windows.Threading;
using DeviceBatteryStatus.Core.Alerts;

namespace DeviceBatteryStatus.App.Alerts;

/// <summary>One ordinary desktop alert at a time; retained events are checked again immediately before display.</summary>
internal sealed class GamingAlertPresenter : IDisposable
{
    private const int MaximumPending = 32;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private readonly IGamingAlertEnvironment environment;
    private readonly Action<Exception>? reportError;
    private readonly Dictionary<string, PendingAlert> pending = new(StringComparer.Ordinal);
    private readonly DispatcherTimer dismissTimer;
    private DateTimeOffset activeUntil;
    private bool disposed;
    internal GamingBatteryAlert? ActiveCandidate { get; private set; }
    internal GamingAlertWindow? ActiveWindow { get; private set; }
    internal int PendingCount => pending.Count;
    internal event Action<GamingBatteryAlert, DateTimeOffset>? Shown;

    internal GamingAlertPresenter(IGamingAlertEnvironment? environment = null, Action<Exception>? reportError = null)
    {
        this.environment = environment ?? new GamingAlertEnvironment();
        this.reportError = reportError;
        dismissTimer = new DispatcherTimer(DispatcherPriority.Background);
        dismissTimer.Tick += DismissElapsed;
    }

    internal void Queue(GamingBatteryAlert candidate, DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        if (pending.TryGetValue(candidate.DeviceId, out var existing))
        {
            // Updating a queued reading must not extend its retention indefinitely.
            pending[candidate.DeviceId] = existing with { Candidate = candidate };
            return;
        }
        if (pending.Count >= MaximumPending)
        {
            var oldest = pending.OrderBy(pair => pair.Value.Candidate.Severity)
                .ThenBy(pair => pair.Value.QueuedAt).First();
            if (oldest.Value.Candidate.Severity > candidate.Severity) return;
            pending.Remove(oldest.Key);
        }
        pending[candidate.DeviceId] = new(candidate, now);
    }

    internal void Tick(DateTimeOffset now, Func<GamingBatteryAlert, GamingBatteryAlert?> revalidate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(revalidate);
        try
        {
            foreach (var item in pending.ToArray())
            {
                if (now < item.Value.QueuedAt || now - item.Value.QueuedAt >= Retention)
                    pending.Remove(item.Key);
            }
            if (ActiveCandidate is { } active)
            {
                if (now >= activeUntil || now < activeUntil - TimeSpan.FromSeconds(30)) CloseActive();
                else if (!active.IsPreview)
                {
                    var current = revalidate(active);
                    if (current is null) CloseActive();
                    else
                    {
                        var destination = environment.ResolveMonitor(current.Rule.DestinationMonitor);
                        if (environment.ShouldDefer(destination))
                        {
                            // Ordinary desktop windows cannot reliably cover exclusive fullscreen.
                            // Keep a still-relevant event for a later desktop opportunity.
                            Queue(current, now);
                            CloseActive();
                        }
                        else UpdateActive(current, destination, now);
                    }
                }
            }

            // Preview is deliberately independent of real notification preferences and delivery bookkeeping.
            if (ActiveCandidate?.IsPreview == true) return;
            var candidates = new List<(PendingAlert Pending, GamingBatteryAlert Current, AlertMonitor Monitor)>();
            foreach (var item in pending.ToArray())
            {
                var current = revalidate(item.Value.Candidate);
                if (current is null) { pending.Remove(item.Key); continue; }
                var destination = environment.ResolveMonitor(current.Rule.DestinationMonitor);
                if (!environment.ShouldDefer(destination)) candidates.Add((item.Value, current, destination));
            }
            foreach (var choice in candidates.OrderByDescending(item => item.Current.Severity)
                .ThenBy(item => item.Pending.QueuedAt))
            {
                if (ActiveCandidate is { } visible)
                {
                    if (visible.DeviceId == choice.Current.DeviceId)
                    {
                        pending.Remove(choice.Current.DeviceId);
                        UpdateActive(choice.Current, choice.Monitor, now);
                        continue;
                    }
                    if (choice.Current.Severity <= visible.Severity) continue;
                    CloseActive();
                }
                if (Show(choice.Current, choice.Monitor, now)) pending.Remove(choice.Current.DeviceId);
                break;
            }
        }
        catch (Exception exception) { Report(exception); }
    }

    internal void Preview(GamingBatteryAlert synthetic, DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(synthetic);
        try
        {
            CloseActive();
            var candidate = synthetic with { IsPreview = true };
            Show(candidate, environment.ResolveMonitor(candidate.Rule.DestinationMonitor), now);
        }
        catch (Exception exception) { Report(exception); }
    }

    internal void Clear()
    {
        pending.Clear();
        CloseActive();
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear();
        dismissTimer.Tick -= DismissElapsed;
        disposed = true;
    }

    private bool Show(GamingBatteryAlert candidate, AlertMonitor destination, DateTimeOffset now)
    {
        GamingAlertWindow? window = null;
        try
        {
            window = new GamingAlertWindow(candidate, destination);
            window.ShowWithoutActivation();
            ActiveWindow = window;
            ActiveCandidate = candidate;
            window.Closed += WindowClosed;
            RestartDismiss(candidate, now);
            NotifyShown(candidate, now);
            if (candidate.Rule.Sound) PlaySound();
            return true;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(ActiveWindow, window)) CloseActive();
            else window?.Close();
            Report(exception);
            return false;
        }
    }

    private void UpdateActive(GamingBatteryAlert candidate, AlertMonitor destination, DateTimeOffset now)
    {
        if (ActiveCandidate is not { } previous || ActiveWindow is null) return;
        var escalation = candidate.Severity > previous.Severity;
        var newCondition = candidate.Conditions.Except(previous.Conditions).Any();
        ActiveWindow.UpdateCandidate(candidate, destination);
        ActiveCandidate = candidate;
        // Identical events and changing percentages never prolong the current alert.
        // A critical escalation receives a fresh display duration.
        if (escalation) RestartDismiss(candidate, now);
        if (escalation || newCondition) NotifyShown(candidate, now);
        if (escalation && candidate.Rule.Sound) PlaySound();
    }

    private void NotifyShown(GamingBatteryAlert candidate, DateTimeOffset now)
    {
        if (candidate.IsPreview) return;
        try { Shown?.Invoke(candidate, now); }
        catch (Exception exception) { Report(exception); }
    }
    private void PlaySound()
    {
        try { environment.PlaySound(); }
        catch (Exception exception) { Report(exception); }
    }

    private void RestartDismiss(GamingBatteryAlert candidate, DateTimeOffset now)
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(candidate.Rule.DurationSeconds, 3, 30));
        activeUntil = now + duration;
        dismissTimer.Stop();
        dismissTimer.Interval = duration;
        dismissTimer.Start();
    }

    private void DismissElapsed(object? sender, EventArgs arguments) => CloseActive();
    private void WindowClosed(object? sender, EventArgs arguments)
    {
        if (!ReferenceEquals(sender, ActiveWindow)) return;
        dismissTimer.Stop();
        ActiveWindow = null;
        ActiveCandidate = null;
    }
    private void CloseActive()
    {
        dismissTimer.Stop();
        var window = ActiveWindow;
        ActiveWindow = null;
        ActiveCandidate = null;
        if (window is not null)
        {
            window.Closed -= WindowClosed;
            window.Close();
        }
    }
    private void Report(Exception exception)
    {
        try { reportError?.Invoke(exception); }
        catch { /* Diagnostics must never turn a transient desktop failure into an app failure. */ }
    }
    private sealed record PendingAlert(GamingBatteryAlert Candidate, DateTimeOffset QueuedAt);
}
