// Small DOM toolkit: element builder, icons, formatting, toasts, dialogs, menus, drawers.

const SVGNS = "http://www.w3.org/2000/svg";

/** h("div", {class: "x", onClick}, child, [children], "text") */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  applyProps(el, props);
  append(el, children);
  return el;
}

function applyProps(el, props) {
  if (!props) return;
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === null || v === false) continue;
    if (k === "class") el.className = Array.isArray(v) ? v.filter(Boolean).join(" ") : v;
    else if (k === "style" && typeof v === "object") Object.assign(el.style, v);
    else if (k === "dataset") Object.assign(el.dataset, v);
    else if (k === "ref") v(el);
    else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k in el && k !== "list" && k !== "form" && typeof v !== "string") el[k] = v;
    else if (k === "value" || k === "checked") el[k] = v;
    else el.setAttribute(k, v === true ? "" : v);
  }
}

export function append(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false || c === true) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

export function replace(el, ...children) {
  clear(el);
  return append(el, children);
}

// ---------------- icons (24×24 stroke) ----------------
const ICONS = {
  grid: "M4 4h6.5v6.5H4zM13.5 4H20v6.5h-6.5zM4 13.5h6.5V20H4zM13.5 13.5H20V20h-6.5z",
  activity: "M3 12h4l3-8 4 16 3-8h4",
  plug: "M9 2v5M15 2v5M6 7h12v4a6 6 0 0 1-12 0zM12 17v5",
  wrench: "M14.7 6.3a4 4 0 0 0-5.3 5.3L3.5 17.5a2.1 2.1 0 0 0 3 3l5.9-5.9a4 4 0 0 0 5.3-5.3l-2.6 2.6-2.5-.5-.5-2.5z",
  list: "M9 6h12M9 12h12M9 18h12M4 6h.01M4 12h.01M4 18h.01",
  sliders: "M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1M15 4v4M9 10v4M17 16v4",
  plus: "M12 5v14M5 12h14",
  play: "M7 4.8v14.4a.8.8 0 0 0 1.2.7l11.5-7.2a.8.8 0 0 0 0-1.4L8.2 4.1a.8.8 0 0 0-1.2.7z",
  stop: "M7 7h10v10H7z",
  more: "M12 5.5h.01M12 12h.01M12 18.5h.01",
  edit: "M4 20h4L19 9a2.8 2.8 0 0 0-4-4L4 16zM13.5 6.5l4 4",
  copy: "M9 9h11v11H9zM5 15H4V4h11v1",
  trash: "M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3",
  refresh: "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7",
  search: "M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM20 20l-4-4",
  serial: "M3 8h18l-2.2 8H5.2zM8 11h.01M12 11h.01M16 11h.01M10 13.5h.01M14 13.5h.01",
  virtual: "M12 3l9 5-9 5-9-5zM3 12.5l9 5 9-5M3 17l9 5 9-5",
  server: "M4 4h16v6H4zM4 14h16v6H4zM8 7h.01M8 17h.01M12 10v4",
  client: "M5 12h13M13 6l6 6-6 6",
  udp: "M12 12h.01M8.5 8.5a5 5 0 0 0 0 7M15.5 8.5a5 5 0 0 1 0 7M5.6 5.6a9 9 0 0 0 0 12.8M18.4 5.6a9 9 0 0 1 0 12.8",
  swap: "M7 7h13M16 3l4 4-4 4M17 17H4M8 13l-4 4 4 4",
  x: "M6 6l12 12M18 6L6 18",
  check: "M5 12.5l4.5 4.5L19 7.5",
  alert: "M12 3.5l9.5 16.5h-19zM12 10v4.5M12 17.5h.01",
  info: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v5.5M12 7.8h.01",
  download: "M12 4v11M7 10.5l5 5 5-5M4 20h16",
  upload: "M12 16V5M7 9.5l5-5 5 5M4 20h16",
  pause: "M8 5v14M16 5v14",
  send: "M4 12l16-8-6 16-2.8-6.2z",
  lock: "M6 11h12v10H6zM8 11V7a4 4 0 0 1 8 0v4",
  sun: "M12 16.5a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4",
  moon: "M20.5 13.3A8.5 8.5 0 1 1 10.7 3.5a6.6 6.6 0 0 0 9.8 9.8z",
  display: "M3 4h18v12H3zM8 20h8M12 16v4",
  external: "M14 4h6v6M20 4l-9 9M18 14v6H4V6h6",
  folder: "M3 6.5h6l2 2h10V19H3z",
  shield: "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z",
  bell: "M6 16v-5a6 6 0 0 1 12 0v5l2 2H4zM10 21h4",
  terminal: "M4 6l6 6-6 6M12 18h8",
  chevronDown: "M6 9l6 6 6-6",
  chevronRight: "M9 6l6 6-6 6",
  logout: "M15 4h4v16h-4M10 8l-4 4 4 4M6 12h11",
  power: "M12 3v8M6.3 6.3a8 8 0 1 0 11.4 0",
  arrowRight: "M5 12h14M13 6l6 6-6 6",
  arrowLeft: "M19 12H5M11 6l-6 6 6 6",
  users: "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM2.5 21a6.5 6.5 0 0 1 13 0M16 3.2a4 4 0 0 1 0 7.6M21.5 21a6.5 6.5 0 0 0-3.5-5.8",
  clock: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3.5 2",
  zap: "M13 2L4 14h7l-1 8 9-12h-7z",
  eye: "M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z",
  globe: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18",
  key: "M14.5 9.5a4.5 4.5 0 1 0-3.2 4.3L13 15.5h2v2h2v2h3v-3l-5.8-5.8a4.5 4.5 0 0 0 .3-1.2zM8.5 8.5h.01",
  layers: "M12 3l9 5-9 5-9-5z",
  filter: "M4 5h16l-6 7.5V19l-4 2v-8.5z",
  save: "M5 4h11l4 4v12H5zM8 4v5h7V4M8 20v-6h8v6",
  cpu: "M7 7h10v10H7zM9.5 3v4M14.5 3v4M9.5 17v4M14.5 17v4M3 9.5h4M3 14.5h4M17 9.5h4M17 14.5h4",
};

