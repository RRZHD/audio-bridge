using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AudioBridge.Server.Core;

/// <summary>
/// USB without adb and without USB tethering: the PC acts as an Android Open Accessory (AOA) host.
///
/// 1. A known Android phone is found on the bus.
/// 2. The PC asks it to switch into accessory mode (vendor control requests 51/52/53 вЂ” the phone
///    re-enumerates as Google 18D1:2D00/2D01).
/// 3. The PC opens the accessory's two bulk endpoints and waits until the Audio Bridge app on the
///    phone has opened the accessory and sent its first frame.
/// 4. From then on it is just a duplex byte stream carrying the usual wire protocol вЂ” a
///    <see cref="LibUsbStream"/> handed to <see cref="ClientSession"/> like a TCP stream.
///
/// Needs UsbDk once (a signed driver that lets user mode talk to a device without replacing its
/// Windows driver). Without it the scan finds nothing it may open and says so.
/// </summary>
public sealed class AoaHost : IDisposable
{
    // AOA identification: the phone app's accessory_filter.xml matches exactly these strings.
    public const string Manufacturer = "AudioBridge";
    public const string Model = "AudioBridge";
    public const string Description = "Audio Bridge PC server";
    public const string Version = "1.0";
    public const string Uri = "https://github.com/RRZHD/audio-bridge";
    public const string Serial = "1";

    private const ushort GoogleVendor = 0x18D1;
    private static readonly HashSet<ushort> AccessoryProducts = new() { 0x2D00, 0x2D01, 0x2D04, 0x2D05 };

    /// <summary>Vendors of Android phones. Only these are ever opened вЂ” never keyboards, drives, hubsвЂ¦</summary>
    private static readonly HashSet<ushort> PhoneVendors = new()
    {
        0x0FCE, // Sony / Sony Mobile
        0x18D1, // Google (and every phone in accessory/adb mode)
        0x04E8, // Samsung
        0x2717, // Xiaomi
        0x22B8, // Motorola
        0x12D1, // Huawei
        0x2A70, // OnePlus
        0x22D9, // OPPO
        0x2D95, // vivo
        0x1004, // LG
        0x0BB4, // HTC
        0x0B05, // ASUS
        0x1949, // Amazon
        0x19D2, // ZTE
        0x2916, // Yota
        0x1782, // Spreadtrum
        0x0E8D, // MediaTek
        0x1BBB, // Alcatel/T&A
        0x2AE5, // Fairphone
        0x1D4D, // Pegatron / Nothing
        0x29A9, // Nothing
    };

    private const byte AoaGetProtocol = 51;
    private const byte AoaSendString = 52;
    private const byte AoaStart = 53;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private IntPtr _ctx;
    private readonly Dictionary<string, DateTime> _cooldown = new();
    private string _status = "";
    private DateTime _fastScanUntil = DateTime.MinValue;

    public event Action<Stream, string>? ClientAccepted;
    public event Action<string>? Log;
    /// <summary>Short human-readable state, for the main window.</summary>
    public event Action<string>? StatusChanged;

    public string Status => _status;

