import { h, icon } from "../dom.js";
import * as api from "../api.js";

/** onShellSignIn: in the PortBridge app's own window, signs the window in again without the password. */
export function loginPage(root, status, onSuccess, onShellSignIn) {
  const pw = h("input", { class: "input", type: "password", autocomplete: "current-password", placeholder: "Admin password", autofocus: true });
  const remember = h("input", { type: "checkbox" });
  const error = h("div", { class: "errors", hidden: true });
  const btn = h("button", { class: "btn primary", type: "submit", style: { height: "38px" } }, "Sign in");
  const again = onShellSignIn
    ? h("button", { class: status.passwordSet ? "btn" : "btn primary", type: "button", style: { height: "38px" }, onClick: onShellSignIn }, "Sign in again")
    : null;

  const form = h("form", { class: "login" },
    h("div", { class: "logo" }, h("img", { src: "img/logo.svg", alt: "" }),
      h("div", null, h("h2", { style: { fontSize: "19px" } }, "PortBridge"), h("div", { class: "muted small" }, "Sign in to manage bridges"))),
    status.passwordSet
      ? [error, pw, h("label", { class: "check small" }, remember, "Keep me signed in for 30 days"), btn, again]
      : onShellSignIn
        ? [h("div", { class: "muted" }, "This window is signed out. The PortBridge app can sign it in again."), again]
        : h("div", { class: "callout" }, icon("lock", 16), h("div", null,
          h("b", null, "Browser sign-in is not set up. "),
          "Open PortBridge on the computer where it runs (Start menu or tray icon) and set an admin password under Settings → Security.",
          status.local ? " The PortBridge app on this PC signs in by itself (Windows asks for administrator approval when it opens)." : "")));

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    error.hidden = true;
    btn.disabled = true;
    try {
      await api.post("/api/auth/login", { password: pw.value, remember: remember.checked });
      onSuccess();
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
      pw.select();
    } finally {
      btn.disabled = false;
    }
  });

  root.append(h("div", { class: "login-wrap" }, form));
  setTimeout(() => pw.focus(), 30);
}
