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
    // Snapshot of the connection-relevant fields at the time we last
    // (re)started — ApplySettings compares against this rather than against
    // the live _settings reference, because callers mutate that reference in
    // place before handing it back to us. Comparing the live object to
    // itself would always look unchanged.
    private (bool enabled, string host, int port, string user, string pw, string baseTopic, string discovery) _liveSnapshot;
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
            // StateChanged catches reason-only shifts (and disconnected updates)
            // that CommandSent doesn't fire on. Without this, an avatar caress
            // (OSC:PFHotHigh) right after a fire (PFSignal) wouldn't update the
            // reason topic if both happened to resolve to the same Heat L3.
            _service.StateChanged += (_, _) => { try { _ = PublishStateAsync(); } catch { } };
            // InputsChanged catches passthrough values (notably wind) that
            // don't influence the resolver — the Reon has no fan, but HA
            // should still see the value change so a real fan can react.
            _service.InputsChanged += (_, _) => { try { _ = PublishStateAsync(); } catch { } };
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
        _settings = settings;
        var next = SnapshotOf(settings);
        if (!next.Equals(_liveSnapshot))
        {
            _ = RestartAsync();
        }
    }

    public Task StartAsync() => RestartAsync();

    private static (bool, string, int, string, string, string, string) SnapshotOf(Settings s) =>
        (s.MqttEnabled, s.MqttHost ?? "", s.MqttPort, s.MqttUsername ?? "",
         s.MqttPassword ?? "", s.MqttBaseTopic ?? "", s.MqttDiscoveryPrefix ?? "");

    private async Task RestartAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);
        _liveSnapshot = SnapshotOf(_settings);

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
        // Use the resolver's intent rather than the last successful write —
        // when no Reon is paired, LastSentCommand stays at its default and
        // HA would otherwise see mode=Stop forever despite the user being
        // in a heat zone. `connected` separately signals whether the Reon
        // is actually applying these commands.
        var cmd = _service.LastResolvedCommand;
        var mode = cmd.Mode switch
        {
            ReonProtocol.Mode.Cool => "Cool",
            ReonProtocol.Mode.Heat => "Heat",
            _ => "Stop",
        };
        var inputs = _service.Snapshot();
        return new
        {
            mode,
            level = cmd.Level,
            source = _service.LastCommandSource,
            reason = _service.LastCommandReason,
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
            // Pass-through of the raw environmental OSC values. Wind in
            // particular is captured here for HA to drive a fan off — the
            // Reon itself can't act on it.
            inputs = new
            {
                pfHotHigh = inputs.GetValueOrDefault("PFHotHigh"),
                water = inputs.GetValueOrDefault("water"),
                cold = inputs.GetValueOrDefault("cold"),
                heat = inputs.GetValueOrDefault("heat"),
                wind = inputs.GetValueOrDefault("wind"),
            },
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

        // Reason — the granular trigger. Distinguishes e.g. "OSC:PFHotHigh"
        // (avatar caress, transient) from "PFSignal" (sitting in a world
        // heat zone, durable) — automations key off this to decide whether
        // to drive the room AC alongside the wearable.
        await PublishDiscoveryEntityAsync("sensor", "reason", new
        {
            name = "Reon trigger reason",
            unique_id = "reonosc_reason",
            state_topic = stateTopic,
            value_template = "{{ value_json.reason }}",
            icon = "mdi:label-outline",
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

        // OSC inputs as first-class HA entities. Wind stays as a binary
        // sensor (sender emits a bool). Cold/heat are exposed BOTH as a
        // 0..1 numeric sensor AND as a binary "active" sensor (ON when
        // the float is > 0) so an automation can pick whichever shape
        // fits — a fan curve off the numeric, a simple AC on/off off
        // the binary. Water and PFHotHigh aren't exposed as their own
        // entities (the user didn't ask for them and they're already in
        // value_json.inputs for anyone who needs them).
        await PublishDiscoveryEntityAsync("binary_sensor", "wind", new
        {
            name = "Reon wind input",
            unique_id = "reonosc_wind",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if (value_json.inputs and value_json.inputs.wind) else 'OFF' }}",
            icon = "mdi:weather-windy",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("sensor", "cold", new
        {
            name = "Reon cold input",
            unique_id = "reonosc_cold",
            state_topic = stateTopic,
            value_template = "{{ value_json.inputs.cold if value_json.inputs else 0 }}",
            icon = "mdi:snowflake",
            state_class = "measurement",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("binary_sensor", "cold_active", new
        {
            name = "Reon cold active",
            unique_id = "reonosc_cold_active",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if (value_json.inputs and value_json.inputs.cold > 0) else 'OFF' }}",
            icon = "mdi:snowflake",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("sensor", "heat", new
        {
            name = "Reon heat input",
            unique_id = "reonosc_heat",
            state_topic = stateTopic,
            value_template = "{{ value_json.inputs.heat if value_json.inputs else 0 }}",
            icon = "mdi:fire",
            state_class = "measurement",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        await PublishDiscoveryEntityAsync("binary_sensor", "heat_active", new
        {
            name = "Reon heat active",
            unique_id = "reonosc_heat_active",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if (value_json.inputs and value_json.inputs.heat > 0) else 'OFF' }}",
            icon = "mdi:fire",
            availability_topic = availabilityTopic,
            device,
        }).ConfigureAwait(false);

        // Retire the entities published by previous builds so HA doesn't
        // keep showing them as 'unavailable' indefinitely. An empty retained
        // payload on a discovery topic tells HA to delete that entity.
        await RetireDiscoveryEntityAsync("binary_sensor", "water").ConfigureAwait(false);
        await RetireDiscoveryEntityAsync("binary_sensor", "pfhothigh").ConfigureAwait(false);
    }

    /// <summary>Tell HA to delete a previously-published discovery entity
    /// by sending an empty retained payload to its config topic. Idempotent.</summary>
    private Task RetireDiscoveryEntityAsync(string component, string objectId)
    {
        var topic = $"{_settings.MqttDiscoveryPrefix.TrimEnd('/')}/{component}/reonosc/{objectId}/config";
        return PublishRawAsync(topic, "", retain: true);
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
