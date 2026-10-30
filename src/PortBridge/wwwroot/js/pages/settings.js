import { h, icon, replace, clear, toast, errorToast, confirmDialog, bind, field, toggle, download } from "../dom.js";
import * as api from "../api.js";
import { store, onChange, inDesktop, postShell, shellHello } from "../store.js";
import { frame } from "./common.js";

function setting(title, desc, control) {
  return h("div", { class: "setting" }, h("div", null, h("div", { class: "s-title" }, title), desc ? h("div", { class: "s-desc" }, desc) : null), control);
}

function section(iconName, title, ...rows) {
  return h("section", { class: "panel" }, h("div", { class: "panel-head" }, icon(iconName, 17), h("h3", null, title)), h("div", null, rows));
}

export function settingsPage(container) {
  const s = structuredClone(store.settings || {});
  let dirty = false;
  const saveBtn = h("button", { class: "btn primary", disabled: true, onClick: save }, icon("save", 15), "Save changes");
  const markDirty = () => { dirty = true; saveBtn.disabled = false; };
  const f = frame(container, { title: "Settings", subtitle: "How PortBridge runs, who can manage it, and where it reports problems", actions: [saveBtn] });
  const info = store.info || {};
  const body = h("div", { class: "settings" });
  f.content.append(body);

  const sw = (key, onExtra) => toggle(!!s[key], (v) => { s[key] = v; markDirty(); onExtra?.(v); });

  // ---------------- running mode ----------------
  const modeBox = h("div");
  const paintMode = () => {
    const shell = store.shell;
    const isService = info.mode === "service";
    const rows = [];
    rows.push(setting(
      isService ? "Running as a Windows service" : info.mode === "desktop" ? "Running inside the desktop app" : "Running headless",
      isService
        ? "Bridges start with Windows and keep running when nobody is signed in."
        : "Bridges stop when you exit the app. Install the service to keep them running in the background, before anyone signs in.",
      inDesktop && shell
        ? (isService
          ? h("button", { class: "btn danger", onClick: () => postShell({ type: "uninstallService" }) }, "Remove service")
          : h("button", { class: "btn primary", onClick: () => postShell({ type: "installService" }) }, icon("shield", 15), "Install as service"))
        : h("span", { class: "badge" }, isService ? "service" : info.mode)));
    if (inDesktop && shell) {
      rows.push(setting("Start the app when I sign in", "Opens PortBridge minimized to the tray.",
        toggle(!!shell.startWithWindows, (v) => postShell({ type: "startWithWindows", enabled: v }))));
    }
    if (inDesktop) {
      rows.push(setting("Closing the window keeps PortBridge in the tray", "Otherwise closing the window exits the app.", sw("closeToTray")));
      rows.push(setting("Desktop notifications", "Show a tray notification when a bridge reports an error.", sw("desktopNotifications")));
    }
    replace(modeBox, section(isServiceIcon(isService), "Running mode", ...rows));
  };
  const isServiceIcon = (svc) => (svc ? "shield" : "display");
  paintMode();

  // ---------------- dashboard access ----------------
  const urls = h("div", { class: "s-desc mono" });
  const paintUrls = () => {
    const scheme = s.useHttps ? "https" : "http";
    const list = [`${scheme}://localhost:${s.webPort}`];
    if (s.allowRemote) for (const a of info.addresses || []) list.push(`${scheme}://${a}:${s.webPort}`);
    replace(urls, list.map((u) => h("div", null, u)));
  };
  paintUrls();
  const remoteSwitch = toggle(!!s.allowRemote, (v, input) => {
    if (v && !s.passwordSet) {
      input.checked = false;
      toast("Set an admin password first (Security, below).", "warn");
      return;
    }
    s.allowRemote = v;
    markDirty();
    paintUrls();
  });
  const access = section("globe", "Dashboard access",
    setting("Dashboard port", "TCP port of this web dashboard.",
      bind(h("input", { class: "input", type: "number", min: 1, max: 65535, style: { width: "110px" } }), s, "webPort", { type: "number", onChange: () => { markDirty(); paintUrls(); } })),
    setting("Allow access from other computers", "Manage PortBridge from any browser on the network. Requires the admin password.", remoteSwitch),
    setting("Use HTTPS", "Encrypts the dashboard with an automatically generated certificate (browsers will warn that it is self-signed).",
      sw("useHttps", () => paintUrls())),
    h("div", { class: "setting stacked" }, h("div", null, h("div", { class: "s-title" }, "Addresses"), urls)));

  // ---------------- security ----------------
  const security = h("div");
  const paintSecurity = () => {
    const cur = h("input", { class: "input", type: "password", autocomplete: "current-password", placeholder: "Current password" });
    const p1 = h("input", { class: "input", type: "password", autocomplete: "new-password", placeholder: "New password (8+ characters)" });
    const p2 = h("input", { class: "input", type: "password", autocomplete: "new-password", placeholder: "Repeat new password" });
    const setBtn = h("button", { class: "btn primary" }, s.passwordSet ? "Change password" : "Set password");
    setBtn.addEventListener("click", async () => {
      if (p1.value.length < 8) return toast("Use at least 8 characters.", "warn");
      if (p1.value !== p2.value) return toast("The passwords don't match.", "warn");
      try {
        await api.post("/api/settings/password", { current: cur.value, newPassword: p1.value });
        s.passwordSet = true;
        store.settings.passwordSet = true;
        toast("Password saved");
        paintSecurity();
      } catch (e) { errorToast(e); }
    });
    const removeBtn = s.passwordSet ? h("button", {
      class: "btn danger",
      onClick: async () => {
        if (!(await confirmDialog({ title: "Remove the password?", message: "Browser sign-in will no longer be possible; only the desktop app on this PC can manage PortBridge.", confirm: "Remove", danger: true }))) return;
        try {
          await api.post("/api/settings/password", { current: cur.value, newPassword: "" });
          s.passwordSet = false;
          store.settings.passwordSet = false;
          s.requirePasswordLocally = false;
          toast("Password removed");
          paintSecurity();
        } catch (e) { errorToast(e); }
      },
    }, "Remove") : null;

    replace(security, section("lock", "Security",
      h("div", { class: "setting stacked" },
        h("div", null,
          h("div", { class: "s-title" }, "Admin password ", s.passwordSet ? h("span", { class: "badge ok" }, "set") : h("span", { class: "badge warn" }, "not set")),
          h("div", { class: "s-desc" }, "Needed to sign in from a browser. The PortBridge app on this PC signs in by itself: it always runs as administrator.")),
        h("div", { class: "grid-3" }, s.passwordSet ? cur : null, p1, p2),
        h("div", { class: "row" }, setBtn, removeBtn)),
      setting("Ask for the password in the desktop app too", "Stops other people at this PC from changing bridges (like TCP-Com's password protection).",
        toggle(!!s.requirePasswordLocally, (v, input) => {
          if (v && !s.passwordSet) { input.checked = false; return toast("Set a password first.", "warn"); }
          s.requirePasswordLocally = v;
          markDirty();
        }))));
  };
  paintSecurity();

  // ---------------- notifications ----------------
  const hookInput = bind(h("input", { class: "input", placeholder: "https://hooks.example.com/…" }), s, "webhookUrl", { onChange: markDirty });
  const cmdInput = bind(h("input", { class: "input mono", placeholder: 'e.g. powershell -File C:\\scripts\\alert.ps1 "%BRIDGE%" "%MESSAGE%"' }), s, "onErrorCommand", { onChange: markDirty });
  const notifications = section("bell", "Alerts & automation",
    h("div", { class: "setting stacked" },
      h("div", null, h("div", { class: "s-title" }, "Webhook"),
        h("div", { class: "s-desc" }, "PortBridge POSTs JSON ({\"text\", \"level\", \"bridge\", \"message\", …}) for warnings and errors. Works with Slack-style incoming webhooks, Teams workflows, n8n, Node-RED, etc.")),
      h("div", { class: "row" }, hookInput, h("button", {
        class: "btn",
        onClick: async () => {
          if (!s.webhookUrl) return;
          try { await api.post("/api/settings/test-webhook", { url: s.webhookUrl }); toast("Test notification sent"); } catch (e) { errorToast(e); }
        },
      }, "Send test")),
      h("label", { class: "check" }, bind(h("input", { type: "checkbox" }), s, "webhookIncludeConnections", { onChange: markDirty }), "Also notify on connects and disconnects")),
    h("div", { class: "setting stacked" },
      h("div", null, h("div", { class: "s-title" }, "Run a command when an error occurs"),
        h("div", { class: "s-desc" }, "Like TCP-Com's error command line. %BRIDGE% and %MESSAGE% are replaced; PORTBRIDGE_BRIDGE / _MESSAGE / _LEVEL / _TIME are set as environment variables. Runs at most once a minute per bridge. As a Windows service it runs as SYSTEM.")),
      cmdInput,
      // Commands (and credentials) found in a folder non-administrators could edit are set aside/reset by the service,
      // never used silently; the note also explains password/remote-access resets.
      s.quarantineNote || s.quarantinedOnErrorCommand || s.quarantinedCom0ComPath ? h("div", { class: "callout" }, icon("shield", 16), h("div", null,
        h("b", null, "Set aside for review: "), s.quarantineNote || "",
        s.quarantinedOnErrorCommand ? h("div", null, "On-error command: ", h("code", null, s.quarantinedOnErrorCommand)) : null,
        s.quarantinedCom0ComPath ? h("div", null, "com0com location: ", h("code", null, s.quarantinedCom0ComPath)) : null,
        h("div", { class: "muted small" }, "Copy a value into its field only if you recognise it. Saving settings clears this notice."))) : null));

  // ---------------- logging ----------------
  const logging = section("list", "Logging",
    setting("Write the event log to disk", `One file per day in ${info.logDir || "the logs folder"} — every user of this PC can open it.`, sw("writeEventLogFile")),
    setting("Keep log files for (days)", null,
      bind(h("input", { class: "input", type: "number", min: 1, style: { width: "110px" } }), s, "logRetentionDays", { type: "number", onChange: markDirty })),
    inDesktop ? setting("Folders", [h("div", null, `Logs: ${info.logDir}`), h("div", null, `Data: ${info.dataDir}`)], h("div", { class: "row" },
      h("button", { class: "btn", onClick: () => postShell({ type: "openFolder", which: "logs" }) }, icon("folder", 15), "Logs"),
      h("button", { class: "btn", onClick: () => postShell({ type: "openFolder", which: "data" }) }, icon("folder", 15), "Data"))) : null);

  // ---------------- backup ----------------
  const fileInput = h("input", { type: "file", accept: ".json,application/json", hidden: true });
  fileInput.addEventListener("change", async () => {
    const file = fileInput.files[0];
    fileInput.value = "";
    if (!file) return;
    const replaceAll = await confirmDialog({
      title: `Import “${file.name}”`,
      message: "Replace: the current bridges and settings are replaced by the file.\nAdd: the file's bridges are added (stopped) next to the existing ones.",
      confirm: "Replace everything", cancel: "Add bridges",
    });
    try {
      const r = await api.api("POST", `/api/config/import?mode=${replaceAll ? "replace" : "merge"}`, await file.text(), { raw: true });
      toast(`Imported ${r.imported} bridge${r.imported === 1 ? "" : "s"}${r.warnings?.length ? " — check them for port conflicts" : ""}`);
    } catch (e) {
      errorToast(e.errors?.length ? `${e.message} ${e.errors.slice(0, 3).join(" ")}` : e);
    }
  });
  const backup = section("download", "Backup & restore",
    setting("Export configuration", "All bridges and settings as a JSON file (without the password).",
      h("button", {
        class: "btn",
        onClick: async () => {
          try {
            const res = await fetch("/api/config/export", { credentials: "same-origin" });
            if (!res.ok) throw new Error(`Export failed (${res.status})`);
            const name = (res.headers.get("content-disposition") || "").match(/filename="?([^";]+)/)?.[1] || "portbridge-config.json";
            download(name, await res.text(), "application/json");
          } catch (e) { errorToast(e); }
        },
      }, icon("download", 15), "Export")),
    setting("Import configuration", "Restore a backup or copy bridges from another PortBridge.",
      h("div", null, fileInput, h("button", { class: "btn", onClick: () => fileInput.click() }, icon("upload", 15), "Import…"))));

  // ---------------- advanced ----------------
  const advanced = section("cpu", "Advanced",
    setting("com0com location", "Folder containing setupc.exe, if com0com is installed somewhere unusual.",
      bind(h("input", { class: "input", placeholder: "Detected automatically", style: { width: "320px" } }), s, "com0ComPath", { onChange: markDirty })));

  // ---------------- about ----------------
  const about = section("info", "About",
    h("div", { class: "setting stacked small" },
      h("div", { class: "grid-2" },
        h("div", null, h("div", { class: "muted" }, "Version"), `PortBridge ${info.version}`),
        h("div", null, h("div", { class: "muted" }, "Computer"), info.machine),
        h("div", null, h("div", { class: "muted" }, "Data folder"), h("span", { class: "mono" }, info.dataDir)),
        h("div", null, h("div", { class: "muted" }, "Virtual port driver"), info.com0com?.installed ? "com0com" : "not installed"))));

  body.append(modeBox, access, security, notifications, logging, backup, advanced, about);

  async function save() {
    saveBtn.disabled = true;
    try {
      const r = await api.put("/api/settings", s);
      store.settings = r.settings;
      Object.assign(s, r.settings);
      dirty = false;
      shellHello();
      if (r.restartRequired) {
        const ok = await confirmDialog({
          title: "Restart the dashboard now?",
          message: "The new address, HTTPS or remote-access setting takes effect after the dashboard restarts. Bridges keep running.",
          confirm: "Restart dashboard",
        });
        if (ok) {
          const target = await api.post("/api/host/restart-web");
          toast("Dashboard restarting…", "info");
          if (inDesktop) postShell({ type: "reconnect" });
          else setTimeout(() => { location.href = `${target.https ? "https" : "http"}://${location.hostname}:${target.port}/`; }, 1500);
        } else {
          toast("Saved — restart the dashboard later to apply the address change.", "info", 5000);
        }
      } else {
        toast("Settings saved");
      }
    } catch (e) {
      errorToast(e);
      saveBtn.disabled = false;
    }
  }

  const off = onChange((what) => what === "shell" && paintMode());
  return () => {
    off();
    if (dirty) toast("Unsaved settings were discarded.", "warn");
  };
}
