using System;
using Concentus.Enums;
using Concentus.Structs;

namespace AudioBridge.Server.Core;

public enum PcAudioCodecKind : byte
{
    PcmFloat32 = 0x00,
    Opus = 0x01,
}

/// <summary>Encodes/decodes the PC->phone (system audio) stream.</summary>
public interface IPcAudioCodec
{
    PcAudioCodecKind Kind { get; }

    /// <summary>Encode one chunk of interleaved float PCM (range -1..1) into wire bytes.</summary>
    byte[] Encode(float[] interleaved, int sampleCountPerChannel);

    /// <summary>Decode wire bytes back into interleaved float PCM. Returns sample count per channel.</summary>
    int Decode(byte[] payload, float[] outInterleaved);
}

/// <summary>Encodes/decodes the phone->PC (mic) stream. Always PCM16 mono 16kHz canonical form.</summary>
public interface IMicAudioCodec
{
    byte[] Encode(short[] pcm, int sampleCount);
    int Decode(byte[] payload, short[] outPcm);
}

/// <summary>Lossless passthrough — used for Wi-Fi and USB where bandwidth is not a constraint.</summary>
public sealed class RawFloat32PcCodec : IPcAudioCodec
{
    public PcAudioCodecKind Kind => PcAudioCodecKind.PcmFloat32;

    public byte[] Encode(float[] interleaved, int sampleCountPerChannel)
    {
        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public int Decode(byte[] payload, float[] outInterleaved)
    {
        var count = payload.Length / sizeof(float);
        Buffer.BlockCopy(payload, 0, outInterleaved, 0, payload.Length);
        return count;
    }
}

/// <summary>Opus-compressed — used for Bluetooth RFCOMM, where raw stereo PCM would not reliably fit.</summary>
public sealed class OpusPcCodec : IPcAudioCodec
{
    private readonly OpusEncoder _encoder;
    private readonly OpusDecoder _decoder;
    private readonly int _channels;
    private readonly byte[] _scratch = new byte[8000];
    private readonly short[] _pcmScratch;

    public PcAudioCodecKind Kind => PcAudioCodecKind.Opus;

    public OpusPcCodec(int sampleRate, int channels, int bitrateBps)
    {
        _channels = channels;
        _encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO)
        {
            Bitrate = bitrateBps,
        };
        _decoder = new OpusDecoder(sampleRate, channels);
        _pcmScratch = new short[sampleRate / 10 * channels]; // generous scratch (>100ms worth)
    }

    public byte[] Encode(float[] interleaved, int sampleCountPerChannel)
    {
        var total = sampleCountPerChannel * _channels;
        if (_pcmScratch.Length < total)
        {
            throw new InvalidOperationException("Opus scratch buffer too small for chunk");
        }
        for (var i = 0; i < total; i++)
        {
            var v = interleaved[i];
            if (v > 1f) v = 1f;
            if (v < -1f) v = -1f;
            _pcmScratch[i] = (short)(v * short.MaxValue);
        }
        var len = _encoder.Encode(_pcmScratch, 0, sampleCountPerChannel, _scratch, 0, _scratch.Length);
        var outBytes = new byte[len];
        Array.Copy(_scratch, outBytes, len);
        return outBytes;
    }

    public int Decode(byte[] payload, float[] outInterleaved)
    {
        var frameSize = outInterleaved.Length / _channels;
        var decoded = _decoder.Decode(payload, 0, payload.Length, _pcmScratch, 0, frameSize, false);
        var total = decoded * _channels;
        for (var i = 0; i < total; i++)
        {
            outInterleaved[i] = _pcmScratch[i] / (float)short.MaxValue;
        }
        return decoded;
    }
}

public sealed class RawPcm16MicCodec : IMicAudioCodec
{
    public byte[] Encode(short[] pcm, int sampleCount)
    {
        var bytes = new byte[sampleCount * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public int Decode(byte[] payload, short[] outPcm)
    {
        var count = payload.Length / sizeof(short);
        Buffer.BlockCopy(payload, 0, outPcm, 0, payload.Length);
        return count;
    }
}

public sealed class OpusMicCodec : IMicAudioCodec
{
    private readonly OpusEncoder _encoder;
    private readonly OpusDecoder _decoder;
    private readonly byte[] _scratch = new byte[4000];

    public OpusMicCodec(int sampleRate, int bitrateBps)
    {
        _encoder = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = bitrateBps,
        };
        _decoder = new OpusDecoder(sampleRate, 1);
    }

    public byte[] Encode(short[] pcm, int sampleCount)
    {
        var len = _encoder.Encode(pcm, 0, sampleCount, _scratch, 0, _scratch.Length);
        var outBytes = new byte[len];
        Array.Copy(_scratch, outBytes, len);
        return outBytes;
    }

    public int Decode(byte[] payload, short[] outPcm)
    {
        return _decoder.Decode(payload, 0, payload.Length, outPcm, 0, outPcm.Length, false);
    }
}
