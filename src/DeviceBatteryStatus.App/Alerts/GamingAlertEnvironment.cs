using System.Runtime.InteropServices;
using System.Text;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeviceBatteryStatus.App.Alerts;

internal sealed record AlertMonitor(string DeviceName, string Name, bool IsPrimary,
    Drawing.Rectangle Bounds, Drawing.Rectangle WorkArea, uint Dpi);

/// <summary>Desktop-only display inputs. The synthetic smoke substitutes this interface.</summary>
internal interface IGamingAlertEnvironment
{
    AlertMonitor ResolveMonitor(string? destinationMonitor);
    bool ShouldDefer(AlertMonitor monitor);
    IntPtr ForegroundWindow { get; }
    void PlaySound();
}

/// <summary>Uses ordinary desktop APIs; never opens, hooks or injects into a game process.</summary>
internal sealed class GamingAlertEnvironment : IGamingAlertEnvironment
{
    public IntPtr ForegroundWindow => AlertNative.GetForegroundWindow();

    internal static IReadOnlyList<AlertMonitor> GetMonitors() => Forms.Screen.AllScreens
        .Select(ToMonitor).ToArray();

    public AlertMonitor ResolveMonitor(string? destinationMonitor)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(monitor => string.Equals(monitor.DeviceName,
                destinationMonitor, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? monitors.FirstOrDefault()
            ?? throw new InvalidOperationException("No Windows desktop monitor is available for battery alerts.");
    }

    public bool ShouldDefer(AlertMonitor monitor)
    {
        try
        {
            if (QueryUserNotificationState(out var state) >= 0)
            {
                // Windows reports busy, presentation and fullscreen-D3D states.
                // Conservatively defer instead of attempting a game overlay.
                if (state is 1 or 2 or 3 or 4 or 6 or 7) return true;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }

        // A geometric fallback cannot distinguish exclusive from borderless
        // fullscreen. A false-positive merely retains a relevant alert for later.
        var foreground = ForegroundWindow;
        if (foreground == IntPtr.Zero || IsIconic(foreground)) return false;
        var name = new StringBuilder(256);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!AlertNative.GetWindowRect(foreground, out var rectangle)) return false;
        var bounds = monitor.Bounds;
        return rectangle.Left <= bounds.Left + 2 && rectangle.Top <= bounds.Top + 2 &&
            rectangle.Right >= bounds.Right - 2 && rectangle.Bottom >= bounds.Bottom - 2;
    }

    public void PlaySound() => System.Media.SystemSounds.Exclamation.Play();

    private static AlertMonitor ToMonitor(Forms.Screen screen)
    {
        var dpi = 96u;
        try
        {
            var point = new NativePoint(screen.Bounds.Left + screen.Bounds.Width / 2,
                screen.Bounds.Top + screen.Bounds.Height / 2);
            var handle = MonitorFromPoint(point, 2);
            if (handle != IntPtr.Zero && GetDpiForMonitor(handle, 0, out var x, out _) >= 0 && x is >= 48 and <= 768)
                dpi = x;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
        return new(screen.DeviceName, screen.Primary ? $"{screen.DeviceName} (primary)" : screen.DeviceName,
            screen.Primary, screen.Bounds, screen.WorkingArea, dpi);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y) { public readonly int X = x; public readonly int Y = y; }
    [DllImport("shell32.dll", EntryPoint = "SHQueryUserNotificationState")]
    private static extern int QueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
}

internal static class AlertNative
{
    internal const long NoActivate = 0x08000000;
    internal const long Transparent = 0x00000020;
    internal const long ToolWindow = 0x00000080;
    internal const long Layered = 0x00080000;
    internal const long AppWindow = 0x00040000;
    internal const int ExtendedStyle = -20;
    internal const int MouseActivate = 0x0021;
    internal const int NonClientHitTest = 0x0084;
    internal const int NoActivateResult = 3;
    internal const int TransparentHitTest = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Drawing.Rectangle Rectangle => Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr data);

    internal static long ReadExtendedStyle(IntPtr window) => IntPtr.Size == 8
        ? GetWindowLongPtr(window, ExtendedStyle).ToInt64() : GetWindowLong(window, ExtendedStyle);

    internal static void WriteExtendedStyle(IntPtr window, long value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr(window, ExtendedStyle, new IntPtr(value))
            : new IntPtr(SetWindowLong(window, ExtendedStyle, unchecked((int)value)));
        if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() is var error && error != 0)
            throw new System.ComponentModel.Win32Exception(error, "Gaming alert window styles could not be set.");
    }
}
