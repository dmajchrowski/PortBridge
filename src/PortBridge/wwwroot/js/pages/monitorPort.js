// "Monitor COM port": sets up PortBridge between a program and the device on a real COM port in one step.
// A COM port can be open in only one program, so it cannot be watched from the side: the program opens a virtual
// COM port instead, and a bridge passes everything between that virtual port and the real one, where the Monitor
// (and optionally the traffic log) sees it.
import { h, icon, replace, toast, errorToast, openDrawer, bind, field, checkbox } from "../dom.js";
import * as api from "../api.js";
import { store } from "../store.js";
import { newBridge, newEndpoint } from "./common.js";
import { lineSettings } from "./editor.js";

export async function openMonitorPort({ navigate }) {
  let ports = store.ports;
  try {
    ports = await api.get("/api/ports");
    store.ports = ports;
  } catch { /* use cached */ }

  const real = (ports?.ports || []).filter((p) => !p.isCom0Com);
  const freePairs = (ports?.com0com?.pairs || []).filter((x) => x.owned && x.usedBy.length === 0).map((x) => x.userPort);
  const c0c = ports?.com0com;

  const firstFree = real.find((p) => !p.usedBy?.length) || real[0];
  const device = newEndpoint("Serial", { portName: firstFree?.name || "" });
  const model = {
    virtualPort: freePairs[0] || ports?.suggestedVirtual || "COM10",
    name: firstFree ? `Monitor ${firstFree.name}` : "COM port monitor",
    tap: false,
    log: true,
  };
  let nameEdited = false;

  const errorsBox = h("div", { class: "errors", hidden: true });
  const flow = h("div", { class: "callout" });
  const paintFlow = () => replace(flow, icon("info", 16), h("div", null,
    h("b", null, "Your software"), " → ", h("code", null, model.virtualPort || "COM?"), " (virtual) ⇄ ",
    h("b", null, "PortBridge"), " ⇄ ", h("code", null, device.portName || "COM?"), " → ", h("b", null, "device"),
    h("div", { class: "muted small", style: { marginTop: "4px" } },
      "A COM port can be open in only one program, so PortBridge sits in between: your software opens the virtual port, " +
      "PortBridge passes every byte to and from the real port and shows it in the Monitor.")));

  // The device's port
  const nameInput = bind(h("input", { class: "input", maxlength: 80 }), model, "name", { onChange: () => (nameEdited = true) });
  const usedHint = h("div", { class: "hint" });
  const portSelect = h("select", { class: "input" }, real.map((p) =>
    h("option", { value: p.name }, `${p.name} — ${p.description}${p.usedBy?.length ? ` (used by ${p.usedBy.join(", ")})` : ""}`)));
  portSelect.value = device.portName;
  const paintUsed = () => {
    const p = real.find((x) => x.name === device.portName);
    usedHint.textContent = p?.usedBy?.length
      ? `${p.name} is already used by ${p.usedBy.join(", ")}. Delete or change that bridge first.`
      : "The port your device (e.g. a paging transmitter) is connected to.";
    usedHint.style.color = p?.usedBy?.length ? "var(--err)" : "";
  };
  portSelect.addEventListener("change", () => {
    device.portName = portSelect.value;
    if (!nameEdited) { model.name = `Monitor ${device.portName}`; nameInput.value = model.name; }
    paintUsed();
    paintFlow();
  });
  paintUsed();

  // The virtual port for the software
  const virtualInput = bind(h("input", { class: "input", placeholder: "COM10", style: { width: "120px" } }), model, "virtualPort", { onChange: () => paintFlow() });
  const pick = (name, note) => h("button", {
    class: "btn sm", type: "button", title: note,
    onClick: () => { model.virtualPort = name; virtualInput.value = name; paintFlow(); },
  }, name, h("span", { class: "muted small", style: { marginLeft: "4px" } }, note));
  const picks = [
    ...freePairs.map((n) => pick(n, "exists")),
    ports?.suggestedVirtual && !freePairs.includes(ports.suggestedVirtual) ? pick(ports.suggestedVirtual, "new") : null,
  ];
  paintFlow();

  const body = h("div", { class: "stack" },
    errorsBox,
    flow,
    c0c && !c0c.installed ? h("div", { class: "callout warn" }, icon("alert", 16),
      h("div", null, h("b", null, "The com0com driver is not installed. "), "The virtual port needs it; see the ",
        h("a", { href: "#/ports" }, "Ports page"), ".")) : null,
    real.length === 0
      ? h("div", { class: "callout warn" }, icon("alert", 16), "No COM ports found on this PC. Connect the device or USB serial adapter, then open this again.")
      : h("div", { class: "panel" },
        h("div", { class: "panel-head" }, h("span", { class: "ico" }, icon("serial", 16)), h("h3", null, "COM port to monitor")),
        h("div", { class: "panel-body stack" },
          h("div", { class: "field" }, h("label", null, "Port"), portSelect, usedHint),
          h("div", { class: "grid-3" }, ...lineSettings(device, "Settings — must match the device")))),
    h("div", { class: "panel" },
      h("div", { class: "panel-head" }, h("span", { class: "ico" }, icon("virtual", 16)), h("h3", null, "Virtual COM port for your software")),
      h("div", { class: "panel-body stack" },
        field("Port", h("div", { class: "row wrap", style: { gap: "8px" } }, virtualInput, ...picks),
          "Point your software at this port instead of the real one. If it already uses a PortBridge virtual port (marked “exists”), pick that one and nothing changes for the software."))),
    h("div", { class: "panel" },
      h("div", { class: "panel-head" }, icon("activity", 16), h("h3", null, "What to show")),
      h("div", { class: "panel-body stack tight" },
        checkbox(model, "tap", "TAP paging (Telocator Alphanumeric Protocol)",
          "The Monitor and the traffic log also say what each TAP message means. Leave it off for other devices."),
        checkbox(model, "log", "Also write a traffic log",
          `A daily file in ${store.info?.logDir ? store.info.logDir + "\\traffic" : "the logs\\traffic folder"} — every user of this PC can read it.`),
        field("Bridge name", nameInput))));

  const createBtn = h("button", { class: "btn primary", disabled: real.length === 0, onClick: create }, icon("activity", 15), "Create & open Monitor");
  const d = openDrawer({
    title: "Monitor a COM port",
    subtitle: "Watch everything your software and a device on a COM port say to each other.",
    body,
    footer: [h("button", { class: "btn ghost", onClick: () => d.close() }, "Cancel"), h("div", { class: "grow" }), createBtn],
  });

  function problems() {
    const v = (model.virtualPort || "").trim().toUpperCase();
    const out = [];
    if (!device.portName) out.push("Pick the COM port to monitor.");
    if (!/^COM\d+$/.test(v)) out.push("The virtual port needs a name like COM5.");
    else if (v === device.portName.toUpperCase()) out.push("The virtual port must have another name than the real one.");
    else if (real.some((p) => p.name.toUpperCase() === v)) out.push(`${v} is a real COM port on this PC; pick another name for the virtual one.`);
    return out;
  }

  async function create() {
    errorsBox.hidden = true;
    const list = problems();
    if (list.length) return showErrors("Please check:", list);
    const v = model.virtualPort.trim().toUpperCase();
    const bridge = newBridge(model.name.trim() || `Monitor ${device.portName}`,
      newEndpoint("VirtualSerial", { portName: v, label: `Software (${v})` }),
      [{ ...device, label: `Device (${device.portName})` }],
      {
        active: true,
        decode: model.tap ? "Tap" : "None",
        notes: `Your software uses ${v}; the device is on ${device.portName}.`,
        trafficLog: { enabled: model.log, format: "Hex", maxFileMb: 20 },
      });
    delete bridge.id;
    createBtn.disabled = true;
    try {
      const saved = await api.post("/api/bridges", bridge);
      d.close();
      toast(`Monitoring ${device.portName} — point your software at ${v}.`, "info", 8000);
      navigate(`monitor/${saved.id}`);
    } catch (e) {
      if (e.errors?.length) showErrors(e.message, e.errors.map((x) => x.replace(/^Source:/, "Virtual port:").replace(/^Target 1:/, `${device.portName}:`)));
      else errorToast(e);
    } finally {
      createBtn.disabled = false;
    }
  }

  function showErrors(title, list) {
    replace(errorsBox, h("b", null, title), h("ul", null, list.map((x) => h("li", null, x))));
    errorsBox.hidden = false;
    errorsBox.scrollIntoView({ behavior: "smooth", block: "start" });
  }
}
