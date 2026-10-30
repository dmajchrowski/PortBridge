import { h, icon, clear, replace, toast, errorToast, seg, fmtBytes, fmtTime, since, download, promptDialog, KIND, debounce } from "../dom.js";
import * as api from "../api.js";
import { store, onChange, subscribeTraffic } from "../store.js";
import { frame, epTitle } from "./common.js";

const MAX_RECORDS = 5000;
const MAX_DOM_LINES = 2500;

const prefs = loadPrefs();
function loadPrefs() {
  try { return { view: "both", ts: true, dir: "all", ...JSON.parse(localStorage.getItem("pb.monitor") || "{}") }; }
  catch { return { view: "both", ts: true, dir: "all" }; }
}
function savePrefs() {
  try { localStorage.setItem("pb.monitor", JSON.stringify(prefs)); } catch { /* ignore */ }
}

function decode(b64) {
  const s = atob(b64);
  const a = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) a[i] = s.charCodeAt(i);
  return a;
}

const hex = (bytes) => Array.from(bytes, (b) => b.toString(16).padStart(2, "0").toUpperCase()).join(" ");

/** Text with control characters rendered as visible escapes. */
function textNodes(bytes) {
  const out = [];
  let run = "";
  const flush = () => { if (run) { out.push(run); run = ""; } };
  bytes.forEach((b, i) => {
    if (b >= 0x20 && b < 0x7f) run += String.fromCharCode(b);
    else {
      flush();
      const esc = b === 13 ? "\\r" : b === 10 ? "\\n" : b === 9 ? "\\t" : b === 0 ? "\\0" : "\\x" + b.toString(16).padStart(2, "0").toUpperCase();
      out.push(h("span", { class: "ctl" }, esc));
      // Break multi-line packets visually, but not after the final newline.
      if (b === 10 && i < bytes.length - 1) out.push("\n");
    }
  });
  flush();
  return out;
}

const plainText = (bytes) => Array.from(bytes, (b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : b === 13 ? "\\r" : b === 10 ? "\\n" : "\\x" + b.toString(16).padStart(2, "0"))).join("");

