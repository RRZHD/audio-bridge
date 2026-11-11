using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBridge.Server.Core;

/// <summary>
/// A duplex byte stream over a pair of bulk endpoints. Reads wait in short slices (so Dispose and the
/// idle check stay responsive); a stream that stays silent for <see cref="IdleLimitMs"/> is treated as
/// dead — the phone pings every few seconds, so silence means the app (or the cable) is gone.
/// </summary>
internal sealed class LibUsbStream : Stream
{
    private const int ChunkBytes = 16 * 1024; // Android's accessory driver moves 16 KB per request
    private const int ReadSliceMs = 250;
    private const int WriteTimeoutMs = 3000;
    public const int IdleLimitMs = 12000;

    private readonly IntPtr _handle;
    private readonly int _iface;
    private readonly byte _inEp, _outEp;
    private readonly int _maxPacket;
    private readonly byte[] _rx = new byte[ChunkBytes];
    private byte[]? _pending;
    private int _pendingOff, _pendingLen;
    private volatile bool _closed;
    private int _inflight;
    private long _lastRxTicks = Environment.TickCount64;
    private readonly object _closeLock = new();
    private bool _released;

    public event Action? Closed;

    public LibUsbStream(IntPtr handle, int iface, byte inEp, byte outEp, int maxPacket)
    {
        _handle = handle;
        _iface = iface;
        _inEp = inEp;
        _outEp = outEp;
        _maxPacket = maxPacket > 0 ? maxPacket : 512;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the first bytes and keeps them for the next Read.
    /// Returns the byte count (&gt;0), 0 on timeout, or -1 if the device is gone.
    /// </summary>
    public int Prefetch(int timeoutMs)
    {
        if (_pending != null) return _pendingLen;
        var rc = BulkIn(_rx, timeoutMs, out var n);
        if (n > 0)
        {
            _pending = new byte[n];
            Buffer.BlockCopy(_rx, 0, _pending, 0, n);
            _pendingOff = 0;
            _pendingLen = n;
            _lastRxTicks = Environment.TickCount64;
            return n;
        }
        return rc == LibUsb.ErrorTimeout ? 0 : -1;
    }

    private int BulkIn(byte[] buf, int timeoutMs, out int transferred)
    {
        Interlocked.Increment(ref _inflight);
        try
        {
            if (_closed) { transferred = 0; return LibUsb.ErrorNoDevice; }
            return LibUsb.libusb_bulk_transfer(_handle, _inEp, buf, buf.Length, out transferred, (uint)timeoutMs);
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        if (_pending != null)
        {
            var take = Math.Min(count, _pendingLen);
            Buffer.BlockCopy(_pending, _pendingOff, buffer, offset, take);
            _pendingOff += take;
            _pendingLen -= take;
            if (_pendingLen == 0) _pending = null;
            return take;
        }

        while (true)
        {
            if (_closed) return 0;
            var rc = BulkIn(_rx, ReadSliceMs, out var n);
            if (n > 0)
            {
                _lastRxTicks = Environment.TickCount64;
                var take = Math.Min(count, n);
                Buffer.BlockCopy(_rx, 0, buffer, offset, take);
                if (take < n)
                {
                    _pending = new byte[n - take];
                    Buffer.BlockCopy(_rx, take, _pending, 0, n - take);
                    _pendingOff = 0;
                    _pendingLen = n - take;
                }
                return take;
            }
            if (rc == LibUsb.ErrorTimeout)
            {
                if (Environment.TickCount64 - _lastRxTicks > IdleLimitMs)
                    throw new IOException("USB: телефон давно ничего не присылает (приложение закрыто или кабель отключён)");
                continue;
            }
            if (rc == LibUsb.ErrorPipe)
            {
                LibUsb.libusb_clear_halt(_handle, _inEp);
                continue;
            }
            if (_closed) return 0;
            throw new IOException($"USB: ошибка чтения ({LibUsb.ErrorName(rc)})");
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var sent = 0;
        while (sent < count)
        {
            if (_closed) throw new IOException("USB: поток закрыт");
            var len = Math.Min(ChunkBytes, count - sent);
            var chunk = new byte[len];
            Buffer.BlockCopy(buffer, offset + sent, chunk, 0, len);
            Interlocked.Increment(ref _inflight);
            int rc, n;
            try
            {
                rc = LibUsb.libusb_bulk_transfer(_handle, _outEp, chunk, len, out n, WriteTimeoutMs);
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
            if (rc != 0 && !(rc == LibUsb.ErrorTimeout && n > 0))
            {
                if (rc == LibUsb.ErrorPipe)
                {
                    LibUsb.libusb_clear_halt(_handle, _outEp);
                    continue;
                }
                throw new IOException(rc == LibUsb.ErrorTimeout
                    ? "USB: телефон не принимает данные"
                    : $"USB: ошибка записи ({LibUsb.ErrorName(rc)})");
            }
            sent += n;

            // A transfer that ends exactly on a packet boundary needs a zero-length packet to tell the
            // device it is complete (otherwise it waits for more bytes to fill its request).
            if (sent == count && n > 0 && n % _maxPacket == 0)
            {
                Interlocked.Increment(ref _inflight);
                try { LibUsb.libusb_bulk_transfer(_handle, _outEp, new byte[1], 0, out _, WriteTimeoutMs); }
                finally { Interlocked.Decrement(ref _inflight); }
            }
        }
    }

    // The default Stream.ReadAsync/WriteAsync serialise on one internal semaphore, which would stall
    // writes behind a pending read. Run each directly on its own worker instead.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => Read(buffer, offset, count), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var tmp = new byte[buffer.Length];
        return new ValueTask<int>(Task.Factory.StartNew(() =>
        {
            var n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Run(() => Write(buffer, offset, count), cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var copy = buffer.ToArray();
        return new ValueTask(Task.Run(() => Write(copy, 0, copy.Length), cancellationToken));
    }

    protected override void Dispose(bool disposing)
    {
        lock (_closeLock)
        {
            if (_released) return;
            _closed = true;
        }
        // In-flight transfers end within their timeout slice; wait for them before the handle goes.
        var deadline = Environment.TickCount64 + WriteTimeoutMs + 500;
        while (Volatile.Read(ref _inflight) > 0 && Environment.TickCount64 < deadline) Thread.Sleep(20);

        lock (_closeLock)
        {
            if (_released) return;
            _released = true;
        }
        try { LibUsb.libusb_release_interface(_handle, _iface); } catch { /* device may be gone */ }
        try { LibUsb.libusb_close(_handle); } catch { /* ignore */ }
        Closed?.Invoke();
        base.Dispose(disposing);
    }
}
