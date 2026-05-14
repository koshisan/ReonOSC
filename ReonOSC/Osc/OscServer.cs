using System.Net;
using System.Net.Sockets;

namespace ReonOSC.Osc;

/// <summary>
/// UDP listener that receives OSC packets and raises <see cref="MessageReceived"/>
/// for each parsed message. The event fires on a background thread; subscribers
/// updating UI must marshal back themselves.
/// </summary>
public sealed class OscServer : IDisposable
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public int Port { get; private set; }
    public bool IsRunning => _udp is not null;

    public event EventHandler<OscMessage>? MessageReceived;
    public event EventHandler<string>? Log;

    public void Start(int port)
    {
        if (IsRunning) Stop();
        _udp = new UdpClient(port);
        Port = port;
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        Log?.Invoke(this, $"OSC listening on UDP {port}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        try { _udp?.Close(); } catch { }
        _udp = null;
        Log?.Invoke(this, "OSC listener stopped.");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"OSC receive error: {ex.Message}");
                continue;
            }

            try
            {
                foreach (var msg in OscParser.Parse(result.Buffer))
                    MessageReceived?.Invoke(this, msg);
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"OSC parse error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
        _receiveLoop = null;
    }
}