    /// <summary>UsbDk's service entry exists (installed by its MSI).</summary>
    public static bool IsUsbDkInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UsbDk");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The persistent UsbDk rules that keep the phone's accessory device from being power-cycled.</summary>
    public static bool AccessoryHideRulesInstalled()
    {
        try
        {
            using var rules = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UsbDk\Parameters\HideRules");
            if (rules == null) return false;
            var found = new HashSet<int>();
            foreach (var name in rules.GetSubKeyNames())
            {
                using var r = rules.OpenSubKey(name);
                if (r?.GetValue("VID") is int vid && vid == GoogleVendor && r.GetValue("PID") is int pid) found.Add(pid);
            }
            return found.Contains(0x2D00) && found.Contains(0x2D01);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// One elevated step: optionally install the UsbDk MSI, then register the hide rules. Without the
    /// rules UsbDk "redirects" the accessory device by restarting its port, which throws the phone
    /// back out of accessory mode (a hidden device is taken over without a restart).
    /// </summary>
    public static bool SetupElevated(string? msiPath, Action<string>? log = null)
    {
        try
        {
            var controller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UsbDk Runtime Library", "UsbDkController.exe");
            var sb = new StringBuilder("@echo off\r\n");
            if (msiPath != null) sb.Append($"msiexec /i \"{msiPath}\" /passive /norestart\r\n");
            foreach (var pid in new[] { "2D00", "2D01", "2D04", "2D05" })
                sb.Append($"\"{controller}\" -P 0 18D1 {pid} -1 -1 1\r\n");
            var script = Path.Combine(Path.GetTempPath(), $"audiobridge-usb-setup-{Environment.ProcessId}.cmd");
            File.WriteAllText(script, sb.ToString());
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit();
            try { File.Delete(script); } catch { /* temp */ }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"USB: РЅР°СЃС‚СЂРѕР№РєР° РЅРµ РІС‹РїРѕР»РЅРµРЅР° ({ex.Message})");
            return false;
        }
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _thread = new Thread(() => Run(ct)) { IsBackground = true, Name = "AoaHost" };
        _thread.Start();
    }

    public void Stop()
    {
        var cts = _cts;
        var thread = _thread;
        _cts = null;
        _thread = null;
        try { cts?.Cancel(); } catch { /* already disposed */ }
        if (thread != null && thread != Thread.CurrentThread) thread.Join(4000);
        cts?.Dispose();
        SetStatus("");
    }

    public void Dispose() => Stop();

    private void SetStatus(string status)
    {
        if (status == _status) return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private void Run(CancellationToken ct)
    {
        var usbdkInstalled = IsUsbDkInstalled();
        if (!LibUsb.TryInit(out _ctx, out var error))
        {
            Log?.Invoke($"USB: libusb РЅРµРґРѕСЃС‚СѓРїРЅР° ({error}) вЂ” USB-СЂРµР¶РёРј РѕС‚РєР»СЋС‡С‘РЅ");
            SetStatus("USB РЅРµРґРѕСЃС‚СѓРїРµРЅ: РЅРµ СѓРґР°Р»РѕСЃСЊ Р·Р°РїСѓСЃС‚РёС‚СЊ libusb");
            return;
        }
        var usbdk = LibUsb.UsbDkActive;
        if (usbdk)
        {
            Log?.Invoke("USB: РґСЂР°Р№РІРµСЂ UsbDk Р°РєС‚РёРІРµРЅ");
        }
        else if (usbdkInstalled)
        {
            Log?.Invoke($"USB: UsbDk СѓСЃС‚Р°РЅРѕРІР»РµРЅ, РЅРѕ libusb РµРіРѕ РЅРµ РїРѕРґС…РІР°С‚РёР» ({error}). Р•СЃР»Рё Windows РЅРµ РїРµСЂРµР·Р°РіСЂСѓР¶Р°Р»Р°СЃСЊ РїРѕСЃР»Рµ СѓСЃС‚Р°РЅРѕРІРєРё вЂ” РїРµСЂРµР·Р°РіСЂСѓР·Рё РµС‘.");
        }
        else
        {
            Log?.Invoke("USB: РґСЂР°Р№РІРµСЂ UsbDk РЅРµ СѓСЃС‚Р°РЅРѕРІР»РµРЅ вЂ” С‚РµР»РµС„РѕРЅ РїРѕ USB РїРµСЂРµРєР»СЋС‡РёС‚СЊ РЅРµР»СЊР·СЏ");
        }

        try
        {
            string? lastLoggedState = null;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ScanOnce(usbdk, ct, ref lastLoggedState);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"USB: РѕС€РёР±РєР° СЃРєР°РЅРёСЂРѕРІР°РЅРёСЏ: {ex.Message}");
                }
                // Right after the accessory switch the new device must be grabbed quickly.
                ct.WaitHandle.WaitOne(DateTime.UtcNow < _fastScanUntil ? 150 : 1500);
            }
        }
        finally
        {
            LibUsb.libusb_exit(_ctx);
            _ctx = IntPtr.Zero;
        }
    }

    private void ScanOnce(bool usbdk, CancellationToken ct, ref string? lastLoggedState)
    {
        var devices = LibUsb.ListDevices(_ctx, out var raw);
        try
        {
            var accessory = devices.FirstOrDefault(d => d.Descriptor.idVendor == GoogleVendor && AccessoryProducts.Contains(d.Descriptor.idProduct));
            if (accessory.Handle != IntPtr.Zero)
            {
                ServeAccessory(accessory, ct);
                return;
            }

            var phone = devices.FirstOrDefault(d => IsPhoneCandidate(d.Descriptor));
            if (phone.Handle == IntPtr.Zero)
            {
                SetStatus(usbdk ? "USB: С‚РµР»РµС„РѕРЅ РЅРµ РЅР°Р№РґРµРЅ вЂ” РїРѕРґРєР»СЋС‡Рё РєР°Р±РµР»СЊ" : "USB: UsbDk РЅРµ Р°РєС‚РёРІРµРЅ (СѓСЃС‚Р°РЅРѕРІРё РґСЂР°Р№РІРµСЂ / РїРµСЂРµР·Р°РіСЂСѓР·Рё Windows)");
                return;
            }
            if (!usbdk)
            {
                SetStatus("USB: С‚РµР»РµС„РѕРЅ РЅР°Р№РґРµРЅ, РЅРѕ UsbDk РЅРµ Р°РєС‚РёРІРµРЅ (СѓСЃС‚Р°РЅРѕРІРё РґСЂР°Р№РІРµСЂ / РїРµСЂРµР·Р°РіСЂСѓР·Рё Windows)");
                return;
            }

            // Don't hammer a phone that refused (or that is mid-way through re-enumerating).
            if (_cooldown.TryGetValue(phone.Key, out var until) && until > DateTime.UtcNow) return;

            SetStatus($"USB: РЅР°Р№РґРµРЅ С‚РµР»РµС„РѕРЅ {phone.Id}, РїРµСЂРµРєР»СЋС‡Р°СЋ РІ СЂРµР¶РёРј Р°РєСЃРµСЃСЃСѓР°СЂР°вЂ¦");
            var rc = TrySwitchToAccessory(phone, out var detail);
            _cooldown[phone.Key] = DateTime.UtcNow.AddSeconds(rc ? 8 : 30);
            if (rc) _fastScanUntil = DateTime.UtcNow.AddSeconds(6);
            if (!rc)
            {
                SetStatus($"USB: С‚РµР»РµС„РѕРЅ {phone.Id} РЅРµ СѓРґР°Р»РѕСЃСЊ РїРµСЂРµРєР»СЋС‡РёС‚СЊ ({detail})");
                var state = $"{phone.Key}:{detail}";
                if (state != lastLoggedState)
                {
                    lastLoggedState = state;
                    Log?.Invoke($"USB: С‚РµР»РµС„РѕРЅ {phone.Id} РЅРµ РїРµСЂРµРєР»СЋС‡С‘РЅ: {detail}");
                }
            }
            else
            {
                Log?.Invoke($"USB: С‚РµР»РµС„РѕРЅ {phone.Id} РїРµСЂРµРєР»СЋС‡С‘РЅ РІ СЂРµР¶РёРј Р°РєСЃРµСЃСЃСѓР°СЂР°");
            }
        }
        finally
        {
            LibUsb.FreeDeviceList(raw);
        }
    }

    private static bool IsPhoneCandidate(LibUsb.DeviceDescriptor d)
    {
        if (!PhoneVendors.Contains(d.idVendor)) return false;
        if (d.idVendor == GoogleVendor && AccessoryProducts.Contains(d.idProduct)) return false;
        // Hubs and HID-only devices from these vendors are not phones.
        return d.bDeviceClass is 0x00 or 0xEF or 0xFF or 0x02 or 0x06;
    }

    // ---- accessory switch -------------------------------------------------------------------

    private bool TrySwitchToAccessory(LibUsb.DeviceInfo dev, out string detail)
    {
        var rc = LibUsb.libusb_open(dev.Handle, out var h);
        if (rc != 0)
        {
            detail = rc == LibUsb.ErrorAccess || rc == LibUsb.ErrorNotSupported
                ? $"РЅРµС‚ РґРѕСЃС‚СѓРїР° ({LibUsb.ErrorName(rc)}) вЂ” UsbDk РЅРµ Р°РєС‚РёРІРµРЅ?"
                : LibUsb.ErrorName(rc);
            return false;
        }
        try
        {
            var proto = new byte[2];
            rc = LibUsb.libusb_control_transfer(h, 0xC0, AoaGetProtocol, 0, 0, proto, 2, 1500);
            if (rc < 2)
            {
                detail = $"С‚РµР»РµС„РѕРЅ РЅРµ РѕС‚РІРµС‚РёР» РЅР° AOA-Р·Р°РїСЂРѕСЃ ({LibUsb.ErrorName(rc)})";
                return false;
            }
            var version = proto[0] | (proto[1] << 8);
            if (version < 1)
            {
                detail = "С‚РµР»РµС„РѕРЅ РЅРµ РїРѕРґРґРµСЂР¶РёРІР°РµС‚ СЂРµР¶РёРј Р°РєСЃРµСЃСЃСѓР°СЂР°";
                return false;
            }

            string[] strings = { Manufacturer, Model, Description, Version, Uri, Serial };
            for (ushort i = 0; i < strings.Length; i++)
            {
                var data = Encoding.UTF8.GetBytes(strings[i] + "\0");
                rc = LibUsb.libusb_control_transfer(h, 0x40, AoaSendString, 0, i, data, (ushort)data.Length, 1500);
                if (rc < 0)
                {
                    detail = $"РЅРµ СѓРґР°Р»РѕСЃСЊ РїРµСЂРµРґР°С‚СЊ РёРґРµРЅС‚РёС„РёРєР°С†РёСЋ ({LibUsb.ErrorName(rc)})";
                    return false;
                }
            }
            rc = LibUsb.libusb_control_transfer(h, 0x40, AoaStart, 0, 0, null, 0, 1500);
            if (rc < 0)
            {
                detail = $"РЅРµ СѓРґР°Р»РѕСЃСЊ Р·Р°РїСѓСЃС‚РёС‚СЊ СЂРµР¶РёРј Р°РєСЃРµСЃСЃСѓР°СЂР° ({LibUsb.ErrorName(rc)})";
                return false;
            }
            detail = "ok";
            return true;
        }
        finally
        {
            LibUsb.libusb_close(h);
        }
    }

    // ---- accessory session ------------------------------------------------------------------

    private void ServeAccessory(LibUsb.DeviceInfo dev, CancellationToken ct)
    {
        var rc = LibUsb.libusb_open(dev.Handle, out var h);
        if (rc != 0)
        {
            SetStatus($"USB: РЅРµ СѓРґР°Р»РѕСЃСЊ РѕС‚РєСЂС‹С‚СЊ Р°РєСЃРµСЃСЃСѓР°СЂ ({LibUsb.ErrorName(rc)})");
            return;
        }

        LibUsbStream? stream = null;
        try
        {
            if (!TryFindBulkEndpoints(h, out var iface, out var inEp, out var outEp, out var maxPacket))
            {
                SetStatus("USB: Сѓ Р°РєСЃРµСЃСЃСѓР°СЂР° РЅРµ РЅР°Р№РґРµРЅС‹ bulk-СЌРЅРґРїРѕРёРЅС‚С‹");
                return;
            }
            rc = LibUsb.libusb_claim_interface(h, iface);
            if (rc != 0)
            {
                SetStatus($"USB: РЅРµ СѓРґР°Р»РѕСЃСЊ Р·Р°РЅСЏС‚СЊ РёРЅС‚РµСЂС„РµР№СЃ ({LibUsb.ErrorName(rc)})");
                return;
            }

            stream = new LibUsbStream(h, iface, inEp, outEp, maxPacket);
            SetStatus("USB: Р¶РґСѓ РїСЂРёР»РѕР¶РµРЅРёРµ Audio Bridge РЅР° С‚РµР»РµС„РѕРЅРµ (Р·Р°РїСѓСЃС‚Рё РµРіРѕ Рё РЅР°Р¶РјРё В«РџРѕРґРєР»СЋС‡РёС‚СЊСЃСЏВ» РІ СЂРµР¶РёРјРµ USB)вЂ¦");

            // The phone app sends CONFIG the moment it opens the accessory. Until then the endpoint
            // would just NAK our audio вЂ” so don't announce a client before it speaks.
            while (!ct.IsCancellationRequested)
            {
                var r = stream.Prefetch(500);
                if (r > 0) break;
                if (r < 0)
                {
                    SetStatus("USB: С‚РµР»РµС„РѕРЅ РѕС‚РєР»СЋС‡С‘РЅ");
                    return;
                }
            }
            if (ct.IsCancellationRequested) return;

            SetStatus("USB: РїРѕРґРєР»СЋС‡РµРЅРѕ (РєР°Р±РµР»СЊ)");
            Log?.Invoke("USB: РїСЂРёР»РѕР¶РµРЅРёРµ РЅР° С‚РµР»РµС„РѕРЅРµ РїРѕРґРєР»СЋС‡РёР»РѕСЃСЊ (СЂРµР¶РёРј Р°РєСЃРµСЃСЃСѓР°СЂР°)");
            var closed = new ManualResetEventSlim(false);
            stream.Closed += () => closed.Set();
            ClientAccepted?.Invoke(stream, "USB (РєР°Р±РµР»СЊ)");

            // Hold the device until the session ends, so we don't open it twice.
            while (!closed.Wait(500, ct)) { }
            SetStatus("USB: СЃРµР°РЅСЃ Р·Р°РІРµСЂС€С‘РЅ");
            Thread.Sleep(1000); // give the phone side time to reopen before we claim again
        }
        finally
        {
            if (stream != null) stream.Dispose();
            else LibUsb.libusb_close(h);
        }
    }

    /// <summary>Walks the raw configuration descriptor for the first interface with a bulk IN and bulk OUT endpoint.</summary>
    private static bool TryFindBulkEndpoints(IntPtr h, out int iface, out byte inEp, out byte outEp, out int maxPacket)
    {
        iface = 0; inEp = 0; outEp = 0; maxPacket = 512;

        var head = new byte[9];
        if (LibUsb.libusb_control_transfer(h, 0x80, 6, 0x0200, 0, head, 9, 1500) < 9) return false;
        var total = head[2] | (head[3] << 8);
        var cfg = new byte[Math.Max(total, 9)];
        var got = LibUsb.libusb_control_transfer(h, 0x80, 6, 0x0200, 0, cfg, (ushort)cfg.Length, 1500);
        if (got < 9) return false;

        var curIface = -1;
        byte curIn = 0, curOut = 0;
        var curMax = 512;

        var pos = 0;
        while (pos + 2 <= got)
        {
            var len = cfg[pos];
            var type = cfg[pos + 1];
            if (len < 2 || pos + len > got) break;
            if (type == 4 && len >= 9) // interface descriptor: the previous one may already be complete
            {
                if (curIface >= 0 && curIn != 0 && curOut != 0) break;
                curIface = cfg[pos + 2];
                curIn = 0; curOut = 0;
            }
            else if (type == 5 && len >= 7 && curIface >= 0) // endpoint descriptor
            {
                var addr = cfg[pos + 2];
                var attrs = cfg[pos + 3];
                if ((attrs & 3) == 2) // bulk
                {
                    if ((addr & 0x80) != 0) { curIn = addr; curMax = cfg[pos + 4] | (cfg[pos + 5] << 8); }
                    else curOut = addr;
                }
            }
            pos += len;
        }
        if (curIface < 0 || curIn == 0 || curOut == 0) return false;
        iface = curIface; inEp = curIn; outEp = curOut; maxPacket = curMax;
        return true;
    }
}
