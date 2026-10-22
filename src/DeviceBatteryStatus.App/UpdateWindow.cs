using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Updates;

namespace DeviceBatteryStatus.App;

/// <summary>Checking, downloading and installing are separate user actions.</summary>
internal sealed class UpdateWindow : Window
{
    private readonly GitHubUpdateService service;
    private readonly string currentVersion;
    private readonly Func<UpdateCheckMode> getMode;
    private readonly Action<StagedInstaller> install;
    private readonly Action<Exception> reportError;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 16) };
    private readonly Button check = new() { Content = "Check now", Margin = new(0, 0, 8, 8) };
    private readonly Button download = new() { Content = "Download and verify", Margin = new(0, 0, 8, 8) };
    private readonly Button installButton = new() { Content = "Install verified update", Margin = new(0, 0, 8, 8) };
    private AvailableUpdate? available;
    private StagedInstaller? staged;
    private bool busy;
    private bool closed;

    public UpdateWindow(GitHubUpdateService service, string currentVersion, Func<UpdateCheckMode> getMode,
        Action<StagedInstaller> install, Action<Exception> reportError, AvailableUpdate? known = null)
    {
        this.service = service;
        this.currentVersion = currentVersion;
        this.getMode = getMode;
        this.install = install;
        this.reportError = reportError;
        token = lifetime.Token;
        available = known;
        Title = "Device Battery Status · Updates";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(8, 8, 8));
        Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Updates", FontSize = 22, Foreground = new SolidColorBrush(Color.FromRgb(68, 214, 44)) });
        panel.Children.Add(new TextBlock { Text = $"Current version: {currentVersion}", Margin = new(0, 10, 0, 0) });
        panel.Children.Add(status);
        var buttons = new WrapPanel();
        buttons.Children.Add(check);
        buttons.Children.Add(download);
        buttons.Children.Add(installButton);
        panel.Children.Add(buttons);
        var releasePage = new Button { Content = "Open release page in browser", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 12) };
        releasePage.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo((available?.ReleasePage ?? GitHubUpdateService.ReleasesPage).AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception exception) { ShowFailure(exception); }
        };
        panel.Children.Add(releasePage);
        panel.Children.Add(new TextBlock
        {
            Text = "Install closes this app and starts a per-user installer. Preferences are preserved. Portable users can instead download a ZIP from the release page. Packages are unsigned; SHA-256 checks establish asset integrity, not publisher identity.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver, FontSize = 12
        });
        Content = panel;
        check.Click += async (_, _) => await CheckAsync();
        download.Click += async (_, _) => await DownloadAsync();
        installButton.Click += async (_, _) => await InstallAsync();
        Loaded += async (_, _) =>
        {
            if (available is null) await CheckAsync();
            else { status.Text = $"Version {available.Version} is available. Downloading does not install it."; UpdateControls(); }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };
        UpdateControls();
    }

    private async Task CheckAsync()
    {
        if (busy || closed) return;
        busy = true;
        staged = null;
        available = null;
        status.Text = "Checking stable releases…";
        UpdateControls();
        try
        {
            var result = await service.CheckAsync(currentVersion, getMode(), manual: true, token);
            if (closed || token.IsCancellationRequested) return;
            available = result.Update;
            status.Text = result.Detail;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { ShowFailure(exception); }
        finally { busy = false; UpdateControls(); }
    }

    private async Task DownloadAsync()
    {
        if (busy || closed || available is null || getMode() == UpdateCheckMode.Off) return;
        busy = true;
        staged = null;
        status.Text = "Downloading and verifying the selected installer…";
        UpdateControls();
        try
        {
            var result = await service.DownloadVerifiedInstallerAsync(available, getMode(), token);
            if (closed || token.IsCancellationRequested || getMode() == UpdateCheckMode.Off) return;
            staged = result;
            status.Text = $"Version {result.Version} downloaded and verified. Install only when you are ready to close the app.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { ShowFailure(exception); }
        finally { busy = false; UpdateControls(); }
    }

    private async Task InstallAsync()
    {
        if (busy || closed || staged is null || getMode() == UpdateCheckMode.Off) return;
        busy = true;
        UpdateControls();
        try
        {
            var selected = staged;
            // Fail before shutdown if a staged file was changed. The controller revalidates
            // again under a read lease immediately before starting the installer after exit.
            using (await service.VerifyForInstallAsync(selected, token)) { }
            if (closed || token.IsCancellationRequested || getMode() == UpdateCheckMode.Off) return;
            status.Text = "Closing the app to start the verified installer…";
            install(selected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { ShowFailure(exception); }
        finally { busy = false; UpdateControls(); }
    }

    private void ShowFailure(Exception exception)
    {
        reportError(exception);
        if (!closed) status.Text = $"The update operation could not be completed. {exception.Message}";
    }

    private void UpdateControls()
    {
        if (closed) return;
        var enabled = !busy && getMode() != UpdateCheckMode.Off;
        check.IsEnabled = enabled;
        download.IsEnabled = enabled && available is not null;
        installButton.IsEnabled = enabled && staged is not null;
    }
}
