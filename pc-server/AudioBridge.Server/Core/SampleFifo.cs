using System;
using NAudio.Wave;

namespace AudioBridge.Server.Core;

/// <summary>
/// Simple float FIFO used to re-cut variable-size capture packets into fixed-size chunks. Not
/// thread-safe on its own — callers serialize access (see AudioEngine's emit lock).
/// </summary>
public sealed class SampleFifo
{
    private float[] _buf = new float[16384];
    private int _start;
    private int _end;

    public int Count => _end - _start;

    public void Append(ReadOnlySpan<float> src)
    {
        if (src.Length == 0) return;
        if (_end + src.Length > _buf.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start * sizeof(float), _buf, 0, (_end - _start) * sizeof(float));
                _end -= _start;
                _start = 0;
            }
            if (_end + src.Length > _buf.Length)
            {
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _end + src.Length));
            }
        }
        src.CopyTo(_buf.AsSpan(_end));
        _end += src.Length;
    }

    /// <summary>Takes exactly <paramref name="n"/> samples into a fresh array, or returns false if fewer are available.</summary>
    public bool TryTake(int n, out float[] chunk)
    {
        if (Count < n)
        {
            chunk = Array.Empty<float>();
            return false;
        }
        chunk = new float[n];
        Buffer.BlockCopy(_buf, _start * sizeof(float), chunk, 0, n * sizeof(float));
        Consume(n);
        return true;
    }

    /// <summary>Takes whatever is buffered (up to <paramref name="n"/>) and zero-pads to exactly n; null if empty.</summary>
    public float[]? TakePadded(int n)
    {
        if (Count == 0) return null;
        var chunk = new float[n];
        var take = Math.Min(n, Count);
        Buffer.BlockCopy(_buf, _start * sizeof(float), chunk, 0, take * sizeof(float));
        Consume(take);
        return chunk;
    }

    /// <summary>Copies up to <paramref name="count"/> samples out (no padding); returns how many.</summary>
    public int ReadInto(float[] dst, int offset, int count)
    {
        var take = Math.Min(count, Count);
        if (take > 0)
        {
            Buffer.BlockCopy(_buf, _start * sizeof(float), dst, offset * sizeof(float), take * sizeof(float));
            Consume(take);
        }
        return take;
    }

    public void Clear()
    {
        _start = _end = 0;
    }

    private void Consume(int n)
    {
        _start += n;
        if (_start == _end) _start = _end = 0;
    }
}

/// <summary>Feeds an NAudio sample-provider chain from a <see cref="SampleFifo"/> without ever
/// padding with silence: Read returns only what's buffered, so the chain is drained by pulling
/// until it returns less than requested.</summary>
public sealed class FifoSampleProvider : ISampleProvider
{
    private readonly SampleFifo _fifo;

    public FifoSampleProvider(SampleFifo fifo, int sampleRate, int channels)
    {
        _fifo = fifo;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count) => _fifo.ReadInto(buffer, offset, count);
}
