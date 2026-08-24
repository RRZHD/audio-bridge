using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBridge.Server.Core;

/// <summary>
/// One TCP listener serves both Wi-Fi and USB clients. USB clients arrive via
/// `adb reverse tcp:PORT tcp:PORT`, which makes the phone's 127.0.0.1:PORT tunnel here — from this
/// listener's point of view it is just another TCP connection, distinguished only by remote address
/// for display purposes (loopback => came in over the adb/USB tunnel).
/// </summary>
public sealed class TcpServerListener : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public event Action<System.IO.Stream, string>? ClientAccepted;
    public event Action<string>? Log;

    public int Port { get; private set; }

    public void Start(int port)
    {
        Stop();
        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Log?.Invoke($"TCP listening on 0.0.0.0:{port} (Wi-Fi + USB/adb reverse)");
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                var isLoopback = remote != null && IPAddress.IsLoopback(remote.Address);
                var label = isLoopback ? $"USB (adb reverse, {remote})" : $"Wi-Fi ({remote})";
                Log?.Invoke($"Client connected: {label}");
                ClientAccepted?.Invoke(client.GetStream(), label);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Log?.Invoke($"TCP accept loop stopped: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();
}
