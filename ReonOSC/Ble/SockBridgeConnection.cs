using System.Collections.Concurrent;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ReonOSC.Models;

namespace ReonOSC.Ble;

/// <summary>
/// MQTT connection to the sockbridge (tools/sockbridge), shared by both
/// <see cref="MqttSockLink"/>s. Keeps the latest retained state per sock so a
/// link bound after connecting still gets it.
/// </summary>
public sealed class SockBridgeConnection : IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly string _base;
    private readonly ConcurrentDictionary<string, JsonElement> _states = new();
    private readonly CancellationTokenSource _cts = new();
    private IMqttClient? _client;
    private Task? _loop;

    public bool BridgeOnline { get; private set; }

    public event Action<bool>? AvailabilityChanged;
    public event Action<string, JsonElement>? StateReceived;
    public event EventHandler<string>? Log;

    public SockBridgeConnection(Settings settings)
    {
        _settings = settings;
        _base = string.IsNullOrWhiteSpace(settings.SockBridgeTopic) ? "sockbridge" : settings.SockBridgeTopic.Trim().TrimEnd('/');
    }

    public static string IdFor(ulong mac) => SockClient.FormatMac(mac).Replace(":", "").ToLowerInvariant();

    public JsonElement? LastState(string id) => _states.TryGetValue(id, out var s) ? s : null;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    public async Task<bool> PublishTargetAsync(string id, int target)
    {
        var c = _client;
        if (c is not { IsConnected: true }) return false;
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"{_base}/{id}/set")
                .WithPayload(JsonSerializer.Serialize(new { target }))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await c.PublishAsync(msg).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"[SockBridge] publish failed: {ex.Message}");
            return false;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client = new MqttFactory().CreateMqttClient();
                _client.ApplicationMessageReceivedAsync += e =>
                {
                    OnMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString());
                    return Task.CompletedTask;
                };
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(_settings.MqttHost, _settings.MqttPort)
                    .WithClientId($"reonosc-socks-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 23))
                    .WithCleanSession(true)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(15));
                if (!string.IsNullOrEmpty(_settings.MqttUsername))
                    opts.WithCredentials(_settings.MqttUsername, _settings.MqttPassword);

                await _client.ConnectAsync(opts.Build(), ct).ConfigureAwait(false);
                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic($"{_base}/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(), ct).ConfigureAwait(false);
                Log?.Invoke(this, $"[SockBridge] MQTT connected to {_settings.MqttHost}:{_settings.MqttPort}");
                backoff = TimeSpan.FromSeconds(2);

                while (!ct.IsCancellationRequested && _client.IsConnected)
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    Log?.Invoke(this, "[SockBridge] MQTT connection lost — reconnecting.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"[SockBridge] MQTT connect failed: {ex.Message} — retrying in {(int)backoff.TotalSeconds} s");
            }
            finally
            {
                try { _client?.Dispose(); } catch { }
                _client = null;
            }
            // We can't see the bridge without the broker: treat it as offline.
            SetOnline(false);
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private void OnMessage(string topic, string payload)
    {
        if (topic == $"{_base}/availability")
        {
            SetOnline(payload == "online");
            return;
        }
        var parts = topic.Split('/');
        if (parts.Length < 3 || parts[^1] != "state") return;
        var id = parts[^2];
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var state = doc.RootElement.Clone();
            _states[id] = state;
            StateReceived?.Invoke(id, state);
        }
        catch { /* malformed, ignore */ }
    }

    private void SetOnline(bool online)
    {
        if (online == BridgeOnline) return;
        BridgeOnline = online;
        Log?.Invoke(this, online ? "[SockBridge] bridge online." : "[SockBridge] bridge offline.");
        AvailabilityChanged?.Invoke(online);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_client is { IsConnected: true } c) await c.DisconnectAsync().ConfigureAwait(false);
        }
        catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
