import { h, icon, clear, replace, toast, errorToast, confirmDialog, showMenu, toggle, fmtBytes, fmtRate, since, plural } from "../dom.js";
import * as api from "../api.js";
import { store, onChange } from "../store.js";
import { frame, endpointChip, STATE_LABEL } from "./common.js";
import { openEditor } from "./editor.js";

export function dashboardPage(container, params, { navigate }) {
  const f = frame(container, {
    title: "Bridges",
    subtitle: "",
    actions: [
      h("button", { class: "btn ghost", onClick: () => run(() => api.post("/api/bridges/start-all"), "All bridges started") }, icon("play", 15), "Start all"),
      h("button", { class: "btn ghost", onClick: stopAll }, icon("stop", 15), "Stop all"),
      h("button", { class: "btn primary", onClick: () => newBridge() }, icon("plus", 16), "New bridge"),
    ],
  });

  async function stopAll() {
    const n = store.bridges.filter((b) => b.running).length;
    if (n === 0) return;
    if (!(await confirmDialog({ title: "Stop all bridges?", message: `${plural(n, "running bridge")} will be stopped and their ports released.`, confirm: "Stop all", danger: true }))) return;
    run(() => api.post("/api/bridges/stop-all"), "All bridges stopped");
  }

  let filterText = "";
  const stats = h("div", { class: "stats" });
  const search = h("input", { class: "input", placeholder: "Filter bridges…", type: "search" });
  search.addEventListener("input", () => {
    filterText = search.value.trim().toLowerCase();
    paint();
  });
  const grid = h("div", { class: "cards" });
  const emptyBox = h("div");
  f.content.append(stats, h("div", { class: "toolbar" }, h("div", { class: "search" }, icon("search", 15), search)), grid, emptyBox);

  const cards = new Map();

  function paint() {
    const bridges = store.bridges;
    const running = bridges.filter((b) => b.running).length;
    f.setSubtitle(bridges.length === 0 ? "Connect serial ports, virtual COM ports and the network" : `${running} of ${plural(bridges.length, "bridge")} running`);
    paintStats(bridges);

    const visible = bridges.filter((b) => !filterText || matches(b, filterText));
    const keep = new Set(visible.map((b) => b.id));
    for (const [id, card] of cards) {
      if (!keep.has(id)) {
        card.el.remove();
        cards.delete(id);
      }
    }
    visible.forEach((b, i) => {
      let card = cards.get(b.id);
      if (!card) {
        card = bridgeCard(b, navigate);
        cards.set(b.id, card);
      }
      card.update(b);
      if (grid.children[i] !== card.el) grid.insertBefore(card.el, grid.children[i] || null);
    });

    if (bridges.length === 0) paintEmpty();
    else if (visible.length === 0) replace(emptyBox, h("div", { class: "empty" }, h("p", null, `No bridge matches “${filterText}”.`)));
    else clear(emptyBox);
  }

  function paintStats(bridges) {
    const running = bridges.filter((b) => b.running);
    const sessions = running.reduce((n, b) => n + [b.primary, ...b.targets].reduce((m, e) => m + e.sessions.length, 0), 0);
    const rate = running.reduce((n, b) => n + (b.stats ? b.stats.forwardRate + b.stats.reverseRate : 0), 0);
    const total = running.reduce((n, b) => n + (b.stats ? b.stats.forwardBytes + b.stats.reverseBytes : 0), 0);
    const errors = bridges.filter((b) => b.state === "error").length;
    replace(stats,
      stat("power", "Running", `${running.length}`, `/ ${bridges.length}`),
      stat("users", "Open connections", `${sessions}`),
      stat("zap", "Throughput", fmtRate(rate), total ? `· ${fmtBytes(total)} total` : ""),
      stat("alert", "Bridges with errors", `${errors}`, "", errors > 0 ? "var(--err)" : null));
  }

  function paintEmpty() {
    replace(emptyBox, h("div", { class: "empty" },
      h("div", { class: "art" }, icon("swap", 34)),
      h("h2", null, "Create your first bridge"),
      h("p", null, "A bridge connects a COM port — a real one, or a virtual one that applications on this PC can open — to a TCP/IP connection."),
      h("button", { class: "btn primary", onClick: () => newBridge() }, icon("plus", 16), "New bridge")));
  }

  function newBridge() {
    openEditor(null, { isNew: true });
  }

  paint();
  const off = onChange((what) => what === "bridges" && paint());
  return () => off();
}

