import { h, icon, clear, replace, toast, errorToast, openDrawer, bind, field, checkbox, seg, select, KIND, confirmDialog } from "../dom.js";
import * as api from "../api.js";
import { store } from "../store.js";
import { newEndpoint, defaultBridge } from "./common.js";

const BAUDS = [300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 28800, 38400, 57600, 76800, 115200, 230400, 460800, 921600];
const KIND_OPTIONS = [
  { value: "Serial", label: "Serial", icon: "serial" },
  { value: "VirtualSerial", label: "Virtual COM", icon: "virtual" },
  { value: "TcpServer", label: "TCP server", icon: "server" },
  { value: "TcpClient", label: "TCP client", icon: "client" },
  { value: "Udp", label: "UDP", icon: "udp" },
];

let uid = 0;

/** Opens the bridge editor drawer. `config` is a BridgeConfig; null opens a new one like TCP-Com's File → New. */
export async function openEditor(config, { isNew = false } = {}) {
  let ports = store.ports;
  try {
    ports = await api.get("/api/ports");
    store.ports = ports;
  } catch { /* use cached */ }

  const model = structuredClone(config ?? defaultBridge(ports));
  model.primary = normalize(model.primary);
  model.targets = (model.targets || []).map(normalize);
  model.trafficLog = model.trafficLog || { enabled: false, format: "Hex", maxFileMb: 20 };

  const running = !isNew && store.bridges.find((b) => b.id === model.id)?.running;
  const errorsBox = h("div", { class: "errors", hidden: true });
  let dirty = false;

  const portList = `ports-${++uid}`;
  const datalist = h("datalist", { id: portList }, (ports?.ports || []).map((p) => h("option", { value: p.name }, `${p.name} — ${p.description}`)));

  const ctx = { ports, portList, markDirty: () => (dirty = true) };

  // The standard layout is TCP-Com's window: one COM port ⇄ one TCP/IP connection. Anything else (several
  // targets, TCP ⇄ TCP, settings that layout cannot show) opens in the multi-endpoint layout.
  let layout = fitsStandard(model) ? "standard" : "multi";
  const layoutBox = h("div");
  const paintLayout = () => replace(layoutBox, layout === "standard"
    ? standardLayout(model, ctx, () => { layout = "multi"; paintLayout(); })
    : multiLayout(model, ctx, () => { layout = "standard"; paintLayout(); }));
  paintLayout();

  const nameInput = bind(h("input", { class: "input", placeholder: "e.g. Scale on COM3", maxlength: 80 }), model, "name", { onChange: ctx.markDirty });
  const body = h("div", { class: "stack" },
    errorsBox,
    h("div", { class: "grid-2" },
      field("Name", nameInput),
      field("Notes", bind(h("input", { class: "input", placeholder: "Optional — location, device, contact…" }), model, "notes", { onChange: ctx.markDirty }))),
    layoutBox,
    h("div", { class: "panel" },
      h("div", { class: "panel-head" }, icon("download", 16), h("h3", null, "Traffic log")),
      h("div", { class: "panel-body grid-3" },
        h("div", { class: "span-all" }, checkbox(model.trafficLog, "enabled", "Write all traffic of this bridge to a daily log file",
          `Files go to ${store.info?.logDir ? store.info.logDir + "\\traffic" : "the logs\\traffic folder"} on the PortBridge machine — every user of that PC can read them.`)),
        field("Format", select([{ value: "Hex", label: "Hex + text" }, { value: "Text", label: "Text only" }], model.trafficLog, "format")),
        field("Max file size (MB)", bind(h("input", { class: "input", type: "number", min: 1 }), model.trafficLog, "maxFileMb", { type: "number" })))),
    datalist);

  const saveBtn = h("button", { class: "btn primary", onClick: () => save(false) }, icon("save", 15), running ? "Save & restart" : "Save");
  const startBtn = running ? null : h("button", { class: "btn", onClick: () => save(true) }, icon("play", 14), "Save & start");

  const d = openDrawer({
    title: isNew ? "New bridge" : `Edit “${config.name}”`,
    subtitle: isNew ? "Configure both sides, then save." : running ? "Saving restarts the bridge with the new settings." : null,
    body,
    footer: [h("button", { class: "btn ghost", onClick: () => d.close() }, "Cancel"), h("div", { class: "grow" }), startBtn, saveBtn],
  });
  setTimeout(() => nameInput.focus(), 50);

  async function save(start) {
    errorsBox.hidden = true;
    const payload = structuredClone(model);
    if (isNew) {
      delete payload.id;
      payload.active = start;
    }
    for (const e of [payload.primary, ...payload.targets]) {
      e.allowedClients = (e.allowedClientsText ?? (e.allowedClients || []).join("\n")).split(/[\n,;]+/).map((s) => s.trim()).filter(Boolean);
      delete e.allowedClientsText;
    }
    saveBtn.disabled = true;
    if (startBtn) startBtn.disabled = true;
    try {
      const v = await api.post("/api/bridges", payload);
      if (start && !v.active) await api.post(`/api/bridges/${v.id}/start`);
      toast(isNew ? `Bridge “${v.name}” created${start ? " and started" : ""}` : `Saved${running ? " — bridge restarted" : ""}`);
      d.close();
    } catch (e) {
      if (e.errors?.length) {
        // The server names endpoints the way the multi-endpoint layout does.
        const where = (x) => layout === "standard" ? x.replace(/^Source:/, "Serial port:").replace(/^Target 1:/, "TCP/IP port:") : x;
        replace(errorsBox, h("b", null, e.message), h("ul", null, e.errors.map((x) => h("li", null, where(x)))));
        errorsBox.hidden = false;
        errorsBox.scrollIntoView({ behavior: "smooth", block: "start" });
      } else {
        errorToast(e);
      }
    } finally {
      saveBtn.disabled = false;
      if (startBtn) startBtn.disabled = false;
    }
  }
}

