import { h, icon, clear, replace, toast, errorToast, fmtDuration } from "./dom.js";
import * as api from "./api.js";
import { store, onChange, connectLive, disconnectLive, inDesktop, postShell, shellHello } from "./store.js";
import { dashboardPage } from "./pages/dashboard.js";
import { monitorPage } from "./pages/monitor.js";
import { portsPage } from "./pages/ports.js";
import { toolsPage } from "./pages/tools.js";
import { eventsPage } from "./pages/events.js";
import { settingsPage } from "./pages/settings.js";
import { loginPage } from "./pages/login.js";

const root = document.getElementById("root");

const NAV = [
  { path: "", label: "Bridges", icon: "grid", page: dashboardPage },
  { path: "monitor", label: "Monitor", icon: "activity", page: monitorPage },
  { path: "ports", label: "Ports", icon: "plug", page: portsPage },
  { path: "tools", label: "Tools", icon: "wrench", page: toolsPage },
  { path: "events", label: "Events", icon: "list", page: eventsPage },
  { path: "settings", label: "Settings", icon: "sliders", page: settingsPage },
];

let main = null;
let navEls = {};
let cleanup = null;
let banner = null;
let hostCard = null;

function parseHash() {
  const raw = location.hash.replace(/^#\/?/, "");
  const [path, ...rest] = raw.split("/");
  return { path: path || "", params: rest.map(decodeURIComponent) };
}

export function navigate(path) {
  location.hash = "#/" + path;
}

// ---------------- theme ----------------
function currentTheme() {
  return document.documentElement.getAttribute("data-theme") || "system";
}
function setTheme(t) {
  if (t === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", t);
  try { localStorage.setItem("pb.theme", t); } catch { /* ignore */ }
}

// ---------------- shell ----------------
function renderShell() {
  clear(root);
  navEls = {};
  const nav = h("nav", { class: "nav" }, NAV.map((n) => {
    const badge = h("span", { class: "badge err", hidden: true });
    const a = h("a", { href: "#/" + n.path }, icon(n.icon, 18), h("span", { class: "lbl" }, n.label), badge);
    navEls[n.path] = { a, badge };
    return a;
  }));

  const themeBtn = h("button", { class: "btn ghost icon sm", title: "Theme" });
  const paintTheme = () => replace(themeBtn, icon({ light: "sun", dark: "moon", system: "display" }[currentTheme()], 16));
  themeBtn.addEventListener("click", () => {
    const order = ["system", "dark", "light"];
    setTheme(order[(order.indexOf(currentTheme()) + 1) % order.length]);
    paintTheme();
    toast(`Theme: ${currentTheme()}`, "info", 1500);
  });
  paintTheme();

  const logoutBtn = h("button", {
    class: "btn ghost icon sm", title: "Sign out",
    onClick: async () => {
      await api.post("/api/auth/logout");
      disconnectLive();
      showLogin();
    },
  }, icon("logout", 16));

  hostCard = h("div", { class: "host-card" });
  main = h("main", { class: "main" });
  banner = h("div", { class: "conn-banner", hidden: true }, icon("alert", 16), "Reconnecting to PortBridge…");

  root.append(h("div", { class: "app" },
    h("aside", { class: "sidebar" },
      h("div", { class: "brand" },
        h("img", { src: "img/logo.svg", alt: "" }),
        h("div", null, h("div", { class: "name" }, "PortBridge"), h("div", { class: "sub" }, "Serial ⇄ network bridge"))),
      nav,
      h("div", { class: "spacer" }),
      hostCard,
      h("div", { class: "sidebar-footer" }, themeBtn, store.info?.mode === "desktop" && inDesktop ? null : logoutBtn)),
    h("div", { class: "main-wrap" }, banner, main)));
  paintHost();
}

function paintHost() {
  if (!hostCard || !store.info) return;
  const i = store.info;
  const mode = i.mode === "service" ? "Windows service" : i.mode === "desktop" ? "Desktop app" : "Headless";
  replace(hostCard,
    h("div", { class: "row" }, h("span", { class: ["dot", store.live ? "ok" : "waiting"] }), h("b", null, i.machine)),
    h("div", { class: "row muted" }, icon(i.mode === "service" ? "shield" : "display", 14), mode),
    h("div", { class: "row muted" }, icon("clock", 14), `Up ${fmtDuration(Date.now() - new Date(i.startedAt).getTime())}`),
    h("div", { class: "muted" }, `v${i.version}`));
}

function paintNav() {
  const { path } = parseHash();
  for (const [p, { a, badge }] of Object.entries(navEls)) {
    a.classList.toggle("active", p === path);
    if (p === "events") {
      badge.hidden = store.unseenErrors === 0 || path === "events";
      badge.textContent = store.unseenErrors > 99 ? "99+" : store.unseenErrors;
    }
  }
  if (banner) banner.hidden = store.live;
}

function route() {
  const { path, params } = parseHash();
  if (path === "login") return showLogin();
  if (!main) return;
  const entry = NAV.find((n) => n.path === path) || NAV[0];
  cleanup?.();
  cleanup = null;
  clear(main);
  if (entry.path === "events") store.unseenErrors = 0;
  paintNav();
  try {
    cleanup = entry.page(main, params, { navigate }) || null;
  } catch (e) {
    console.error(e);
    main.append(h("div", { class: "content" }, h("div", { class: "errors" }, String(e))));
  }
}

async function showLogin() {
  cleanup?.();
  cleanup = null;
  main = null;
  clear(root);
  let status = { passwordSet: false };
  try { status = await api.get("/api/auth/status"); } catch { /* offline */ }
  if (status.authenticated) {
    location.hash = "#/";
    return start();
  }
  loginPage(root, status, () => {
    location.hash = "#/";
    start();
  });
}

let started = false;
async function start() {
  try {
    store.info = await api.get("/api/info");
    store.settings = await api.get("/api/settings");
  } catch (e) {
    if (e.status === 401) return showLogin();
    clear(root);
    root.append(h("div", { class: "login-wrap" }, h("div", { class: "login" }, h("h3", null, "PortBridge is not reachable"), h("p", { class: "muted" }, e.message),
      h("button", { class: "btn primary", onClick: () => location.reload() }, "Retry"))));
    return;
  }
  renderShell();
  connectLive();
  shellHello();
  postShell({ type: "hello" });
  if (!started) {
    started = true;
    window.addEventListener("hashchange", route);
    onChange((what) => {
      if (what === "live" || what === "events" || what === "bridges") paintNav();
      if (what === "live") paintHost();
    });
    setInterval(paintHost, 30_000);
    window.addEventListener("pb:unauthorized", () => {
      disconnectLive();
      showLogin();
    });
    window.addEventListener("pb:command", async (e) => {
      try {
        if (e.detail === "startAll") await api.post("/api/bridges/start-all");
        if (e.detail === "stopAll") await api.post("/api/bridges/stop-all");
        toast(e.detail === "startAll" ? "All bridges started" : "All bridges stopped");
      } catch (err) {
        errorToast(err);
      }
    });
  }
  route();
}

// Prevent the WebView/browser from navigating away when a file is dropped outside a drop zone.
window.addEventListener("dragover", (e) => e.preventDefault());
window.addEventListener("drop", (e) => e.preventDefault());

if (parseHash().path === "login") showLogin();
else start();
