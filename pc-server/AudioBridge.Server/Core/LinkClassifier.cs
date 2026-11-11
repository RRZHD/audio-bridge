using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace AudioBridge.Server.Core;

/// <summary>Works out what kind of link a TCP client came in over, for display in the client list.</summary>
public static class LinkClassifier
{
    // Adapter names/descriptions Windows gives a phone's USB-tethering interface.
    private static readonly string[] UsbModemHints = { "rndis", "remote ndis", "ncm", "usb ethernet", "android", "tether" };

    public static string Describe(IPEndPoint? remote, IPEndPoint? local)
    {
        if (remote != null && IPAddress.IsLoopback(remote.Address))
        {
            return $"локально, {remote}"; // legacy `adb reverse` clients arrive like this
        }

        var nic = FindAdapterOwning(local?.Address);
        var kind = nic != null && IsUsbModem(nic) ? "USB-модем" : "Wi-Fi/LAN";
        return $"{kind} ({remote})";
    }

    private static NetworkInterface? FindAdapterOwning(IPAddress? address)
    {
        if (address == null) return null;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address)));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsbModem(NetworkInterface nic)
    {
        var text = $"{nic.Name} {nic.Description}";
        return UsbModemHints.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