function nextFreePort(model) {
  const used = new Set([model.primary, ...model.targets].map((e) => e.port));
  let p = 4001;
  while (used.has(p)) p++;
  return p;
}

function normalize(ep) {
  const base = newEndpoint(ep.kind);
  const merged = { ...base, ...ep, tls: { ...base.tls, ...(ep.tls || {}) } };
  merged.allowedClientsText = (merged.allowedClients || []).join("\n");
  return merged;
}

/** Turns `ep` into a fresh endpoint of kind `k` in place, keeping its id, label and the listed settings. */
function resetKind(ep, k, ports, keep = []) {
  const kept = { id: ep.id, label: ep.label };
  for (const key of keep) kept[key] = ep[key];
  const fresh = newEndpoint(k);
  if (k === "VirtualSerial") fresh.portName = ports?.suggestedVirtual || "COM10";
  else if (k === "Serial") fresh.portName = ports?.ports?.find((p) => !p.isCom0Com)?.name || "COM1";
  else if (k === "TcpServer") fresh.port = ep.port || 4001;
  else if (k === "TcpClient") { fresh.port = ep.port || 4001; fresh.host = ep.host || ""; }
  // Keep line settings when switching between serial kinds.
  if ((k === "Serial" || k === "VirtualSerial") && (ep.kind === "Serial" || ep.kind === "VirtualSerial")) {
    for (const key of ["baudRate", "dataBits", "parity", "stopBits", "flowControl"]) fresh[key] = ep[key];
  }
  for (const key of Object.keys(ep)) delete ep[key];
  Object.assign(ep, fresh, kept);
  ep.allowedClientsText = "";
}

// ---------------------------------------------------------------------------------------------
// Standard layout: TCP-Com's converter window — serial port on the left, TCP/IP port on the right.

const isSerialKind = (k) => k === "Serial" || k === "VirtualSerial";
const isNetKind = (k) => k === "TcpClient" || k === "TcpServer" || k === "Udp";

/** True when every setting of the bridge is visible in the standard layout. */
function fitsStandard(model) {
  const [net, ...more] = model.targets;
  const s = model.primary;
  return isSerialKind(s.kind) && net && more.length === 0 && isNetKind(net.kind) &&
    s.receive && s.transmit && !s.holdWhileDisconnected && s.packetMode !== "Delimiter" &&
    (net.kind !== "TcpServer" || net.maxClients === 0 || net.maxClients === 1);
}

function standardLayout(model, ctx, toMulti) {
  const serial = model.primary;
  const net = model.targets[0];
  const more = moreOptions(serial, net, ctx);
  const netBox = h("div");
  const paintNet = () => { replace(netBox, networkPanel(net, serial, ctx, paintNet)); more.refresh(); };
  const serialBox = h("div");
  const paintSerial = () => { replace(serialBox, serialPanel(serial, net, ctx, paintSerial)); paintNet(); };
  paintSerial();
  return h("div", { class: "stack" },
    h("div", { class: "tc-panels" }, serialBox, netBox),
    more.el,
    h("div", { class: "layout-switch" }, "Need several connections for one port, or TCP ⇄ TCP? ",
      h("button", { type: "button", onClick: toMulti }, "Use the multi-endpoint editor")));
}

