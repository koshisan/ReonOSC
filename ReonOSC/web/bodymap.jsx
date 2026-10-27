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

const AV_W = 609, AV_H = 1474;
const AV_NECK = [281, 222], AV_FEET = { R: [109, 1420], L: [511, 1420] };

const AV_IMG = new Image();
AV_IMG.src = "avatar-mask.png";
const VB = { x: -40, y: -20, w: AV_W + 80, h: AV_H + 150 };

function BodyMap({ reon, socks }) {
  const wrapRef = React.useRef(null), canRef = React.useRef(null);
  const stateRef = React.useRef({ reon, socks });
  stateRef.current = { reon, socks };
  React.useEffect(() => {
    const wrap = wrapRef.current, can = canRef.current, ctx = can.getContext("2d");
    const fig = document.createElement("canvas"), fctx = fig.getContext("2d");
    let raf, alive = true;
    const draw = (now) => {
      if (!alive) return;
      const { reon, socks } = stateRef.current;
      const cw = wrap.clientWidth, ch = wrap.clientHeight, dpr = window.devicePixelRatio || 1;
      if (can.width !== Math.round(cw * dpr) || can.height !== Math.round(ch * dpr)) { can.width = Math.round(cw * dpr); can.height = Math.round(ch * dpr); }
      const sc = Math.min(cw / VB.w, ch / VB.h), ox = (cw - VB.w * sc) / 2 - VB.x * sc, oy = (ch - VB.h * sc) / 2 - VB.y * sc;
      const css = getComputedStyle(document.documentElement);
      const figC = css.getPropertyValue("--figure").trim() || "#444", txt = css.getPropertyValue("--text").trim(), mut = css.getPropertyValue("--text-muted").trim();
      const br = 0.5 + 0.5 * Math.sin(now / 450);
      const rk = reon.connected && reon.mode !== "Stop" ? 0.3 + reon.level * 0.175 : 0;
      const sk = (s) => (s.connected && s.target > 0 ? Math.min(1, (s.target - 28) / 40) : 0);
      const zones = [];
      if (rk) zones.push({ x: AV_NECK[0], y: AV_NECK[1] + 60, rx: 200, ry: 160, c: reonColor(reon.mode), k: rk });
      ["L", "R"].forEach((f) => { if (sk(socks[f])) zones.push({ x: AV_FEET[f][0], y: AV_FEET[f][1] - 50, rx: 115, ry: 200, c: tempColor(socks[f].target), k: sk(socks[f]) }); });

      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, cw, ch);
      ctx.setTransform(dpr * sc, 0, 0, dpr * sc, dpr * ox, dpr * oy);
      // aura behind
      zones.forEach((z) => {
        ctx.save(); ctx.translate(z.x, z.y); ctx.scale(z.rx * 1.6, z.ry * 1.3);
        const g = ctx.createRadialGradient(0, 0, 0, 0, 0, 1);
        g.addColorStop(0, z.c); g.addColorStop(1, "rgba(0,0,0,0)");
        ctx.globalAlpha = (0.10 + z.k * 0.16) * (0.8 + br * 0.2); ctx.fillStyle = g;
        ctx.beginPath(); ctx.arc(0, 0, 1, 0, Math.PI * 2); ctx.fill(); ctx.restore();
      });
      // figure with glow clipped to silhouette
      if (AV_IMG.complete && AV_IMG.naturalWidth) {
        if (fig.width !== AV_W) { fig.width = AV_W; fig.height = AV_H; }
        fctx.globalCompositeOperation = "source-over"; fctx.globalAlpha = 1;
        fctx.clearRect(0, 0, AV_W, AV_H);
        fctx.drawImage(AV_IMG, 0, 0, AV_W, AV_H);
        fctx.globalCompositeOperation = "source-in"; fctx.fillStyle = figC; fctx.fillRect(0, 0, AV_W, AV_H);
        fctx.globalCompositeOperation = "source-atop";
        zones.forEach((z) => {
          fctx.save(); fctx.translate(z.x, z.y); fctx.scale(z.rx, z.ry);
          const g = fctx.createRadialGradient(0, 0, 0, 0, 0, 1);
          g.addColorStop(0, z.c); g.addColorStop(0.55, z.c); g.addColorStop(1, "rgba(0,0,0,0)");
          fctx.globalAlpha = (0.55 + z.k * 0.45) * (0.85 + br * 0.15); fctx.fillStyle = g;
          fctx.beginPath(); fctx.arc(0, 0, 1, 0, Math.PI * 2); fctx.fill(); fctx.restore();
        });
        ctx.globalAlpha = 1; ctx.drawImage(fig, 0, 0);
      }
      // labels
      ctx.globalAlpha = 1; ctx.textBaseline = "alphabetic";
      const lab = (x, y, align, col, main, sub) => {
        if (!main) return;
        ctx.textAlign = align; ctx.font = "600 72px 'Geist Mono', monospace"; ctx.fillStyle = col;
        ctx.fillText(main, x, y);
        if (sub) { const w = ctx.measureText(main).width; ctx.font = "500 50px 'Geist Mono', monospace"; ctx.globalAlpha = 0.7; ctx.textAlign = "left"; ctx.fillText(sub, align === "center" ? x + w / 2 + 14 : x + w + 14, y); ctx.globalAlpha = 1; }
      };
      lab(AV_NECK[0] + 150, AV_NECK[1] + 40, "left", reon.connected ? reonColor(reon.mode) : mut, !reon.connected ? "—" : reon.mode === "Stop" ? "" : reon.mode[0] + reon.level);
      ["L", "R"].forEach((f) => { const s = socks[f]; lab(AV_FEET[f][0], AV_H + 110, "center", s.connected ? tempColor(s.target) : mut, !s.connected ? "—" : s.target > 0 ? s.target + "°" : "", f); });
      raf = requestAnimationFrame(draw);
    };
    raf = requestAnimationFrame(draw);
    return () => { alive = false; cancelAnimationFrame(raf); };
  }, []);
  return <div ref={wrapRef} className="bodymap"><canvas ref={canRef} style={{ width: "100%", height: "100%", display: "block" }} aria-label="Heat zones" /></div>;
}

function Spark({ data, color, h = 40 }) {
  const w = 300, min = Math.min(...data) - 0.5, max = Math.max(...data) + 0.5;
  const step = w / (data.length - 1);
  const d = data.map((v, i) => `${i ? "L" : "M"}${(i * step).toFixed(1)} ${(h - ((v - min) / (max - min)) * (h - 4) - 2).toFixed(1)}`).join(" ");
  return <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" className="spark"><path d={d} fill="none" stroke={color} strokeWidth="1.5" vectorEffect="non-scaling-stroke" /></svg>;
}

Object.assign(window, { tempColor, reonColor, BodyMap, Spark });
