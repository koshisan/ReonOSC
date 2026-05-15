using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ReonOSC.Ble;
using ReonOSC.Models;

namespace ReonOSC.Services;

/// <summary>
/// Publishes the current device + control state to an MQTT broker so Home
/// Assistant (and anything else listening) can mirror the Reon and trigger
/// automations off it — the classic "Reon went into Cool → also crank the AC"
/// use case the user has in mind.
///
/// Publishes:
///   {base}/availability      → "online" / "offline" (retained, LWT'd)
///   {base}/state             → JSON state document (retained)
///   {discoveryPrefix}/...    → HA MQTT Discovery configs (retained)
///
/// State JSON shape — see <see cref="BuildStatePayload"/>. Stable contract so
/// HA dashboards can pin to it.
/// </summary>
public sealed class MqttPublisher : IAsyncDisposable
{
    public enum State { Disabled, Disconnected, Connecting, Connected, Error }

    private readonly ControlService _service;
    private Settings _settings;
    private IMqttClient? _client;
    // Lazy-init: don't touch the MQTTnet types until we actually try to
    // connect, so a broken/missing MQTTnet.dll on the host machine can't
    // tank the app's startup (which used to deny a tray icon entirely).
    private MqttFactory? _factory;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Latest telemetry snapshot — TelemetryReceived fires faster than we want
    // to publish, so we coalesce into this and let the state push pick the
    // freshest values whenever something else (mode/source/conn) changes.
    private Ble.Telemetry? _lastTelemetry;

    public State CurrentState { get; private set; } = State.Disabled;
    public string? LastError { get; private set; }
    public event EventHandler<(State state, string? error)>? StateChanged;
    public event EventHandler<string>? Log;

