using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace AudioBridge.Server.Core;

/// <summary>
/// Recognises virtual audio outputs ("virtual cables"). Windows can only get a new, separate output
/// device from a kernel-mode audio driver, and those must be signed by Microsoft to load with
/// Secure Boot — so the server doesn't ship one. Instead it finds a signed virtual cable the user
/// has installed and captures from it: pick that output in Windows and the audio goes only to the
/// phone (the output's name is whatever the user renames it to, e.g. the phone's name).
/// </summary>
public static class VirtualAudio
{
    // Best first. Matched case-insensitively as a substring of the endpoint's friendly name.
    private static readonly string[] Preferred =
    {
        "cable input",              // VB-Audio VB-CABLE ("CABLE Input (VB-Audio Virtual Cable)")
        "hi-fi cable",              // VB-Audio Hi-Fi Cable
        "vb-audio",
        "virtual audio cable",      // Muzychenko VAC ("Line 1 (Virtual Audio Cable)")
        "voicemeeter",
        "scream",
        "audiorelay",
        "virtual",
    };

    // Recognised as virtual (marked in the list) but never auto-picked: Steam's output only exists
    // for Remote Play and silently grabbing it would surprise people.
    private static readonly string[] KnownButNotAuto = { "steam streaming speakers" };

    /// <summary>0 = best match ... higher = weaker; -1 = not a virtual output.</summary>
    public static int Rank(string friendlyName)
    {
        for (var i = 0; i < Preferred.Length; i++)
        {
            if (friendlyName.Contains(Preferred[i], StringComparison.OrdinalIgnoreCase)) return i;
        }
        for (var i = 0; i < KnownButNotAuto.Length; i++)
        {
            if (friendlyName.Contains(KnownButNotAuto[i], StringComparison.OrdinalIgnoreCase)) return Preferred.Length + i;
        }
        return -1;
    }

    public static bool IsVirtual(MMDevice device) => Rank(device.FriendlyName) >= 0;

    /// <summary>The best virtual output to capture automatically, or null if the user has none installed.</summary>
    public static MMDevice? FindBestForAuto(IEnumerable<MMDevice> devices) =>
        devices
            .Select(d => (device: d, rank: Rank(d.FriendlyName)))
            .Where(x => x.rank >= 0 && x.rank < Preferred.Length)
            .OrderBy(x => x.rank)
            .Select(x => x.device)
            .FirstOrDefault();
}
