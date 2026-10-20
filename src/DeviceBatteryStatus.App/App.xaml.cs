using System.Windows;

namespace DeviceBatteryStatus.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private ApplicationController? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        controller?.StopImmediately();
        if (instanceMutex is not null)
        {
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
