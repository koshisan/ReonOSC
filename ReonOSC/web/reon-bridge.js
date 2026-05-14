/*
 * reon-bridge.js
 *
 * Thin layer between the React UI and the C# host (WebView2). It is loaded
 * before app.jsx so that the React app can rely on `window.reonBridge` being
 * present.
 *
 * Contract:
 *   reonBridge.isHosted              true  if running inside WebView2
 *   reonBridge.send(cmd, payload)    JSON-encode and post to the host
 *   reonBridge.on(event, handler)    subscribe to host-pushed events, returns unsub fn
 *
 * Host -> UI events (see design_handoff_reonosc/README.md):
 *   conn.state, osc.state, osc.input, state.current, telemetry, log.line, settings
 */
(function () {
  const listeners = Object.create(null);
  const hosted = !!(window.chrome && window.chrome.webview);

  function dispatch(env) {
    if (!env || typeof env !== "object") return;
    const { event, payload } = env;
    const subs = listeners[event];
    if (!subs) return;
    for (const fn of subs.slice()) {
      try { fn(payload); }
      catch (e) { console.error("reonBridge handler for", event, "threw", e); }
    }
  }

  if (hosted) {
    window.chrome.webview.addEventListener("message", (ev) => {
      const data = ev.data;
      if (typeof data === "string") {
        try { dispatch(JSON.parse(data)); }
        catch (e) { console.error("Bad message from host", e, data); }
      } else {
        dispatch(data);
      }
    });
  }

  window.reonBridge = {
    isHosted: hosted,

    send(cmd, payload) {
      if (!hosted) {
        console.info("[reonBridge sim] send", cmd, payload);
        return;
      }
      try {
        window.chrome.webview.postMessage(JSON.stringify({ cmd, payload: payload ?? null }));
      } catch (e) {
        console.error("postMessage failed", e);
      }
    },

    on(event, handler) {
      (listeners[event] = listeners[event] || []).push(handler);
      return () => {
        const arr = listeners[event];
        if (!arr) return;
        const i = arr.indexOf(handler);
        if (i >= 0) arr.splice(i, 1);
      };
    },
  };

  // NOTE: 'ui.ready' is NOT sent from here any more — it's sent from app.jsx
  // after React has mounted and the on(...) listeners are in place. Sending
  // it from this IIFE would race against React's first useEffect and lose
  // the host's initial state push events.
})();
