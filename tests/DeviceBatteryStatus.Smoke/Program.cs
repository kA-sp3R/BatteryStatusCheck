using System.Windows;
using DeviceBatteryStatus.App.Testing;

namespace DeviceBatteryStatus.Smoke;

/// <summary>Separate synthetic CI process; production App startup is never invoked.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0])) return 2;
        try
        {
            // This is deliberately a plain Application, never DeviceBatteryStatus.App.App.
            // The resource dictionary is loaded from the exact packaged application DLL.
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DeviceBatteryStatus;component/Themes/Controls.xaml", UriKind.Absolute)
            });
            application.Startup += async (_, _) =>
            {
                var result = await SoftwareSmokeTest.RunAsync(args[0]);
                application.Shutdown(result);
            };
            return application.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Separate synthetic harness failed ({exception.GetType().Name}): {exception.Message}");
            return 1;
        }
    }
}
