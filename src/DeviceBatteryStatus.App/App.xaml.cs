using System.Windows;

namespace DeviceBatteryStatus.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private ApplicationController? controller;
    internal Action? AfterShutdown { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Retired CI invocations must fail closed instead of starting real
        // monitoring. Synthetic checks now live only in the separate harness.
        if (e.Args.Contains("--software-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(2);
            return;
        }
        instanceMutex = new Mutex(true, @"Local\DeviceBatteryStatus", out var firstInstance);
        if (!firstInstance)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            Shutdown();
            return;
        }
        try
        {
            controller = new ApplicationController(this);
            controller.Start(showPopup: !e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            controller?.StopImmediately();
            MessageBox.Show($"Device Battery Status could not start.\n\n{exception.Message}",
                "Device Battery Status", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var afterShutdown = AfterShutdown;
        AfterShutdown = null;
        try { controller?.StopImmediately(); }
        catch (Exception cleanupError)
        {
            // A failed shell cleanup must not retain the single-instance mutex or
            // prevent a deliberately queued installer from starting after exit.
            System.Diagnostics.Trace.WriteLine($"Application cleanup failed ({cleanupError.GetType().Name}).");
        }
        finally
        {
            try
            {
                if (instanceMutex is not null)
                {
                    instanceMutex.ReleaseMutex();
                    instanceMutex.Dispose();
                    instanceMutex = null;
                }
            }
            finally { base.OnExit(e); }
        }
        try { afterShutdown?.Invoke(); }
        catch
        {
            MessageBox.Show("The update installer could not start. Open Updates and try again.",
                "Device Battery Status", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
