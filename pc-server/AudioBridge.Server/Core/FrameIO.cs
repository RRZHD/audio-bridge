using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBridge.Server.Core;

public enum FrameType : byte
{
    AudioPc = 0x01,
    AudioMic = 0x02,
    Format = 0x03,
    Config = 0x04,
    Ping = 0xFF,
}

public readonly struct Frame
{
    public FrameType Type { get; }
    public byte[] Payload { get; }

    public Frame(FrameType type, byte[] payload)
    {
        Type = type;
        Payload = payload;
    }
}

/// <summary>
/// Reads/writes the wire frame: byte type, uint32 length (LE), payload bytes.
/// Shared format for TCP (Wi-Fi/USB) and Bluetooth RFCOMM streams.
/// </summary>
public static class FrameIO
{
    private const int HeaderSize = 5;

    public static async Task WriteFrameAsync(Stream stream, FrameType type, ReadOnlyMemory<byte> payload, SemaphoreSlim writeLock, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        header[0] = (byte)type;
        BitConverter.TryWriteBytes(header.AsSpan(1, 4), payload.Length);

        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public static async Task<Frame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        var type = (FrameType)header[0];
        var length = BitConverter.ToInt32(header, 1);
        if (length < 0 || length > 10 * 1024 * 1024)
        {
            throw new IOException($"Invalid frame length {length}");
        }

        if (length == 0)
        {
            return new Frame(type, Array.Empty<byte>());
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return new Frame(type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Stream closed by remote end");
            }
            offset += read;
        }
    }
}
