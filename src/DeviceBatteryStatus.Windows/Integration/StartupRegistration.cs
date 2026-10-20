using Microsoft.Win32;

namespace DeviceBatteryStatus.Windows.Integration;

/// <summary>Opt-in startup for this apphost only, using the current user's Run key.</summary>
public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeviceBatteryStatus";
    private readonly string? executablePath;

    public StartupRegistration(string? executablePath = null)
    {
        string? candidate = executablePath ?? Environment.ProcessPath;
        if (candidate is null || !Path.IsPathFullyQualified(candidate) ||
            candidate.Any(char.IsControl) || candidate.Contains('"') ||
            !string.Equals(Path.GetFileName(candidate), "DeviceBatteryStatus.exe", StringComparison.OrdinalIgnoreCase)) return;
        try { this.executablePath = Path.GetFullPath(candidate); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    public bool CanRegister => executablePath is not null;

    public bool IsEnabled()
    {
        if (!CanRegister) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return OwnsCommand(key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }

    public void SetEnabled(bool enabled)
    {
        if (!CanRegister)
            throw new InvalidOperationException("Windows startup requires the DeviceBatteryStatus.exe apphost; a dotnet or test host cannot be registered.");
        using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            if (enabled) throw new IOException("The current user's Windows startup key could not be opened.");
            return;
        }
        object? existing = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (existing is not null && !OwnsCommand(existing as string))
            throw new InvalidOperationException("The DeviceBatteryStatus startup entry contains a different command. It was preserved; review it in Windows startup settings or Registry Editor.");
        if (enabled)
        {
            if (existing is null) key.SetValue(ValueName, $"\"{executablePath}\" --startup", RegistryValueKind.String);
        }
        else if (existing is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal bool OwnsCommand(string? command)
    {
        if (!CanRegister || string.IsNullOrEmpty(command) || command[0] != '"' || command.Any(char.IsControl)) return false;
        int closingQuote = command.IndexOf('"', 1);
        if (closingQuote <= 1 || !string.Equals(command[(closingQuote + 1)..].Trim(), "--startup", StringComparison.Ordinal)) return false;
        string path = command[1..closingQuote];
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)) return false;
        try { return string.Equals(Path.GetFullPath(path), executablePath, StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