function serialPanel(ep, net, ctx, repaint) {
  const info = store.info || {};
  const virtual = ep.kind === "VirtualSerial";
  const c0c = ctx.ports?.com0com;
  const flags = { virtual, wait: ep.packetMode === "IdleTimeout" };
  const timeout = field("Timeout value (ms)", num(ep, "packetTimeoutMs", { min: 1 }));
  timeout.hidden = !flags.wait;

  let port;
  if (virtual) {
    const suggest = ctx.ports?.suggestedVirtual;
    const input = bind(h("input", { class: "input", placeholder: "COM10" }), ep, "portName");
    port = field("Virtual COM port", h("div", { class: "row" }, input,
      suggest ? h("button", { class: "btn sm", type: "button", onClick: () => { ep.portName = suggest; input.value = suggest; } }, suggest) : null),
      "Applications on this PC open this name. It appears while the bridge runs.", "span-all");
  } else {
    const desc = h("div", { class: "hint" });
    const paintDesc = () => {
      const p = ctx.ports?.ports?.find((x) => x.name.toUpperCase() === (ep.portName || "").toUpperCase());
      desc.textContent = p ? `${p.description}${p.manufacturer ? " · " + p.manufacturer : ""}${p.usedBy?.length ? " · used by " + p.usedBy.join(", ") : ""}` : "Not currently present — the bridge will keep retrying until it appears.";
    };
    paintDesc();
    port = h("div", { class: "field span-all" }, h("label", null, "COM port"),
      bind(h("input", { class: "input", list: ctx.portList, placeholder: "COM1" }), ep, "portName", { onChange: () => paintDesc() }), desc);
  }

  return h("section", { class: "panel" },
    h("div", { class: "panel-head" }, h("span", { class: "ico" }, icon(virtual ? "virtual" : "serial", 16)), h("h3", null, "Serial port")),
    h("div", { class: "panel-body stack" },
      virtual && c0c && !c0c.installed ? h("div", { class: "callout warn" }, icon("alert", 16),
        h("div", null, h("b", null, "The com0com driver is not installed. "),
          "Virtual COM ports are provided by the free, signed com0com null-modem driver. See the ",
          h("a", { href: "#/ports" }, "Ports page"), " for how to install it.")) : null,
      h("div", { class: "grid-2" }, port,
        virtual ? h("div", { class: "muted small span-all" }, "The application sets the speed and format when it opens the port.") : lineSettings(ep)),
      h("div", { class: "stack tight" },
        checkbox(flags, "virtual", "Create virtual COM port", "Nothing physical is opened; applications on this PC use the virtual port instead.",
          (on) => { resetKind(ep, on ? "VirtualSerial" : "Serial", ctx.ports, ["packetMode", "packetTimeoutMs", "maxPacketBytes"]); repaint(); }),
        checkbox(net, "holdWhileDisconnected", "Buffer data if the TCP/IP connection is closed",
          "Keeps serial data (up to the buffer size) and sends it as soon as the connection is back. Otherwise it is discarded."),
        checkbox(flags, "wait", "Wait for timeout before transmit",
          "Collects serial data until the line is quiet for the timeout (or the maximum packet size is reached), then sends it as one packet.",
          (on) => { ep.packetMode = on ? "IdleTimeout" : "Immediate"; timeout.hidden = !on; }),
        h("div", { class: "grid-2" }, timeout)),
      virtual && !info.elevated && info.mode === "desktop"
        ? h("div", { class: "callout" }, icon("shield", 16), "Windows will ask for administrator approval the first time this virtual port is created.") : null));
}

/** UDP has no client/server in the protocol; remembers which one the user picked while the remote host is still empty. */
const udpRole = new WeakMap();

function networkPanel(ep, serial, ctx, repaint) {
  const info = store.info || {};
  const udp = ep.kind === "Udp";
  const server = ep.kind === "TcpServer" || (udp && (udpRole.get(ep) ?? (ep.host ? "client" : "server")) === "server");
  const name = `tc-mode-${++uid}`;
  const flags = { udp };
  const setKind = (asServer, useUdp) => {
    const k = useUdp ? "Udp" : asServer ? "TcpServer" : "TcpClient";
    // A server's host is the interface it listens on, not something a client could connect to.
    const remote = server ? "" : ep.host;
    resetKind(ep, k, ctx.ports, ["port", "holdWhileDisconnected", "holdBufferKb", "receive", "transmit"]);
    // A TCP server listens on all interfaces; a UDP "server" answers whoever sent the last datagram.
    ep.host = asServer ? "" : remote;
    if (useUdp) udpRole.set(ep, asServer ? "server" : "client");
    repaint();
  };
  const radio = (label, on, pick) => {
    const input = h("input", { type: "radio", name, checked: on });
    input.addEventListener("change", () => input.checked && pick());
    return h("label", { class: "check" }, input, h("span", null, label));
  };

  const addrs = ["", "127.0.0.1", ...(info.addresses || [])];
  const fields = server
    ? [
        udp ? null : field("Local IP address", select(addrs.map((a) => ({ value: a, label: a === "" ? "All interfaces" : a === "127.0.0.1" ? "This PC only (127.0.0.1)" : a })), ep, "host")),
        field("Local port", num(ep, "port", { min: 1, max: 65535 }), udp ? "Replies go to whoever sent the last datagram." : null),
      ]
    : [
        field("Remote host IP address", txt(ep, "host", { placeholder: "192.168.1.50 or a computer name" }), "IP address, computer name or DNS name", "span-all"),
        field("Remote port", num(ep, "port", { min: 1, max: 65535 })),
      ];

  return h("section", { class: "panel" },
    h("div", { class: "panel-head net" }, h("span", { class: "ico" }, icon(server ? "server" : "client", 16)), h("h3", null, "TCP/IP port")),
    h("div", { class: "panel-body stack" },
      h("div", { class: "stack tight" },
        radio("This PC will act as TCP client", !server, () => setKind(false, udp)),
        radio("This PC will act as TCP server", server, () => setKind(true, udp))),
      h("div", { class: "grid-2" }, fields),
      checkbox(flags, "udp", "Use UDP instead of TCP/IP", "Both ends must use UDP.", (on) => setKind(server, on)),
      ioOptions(ep, serial)));
}

