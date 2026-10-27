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
    private readonly MqttPublisher? _mqtt;
    private readonly System.Threading.SynchronizationContext? _uiCtx;
    private Settings _settings;
    private DateTime? _connectedAt;
    private ulong? _knownAddress;
    private readonly System.Threading.Timer _packetPollTimer;
    private long _lastPushedPacketCount = -1;
    private string _lastPfHex = "#000000";

    public WebViewBridge(WebView2 web, ControlService service, Settings settings, MqttPublisher? mqtt)
    {
        _web = web;
        _service = service;
        _settings = settings;
        _mqtt = mqtt;
        _uiCtx = System.Threading.SynchronizationContext.Current;

        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        _service.Log += (_, msg) => Push("log.line", new { t = Ts(), kind = ClassifyLog(msg), msg });
        _service.CommandSent += (_, cmd) =>
        {
            PushCurrent(cmd, _service.LastCommandSource, _service.LastCommandReason);
            PushZones();
        };
        // Reason-only updates: when the trigger changes but the wire command
        // stays the same (e.g. PFSignal → OSC:PFHotHigh both resolving to
        // Heat L3), CommandSent doesn't fire. StateChanged catches it so the
        // GUI's SOURCE/reason indicator stays honest.
        _service.StateChanged += (_, s) =>
        {
            Push("state.current", new
            {
                mode = s.cmd.Mode switch
                {
                    ReonProtocol.Mode.Cool => "Cool",
                    ReonProtocol.Mode.Heat => "Heat",
                    _ => "Stop",
                },
                level = s.cmd.Level,
                source = s.source,
                reason = s.reason,
            });
            PushZones();
        };
        _service.SockTargetChanged += (_, _) => PushZones();
        _service.SockL.ConnectionChanged += (_, up) => PushDeviceState("sockL", up ? "connected" : "disconnected");
        _service.SockR.ConnectionChanged += (_, up) => PushDeviceState("sockR", up ? "connected" : "disconnected");
        _service.SockL.BatteryChanged += (_, _) => PushDeviceState("sockL",
            _service.SockL.IsConnected ? "connected" : "disconnected");
        _service.SockR.BatteryChanged += (_, _) => PushDeviceState("sockR",
            _service.SockR.IsConnected ? "connected" : "disconnected");
        _service.Reon.TelemetryReceived += (_, t) => Push("telemetry", new
        {
            plate = t.SkinPlate, sink = t.Heatsink, board = t.Board, ambient = t.Ambient,
        });
        _service.InputsChanged += (_, snapshot) => PushInputs(snapshot);
        _service.PfSignal.SignalChanged += (_, sample) =>
        {
            _lastPfHex = sample.ToString();
            var thermal = PfSignalDecoder.Decode(sample);
            Push("pf.signal", new
            {
                hex = _lastPfHex,
                r = sample.R, g = sample.G, b = sample.B,
                running = _service.PfSignal.IsRunning,
                backend = _service.PfSignal.ActiveBackend.ToString(),
                mode = thermal.Mode.ToString(),  // "Off" / "Cool" / "Hot"
                level = thermal.Level,
                decoded = thermal.ToString(),    // "Cool L2" etc.
            });
        };
        // NB: no log line on signal change — in a real VR scene the sampled
        // pixel updates on every render frame (head motion, animations, etc.),
        // which would flood the log. The live swatch in the GUI is the
        // observation surface.

        if (_mqtt is not null)
        {
            _mqtt.Log += (_, msg) => Push("log.line", new { t = Ts(), kind = ClassifyLog(msg), msg });
            _mqtt.StateChanged += (_, _) => PushMqttState();
        }

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
            case "ui.ready":        OnUiReady(); break;

            // Legacy (reon-only) connect/disconnect/pair kept so the current
            // UI still works until Phase 4 swaps it out.
            case "connect":         _ = ConnectAsync(); break;
            case "disconnect":      _ = DisconnectAsync(); break;
            case "pair":            _ = PairAsync(); break;

            // New per-device contract: {id: "reon" | "sockL" | "sockR"}.
            case "device.connect":    _ = DeviceConnectAsync(payload); break;
            case "device.disconnect": _ = DeviceDisconnectAsync(payload); break;
            case "device.pair":       _ = DevicePairAsync(payload); break;

            case "osc.start":       OscStart(payload); break;
            case "osc.stop":        _service.StopOsc(); PushOscState(); break;
            case "osc.setPort":     SetOscPort(payload); break;
            case "osc.setAddress":  SetAddress(payload); break;
            case "manual.toggle":   _ = ToggleManual(payload); break;
            case "manual.set":      _ = SetManual(payload); break;
            case "preset.heat":     PresetHeat(payload); break;
            case "preset.cold":     PresetCold(payload); break;
            case "socks.config":    _ = SocksConfig(payload); break;
            case "options.set":     SetOptions(payload); break;
            case "pfHook.toggle":   TogglePfHook(payload); break;
            case "pfSignal.capture": CapturePfSignal(payload); break;
            case "mqtt.set":        SetMqtt(payload); break;
            case "log.clear":       /* the UI owns its own log buffer */ break;
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
                wind = _settings.AddrWind,
                touchL = _settings.AddrTouchL,
                touchR = _settings.AddrTouchR,
            },
            heatTouchLevel = _settings.HeatTouchLevel,
            coldWaterLevel = _settings.ColdWaterLevel,
            startMinimised = _settings.StartMinimised,
            autoConnectOnStart = _settings.AutoConnectOnStart,
            enablePfSignalHook = _settings.EnablePfSignalHook,
            socks = new
            {
                lMac = _settings.SockLMac,
                rMac = _settings.SockRMac,
                baselineOn = _settings.SockBaselineOn,
                baseline = _settings.SockBaseline,
                heatTouch = _settings.SockHeatTouch,
                heatMax = _settings.SockHeatMax,
                touchMax = _settings.SockTouchMax,
                limit = _settings.SockLimit,
                manualL = _settings.ManualSockL,
                manualR = _settings.ManualSockR,
            },
            mqtt = new
            {
                enabled = _settings.MqttEnabled,
                host = _settings.MqttHost,
                port = _settings.MqttPort,
                username = _settings.MqttUsername,
                // Password intentionally NOT echoed back to the UI — the user
                // types it once, we keep it in settings.json, and the field
                // shows empty on reload (placeholder behaviour).
                baseTopic = _settings.MqttBaseTopic,
                discoveryPrefix = _settings.MqttDiscoveryPrefix,
            },
        });
        PushMqttState();
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
        if (_service.LastResolvedCommand.Mode != ReonProtocol.Mode.Stop)
            PushCurrent(_service.LastResolvedCommand, _service.LastCommandSource, _service.LastCommandReason);

        // Push the current OSC inputs once so the GUI has a baseline reading.
        PushInputs(_service.Snapshot());

        // New v3 scoped events for the body-map UI.
        PushDeviceState("reon",
            _service.Reon.IsConnected ? "connected" : "disconnected", _settings.LastKnownMac);
        PushDeviceState("sockL",
            _service.SockL.IsConnected ? "connected" : "disconnected", _settings.SockLMac);
        PushDeviceState("sockR",
            _service.SockR.IsConnected ? "connected" : "disconnected", _settings.SockRMac);
        PushZones();

        // Auto-connect any socks that already have a MAC saved. The Reon's
        // auto-connect is handled below.
        if (_settings.AutoConnectOnStart)
        {
            if (!string.IsNullOrWhiteSpace(_settings.SockLMac))
                _ = ConnectSockAsync(SockClient.Side.Left);
            if (!string.IsNullOrWhiteSpace(_settings.SockRMac))
                _ = ConnectSockAsync(SockClient.Side.Right);
        }

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
        if (_service.Reon.IsConnected)
        {
            Push("conn.state", new { state = "connected" });
            PushDeviceState("reon", "connected");
            return;
        }

        Push("conn.state", new { state = "connecting" });
        PushDeviceState("reon", "connecting");
        Push("log.line", new { t = Ts(), kind = "info", msg = "Connecting …" });

        try
        {
            var addr = await ResolveAddressAsync();
            if (addr is null)
            {
                Push("log.line", new { t = Ts(), kind = "err", msg = "Reon not visible." });
                Push("conn.state", new { state = "disconnected" });
                PushDeviceState("reon", "disconnected");
                return;
            }

            var stored = TokenStorage.Load();
            if (stored is null)
            {
                Push("log.line", new { t = Ts(), kind = "err", msg = "No bond token saved. Pair the device first." });
                Push("conn.state", new { state = "disconnected" });
                PushDeviceState("reon", "disconnected");
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
            PushDeviceState("reon", "connected", ReonClient.FormatMac(addr.Value));
            Push("log.line", new { t = Ts(), kind = "ok", msg = $"Connected and authed{(_service.Reon.Model is { } m ? $" ({m})" : "")}." });
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"Connect failed: {ex.Message}" });
            Push("conn.state", new { state = "disconnected" });
            PushDeviceState("reon", "disconnected");
        }
    }

    private async Task DisconnectAsync()
    {
        try { await _service.Reon.DisposeAsync(); } catch { }
        _connectedAt = null;
        Push("conn.state", new { state = "disconnected" });
        PushDeviceState("reon", "disconnected");
        Push("log.line", new { t = Ts(), kind = "info", msg = "Disconnected." });
    }

    // ---- per-device dispatch (new contract) --------------------------------

    private static string? DeviceId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        return payload.TryGetProperty("id", out var i) ? i.GetString() : null;
    }

    private Task DeviceConnectAsync(JsonElement payload) => (DeviceId(payload)) switch
    {
        "reon"  => ConnectAsync(),
        "sockL" => ConnectSockAsync(SockClient.Side.Left),
        "sockR" => ConnectSockAsync(SockClient.Side.Right),
        _ => Task.CompletedTask,
    };

    private Task DeviceDisconnectAsync(JsonElement payload) => (DeviceId(payload)) switch
    {
        "reon"  => DisconnectAsync(),
        "sockL" => _service.UnbindSockAsync(SockClient.Side.Left).ContinueWith(_ =>
                    PushDeviceState("sockL", "disconnected")),
        "sockR" => _service.UnbindSockAsync(SockClient.Side.Right).ContinueWith(_ =>
                    PushDeviceState("sockR", "disconnected")),
        _ => Task.CompletedTask,
    };

    private Task DevicePairAsync(JsonElement payload) => (DeviceId(payload)) switch
    {
        "reon"  => PairAsync(),
        "sockL" => PairSockAsync(SockClient.Side.Left),
        "sockR" => PairSockAsync(SockClient.Side.Right),
        _ => Task.CompletedTask,
    };

    private async Task ConnectSockAsync(SockClient.Side side)
    {
        var mac = side == SockClient.Side.Left ? _settings.SockLMac : _settings.SockRMac;
        if (string.IsNullOrWhiteSpace(mac))
        {
            Push("log.line", new { t = Ts(), kind = "err",
                msg = $"Sock {(side == SockClient.Side.Left ? "L" : "R")} has no MAC saved — pair first." });
            PushDeviceState(side == SockClient.Side.Left ? "sockL" : "sockR", "disconnected");
            return;
        }
        PushDeviceState(side == SockClient.Side.Left ? "sockL" : "sockR", "connecting", mac);
        try { await _service.BindSockAsync(side, SockClient.ParseMac(mac)); }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err",
                msg = $"Sock {(side == SockClient.Side.Left ? "L" : "R")} bind failed: {ex.Message}" });
        }
    }

    private async Task PairSockAsync(SockClient.Side side)
    {
        var tag = side == SockClient.Side.Left ? "sockL" : "sockR";
        var label = side == SockClient.Side.Left ? "L" : "R";
        PushDeviceState(tag, "pairing");
        Push("log.line", new { t = Ts(), kind = "info", msg = $"Scanning for Sock {label} …" });

        try
        {
            var found = await SockClient.ScanForSocksAsync(TimeSpan.FromSeconds(20));
            // Prefer a MAC that isn't already bound to the other foot.
            var otherMac = side == SockClient.Side.Left ? _settings.SockRMac : _settings.SockLMac;
            ulong? otherParsed = null;
            if (!string.IsNullOrWhiteSpace(otherMac))
            { try { otherParsed = SockClient.ParseMac(otherMac); } catch { } }

            var pick = found.FirstOrDefault(m => m != otherParsed);
            if (pick == 0)
            {
                Push("log.line", new { t = Ts(), kind = "err",
                    msg = $"Sock {label} not found. ({found.Count} candidates seen; move the sock closer or press its button to wake it.)" });
                PushDeviceState(tag, "disconnected");
                return;
            }

            var macStr = SockClient.FormatMac(pick);
            if (side == SockClient.Side.Left) _settings.SockLMac = macStr;
            else                              _settings.SockRMac = macStr;
            _settings.Save();

            Push("log.line", new { t = Ts(), kind = "ok", msg = $"Sock {label} paired → {macStr}" });
            PushDeviceState(tag, "connecting", macStr);
            await _service.BindSockAsync(side, pick);
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"Sock {label} pair failed: {ex.Message}" });
            PushDeviceState(tag, "disconnected");
        }
    }

    private async Task PairAsync()
    {
        Push("conn.state", new { state = "pairing" });
        PushDeviceState("reon", "pairing");
        Push("log.line", new { t = Ts(), kind = "info", msg = "Scanning for Reon in pair mode …" });

        try
        {
            var addr = await ReonClient.FindReonAsync(TimeSpan.FromSeconds(8));
            if (addr is null)
            {
                Push("log.line", new { t = Ts(), kind = "err", msg = "Reon not found. Long-press the button to enter pair mode." });
                Push("conn.state", new { state = "disconnected" });
                PushDeviceState("reon", "disconnected");
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
            PushDeviceState("reon", "connected", ReonClient.FormatMac(addr.Value));
        }
        catch (Exception ex)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"Pair failed: {ex.Message}" });
            Push("conn.state", new { state = "disconnected" });
            PushDeviceState("reon", "disconnected");
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
            case "wind":      _settings.AddrWind      = addr; break;
            case "touchL":    _settings.AddrTouchL    = addr; break;
            case "touchR":    _settings.AddrTouchR    = addr; break;
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

        // Legacy single-mode form (Reon only).
        if (payload.TryGetProperty("mode", out var m))
        {
            var modeStr = m.GetString();
            var level = payload.TryGetProperty("level", out var l) && l.TryGetInt32(out var li) ? li : 0;
            var mode = modeStr switch
            {
                "Cool" => ReonProtocol.Mode.Cool,
                "Heat" => ReonProtocol.Mode.Heat,
                _      => ReonProtocol.Mode.Stop,
            };
            var caps = _service.Reon.Capabilities;
            var modeMax = mode == ReonProtocol.Mode.Heat ? caps.HeatLevelMax : caps.CoolLevelMax;
            level = Math.Clamp(level, ReonProtocol.LevelMin, modeMax);
            _service.ManualCommand = new ResolvedCommand(mode, level);
        }
        else if (payload.TryGetProperty("reonMode", out var rm))
        {
            // New v3 form: {enabled?, reonMode, reonLevel, sockL, sockR}.
            var modeStr = rm.GetString();
            var level = payload.TryGetProperty("reonLevel", out var rl) && rl.TryGetInt32(out var rli) ? rli : 0;
            var mode = modeStr switch
            {
                "Cool" => ReonProtocol.Mode.Cool,
                "Heat" => ReonProtocol.Mode.Heat,
                _      => ReonProtocol.Mode.Stop,
            };
            var caps = _service.Reon.Capabilities;
            var modeMax = mode == ReonProtocol.Mode.Heat ? caps.HeatLevelMax : caps.CoolLevelMax;
            level = Math.Clamp(level, ReonProtocol.LevelMin, modeMax);
            _service.ManualCommand = new ResolvedCommand(mode, level);
        }

        // Both forms accept optional sockL / sockR temperatures.
        bool sockChanged = false;
        if (payload.TryGetProperty("sockL", out var sL) && sL.TryGetInt32(out var sLv))
        {
            var clamped = Math.Clamp(sLv, 0, SockResolver.ClampLimit(_settings.SockLimit));
            if (_settings.ManualSockL != clamped) { _settings.ManualSockL = clamped; sockChanged = true; }
        }
        if (payload.TryGetProperty("sockR", out var sR) && sR.TryGetInt32(out var sRv))
        {
            var clamped = Math.Clamp(sRv, 0, SockResolver.ClampLimit(_settings.SockLimit));
            if (_settings.ManualSockR != clamped) { _settings.ManualSockR = clamped; sockChanged = true; }
        }
        if (payload.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _service.ManualOverride = en.GetBoolean();
        }
        if (sockChanged) _settings.Save();

        await _service.ReconcileAsync();
    }

    private async Task SocksConfig(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;

        bool anyChange = false;
        void SetInt(string key, Action<int> apply, int lo, int hi)
        {
            if (payload.TryGetProperty(key, out var v) && v.TryGetInt32(out var iv))
            {
                apply(Math.Clamp(iv, lo, hi));
                anyChange = true;
            }
        }
        void SetBool(string key, Action<bool> apply)
        {
            if (payload.TryGetProperty(key, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                apply(v.GetBoolean());
                anyChange = true;
            }
        }

        SetBool("baselineOn", b => _settings.SockBaselineOn = b);
        SetInt("baseline",   v => _settings.SockBaseline   = v, SockProtocol.TempMin, SockProtocol.TempMax);
        SetInt("heatTouch",  v => _settings.SockHeatTouch  = v, SockProtocol.TempMin, SockProtocol.TempMax);
        SetInt("heatMax",    v => _settings.SockHeatMax    = v, SockProtocol.TempMin, SockProtocol.TempMax);
        SetInt("touchMax",   v => _settings.SockTouchMax   = v, SockProtocol.TempMin, SockProtocol.TempMax);
        SetInt("limit",      v => _settings.SockLimit      = v, SockProtocol.TempMin, SockProtocol.TempMax);

        // Lowering the limit should drag every other target down with it so
        // the UI's "every value ≤ limit" invariant can't be violated by a
        // config sent out of order.
        var limit = _settings.SockLimit;
        if (_settings.SockBaseline  > limit) _settings.SockBaseline  = limit;
        if (_settings.SockHeatTouch > limit) _settings.SockHeatTouch = limit;
        if (_settings.SockHeatMax   > limit) _settings.SockHeatMax   = limit;
        if (_settings.SockTouchMax  > limit) _settings.SockTouchMax  = limit;
        if (_settings.ManualSockL   > limit) _settings.ManualSockL   = limit;
        if (_settings.ManualSockR   > limit) _settings.ManualSockR   = limit;

        if (anyChange) _settings.Save();
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

    private void CapturePfSignal(JsonElement payload)
    {
        if (!_service.PfSignal.IsRunning)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = "PF capture: hook is not running" });
            return;
        }
        // Persist the active backend's current frame to C:\temp so the user
        // can see exactly where the reader thinks the PFSignal pixels are.
        // Red boxes mark the finder candidate positions, green ones the
        // signal pixel candidates. Crucially: no text prompt — the original
        // window.prompt() was unusable in VR. The label payload field is
        // now ignored.
        var path = _service.PfSignal.CaptureToFile(@"C:\temp");
        var top = _service.PfSignal.LastFinderTop;
        var bot = _service.PfSignal.LastFinderBottom;
        var summary = $"signal={_lastPfHex} y0.03={top} y0.97={bot} orient={_service.PfSignal.LastOrientation} backend={_service.PfSignal.ActiveBackend}";
        if (path is null)
        {
            Push("log.line", new { t = Ts(), kind = "err", msg = $"PF capture: write failed. {summary}" });
        }
        else
        {
            Push("log.line", new { t = Ts(), kind = "info", msg = $"PF capture saved → {path}  ({summary})" });
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

    private void SetMqtt(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        if (_mqtt is null)
        {
            Push("log.line", new { t = Ts(), kind = "err",
                msg = "MQTT publisher unavailable (init failed at startup — see exe directory for MQTTnet.dll)" });
            return;
        }

        if (payload.TryGetProperty("enabled", out var en) && (en.ValueKind is JsonValueKind.True or JsonValueKind.False))
            _settings.MqttEnabled = en.GetBoolean();
        if (payload.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String)
            _settings.MqttHost = h.GetString() ?? "";
        if (payload.TryGetProperty("port", out var p) && p.TryGetInt32(out var port) && port is > 0 and <= 65535)
            _settings.MqttPort = port;
        if (payload.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String)
            _settings.MqttUsername = u.GetString() ?? "";
        // Password only updates when the user actually typed something — the
        // field arrives empty on a no-op reload (we never echo it back).
        if (payload.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String)
        {
            var s = pw.GetString() ?? "";
            if (!string.IsNullOrEmpty(s)) _settings.MqttPassword = s;
        }
        if (payload.TryGetProperty("baseTopic", out var b) && b.ValueKind == JsonValueKind.String)
            _settings.MqttBaseTopic = b.GetString() ?? "reonosc";
        if (payload.TryGetProperty("discoveryPrefix", out var d) && d.ValueKind == JsonValueKind.String)
            _settings.MqttDiscoveryPrefix = d.GetString() ?? "";

        _settings.Save();
        _mqtt.ApplySettings(_settings);
    }

    private void PushMqttState()
    {
        if (_mqtt is null)
        {
            Push("mqtt.state", new { state = "Unavailable", error = (string?)"MQTTnet failed to load" });
            return;
        }
        Push("mqtt.state", new
        {
            state = _mqtt.CurrentState.ToString(),
            error = _mqtt.LastError,
        });
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

    private void PushCurrent(ResolvedCommand cmd, string source, string reason)
    {
        var modeStr = cmd.Mode switch
        {
            ReonProtocol.Mode.Cool => "Cool",
            ReonProtocol.Mode.Heat => "Heat",
            _ => "Stop",
        };
        Push("state.current", new { mode = modeStr, level = cmd.Level, source, reason });
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
            wind = snapshot.GetValueOrDefault("wind"),
            touchL = snapshot.GetValueOrDefault("touchL"),
            touchR = snapshot.GetValueOrDefault("touchR"),
        });
    }

    /// <summary>v3 scoped device event: {id, state, mac, battery, model?, caps?}.
    /// Model/caps are only meaningful for the reon and are included on reon
    /// pushes; left out for the socks.</summary>
    private void PushDeviceState(string id, string state, string? mac = null)
    {
        object payload = id switch
        {
            "reon" => new
            {
                id,
                state,
                mac = mac ?? _settings.LastKnownMac,
                model = _service.Reon.Model,
                caps = new
                {
                    coolMax = _service.Reon.Capabilities.CoolLevelMax,
                    heatMax = _service.Reon.Capabilities.HeatLevelMax,
                },
            },
            "sockL" => new
            {
                id,
                state,
                mac = mac ?? _settings.SockLMac,
                battery = (int?)_service.SockL.BatteryPercent,
            },
            "sockR" => new
            {
                id,
                state,
                mac = mac ?? _settings.SockRMac,
                battery = (int?)_service.SockR.BatteryPercent,
            },
            _ => new { id, state },
        };
        Push("device.state", payload);
    }

    /// <summary>v3 zones event: {reon:{mode,level,source}, L:{target,source}, R:{target,source}}.
    /// Mirrors what the body-map UI reads.</summary>
    private void PushZones()
    {
        var cmd = _service.LastResolvedCommand;
        var reonMode = cmd.Mode switch
        {
            ReonProtocol.Mode.Cool => "Cool",
            ReonProtocol.Mode.Heat => "Heat",
            _ => "Stop",
        };
        Push("zones", new
        {
            reon = new
            {
                mode = reonMode,
                level = cmd.Level,
                source = _service.LastCommandSource,
                reason = _service.LastCommandReason,
            },
            l = new
            {
                target = (int)_service.LastResolvedSockL.TempC,
                source = _service.LastResolvedSockL.Source,
            },
            r = new
            {
                target = (int)_service.LastResolvedSockR.TempC,
                source = _service.LastResolvedSockR.Source,
            },
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
