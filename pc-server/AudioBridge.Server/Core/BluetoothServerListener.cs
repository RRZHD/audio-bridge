using System;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace AudioBridge.Server.Core;

/// <summary>
/// Listens for an incoming RFCOMM connection from the phone app (custom service UUID — this is NOT
/// standard A2DP, so it carries our own duplex protocol including the mic stream, at the cost of not
/// being usable by generic Bluetooth headset pairing).
/// </summary>
public sealed class BluetoothServerListener : IDisposable
{
    // Fixed app-specific UUID — must match android-app/.../BluetoothTransport.kt
    public static readonly Guid ServiceUuid = new("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e3f");

    private BluetoothListener? _listener;
    private CancellationTokenSource? _cts;

    public event Action<System.IO.Stream, string>? ClientAccepted;
    public event Action<string>? Log;

    public void Start()
    {
        Stop();
        try
        {
            BluetoothRadio.Default.Mode = RadioMode.Connectable;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not set radio mode (continuing anyway): {ex.Message}");
        }

        _cts = new CancellationTokenSource();
        _listener = new BluetoothListener(ServiceUuid);
        _listener.Start();
        Log?.Invoke($"Bluetooth RFCOMM listening (service {ServiceUuid})");
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(BluetoothListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await Task.Run(() => listener.AcceptBluetoothClient(), ct).ConfigureAwait(false);
                var label = $"Bluetooth ({client.RemoteMachineName})";
                Log?.Invoke($"Client connected: {label}");
                ClientAccepted?.Invoke(client.GetStream(), label);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke($"Bluetooth accept error: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