/** TCP-Com's "I/O Options" dialog, as a collapsible section. */
function ioOptions(ep, serial) {
  const body = h("div", { class: "stack tight", hidden: true, style: { marginTop: "10px" } });
  const toggle = h("button", { type: "button", class: "adv-toggle" }, "I/O options", icon("chevronDown", 15));
  toggle.addEventListener("click", () => {
    body.hidden = !body.hidden;
    toggle.classList.toggle("open", !body.hidden);
  });

  if (ep.kind === "TcpServer") {
    const multi = { on: ep.maxClients !== 1 };
    const inbound = { v: !ep.receive || ep.receiveFrom === "None" ? "Ignore" : ep.receiveFrom };
    const outbound = { v: !ep.transmit || ep.transmitTo === "None" ? "Ignore" : ep.transmitTo };
    body.append(
      checkbox(multi, "on", "Allow multiple client connections", null, (on) => (ep.maxClients = on ? 0 : 1)),
      field("Inbound TCP/IP data", select([
        { value: "All", label: "Send data from all clients out the serial port" },
        { value: "Ignore", label: "Ignore data from all clients" },
        { value: "First", label: "Send data from the first connected client only" }], inbound, "v",
      { onChange: (v) => { ep.receive = v !== "Ignore"; ep.receiveFrom = v === "First" ? "First" : "All"; } })),
      field("Inbound serial data", select([
        { value: "All", label: "Send serial data to all clients" },
        { value: "Ignore", label: "Ignore serial data" },
        { value: "First", label: "Send serial data to the first connected client only" }], outbound, "v",
      { onChange: (v) => { ep.transmit = v !== "Ignore"; ep.transmitTo = v === "First" ? "First" : "All"; } })));
  } else {
    const io = { v: !ep.receive ? "NoSerialOut" : !ep.transmit ? "NoNetOut" : "Both" };
    body.append(field("Data direction", select([
      { value: "Both", label: "Allow complete 2-way I/O" },
      { value: "NoSerialOut", label: "Disable output to the serial port" },
      { value: "NoNetOut", label: "Disable output to the TCP/IP port" }], io, "v",
    { onChange: (v) => { ep.receive = v !== "NoSerialOut"; ep.transmit = v !== "NoNetOut"; } })));
    if (ep.kind === "TcpClient" && serial.kind === "VirtualSerial") {
      body.append(checkbox(ep, "connectOnDemand", "Open the TCP/IP connection only while the virtual COM port is open",
        "Otherwise the connection opens when the bridge starts, and data arriving while no application has the port open is discarded."));
    }
  }
  return h("div", null, toggle, body);
}

/** Settings PortBridge has beyond TCP-Com's window, collapsed by default. `refresh` follows kind changes above. */
function moreOptions(serial, net, ctx) {
  const body = h("div", { class: "adv", hidden: true });
  const toggle = h("button", { type: "button", class: "adv-toggle" }, "More options", icon("chevronDown", 15));
  const paint = () => replace(body,
    h("div", { class: "adv-group" }, h("h4", null, "Serial port"), serialExtras(serial)),
    h("div", { class: "adv-group" }, h("h4", null, "TCP/IP port"),
      net.kind === "TcpServer" ? field("Allowed clients", bind(h("textarea", { class: "input mono", rows: 2, placeholder: "Anyone may connect. To restrict, list IPs or ranges, e.g.\n192.168.1.0/24" }), net, "allowedClientsText"),
        "One IP address or CIDR range per line. Empty = everyone.") : null,
      net.kind === "TcpClient" ? h("div", { class: "grid-3" },
        field("Retry every (ms)", num(net, "reconnectDelayMs", { min: 100, step: 100 })),
        field("Connect timeout (ms)", num(net, "connectTimeoutMs", { min: 500, step: 500 }))) : null,
      net.kind === "Udp" ? checkbox(net, "broadcast", "Allow broadcast", "Use 255.255.255.255 or x.x.x.255 as the remote host.") : null),
    ...advancedFields(net, ctx, paint, { standard: true }));
  toggle.addEventListener("click", () => {
    if (body.hidden) paint();
    body.hidden = !body.hidden;
    toggle.classList.toggle("open", !body.hidden);
  });
  return { el: h("div", null, toggle, body), refresh: () => { if (!body.hidden) paint(); } };
}

