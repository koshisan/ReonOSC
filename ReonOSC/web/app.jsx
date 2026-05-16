/* global React, ReactDOM, ReonDevice, reonBridge */
const { useState, useEffect, useRef, useCallback } = React;

/* ---------- helpers ---------- */
const pad = (n) => String(n).padStart(2, "0");
const fmtTime = (d = new Date()) =>
  `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;

const fmtUptime = (sec) => {
  if (!sec || sec < 0) return "—";
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = Math.floor(sec % 60);
  return `${pad(h)}:${pad(m)}:${pad(s)}`;
};

const MODES = ["Stop", "Cool", "Heat"];
const LEVEL_MAX = 4;

/* ---------- icons ---------- */
const Icon = ({ name, size = 14, stroke = 1.6 }) => {
  const paths = {
    link: <><path d="M9 7h-3a4 4 0 0 0 0 8h3M15 7h3a4 4 0 0 1 0 8h-3M8 11h8"/></>,
    unlink: <><path d="M9 7h-3a4 4 0 0 0 0 8h3M15 7h3a4 4 0 0 1 0 8h-3"/><path d="M4 4l16 16"/></>,
    bluetooth: <path d="M7 7l10 10-5 4V3l5 4L7 17"/>,
    play: <path d="M6 4l12 8-12 8z" strokeLinejoin="round"/>,
    stop: <rect x="6" y="6" width="12" height="12" rx="1"/>,
    chevD: <path d="M6 9l6 6 6-6"/>,
    chevU: <path d="M6 15l6-6 6 6"/>,
    trash: <><path d="M4 7h16M10 11v6M14 11v6M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12M9 7V4h6v3"/></>,
    moon: <path d="M20 14.5A8 8 0 1 1 9.5 4a7 7 0 0 0 10.5 10.5z"/>,
    sun: <><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M22 12h-2M4 12H2M19.07 4.93l-1.41 1.41M6.34 17.66l-1.41 1.41M19.07 19.07l-1.41-1.41M6.34 6.34L4.93 4.93"/></>,
  };
  return (
    <svg className="btn-icon" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={stroke} strokeLinecap="round" strokeLinejoin="round">
      {paths[name]}
    </svg>
  );
};

/* ---------- Sparkline ---------- */
function Sparkline({ data, color, height = 48 }) {
  if (!data.length) return <div className="sparkline"/>;
  const w = 460, h = height;
  const min = Math.min(...data), max = Math.max(...data);
  const span = max - min || 1;
  const step = w / Math.max(1, data.length - 1);
  const pts = data.map((v, i) => [i * step, h - ((v - min) / span) * (h - 6) - 3]);
  const d = pts.map(([x, y], i) => `${i ? "L" : "M"}${x.toFixed(1)} ${y.toFixed(1)}`).join(" ");
  const area = d + ` L ${w} ${h} L 0 ${h} Z`;
  return (
    <svg className="sparkline" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" xmlns="http://www.w3.org/2000/svg">
      <defs>
        <linearGradient id="spark-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.35"/>
          <stop offset="100%" stopColor={color} stopOpacity="0"/>
        </linearGradient>
      </defs>
      <path d={area} fill="url(#spark-fill)"/>
      <path d={d} fill="none" stroke={color} strokeWidth="1.5" strokeLinecap="round"/>
      <circle cx={pts[pts.length-1][0]} cy={pts[pts.length-1][1]} r="2.5" fill={color}/>
    </svg>
  );
}

/* ---------- NumInput ----------
 *
 * Lets the user type freely (no clamping per keystroke) and commits the value
 * on Blur / Enter, when it's clamped to [min, max]. The arrow buttons commit
 * immediately because there's no half-typed state involved.
 *
 * When `disabled` is true the input is readonly and visually dimmed.
 */
function NumInput({ value, onChange, min = 0, max = 9999, step = 1, width, disabled = false }) {
  const [draft, setDraft] = useState(String(value));

  // Sync external value changes into the draft when the user isn't editing.
  useEffect(() => { setDraft(String(value)); }, [value]);

  const commit = (raw) => {
    const n = Number(raw);
    if (Number.isNaN(n)) { setDraft(String(value)); return; }
    const clamped = Math.max(min, Math.min(max, Math.round(n)));
    setDraft(String(clamped));
    if (clamped !== value) onChange(clamped);
  };

  const bump = (delta) => {
    if (disabled) return;
    const n = Number(draft);
    const base = Number.isNaN(n) ? value : n;
    const clamped = Math.max(min, Math.min(max, base + delta));
    setDraft(String(clamped));
    onChange(clamped);
  };

  return (
    <span className={`num${disabled ? " disabled" : ""}`} style={width ? { width } : null}>
      <input
        type="number"
        value={draft}
        min={min} max={max} step={step}
        readOnly={disabled}
        disabled={disabled}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={(e) => commit(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") { commit(e.currentTarget.value); e.currentTarget.blur(); }
          else if (e.key === "Escape") { setDraft(String(value)); e.currentTarget.blur(); }
        }}
      />
      <span className="num-steppers">
        <button className="num-step" onClick={() => bump(step)} disabled={disabled} aria-label="up">▲</button>
        <button className="num-step" onClick={() => bump(-step)} disabled={disabled} aria-label="down">▼</button>
      </span>
    </span>
  );
}

/* ---------- Level bars (clickable) ---------- */
function LevelBars({ value, onChange, color, max = 4 }) {
  const bars = [];
  for (let i = 1; i <= max; i++) bars.push(i);
  const clamped = Math.min(value, max);
  return (
    <div className="level">
      <div className="level-bars">
        {bars.map((i) => (
          <div
            key={i}
            className={`level-bar ${i <= clamped ? "active" : ""}`}
            style={i <= clamped && color ? { background: color } : null}
            onClick={() => onChange(i === clamped ? i - 1 : i)}
            title={`Level ${i}`}
          />
        ))}
      </div>
      <span className="level-num">L{clamped}</span>
    </div>
  );
}

/* ---------- App ---------- */
function App() {
  // Theme preference: "system" follows the OS via prefers-color-scheme; "dark"
  // and "light" are explicit user overrides. The toggle cycles system→light→dark.
  const [themePref, setThemePref] = useState(() => {
    const s = localStorage.getItem("reon.theme");
    return (s === "dark" || s === "light") ? s : "system";
  });
  const [osDark, setOsDark] = useState(() =>
    window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
  const [showSparkline, setShowSparkline] = useState(() => localStorage.getItem("reon.spark") !== "0");

  // Subscribe to OS theme changes — keeps "system" mode live.
  useEffect(() => {
    if (!window.matchMedia) return;
    const mql = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = (e) => setOsDark(e.matches);
    mql.addEventListener ? mql.addEventListener("change", onChange) : mql.addListener(onChange);
    return () => {
      mql.removeEventListener ? mql.removeEventListener("change", onChange) : mql.removeListener(onChange);
    };
  }, []);

  // Effective theme = user override, or OS preference if on "system".
  const effectiveTheme = themePref === "system" ? (osDark ? "dark" : "light") : themePref;
  useEffect(() => {
    document.documentElement.dataset.theme = effectiveTheme;
    document.documentElement.style.colorScheme = effectiveTheme;
  }, [effectiveTheme]);

  // Persist preference. "system" means "no override" → clear the key.
  useEffect(() => {
    if (themePref === "system") localStorage.removeItem("reon.theme");
    else localStorage.setItem("reon.theme", themePref);
  }, [themePref]);

  const cycleTheme = () => setThemePref((p) =>
    p === "system" ? "light" : p === "light" ? "dark" : "system");

  useEffect(() => { localStorage.setItem("reon.spark", showSparkline ? "1" : "0"); }, [showSparkline]);

  // Left column has three collapsible panels (Configuration, MQTT, Log)
  // that are mutually exclusive — only one open at a time. null = all collapsed.
  const [openPanel, setOpenPanel] = useState(() => {
    const v = localStorage.getItem("reon.openPanel");
    return v === "config" || v === "mqtt" || v === "log" ? v : null;
  });
  useEffect(() => {
    if (openPanel) localStorage.setItem("reon.openPanel", openPanel);
    else localStorage.removeItem("reon.openPanel");
  }, [openPanel]);
  const togglePanel = (which) => setOpenPanel((p) => (p === which ? null : which));

  const hosted = !!(window.reonBridge && window.reonBridge.isHosted);
  const send = useCallback((cmd, payload) => {
    if (hosted) reonBridge.send(cmd, payload);
  }, [hosted]);

  // Connection state
  const [connState, setConnState] = useState(hosted ? "disconnected" : "connected");
  const [macAddr, setMacAddr] = useState(hosted ? "—" : "F1:15:62:AC:D9:98");
  const [model, setModel] = useState(null);
  // Per-device level caps (cool max, heat max). Conservative default; updated
  // from the backend's conn.state event once a device is connected.
  const [caps, setCaps] = useState({ coolMax: 3, heatMax: 3 });
  const [fw, setFw] = useState(null);
  const [battery, setBattery] = useState(null);
  const [connectedAt, setConnectedAt] = useState(null);
  const [uptime, setUptime] = useState(0);

  // OSC server
  const [oscPort, setOscPort] = useState(9302);
  const [oscRunning, setOscRunning] = useState(false);
  const [oscPackets, setOscPackets] = useState(0);
  const [addresses, setAddresses] = useState({
    PFHotHigh: "/PFHotHigh",
    water: "/ChairOSC/v1/water",
    cold: "/ChairOSC/v1/cold",
    heat: "/ChairOSC/v1/heat",
    wind: "/ChairOSC/v1/wind",
  });

  // Manual control
  const [manualOverride, setManualOverride] = useState(false);
  const [manualMode, setManualMode] = useState("Cool");
  const [manualLevel, setManualLevel] = useState(3);

  // Presets
  const [heatLevel, setHeatLevel] = useState(3);
  const [coldLevel, setColdLevel] = useState(3);

  // Options
  const [startMin, setStartMin] = useState(false);
  const [autoConn, setAutoConn] = useState(true);
  const [pfHook, setPfHook] = useState(false);
  const [pfState, setPfState] = useState({ running: false, hex: "—", decoded: "Off", mode: "Off", level: 0, backend: "None" });

  // MQTT publisher — pushes state to Home Assistant so it can mirror the Reon.
  // The password field is write-only: empty in the UI means "don't change",
  // since the host never echoes it back.
  const [mqttCfg, setMqttCfg] = useState({
    enabled: false, host: "", port: 1883, username: "", password: "",
    baseTopic: "reonosc", discoveryPrefix: "homeassistant",
  });
  const [mqttState, setMqttState] = useState({ state: "Disabled", error: null });

  // Incoming OSC inputs
  const [oscIn, setOscIn] = useState({ PFHotHigh: 0, water: 0, cold: 0.0, heat: 0.0, wind: 0.0 });

  // Live derived state pushed by backend
  const [currentMode, setCurrentMode] = useState("Stop");
  const [currentLevel, setCurrentLevel] = useState(0);
  const [currentSource, setCurrentSource] = useState("OSC");
  const [currentReason, setCurrentReason] = useState("Idle");
  const [temps, setTemps] = useState({ plate: 0, sink: 0, board: 0, ambient: 0 });
  const [hasTemps, setHasTemps] = useState(false);

  // Log
  const [log, setLog] = useState([]);
  const logBodyRef = useRef(null);

  const appendLog = useCallback((k, msg) => {
    setLog((prev) => {
      const next = [...prev, { t: fmtTime(), k, msg }];
      return next.slice(-200);
    });
  }, []);

  // Auto-scroll log
  useEffect(() => {
    const el = logBodyRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [log]);

  // Sparkline data — only meaningful when temps come in
  const [plateHistory, setPlateHistory] = useState([]);
  useEffect(() => {
    if (!hasTemps || temps.plate == null) return;
    setPlateHistory((h) => [...h.slice(-79), temps.plate]);
  }, [temps.plate, hasTemps]);

  // Uptime tick once per second while connected
  useEffect(() => {
    if (!connectedAt) { setUptime(0); return; }
    const tick = () => setUptime(Math.floor((Date.now() - connectedAt) / 1000));
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [connectedAt]);

  /* ---------- Bridge subscriptions (only in hosted mode) ---------- */
  useEffect(() => {
    if (!hosted) return;

    const unsubs = [
      reonBridge.on("conn.state", (p) => {
        if (!p) return;
        setConnState(p.state);
        if (p.mac) setMacAddr(p.mac);
        if (p.state !== "connected") {
          setMacAddr("—");
          setConnectedAt(null);
        } else if (!connectedAt) {
          setConnectedAt(Date.now());
        }
        if (p.model !== undefined) setModel(p.model);
        if (p.caps) setCaps({
          coolMax: p.caps.coolMax ?? 3,
          heatMax: p.caps.heatMax ?? 3,
        });
        if (p.fw !== undefined) setFw(p.fw);
        if (p.battery !== undefined) setBattery(p.battery);
      }),

      reonBridge.on("osc.state", (p) => {
        if (!p) return;
        setOscRunning(!!p.running);
        if (p.port) setOscPort(p.port);
        if (typeof p.packets === "number") setOscPackets(p.packets);
      }),

      reonBridge.on("osc.input", (p) => {
        if (!p) return;
        setOscIn({
          PFHotHigh: p.PFHotHigh ?? 0,
          water: p.water ?? 0,
          cold: p.cold ?? 0,
          heat: p.heat ?? 0,
          wind: p.wind ?? 0,
        });
      }),

      reonBridge.on("state.current", (p) => {
        if (!p) return;
        setCurrentMode(p.mode);
        setCurrentLevel(p.level);
        if (p.source) setCurrentSource(p.source);
        if (p.reason) setCurrentReason(p.reason);
      }),

      reonBridge.on("telemetry", (p) => {
        if (!p) return;
        // Null fields mean "sensor unwired / sentinel 0xffff" — preserve as null
        // so the UI shows a dash instead of a misleading 655.35°C.
        setTemps({
          plate: p.plate ?? null,
          sink: p.sink ?? null,
          board: p.board ?? null,
          ambient: p.ambient ?? null,
        });
        setHasTemps(true);
      }),

      reonBridge.on("log.line", (p) => {
        if (!p) return;
        setLog((prev) => {
          const next = [...prev, { t: p.t || fmtTime(), k: p.kind || "info", msg: p.msg || "" }];
          return next.slice(-200);
        });
      }),

      reonBridge.on("settings", (p) => {
        if (!p) return;
        if (p.oscPort) setOscPort(p.oscPort);
        if (p.addresses) setAddresses((a) => ({ ...a, ...p.addresses }));
        if (typeof p.heatTouchLevel === "number") setHeatLevel(p.heatTouchLevel);
        if (typeof p.coldWaterLevel === "number") setColdLevel(p.coldWaterLevel);
        if (typeof p.startMinimised === "boolean") setStartMin(p.startMinimised);
        if (typeof p.autoConnectOnStart === "boolean") setAutoConn(p.autoConnectOnStart);
        if (typeof p.enablePfSignalHook === "boolean") setPfHook(p.enablePfSignalHook);
        if (p.mqtt) setMqttCfg((m) => ({
          ...m,
          enabled: !!p.mqtt.enabled,
          host: p.mqtt.host ?? m.host,
          port: p.mqtt.port ?? m.port,
          username: p.mqtt.username ?? m.username,
          baseTopic: p.mqtt.baseTopic ?? m.baseTopic,
          discoveryPrefix: p.mqtt.discoveryPrefix ?? m.discoveryPrefix,
          // password intentionally NOT hydrated — host never echoes it.
        }));
      }),

      reonBridge.on("mqtt.state", (p) => {
        if (!p) return;
        setMqttState({ state: p.state ?? "Disabled", error: p.error ?? null });
      }),

      reonBridge.on("pf.signal", (p) => {
        if (!p) return;
        setPfState({
          running: !!p.running,
          hex: p.hex ?? "—",
          decoded: p.decoded ?? "Off",
          mode: p.mode ?? "Off",
          level: p.level ?? 0,
          backend: p.backend ?? "None",
        });
      }),
    ];

    // All listeners are wired now — tell the host we're ready to receive
    // initial state. Sending earlier (from reon-bridge.js's IIFE) would race
    // against this useEffect and the first round of pushes would arrive
    // before the on(...) handlers exist.
    reonBridge.send("ui.ready", null);

    return () => unsubs.forEach((u) => u());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hosted]);

  /* ---------- Local-only derivation (when no bridge: simulate) ---------- */
  useEffect(() => {
    if (hosted) return;
    let mode = "Stop", level = 0;
    if (manualOverride) {
      mode = manualMode;
      level = manualMode === "Stop" ? 0 : manualLevel;
    } else {
      if (oscIn.PFHotHigh >= 1) { mode = "Heat"; level = heatLevel; }
      else if (oscIn.water >= 1) { mode = "Cool"; level = coldLevel; }
      else if (oscIn.cold > 0.05) { mode = "Cool"; level = Math.max(1, Math.round(oscIn.cold * LEVEL_MAX)); }
      else if (oscIn.heat > 0.05) { mode = "Heat"; level = Math.max(1, Math.round(oscIn.heat * LEVEL_MAX)); }
      else { mode = "Stop"; level = 0; }
    }
    if (mode !== currentMode || level !== currentLevel) {
      setCurrentMode(mode);
      setCurrentLevel(level);
      if (connState === "connected") {
        const msg = mode === "Stop" ? "→ Stop" : `→ ${mode} L${level}`;
        appendLog(mode === "Stop" ? "stop" : mode.toLowerCase(), msg);
      }
    }
  }, [hosted, manualOverride, manualMode, manualLevel, oscIn, heatLevel, coldLevel, connState, currentMode, currentLevel, appendLog]);

  /* ---------- Sim only: drive temperatures, occasional OSC inputs ---------- */
  useEffect(() => {
    if (hosted) return;
    const id = setInterval(() => {
      setTemps((t) => {
        const target = currentMode === "Cool"
          ? 30.69 - currentLevel * 2.2
          : currentMode === "Heat"
            ? 30.69 + currentLevel * 3.6
            : 30.69 + (Math.random() - 0.5) * 0.6;
        const lerp = (a, b, k) => a + (b - a) * k;
        const drift = () => (Math.random() - 0.5) * 0.18;
        const k = 0.18;
        const plate = lerp(t.plate || 31, target, k) + drift();
        const sink = lerp(t.sink || 31, target * 0.95 + 1.6, k * 0.85) + drift();
        const board = lerp(t.board || 31, 31 + currentLevel * 0.4, 0.1) + drift() * 0.3;
        const ambient = lerp(t.ambient || 30, 30.69, 0.05) + drift() * 0.4;
        return { plate, sink, board, ambient };
      });
      setHasTemps(true);
    }, 700);
    return () => clearInterval(id);
  }, [hosted, currentMode, currentLevel]);

  useEffect(() => {
    if (hosted || !oscRunning || connState !== "connected") return;
    const id = setInterval(() => {
      if (Math.random() < 0.08) {
        const which = ["PFHotHigh", "water", "cold", "heat", "wind"][Math.floor(Math.random() * 5)];
        setOscIn((o) => {
          const next = { ...o };
          if (which === "PFHotHigh" || which === "water") next[which] = next[which] ? 0 : 1;
          else next[which] = Math.random() < 0.4 ? 0 : Math.round(Math.random() * 100) / 100;
          return next;
        });
      }
    }, 2400);
    return () => clearInterval(id);
  }, [hosted, oscRunning, connState]);

  /* ---------- handlers — send to bridge in hosted mode, fall back to local state in sim ---------- */
  const doConnect = () => {
    if (hosted) { send("connect", null); return; }
    setConnState("connecting");
    appendLog("info", "Connecting to " + macAddr + " …");
    setTimeout(() => { setConnState("connected"); setConnectedAt(Date.now()); appendLog("ok", "Connected and authed."); }, 900);
  };
  const doDisconnect = () => {
    if (hosted) { send("disconnect", null); return; }
    setConnState("disconnected"); setConnectedAt(null); appendLog("info", "Disconnected.");
  };
  const doPair = () => {
    if (hosted) { send("pair", null); return; }
    setConnState("pairing"); appendLog("info", "Waiting for device in pair mode …");
    setTimeout(() => { setConnState("connected"); setConnectedAt(Date.now()); appendLog("ok", "Paired. Connected and authed."); }, 1600);
  };
  const toggleOsc = () => {
    if (hosted) { send(oscRunning ? "osc.stop" : "osc.start", { port: oscPort }); return; }
    if (oscRunning) { setOscRunning(false); appendLog("info", "OSC server stopped."); }
    else { setOscRunning(true); appendLog("info", `OSC listening on UDP ${oscPort}`); }
  };

  const onPortChange = (v) => {
    // The port input is locked while OSC is running, so we only see commits
    // in the stopped state. Just remember the user's choice; the next Start
    // click sends it to the bridge.
    setOscPort(v);
    if (hosted && oscRunning) send("osc.start", { port: v });
  };

  const onAddressChange = (key, value) => {
    setAddresses((a) => ({ ...a, [key]: value }));
    if (hosted) send("osc.setAddress", { key, address: value });
  };

  const onOverrideChange = (v) => {
    setManualOverride(v);
    if (hosted) send("manual.toggle", { enabled: v });
  };
  const onManualModeChange = (m) => {
    setManualMode(m);
    if (hosted && manualOverride) send("manual.set", { mode: m, level: m === "Stop" ? 0 : manualLevel });
  };
  const onManualLevelChange = (l) => {
    setManualLevel(l);
    if (hosted && manualOverride && manualMode !== "Stop") send("manual.set", { mode: manualMode, level: l });
  };
  const onHeatLevelChange = (v) => { setHeatLevel(v); if (hosted) send("preset.heat", { level: v }); };
  const onColdLevelChange = (v) => { setColdLevel(v); if (hosted) send("preset.cold", { level: v }); };
  const onStartMinChange = (v) => { setStartMin(v); if (hosted) send("options.set", { startMinimised: v }); };
  const onAutoConnChange = (v) => { setAutoConn(v); if (hosted) send("options.set", { autoConnect: v }); };
  const onPfHookChange   = (v) => { setPfHook(v); if (hosted) send("pfHook.toggle", { enabled: v }); };
  const onClearLog = () => { setLog([]); if (hosted) send("log.clear", null); };

  // MQTT: commit changes to the host. The password field stays in local state
  // until the user types something — sending empty means "leave existing".
  const commitMqtt = (patch) => {
    setMqttCfg((m) => {
      const next = { ...m, ...patch };
      if (hosted) {
        const out = { ...next };
        // Drop password from the payload unless the user actually typed one;
        // otherwise an empty Save would clobber the saved secret.
        if (!out.password) delete out.password;
        send("mqtt.set", out);
      }
      return next;
    });
  };

  /* ---------- derived ----- */
  const accent = currentMode === "Cool" ? "#4ab8ff" : currentMode === "Heat" ? "#ff7a3d" : "#7c8694";
  useEffect(() => {
    document.documentElement.style.setProperty("--accent", accent);
  }, [accent]);

  const connPillProps = {
    connected: { cls: "connected", dot: "", label: "Connected" },
    connecting: { cls: "", dot: "", label: "Connecting…" },
    pairing: { cls: "", dot: "", label: "Pairing…" },
    disconnected: { cls: "disconnected", dot: "off", label: "Disconnected" },
  }[connState];

  /* ----- render ----- */
  return (
    /* The CSS sets grid-template-rows: 38px 1fr to make room for a custom
       title bar we don't render. Collapse that first row so <main> gets the
       full height. */
    <div className="app" style={{gridTemplateRows: "1fr"}}>
      <main className="main">
        {/* Controls panel — left column. Two mutually-exclusive collapsibles
            (Configuration / Log) so only one is open at a time. */}
        <section className="controls-panel">
          {/* Configuration — collapsible: OSC, addresses, preset levels, options */}
          <div className="card">
            <div className="card-header card-toggle" onClick={() => togglePanel("config")}>
              <div className="card-title"><span className="dot"/>Configuration</div>
              <div style={{display: "flex", alignItems: "center", gap: 10}}>
                <span className={`pill ${oscRunning ? "connected" : "disconnected"}`}>
                  <span className={`pulse ${oscRunning ? "" : "off"}`} style={oscRunning ? {background: "var(--accent)", boxShadow: `0 0 0 0 ${accent}80`} : null}/>
                  {oscRunning ? `OSC ${oscPort} · ${oscPackets} pkt` : "OSC off"}
                </span>
                <Icon name={openPanel === "config" ? "chevU" : "chevD"} size={14}/>
              </div>
            </div>
            {openPanel === "config" && (
              <div className="card-body">
                {/* OSC server */}
                <div className="config-section-title">OSC server</div>
                <div className="row" style={{gap: 14}}>
                  <span className="field-label" style={{width: 70}}>UDP port</span>
                  <NumInput
                    value={oscPort}
                    onChange={onPortChange}
                    min={1024}
                    max={65535}
                    step={1}
                    width={96}
                    disabled={oscRunning}
                  />
                  <button className={`btn ${oscRunning ? "danger" : "primary"}`} onClick={toggleOsc}>
                    {oscRunning ? <><Icon name="stop"/> Stop</> : <><Icon name="play"/> Start</>}
                  </button>
                </div>

                <div className="config-divider"/>

                <div className="config-section-title small">
                  OSC addresses <span style={{color: "var(--text-muted)", fontWeight: 400, textTransform: "none", letterSpacing: 0}}>— edit to match your sender</span>
                </div>

                {/* PFHotHigh is intentionally NOT exposed here — its address is
                    fixed for backward compatibility with the Pebble Feel sender.
                    Wind doesn't drive the Reon (no fan) but is forwarded to
                    MQTT so HA automations can react. */}
                {[
                  { key: "water",     label: "water",     type: "bool" },
                  { key: "cold",      label: "cold",      type: "float" },
                  { key: "heat",      label: "heat",      type: "float" },
                  { key: "wind",      label: "wind",      type: "float" },
                ].map((row) => (
                  <div className="osc-row" key={row.key}>
                    <span className="osc-label">{row.label} <span className={`tag ${row.type}`}>{row.type}</span></span>
                    <input className="input mono" value={addresses[row.key]} onChange={(e) => onAddressChange(row.key, e.target.value)}/>
                  </div>
                ))}

                <div className="config-divider"/>

                {/* Preset levels */}
                <div className="config-section-title">Preset levels for OSC triggers</div>
                <div className="row between">
                  <span className="osc-label">
                    Heat Touch <span className="tag" style={{color: "var(--heat-2)"}}>when PFHotHigh = 1</span>
                  </span>
                  <LevelBars value={heatLevel} onChange={onHeatLevelChange} color="#ff7a3d" max={caps.heatMax}/>
                </div>
                <div className="row between">
                  <span className="osc-label">
                    Cold Water <span className="tag" style={{color: "var(--cool)"}}>when water = 1</span>
                  </span>
                  <LevelBars value={coldLevel} onChange={onColdLevelChange} color="#4ab8ff" max={caps.coolMax}/>
                </div>

                <div className="config-divider"/>

                {/* Options */}
                <div className="config-section-title">
                  Options
                  <span style={{flex: 1}}/>
                  <button
                    className="btn ghost"
                    onClick={(e) => { e.stopPropagation(); cycleTheme(); }}
                    title={`Theme: ${themePref === "system" ? "follow OS" : themePref} — click to cycle`}
                  >
                    <Icon name={effectiveTheme === "dark" ? "sun" : "moon"} size={12}/>
                    <span style={{marginLeft: 6}}>
                      {themePref === "system" ? "Auto" : themePref === "dark" ? "Dark" : "Light"}
                    </span>
                  </button>
                  <button className="btn ghost" onClick={(e) => { e.stopPropagation(); setShowSparkline((v) => !v); }} title="Toggle sparkline">
                    {showSparkline ? "Hide sparkline" : "Show sparkline"}
                  </button>
                </div>
                <div style={{display: "flex", flexDirection: "row", gap: 24, flexWrap: "wrap"}}>
                  <label className="check">
                    <input type="checkbox" checked={startMin} onChange={(e) => onStartMinChange(e.target.checked)}/>
                    <span className="check-box"/>
                    Start minimised to tray
                  </label>
                  <label className="check">
                    <input type="checkbox" checked={autoConn} onChange={(e) => onAutoConnChange(e.target.checked)}/>
                    <span className="check-box"/>
                    Auto-connect to Reon on start
                  </label>
                  <div style={{display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap"}}>
                    <label className="check" title="Reads the Pebble Feel signal pixel from the SteamVR compositor mirror texture. Experimental.">
                      <input type="checkbox" checked={pfHook} onChange={(e) => onPfHookChange(e.target.checked)}/>
                      <span className="check-box"/>
                      Pebble Feel signal hook
                    </label>
                    {pfHook && pfState.running && (
                      <>
                        <span
                          style={{
                            display: "inline-block",
                            width: 16,
                            height: 16,
                            background: pfState.hex || "#000",
                            border: "1px solid var(--border)",
                            borderRadius: 3,
                          }}
                          title={pfState.hex}
                        />
                        <span className="tag mono" style={{color: "var(--text-dim)"}}>
                          {pfState.hex}
                        </span>
                        <span
                          className="tag"
                          style={{
                            color: pfState.mode === "Hot"  ? "var(--heat-2)"
                                : pfState.mode === "Cool" ? "var(--cool-2)"
                                : "var(--text-muted)",
                            fontWeight: 600,
                          }}
                        >
                          → {pfState.decoded}
                        </span>
                        <span
                          className="tag mono"
                          style={{color: "var(--text-muted)", fontSize: 10}}
                          title="Active capture backend"
                        >
                          {pfState.backend === "OpenVrMirror" ? "VR" :
                           pfState.backend === "DesktopDuplication" ? "Desktop" :
                           pfState.backend}
                        </span>
                        <button
                          className="btn ghost"
                          style={{padding: "3px 10px", fontSize: 11.5}}
                          onClick={() => send("pfSignal.capture", null)}
                          title="Save a full-frame screenshot with sample-position overlays to C:\\temp\\reon-pfcapture-*.png"
                        >
                          Capture
                        </button>
                      </>
                    )}
                    {pfHook && !pfState.running && (
                      <span className="tag mono" style={{color: "var(--text-muted)"}}>
                        not running
                      </span>
                    )}
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* MQTT publish — collapsible, mutually exclusive with siblings.
              Header shows live status; body holds the broker config. */}
          <div className="card">
            <div className="card-header card-toggle" onClick={() => togglePanel("mqtt")}>
              <div className="card-title"><span className="dot"/>MQTT publish</div>
              <div style={{display: "flex", alignItems: "center", gap: 10}}>
                {mqttCfg.enabled ? (
                  <span
                    className={`pill ${mqttState.state === "Connected" ? "connected" : "disconnected"}`}
                    title={mqttState.error || ""}
                  >
                    <span className={`pulse ${mqttState.state === "Connected" ? "" : "off"}`}/>
                    {mqttState.state === "Connected" ? "live"
                      : mqttState.state === "Connecting" ? "connecting…"
                      : mqttState.state === "Error" ? "error"
                      : mqttState.state === "Unavailable" ? "unavailable"
                      : "down"}
                  </span>
                ) : (
                  <span className="pill disconnected"><span className="pulse off"/>off</span>
                )}
                <Icon name={openPanel === "mqtt" ? "chevU" : "chevD"} size={14}/>
              </div>
            </div>
            {openPanel === "mqtt" && (
              <div className="card-body">
                <div className="row between">
                  <span className="osc-label" style={{color: "var(--text-dim)"}}>
                    Push state to a broker (Home Assistant via auto-discovery)
                  </span>
                  <label className="toggle" onClick={(e) => e.stopPropagation()}>
                    <input
                      type="checkbox"
                      checked={mqttCfg.enabled}
                      onChange={(e) => commitMqtt({ enabled: e.target.checked })}
                    />
                    <span className="toggle-track"/>
                    <span className={`toggle-label ${mqttCfg.enabled ? "strong" : ""}`}>Enabled</span>
                  </label>
                </div>
                <div style={{opacity: mqttCfg.enabled ? 1 : 0.55, transition: "opacity 0.15s"}}>
                  <div className="osc-row">
                    <span className="osc-label">Broker host</span>
                    <input
                      className="input mono"
                      placeholder="homeassistant.local"
                      value={mqttCfg.host}
                      onChange={(e) => setMqttCfg((m) => ({...m, host: e.target.value}))}
                      onBlur={(e) => commitMqtt({ host: e.target.value })}
                    />
                  </div>
                  <div className="osc-row">
                    <span className="osc-label">Port</span>
                    <NumInput
                      value={mqttCfg.port}
                      onChange={(v) => commitMqtt({ port: v })}
                      min={1} max={65535} step={1} width={96}
                    />
                  </div>
                  <div className="osc-row">
                    <span className="osc-label">Username</span>
                    <input
                      className="input mono"
                      value={mqttCfg.username}
                      onChange={(e) => setMqttCfg((m) => ({...m, username: e.target.value}))}
                      onBlur={(e) => commitMqtt({ username: e.target.value })}
                    />
                  </div>
                  <div className="osc-row">
                    <span className="osc-label">Password</span>
                    <input
                      className="input mono"
                      type="password"
                      placeholder="(unchanged)"
                      value={mqttCfg.password}
                      onChange={(e) => setMqttCfg((m) => ({...m, password: e.target.value}))}
                      onBlur={(e) => {
                        if (e.target.value) commitMqtt({ password: e.target.value });
                      }}
                    />
                  </div>
                  <div className="osc-row">
                    <span className="osc-label">Base topic</span>
                    <input
                      className="input mono"
                      value={mqttCfg.baseTopic}
                      onChange={(e) => setMqttCfg((m) => ({...m, baseTopic: e.target.value}))}
                      onBlur={(e) => commitMqtt({ baseTopic: e.target.value })}
                    />
                  </div>
                  <div className="osc-row">
                    <span className="osc-label">HA discovery prefix <span className="tag" style={{color: "var(--text-muted)"}}>blank to disable</span></span>
                    <input
                      className="input mono"
                      placeholder="homeassistant"
                      value={mqttCfg.discoveryPrefix}
                      onChange={(e) => setMqttCfg((m) => ({...m, discoveryPrefix: e.target.value}))}
                      onBlur={(e) => commitMqtt({ discoveryPrefix: e.target.value })}
                    />
                  </div>
                  {mqttState.error && (
                    <div className="osc-row">
                      <span className="osc-label" style={{color: "var(--danger)"}}>Last error</span>
                      <span className="tag mono" style={{color: "var(--danger)", borderColor: "color-mix(in oklab, var(--danger) 40%, var(--border))"}}>
                        {mqttState.error}
                      </span>
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>

          {/* Log — collapsible, mutually exclusive with siblings above */}
          <div className="card">
            <div className="card-header card-toggle" onClick={() => togglePanel("log")}>
              <div className="card-title"><span className="dot"/>Log</div>
              <div style={{display: "flex", alignItems: "center", gap: 10}}>
                <span className="tag mono" style={{color: "var(--text-muted)"}}>
                  {log.length} {log.length === 1 ? "line" : "lines"}
                </span>
                {openPanel === "log" && (
                  <button
                    className="btn ghost"
                    style={{padding: "4px 8px"}}
                    onClick={(e) => { e.stopPropagation(); onClearLog(); }}
                    title="Clear log"
                  >
                    <Icon name="trash" size={12}/>
                  </button>
                )}
                <Icon name={openPanel === "log" ? "chevU" : "chevD"} size={14}/>
              </div>
            </div>
            {openPanel === "log" && (
              <div className="log-body" ref={logBodyRef}>
                {log.map((l, i) => (
                  <div className={`log-line ${l.k}`} key={i}>
                    <span className="log-time">{l.t}</span>
                    <span className="log-icon">
                      {l.k === "ok" ? "✓" : l.k === "err" ? "✕" : l.k === "cool" ? "❄" : l.k === "heat" ? "🔥" : l.k === "stop" ? "■" : "›"}
                    </span>
                    <span className="log-msg">{typeof l.msg === "string" ? l.msg : l.msg}</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        </section>

        {/* Device panel — right column. Holds the connection + manual cards
            on top (relocated from the left column), then the device hero. */}
        <section className="device-panel">
          {/* Reon connection */}
          <div className="card">
            <div className="card-header">
              <div className="card-title"><span className="dot"/>Reon connection</div>
              <span className={`pill ${connPillProps.cls}`}>
                <span className={`pulse ${connPillProps.dot}`}/>
                {connPillProps.label}
              </span>
            </div>
            <div className="card-body">
              <div className="row between" style={{flexWrap: "wrap", gap: 10}}>
                <div style={{display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap"}}>
                  <span className="mac-addr">{connState === "connected" ? macAddr : "—"}</span>
                  {fw && <span className="tag mono">FW {fw}</span>}
                  {battery != null && <span className="tag mono">⛁ {battery}%</span>}
                </div>
                <div className="actions">
                  <button className="btn" onClick={doConnect}
                    disabled={connState === "connected" || connState === "connecting"}>
                    <Icon name="link"/> Connect
                  </button>
                  <button className="btn" onClick={doDisconnect} disabled={connState !== "connected"}>
                    <Icon name="unlink"/> Disconnect
                  </button>
                  <button className="btn" onClick={doPair} disabled={connState === "pairing"}>
                    <Icon name="bluetooth"/> Pair…
                  </button>
                </div>
              </div>
            </div>
          </div>

          {/* Manual control */}
          <div className="card">
            <div className="card-header">
              <div className="card-title"><span className="dot"/>Manual control</div>
              <label className="toggle">
                <input type="checkbox" checked={manualOverride} onChange={(e) => onOverrideChange(e.target.checked)}/>
                <span className="toggle-track"/>
                <span className={`toggle-label ${manualOverride ? "strong" : ""}`}>Override OSC</span>
              </label>
            </div>
            <div className="card-body" style={{opacity: manualOverride ? 1 : 0.5, pointerEvents: manualOverride ? "auto" : "none", transition: "opacity 0.2s"}}>
              <div className="row" style={{gap: 16, flexWrap: "wrap"}}>
                <span className="field-label" style={{width: 50}}>Mode</span>
                <div className="seg">
                  {MODES.map((m) => (
                    <button key={m} className={`seg-btn ${manualMode === m ? `active ${m.toLowerCase()}` : ""}`}
                      onClick={() => onManualModeChange(m)}>{m}</button>
                  ))}
                </div>
                <span className="field-label" style={{width: 50, marginLeft: 12}}>Level</span>
                <LevelBars
                  value={manualLevel}
                  onChange={onManualLevelChange}
                  color={manualMode === "Cool" ? "#4ab8ff" : manualMode === "Heat" ? "#ff7a3d" : null}
                  max={manualMode === "Heat" ? caps.heatMax : caps.coolMax}
                />
              </div>
            </div>
          </div>

          <div className="device-stage">
            <ReonDevice
              mode={currentMode}
              level={currentLevel}
              plate={temps.plate ?? 31.37}
              ambient={temps.ambient ?? 30.69}
            />
          </div>

          <div className="device-info">
            <div className="mode-display">
              <span className={`mode-text ${currentMode.toLowerCase()}`}>
                {currentMode}{currentMode !== "Stop" ? ` L${currentLevel}` : ""}
              </span>
              <span className="mode-meta">
                <div>
                  TRIGGER · <strong>{manualOverride ? "Manual" : (currentReason || currentSource || "Idle")}</strong>
                </div>
                <div style={{marginTop: 2}}>UPTIME · <strong>{fmtUptime(uptime)}</strong></div>
              </span>
            </div>

            <div className="telemetry">
              {[
                { l: "Plate · skin", v: temps.plate },
                { l: "Sink",         v: temps.sink  },
                { l: "Board",        v: temps.board },
                { l: "Ambient",      v: temps.ambient },
              ].map((c) => {
                const has = hasTemps && c.v != null;
                return (
                  <div className="tel-cell" key={c.l}>
                    <div className="tel-label">{c.l}</div>
                    <div className="tel-value">
                      {has ? c.v.toFixed(2) : "—"}
                      {has && <span className="tel-unit">°C</span>}
                    </div>
                  </div>
                );
              })}
            </div>

            {showSparkline && (
              <Sparkline data={plateHistory} color={accent}/>
            )}

            <div className="osc-readout">
              {[
                { k: "PFHotHigh", v: oscIn.PFHotHigh, fmt: (v) => v },
                { k: "water",     v: oscIn.water,     fmt: (v) => v },
                { k: "cold",      v: oscIn.cold,      fmt: (v) => Number(v).toFixed(2) },
                { k: "heat",      v: oscIn.heat,      fmt: (v) => Number(v).toFixed(2) },
                { k: "wind",      v: oscIn.wind,      fmt: (v) => Number(v).toFixed(2) },
              ].map((c) => (
                <div className="osc-readout-cell" key={c.k}>
                  <div className="osc-readout-label">{c.k}</div>
                  <div className={`osc-readout-value ${Number(c.v) > 0 ? "active" : ""}`}>{c.fmt(c.v)}</div>
                </div>
              ))}
            </div>
          </div>
        </section>
      </main>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App/>);
