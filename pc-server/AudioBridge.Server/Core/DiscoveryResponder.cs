using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBridge.Server.Core;

/// <summary>
/// Answers UDP broadcast discovery requests from the Android app so the user doesn't have to type
/// the PC's IP manually (it differs per hotspot/router setup). Not used for USB or Bluetooth —
/// those don't need an IP at all.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    // Chosen below the ephemeral/dynamic port range (49152+) that Hyper-V/WSL/Docker carve into
    // large excluded blocks for NAT (`netsh int ipv4 show excludedportrange protocol=udp`) — a port
    // up there has a real chance of landing in one of those reserved ranges and failing to bind
    // with WSAEACCES, which is exactly what happened during testing with port 57121.
    public const int DiscoveryPort = 45120;
    private static readonly byte[] RequestMagic = Encoding.ASCII.GetBytes("ABDQ");
    private static readonly byte[] ResponseMagic = Encoding.ASCII.GetBytes("ABDR");

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public event Action<string>? Log;

    /// <summary>
    /// Never throws — discovery is a convenience, not core functionality (TCP/Bluetooth still work
    /// without it), so a bind failure here should be logged and shrugged off rather than taking the
    /// whole server down. See the DiscoveryPort comment for why binds can fail on some machines.
    /// </summary>
    public void Start(int tcpPort)
    {
        Stop();
        try
        {
            _cts = new CancellationTokenSource();
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            Log?.Invoke($"Discovery responder listening on UDP {DiscoveryPort}");
            _ = ListenLoopAsync(_udp, tcpPort, _cts.Token);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Discovery responder disabled — couldn't bind UDP {DiscoveryPort}: {ex.Message}. " +
                        "Wi-Fi/USB/Bluetooth still work, just type the PC's IP manually in the app.");
            _udp?.Dispose();
            _udp = null;
        }
    }

    private async Task ListenLoopAsync(UdpClient udp, int tcpPort, CancellationToken ct)
    {
        var hostname = Environment.MachineName;
        var hostnameBytes = Encoding.UTF8.GetBytes(hostname);
        if (hostnameBytes.Length > 255) Array.Resize(ref hostnameBytes, 255);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (!IsValidRequest(result.Buffer)) continue;

                var response = new byte[ResponseMagic.Length + 1 + hostnameBytes.Length + 4];
                var offset = 0;
                Buffer.BlockCopy(ResponseMagic, 0, response, offset, ResponseMagic.Length);
                offset += ResponseMagic.Length;
                response[offset++] = (byte)hostnameBytes.Length;
                Buffer.BlockCopy(hostnameBytes, 0, response, offset, hostnameBytes.Length);
                offset += hostnameBytes.Length;
                BitConverter.TryWriteBytes(response.AsSpan(offset, 4), tcpPort);

                await udp.SendAsync(response, response.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Discovery responder error: {ex.Message}");
            }
        }
    }

    private static bool IsValidRequest(byte[] buffer)
    {
        if (buffer.Length < RequestMagic.Length) return false;
        for (var i = 0; i < RequestMagic.Length; i++)
        {
            if (buffer[i] != RequestMagic[i]) return false;
        }
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
    }

    public void Dispose() => Stop();
}