function serialExtras(ep) {
  if (ep.kind === "VirtualSerial") {
    return h("div", { class: "stack tight" },
      checkbox(ep, "signalPeerState", "Signal network state on DSR/DCD", "The application sees DSR and DCD only while the other side is connected."),
      checkbox(ep, "emulateBaudRate", "Emulate baud-rate timing", "Slows data to the speed the application sets, like a real port."),
      checkbox(ep, "modemPassthrough", "Pass DTR/RTS and modem lines through RFC 2217",
        "With an RFC 2217 TCP client on the other side: the application's DTR/RTS are sent to the remote port, the remote CTS appears as CTS and the remote DSR as DSR + DCD. " +
        "Not passed: RI, break, and baud rate / data format changes made by the application."),
      h("div", { class: "grid-3" }, field("Max packet size (bytes)", num(ep, "maxPacketBytes", { min: 0 }), "Used with “wait for timeout”. 0 = no limit")));
  }
  return h("div", { class: "stack tight" },
    h("div", { class: "row wrap", style: { gap: "18px" } },
      checkbox(ep, "dtr", "Raise DTR when open"),
      checkbox(ep, "rts", "Raise RTS when open")),
    h("div", { class: "grid-3" },
      field("Max packet size (bytes)", num(ep, "maxPacketBytes", { min: 0 }), "Used with “wait for timeout”. 0 = no limit"),
      field("Reopen after error (ms)", num(ep, "reopenDelayMs", { min: 500, step: 500 }), "USB adapters that are unplugged and replugged recover automatically.")));
}

// ---------------------------------------------------------------------------------------------
// Multi-endpoint layout: one source, any number of targets of any kind.

function multiLayout(model, ctx, toStandard) {
  const targetsBox = h("div");
  const renderTargets = () => {
    clear(targetsBox);
    model.targets.forEach((t, i) => {
      if (i > 0) targetsBox.append(h("div", { style: { height: "10px" } }));
      targetsBox.append(endpointEditor(t, `Target${model.targets.length > 1 ? " " + (i + 1) : ""}`, {
        ...ctx,
        onRemove: model.targets.length > 1 ? () => { model.targets.splice(i, 1); renderTargets(); } : null,
      }));
    });
  };
  renderTargets();

  return h("div", { class: "stack" },
    h("div", null,
      h("div", { class: "section-title" }, "Source"),
      endpointEditor(model.primary, "Source", ctx)),
    h("div", { class: "connector" }, icon("swap", 16), "data flows both ways", icon("swap", 16)),
    h("div", null,
      h("div", { class: "row", style: { marginBottom: "10px" } },
        h("div", { class: "section-title", style: { margin: 0 } }, "Targets"),
        h("span", { class: "muted small" }, "— data from the source goes to every target; data from any target goes back to the source")),
      targetsBox,
      h("button", {
        class: "btn ghost", style: { marginTop: "10px" },
        onClick: () => {
          model.targets.push(newEndpoint("TcpServer", { port: nextFreePort(model) }));
          renderTargets();
        },
      }, icon("plus", 15), "Add another target")),
    h("div", { class: "layout-switch" }, "One COM port and one TCP/IP connection? ",
      h("button", {
        type: "button",
        onClick: () => fitsStandard(model) ? toStandard()
          : toast("This bridge uses settings the standard layout cannot show (several targets, TCP ⇄ TCP, data direction or buffering on the serial side, or a delimiter).", "warn"),
      }, "Use the standard layout")));
}

// ---------------------------------------------------------------------------------------------

function endpointEditor(ep, role, ctx) {
  const wrap = h("section", { class: "ep-editor" });
  let advancedOpen = false;

  const render = () => {
    clear(wrap);
    const kindSeg = seg(KIND_OPTIONS, ep.kind, (k) => changeKind(k), "sm");
    const head = h("div", { class: "ep-editor-head" },
      h("span", { class: "role" }, role),
      kindSeg,
      h("div", { class: "grow" }),
      bind(h("input", { class: "input", placeholder: "Label (optional)", style: { width: "170px", height: "30px" } }), ep, "label"),
      ctx.onRemove ? h("button", { class: "btn ghost icon sm danger", title: "Remove target", onClick: ctx.onRemove }, icon("trash", 15)) : null);

    const body = h("div", { class: "ep-editor-body" }, basicFields(ep, ctx, render));
    const advToggle = h("button", { type: "button", class: ["adv-toggle", advancedOpen && "open"] }, "Advanced options", icon("chevronDown", 15));
    const adv = h("div", { class: "adv", hidden: !advancedOpen }, advancedFields(ep, ctx, render));
    advToggle.addEventListener("click", () => {
      advancedOpen = !advancedOpen;
      adv.hidden = !advancedOpen;
      advToggle.classList.toggle("open", advancedOpen);
    });
    body.append(h("div", null, advToggle), adv);
    wrap.append(head, body);
  };

  const changeKind = (k) => {
    resetKind(ep, k, ctx.ports);
    render();
  };

  render();
  return wrap;
}