function matches(b, text) {
  const hay = [b.name, b.notes, b.primary.label, ...b.targets.map((t) => t.label)].join(" ").toLowerCase();
  return hay.includes(text);
}

function stat(ic, label, value, small = "", color = null) {
  return h("div", { class: "stat" },
    h("div", { class: "k" }, icon(ic, 14), label),
    h("div", { class: "v", style: color ? { color } : null }, value, small ? h("small", null, small) : null));
}

async function run(fn, okMessage) {
  try {
    await fn();
    if (okMessage) toast(okMessage);
  } catch (e) {
    errorToast(e);
  }
}

/** Returns [area, line] path data; the line is drawn separately so the area's closing edges aren't stroked. */
function sparkPaths(values, max, height = 44, width = 60) {
  if (!values || values.length < 2) return ["", ""];
  const step = width / (values.length - 1);
  const pts = values.map((v, i) => `${(i * step).toFixed(2)} ${(height - 2 - (max > 0 ? (v / max) * (height - 6) : 0)).toFixed(2)}`);
  const line = "M" + pts.join(" L");
  return [`M0 ${height} L${pts.join(" L")} L${width} ${height} Z`, line];
}

function bridgeCard(initial, navigate) {
  let view = initial;
  const dot = h("span", { class: "dot" });
  const title = h("div", { class: "card-title", onClick: () => openEditor(view.config) });
  const pill = h("span", { class: "pill" });
  const sw = toggle(false, async (on, input) => {
    input.disabled = true;
    try {
      await api.post(`/api/bridges/${view.id}/${on ? "start" : "stop"}`);
    } catch (e) {
      errorToast(e);
      input.checked = !on;
    } finally {
      input.disabled = false;
    }
  }, "Start / stop");
  const switchInput = sw.querySelector("input");
  const menuBtn = h("button", { class: "btn ghost icon sm", title: "More", onClick: (e) => { e.stopPropagation(); menu(menuBtn); } }, icon("more", 18));
  const notes = h("div", { class: "card-notes" });
  const flow = h("div", { class: "flow" });
  const svgNS = "http://www.w3.org/2000/svg";
  const spark = document.createElementNS(svgNS, "svg");
  spark.setAttribute("viewBox", "0 0 60 44");
  spark.setAttribute("preserveAspectRatio", "none");
  spark.setAttribute("class", "spark");
  const mk = (cls) => {
    const p = document.createElementNS(svgNS, "path");
    p.setAttribute("class", cls);
    spark.append(p);
    return p;
  };
  const [pfA, prA, pfL, prL] = [mk("fwd-a"), mk("rev-a"), mk("fwd-l"), mk("rev-l")];
  const err = h("div", { class: "card-error", hidden: true });
  const fwd = h("span", { class: "metric fwd", title: "Source → targets" }, icon("arrowRight", 14), h("span"));
  const rev = h("span", { class: "metric rev", title: "Targets → source" }, icon("arrowLeft", 14), h("span"));
  const sessions = h("span", { class: "metric", title: "Open connections" }, icon("users", 14), h("span"));
  const uptime = h("span", { class: "right" });

  const el = h("article", { class: "card" },
    h("div", { class: "card-head" }, dot, title, h("div", { class: "right" }, pill, sw, menuBtn)),
    notes,
    h("div", { class: "card-body" }, flow, spark),
    err,
    h("div", { class: "card-foot" }, fwd, rev, sessions, uptime));

  let flowKey = "";

  function menu(anchor) {
    showMenu(anchor, [
      { label: "Edit", icon: "edit", action: () => openEditor(view.config) },
      { label: "Monitor traffic", icon: "activity", action: () => navigate(`monitor/${view.id}`) },
      { label: "Duplicate", icon: "copy", action: () => run(() => api.post(`/api/bridges/${view.id}/duplicate`), "Copy created (stopped)") },
      view.running ? { label: "Restart", icon: "refresh", action: () => run(() => api.post(`/api/bridges/${view.id}/restart`), "Restarted") } : null,
      view.running ? { label: "Reset counters", icon: "refresh", action: () => run(() => api.post(`/api/bridges/${view.id}/reset-stats`)) } : null,
      "-",
      {
        label: "Delete", icon: "trash", danger: true,
        action: async () => {
          if (!(await confirmDialog({ title: `Delete “${view.name}”?`, message: "The bridge is stopped and its settings are removed. This cannot be undone.", confirm: "Delete", danger: true }))) return;
          run(() => api.del(`/api/bridges/${view.id}`), "Bridge deleted");
        },
      },
    ].filter(Boolean));
  }

  function update(v) {
    view = v;
    el.className = `card state-${v.state}`;
    dot.className = `dot ${v.state}${v.state === "connected" ? " pulse" : ""}`;
    title.textContent = v.name;
    title.title = "Edit bridge";
    pill.className = `pill ${v.state}`;
    pill.textContent = STATE_LABEL[v.state] || v.state;
    if (document.activeElement !== switchInput) switchInput.checked = v.active;
    notes.textContent = v.notes || "";
    notes.hidden = !v.notes;

    // Re-render the endpoint diagram only when something visible changed.
    const all = [v.primary, ...v.targets];
    const key = JSON.stringify(all.map((e) => [e.id, e.status, e.statusText, e.sessions.length])) + JSON.stringify([v.config.primary, v.config.targets]);
    if (key !== flowKey) {
      flowKey = key;
      const cfgById = new Map([v.config.primary, ...v.config.targets].map((c) => [c.id, c]));
      const live = v.state === "connected";
      replace(flow,
        endpointChip(v.config.primary, v.running ? v.primary : null),
        h("div", { class: ["link", live && "live"] }, icon("swap", 18)),
        h("div", { class: "targets" }, v.targets.map((t) => endpointChip(cfgById.get(t.id) || t, v.running ? t : null))));
    }

    const s = v.stats;
    if (s) {
      const max = Math.max(1, ...s.forwardHistory, ...s.reverseHistory);
      const [fa, fl] = sparkPaths(s.forwardHistory, max);
      const [ra, rl] = sparkPaths(s.reverseHistory, max);
      pfA.setAttribute("d", fa);
      pfL.setAttribute("d", fl);
      prA.setAttribute("d", ra);
      prL.setAttribute("d", rl);
      fwd.lastChild.textContent = `${fmtBytes(s.forwardBytes)}${s.forwardRate > 0 ? " · " + fmtRate(s.forwardRate) : ""}`;
      rev.lastChild.textContent = `${fmtBytes(s.reverseBytes)}${s.reverseRate > 0 ? " · " + fmtRate(s.reverseRate) : ""}`;
    } else {
      for (const p of [pfA, pfL, prA, prL]) p.setAttribute("d", "");
      fwd.lastChild.textContent = "–";
      rev.lastChild.textContent = "–";
    }
    spark.style.opacity = v.running ? "1" : "0.25";
    const n = all.reduce((m, e) => m + e.sessions.length, 0);
    sessions.lastChild.textContent = String(n);
    uptime.textContent = v.running && v.startedAt ? `up ${since(v.startedAt)}` : "stopped";

    const errEp = all.find((e) => e.status === "Error");
    err.hidden = !errEp;
    if (errEp) replace(err, icon("alert", 15), h("span", null, h("b", null, `${errEp.label}: `), errEp.lastError || errEp.statusText));
  }

  return { el, update };
}
