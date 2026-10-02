// Runs before first paint: applies a saved theme choice (if any) to avoid a flash.
(function () {
  try {
    var saved = window.localStorage.getItem("nb.theme");
    if (saved === "light" || saved === "dark") {
      document.documentElement.setAttribute("data-theme", saved);
    }
  } catch (e) {
    /* storage unavailable: follow the system preference */
  }
})();