const num = (obj, key, attrs = {}) => bind(h("input", { class: "input", type: "number", ...attrs }), obj, key, { type: "number" });
const txt = (obj, key, attrs = {}) => bind(h("input", { class: "input", ...attrs }), obj, key);

function lineSettings(ep, title) {
  return [
    title ? h("div", { class: "span-all section-title", style: { margin: "4px 0 0" } }, title) : null,
    field("Baud rate", bind(h("input", { class: "input", type: "number", list: "pb-bauds", min: 50 }), ep, "baudRate", { type: "number" })),
    field("Data bits", select(["8", "7", "6", "5"], ep, "dataBits", { type: "number" })),
    field("Parity", select(["None", "Even", "Odd", "Mark", "Space"], ep, "parity")),
    field("Stop bits", select([{ value: "One", label: "1" }, { value: "OnePointFive", label: "1.5" }, { value: "Two", label: "2" }], ep, "stopBits")),
    field("Flow control", select([
      { value: "None", label: "None" }, { value: "RtsCts", label: "Hardware (RTS/CTS)" },
      { value: "DtrDsr", label: "Hardware (DTR/DSR)" }, { value: "XonXoff", label: "Software (XON/XOFF)" }], ep, "flowControl")),
    h("datalist", { id: "pb-bauds" }, BAUDS.map((b) => h("option", { value: b }))),
  ];
}

function basicFields(ep, ctx, rerender) {
  const info = store.info || {};
  switch (ep.kind) {
    case "Serial": {
      const desc = h("div", { class: "hint" });
      const portInput = bind(h("input", { class: "input", list: ctx.portList, placeholder: "COM1" }), ep, "portName", {
        onChange: () => paintDesc(),
      });
      const paintDesc = () => {
        const p = ctx.ports?.ports?.find((x) => x.name.toUpperCase() === (ep.portName || "").toUpperCase());
        desc.textContent = p ? `${p.description}${p.manufacturer ? " · " + p.manufacturer : ""}${p.usedBy?.length ? " · used by " + p.usedBy.join(", ") : ""}` : "Not currently present — the bridge will keep retrying until it appears.";
      };
      paintDesc();
      return h("div", { class: "grid-3" },
        h("div", { class: "field" }, h("label", null, "Port"), portInput, desc),
        ...lineSettings(ep),
        h("div", { class: "span-all row wrap", style: { gap: "18px" } },
          checkbox(ep, "dtr", "Raise DTR when open"),
          checkbox(ep, "rts", "Raise RTS when open")));
    }

    case "VirtualSerial": {
      const c0c = ctx.ports?.com0com;
      const suggest = ctx.ports?.suggestedVirtual;
      const input = bind(h("input", { class: "input", placeholder: "COM10" }), ep, "portName");
      return h("div", { class: "stack" },
        c0c && !c0c.installed ? h("div", { class: "callout warn" }, icon("alert", 16),
          h("div", null, h("b", null, "The com0com driver is not installed. "),
            "Virtual COM ports are provided by the free, signed com0com null-modem driver. See the ",
            h("a", { href: "#/ports" }, "Ports page"), " for how to install it.")) : null,
        h("div", { class: "grid-3" },
          field("Virtual port name", h("div", { class: "row" }, input,
            suggest ? h("button", { class: "btn sm", type: "button", onClick: () => { ep.portName = suggest; input.value = suggest; } }, suggest) : null),
            "Applications open this name. It appears while the bridge runs."),
          h("div", { class: "span-2 stack tight", style: { alignSelf: "center" } },
            checkbox(ep, "signalPeerState", "Signal network state on DSR/DCD", "The application sees DSR and DCD only while the other side is connected."),
            checkbox(ep, "emulateBaudRate", "Emulate baud-rate timing", "Slows data to the speed the application sets, like a real port."),
            checkbox(ep, "modemPassthrough", "Pass DTR/RTS and modem lines through RFC 2217",
              "With an RFC 2217 TCP client on the other side: the application's DTR/RTS are sent to the remote port, the remote CTS appears as CTS and the remote DSR as DSR + DCD (com0com wires both to one pin). " +
              "Not passed: RI, break, and baud rate / data format changes made by the application (the remote port keeps the settings configured on the TCP client)."))),
        !info.elevated && info.mode === "desktop" ? h("div", { class: "callout" }, icon("shield", 16),
          "Windows will ask for administrator approval the first time this virtual port is created.") : null);
    }

    case "TcpServer": {
      const addrs = ["", "127.0.0.1", ...(info.addresses || [])];
      return h("div", { class: "grid-3" },
        field("Listen port", num(ep, "port", { min: 1, max: 65535 })),
        field("Listen on", select(addrs.map((a) => ({ value: a, label: a === "" ? "All interfaces" : a === "127.0.0.1" ? "This PC only (127.0.0.1)" : a })), ep, "host")),
        field("Max clients", num(ep, "maxClients", { min: 0 }), "0 = unlimited"),
        field("Allowed clients", bind(h("textarea", { class: "input mono", rows: 2, placeholder: "Anyone may connect. To restrict, list IPs or ranges, e.g.\n192.168.1.0/24" }), ep, "allowedClientsText"),
          "One IP address or CIDR range per line. Empty = everyone.", "span-all"));
    }

    case "TcpClient":
      return h("div", { class: "stack" },
        h("div", { class: "grid-3" },
          field("Remote host", txt(ep, "host", { placeholder: "192.168.1.50 or device.local" }), "IP address, computer name or DNS name", "span-2"),
          field("Remote port", num(ep, "port", { min: 1, max: 65535 })),
          field("Retry every (ms)", num(ep, "reconnectDelayMs", { min: 100, step: 100 })),
          field("Connect timeout (ms)", num(ep, "connectTimeoutMs", { min: 500, step: 500 }))),
        checkbox(ep, "connectOnDemand", "Connect only while the other side is active",
          "For example: open the TCP connection only while an application has the virtual COM port open (TCP-Com's “open TCP/IP port only when virtual COM port is open”)."),
        ep.rfc2217 ? h("div", { class: "grid-3" }, ...lineSettings(ep, "Remote serial settings (sent via RFC 2217)")) : null);

    case "Udp":
      return h("div", { class: "grid-3" },
        field("Remote host", txt(ep, "host", { placeholder: "Empty = reply to sender" }), "Use 255.255.255.255 or x.x.x.255 with broadcast"),
        field("Remote port", num(ep, "port", { min: 1, max: 65535 })),
        field("Local port", num(ep, "localPort", { min: 0, max: 65535 }), "0 = same as remote port"),
        h("div", { class: "span-all" }, checkbox(ep, "broadcast", "Allow broadcast")));
  }
  return null;
}

