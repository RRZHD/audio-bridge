using System;
using NAudio.CoreAudioApi;

namespace AudioBridge.Server.Core;

/// <summary>
/// Keeps the Windows volume of the captured output and the phone's media volume in step.
///
/// WASAPI loopback captures the stream <i>before</i> the endpoint volume, so moving the Windows
/// slider changes nothing on the phone by itself. Instead the slider is mirrored: a change on the PC
/// is sent to the phone (which sets its media volume), a change on the phone is applied to the
/// endpoint here. Mute travels along. Changes made by the sync itself are not echoed back.
/// </summary>
public sealed class VolumeSync : IDisposable
{
    private readonly object _gate = new();
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;
    private long _suppressUntilTicks;
    private int _lastAppliedLevel = -1;
    private bool _lastAppliedMuted;

    /// <summary>The Windows volume changed (0..100, muted) — forward it to the phones.</summary>
    public event Action<int, bool>? Changed;

    public void Attach(MMDevice device)
    {
        Detach();
        lock (_gate)
        {
            _device = device;
            _volume = device.AudioEndpointVolume;
            _volume.OnVolumeNotification += OnNotification;
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            if (_volume != null) _volume.OnVolumeNotification -= OnNotification;
            _volume = null;
            _device = null;
        }
    }

    /// <summary>Current Windows state, or null if nothing is attached.</summary>
    public (int Level, bool Muted)? Current
    {
        get
        {
            lock (_gate)
            {
                if (_volume == null) return null;
                try { return (ToLevel(_volume.MasterVolumeLevelScalar), _volume.Mute); }
                catch { return null; }
            }
        }
    }

    /// <summary>Apply a volume reported by the phone.</summary>
    public void SetFromPhone(int level, bool muted)
    {
        lock (_gate)
        {
            if (_volume == null) return;
            try
            {
                level = Math.Clamp(level, 0, 100);
                _lastAppliedLevel = level;
                _lastAppliedMuted = muted;
                _suppressUntilTicks = Environment.TickCount64 + 500;
                if (ToLevel(_volume.MasterVolumeLevelScalar) != level) _volume.MasterVolumeLevelScalar = level / 100f;
                if (_volume.Mute != muted) _volume.Mute = muted;
            }
            catch
            {
                // Some endpoints refuse volume changes — nothing useful to do.
            }
        }
    }

    private void OnNotification(AudioVolumeNotificationData data)
    {
        var level = ToLevel(data.MasterVolume);
        lock (_gate)
        {
            // The echo of what SetFromPhone just did.
            if (Environment.TickCount64 < _suppressUntilTicks && level == _lastAppliedLevel && data.Muted == _lastAppliedMuted) return;
        }
        Changed?.Invoke(level, data.Muted);
    }

    private static int ToLevel(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f);

    public void Dispose() => Detach();
}
