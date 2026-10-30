import { h, icon, replace, clear, errorToast } from "../dom.js";
import * as api from "../api.js";
import { frame } from "./common.js";

export function toolsPage(container) {
  const f = frame(container, { title: "Tools", subtitle: "Network diagnostics, run from the PortBridge machine" });
  f.content.append(h("div", { class: "stack" },
    h("div", { class: "grid-2", style: { gap: "14px" } }, pingTool(), tcpTool(), resolveTool(), listenersTool()),
    interfacesTool()));
}

function tool(iconName, title, desc, form, result) {
  return h("section", { class: "panel" },
    h("div", { class: "panel-head" }, icon(iconName, 17), h("div", null, h("h3", null, title), h("div", { class: "muted small" }, desc))),
    h("div", { class: "panel-body stack tight" }, form, result));
}

function busy(btn, on) {
  btn.disabled = on;
  btn.dataset.label ??= btn.textContent;
  replace(btn, on ? h("span", { class: "spinner" }) : btn.dataset.label);
}

function pingTool() {
  const host = h("input", { class: "input", placeholder: "Host or IP" });
  const count = h("select", { class: "input", style: { width: "90px" } }, [1, 4, 10].map((n) => h("option", { value: n, selected: n === 4 }, `${n}×`)));
  const out = h("div", { class: "result", hidden: true });
  const btn = h("button", { class: "btn primary" }, "Ping");
  const run = async () => {
    if (!host.value.trim()) return host.focus();
    busy(btn, true);
    out.hidden = false;
    out.textContent = `Pinging ${host.value}…`;
    try {
      const r = await api.post("/api/tools/ping", { host: host.value.trim(), count: Number(count.value) });
      const ok = r.filter((x) => x.status === "Success");
      out.textContent = r.map((x) => x.status === "Success" ? `#${x.seq}  reply from ${x.address}  ${x.ms} ms` : `#${x.seq}  ${x.status}`).join("\n") +
        `\n\n${ok.length}/${r.length} replies${ok.length ? `, avg ${Math.round(ok.reduce((a, x) => a + x.ms, 0) / ok.length)} ms` : ""}`;
    } catch (e) {
      out.textContent = e.message;
    } finally {
      busy(btn, false);
    }
  };
  btn.addEventListener("click", run);
  host.addEventListener("keydown", (e) => e.key === "Enter" && run());
  return tool("activity", "Ping", "Check that a device or PC answers on the network.", h("div", { class: "row" }, host, count, btn), out);
}

function tcpTool() {
  const host = h("input", { class: "input", placeholder: "Host or IP" });
  const port = h("input", { class: "input", type: "number", placeholder: "Port", style: { width: "100px" }, value: 4001 });
  const out = h("div", { class: "result", hidden: true });
  const btn = h("button", { class: "btn primary" }, "Test");
  const run = async () => {
    if (!host.value.trim()) return host.focus();
    busy(btn, true);
    out.hidden = false;
    out.textContent = "Connecting…";
    try {
      const r = await api.post("/api/tools/tcp-check", { host: host.value.trim(), port: Number(port.value) });
      out.textContent = r.open ? `✓ Port ${port.value} is open (${r.ms} ms) — ${r.remote}` : `✗ Cannot connect: ${r.error}`;
      out.style.color = r.open ? "var(--ok)" : "var(--err)";
    } catch (e) {
      out.textContent = e.message;
    } finally {
      busy(btn, false);
    }
  };
  btn.addEventListener("click", run);
  host.addEventListener("keydown", (e) => e.key === "Enter" && run());
  return tool("plug", "TCP port check", "See whether something accepts connections on a host and port.", h("div", { class: "row" }, host, port, btn), out);
}

function resolveTool() {
  const host = h("input", { class: "input", placeholder: "Computer name, DNS name or IP" });
  const out = h("div", { class: "result", hidden: true });
  const btn = h("button", { class: "btn primary" }, "Resolve");
  const run = async () => {
    if (!host.value.trim()) return host.focus();
    busy(btn, true);
    out.hidden = false;
    try {
      const r = await api.post("/api/tools/resolve", { host: host.value.trim() });
      out.textContent = `${r.hostName}\n${r.addresses.join("\n")}`;
    } catch (e) {
      out.textContent = e.message;
    } finally {
      busy(btn, false);
    }
  };
  btn.addEventListener("click", run);
  host.addEventListener("keydown", (e) => e.key === "Enter" && run());
  return tool("globe", "Resolve host", "Look up the IP addresses for a name (or the name for an IP).", h("div", { class: "row" }, host, btn), out);
}

function listenersTool() {
  const out = h("div", { class: "row wrap", style: { gap: "6px" } }, h("span", { class: "spinner" }));
  const load = async () => {
    try {
      const ports = await api.get("/api/tools/listeners");
      replace(out, ports.length ? ports.map((p) => h("span", { class: "badge" }, p)) : h("span", { class: "muted" }, "None"));
    } catch (e) {
      replace(out, h("span", { class: "muted" }, e.message));
    }
  };
  load();
  return tool("server", "TCP ports in use", "Ports something is already listening on — pick a different one for a new TCP server.",
    h("div", null, h("button", { class: "btn sm", onClick: load }, icon("refresh", 13), "Refresh")), out);
}

function interfacesTool() {
  const body = h("div", null, h("div", { class: "panel-body" }, h("span", { class: "spinner" })));
  api.get("/api/tools/interfaces").then((list) => {
    replace(body, h("table", { class: "tbl" },
      h("thead", null, h("tr", null, ["Adapter", "Addresses", "Gateway", "MAC", "Speed"].map((t) => h("th", null, t)))),
      h("tbody", null, list.map((n) => h("tr", null,
        h("td", null, h("b", null, n.name), h("div", { class: "muted small" }, n.description)),
        h("td", { class: "mono" }, n.addresses.join("\n").split("\n").map((a) => h("div", null, a))),
        h("td", { class: "mono muted" }, n.gateways.join(", ") || "–"),
        h("td", { class: "mono muted" }, n.mac || "–"),
        h("td", { class: "muted" }, n.speedMbps ? `${n.speedMbps >= 1000 ? n.speedMbps / 1000 + " Gbit/s" : n.speedMbps + " Mbit/s"}` : "–"))))));
  }).catch((e) => replace(body, h("div", { class: "panel-body muted" }, e.message)));
  return h("section", { class: "panel" },
    h("div", { class: "panel-head" }, icon("globe", 17), h("h3", null, "Network adapters")), body);
}
