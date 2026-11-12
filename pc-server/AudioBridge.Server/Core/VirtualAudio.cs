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
        // Voicemeeter's adapter is also called "VB-Audio ..."; it must rank below the real cables.
        if (friendlyName.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase)) return Array.IndexOf(Preferred, "voicemeeter");
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

    /// <summary>A real cable (playback side feeds a matching microphone) — Steam's output is virtual but is not one.</summary>
    public static bool IsCable(MMDevice device) { var r = Rank(device.FriendlyName); return r >= 0 && r < Preferred.Length; }

    /// <summary>The best virtual output to capture automatically, or null if the user has none installed.</summary>
    public static MMDevice? FindBestForAuto(IEnumerable<MMDevice> devices) =>
        devices
            .Select(d => (device: d, rank: Rank(d.FriendlyName)))
            .Where(x => x.rank >= 0 && x.rank < Preferred.Length)
            .OrderBy(x => x.rank)
            .Select(x => x.device)
            .FirstOrDefault();

    private static readonly PropertyKey ContainerIdKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    /// <summary>
    /// The microphone (capture endpoint) that corresponds to a virtual cable's playback side: what a program
    /// like Discord has to pick as "input device" to hear what is played into <paramref name="render"/>.
    /// Matched by the shared device container; failing that by the adapter name in brackets
    /// ("CABLE Input (VB-Audio Virtual Cable)" ↔ "CABLE Output (VB-Audio Virtual Cable)").
    /// </summary>
    public static MMDevice? FindPartnerCapture(MMDevice render, IReadOnlyList<MMDevice> captures)
    {
        // Voicemeeter has many endpoints in one adapter: its main input pairs with bus B1.
        if (VoicemeeterRemote.IsMainInput(render.FriendlyName))
            return captures.FirstOrDefault(c => VoicemeeterRemote.IsMainMic(c.FriendlyName));

        try
        {
            var container = render.Properties[ContainerIdKey]?.Value;
            // Virtual drivers share the "no container" placeholder, which says nothing about pairing.
            if (container != null && !container.ToString()!.EndsWith("ffff-ffffffffffff", StringComparison.OrdinalIgnoreCase))
            {
                var byContainer = captures.FirstOrDefault(c =>
                {
                    try { return Equals(c.Properties[ContainerIdKey]?.Value, container); }
                    catch { return false; }
                });
                if (byContainer != null) return byContainer;
            }
        }
        catch
        {
            // fall through to the name heuristic
        }

        var name = render.FriendlyName;
        var open = name.LastIndexOf('(');
        var adapter = open >= 0 ? name[open..] : null;
        return captures
            .Where(c => IsVirtual(c) || (adapter != null && c.FriendlyName.EndsWith(adapter, StringComparison.OrdinalIgnoreCase)))
            .Where(c => adapter == null || c.FriendlyName.EndsWith(adapter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.FriendlyName.Contains("output", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }
}
