// Shared live state fed by the /api/live WebSocket, plus the bridge to the desktop shell (WebView2).

export const store = {
  bridges: [],
  info: null,
  settings: null,
  shell: null,          // set when running inside the desktop app
  events: [],           // newest first
  live: false,
  unseenErrors: 0,
  ports: null,
};

const listeners = new Set();
export function onChange(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
export function emit(what) {
  for (const fn of [...listeners]) {
    try { fn(what); } catch (e) { console.error(e); }
  }
}

// ---------------- live socket ----------------
let ws = null;
let backoff = 500;
let stopped = false;
const trafficListeners = new Map(); // bridgeId -> Set<fn>

export function connectLive() {
  stopped = false;
  const proto = location.protocol === "https:" ? "wss" : "ws";
  ws = new WebSocket(`${proto}://${location.host}/api/live`);
  ws.onopen = () => {
    backoff = 500;
    store.live = true;
    for (const id of trafficListeners.keys()) send({ type: "subscribe", bridgeId: id });
    emit("live");
  };
  ws.onmessage = (e) => {
    const m = JSON.parse(e.data);
    if (m.type === "snapshot") {
      store.bridges = m.bridges;
      emit("bridges");
      shellHello();
    } else if (m.type === "events") {
      store.events = [...m.events.slice().reverse(), ...store.events].slice(0, 1000);
      for (const ev of m.events) {
        if (ev.level === "Error") {
          store.unseenErrors++;
          notifyShell(ev);
        }
      }
      emit("events");
    } else if (m.type === "traffic") {
      const byBridge = new Map();
      for (const r of m.records) {
        if (!byBridge.has(r.bridgeId)) byBridge.set(r.bridgeId, []);
        byBridge.get(r.bridgeId).push(r);
      }
      for (const [id, recs] of byBridge) for (const fn of trafficListeners.get(id) || []) fn(recs, m.dropped);
    }
  };
  ws.onclose = () => {
    store.live = false;
    emit("live");
    if (stopped) return;
    setTimeout(connectLive, backoff);
    backoff = Math.min(backoff * 2, 8000);
  };
}

export function disconnectLive() {
  stopped = true;
  ws?.close();
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

export function subscribeTraffic(bridgeId, fn) {
  if (!trafficListeners.has(bridgeId)) {
    trafficListeners.set(bridgeId, new Set());
    send({ type: "subscribe", bridgeId });
  }
  trafficListeners.get(bridgeId).add(fn);
  return () => {
    const set = trafficListeners.get(bridgeId);
    set?.delete(fn);
    if (set && set.size === 0) {
      trafficListeners.delete(bridgeId);
      send({ type: "unsubscribe", bridgeId });
    }
  };
}

// ---------------- desktop shell bridge ----------------
const webview = window.chrome && window.chrome.webview;
export const inDesktop = !!webview;

export function postShell(msg) {
  if (webview) webview.postMessage(msg);
}

if (webview) {
  webview.addEventListener("message", (e) => {
    const m = e.data;
    if (m.type === "shell") {
      store.shell = m;
      emit("shell");
    } else if (m.type === "command") {
      window.dispatchEvent(new CustomEvent("pb:command", { detail: m.command }));
    }
  });
}

let lastHello = "";
export function shellHello() {
  if (!webview) return;
  const active = store.bridges.filter((b) => b.running).length;
  const msg = {
    type: "hello",
    active,
    total: store.bridges.length,
    closeToTray: store.settings ? store.settings.closeToTray : true,
    notifications: store.settings ? store.settings.desktopNotifications : true,
  };
  const key = JSON.stringify(msg);
  if (key === lastHello) return;
  lastHello = key;
  postShell(msg);
}

let lastNotify = 0;
function notifyShell(ev) {
  if (!webview || Date.now() - lastNotify < 4000) return;
  lastNotify = Date.now();
  postShell({ type: "notify", title: ev.bridgeName || "PortBridge", message: ev.message, level: "error" });
}
