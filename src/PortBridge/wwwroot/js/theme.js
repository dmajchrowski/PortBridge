// Applied before first paint so the page never flashes the wrong theme.
(function () {
  try {
    var t = localStorage.getItem("pb.theme");
    if (t === "light" || t === "dark") document.documentElement.setAttribute("data-theme", t);
  } catch (e) { /* storage unavailable */ }
})();
