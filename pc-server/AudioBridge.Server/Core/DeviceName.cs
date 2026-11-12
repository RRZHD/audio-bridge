using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;

namespace AudioBridge.Server.Core;

/// <summary>
/// <c>MMDevice.FriendlyName</c> reads the device's property store through COM on every call (~10+ ms each),
/// and the UI asks for names of every endpoint several times a second. A device's name does not change while the
/// object lives, so read it once.
/// </summary>
public static class DeviceName
{
    private static readonly ConditionalWeakTable<MMDevice, string> Cache = new();

    public static string Label(this MMDevice device)
    {
        if (Cache.TryGetValue(device, out var name)) return name;
        name = device.FriendlyName;
        Cache.AddOrUpdate(device, name);
        return name;
    }
}