export function icon(name, size = 18, extra = "") {
  const svg = document.createElementNS(SVGNS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("width", size);
  svg.setAttribute("height", size);
  svg.setAttribute("fill", name === "play" ? "currentColor" : "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", name === "more" ? "3" : "2");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  if (extra) svg.setAttribute("class", extra);
  const p = document.createElementNS(SVGNS, "path");
  p.setAttribute("d", ICONS[name] || ICONS.info);
  svg.append(p);
  return svg;
}

export const KIND = {
  Serial: { label: "Serial port", short: "Serial", icon: "serial", net: false },
  VirtualSerial: { label: "Virtual COM port", short: "Virtual COM", icon: "virtual", net: false },
  TcpServer: { label: "TCP server", short: "TCP server", icon: "server", net: true },
  TcpClient: { label: "TCP client", short: "TCP client", icon: "client", net: true },
  Udp: { label: "UDP", short: "UDP", icon: "udp", net: true },
};

// ---------------- formatting ----------------
export function fmtBytes(n) {
  if (n === null || n === undefined) return "–";
  if (n < 1024) return `${n} B`;
  const u = ["KB", "MB", "GB", "TB"];
  let i = -1;
  do { n /= 1024; i++; } while (n >= 1024 && i < u.length - 1);
  return `${n < 10 ? n.toFixed(1) : Math.round(n)} ${u[i]}`;
}

export const fmtRate = (n) => (n > 0 ? `${fmtBytes(Math.round(n))}/s` : "0 B/s");

export function fmtDuration(ms) {
  if (ms < 0 || !isFinite(ms)) return "–";
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400), hh = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), ss = s % 60;
  if (d > 0) return `${d}d ${hh}h`;
  if (hh > 0) return `${hh}h ${m}m`;
  if (m > 0) return `${m}m ${ss}s`;
  return `${ss}s`;
}

