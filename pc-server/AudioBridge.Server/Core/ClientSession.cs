using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AudioBridge.Server.Core;

public sealed class ClientSession : IDisposable
{
    /// <summary>Chunks (20 ms each) allowed to queue for a slow link before the oldest are dropped.
    /// Bounds latency: a stalled network costs a short glitch instead of ever-growing delay.</summary>
    public const int MaxQueuedChunks = 15;

    /// <summary>How long to wait for the phone's CONFIG before sending FORMAT with defaults.</summary>
    private const int ConfigWaitMs = 1500;

    public string Label { get; }

    /// <summary>Device name the phone reported in CONFIG (e.g. "Xperia 1 IV"), if any.</summary>
    public string? PhoneName { get; private set; }

    public bool IsConnected { get; private set; } = true;

    public event Action<ClientSession>? Disconnected;

    /// <summary>Raised when the phone's CONFIG (name etc.) arrives, so the UI can refresh.</summary>
    public event Action<ClientSession>? Changed;

    public event Action<string>? Log;

    private readonly Stream _stream;
    private readonly AudioEngine _engine;
    private readonly IPcAudioCodec _pcCodec;
    private readonly IMicAudioCodec _micCodec;
    private readonly int _pcSampleRate;
    private readonly int _pcChannels;
    private readonly bool _useNativeFormat;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly short[] _micDecodeScratch = new short[AudioEngine.MicSampleRate / 10];
    private readonly Channel<PcAudioChunk> _queue;
    private readonly TaskCompletionSource _configReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<float[]> _convChunks = new();
    private ResamplingPath? _rawConv;
    private long _lastSentTicks = Environment.TickCount64;
    private System.Threading.Timer? _pingTimer;
    private long _dropped;
    private long _droppedReported;
    private int _pendingBitrate; // 0 = none; applied by the writer so the encoder is never touched concurrently
    private volatile int _maxRawRate; // 0 = no limit
    private int _disconnected;

