using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioBridge.Server.Core;

/// <summary>
/// Minimal P/Invoke layer over libusb-1.0 (the native DLL ships with the <c>libusb.runtime</c>
/// package). Only what the Android Open Accessory host needs: enumerate, open, control transfer,
/// bulk transfer. 64-bit only — the server is x64.
///
/// libusb is told to prefer the UsbDk backend: UsbDk (a signed filter driver, Apache-2.0) lets
/// user-mode code open any USB device without replacing its Windows driver, which is what makes
/// "switch the phone into accessory mode" possible on a stock Windows install. If UsbDk is not
/// installed libusb silently falls back to its WinUSB backend (works for devices that already use
/// WinUSB, e.g. after a one-time Zadig install on the phone's accessory interface).
/// </summary>
internal static class LibUsb
{
    private const string Lib = "libusb-1.0";

    public const int Success = 0;
    public const int ErrorIo = -1;
    public const int ErrorAccess = -3;
    public const int ErrorNoDevice = -4;
    public const int ErrorNotFound = -5;
    public const int ErrorBusy = -6;
    public const int ErrorTimeout = -7;
    public const int ErrorPipe = -9;
    public const int ErrorNotSupported = -12;

    private const int OptionUseUsbDk = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceDescriptor
    {
        public byte bLength, bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
        public ushort idVendor, idProduct, bcdDevice;
        public byte iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitOption
    {
        public int option;
        public long value;
    }

    [DllImport(Lib)] private static extern int libusb_set_option(IntPtr ctx, int option);
    [DllImport(Lib)] private static extern int libusb_init(out IntPtr ctx);
    [DllImport(Lib)] public static extern void libusb_exit(IntPtr ctx);
    [DllImport(Lib)] private static extern nint libusb_get_device_list(IntPtr ctx, out IntPtr list);
    [DllImport(Lib)] private static extern void libusb_free_device_list(IntPtr list, int unrefDevices);
    [DllImport(Lib)] public static extern int libusb_get_device_descriptor(IntPtr dev, out DeviceDescriptor desc);
    [DllImport(Lib)] public static extern byte libusb_get_bus_number(IntPtr dev);
    [DllImport(Lib)] public static extern byte libusb_get_device_address(IntPtr dev);
    [DllImport(Lib)] public static extern IntPtr libusb_ref_device(IntPtr dev);
    [DllImport(Lib)] public static extern void libusb_unref_device(IntPtr dev);
    [DllImport(Lib)] public static extern int libusb_open(IntPtr dev, out IntPtr handle);
    [DllImport(Lib)] public static extern void libusb_close(IntPtr handle);
    [DllImport(Lib)] public static extern int libusb_claim_interface(IntPtr handle, int iface);
    [DllImport(Lib)] public static extern int libusb_release_interface(IntPtr handle, int iface);
    [DllImport(Lib)] public static extern int libusb_clear_halt(IntPtr handle, byte endpoint);
    [DllImport(Lib)] private static extern IntPtr libusb_error_name(int code);

    [DllImport(Lib)]
    public static extern int libusb_control_transfer(IntPtr handle, byte requestType, byte request, ushort value, ushort index,
        byte[]? data, ushort length, uint timeoutMs);

    [DllImport(Lib)]
    public static extern int libusb_bulk_transfer(IntPtr handle, byte endpoint, byte[] data, int length, out int transferred, uint timeoutMs);

    [DllImport(Lib)]
    public static extern int libusb_get_string_descriptor_ascii(IntPtr handle, byte index, byte[] data, int length);

    public static string ErrorName(int code)
    {
        try { return Marshal.PtrToStringAnsi(libusb_error_name(code)) ?? code.ToString(); }
        catch { return code.ToString(); }
    }

    /// <summary>True when the context talks to devices through UsbDk (otherwise the WinUSB backend).</summary>
    public static bool UsbDkActive { get; private set; }

    /// <summary>
    /// Creates a context. UsbDk is switched on <i>after</i> init: libusb only knows whether the UsbDk
    /// backend is available once it has initialised, so asking for it via init options fails with
    /// "UsbDk backend not available" even when UsbDk is running.
    /// </summary>
    public static bool TryInit(out IntPtr ctx, out string? error)
    {
        ctx = IntPtr.Zero;
        error = null;
        UsbDkActive = false;
        try
        {
            var rc = libusb_init(out ctx);
            if (rc != 0)
            {
                error = ErrorName(rc);
                return false;
            }
            rc = libusb_set_option(ctx, OptionUseUsbDk);
            UsbDkActive = rc == 0;
            if (!UsbDkActive) error = $"UsbDk: {ErrorName(rc)}";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            error = "libusb-1.0.dll не найдена";
            return false;
        }
    }

    public readonly record struct DeviceInfo(IntPtr Handle, DeviceDescriptor Descriptor, int Bus, int Address)
    {
        public string Key => $"{Bus}:{Address}";
        public string Id => $"{Descriptor.idVendor:X4}:{Descriptor.idProduct:X4}";
    }

    /// <summary>Snapshot of the bus. The caller must pass the result to <see cref="FreeDeviceList"/>.</summary>
    public static List<DeviceInfo> ListDevices(IntPtr ctx, out IntPtr rawList)
    {
        var result = new List<DeviceInfo>();
        rawList = IntPtr.Zero;
        var n = libusb_get_device_list(ctx, out var list);
        if (n < 0) return result;
        rawList = list;
        for (var i = 0; i < n; i++)
        {
            var dev = Marshal.ReadIntPtr(list, i * IntPtr.Size);
            if (libusb_get_device_descriptor(dev, out var desc) != 0) continue;
            result.Add(new DeviceInfo(dev, desc, libusb_get_bus_number(dev), libusb_get_device_address(dev)));
        }
        return result;
    }

    public static void FreeDeviceList(IntPtr rawList)
    {
        if (rawList != IntPtr.Zero) libusb_free_device_list(rawList, 1);
    }

    public static string? ReadString(IntPtr handle, byte index)
    {
        if (index == 0) return null;
        var buf = new byte[128];
        var n = libusb_get_string_descriptor_ascii(handle, index, buf, buf.Length);
        return n > 0 ? Encoding.ASCII.GetString(buf, 0, n) : null;
    }
}
