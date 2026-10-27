/* global React */
const BM_STOPS = [[30, [150, 158, 172]], [40, [255, 186, 92]], [52, [255, 132, 56]], [62, [250, 84, 44]], [70, [232, 36, 72]]];
function tempColor(t) {
  if (t <= BM_STOPS[0][0]) return `rgb(${BM_STOPS[0][1].join(",")})`;
  for (let i = 1; i < BM_STOPS.length; i++) {
    const [t1, c1] = BM_STOPS[i], [t0, c0] = BM_STOPS[i - 1];
    if (t <= t1) { const k = (t - t0) / (t1 - t0); return `rgb(${c0.map((v, j) => Math.round(v + (c1[j] - v) * k)).join(",")})`; }
  }
  return `rgb(${BM_STOPS[BM_STOPS.length - 1][1].join(",")})`;
}
const reonColor = (mode) => (mode === "Cool" ? "#2f9cf0" : mode === "Heat" ? "#f26a2e" : "#8a93a3");

const FIGURE = "M600 243C540 243 515 262 500 298C491 316 474 527 474 527A22 22 0 0 0 519 532L537 348L541 490L529 860A26 26 0 0 0 581 864L612 558L642 864A26 26 0 0 0 694 860L683 490L687 348L705 532A22 22 0 0 0 750 527C750 527 733 316 724 298C709 262 684 243 624 243Z";

function Zone({ cx, cy, r, color, k, live, label, lx, ly, anchor = "middle" }) {
  return (
    <g>
      {live && k > 0 && <circle cx={cx} cy={cy} r={r * 1.75} fill={color} opacity={0.12 + k * 0.22} className="zone-breath" />}
      {live
        ? <circle cx={cx} cy={cy} r={r} fill={color} stroke="var(--bg)" strokeWidth="8" />
        : <circle cx={cx} cy={cy} r={r} fill="var(--bg)" stroke="var(--text-muted)" strokeWidth="5" strokeDasharray="10 8" />}
      <text x={lx} y={ly} textAnchor={anchor} className="bm-label" fill={live ? "var(--text)" : "var(--text-muted)"}>{label}</text>
    </g>
  );
}

function BodyMap({ reon, socks }) {
  const rk = reon.mode === "Stop" ? 0 : 0.3 + reon.level * 0.175;
  const sk = (s) => (s.target > 0 ? Math.min(1, (s.target - 28) / 40) : 0);
  const sockLabel = (s) => (!s.connected ? "—" : s.target > 0 ? `${s.target}°` : "off");
  return (
    <svg viewBox="430 70 400 920" className="bodymap" xmlns="http://www.w3.org/2000/svg" aria-label="Heat zones">
      <circle cx="612" cy="160" r="67" fill="var(--figure)" />
      <path d={FIGURE} fill="var(--figure)" />
      <Zone cx={612} cy={244} r={42} color={reonColor(reon.mode)} k={rk} live={reon.connected}
        label={!reon.connected ? "—" : reon.mode === "Stop" ? "Stop" : `${reon.mode[0]}${reon.level}`} lx={672} ly={200} anchor="start" />
      {["L", "R"].map((f, i) => {
        const s = socks[f];
        return <Zone key={f} cx={i ? 669 : 555} cy={872} r={40} color={s.target > 0 ? tempColor(s.target) : "var(--figure-2)"} k={sk(s)} live={s.connected}
          label={sockLabel(s)} lx={i ? 669 : 555} ly={968} />;
      })}
    </svg>
  );
}

function Spark({ data, color, h = 40 }) {
  const w = 300, min = Math.min(...data) - 0.5, max = Math.max(...data) + 0.5;
  const step = w / (data.length - 1);
  const d = data.map((v, i) => `${i ? "L" : "M"}${(i * step).toFixed(1)} ${(h - ((v - min) / (max - min)) * (h - 4) - 2).toFixed(1)}`).join(" ");
  return <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" className="spark"><path d={d} fill="none" stroke={color} strokeWidth="1.5" vectorEffect="non-scaling-stroke" /></svg>;
}

Object.assign(window, { tempColor, reonColor, BodyMap, Spark });
