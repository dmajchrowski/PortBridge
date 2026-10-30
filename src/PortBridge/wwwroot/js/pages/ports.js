import { h, icon, replace, toast, errorToast, confirmDialog } from "../dom.js";
import * as api from "../api.js";
import { store, inDesktop, postShell } from "../store.js";
import { frame } from "./common.js";

const COM0COM_URL = "https://sourceforge.net/projects/com0com/files/com0com/2.2.2.0/";

export function portsPage(container) {
  const refreshBtn = h("button", { class: "btn", onClick: () => load() }, icon("refresh", 15), "Refresh");
  const f = frame(container, { title: "Ports", subtitle: "Serial ports on this PC and the virtual COM port driver", actions: [refreshBtn] });
  const body = h("div", { class: "stack" }, h("div", { class: "muted" }, h("span", { class: "spinner" }), " Scanning ports…"));
  f.content.append(body);

  async function load() {
    refreshBtn.disabled = true;
    try {
      const p = await api.get("/api/ports");
      store.ports = p;
      paint(p);
    } catch (e) {
      errorToast(e);
    } finally {
      refreshBtn.disabled = false;
    }
  }

  function paint(p) {
    const present = new Set(p.ports.map((x) => x.name.toUpperCase()));
    const missing = p.configured.filter((c) => !present.has(c.name.toUpperCase()));
    replace(body,
      h("section", { class: "panel" },
        h("div", { class: "panel-head" }, icon("serial", 17), h("h3", null, "Serial ports"), h("span", { class: "badge" }, p.ports.length)),
        p.ports.length === 0
          ? h("div", { class: "panel-body muted" }, "No COM ports found. USB-serial adapters appear here as soon as they are plugged in.")
          : h("table", { class: "tbl" },
            h("thead", null, h("tr", null, ["Port", "Device", "Manufacturer", "Type", "Used by"].map((t) => h("th", null, t)))),
            h("tbody", null, p.ports.map((x) => h("tr", null,
              h("td", null, h("b", null, x.name)),
              h("td", null, x.description),
              h("td", { class: "muted" }, x.manufacturer || "–"),
              h("td", null, x.isCom0Com ? h("span", { class: "badge accent" }, "virtual") : h("span", { class: "badge" }, "physical")),
              h("td", null, x.usedBy.length ? x.usedBy.join(", ") : h("span", { class: "muted" }, "free")))))),
        missing.length ? h("div", { class: "panel-body", style: { borderTop: "1px solid var(--border)" } },
          h("div", { class: "callout" }, icon("info", 16), h("div", null,
            h("b", null, "Configured but not present right now: "),
            missing.map((m) => `${m.name} (${m.usedBy.join(", ")})`).join(", "),
            ". Virtual ports only appear while their bridge runs; physical ports reappear when the device is plugged in."))) : null),

      com0comPanel(p));
  }

  function com0comPanel(p) {
    const c = p.com0com;
    const head = h("div", { class: "panel-head" }, icon("virtual", 17), h("h3", null, "Virtual COM port driver"),
      c.installed ? h("span", { class: "badge ok" }, "com0com installed") : h("span", { class: "badge warn" }, "not installed"));
    if (!c.installed) {
      return h("section", { class: "panel" }, head, h("div", { class: "panel-body stack" },
        h("p", { style: { margin: 0 } }, "Virtual COM ports let existing serial software talk over the network as if a device were plugged into this PC. ",
          "PortBridge uses the free, open-source ", h("b", null, "com0com"), " null-modem driver, which is digitally signed for 64-bit Windows."),
        h("ol", { style: { margin: 0, paddingLeft: "20px", display: "grid", gap: "4px" } },
          h("li", null, "Download ", h("code", null, "com0com-2.2.2.0-x64-fre-signed.zip"), " (or x86 for 32-bit Windows)."),
          h("li", null, "Run ", h("code", null, "setup.exe"), " as administrator. You can untick the default CNCA0/CNCB0 pair — PortBridge creates the ports it needs."),
          h("li", null, "Come back here and press Refresh. Bridges with virtual ports start working automatically.")),
        h("div", { class: "row" },
          h("a", {
            class: "btn primary", href: COM0COM_URL, target: "_blank", rel: "noopener",
            onClick: (e) => { if (inDesktop) { e.preventDefault(); postShell({ type: "openExternal", url: COM0COM_URL }); } },
          }, icon("external", 15), "Open com0com downloads")),
        h("div", { class: "callout" }, icon("shield", 16),
          "On PCs with Secure Boot and Memory Integrity enabled, Windows may refuse older signed drivers. If the ports don’t appear, check Device Manager for “com0com” devices with a warning sign.")));
    }

    const pairs = c.pairs || [];
    return h("section", { class: "panel" }, head,
      h("div", { class: "panel-body stack tight" },
        h("div", { class: "muted small" }, "Driver tools: ", h("code", null, c.path)),
        !c.canList ? h("div", { class: "callout" }, icon("info", 16), "Listing virtual port pairs needs administrator rights. Run PortBridge as administrator or as the Windows service to manage pairs here.") : null,
        c.error ? h("div", { class: "errors" }, c.error) : null),
      c.canList ? (pairs.length === 0
        ? h("div", { class: "panel-body muted", style: { paddingTop: 0 } }, "No virtual port pairs yet. They are created automatically when a bridge with a virtual COM port starts.")
        : h("table", { class: "tbl" },
          h("thead", null, h("tr", null, ["Application port", "Bridge side", "Managed by", "Used by", ""].map((t) => h("th", null, t)))),
          h("tbody", null, pairs.map((x) => h("tr", null,
            h("td", null, h("b", null, x.userPort)),
            h("td", { class: "mono muted" }, x.bridgePort),
            h("td", null, x.owned
              ? h("span", { class: "badge accent" }, x.origin === "adopted" ? "PortBridge (adopted)" : "PortBridge")
              : h("span", { class: "badge", title: "PortBridge never changes or removes pairs it does not own." }, "other program / manual")),
            h("td", null, x.usedBy.length ? x.usedBy.join(", ") : h("span", { class: "muted" }, "–")),
            h("td", { style: { textAlign: "right" } }, x.adoptable ? h("button", {
              class: "btn sm",
              title: "Looks like a pair created by an earlier PortBridge version",
              onClick: async () => {
                if (!(await confirmDialog({
                  title: `Let PortBridge manage ${x.userPort}?`,
                  message: `Only do this if PortBridge created ${x.userPort} (${x.bridgePort}) — for example with an earlier version. No driver settings are changed now; afterwards PortBridge may reconfigure or remove this pair. You can release it again.`,
                  confirm: "Adopt",
                }))) return;
                try {
                  await api.post(`/api/virtual-pairs/${x.number}/adopt`, { comName: x.userPort, confirm: true });
                  toast(`${x.userPort} adopted`);
                  load();
                } catch (e) { errorToast(e); }
              },
            }, "Adopt…") : x.owned && x.origin === "adopted" && x.usedBy.length === 0 ? h("button", {
              class: "btn sm",
              onClick: async () => {
                try { await api.post(`/api/virtual-pairs/${x.number}/release`, {}); toast(`${x.userPort} released`); load(); } catch (e) { errorToast(e); }
              },
            }, "Release") : null,
            x.owned && x.usedBy.length === 0 ? h("button", {
              class: "btn sm danger",
              onClick: async () => {
                if (!(await confirmDialog({ title: `Remove ${x.userPort}?`, message: `The virtual port pair ${x.userPort} ⇄ ${x.bridgePort} is removed from Windows.`, confirm: "Remove", danger: true }))) return;
                try {
                  await api.del(`/api/virtual-pairs/${x.number}`);
                  toast(`${x.userPort} removed`);
                  load();
                } catch (e) { errorToast(e); }
              },
            }, icon("trash", 13), "Remove") : null)))))) : null);
  }

  load();
}