export function monitorPage(container, params, { navigate }) {
  const bridgeId = params[0] || store.bridges.find((b) => b.running)?.id || store.bridges[0]?.id;
  const pick = h("select", { class: "input", style: { width: "260px" } });
  const paintPicker = () => {
    const cur = pick.value || bridgeId;
    replace(pick, store.bridges.map((b) => h("option", { value: b.id, selected: b.id === cur }, `${b.running ? "● " : "○ "}${b.name}`)));
    if (bridgeId) pick.value = bridgeId;
  };
  pick.addEventListener("change", () => navigate(`monitor/${pick.value}`));
  paintPicker();

  const f = frame(container, { title: "Monitor", subtitle: "Live traffic, connections and test data", actions: [pick], flush: true });

  if (!bridgeId) {
    f.content.classList.remove("flush");
    f.content.append(h("div", { class: "empty" }, h("div", { class: "art" }, icon("activity", 32)), h("h2", null, "Nothing to monitor yet"),
      h("p", null, "Create a bridge first, then watch its traffic here."), h("a", { class: "btn primary", href: "#/" }, "Go to bridges")));
    return;
  }

  const records = [];
  let paused = false;
  let pending = [];
  let search = "";
  let dropped = 0;

  // ---------------- toolbar ----------------
  const pauseBtn = h("button", { class: "btn sm" });
  const paintPause = () => replace(pauseBtn, icon(paused ? "play" : "pause", 14), paused ? `Resume${pending.length ? ` (${pending.length})` : ""}` : "Pause");
  pauseBtn.addEventListener("click", () => {
    paused = !paused;
    if (!paused) {
      const p = pending;
      pending = [];
      addRecords(p);
    }
    paintPause();
  });
  paintPause();

  const tsBox = h("input", { type: "checkbox", checked: prefs.ts });
  tsBox.addEventListener("change", () => { prefs.ts = tsBox.checked; savePrefs(); rerender(); });
  const searchInput = h("input", { class: "input", placeholder: "Highlight text or hex…", style: { width: "190px", height: "28px" } });
  searchInput.addEventListener("input", debounce(() => { search = searchInput.value.trim(); rerender(); }, 200));
  const stats = h("span", { class: "muted small", style: { marginLeft: "auto" } });

  const bar = h("div", { class: "monitor-bar" },
    seg([{ value: "both", label: "Hex + text" }, { value: "hex", label: "Hex" }, { value: "text", label: "Text" }], prefs.view, (v) => { prefs.view = v; savePrefs(); rerender(); }, "sm"),
    seg([{ value: "all", label: "Both ways" }, { value: "Forward", label: "→ Source to targets" }, { value: "Reverse", label: "← Targets to source" }], prefs.dir, (v) => { prefs.dir = v; savePrefs(); rerender(); }, "sm"),
    h("label", { class: "check small" }, tsBox, "Timestamps"),
    searchInput,
    stats,
    pauseBtn,
    h("button", { class: "btn sm", title: "Clear the view", onClick: () => { records.length = 0; pending = []; rerender(); } }, icon("trash", 14), "Clear"),
    h("button", { class: "btn sm", title: "Download as text", onClick: exportLog }, icon("download", 14), "Export"));

  const log = h("div", { class: "log", tabindex: "0" });
  let stick = true;
  log.addEventListener("scroll", () => { stick = log.scrollTop + log.clientHeight >= log.scrollHeight - 30; });

  // ---------------- send bar ----------------
  const sendState = loadSendState(bridgeId);
  const target = h("select", { class: "input", style: { width: "230px" } });
  const input = h("input", { class: "input mono grow", placeholder: "Text to send — escapes: \\r \\n \\x02", value: sendState.text || "" });
  const crlf = h("input", { type: "checkbox", checked: !!sendState.crlf });
  let format = sendState.format || "text";
  const fmtSeg = seg([{ value: "text", label: "Text" }, { value: "hex", label: "Hex" }], format, (v) => {
    format = v;
    input.placeholder = v === "hex" ? "Hex bytes, e.g. 02 41 42 03" : "Text to send — escapes: \\r \\n \\x02";
  }, "sm");
  const history = sendState.history || [];
  let histPos = -1;
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter") doSend();
    if (e.key === "ArrowUp" && history.length) { histPos = Math.min(histPos + 1, history.length - 1); input.value = history[histPos].text; e.preventDefault(); }
    if (e.key === "ArrowDown" && histPos >= 0) { histPos--; input.value = histPos >= 0 ? history[histPos].text : ""; e.preventDefault(); }
  });
  const macrosBox = h("div", { class: "macros" });

  async function doSend(text = input.value, fmt = format, nl = crlf.checked) {
    if (!text) return;
    try {
      const r = await api.post(`/api/bridges/${bridgeId}/send`, { endpointId: target.value, data: text, format: fmt, appendNewline: nl });
      if (r.delivered === 0) toast("Sent, but nothing is connected on that endpoint right now — data was dropped.", "warn");
      if (text === input.value) {
        history.unshift({ text, format: fmt });
        history.splice(20);
        histPos = -1;
      }
      persistSend();
    } catch (e) {
      errorToast(e);
    }
  }

  function persistSend() {
    saveSendState(bridgeId, { text: input.value, crlf: crlf.checked, format, history, macros: sendState.macros || [], target: target.value });
  }

  function paintMacros() {
    const macros = sendState.macros || [];
    replace(macrosBox, macros.map((m, i) => h("span", { class: "row", style: { gap: "2px" } },
      h("button", { class: "btn sm", title: `${m.format}: ${m.text}`, onClick: () => doSend(m.text, m.format, m.crlf) }, icon("zap", 13), m.name),
      h("button", { class: "btn ghost icon sm", title: "Remove", onClick: () => { macros.splice(i, 1); persistSend(); paintMacros(); } }, icon("x", 12)))));
    macrosBox.hidden = macros.length === 0;
  }
  paintMacros();

  const sendBar = h("div", { class: "send-bar" },
    icon("send", 16), target, fmtSeg, input,
    h("label", { class: "check small" }, crlf, "+ CR LF"),
    h("button", { class: "btn primary sm", onClick: () => doSend() }, "Send"),
    h("button", {
      class: "btn ghost sm", title: "Save as a one-click macro",
      onClick: async () => {
        if (!input.value) return;
        const name = await promptDialog({ title: "Save macro", message: "A button that sends this data again.", value: input.value.slice(0, 16), confirm: "Save" });
        if (!name) return;
        (sendState.macros = sendState.macros || []).push({ name, text: input.value, format, crlf: crlf.checked });
        persistSend();
        paintMacros();
      },
    }, icon("plus", 14), "Macro"));

  const side = h("aside", { class: "side" });
  f.content.append(h("div", { class: "monitor" },
    h("div", { class: "monitor-main" }, bar, log, sendBar, macrosBox),
    side));

  // ---------------- rendering ----------------
  function visible(r) {
    return prefs.dir === "all" || r.direction === prefs.dir;
  }

  function lineFor(r) {
    const bytes = r.bytes || (r.bytes = decode(r.data));
    const fwd = r.direction === "Forward";
    let data;
    if (prefs.view === "hex") data = h("span", { class: "hex" }, hex(bytes));
    else if (prefs.view === "text") data = h("span", null, textNodes(bytes));
    else data = [h("span", { class: "hex" }, hex(bytes)), h("span", { class: "asc" }, textNodes(bytes))];
    const matched = search && (hex(bytes).includes(search.toUpperCase()) || plainText(bytes).toLowerCase().includes(search.toLowerCase()));
    return h("div", { class: ["line", !prefs.ts && "hide-ts", r.injected && "inj", matched && "match"] },
      prefs.ts ? h("span", { class: "ts" }, fmtTime(r.time)) : null,
      h("span", { class: ["dir", fwd ? "f" : "r"], title: fwd ? "Source → targets" : "Targets → source" }, fwd ? "→" : "←"),
      h("span", { class: "src", title: r.source }, r.source),
      h("span", { class: "len" }, bytes.length),
      h("span", { class: "data" }, data));
  }

  function rerender() {
    clear(log);
    const vis = records.filter(visible).slice(-MAX_DOM_LINES);
    if (vis.length === 0) log.append(h("div", { class: "note" }, "Waiting for data… Traffic appears here as soon as anything flows through the bridge."));
    const frag = document.createDocumentFragment();
    for (const r of vis) frag.append(lineFor(r));
    log.append(frag);
    log.scrollTop = log.scrollHeight;
    stick = true;
    paintStats();
  }

  function paintStats() {
    const b = records.reduce((n, r) => n + (r.bytes ? r.bytes.length : 0), 0);
    stats.textContent = `${records.length} packets · ${fmtBytes(b)}${dropped ? ` · ${dropped} skipped (too fast)` : ""}`;
  }

  function addRecords(recs) {
    if (paused) {
      pending.push(...recs);
      if (pending.length > MAX_RECORDS) pending.splice(0, pending.length - MAX_RECORDS);
      paintPause();
      return;
    }
    const note = log.querySelector(".note");
    if (note) note.remove();
    const frag = document.createDocumentFragment();
    for (const r of recs) {
      records.push(r);
      if (visible(r)) frag.append(lineFor(r));
    }
    if (records.length > MAX_RECORDS) records.splice(0, records.length - MAX_RECORDS);
    log.append(frag);
    while (log.childElementCount > MAX_DOM_LINES) log.firstElementChild.remove();
    if (stick) log.scrollTop = log.scrollHeight;
    paintStats();
  }

  function exportLog() {
    const lines = records.filter(visible).map((r) => {
      const bytes = r.bytes || decode(r.data);
      return `${new Date(r.time).toISOString()} ${r.direction === "Forward" ? ">>" : "<<"} [${r.source}${r.injected ? " (sent)" : ""}] (${bytes.length}) ${hex(bytes)}  |${plainText(bytes)}|`;
    });
    const name = store.bridges.find((b) => b.id === bridgeId)?.name || "bridge";
    download(`${name.replace(/[^\w-]+/g, "_")}-traffic-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "")}.txt`, lines.join("\r\n"));
  }

  // ---------------- side panel ----------------
  let sideKey = "";
  function paintSide() {
    const b = store.bridges.find((x) => x.id === bridgeId);
    paintPicker();
    if (!b) {
      replace(side, h("div", { class: "muted" }, "This bridge no longer exists."));
      return;
    }
    // Endpoint targets for the send box
    const opts = [b.config.primary, ...b.config.targets].map((c, i) => ({ value: c.id, label: `${i === 0 ? "Source" : "Target"}: ${c.label || epTitle(c)}` }));
    const curTarget = target.value || sendState.target;
    const optKey = JSON.stringify(opts);
    if (target.dataset.key !== optKey) {
      target.dataset.key = optKey;
      replace(target, opts.map((o) => h("option", { value: o.value }, o.label)));
      if (opts.some((o) => o.value === curTarget)) target.value = curTarget;
      else target.value = opts[0].value;
    }

    const key = JSON.stringify([b.running, b.state, [b.primary, ...b.targets].map((e) => [e.status, e.statusText, e.details, e.sessions.map((s) => [s.id, s.bytesIn, s.bytesOut, s.detail])])]);
    if (key === sideKey) return;
    sideKey = key;
    const cfgs = [b.config.primary, ...b.config.targets];
    replace(side,
      !b.running ? h("div", { class: "callout warn" }, icon("alert", 16), h("div", null, "This bridge is stopped. ",
        h("a", { href: "#", onClick: async (e) => { e.preventDefault(); try { await api.post(`/api/bridges/${b.id}/start`); } catch (err) { errorToast(err); } } }, "Start it"))) : null,
      [b.primary, ...b.targets].map((ep, i) => endpointBlock(b, ep, cfgs[i], i === 0)));
  }

  function endpointBlock(b, ep, cfg, isSource) {
    const k = KIND[ep.kind];
    const lines = ep.details?.lines;
    return h("div", { class: "ep-block" },
      h("div", { class: "row" }, h("span", { class: ["dot", ep.status.toLowerCase()] }), icon(k.icon, 16),
        h("b", { class: "grow", style: { overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" } }, cfg.label || epTitle(cfg)),
        h("span", { class: "badge" }, isSource ? "source" : "target")),
      h("div", { class: "muted small" }, ep.statusText),
      ep.lastError && ep.status === "Error" ? h("div", { class: "small", style: { color: "var(--err)" } }, ep.lastError) : null,
      lines !== undefined ? h("div", { class: "lines", title: "Modem status lines" },
        ["Cts", "Dsr", "Dcd", "Ring"].map((l) => h("span", { class: ["ln", lines.includes(l) && "on"] }, l.toUpperCase().replace("RING", "RI"))),
        ep.details.dtr ? h("span", { class: ["ln", ep.details.dtr === "on" && "on"] }, "DTR") : null,
        ep.details.rts ? h("span", { class: ["ln", ep.details.rts === "on" && "on"] }, "RTS") : null) : null,
      ep.sessions.map((s) => h("div", { class: "sess" },
        h("div", { class: "r1" }, icon(ep.kind === "TcpServer" ? "users" : "link", 13), h("span", { class: "grow" }, s.remote),
          ep.kind === "TcpServer" ? h("button", {
            class: "btn ghost icon sm", title: "Disconnect this client",
            onClick: async () => { try { await api.post(`/api/bridges/${b.id}/sessions/${s.id}/kick`); toast("Client disconnected"); } catch (e) { errorToast(e); } },
          }, icon("x", 13)) : null),
        h("div", { class: "r2" }, `connected ${since(s.connectedAt)} · in ${fmtBytes(s.bytesIn)} · out ${fmtBytes(s.bytesOut)}${s.queued ? ` · queued ${fmtBytes(s.queued)}` : ""}`),
        s.detail ? h("div", { class: "r2" }, s.detail) : null)),
      ep.sessions.length === 0 && b.running ? h("div", { class: "muted small" }, ep.kind === "VirtualSerial" ? "No application has the port open." : "No active connection.") : null);
  }

  target.addEventListener("change", () => { sendState.target = target.value; persistSend(); });
  crlf.addEventListener("change", persistSend);

  // ---------------- data ----------------
  rerender();
  api.get(`/api/bridges/${bridgeId}/traffic`).then((recent) => {
    if (records.length === 0 && recent.length) addRecords(recent);
  }).catch(() => {});
  const unsub = subscribeTraffic(bridgeId, (recs, d) => {
    if (d) dropped += d;
    addRecords(recs);
  });
  paintSide();
  const off = onChange((what) => what === "bridges" && paintSide());
  return () => {
    unsub();
    off();
    persistSend();
  };
}

function loadSendState(id) {
  try { return JSON.parse(localStorage.getItem(`pb.send.${id}`) || "{}"); } catch { return {}; }
}
function saveSendState(id, s) {
  try { localStorage.setItem(`pb.send.${id}`, JSON.stringify(s)); } catch { /* ignore */ }
}
