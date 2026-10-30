import { h, icon, KIND } from "../dom.js";

/** Standard page chrome: title row with actions, scrolling content area. */
export function frame(container, { title, subtitle, actions = [], flush = false }) {
  const sub = h("div", { class: "subtitle" }, subtitle || "");
  const actionBox = h("div", { class: "actions" }, actions);
  const content = h("div", { class: ["content", flush && "flush"] });
  container.append(h("header", { class: "topbar" }, h("div", null, h("h1", null, title), sub), actionBox), content);
  return {
    content,
    actions: actionBox,
    setSubtitle: (t) => (sub.textContent = t),
  };
}

export const STATE_LABEL = { connected: "Connected", waiting: "Waiting", error: "Error", stopped: "Stopped" };

export function epTitle(ep) {
  const c = ep;
  switch (c.kind) {
    case "Serial": return c.portName;
    case "VirtualSerial": return c.portName;
    case "TcpServer": return `TCP server :${c.port}`;
    case "TcpClient": return `${c.host || "?"}:${c.port}`;
    case "Udp": return `UDP :${c.localPort || c.port}${c.host ? " → " + c.host : ""}`;
    default: return c.kind;
  }
}

export function epSubtitle(c) {
  switch (c.kind) {
    case "Serial": return `${c.baudRate} · ${c.dataBits}${c.parity[0]}${stopText(c.stopBits)}${c.flowControl !== "None" ? " · " + flowText(c.flowControl) : ""}`;
    case "VirtualSerial": return "Virtual COM port";
    case "TcpServer": return `Listens on ${c.host || "all interfaces"}`;
    case "TcpClient": return "TCP client";
    case "Udp": return c.host ? `to ${c.host}:${c.port}` : "replies to sender";
    default: return "";
  }
}

export const stopText = (s) => (s === "Two" ? "2" : s === "OnePointFive" ? "1.5" : "1");
export const flowText = (f) => ({ RtsCts: "RTS/CTS", DtrDsr: "DTR/DSR", XonXoff: "XON/XOFF", None: "No flow control" }[f] || f);

/** Endpoint chip used in cards: icon, title, live status. */
export function endpointChip(cfg, live) {
  const k = KIND[cfg.kind] || KIND.Serial;
  const status = live ? live.status.toLowerCase() : "stopped";
  const sessions = live ? live.sessions.length : 0;
  const tags = [];
  if (cfg.kind === "VirtualSerial") tags.push(h("span", { class: "badge accent" }, "virtual"));
  if (cfg.rfc2217) tags.push(h("span", { class: "badge" }, "RFC 2217"));
  if (cfg.tls?.enabled) tags.push(h("span", { class: "badge" }, icon("lock", 10), " TLS"));
  return h("div", { class: `ep k-${cfg.kind}`, title: (live?.status === "Error" && live.lastError) || live?.statusText || "" },
    h("div", { class: "ico" }, icon(k.icon, 17)),
    h("div", { class: "txt" },
      h("div", { class: "t1" }, h("span", { class: ["dot", status] }), cfg.label || epTitle(cfg), tags),
      h("div", { class: "t2" }, live && live.status !== "Stopped" ? live.statusText : epSubtitle(cfg))),
    cfg.kind === "TcpServer" && live ? h("span", { class: "count", title: "Connected clients" }, sessions) : null);
}

// ---------------- endpoint model ----------------

export function newEndpoint(kind = "Serial", extra = {}) {
  return {
    id: Math.random().toString(16).slice(2, 14),
    kind,
    label: "",
    portName: "COM1",
    baudRate: 9600,
    dataBits: 8,
    parity: "None",
    stopBits: "One",
    flowControl: "None",
    dtr: true,
    rts: true,
    reopenDelayMs: 3000,
    signalPeerState: false,
    emulateBaudRate: false,
    modemPassthrough: false,
    host: "",
    port: 4001,
    localPort: 0,
    broadcast: false,
    maxClients: 0,
    allowedClients: [],
    reconnectDelayMs: 2000,
    connectTimeoutMs: 5000,
    connectOnDemand: false,
    noDelay: true,
    tcpKeepAlive: true,
    keepAliveSeconds: 10,
    pingWatchdog: false,
    pingIntervalSeconds: 10,
    pingTimeoutMs: 2000,
    pingFailures: 3,
    idleTimeoutSeconds: 0,
    rfc2217: false,
    tls: { enabled: false, certificatePath: "", certificatePassword: "", validation: "Strict", pinnedThumbprint: "", serverName: "" },
    receive: true,
    transmit: true,
    receiveFrom: "All",
    transmitTo: "All",
    holdWhileDisconnected: false,
    holdBufferKb: 512,
    packetMode: "Immediate",
    packetTimeoutMs: 150,
    maxPacketBytes: 2000,
    delimiter: "\\r\\n",
    ...extra,
  };
}

export function newBridge(name, primary, targets, extra = {}) {
  return {
    id: "",
    name,
    notes: "",
    active: false,
    primary,
    targets,
    trafficLog: { enabled: false, format: "Hex", maxFileMb: 20 },
    ...extra,
  };
}

/** What TCP-Com's File → New gives you: one COM port ⇄ one TCP/IP connection, as a TCP client. */
export function defaultBridge(ports) {
  const com = ports?.ports?.find((p) => !p.isCom0Com)?.name || "COM1";
  return newBridge("New bridge", newEndpoint("Serial", { portName: com }), [newEndpoint("TcpClient")]);
}
