import { h, icon, replace, seg, fmtDateTime, download, debounce, errorToast, confirmDialog } from "../dom.js";
import * as api from "../api.js";
import { store, onChange, inDesktop, postShell } from "../store.js";
import { frame } from "./common.js";

export function eventsPage(container) {
  let level = "all";
  let bridge = "";
  let text = "";

  const bridgeSel = h("select", { class: "input", style: { width: "220px" } });
  bridgeSel.addEventListener("change", () => { bridge = bridgeSel.value; paint(); });
  const search = h("input", { class: "input", type: "search", placeholder: "Search messages…", style: { width: "240px" } });
  search.addEventListener("input", debounce(() => { text = search.value.trim().toLowerCase(); paint(); }, 150));

  const f = frame(container, {
    title: "Events",
    subtitle: "Connections, errors and configuration changes",
    actions: [
      inDesktop ? h("button", { class: "btn ghost", onClick: () => postShell({ type: "openFolder", which: "logs" }) }, icon("folder", 15), "Log folder") : null,
      h("button", { class: "btn ghost", onClick: exportCsv }, icon("download", 15), "Export"),
      h("button", {
        class: "btn ghost",
        onClick: async () => {
          if (!(await confirmDialog({ title: "Clear the event list?", message: "This clears the in-memory list. Log files on disk are kept.", confirm: "Clear" }))) return;
          try { await api.del("/api/events"); store.events = []; paint(); } catch (e) { errorToast(e); }
        },
      }, icon("trash", 15), "Clear"),
    ],
  });

  const list = h("div", { class: "panel", style: { overflow: "hidden" } });
  f.content.append(
    h("div", { class: "toolbar" },
      seg([{ value: "all", label: "All" }, { value: "Warning", label: "Warnings & errors" }, { value: "Error", label: "Errors" }], level, (v) => { level = v; paint(); }),
      bridgeSel, search),
    list);

  const rank = { Info: 0, Warning: 1, Error: 2 };
  function filtered() {
    return store.events.filter((e) =>
      (level === "all" || rank[e.level] >= rank[level]) &&
      (!bridge || e.bridgeId === bridge) &&
      (!text || e.message.toLowerCase().includes(text) || (e.bridgeName || "").toLowerCase().includes(text)));
  }

  function paintBridges() {
    const cur = bridgeSel.value;
    replace(bridgeSel, h("option", { value: "" }, "All bridges"), store.bridges.map((b) => h("option", { value: b.id }, b.name)));
    bridgeSel.value = cur;
  }

  function paint() {
    const items = filtered().slice(0, 600);
    replace(list, items.length === 0
      ? h("div", { class: "empty" }, h("p", null, "No events match."))
      : items.map((e) => h("div", { class: `ev ${e.level}` },
        h("span", { class: "t", title: new Date(e.time).toLocaleString() }, fmtDateTime(e.time)),
        h("span", { class: "lv", title: e.level }, icon(e.level === "Error" ? "alert" : e.level === "Warning" ? "alert" : "info", 15)),
        h("span", { class: "b" }, e.bridgeName || "PortBridge"),
        h("span", { class: "m" }, e.message))));
  }

  function exportCsv() {
    const q = (s) => `"${String(s ?? "").replace(/"/g, '""')}"`;
    const rows = [["time", "level", "bridge", "category", "message"].join(",")]
      .concat(filtered().map((e) => [q(e.time), q(e.level), q(e.bridgeName), q(e.category), q(e.message)].join(",")));
    download(`portbridge-events-${new Date().toISOString().slice(0, 10)}.csv`, rows.join("\r\n"), "text/csv");
  }

  paintBridges();
  paint();
  api.get("/api/events?limit=1000").then((evs) => {
    const known = new Set(store.events.map((e) => e.id));
    store.events = [...store.events, ...evs.filter((e) => !known.has(e.id))].sort((a, b) => b.id - a.id).slice(0, 1000);
    paint();
  }).catch(() => {});
  const off = onChange((what) => {
    if (what === "events") { store.unseenErrors = 0; paint(); }
    if (what === "bridges" && bridgeSel.options.length !== store.bridges.length + 1) paintBridges();
  });
  return () => off();
}
