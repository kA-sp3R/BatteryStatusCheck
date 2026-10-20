using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DeviceBatteryStatus.Core.Hardware;

namespace DeviceBatteryStatus.Windows.Hardware;

/// <summary>Enumerates HID collections. It does not infer wireless connection from USB presence.</summary>
public sealed class WindowsHidDiscovery : IHidDiscovery
{
    public IReadOnlyList<string> LastErrors { get; private set; } = [];
    public IReadOnlyList<string> Warnings => LastErrors;

    public IReadOnlyList<HidEndpoint> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows HID is required.");
        var results = new List<HidEndpoint>();
        var errors = new List<string>();
        NativeMethods.HidD_GetHidGuid(out var guid);
        using var infoSet = NativeMethods.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (infoSet.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate HID interfaces.");

        for (uint index = 0; ; index++)
        {
            var data = new NativeMethods.DeviceInterfaceData { Size = Marshal.SizeOf<NativeMethods.DeviceInterfaceData>() };
            if (!NativeMethods.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref guid, index, ref data))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorNoMoreItems) break;
                throw new Win32Exception(error, "Cannot enumerate a HID interface.");
            }

            try
            {
                var deviceInfo = new NativeMethods.DeviceInfoData { Size = Marshal.SizeOf<NativeMethods.DeviceInfoData>() };
                NativeMethods.SetupDiGetDeviceInterfaceDetailW(infoSet, ref data, IntPtr.Zero, 0, out var size, ref deviceInfo);
                if (size < 6 || size > 65536) throw new IOException("Invalid HID interface detail size.");
                var buffer = Marshal.AllocHGlobal(checked((int)size));
                string path;
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetailW(infoSet, ref data, buffer, size, out _, ref deviceInfo))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID interface details.");
                    path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? throw new IOException("Missing HID path.");
                }
                finally { Marshal.FreeHGlobal(buffer); }

                // Skip unrelated vendors before opening a metadata handle.
                if (!IsCandidatePath(path)) continue;
                using var handle = NativeMethods.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open HID metadata.");
                var attributes = new NativeMethods.HidAttributes { Size = Marshal.SizeOf<NativeMethods.HidAttributes>() };
                if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID attributes.");
                if (attributes.VendorId is not (0x1532 or 0x046D or 0x187C or 0x413C)) continue;

                var usbId = FindPhysicalUsbAncestor(deviceInfo.DevInst, attributes.VendorId, attributes.ProductId);
                if (usbId is null) continue; // Bluetooth and non-USB HID are deliberately excluded.
                if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsed))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID descriptor.");
                NativeMethods.HidCaps caps;
                try
                {
                    if (NativeMethods.HidP_GetCaps(preparsed, out caps) != 0x00110000)
                        throw new IOException("HID capabilities are unavailable.");
                }
                finally { NativeMethods.HidD_FreePreparsedData(preparsed); }

                var containerKey = new NativeMethods.DevicePropertyKey
                {
                    FormatId = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), PropertyId = 2
                };
                var containerBytes = new byte[16];
                var identity = usbId;
                if (NativeMethods.SetupDiGetDevicePropertyW(infoSet, ref deviceInfo, ref containerKey,
                        out var propertyType, containerBytes, 16, out var propertySize, 0)
                    && propertyType == 0x0D && propertySize == 16)
                {
                    var container = new Guid(containerBytes);
                    if (container != Guid.Empty) identity = container.ToString("D");
                }
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..24];
                var receiverId = $"usb:{attributes.VendorId:X4}:{attributes.ProductId:X4}:{hash}";
                // Read cached PnP labels, rather than issuing synchronous USB string-descriptor requests.
                var productName = GetCachedName(infoSet, ref deviceInfo);
                results.Add(new HidEndpoint(path, attributes.VendorId, attributes.ProductId,
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength,
                    caps.FeatureReportByteLength, receiverId, productName));
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException)
            {
                errors.Add($"HID interface {index}: {ex.Message}");
            }
        }
        LastErrors = errors.AsReadOnly();
        return results.AsReadOnly();
    }

    private static bool IsCandidatePath(string path) =>
        path.Contains("vid_1532", StringComparison.OrdinalIgnoreCase)
        || path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)
        || path.Contains("vid_187c", StringComparison.OrdinalIgnoreCase)
        || path.Contains("vid_413c", StringComparison.OrdinalIgnoreCase);

    private static string? GetCachedName(NativeMethods.DeviceInfoSetHandle infoSet,
        ref NativeMethods.DeviceInfoData deviceInfo)
    {
        foreach (var propertyId in new uint[] { 14, 2 }) // FriendlyName, DeviceDesc
        {
            var key = new NativeMethods.DevicePropertyKey
            {
                FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = propertyId
            };
            var bytes = new byte[2048];
            if (NativeMethods.SetupDiGetDevicePropertyW(infoSet, ref deviceInfo, ref key,
                    out var type, bytes, (uint)bytes.Length, out var length, 0)
                && type == 0x12 && length is >= 2 and <= 2048)
                return Encoding.Unicode.GetString(bytes, 0, (int)length).TrimEnd('\0');
        }
        return null;
    }

    private static string? FindPhysicalUsbAncestor(uint devInst, ushort vendorId, ushort productId)
    {
        var prefix = $"USB\\VID_{vendorId:X4}&PID_{productId:X4}";
        string? result = null;
        for (var depth = 0; depth < 32; depth++)
        {
            if (NativeMethods.CM_Get_Device_ID_Size(out var length, devInst, 0) != 0)
                throw new IOException("Cannot read HID parent identity.");
            var id = new StringBuilder(checked((int)length + 1));
            if (NativeMethods.CM_Get_Device_IDW(devInst, id, (uint)id.Capacity, 0) != 0)
                throw new IOException("Cannot read HID parent identity.");
            var text = id.ToString();
            if (text.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return null;
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = text;
                // A physical USB node has no composite-interface MI_ suffix before its instance component.
                if (!text.Split('\\')[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return text;
            }
            var status = NativeMethods.CM_Get_Parent(out var parent, devInst, 0);
            if (status == 0x0D) break; // CR_NO_SUCH_DEVNODE: the root has no parent.
            if (status != 0) throw new IOException($"Cannot inspect HID USB ancestry (CM {status:X}).");
            devInst = parent;
        }
        return result;
    }
}