    public MqttPublisher(ControlService service, Settings settings)
    {
        _service = service;
        _settings = settings;

        // Defensive subscribes: ControlService is unconditionally created,
        // so these should always succeed — but wrapping keeps a broken event
        // wiring from killing the host before the tray icon is up.
        try
        {
            _service.CommandSent += (_, _) => { try { _ = PublishStateAsync(); } catch { } };
            _service.Reon.TelemetryReceived += (_, t) =>
            {
                _lastTelemetry = t;
                try { _ = PublishStateAsync(); } catch { }
            };
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"MQTT publisher init warning: {ex.Message}");
        }
    }

    public void ApplySettings(Settings settings)
    {
        bool restart =
            settings.MqttEnabled != _settings.MqttEnabled
            || settings.MqttHost != _settings.MqttHost
            || settings.MqttPort != _settings.MqttPort
            || settings.MqttUsername != _settings.MqttUsername
            || settings.MqttPassword != _settings.MqttPassword
            || settings.MqttBaseTopic != _settings.MqttBaseTopic
            || settings.MqttDiscoveryPrefix != _settings.MqttDiscoveryPrefix;

        _settings = settings;
        if (restart)
        {
            _ = RestartAsync();
        }
    }

    public Task StartAsync() => RestartAsync();

    private async Task RestartAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);

        if (!_settings.MqttEnabled || string.IsNullOrWhiteSpace(_settings.MqttHost))
        {
            SetState(State.Disabled, null);
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => ConnectionLoopAsync(_loopCts.Token));
    }

    private async Task StopInternalAsync()
    {
        try { _loopCts?.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
            _loop = null;
        }
        try
        {
            if (_client is { IsConnected: true })
            {
                // Best-effort offline notice; the LWT covers the case where we
                // crash before this lands.
                await PublishRawAsync(AvailabilityTopic(), "offline", retain: true).ConfigureAwait(false);
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _loopCts?.Dispose();
        _loopCts = null;
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(State.Connecting, null);
                _factory ??= new MqttFactory();
                _client = _factory.CreateMqttClient();

                var optsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(_settings.MqttHost, _settings.MqttPort)
                    .WithCleanSession(true)
                    .WithClientId($"reonosc-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 23))
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .WithWillTopic(AvailabilityTopic())
                    .WithWillPayload("offline")
                    .WithWillRetain(true)
                    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

                if (!string.IsNullOrEmpty(_settings.MqttUsername))
                    optsBuilder.WithCredentials(_settings.MqttUsername, _settings.MqttPassword);

                var opts = optsBuilder.Build();

                _client.DisconnectedAsync += async _ =>
                {
                    // Setting Disconnected from the event handler — the outer
                    // loop will pick up the cancellation/error and reconnect.
                    if (CurrentState == State.Connected)
                        SetState(State.Disconnected, null);
                    await Task.CompletedTask;
                };

                await _client.ConnectAsync(opts, ct).ConfigureAwait(false);
                SetState(State.Connected, null);
                Log?.Invoke(this, $"MQTT connected to {_settings.MqttHost}:{_settings.MqttPort}");

                await PublishRawAsync(AvailabilityTopic(), "online", retain: true).ConfigureAwait(false);
                await PublishDiscoveryAsync().ConfigureAwait(false);
                await PublishStateAsync().ConfigureAwait(false);

                backoff = TimeSpan.FromSeconds(2); // reset on successful connect

                // Idle until the connection drops or we're cancelled.
                while (!ct.IsCancellationRequested && _client.IsConnected)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(State.Error, ex.Message);
                Log?.Invoke(this, $"MQTT connect failed: {ex.Message} — retrying in {(int)backoff.TotalSeconds}s");
            }
            finally
            {
                try { _client?.Dispose(); } catch { }
                _client = null;
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 1.8, maxBackoff.TotalSeconds));
        }
    }

    public async Task PublishStateAsync()
    {
        if (_client is not { IsConnected: true }) return;
        var payload = JsonSerializer.Serialize(BuildStatePayload(), JsonOpts);
        await PublishRawAsync(StateTopic(), payload, retain: true).ConfigureAwait(false);
    }

    private object BuildStatePayload()
    {
        var cmd = _service.LastSentCommand;
        var mode = cmd.Mode switch
        {
            ReonProtocol.Mode.Cool => "Cool",
            ReonProtocol.Mode.Heat => "Heat",
            _ => "Stop",
        };
        return new
        {
            mode,
            level = cmd.Level,
            source = _service.LastCommandSource,
            connected = _service.Reon.IsConnected,
            model = _service.Reon.Model,
            caps = new
            {
                coolMax = _service.Reon.Capabilities.CoolLevelMax,
                heatMax = _service.Reon.Capabilities.HeatLevelMax,
            },
            temps = _lastTelemetry is { } tel ? (object)new
            {
                plate = tel.SkinPlate, sink = tel.Heatsink, board = tel.Board, ambient = tel.Ambient,
            } : null,
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    private async Task PublishDiscoveryAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.MqttDiscoveryPrefix)) return;

        var device = new
        {
            identifiers = new[] { "reonosc" },
            name = "Reon Pocket (ReonOSC)",
            manufacturer = "Sony",
            model = _service.Reon.Model ?? "Reon Pocket",
            sw_version = typeof(MqttPublisher).Assembly.GetName().Version?.ToString() ?? "0",
        };

        var stateTopic = StateTopic();
        var availabilityTopic = AvailabilityTopic();

        // One discovery payload per entity. value_template pulls from the
        // shared JSON state document, so we only publish one state topic.
        await PublishDiscoveryEntityAsync("sensor", "mode", new
        {
            name = "Reon mode",
            unique_id = "reonosc_mode",
            state_topic = stateTopic,
            value_template = "{{ value_json.mode }}",
            icon = "mdi:thermostat",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("sensor", "level", new
        {
            name = "Reon level",
            unique_id = "reonosc_level",
            state_topic = stateTopic,
            value_template = "{{ value_json.level }}",
            icon = "mdi:numeric",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("sensor", "source", new
        {
            name = "Reon source",
            unique_id = "reonosc_source",
            state_topic = stateTopic,
            value_template = "{{ value_json.source }}",
            icon = "mdi:source-branch",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("binary_sensor", "connected", new
        {
            name = "Reon connected",
            unique_id = "reonosc_connected",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if value_json.connected else 'OFF' }}",
            device_class = "connectivity",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("sensor", "plate", new
        {
            name = "Reon plate temperature",
            unique_id = "reonosc_plate",
            state_topic = stateTopic,
            value_template = "{{ value_json.temps.plate if value_json.temps else none }}",
            unit_of_measurement = "°C",
            device_class = "temperature",
            state_class = "measurement",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("sensor", "ambient", new
        {
            name = "Reon ambient temperature",
            unique_id = "reonosc_ambient",
            state_topic = stateTopic,
            value_template = "{{ value_json.temps.ambient if value_json.temps else none }}",
            unit_of_measurement = "°C",
            device_class = "temperature",
            state_class = "measurement",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);
    }

    private Task PublishDiscoveryEntityAsync(string component, string objectId, object payload)
    {
        var topic = $"{_settings.MqttDiscoveryPrefix.TrimEnd('/')}/{component}/reonosc/{objectId}/config";
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        return PublishRawAsync(topic, json, retain: true);
    }

    private async Task PublishRawAsync(string topic, string payload, bool retain)
    {
        if (_client is not { IsConnected: true }) return;
        await _publishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"MQTT publish to {topic} failed: {ex.Message}");
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private string BaseTopic() =>
        string.IsNullOrWhiteSpace(_settings.MqttBaseTopic) ? "reonosc" : _settings.MqttBaseTopic.Trim().TrimEnd('/');
    private string StateTopic() => $"{BaseTopic()}/state";
    private string AvailabilityTopic() => $"{BaseTopic()}/availability";

    private void SetState(State next, string? error)
    {
        if (CurrentState == next && LastError == error) return;
        CurrentState = next;
        LastError = error;
        StateChanged?.Invoke(this, (next, error));
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);
        _publishLock.Dispose();
    }
}
