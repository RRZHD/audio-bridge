using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioBridge.Server.Core;

/// <summary>
/// Converts a stream of interleaved float samples to another sample rate / channel count and
/// re-cuts it into exact fixed-length chunks. Input may arrive in any size (capture packets or
/// whole chunks); output comes only as complete chunks, never zero-padded mid-stream.
/// </summary>
public sealed class ResamplingPath
{
    private readonly SampleFifo _in = new();
    private readonly SampleFifo _out = new();
    private readonly ISampleProvider _chain;
    private readonly float[] _tmp;

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Interleaved floats in one output chunk.</summary>
    public int ChunkSamples { get; }

    public ResamplingPath(int srcRate, int srcChannels, int dstRate, int dstChannels, int chunkMillis)
    {
        SampleRate = dstRate;
        Channels = dstChannels;
        ChunkSamples = dstRate * chunkMillis / 1000 * dstChannels;
        _tmp = new float[ChunkSamples * 4];

        ISampleProvider chain = new FifoSampleProvider(_in, srcRate, srcChannels);
        if (srcChannels == 1 && dstChannels == 2) chain = new MonoToStereoSampleProvider(chain);
        else if (srcChannels > 2) chain = new StereoDownmixSampleProvider(chain, srcChannels);
        if (srcRate != dstRate) chain = new WdlResamplingSampleProvider(chain, dstRate);
        _chain = chain;
    }

    /// <summary>Feeds source samples; every complete output chunk that results is added to <paramref name="chunks"/>.</summary>
    public void Push(ReadOnlySpan<float> samples, List<float[]> chunks)
    {
        _in.Append(samples);
        int read;
        do
        {
            read = _chain.Read(_tmp, 0, _tmp.Length);
            if (read > 0) _out.Append(_tmp.AsSpan(0, read));
        } while (read == _tmp.Length); // a short read means the source FIFO is drained

        while (_out.TryTake(ChunkSamples, out var chunk)) chunks.Add(chunk);
    }

    /// <summary>Flushes a partial chunk (zero-padded), or null if nothing is buffered.</summary>
    public float[]? TakePaddedTail() => _out.TakePadded(ChunkSamples);
}
