using System;
using System.Collections.Generic;
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
/// Captures system output audio (WASAPI loopback) and chunks it into fixed 20ms slices at the
/// device's native format (no resampling — kept lossless for the raw Wi-Fi/USB path). Also plays
/// back mic audio received from the phone through a chosen output device.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int ChunkMillis = 20;
    public const int MicSampleRate = 16000;

    public const int BtSampleRate = 48000;
    public const int BtChannels = 2;

    private WasapiLoopbackCapture? _capture;
    private System.Threading.Timer? _chunkTimer;
    private System.Threading.Timer? _btChunkTimer;

    private WasapiOut? _micOut;
    private BufferedWaveProvider? _micPlaybackBuffer;

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

        _capture = renderDevice is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(renderDevice);
        NativeSampleRate = _capture.WaveFormat.SampleRate;
        NativeChannels = _capture.WaveFormat.Channels;
        Log?.Invoke($"Capture format: {NativeSampleRate} Hz, {NativeChannels} ch, {_capture.WaveFormat.BitsPerSample} bit ({_capture.WaveFormat.Encoding})");

        // Raw chain: native format, no resampling — feeds the lossless Wi-Fi/USB path.
        var rawBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1),
        };
        var rawSampleProvider = rawBuffer.ToSampleProvider();

        // BT chain: independently buffered from the same bytes, resampled/remixed to a fixed
        // 48kHz/stereo so it can always be fed into Opus regardless of the device's native format.
        var btBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1),
        };
        ISampleProvider btSampleProvider = btBuffer.ToSampleProvider();
        if (NativeChannels == 1)
        {
            btSampleProvider = new MonoToStereoSampleProvider(btSampleProvider);
        }
        else if (NativeChannels > 2)
        {
            btSampleProvider = new StereoDownmixSampleProvider(btSampleProvider, NativeChannels);
        }
        if (NativeSampleRate != BtSampleRate)
        {
            btSampleProvider = new WdlResamplingSampleProvider(btSampleProvider, BtSampleRate);
        }

        _capture.DataAvailable += (_, e) =>
        {
            rawBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            btBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Log?.Invoke($"Capture stopped with error: {e.Exception.Message}");
        };
        _capture.StartRecording();

        var samplesPerChunk = NativeSampleRate * ChunkMillis / 1000;
        var floatBuf = new float[samplesPerChunk * NativeChannels];
        _chunkTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var read = rawSampleProvider.Read(floatBuf, 0, floatBuf.Length);
                if (read < floatBuf.Length)
                {
                    Array.Clear(floatBuf, read, floatBuf.Length - read); // pad with silence
                }
                var chunk = new float[floatBuf.Length];
                Array.Copy(floatBuf, chunk, floatBuf.Length);
                PcChunkReady?.Invoke(new PcAudioChunk { Interleaved = chunk, SampleCountPerChannel = samplesPerChunk });
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Chunk timer error: {ex.Message}");
            }
        }, null, 0, ChunkMillis);

        var btSamplesPerChunk = BtSampleRate * ChunkMillis / 1000;
        var btFloatBuf = new float[btSamplesPerChunk * BtChannels];
        _btChunkTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var read = btSampleProvider.Read(btFloatBuf, 0, btFloatBuf.Length);
                if (read < btFloatBuf.Length)
                {
                    Array.Clear(btFloatBuf, read, btFloatBuf.Length - read);
                }
                var chunk = new float[btFloatBuf.Length];
                Array.Copy(btFloatBuf, chunk, btFloatBuf.Length);
                PcChunkReady48kStereo?.Invoke(new PcAudioChunk { Interleaved = chunk, SampleCountPerChannel = btSamplesPerChunk });
            }
            catch (Exception ex)
            {
                Log?.Invoke($"BT chunk timer error: {ex.Message}");
            }
        }, null, 0, ChunkMillis);
    }

    public void StopCapture()
    {
        _chunkTimer?.Dispose();
        _chunkTimer = null;
        _btChunkTimer?.Dispose();
        _btChunkTimer = null;
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
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

    /// <summary>Feed decoded mic PCM16 mono samples (16kHz) received from the phone into the playback buffer.</summary>
    public void PlayMicSamples(short[] pcm, int sampleCount)
    {
        if (_micPlaybackBuffer is null) return;
        var bytes = new byte[sampleCount * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _micPlaybackBuffer.AddSamples(bytes, 0, bytes.Length);
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
