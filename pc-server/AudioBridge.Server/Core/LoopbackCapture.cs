using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioBridge.Server.Core;

/// <summary>
/// WASAPI loopback capture in <b>event-driven</b> mode. NAudio's own WasapiLoopbackCapture polls with
/// Thread.Sleep(bufferLength/2), which on Windows measured as a ~62 ms cadence of 50-70 ms bursts —
/// pure added latency plus jitter. Event-driven delivery gives steady 10 ms packets (verified by
/// measurement, including while nothing is playing).
/// </summary>
public sealed class LoopbackCapture : WasapiCapture
{
    public LoopbackCapture(MMDevice renderDevice) : base(renderDevice, useEventSync: true, audioBufferMillisecondsLength: 40)
    {
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}
