/* global React, ReactDOM, BodyMap, Spark, tempColor, reonColor, reonBridge */
// ReonOSC v3 UI, wired to the WebView2 bridge.
// Original design + layout from design_handoff_reonosc/design_reference/app-v3.jsx;
// state and simulation stripped out — the backend now owns device state,
// resolver output, and OSC inputs, and this file is pure render + commands.

const { useState, useEffect, useRef, useMemo, useCallback } = React;

const pad = (n) => String(n).padStart(2, "0");
const fmtTime = (d = new Date()) => `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const HARD_MAX = 70;

const bridge = window.reonBridge;

// ---- helpers ---------------------------------------------------------------

function useBridgeEvent(event, handler) {
  useEffect(() => bridge.on(event, handler), [event]); // eslint-disable-line react-hooks/exhaustive-deps
}

/// Collapse the backend's (source, reason) pair into one chip label the UI
/// shows. Mirrors the Reason-field catalog described in the handoff README.
function reonSourceLabel(z) {
  if (!z) return "Idle";
  if (z.source === "Manual") return "Manual";
  if (z.source === "PF") return "PFSignal";
  const r = z.reason || "";
  if (r.startsWith("OSC:")) {
    const tail = r.slice(4);
    if (tail === "PFHotHigh") return "PFHotHigh";
    if (tail === "water") return "Water";
    if (tail === "cold") return "Cold";
    if (tail === "heat") return "Heat";
  }
  if (z.mode === "Stop") return "Idle";
  return r || z.source || "OSC";
}

// ---- small reused building blocks (verbatim from the design) ---------------

const Icon = ({ name, size = 13 }) => {
  const p = {
    link: <path d="M9 7h-3a4 4 0 0 0 0 8h3M15 7h3a4 4 0 0 1 0 8h-3M8 11h8" />,
    unlink: <><path d="M9 7h-3a4 4 0 0 0 0 8h3M15 7h3a4 4 0 0 1 0 8h-3" /><path d="M4 4l16 16" /></>,
    bluetooth: <path d="M7 7l10 10-5 4V3l5 4L7 17" />,
    play: <path d="M6 4l12 8-12 8z" />,
    stop: <rect x="6" y="6" width="12" height="12" rx="1" />,
    trash: <path d="M4 7h16M10 11v6M14 11v6M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12M9 7V4h6v3" />,
    swap: <><path d="M7 7h12l-3-3M17 17H5l3 3" /></>,
  };
  return (
    <svg className="btn-icon" width={size} height={size} viewBox="0 0 24 24" fill="none"
         stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
      {p[name]}
    </svg>
  );
};

function LevelBars({ value, onChange, color }) {
  return (
    <div className="level">
      <div className="level-bars">
        {[1, 2, 3, 4].map((i) => (
          <div
            key={i}
            className={`level-bar ${i <= value ? "active" : ""}`}
            style={i <= value && color ? { background: color } : null}
            onClick={() => onChange(i === value ? i - 1 : i)}
          />
        ))}
      </div>
      <span className="level-num">L{value}</span>
    </div>
  );
}

function TempSlider({ value, onChange, limit, min = 20, disabled, danger }) {
  const pct = (v) => ((v - min) / (HARD_MAX - min)) * 100;
  const col = danger ? "var(--danger)" : tempColor(value);
  const bg = danger
    ? `linear-gradient(90deg, var(--border) ${pct(value)}%, color-mix(in oklab, var(--danger) 28%, var(--bg-2)) ${pct(value)}%)`
    : `linear-gradient(90deg, ${tempColor(30)} 0%, ${col} ${pct(value)}%, var(--border) ${pct(value)}%, var(--border) ${pct(limit)}%, color-mix(in oklab, var(--danger) 28%, var(--bg-2)) ${pct(limit)}%)`;
  return (
    <div className={`tslider ${disabled ? "disabled" : ""}`}>
      <input
        type="range"
        min={min}
        max={HARD_MAX}
        value={value}
        style={{ background: bg, "--thumb": col }}
        onChange={(e) => onChange(Math.min(Number(e.target.value), limit ?? HARD_MAX))}
      />
      <span className="tslider-val" style={{ color: col }}>{value}°</span>
    </div>
  );
}

const Row = ({ label, sub, children }) => (
  <div className="srow">
    <span className="srow-label">{label}{sub && <span className="sub">{sub}</span>}</span>
    <div className="srow-ctl">{children}</div>
  </div>
);

const Toggle = ({ checked, onChange, label }) => (
  <label className="toggle">
    <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
    <span className="toggle-track" />
    {label && <span className={`toggle-label ${checked ? "strong" : ""}`}>{label}</span>}
  </label>
);

// ---- static metadata -------------------------------------------------------

const DEVICE_META = {
  reon:  { title: "Reon",   zone: "Neck" },
  sockL: { title: "Sock L", zone: "Left foot" },
  sockR: { title: "Sock R", zone: "Right foot" },
};
const OSC_ADDRS = [
  ["PFHotHigh", "bool",  "Reon + Socks"],
  ["water",     "bool",  "Reon"],
  ["cold",      "float", "Reon"],
  ["heat",      "float", "Reon + Socks"],
  ["touchL",    "float", "Sock L"],
  ["touchR",    "float", "Sock R"],
];
const TABS = ["Devices", "OSC", "Socks", "Reon", "Manual", "Log"];
const STATE_LABEL = { connected: "Connected", connecting: "Connecting…", pairing: "Pairing…", disconnected: "Offline" };

// ---- main app --------------------------------------------------------------

function App() {
  // ---- UI-only state (not synced with the backend) ----
  const [tab, setTab] = useState(() => localStorage.getItem("reonosc.tab") || "Devices");
  useEffect(() => { localStorage.setItem("reonosc.tab", tab); }, [tab]);

  // ---- Device state (backend-owned, mirrored here) ----
  const [devices, setDevices] = useState({
    reon:  { state: "disconnected", mac: null, battery: null, model: null, caps: { coolMax: 3, heatMax: 3 } },
    sockL: { state: "disconnected", mac: null, battery: null },
    sockR: { state: "disconnected", mac: null, battery: null },
  });

  // OSC + inputs
  const [oscPort, setOscPortState] = useState(9001);
  const [oscRunning, setOscRunning] = useState(false);
  const [addresses, setAddresses] = useState({
    PFHotHigh: "/PFHotHigh",
    water:     "/ChairOSC/v1/water",
    cold:      "/ChairOSC/v1/cold",
    heat:      "/ChairOSC/v1/heat",
    touchL:    "/ChairOSC/v1/touch/left",
    touchR:    "/ChairOSC/v1/touch/right",
  });
  const [oscIn, setOscIn] = useState({ PFHotHigh: 0, water: 0, cold: 0, heat: 0, touchL: 0, touchR: 0 });

  // Zones (backend resolver output)
  const [reon, setReon] = useState({ mode: "Stop", level: 0, source: "OSC", reason: "Idle" });
  const [sockL, setSockL] = useState({ target: 0, source: "Off" });
  const [sockR, setSockR] = useState({ target: 0, source: "Off" });

  // Sock config + manual targets
  const [cfg, setCfgState] = useState({ baselineOn: true, baseline: 30, heatTouch: 55, heatMax: 60, touchMax: 45, limit: 60 });
  const [mSock, setMSock] = useState({ L: 40, R: 40 });

  // Manual override
  const [manual, setManual] = useState(false);
  const [mMode, setMMode] = useState("Stop");
  const [mLevel, setMLevel] = useState(0);

  // Reon presets
  const [heatLevel, setHeatLevel] = useState(3);
  const [coldLevel, setColdLevel] = useState(0);

  // Reon telemetry
  const [temps, setTemps] = useState({ plate: null, sink: null, board: null, ambient: null });
  const [plateHist, setPlateHist] = useState(() => Array(60).fill(30));

  // Options
  const [startMin, setStartMin] = useState(false);
  const [autoConn, setAutoConn] = useState(true);

  // Log — ring buffer of the last 300 lines.
  const [log, setLog] = useState([]);
  const logRef = useRef(null);
  const appendLog = useCallback((k, text) => setLog((p) => [...p, { t: fmtTime(), k, text }].slice(-300)), []);
  useEffect(() => { if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight; }, [log, tab]);

  // ---- bridge subscriptions ----
  useBridgeEvent("settings", (s) => {
    if (!s) return;
    if (typeof s.oscPort === "number") setOscPortState(s.oscPort);
    if (s.addresses) setAddresses((a) => ({ ...a, ...s.addresses }));
    if (s.socks) {
      setCfgState({
        baselineOn: !!s.socks.baselineOn,
        baseline:   s.socks.baseline   ?? 30,
        heatTouch:  s.socks.heatTouch  ?? 55,
        heatMax:    s.socks.heatMax    ?? 60,
        touchMax:   s.socks.touchMax   ?? 45,
        limit:      s.socks.limit      ?? 60,
      });
      setMSock({ L: s.socks.manualL ?? 40, R: s.socks.manualR ?? 40 });
    }
    if (typeof s.heatTouchLevel === "number") setHeatLevel(s.heatTouchLevel);
    if (typeof s.coldWaterLevel === "number") setColdLevel(s.coldWaterLevel);
    if (typeof s.startMinimised === "boolean") setStartMin(s.startMinimised);
    if (typeof s.autoConnectOnStart === "boolean") setAutoConn(s.autoConnectOnStart);
  });
  useBridgeEvent("osc.state", (s) => {
    if (!s) return;
    if (typeof s.running === "boolean") setOscRunning(s.running);
    if (typeof s.port === "number") setOscPortState(s.port);
  });
  useBridgeEvent("osc.input", (d) => d && setOscIn((o) => ({ ...o, ...d })));
  useBridgeEvent("device.state", (d) => {
    if (!d || !d.id) return;
    setDevices((prev) => ({ ...prev, [d.id]: { ...prev[d.id], ...d } }));
  });
  useBridgeEvent("zones", (z) => {
    if (!z) return;
    if (z.reon) setReon(z.reon);
    if (z.l) setSockL(z.l);
    if (z.r) setSockR(z.r);
  });
  useBridgeEvent("telemetry", (t) => {
    if (!t) return;
    setTemps(t);
    if (typeof t.plate === "number") setPlateHist((h) => [...h.slice(-59), t.plate]);
  });
  useBridgeEvent("log.line", (l) => {
    if (!l) return;
    setLog((p) => [...p, { t: l.t || fmtTime(), k: l.kind || "info", text: l.msg || "" }].slice(-300));
  });

  // Send ui.ready AFTER the subscription effects have run — strictly React 18
  // double-mount-safe thanks to the useEffect-with-empty-deps dance.
  useEffect(() => { if (bridge && bridge.isHosted) bridge.send("ui.ready"); }, []);

  // ---- derived --------------------------------------------------------------

  const reonState = { ...reon, connected: devices.reon.state === "connected" };
  const socksView = {
    L: { ...sockL, connected: devices.sockL.state === "connected" },
    R: { ...sockR, connected: devices.sockR.state === "connected" },
  };

  const nConn = Object.values(devices).filter((d) => d.state === "connected").length;
  const lastLog = log[log.length - 1];

  // ---- outbound actions (sent to the backend) -------------------------------

  const connect    = (id) => bridge.send("device.connect",    { id });
  const disconnect = (id) => bridge.send("device.disconnect", { id });
  const pair       = (id) => bridge.send("device.pair",       { id });
  const swapSocks  = () => bridge.send("socks.swap");

  const toggleOsc = () => {
    if (oscRunning) bridge.send("osc.stop");
    else bridge.send("osc.start", { port: oscPort });
  };
  const changeOscPort = (p) => {
    const clamped = clamp(Number(p) || 1024, 1024, 65535);
    setOscPortState(clamped);
    bridge.send("osc.setPort", { port: clamped });
  };
  const changeAddress = (key, address) => {
    setAddresses((a) => ({ ...a, [key]: address }));
    bridge.send("osc.setAddress", { key, address });
  };

  // Sock config: whenever any slider lands, push the whole block. Lowering
  // the limit drags every other setting down with it on the backend too,
  // so the local echo matches.
  const setCfg = (key, value) => {
    setCfgState((c) => {
      const next = { ...c, [key]: value };
      if (key === "limit") {
        for (const k of ["baseline", "heatTouch", "heatMax", "touchMax"])
          next[k] = Math.min(next[k], value);
      }
      bridge.send("socks.config", {
        baselineOn: next.baselineOn,
        baseline:   next.baseline,
        heatTouch:  next.heatTouch,
        heatMax:    next.heatMax,
        touchMax:   next.touchMax,
        limit:      next.limit,
      });
      return next;
    });
  };

  // Manual override toggle + Reon + socks are a single bridge channel —
  // every change sends the full {enabled, reonMode, reonLevel, sockL, sockR}
  // payload so the backend doesn't have to merge partial updates.
  const sendManual = (patch) => {
    const next = {
      enabled: patch.enabled ?? manual,
      reonMode: patch.reonMode ?? mMode,
      reonLevel: patch.reonLevel ?? mLevel,
      sockL: patch.sockL ?? mSock.L,
      sockR: patch.sockR ?? mSock.R,
    };
    bridge.send("manual.set", next);
  };
  const toggleManual = (b) => { setManual(b); sendManual({ enabled: b }); };
  const setMModeAndSend = (m) => { setMMode(m); sendManual({ reonMode: m }); };
  const setMLevelAndSend = (l) => { setMLevel(l); sendManual({ reonLevel: l }); };
  const setMSockL = (v) => { setMSock((s) => ({ ...s, L: v })); sendManual({ sockL: v }); };
  const setMSockR = (v) => { setMSock((s) => ({ ...s, R: v })); sendManual({ sockR: v }); };

  const setHeatLevelAndSend = (l) => { setHeatLevel(l); bridge.send("preset.heat", { level: l }); };
  const setColdLevelAndSend = (l) => { setColdLevel(l); bridge.send("preset.cold", { level: l }); };
  const setStartMinAndSend = (b) => { setStartMin(b); bridge.send("options.set", { startMinimised: b, autoConnect: autoConn }); };
  const setAutoConnAndSend = (b) => { setAutoConn(b); bridge.send("options.set", { startMinimised: startMin, autoConnect: b }); };

  // ---- zone rows for the sidebar body-map legend ----------------------------

  const reonChip = reonSourceLabel(reon);
  const zoneRows = [
    {
      id: "reon",
      name: "Neck",
      dev: "Reon",
      live: reonState.connected,
      color: reonColor(reon.mode),
      value: reon.mode === "Stop" ? "Stop" : `${reon.mode} L${reon.level}`,
      src: reonChip,
      extra: temps.plate != null ? `${temps.plate.toFixed(1)}°` : null,
    },
    ...["L", "R"].map((f) => ({
      id: f,
      name: f === "L" ? "Left foot" : "Right foot",
      dev: `Sock ${f}`,
      live: socksView[f].connected,
      color: socksView[f].target ? tempColor(socksView[f].target) : "var(--figure-2)",
      value: socksView[f].target ? `${socksView[f].target} °C` : "Off",
      src: socksView[f].source,
    })),
  ];

  // ---- render ---------------------------------------------------------------

  return (
    <div className="app v3">
      <div className="v3-main">
        <aside className="v3-side">
          <div className="side-fig">
            <BodyMap reon={{ ...reon, connected: reonState.connected }} socks={socksView} />
          </div>
          <div className="zone-list">
            {zoneRows.map((z) => (
              <div className={`zrow ${z.live ? "" : "off"}`} key={z.id}>
                <span className="zrow-dot"
                      style={{ background: z.live ? z.color : "transparent", borderColor: z.live ? z.color : "var(--text-muted)" }} />
                <span className="zrow-name">{z.name}<span className="sub">{z.dev}</span></span>
                <span className="zrow-val">
                  {z.live ? z.value : "Offline"}
                  {z.live && z.extra && <span className="sub">plate {z.extra}</span>}
                </span>
                {z.live && <span className={`src-chip ${String(z.src).toLowerCase()}`}>{z.src}</span>}
              </div>
            ))}
          </div>
        </aside>

        <section className="v3-content">
          <nav className="tabs">
            {TABS.map((t) => (
              <button key={t} className={`tab ${tab === t ? "active" : ""}`} onClick={() => setTab(t)}>
                {t}
                {t === "Devices" && <span className={`tab-badge ${nConn < 3 ? "warn" : ""}`}>{nConn}/3</span>}
                {t === "Manual"  && manual && <span className="tab-badge on">ON</span>}
              </button>
            ))}
          </nav>

          <div className="pane">
            {tab === "Devices" && (<>
              <div className="tbl">
                {Object.keys(DEVICE_META).map((id) => {
                  const meta = DEVICE_META[id];
                  const d = devices[id];
                  const st = d.state;
                  // Socks report cell voltage; the % is an estimate from it.
                  const bat = d.battery == null ? null : d.batteryVolts == null
                    ? <span>{d.battery}%</span>
                    : <span className="bat" title="Estimated from cell voltage">~{d.battery}%<span className="sub">{d.batteryVolts.toFixed(1)} V</span></span>;
                  return (
                    <div className="tbl-row dev" key={id}>
                      <span className={`stat-dot ${st}`} />
                      <span className="cell-main">{meta.title}<span className="sub">{meta.zone}</span></span>
                      <span className="mono dim">{d.mac || "—"}</span>
                      <span className="mono dim r">{st === "connected" ? bat : ""}</span>
                      <span className={`st-text ${st}`}>{STATE_LABEL[st] || st}</span>
                      <div className="actions">
                        {st === "connected"
                          ? <button className="btn sm" onClick={() => disconnect(id)}><Icon name="unlink" />Disconnect</button>
                          : <button className="btn sm" onClick={() => connect(id)} disabled={st !== "disconnected"}><Icon name="link" />Connect</button>}
                        <button className="btn sm ghost" onClick={() => pair(id)} disabled={st === "pairing"} title="Pair"><Icon name="bluetooth" /></button>
                      </div>
                    </div>
                  );
                })}
              </div>
              <div className="inline-bar sock-swap-bar">
                <span className="dim">Left ↔ right mixed up?</span>
                <button className="btn sm"
                        onClick={swapSocks}
                        disabled={!devices.sockL.mac && !devices.sockR.mac}
                        title="Swap the paired MACs between L and R">
                  <Icon name="swap" />Swap sock L ↔ R
                </button>
              </div>
              <div className="group-title">Options</div>
              <div className="checks">
                <label className="check"><input type="checkbox" checked={startMin} onChange={(e) => setStartMinAndSend(e.target.checked)} /><span className="check-box" />Start minimised to tray</label>
                <label className="check"><input type="checkbox" checked={autoConn} onChange={(e) => setAutoConnAndSend(e.target.checked)} /><span className="check-box" />Auto-connect all devices on start</label>
              </div>
            </>)}

            {tab === "OSC" && (<>
              <div className="inline-bar">
                <span className="srow-label">UDP port</span>
                <input className="input mono port" type="number" value={oscPort} min="1024" max="65535"
                       onChange={(e) => changeOscPort(e.target.value)} />
                <button className={`btn sm ${oscRunning ? "danger" : "primary"}`} onClick={toggleOsc}>
                  {oscRunning ? <><Icon name="stop" />Stop</> : <><Icon name="play" />Start</>}
                </button>
                <span className={`st-text ${oscRunning ? "connected" : "disconnected"}`}>{oscRunning ? "Listening" : "Stopped"}</span>
              </div>
              <div className="tbl">
                <div className="tbl-row osc head"><span>Input</span><span>Address</span><span>Drives</span><span className="r">Value</span></div>
                {OSC_ADDRS.map(([k, type, drives]) => {
                  const v = oscIn[k] ?? 0;
                  return (
                    <div className="tbl-row osc" key={k}>
                      <span className="cell-main">{k}<span className={`tag ${type}`}>{type}</span></span>
                      <input className="input mono" value={addresses[k] ?? ""}
                             onChange={(e) => changeAddress(k, e.target.value)} />
                      <span className="dim">{drives}</span>
                      <span className={`mono r val ${v > 0 ? "active" : ""}`}>{type === "bool" ? (v > 0 ? 1 : 0) : Number(v).toFixed(2)}</span>
                    </div>
                  );
                })}
              </div>
            </>)}

            {tab === "Socks" && (<>
              <div className="note">
                No temperature sensor on the socks — values are targets. Per foot the highest request wins,
                clamped to the max.
              </div>
              <Row label="Idle baseline" sub="when nothing happens">
                <Toggle checked={cfg.baselineOn} onChange={(v) => setCfg("baselineOn", v)} />
                <TempSlider value={cfg.baseline} onChange={(v) => setCfg("baseline", v)} limit={cfg.limit} disabled={!cfg.baselineOn} />
              </Row>
              <Row label="Heat touch" sub="PFHotHigh = 1">
                <TempSlider value={cfg.heatTouch} onChange={(v) => setCfg("heatTouch", v)} limit={cfg.limit} />
              </Row>
              <Row label="Heat at 1.0" sub="heat scales up to">
                <TempSlider value={cfg.heatMax} onChange={(v) => setCfg("heatMax", v)} limit={cfg.limit} />
              </Row>
              <Row label="Touch at 1.0" sub="touchL / touchR up to">
                <TempSlider value={cfg.touchMax} onChange={(v) => setCfg("touchMax", v)} limit={cfg.limit} />
              </Row>
              <Row label={<span className="limit-label">Max temperature</span>} sub="hard limit">
                <TempSlider value={cfg.limit} onChange={(v) => setCfg("limit", Math.max(30, v))} danger />
              </Row>
            </>)}

            {tab === "Reon" && (<>
              <div className="telem">
                {[["Plate", temps.plate], ["Sink", temps.sink], ["Board", temps.board], ["Ambient", temps.ambient]].map(([l, v]) => (
                  <div key={l}>
                    <span className="sub">{l}</span>
                    <span className="telem-v">{v != null ? v.toFixed(2) : "—"}<span className="sub">°C</span></span>
                  </div>
                ))}
              </div>
              <Spark data={plateHist} color={reonColor(reon.mode)} />
              <div className="group-title">Presets for OSC triggers</div>
              <Row label="Heat Touch" sub="PFHotHigh = 1">
                <LevelBars value={heatLevel} onChange={setHeatLevelAndSend} color={reonColor("Heat")} />
              </Row>
              <Row label="Cold Water" sub="water = 1">
                <LevelBars value={coldLevel} onChange={setColdLevelAndSend} color={reonColor("Cool")} />
              </Row>
            </>)}

            {tab === "Manual" && (<>
              <Row label="Override OSC" sub="ignore incoming OSC">
                <Toggle checked={manual} onChange={toggleManual} />
              </Row>
              <div className={`manual-body ${manual ? "" : "disabled"}`}>
                <Row label="Reon">
                  <div className="seg">
                    {["Stop", "Cool", "Heat"].map((m) => (
                      <button key={m} className={`seg-btn ${mMode === m ? `active ${m.toLowerCase()}` : ""}`} onClick={() => setMModeAndSend(m)}>{m}</button>
                    ))}
                  </div>
                  <LevelBars value={mLevel} onChange={setMLevelAndSend} color={mMode === "Stop" ? null : reonColor(mMode)} />
                </Row>
                <Row label="Sock L">
                  <TempSlider value={mSock.L} onChange={setMSockL} limit={cfg.limit} />
                </Row>
                <Row label="Sock R">
                  <TempSlider value={mSock.R} onChange={setMSockR} limit={cfg.limit} />
                </Row>
              </div>
            </>)}

            {tab === "Log" && (
              <div className="log-wrap">
                <div className="log-body v3" ref={logRef}>
                  {log.map((l, i) => (
                    <div className={`log-line ${l.k}`} key={i}>
                      <span className="log-time">{l.t}</span>
                      <span className="log-icon">{{ ok: "✓", err: "✕", cool: "❄", heat: "▲", stop: "■" }[l.k] || "›"}</span>
                      <span className="log-msg">{l.text}</span>
                    </div>
                  ))}
                </div>
                <button className="btn sm ghost log-clear" onClick={() => setLog([])}><Icon name="trash" />Clear</button>
              </div>
            )}
          </div>
        </section>
      </div>

      <footer className="statusbar">
        <span className="sb-item">
          <span className={`stat-dot ${oscRunning ? "connected" : "disconnected"}`} />
          OSC {oscRunning ? `UDP ${oscPort}` : "stopped"}
        </span>
        <span className="sb-item">{nConn}/3 devices</span>
        <span className={`sb-item ${manual ? "warn" : ""}`}>{manual ? "Manual override" : "OSC control"}</span>
        <button className="sb-log" onClick={() => setTab("Log")}>
          {lastLog ? <><span className="mono">{lastLog.t}</span> {lastLog.text}</> : "—"}
        </button>
      </footer>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App />);
