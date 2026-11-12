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

    private const int MinOpusBitrate = 64_000;

    /// <summary>Bluetooth stalls for 100-250 ms (measured) and then delivers in a burst: the queue must ride that out.</summary>
    public const int MaxQueuedChunksBluetooth = 40;

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

    /// <summary>The phone's media volume changed (0..100, muted).</summary>
    public event Action<int, bool>? VolumeReceived;

    /// <summary>A headset button on the phone: 1 = play/pause, 2 = next, 3 = previous.</summary>
    public event Action<byte>? MediaCommand;

    private readonly Stream _stream;
    private readonly AudioEngine _engine;
    private IPcAudioCodec _pcCodec;
    private readonly IMicAudioCodec _micCodec;
    private int _pcSampleRate;
    private int _pcChannels;
    private bool _useNativeFormat;
    private bool _subscribed; // the engine event for the current format is hooked up
    private volatile int _compressPref; // from CONFIG: 0 = auto, 1 = always lossless, 2 = always compressed (Opus)
    private int _configBitrate = 192_000;
    private int _switchRequested;
    private readonly Queue<(long Ticks, long Dropped)> _dropWindow = new();
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
    private long _lastMicTicks;

    /// <summary>The phone is sending its microphone right now (a mic frame in the last 2 s).</summary>
    public bool MicActive => _lastMicTicks != 0 && Environment.TickCount64 - Interlocked.Read(ref _lastMicTicks) < 2000;

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
            new BoundedChannelOptions(useNativeFormat ? MaxQueuedChunks : MaxQueuedChunksBluetooth)
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

    /// <summary>
    /// Moves this session from the lossless raw stream to Opus at 48 kHz stereo: the link could not
    /// carry ~3 Mbit/s (the queue kept overflowing), and a compressed stream that arrives is better
    /// than a lossless one that stutters. Called on the writer thread; the phone is told by a new FORMAT.
    /// </summary>
    private async Task SwitchToOpusAsync(string reason)
    {
        if (!_useNativeFormat) return;
        if (_subscribed)
        {
            _engine.PcChunkReady -= OnPcChunkReady;
            _engine.PcChunkReady48kStereo += OnPcChunkReady;
        }
        _useNativeFormat = false;
        _rawConv = null;
        while (_queue.Reader.TryRead(out _)) { } // raw chunks must not reach the Opus encoder
        _pcSampleRate = AudioEngine.BtSampleRate;
        _pcChannels = AudioEngine.BtChannels;
        _pcCodec = new OpusPcCodec(_pcSampleRate, _pcChannels, _configBitrate);
        Log?.Invoke($"{Label}: {reason} — перехожу на сжатие Opus {_configBitrate / 1000} кбит/с");

        if (_subscribed)
        {
            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), _pcSampleRate);
            payload[4] = (byte)_pcChannels;
            payload[5] = (byte)_pcCodec.Kind;
            await FrameIO.WriteFrameAsync(_stream, FrameType.Format, payload, _writeLock, _cts.Token).ConfigureAwait(false);
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            // The phone says up front what it can play (and its name); wait briefly so FORMAT can
            // reflect that instead of guessing. Clients that never send CONFIG just get defaults.
            await Task.WhenAny(_configReceived.Task, Task.Delay(ConfigWaitMs, _cts.Token)).ConfigureAwait(false);

            // The app can ask for compression outright (Wi-Fi quality setting); "auto" starts lossless and
            // falls back to Opus by itself when the link cannot carry it (see the drop monitor).
            if (_compressPref == 2 && _useNativeFormat && _pcCodec.Kind == PcAudioCodecKind.PcmFloat32)
                await SwitchToOpusAsync("по настройке приложения").ConfigureAwait(false);

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
            _subscribed = true;

            // Opus (Bluetooth) is rate-controlled: the radio link's capacity varies, and a fixed high
            // bitrate on a link that cannot carry it just piles up latency and then drops audio.
            var adaptive = _pcCodec.Kind == PcAudioCodecKind.Opus;
            var maxBitrate = _configBitrate;
            var curBitrate = maxBitrate;
            var lastChangeMs = Environment.TickCount64;
            var lastCongestedMs = 0L;
            var deepSinceMs = 0L;
            var statsAtMs = Environment.TickCount64;
            long statBytes = 0;
            double statMaxWriteMs = 0;
            var statMinBitrate = curBitrate;

            await foreach (var chunk in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _switchRequested, 0) == 1)
                {
                    await SwitchToOpusAsync("канал не успевает передавать поток без сжатия").ConfigureAwait(false);
                    adaptive = true;
                    maxBitrate = _configBitrate;
                    curBitrate = maxBitrate;
                    statMinBitrate = curBitrate;
                    lastChangeMs = Environment.TickCount64;
                    continue; // this chunk is raw: drop it, the next ones come from the 48 kHz stream
                }

                var bitrate = Interlocked.Exchange(ref _pendingBitrate, 0);
                if (bitrate > 0)
                {
                    maxBitrate = bitrate;
                    curBitrate = Math.Min(curBitrate, maxBitrate);
                    _pcCodec.SetBitrate(curBitrate);
                }

                var encoded = _pcCodec.Encode(chunk.Interleaved, chunk.SampleCountPerChannel);
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await FrameIO.WriteFrameAsync(_stream, FrameType.AudioPc, encoded, _writeLock, _cts.Token).ConfigureAwait(false);
                var writeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                Interlocked.Exchange(ref _lastSentTicks, Environment.TickCount64);

                if (!adaptive)
                {
                    // Lossless auto mode: a queue that stays deep (or writes that block) means the link cannot
                    // carry the raw stream — don't wait for the queue to overflow, switch to Opus.
                    if (_compressPref == 0 && _useNativeFormat)
                    {
                        var nowMs = Environment.TickCount64;
                        if (_queue.Reader.Count >= 10 || writeMs > 400)
                        {
                            if (deepSinceMs == 0) deepSinceMs = nowMs;
                            else if (nowMs - deepSinceMs > 2500) Interlocked.Exchange(ref _switchRequested, 1);
                        }
                        else
                        {
                            deepSinceMs = 0;
                        }
                    }
                    continue;
                }

                statBytes += encoded.Length + 5;
                if (writeMs > statMaxWriteMs) statMaxWriteMs = writeMs;
                var now = Environment.TickCount64;
                var backlog = _queue.Reader.Count;
                // A burst after a radio stall is normal (up to ~15 chunks); only a backlog that stays deep
                // means the link cannot carry this bitrate.
                var congested = backlog >= 25 || writeMs > 700;
                if (congested) lastCongestedMs = now;
                if (congested && now - lastChangeMs > 700 && curBitrate > MinOpusBitrate)
                {
                    curBitrate = Math.Max(MinOpusBitrate, curBitrate * 3 / 4);
                    _pcCodec.SetBitrate(curBitrate);
                    lastChangeMs = now;
                    statMinBitrate = Math.Min(statMinBitrate, curBitrate);
                }
                else if (now - lastCongestedMs > 8000 && now - lastChangeMs > 8000 && curBitrate < maxBitrate)
                {
                    curBitrate = Math.Min(maxBitrate, curBitrate + 16_000);
                    _pcCodec.SetBitrate(curBitrate);
                    lastChangeMs = now;
                }

                if (now - statsAtMs >= 5000)
                {
                    Log?.Invoke($"{Label}: Opus {curBitrate / 1000} кбит/с (минимум за период {statMinBitrate / 1000}), " +
                                $"отправлено {statBytes * 8 / (now - statsAtMs)} кбит/с, макс. запись {statMaxWriteMs:F0} мс, очередь {backlog}");
                    statsAtMs = now;
                    statBytes = 0;
                    statMaxWriteMs = 0;
                    statMinBitrate = curBitrate;
                }
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

    /// <summary>Tell the phone the Windows volume changed.</summary>
    public void SendVolume(int level, bool muted)
    {
        if (!IsConnected) return;
        var payload = new[] { (byte)Math.Clamp(level, 0, 100), (byte)(muted ? 1 : 0) };
        _ = FrameIO.WriteFrameAsync(_stream, FrameType.Volume, payload, _writeLock, _cts.Token)
            .ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
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

        // Auto mode: a lossless stream that keeps overflowing the queue (>= 20 dropped chunks in the
        // last 12 s, i.e. 0.4 s of audio lost) is switched to Opus — see SwitchToOpusAsync.
        if (_useNativeFormat && _subscribed && _compressPref == 0 && _pcCodec.Kind == PcAudioCodecKind.PcmFloat32)
        {
            var nowTicks = Environment.TickCount64;
            lock (_dropWindow)
            {
                _dropWindow.Enqueue((nowTicks, dropped));
                while (_dropWindow.Count > 0 && nowTicks - _dropWindow.Peek().Ticks > 12_000) _dropWindow.Dequeue();
                if (_dropWindow.Count > 1 && dropped - _dropWindow.Peek().Dropped >= 20)
                    Interlocked.Exchange(ref _switchRequested, 1);
            }
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
                    Interlocked.Exchange(ref _lastMicTicks, Environment.TickCount64);
                }
                else if (frame.Type == FrameType.Media && frame.Payload.Length >= 1)
                {
                    MediaCommand?.Invoke(frame.Payload[0]);
                }
                else if (frame.Type == FrameType.Volume && frame.Payload.Length >= 2)
                {
                    VolumeReceived?.Invoke(Math.Min(frame.Payload[0], (byte)100), (frame.Payload[1] & 1) != 0);
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
        if (bitrate > 0)
        {
            Interlocked.Exchange(ref _pendingBitrate, bitrate);
            _configBitrate = bitrate;
        }

        if (p.Length >= 8) _maxRawRate = Math.Max(0, BitConverter.ToInt32(p, 4));

        if (p.Length >= 9)
        {
            var len = p[8];
            if (len > 0 && p.Length >= 9 + len) PhoneName = Encoding.UTF8.GetString(p, 9, len);
            // v3: a flags byte after the name — bits 0-1 = Wi-Fi quality (0 auto, 1 lossless, 2 compressed).
            if (p.Length >= 10 + len) _compressPref = p[9 + len] & 3;
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
