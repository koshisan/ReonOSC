using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.WinForms;
using ReonOSC.Ble;
using ReonOSC.Models;
using ReonOSC.Services;

namespace ReonOSC.Bridge;

/// <summary>
/// Translates between the WebView2-hosted React UI and the C# backend.
///
/// Contract — see design_handoff_reonosc/README.md. Inbound messages are
/// JSON-encoded { cmd, payload }; outbound events are JSON-encoded
/// { event, payload }.
/// </summary>
public sealed class WebViewBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebView2 _web;
    private readonly ControlService _service;
    private readonly System.Threading.SynchronizationContext? _uiCtx;
    private Settings _settings;
    private DateTime? _connectedAt;
    private ulong? _knownAddress;
    private readonly System.Threading.Timer _packetPollTimer;
    private long _lastPushedPacketCount = -1;

    public WebViewBridge(WebView2 web, ControlService service, Settings settings)
    {
        _web = web;
        _service = service;
        _settings = settings;
        _uiCtx = System.Threading.SynchronizationContext.Current;

        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        _service.Log += (_, msg) => Push("log.line", new { t = Ts(), kind = ClassifyLog(msg), msg });
        _service.CommandSent += (_, cmd) => PushCurrent(cmd, _service.ManualOverride ? "Manual" : "OSC");
        _service.Reon.TelemetryReceived += (_, t) => Push("telemetry", new
        {
            plate = t.SkinPlate, sink = t.Heatsink, board = t.Board, ambient = t.Ambient,
        });
        _service.InputsChanged += (_, snapshot) => PushInputs(snapshot);
        _service.PfSignal.SignalChanged += (_, sample) => Push("pf.signal", new
        {
            hex = sample.ToString(),
            r = sample.R, g = sample.G, b = sample.B,
            running = _service.PfSignal.IsRunning,
        });

        // Poll the OSC packet counter every 500 ms and push osc.state when it
        // changes, so the UI can show a live 'X packets' counter without per-
        // packet log spam.
        _packetPollTimer = new System.Threading.Timer(_ =>
        {
            var n = _service.OscPacketsReceived;
            if (n != _lastPushedPacketCount)
            {
                _lastPushedPacketCount = n;
                PushOscState();
            }
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public void Dispose()
    {
        try { _packetPollTimer.Dispose(); } catch { }
        try { _web.CoreWebView2.WebMessageReceived -= OnWebMessage; } catch { }
    }

    // -------- inbound: JS -> C# --------------------------------------------

    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.WebMessageAsJson ?? ""; }
        catch { try { raw = e.TryGetWebMessageAsString() ?? ""; } catch { return; } }
        if (string.IsNullOrEmpty(raw)) return;

        try
        {
            // The JS posts a JSON-encoded string; WebView's WebMessageAsJson returns that
            // string already wrapped as a JSON string literal. Unwrap once if needed.
            using var outer = JsonDocument.Parse(raw);
            JsonElement env;
            if (outer.RootElement.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(outer.RootElement.GetString() ?? "{}");
                env = inner.RootElement.Clone();
            }
            else
            {
                env = outer.RootElement.Clone();
            }

            var cmd = env.TryGetProperty("cmd", out var cmdProp) ? cmdProp.GetString() : null;
            if (string.IsNullOrEmpty(cmd)) return;
            var payload = env.TryGetProperty("payload", out var p) ? p : default;

            HandleCommand(cmd, payload);
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"Bridge parse error: {ex.Message}" });
        }
    }

    private void HandleCommand(string cmd, JsonElement payload)
    {
        switch (cmd)
        {
            case "ui.ready":       OnUiReady(); break;
            case "connect":        _ = ConnectAsync(); break;
            case "disconnect":     _ = DisconnectAsync(); break;
            case "pair":           _ = PairAsync(); break;
            case "osc.start":      OscStart(payload); break;
            case "osc.stop":       _service.StopOsc(); PushOscState(); break;
            case "osc.setPort":    SetOscPort(payload); break;
            case "osc.setAddress": SetAddress(payload); break;
            case "manual.toggle":  _ = ToggleManual(payload); break;
            case "manual.set":     _ = SetManual(payload); break;
            case "preset.heat":    PresetHeat(payload); break;
            case "preset.cold":    PresetCold(payload); break;
            case "options.set":    SetOptions(payload); break;
            case "pfHook.toggle":  TogglePfHook(payload); break;
            case "log.clear":      /* the UI owns its own log buffer */ break;
            default: Push("log.line", new { t = Ts(), kind = "err", msg = $"Unknown command: {cmd}" }); break;
        }
    }

    // -------- command handlers ---------------------------------------------

    private void OnUiReady()
    {
        Push("settings", new
        {
            oscPort = _settings.OscPort,
            addresses = new
            {
                PFHotHigh = _settings.AddrPfHotHigh,
                water = _settings.AddrWater,
                cold = _settings.AddrCold,
                heat = _settings.AddrHeat,
            },
            heatTouchLevel = _settings.HeatTouchLevel,
            coldWaterLevel = _settings.ColdWaterLevel,
            startMinimised = _settings.StartMinimised,
            autoConnectOnStart = _settings.AutoConnectOnStart,
            enablePfSignalHook = _settings.EnablePfSignalHook,
        });
        Push("pf.signal", new { running = _service.PfSignal.IsRunning, hex = "—" });
        // If TrayContext's silent auto-start didn't bring OSC up (port collision,
        // permission, etc.), retry here so the user sees the failure in the log
        // and can change the port from the UI. Either way, surface the final
        // state via PushOscState below.
        if (!_service.Osc.IsRunning)
        {
            try
            {
                _service.StartOsc();
                Push("log.line", new { t = Ts(), kind = "info", msg = $"OSC listening on UDP {_service.Osc.Port}" });
            }
            catch (Exception ex)
            {
                Push("log.line", new { t = Ts(), kind = "err",
                    msg = $"OSC auto-start failed on port {_settings.OscPort}: {ex.Message}" });
            }
        }
        else
        {
            // Auto-start happened earlier (silently). Echo the state into the GUI log too.
            Push("log.line", new { t = Ts(), kind = "info", msg = $"OSC listening on UDP {_service.Osc.Port}" });
        }
        PushOscState();
        Push("conn.state", new
        {
            state = _service.Reon.IsConnected ? "connected" : "disconnected",
            mac = _settings.LastKnownMac,
            model = _service.Reon.Model,
            caps = new
            {
                coolMax = _service.Reon.Capabilities.CoolLevelMax,
                heatMax = _service.Reon.Capabilities.HeatLevelMax,
            },
        });
        if (_service.LastSentCommand.Mode != ReonProtocol.Mode.Stop)
            PushCurrent(_service.LastSentCommand, _service.ManualOverride ? "Manual" : "OSC");

        // Push the current OSC inputs once so the GUI has a baseline reading.
        PushInputs(_service.Snapshot());

        // The first OSC packet may have arrived before the bridge was wired
        // (the GUI takes a few seconds to come up). Surface that info now
        // so the user can confirm the pipe and see what's actually arriving.
        if (_service.FirstOscAddress is { } addr)
        {
            Push("log.line", new { t = Ts(), kind = "info",
                msg = $"OSC first packet: {addr} = {_service.FirstOscArg}  ({_service.OscPacketsReceived} total)" });
        }

        if (_settings.AutoConnectOnStart && !_service.Reon.IsConnected)
            _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_service.Reon.IsConnected) { Push("conn.state", new { state = "connected" }); return; }

        Push("conn.state", new { state = "connecting" });
        Push("log.line", new { t = Ts(), kind = "info", msg = "Connecting …" });

        try
        {
            var addr = await ResolveAddressAsync();
            if (addr is null)
            {
                Push("log.line", new { t = Ts(), kind = "err", msg = "Reon not visible." });
                Push("conn.state", new { state = "disconnected" });
                return;
            }

            var stored = TokenStorage.Load();
            if (stored is null)
            {
                Push("log.line", new { t = Ts(), kind = "err", msg = "No bond token saved. Pair the device first." });
                Push("conn.state", new { state = "disconnected" });
                return;
            }

            await _service.Reon.ConnectAsync(addr.Value, stored.TokenBytes);
            _connectedAt = DateTime.UtcNow;
            _knownAddress = addr;
            Push("conn.state", new
            {
                state = "connected",
                mac = ReonClient.FormatMac(addr.Value),
                model = _service.Reon.Model,
                caps = new
                {
                    coolMax = _service.Reon.Capabilities.CoolLevelMax,
                    heatMax = _service.Reon.Capabilities.HeatLevelMax,
                },
            });
            Push("log.line", new { t = Ts(), kind = "ok", msg = $"Connected and authed{(_service.Reon.Model is { } m ? $" ({m})" : "")}." });
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"Connect failed: {ex.Message}" });
            Push("conn.state", new { state = "disconnected" });
        }
    }

    private async Task DisconnectAsync()
    {
        try { await _service.Reon.DisposeAsync(); } catch { }
        _connectedAt = null;
        Push("conn.state", new { state = "disconnected" });
        Push("log.line", new { t = Ts(), kind = "info", msg = "Disconnected." });
    }

    private async Task PairAsync()
    {
        Push("conn.state", new { state = "pairing" });
        Push("log.line", new { t = Ts(), kind = "info", msg = "Scanning for Reon in pair mode …" });

        try
        {
            var addr = await ReonClient.FindReonAsync(TimeSpan.FromSeconds(8));
            if (addr is null)
            {
                Push("log.line", new { t = Ts(), kind = "err", msg = "Reon not found. Long-press the button to enter pair mode." });
                Push("conn.state", new { state = "disconnected" });
                return;
            }

            var token = new byte[ReonProtocol.AuthTokenLength];
            token[0] = 0x01;
            System.Security.Cryptography.RandomNumberGenerator.Fill(token.AsSpan(1));

            // If we're already connected from a previous session, drop that first
            // so PairAsync can take over the BLE handle cleanly.
            if (_service.Reon.IsConnected)
            {
                try { await _service.Reon.DisposeAsync(); } catch { }
            }

            // Pair AND fully connect in one shot using the main client, so the
            // user can issue commands immediately without a separate Connect.
            await _service.Reon.PairAsync(addr.Value, token);

            _settings.LastKnownMac = ReonClient.FormatMac(addr.Value);
            _settings.Save();
            _knownAddress = addr;
            _connectedAt = DateTime.UtcNow;

            Push("log.line", new { t = Ts(), kind = "ok", msg = $"Paired and connected{(_service.Reon.Model is { } m ? $" ({m})" : "")}." });
            Push("conn.state", new
            {
                state = "connected",
                mac = ReonClient.FormatMac(addr.Value),
                model = _service.Reon.Model,
                caps = new
                {
                    coolMax = _service.Reon.Capabilities.CoolLevelMax,
                    heatMax = _service.Reon.Capabilities.HeatLevelMax,
                },
            });
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"Pair failed: {ex.Message}" });
            Push("conn.state", new { state = "disconnected" });
        }
    }

    private void OscStart(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var port))
        {
            _settings.OscPort = port;
            _settings.Save();
            _service.ApplySettings(_settings);
        }
        try
        {
            if (_service.Osc.IsRunning) _service.StopOsc();
            _service.StartOsc();
            Push("log.line", new { t = Ts(), kind = "info", msg = $"OSC listening on UDP {_service.Osc.Port}" });
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"OSC start failed: {ex.Message}" });
        }
        PushOscState();
    }

    private void SetOscPort(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        if (!payload.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port)) return;
        if (port < 1 || port > 65535) return;
        if (_settings.OscPort == port) return;

        _settings.OscPort = port;
        _settings.Save();
        // Don't auto-restart a stopped server here; if it's running, ApplySettings
        // will hot-swap the listener to the new port.
        _service.ApplySettings(_settings);
        PushOscState();
    }

    private void SetAddress(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        var key = payload.TryGetProperty("key", out var k) ? k.GetString() : null;
        var addr = payload.TryGetProperty("address", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(key) || addr is null) return;

        switch (key)
        {
            case "PFHotHigh": _settings.AddrPfHotHigh = addr; break;
            case "water":     _settings.AddrWater     = addr; break;
            case "cold":      _settings.AddrCold      = addr; break;
            case "heat":      _settings.AddrHeat      = addr; break;
            default: return;
        }
        _settings.Save();
        _service.ApplySettings(_settings);
    }

    private async Task ToggleManual(JsonElement payload)
    {
        var enabled = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("enabled", out var e) && e.GetBoolean();
        _service.ManualOverride = enabled;
        await _service.ReconcileAsync();
    }

    private async Task SetManual(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        var modeStr = payload.TryGetProperty("mode", out var m) ? m.GetString() : "Stop";
        var level = payload.TryGetProperty("level", out var l) && l.TryGetInt32(out var li) ? li : 0;
        var mode = modeStr switch
        {
            "Cool" => ReonProtocol.Mode.Cool,
            "Heat" => ReonProtocol.Mode.Heat,
            _      => ReonProtocol.Mode.Stop,
        };

        // Clamp against the per-direction cap derived from the connected
        // device's model. The GUI honours this too, but we double-check here
        // so a stale UI / future caller can't punch through.
        var caps = _service.Reon.Capabilities;
        var modeMax = mode == ReonProtocol.Mode.Heat ? caps.HeatLevelMax : caps.CoolLevelMax;
        level = Math.Clamp(level, ReonProtocol.LevelMin, modeMax);

        _service.ManualCommand = new ResolvedCommand(mode, level);
        await _service.ReconcileAsync();
    }

    private void PresetHeat(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("level", out var l) && l.TryGetInt32(out var lvl))
        {
            _settings.HeatTouchLevel = Math.Clamp(lvl, 0, ReonProtocol.LevelMax);
            _settings.Save();
            _service.ApplySettings(_settings);
        }
    }

    private void PresetCold(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("level", out var l) && l.TryGetInt32(out var lvl))
        {
            _settings.ColdWaterLevel = Math.Clamp(lvl, 0, ReonProtocol.LevelMax);
            _settings.Save();
            _service.ApplySettings(_settings);
        }
    }

    private void TogglePfHook(JsonElement payload)
    {
        var enabled = payload.ValueKind == JsonValueKind.Object
                      && payload.TryGetProperty("enabled", out var e)
                      && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                      && e.GetBoolean();

        _settings.EnablePfSignalHook = enabled;
        _settings.Save();

        if (enabled)
        {
            try { _service.PfSignal.Start(); }
            catch (Exception ex) { Push("log.line", new { t = Ts(), kind = "err", msg = $"PF hook start failed: {ex.Message}" }); }
        }
        else
        {
            _service.PfSignal.Stop();
        }
        Push("pf.signal", new { running = _service.PfSignal.IsRunning, hex = "—" });
    }

    private void SetOptions(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        if (payload.TryGetProperty("startMinimised", out var sm) && (sm.ValueKind is JsonValueKind.True or JsonValueKind.False))
            _settings.StartMinimised = sm.GetBoolean();
        if (payload.TryGetProperty("autoConnect", out var ac) && (ac.ValueKind is JsonValueKind.True or JsonValueKind.False))
            _settings.AutoConnectOnStart = ac.GetBoolean();
        _settings.Save();
        _service.ApplySettings(_settings);
    }

    // -------- outbound helpers ---------------------------------------------

    private void Push(string eventName, object? payload)
    {
        var envelope = new Dictionary<string, object?> { ["event"] = eventName, ["payload"] = payload };
        var json = JsonSerializer.Serialize(envelope, JsonOpts);

        void Send()
        {
            try { _web.CoreWebView2.PostWebMessageAsJson(json); }
            catch { /* the WebView may be closing */ }
        }

        if (_uiCtx is null || System.Threading.SynchronizationContext.Current == _uiCtx) Send();
        else _uiCtx.Post(_ => Send(), null);
    }

    private void PushOscState()
        => Push("osc.state", new
        {
            running = _service.Osc.IsRunning,
            port = _service.Osc.IsRunning ? _service.Osc.Port : _settings.OscPort,
            packets = _service.OscPacketsReceived,
        });

    private void PushCurrent(ResolvedCommand cmd, string source)
    {
        var modeStr = cmd.Mode switch
        {
            ReonProtocol.Mode.Cool => "Cool",
            ReonProtocol.Mode.Heat => "Heat",
            _ => "Stop",
        };
        Push("state.current", new { mode = modeStr, level = cmd.Level, source });
        var kind = modeStr.ToLowerInvariant();
        var line = modeStr == "Stop" ? "→ Stop" : $"→ {modeStr} L{cmd.Level}";
        Push("log.line", new { t = Ts(), kind, msg = line });
    }

    private void PushInputs(IReadOnlyDictionary<string, float> snapshot)
    {
        Push("osc.input", new
        {
            PFHotHigh = snapshot.GetValueOrDefault("PFHotHigh"),
            water = snapshot.GetValueOrDefault("water"),
            cold = snapshot.GetValueOrDefault("cold"),
            heat = snapshot.GetValueOrDefault("heat"),
        });
    }

    private async Task<ulong?> ResolveAddressAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastKnownMac))
        {
            try { return ReonClient.ParseMac(_settings.LastKnownMac); }
            catch { /* fall through to scan */ }
        }
        return await ReonClient.FindReonAsync(TimeSpan.FromSeconds(8));
    }

    private static string Ts() => DateTime.Now.ToString("HH:mm:ss");

    private static string ClassifyLog(string msg)
    {
        var m = msg ?? "";
        if (m.StartsWith("→ Cool", StringComparison.OrdinalIgnoreCase)) return "cool";
        if (m.StartsWith("→ Heat", StringComparison.OrdinalIgnoreCase)) return "heat";
        if (m.StartsWith("→ Stop", StringComparison.OrdinalIgnoreCase)) return "stop";
        if (m.Contains("Connected", StringComparison.OrdinalIgnoreCase) || m.Contains("Paired", StringComparison.OrdinalIgnoreCase)) return "ok";
        if (m.Contains("fail", StringComparison.OrdinalIgnoreCase) || m.Contains("error", StringComparison.OrdinalIgnoreCase) || m.Contains("rejected", StringComparison.OrdinalIgnoreCase)) return "err";
        return "info";
    }
}
