using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioBridge.Server.Core;

public sealed class PcAudioChunk
{
    public required float[] Interleaved { get; init; }
    public required int SampleCountPerChannel { get; init; }
}

/// <summary>
/// Captures system output audio (WASAPI loopback, event-driven) and re-cuts it into fixed 20 ms
/// chunks <i>as the data arrives</i> — never on a timer. The previous design pulled 20 ms slices on
/// a coarse Windows timer (std ~7 ms jitter, 26% of intervals &gt;30 ms), so the network saw an
/// irregular stream and any phone without a deep buffer underran. Here the cadence is the audio
/// device's own clock. Also plays back mic audio received from the phone.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int ChunkMillis = 20;
    public const int MicSampleRate = 16000;

    public const int BtSampleRate = 48000;
    public const int BtChannels = 2;

    // If WASAPI delivers nothing for this long we start synthesizing silence at real-time rate, so
    // the phone's playout buffer doesn't run dry and drift while the device is idle.
    private const int IdleAfterMs = 60;

    // Mic playback on the PC: keep this much queued before/after an underrun, drop beyond the cap.
    private const int MicPrimeMs = 60;
    private const int MicMaxBufferedMs = 250;

    private LoopbackCapture? _capture;
    private readonly object _emitLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _idleThread;
    private volatile bool _idleThreadRun;

    // Raw (native-format) path
    private readonly SampleFifo _rawFifo = new();
    private int _rawChunkSamples; // total interleaved floats per 20 ms chunk

    // 48 kHz stereo path (Opus/Bluetooth): native samples -> channel map -> resample -> FIFO
    private ResamplingPath? _bt;
    private readonly List<float[]> _btChunks = new();

    private float[] _convert = new float[8192];
    private long _lastPacketMs;
    private bool _idle;
    private bool _threadTuned;

    private WasapiOut? _micOut;
    private BufferedWaveProvider? _micPlaybackBuffer;
    private bool _micPrimed;

    public int NativeSampleRate { get; private set; }
    public int NativeChannels { get; private set; }

    /// <summary>Native-format chunks — used for the lossless Wi-Fi/USB path.</summary>
    public event Action<PcAudioChunk>? PcChunkReady;

    /// <summary>Fixed 48kHz/stereo chunks — used for the Opus/Bluetooth path (Opus only accepts
    /// specific sample rates, so this is resampled independently of the raw path above).</summary>
    public event Action<PcAudioChunk>? PcChunkReady48kStereo;

    public event Action<string>? Log;

    public static List<MMDevice> GetRenderDevices() =>
        new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList2();

    public void StartCapture(MMDevice? renderDevice)
    {
        StopCapture();

        var device = renderDevice ?? new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var capture = new LoopbackCapture(device);
        var fmt = capture.WaveFormat;
        NativeSampleRate = fmt.SampleRate;
        NativeChannels = fmt.Channels;
        Log?.Invoke($"Capture format: {NativeSampleRate} Hz, {NativeChannels} ch, {fmt.BitsPerSample} bit ({fmt.Encoding}), event-driven");

        lock (_emitLock)
        {
            _rawFifo.Clear();
            _rawChunkSamples = NativeSampleRate * ChunkMillis / 1000 * NativeChannels;
            _bt = new ResamplingPath(NativeSampleRate, NativeChannels, BtSampleRate, BtChannels, ChunkMillis);
            _lastPacketMs = _clock.ElapsedMilliseconds;
            _idle = false;
        }

        capture.DataAvailable += (_, e) =>
        {
            TuneCaptureThreadOnce();
            OnPacket(e.Buffer, e.BytesRecorded, fmt);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Log?.Invoke($"Capture stopped with error: {e.Exception.Message}");
        };
        _capture = capture;
        capture.StartRecording();

        _idleThreadRun = true;
        _idleThread = new Thread(IdleLoop) { IsBackground = true, Name = "AudioBridge-idle" };
        _idleThread.Start();
    }

    private void OnPacket(byte[] data, int bytes, WaveFormat fmt)
    {
        var count = ConvertToFloat(data, bytes, fmt);
        if (count == 0) return;

        lock (_emitLock)
        {
            _lastPacketMs = _clock.ElapsedMilliseconds;
            _idle = false;

            _rawFifo.Append(_convert.AsSpan(0, count));
            while (_rawFifo.TryTake(_rawChunkSamples, out var chunk))
            {
                EmitRaw(chunk);
            }

            var bt = _bt;
            if (bt != null)
            {
                _btChunks.Clear();
                bt.Push(_convert.AsSpan(0, count), _btChunks);
                foreach (var chunk in _btChunks) EmitBt(chunk);
            }
        }
    }

    // Runs on a dedicated thread; only does anything when WASAPI has gone quiet.
    private void IdleLoop()
    {
        while (_idleThreadRun)
        {
            Thread.Sleep(5);
            try
            {
                lock (_emitLock)
                {
                    var bt = _bt;
                    if (bt == null) continue;
                    var now = _clock.ElapsedMilliseconds;
                    if (now - _lastPacketMs <= IdleAfterMs)
                    {
                        _idle = false;
                        continue;
                    }

                    if (!_idle)
                    {
                        // Entering idle: flush any partial chunk (padded) so nothing stale lingers,
                        // then start the real-time silence schedule from "now".
                        _idle = true;
                        _idleDueMs = now;
                        var rawTail = _rawFifo.TakePadded(_rawChunkSamples);
                        if (rawTail != null) EmitRaw(rawTail);
                        var btTail = bt.TakePaddedTail();
                        if (btTail != null) EmitBt(btTail);
                    }

                    while (_idleDueMs <= now)
                    {
                        EmitRaw(new float[_rawChunkSamples]);
                        EmitBt(new float[bt.ChunkSamples]);
                        _idleDueMs += ChunkMillis;
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Idle generator error: {ex.Message}");
            }
        }
    }

    private long _idleDueMs;

    private void EmitRaw(float[] chunk) =>
        PcChunkReady?.Invoke(new PcAudioChunk { Interleaved = chunk, SampleCountPerChannel = chunk.Length / NativeChannels });

    private void EmitBt(float[] chunk) =>
        PcChunkReady48kStereo?.Invoke(new PcAudioChunk { Interleaved = chunk, SampleCountPerChannel = chunk.Length / BtChannels });

    private int ConvertToFloat(byte[] data, int bytes, WaveFormat fmt)
    {
        var bytesPer = fmt.BitsPerSample / 8;
        if (bytesPer == 0) return 0;
        var n = bytes / bytesPer;
        if (_convert.Length < n) _convert = new float[n * 2];

        if (IsFloat(fmt) && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(data, 0, _convert, 0, n * sizeof(float));
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < n; i++) _convert[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        }
        else if (fmt.BitsPerSample == 24)
        {
            for (var i = 0; i < n; i++)
            {
                var o = i * 3;
                var v = (data[o] << 8) | (data[o + 1] << 16) | (data[o + 2] << 24); // sign-extends via the top byte
                _convert[i] = (v >> 8) / 8388608f;
            }
        }
        else if (fmt.BitsPerSample == 32)
        {
            for (var i = 0; i < n; i++) _convert[i] = BitConverter.ToInt32(data, i * 4) / 2147483648f;
        }
        else
        {
            throw new NotSupportedException($"Unsupported capture format: {fmt.BitsPerSample}-bit {fmt.Encoding}");
        }
        return n;
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private static bool IsFloat(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.IeeeFloat ||
        (f is WaveFormatExtensible x && x.SubFormat == IeeeFloatSubFormat);

    // The capture callback thread is time-critical: raise its priority and register it with the
    // multimedia scheduler once (best-effort — failures are harmless).
    private void TuneCaptureThreadOnce()
    {
        if (_threadTuned) return;
        _threadTuned = true;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            AvSetMmThreadCharacteristics("Pro Audio", out _);
        }
        catch { /* best effort */ }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    public void StopCapture()
    {
        _idleThreadRun = false;
        _idleThread?.Join(200);
        _idleThread = null;
        var cap = _capture;
        _capture = null;
        if (cap != null)
        {
            try { cap.StopRecording(); } catch { /* ignore */ }
            cap.Dispose();
        }
        _threadTuned = false;
    }

    public static List<MMDevice> GetOutputDevicesForMic() =>
        new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList2();

    public void StartMicPlayback(MMDevice? outputDevice)
    {
        StopMicPlayback();

        var format = new WaveFormat(MicSampleRate, 16, 1);
        _micPlaybackBuffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };
        _micPrimed = false;

        _micOut = outputDevice is null ? new WasapiOut() : new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 50);
        _micOut.Init(_micPlaybackBuffer);
        _micOut.Play();
    }

    public void StopMicPlayback()
    {
        _micOut?.Stop();
        _micOut?.Dispose();
        _micOut = null;
        _micPlaybackBuffer = null;
    }

    /// <summary>
    /// Feed decoded mic PCM16 mono samples (16kHz) received from the phone into the playback buffer.
    /// Keeps a small cushion queued (re-priming with silence after an underrun, instead of playing
    /// each late frame the instant it lands) and drops input if latency has grown past the cap.
    /// </summary>
    public void PlayMicSamples(short[] pcm, int sampleCount)
    {
        var buf = _micPlaybackBuffer;
        if (buf is null) return;

        var bufferedMs = buf.BufferedDuration.TotalMilliseconds;
        if (!_micPrimed || bufferedMs < 5)
        {
            var silence = new byte[MicSampleRate * MicPrimeMs / 1000 * 2];
            buf.AddSamples(silence, 0, silence.Length);
            _micPrimed = true;
        }
        else if (bufferedMs > MicMaxBufferedMs)
        {
            return;
        }

        var bytes = new byte[sampleCount * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        buf.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        StopCapture();
        StopMicPlayback();
    }
}

/// <summary>Takes the first two channels of a multichannel source as L/R (simple downmix — good
/// enough for the uncommon case of a >2 channel default render device; not a proper LFE/surround mix).</summary>
internal sealed class StereoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public StereoDownmixSampleProvider(ISampleProvider source, int sourceChannels)
    {
        _source = source;
        _sourceChannels = sourceChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / 2;
        var sourceNeeded = framesRequested * _sourceChannels;
        if (_sourceBuffer.Length < sourceNeeded) _sourceBuffer = new float[sourceNeeded];
        var sourceRead = _source.Read(_sourceBuffer, 0, sourceNeeded);
        var framesRead = sourceRead / _sourceChannels;
        for (var i = 0; i < framesRead; i++)
        {
            buffer[offset + i * 2] = _sourceBuffer[i * _sourceChannels];
            buffer[offset + i * 2 + 1] = _sourceBuffer[i * _sourceChannels + 1];
        }
        return framesRead * 2;
    }
}

internal static class MmDeviceCollectionExtensions
{
    public static List<MMDevice> ToList2(this MMDeviceCollection collection)
    {
        var list = new List<MMDevice>();
        foreach (var d in collection) list.Add(d);
        return list;
    }
}