export const since = (iso) => (iso ? fmtDuration(Date.now() - new Date(iso).getTime()) : "–");

export function fmtTime(iso, ms = true) {
  const d = new Date(iso);
  const p = (x, n = 2) => String(x).padStart(n, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}${ms ? "." + p(d.getMilliseconds(), 3) : ""}`;
}

export function fmtDateTime(iso) {
  const d = new Date(iso);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  return sameDay ? fmtTime(iso, false) : `${d.toLocaleDateString(undefined, { month: "short", day: "numeric" })} ${fmtTime(iso, false)}`;
}

export const plural = (n, word, pluralWord) => `${n} ${n === 1 ? word : pluralWord || word + "s"}`;

// ---------------- toasts ----------------
export function toast(message, kind = "ok", ms = 3500) {
  const box = document.getElementById("toasts");
  const ic = kind === "err" ? "alert" : kind === "warn" ? "alert" : kind === "info" ? "info" : "check";
  const el = h("div", { class: `toast ${kind}`, role: "status" }, icon(ic, 17), h("div", null, message));
  box.append(el);
  setTimeout(() => {
    el.style.transition = "opacity .2s";
    el.style.opacity = "0";
    setTimeout(() => el.remove(), 220);
  }, ms);
}

export function errorToast(e) {
  toast(e?.message || String(e), "err", 6000);
}

// ---------------- dialogs ----------------
export function confirmDialog({ title, message, confirm = "OK", cancel = "Cancel", danger = false }) {
  return new Promise((resolve) => {
    const overlay = h("div", { class: "overlay", style: { zIndex: 65 } });
    const done = (v) => {
      overlay.remove();
      modal.remove();
      document.removeEventListener("keydown", onKey);
      resolve(v);
    };
    const okBtn = h("button", { class: ["btn", danger ? "danger solid" : "primary"], onClick: () => done(true) }, confirm);
    const modal = h("div", { class: "modal", role: "dialog", "aria-modal": "true" },
      h("div", { class: "m-body" }, h("h3", null, title), message ? h("p", null, message) : null),
      h("div", { class: "m-foot" }, h("button", { class: "btn", onClick: () => done(false) }, cancel), okBtn));
    const onKey = (e) => {
      if (e.key === "Escape") done(false);
      if (e.key === "Enter") done(true);
    };
    overlay.addEventListener("click", () => done(false));
    document.addEventListener("keydown", onKey);
    document.body.append(overlay, modal);
    okBtn.focus();
  });
}

export function promptDialog({ title, message, value = "", placeholder = "", confirm = "OK", type = "text" }) {
  return new Promise((resolve) => {
    const overlay = h("div", { class: "overlay", style: { zIndex: 65 } });
    const input = h("input", { class: "input", value, placeholder, type });
    const done = (v) => {
      overlay.remove();
      modal.remove();
      resolve(v);
    };
    const modal = h("div", { class: "modal", role: "dialog" },
      h("div", { class: "m-body stack tight" }, h("h3", null, title), message ? h("p", null, message) : null, input),
      h("div", { class: "m-foot" },
        h("button", { class: "btn", onClick: () => done(null) }, "Cancel"),
        h("button", { class: "btn primary", onClick: () => done(input.value) }, confirm)));
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") done(input.value);
      if (e.key === "Escape") done(null);
    });
    overlay.addEventListener("click", () => done(null));
    document.body.append(overlay, modal);
    input.focus();
    input.select();
  });
}

// ---------------- popup menu ----------------
let openMenu = null;
export function showMenu(anchor, items) {
  closeMenu();
  const menu = h("div", { class: "menu", role: "menu" },
    items.map((it) => it === "-" ? h("hr") : h("button", {
      class: it.danger ? "danger" : "", role: "menuitem",
      onClick: (e) => { e.stopPropagation(); closeMenu(); it.action(); },
    }, it.icon ? icon(it.icon, 16) : null, it.label)));
  document.body.append(menu);
  const r = anchor.getBoundingClientRect();
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  let left = Math.min(r.right - mw, window.innerWidth - mw - 8);
  let top = r.bottom + 4;
  if (top + mh > window.innerHeight - 8) top = r.top - mh - 4;
  menu.style.left = `${Math.max(8, left)}px`;
  menu.style.top = `${Math.max(8, top)}px`;
  openMenu = menu;
  setTimeout(() => document.addEventListener("click", closeMenu, { once: true }), 0);
}
export function closeMenu() {
  if (openMenu) openMenu.remove();
  openMenu = null;
}
window.addEventListener("resize", closeMenu);
window.addEventListener("keydown", (e) => e.key === "Escape" && closeMenu());

// ---------------- drawer ----------------
export function openDrawer({ title, subtitle, body, footer, onClose }) {
  const overlay = h("div", { class: "overlay" });
  const close = () => {
    overlay.remove();
    drawer.remove();
    document.removeEventListener("keydown", onKey);
    onClose?.();
  };
  const drawer = h("aside", { class: "drawer", role: "dialog", "aria-modal": "true" },
    h("div", { class: "drawer-head" },
      h("div", { class: "grow" }, h("h2", null, title), subtitle ? h("div", { class: "muted small" }, subtitle) : null),
      h("button", { class: "btn ghost icon", title: "Close", onClick: () => close() }, icon("x"))),
    h("div", { class: "drawer-body" }, body),
    footer ? h("div", { class: "drawer-foot" }, footer) : null);
  const onKey = (e) => {
    if (e.key === "Escape" && !document.querySelector(".modal")) close();
  };
  overlay.addEventListener("click", close);
  document.addEventListener("keydown", onKey);
  document.body.append(overlay, drawer);
  return { close, drawer };
}

// ---------------- form helpers ----------------
/** Two-way binds an input to obj[key]. */
export function bind(el, obj, key, { type = "text", onChange } = {}) {
  const isCheck = el.type === "checkbox";
  if (isCheck) el.checked = !!obj[key];
  else el.value = obj[key] ?? "";
  el.addEventListener(isCheck || el.tagName === "SELECT" ? "change" : "input", () => {
    let v = isCheck ? el.checked : el.value;
    if (type === "number") v = v === "" ? 0 : Number(v);
    obj[key] = v;
    onChange?.(v);
  });
  return el;
}

export function field(label, control, hint, cls) {
  return h("div", { class: ["field", cls] }, label ? h("label", null, label) : null, control, hint ? h("div", { class: "hint" }, hint) : null);
}

export function checkbox(obj, key, label, desc, onChange) {
  const input = bind(h("input", { type: "checkbox" }), obj, key, { onChange });
  return h("label", { class: "check block" }, input, h("span", null, label, desc ? h("span", { class: "desc" }, desc) : null));
}

export function toggle(checked, onChange, title) {
  const input = h("input", { type: "checkbox", checked });
  input.addEventListener("change", () => onChange(input.checked, input));
  return h("label", { class: "switch", title }, input, h("span"));
}

export function seg(options, value, onChange, cls = "") {
  const wrap = h("div", { class: `seg ${cls}`, role: "radiogroup" });
  const render = (v) => {
    clear(wrap);
    for (const o of options) {
      wrap.append(h("button", {
        type: "button", class: o.value === v ? "on" : "", role: "radio", "aria-checked": String(o.value === v),
        onClick: () => { render(o.value); onChange(o.value); },
      }, o.icon ? icon(o.icon, 15) : null, o.label));
    }
  };
  render(value);
  return wrap;
}

export function select(options, obj, key, opts = {}) {
  const el = h("select", { class: "input" }, options.map((o) =>
    typeof o === "object" ? h("option", { value: o.value }, o.label) : h("option", { value: o }, o)));
  return bind(el, obj, key, opts);
}

/** Download a blob as a file (works in the browser and in WebView2). */
export function download(filename, content, type = "text/plain") {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const a = h("a", { href: url, download: filename });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}

export function debounce(fn, ms) {
  let t;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}