    /// <param name="useNativeFormat">true = subscribe to the engine's native-format chunk stream
    /// (Wi-Fi/USB raw path); false = subscribe to the fixed 48kHz/stereo stream (Bluetooth/Opus path).</param>
    public ClientSession(Stream stream, string label, AudioEngine engine, IPcAudioCodec pcCodec, IMicAudioCodec micCodec,
        int pcSampleRate, int pcChannels, bool useNativeFormat)
    {
        _stream = stream;
        Label = label;
        _engine = engine;
        _pcCodec = pcCodec;
        _micCodec = micCodec;
        _pcSampleRate = pcSampleRate;
        _pcChannels = pcChannels;
        _useNativeFormat = useNativeFormat;
        _queue = Channel.CreateBounded<PcAudioChunk>(
            new BoundedChannelOptions(MaxQueuedChunks)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true, // chunks arrive from the engine's single emit path
            },
            itemDropped: _ => Interlocked.Increment(ref _dropped));
    }

    public void Start()
    {
        // The writer sends FORMAT first, then audio, strictly in order. (Previously every chunk was
        // its own fire-and-forget task: chunks could be reordered, FORMAT could lose the race, and
        // the stateful Opus encoder was invoked concurrently from several thread-pool threads.)
        _ = Task.Run(ReaderLoopAsync);
        _ = Task.Run(WriterLoopAsync);
        _pingTimer = new System.Threading.Timer(_ => _ = SendPingIfIdleAsync(), null, 3000, 3000);
    }

    private void OnPcChunkReady(PcAudioChunk chunk)
    {
        if (!IsConnected) return;

        var conv = _rawConv;
        if (conv == null)
        {
            _queue.Writer.TryWrite(chunk); // never blocks; drops the oldest if the link can't keep up
            return;
        }

        _convChunks.Clear();
        conv.Push(chunk.Interleaved, _convChunks);
        foreach (var c in _convChunks)
        {
            _queue.Writer.TryWrite(new PcAudioChunk { Interleaved = c, SampleCountPerChannel = c.Length / conv.Channels });
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            // The phone says up front what it can play (and its name); wait briefly so FORMAT can
            // reflect that instead of guessing. Clients that never send CONFIG just get defaults.
            await Task.WhenAny(_configReceived.Task, Task.Delay(ConfigWaitMs, _cts.Token)).ConfigureAwait(false);

            var rate = _pcSampleRate;
            var channels = _pcChannels;
            if (_useNativeFormat && _pcCodec.Kind == PcAudioCodecKind.PcmFloat32)
            {
                // Raw lossless path: never send a rate the phone said it can't play (e.g. a 192 kHz
                // Windows mix format — the Xperia's audio stack accepts such a track but never
                // consumes it), and never more than stereo (the phone plays stereo at most).
                var maxRate = _maxRawRate;
                var targetRate = maxRate > 0 ? Math.Min(rate, maxRate) : rate;
                var targetChannels = Math.Min(channels, 2);
                if (targetRate != rate || targetChannels != channels)
                {
                    _rawConv = new ResamplingPath(rate, channels, targetRate, targetChannels, AudioEngine.ChunkMillis);
                    Log?.Invoke($"{Label}: поток ПК {rate} Гц/{channels} кан. → {targetRate} Гц/{targetChannels} кан. (предел телефона)");
                    rate = targetRate;
                    channels = targetChannels;
                }
            }

            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), rate);
            payload[4] = (byte)channels;
            payload[5] = (byte)_pcCodec.Kind;
            await FrameIO.WriteFrameAsync(_stream, FrameType.Format, payload, _writeLock, _cts.Token).ConfigureAwait(false);

            // Only now start receiving audio, so nothing can precede FORMAT.
            if (_useNativeFormat) _engine.PcChunkReady += OnPcChunkReady;
            else _engine.PcChunkReady48kStereo += OnPcChunkReady;

            await foreach (var chunk in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var bitrate = Interlocked.Exchange(ref _pendingBitrate, 0);
                if (bitrate > 0) _pcCodec.SetBitrate(bitrate);

                var encoded = _pcCodec.Encode(chunk.Interleaved, chunk.SampleCountPerChannel);
                await FrameIO.WriteFrameAsync(_stream, FrameType.AudioPc, encoded, _writeLock, _cts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastSentTicks, Environment.TickCount64);
            }
        }
        catch (OperationCanceledException)
        {
            // disconnecting
        }
        catch (Exception ex)
        {
            if (IsConnected) Log?.Invoke($"{Label}: ошибка отправки — {ex.GetType().Name}: {ex.Message}");
        }
        Disconnect();
    }

    private async Task SendPingIfIdleAsync()
    {
        if (!IsConnected) return;

        var dropped = Interlocked.Read(ref _dropped);
        if (dropped != _droppedReported)
        {
            Log?.Invoke($"{Label}: сеть не успевает — пропущено {dropped - _droppedReported} чанков по 20 мс");
            _droppedReported = dropped;
        }

        if (Environment.TickCount64 - Interlocked.Read(ref _lastSentTicks) < 3000) return;
        try
        {
            await FrameIO.WriteFrameAsync(_stream, FrameType.Ping, Array.Empty<byte>(), _writeLock, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            Disconnect();
        }
    }

    private async Task ReaderLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await FrameIO.ReadFrameAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (frame.Type == FrameType.AudioMic)
                {
                    var decoded = _micCodec.Decode(frame.Payload, _micDecodeScratch);
                    _engine.PlayMicSamples(_micDecodeScratch, decoded);
                }
                else if (frame.Type == FrameType.Config && frame.Payload.Length >= 4)
                {
                    ApplyConfig(frame.Payload);
                }
                // PING and unknown types: no-op, receiving anything resets the read timeout implicitly.
            }
        }
        catch (Exception ex)
        {
            if (IsConnected) Log?.Invoke($"{Label}: клиент закрыл соединение — {ex.GetType().Name}: {ex.Message}");
        }
        Disconnect();
    }

    /// <summary>CONFIG: uint32 pcAudioBitrateBps [, uint32 maxRawSampleRate [, byte nameLen, utf8 name]] — see PROTOCOL.md.</summary>
    private void ApplyConfig(byte[] p)
    {
        var bitrate = BitConverter.ToInt32(p, 0);
        if (bitrate > 0) Interlocked.Exchange(ref _pendingBitrate, bitrate);

        if (p.Length >= 8) _maxRawRate = Math.Max(0, BitConverter.ToInt32(p, 4));

        if (p.Length >= 9)
        {
            var len = p[8];
            if (len > 0 && p.Length >= 9 + len) PhoneName = Encoding.UTF8.GetString(p, 9, len);
        }

        _configReceived.TrySetResult();
        Changed?.Invoke(this);
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        IsConnected = false;
        if (_useNativeFormat) _engine.PcChunkReady -= OnPcChunkReady;
        else _engine.PcChunkReady48kStereo -= OnPcChunkReady;
        _pingTimer?.Dispose();
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _stream.Dispose(); } catch { /* ignore */ }
        Disconnected?.Invoke(this);
    }

    public void Dispose() => Disconnect();
}
