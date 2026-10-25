using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeviceBatteryStatus.App;

/// <summary>Coalesces Windows PnP notifications; it neither enumerates nor opens HID devices.</summary>
internal sealed class DeviceChangeWatcher : IDisposable
{
    private readonly HwndSource source;
    private readonly DispatcherTimer debounce;
    private IntPtr registration;
    private bool disposed;
    private readonly bool receiveNativeNotifications;
    public event Action? Changed;

    internal DeviceChangeWatcher(bool receiveNativeNotifications = true)
    {
        // Broadcast device-node messages go to top-level windows, not HWND_MESSAGE.
        // No WS_VISIBLE flag: this zero-size window never enters the product UI.
        this.receiveNativeNotifications = receiveNativeNotifications;
        debounce = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(350) };
        source = new HwndSource(new HwndSourceParameters("Device Battery Status PnP notifications")
        { WindowStyle = 0, Width = 0, Height = 0 });
        var filter = new DeviceInterfaceFilter
        {
            Size = Marshal.SizeOf<DeviceInterfaceFilter>(), DeviceType = 5,
            ClassGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030")
        };
        try
        {
            debounce.Tick += Flush;
            source.AddHook(WindowMessage);
            if (receiveNativeNotifications)
                registration = RegisterDeviceNotification(source.Handle, ref filter, 0);
        }
        catch
        {
            debounce.Tick -= Flush;
            debounce.Stop();
            source.RemoveHook(WindowMessage);
            source.Dispose();
            throw;
        }
        // DBT_DEVNODES_CHANGED broadcasts and periodic reconciliation remain
        // available if interface registration is unavailable on this desktop.
    }

    internal bool InterfaceNotificationsRegistered => registration != IntPtr.Zero;
    private IntPtr WindowMessage(IntPtr window, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (!disposed && receiveNativeNotifications && message == 0x0219 && parameter.ToInt64() is 0x0007 or 0x8000 or 0x8004)
            QueueChange();
        return IntPtr.Zero;
    }

    internal void QueueChange()
    {
        if (disposed) return;
        debounce.Stop();
        debounce.Start();
    }

    private void Flush(object? sender, EventArgs args)
    {
        debounce.Stop();
        if (!disposed) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        debounce.Stop();
        debounce.Tick -= Flush;
        if (registration != IntPtr.Zero)
        {
            UnregisterDeviceNotification(registration);
            registration = IntPtr.Zero;
        }
        source.RemoveHook(WindowMessage);
        source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceFilter
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public short Name;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterDeviceNotificationW")]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceInterfaceFilter filter, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
