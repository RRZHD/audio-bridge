using System;
using System.Runtime.InteropServices;

namespace AudioBridge.Server.Core;

/// <summary>
/// Presses the Windows media keys, so a headset button on the phone controls whatever is playing on
/// the PC exactly like a keyboard's play/pause key (the foreground/last-active media player reacts).
/// </summary>
public static class MediaKeys
{
    public const byte PlayPause = 1;
    public const byte Next = 2;
    public const byte Previous = 3;

    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static string? Press(byte command)
    {
        var (vk, name) = command switch
        {
            PlayPause => (VkMediaPlayPause, "пауза/воспроизведение"),
            Next => (VkMediaNextTrack, "следующий трек"),
            Previous => (VkMediaPrevTrack, "предыдущий трек"),
            _ => ((byte)0, ""),
        };
        if (vk == 0) return null;
        keybd_event(vk, 0, KeyEventExtended, UIntPtr.Zero);
        keybd_event(vk, 0, KeyEventExtended | KeyEventKeyUp, UIntPtr.Zero);
        return name;
    }
}
