using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBridge.Server.Core;

public sealed class ClientSession : IDisposable
{
    public string Label { get; }
    public bool IsConnected { get; private set; } = true;

    public event Action<ClientSession>? Disconnected;

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
    private DateTime _lastSentUtc = DateTime.MinValue;
    private System.Threading.Timer? _pingTimer;

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
    }

    public void Start()
    {
        if (_useNativeFormat) _engine.PcChunkReady += OnPcChunkReady;
        else _engine.PcChunkReady48kStereo += OnPcChunkReady;
        _pingTimer = new System.Threading.Timer(_ => _ = SendPingIfIdleAsync(), null, 3000, 3000);
        _ = Task.Run(ReaderLoopAsync);
        _ = SendFormatAsync();
    }

    private async Task SendFormatAsync()
    {
        try
        {
            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), _pcSampleRate);
            payload[4] = (byte)_pcChannels;
            payload[5] = (byte)_pcCodec.Kind;
            await FrameIO.WriteFrameAsync(_stream, FrameType.Format, payload, _writeLock, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            Disconnect();
        }
    }

    private void OnPcChunkReady(PcAudioChunk chunk)
    {
        if (!IsConnected) return;
        _ = SendPcChunkAsync(chunk);
    }

    private async Task SendPcChunkAsync(PcAudioChunk chunk)
    {
        try
        {
            var encoded = _pcCodec.Encode(chunk.Interleaved, chunk.SampleCountPerChannel);
            await FrameIO.WriteFrameAsync(_stream, FrameType.AudioPc, encoded, _writeLock, _cts.Token).ConfigureAwait(false);
            _lastSentUtc = DateTime.UtcNow;
        }
        catch
        {
            Disconnect();
        }
    }

    private async Task SendPingIfIdleAsync()
    {
        if (!IsConnected) return;
        if ((DateTime.UtcNow - _lastSentUtc).TotalSeconds < 3) return;
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
                    var bitrate = BitConverter.ToInt32(frame.Payload, 0);
                    _pcCodec.SetBitrate(bitrate);
                }
                // PING and unknown types: no-op, receiving anything resets the read timeout implicitly.
            }
        }
        catch
        {
            // fall through to disconnect
        }
        Disconnect();
    }

    private int _disconnected;

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        IsConnected = false;
        if (_useNativeFormat) _engine.PcChunkReady -= OnPcChunkReady;
        else _engine.PcChunkReady48kStereo -= OnPcChunkReady;
        _pingTimer?.Dispose();
        _cts.Cancel();
        try { _stream.Dispose(); } catch { /* ignore */ }
        Disconnected?.Invoke(this);
    }

    public void Dispose() => Disconnect();
}
