using System;
using System.IO;
using System.Text.Json;

namespace AudioBridge.Server.Core;

/// <summary>Remembers the user's device choices between runs (%AppData%\AudioBridge\server.json).</summary>
public sealed class ServerSettings
{
    /// <summary>Endpoint ID of the output to capture; null = automatic (a virtual cable if one is installed).</summary>
    public string? CaptureDeviceId { get; set; }

    /// <summary>Endpoint ID for playing the phone's microphone; null = Windows default output.</summary>
    public string? MicOutputDeviceId { get; set; }

    public int? Port { get; set; }

    /// <summary>Look for a phone on USB and switch it into accessory mode (needs UsbDk). On by default.</summary>
    public bool UsbEnabled { get; set; } = true;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge", "server.json");

    public static ServerSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(FilePath)) ?? new ServerSettings();
        }
        catch
        {
            return new ServerSettings(); // first run or unreadable file — defaults are fine
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember a choice must never break the app.
        }
    }
}