/** With `standard`, the settings the standard layout already shows (data direction, the buffer switch) are left out. */
function advancedFields(ep, ctx, rerender, { standard = false } = {}) {
  const isTcp = ep.kind === "TcpServer" || ep.kind === "TcpClient";
  const groups = [];

  // ---- data direction ----
  const direction = h("div", { class: "adv-group" }, h("h4", null, "Data direction"),
    h("div", { class: "row wrap", style: { gap: "18px" } },
      checkbox(ep, "receive", "Forward data received here", "Turn off to ignore everything this endpoint sends."),
      checkbox(ep, "transmit", "Send data to this endpoint", "Turn off to never write to it (monitor-only).")));
  if (ep.kind === "TcpServer") {
    direction.append(h("div", { class: "grid-2" },
      field("Accept data from", select([
        { value: "All", label: "All connected clients" }, { value: "First", label: "First connected client only" }, { value: "None", label: "No clients (read-only)" }], ep, "receiveFrom")),
      field("Send data to", select([
        { value: "All", label: "All connected clients" }, { value: "First", label: "First connected client only" }, { value: "None", label: "No clients" }], ep, "transmitTo"))));
  }
  if (!standard) groups.push(direction);

  // ---- packets ----
  const packetFields = h("div", { class: "grid-3" });
  const paintPackets = () => {
    clear(packetFields);
    if (ep.packetMode === "IdleTimeout") packetFields.append(field("Quiet time (ms)", num(ep, "packetTimeoutMs", { min: 1 }), "Send after the line is quiet this long"));
    if (ep.packetMode === "Delimiter") {
      packetFields.append(
        field("Delimiter", txt(ep, "delimiter", { class: "input mono" }), "Escapes allowed: \\r \\n \\t \\xHH"),
        field("Flush partial after (ms)", num(ep, "packetTimeoutMs", { min: 1 })));
    }
    packetFields.append(field("Max packet size (bytes)", num(ep, "maxPacketBytes", { min: 0 }), "0 = no limit"));
  };
  paintPackets();
  groups.push(h("div", { class: "adv-group" },
    h("h4", null, standard ? "Data from TCP/IP — how it is grouped before it goes out the serial port" : "Packets — how data read here is grouped before forwarding"),
    seg([{ value: "Immediate", label: "Immediately" }, { value: "IdleTimeout", label: "After a pause" }, { value: "Delimiter", label: "By delimiter" }],
      ep.packetMode, (v) => { ep.packetMode = v; paintPackets(); }, "sm"),
    packetFields));

  // ---- buffering ----
  groups.push(h("div", { class: "adv-group" }, h("h4", null, "Buffering"),
    h("div", { class: "grid-3" },
      standard ? null : h("div", { class: "span-2" }, checkbox(ep, "holdWhileDisconnected", "Hold data while nothing is connected here",
        "Keeps data for this endpoint and delivers it on (re)connect, so readings are not lost during network outages.")),
      field("Buffer size (KB)", num(ep, "holdBufferKb", { min: 1 }), standard ? "For “Buffer data if the TCP/IP connection is closed”." : null))));

  // ---- serial reopen ----
  if (ep.kind === "Serial") {
    groups.push(h("div", { class: "adv-group" }, h("h4", null, "Recovery"),
      h("div", { class: "grid-3" }, field("Reopen after error (ms)", num(ep, "reopenDelayMs", { min: 500, step: 500 }),
        "USB adapters that are unplugged and replugged recover automatically."))));
  }

  // ---- health ----
  if (isTcp) {
    groups.push(h("div", { class: "adv-group" }, h("h4", null, "Connection health"),
      h("div", { class: "grid-3" },
        h("div", { class: "span-2" }, checkbox(ep, "tcpKeepAlive", "TCP keep-alive", "Detects dead connections (cable pulled, router reboot) without sending data.")),
        field("Keep-alive interval (s)", num(ep, "keepAliveSeconds", { min: 1 })),
        h("div", { class: "span-2" }, checkbox(ep, "pingWatchdog", "Ping watchdog", "Pings the remote host and drops the connection when it stops answering (TCP-Com's remote ping).")),
        field("Ping every (s)", num(ep, "pingIntervalSeconds", { min: 1 })),
        field("Ping timeout (ms)", num(ep, "pingTimeoutMs", { min: 100, step: 100 })),
        field("Missed pings before reconnect", num(ep, "pingFailures", { min: 1 })),
        field("Idle timeout (s)", num(ep, "idleTimeoutSeconds", { min: 0 }), "Drop if nothing received for this long. 0 = off"),
        h("div", { class: "span-all" }, checkbox(ep, "noDelay", "Send immediately (disable Nagle)", "Lower latency for small messages; recommended for serial traffic.")))));
  }

  // ---- protocol ----
  if (isTcp) {
    const tlsBox = h("div", { class: "grid-3" });
    const paintTls = () => {
      clear(tlsBox);
      if (!ep.tls.enabled) return;
      if (ep.kind === "TcpServer") {
        tlsBox.append(
          field("Certificate (PFX file on the PortBridge machine)", txt(ep.tls, "certificatePath", { placeholder: "Empty = automatic self-signed certificate" }), null, "span-2"),
          field("Certificate password", txt(ep.tls, "certificatePassword", { type: "password", autocomplete: "new-password" })));
      } else {
        tlsBox.append(
          field("Verify server certificate", select([
            { value: "Strict", label: "Must be trusted (recommended)" }, { value: "Pinned", label: "Pinned thumbprint" }, { value: "AcceptAny", label: "Accept any (insecure)" }], ep.tls, "validation")),
          field("Pinned thumbprint (SHA-1 or SHA-256)", txt(ep.tls, "pinnedThumbprint", { class: "input mono" })),
          field("Server name (SNI)", txt(ep.tls, "serverName", { placeholder: "Default: remote host" })));
      }
    };
    paintTls();
    groups.push(h("div", { class: "adv-group" }, h("h4", null, "Protocol & security"),
      checkbox(ep, "rfc2217", "RFC 2217 (Telnet COM Port Control)",
        ep.kind === "TcpServer"
          ? "Clients such as pyserial (rfc2217://), ser2net-compatible software or other device-server drivers can change baud rate, DTR/RTS and flow control of the serial port on the other side."
          : "Talk to an RFC 2217 device server and send it the serial settings below.", () => rerender()),
      checkbox(ep.tls, "enabled", "Encrypt with TLS",
        ep.kind === "TcpServer" ? "Clients must connect with TLS (e.g. another PortBridge with TLS enabled, or stunnel)." : "Connect to a TLS server.", () => paintTls()),
      tlsBox,
      // The multi-endpoint layout shows these with the client's basic fields.
      standard && ep.kind === "TcpClient" && ep.rfc2217 ? h("div", { class: "grid-3" }, ...lineSettings(ep, "Remote serial settings (sent via RFC 2217)")) : null));
  }

  return groups;
}
