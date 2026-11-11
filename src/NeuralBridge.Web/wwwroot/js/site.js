// Site-wide progressive enhancements. Everything is delegated or polled from the DOM, so it
// keeps working after Blazor enhanced navigation and interactive re-renders.
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

// ---------- Theme (system by default; an explicit choice is remembered) ----------
function currentTheme() {
  const explicit = document.documentElement.getAttribute("data-theme");
  if (explicit) return explicit;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

document.addEventListener("click", (e) => {
  const toggle = e.target.closest("[data-theme-toggle]");
  if (!toggle) return;
  const next = currentTheme() === "dark" ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", next);
  try {
    localStorage.setItem("nb.theme", next);
  } catch {
    /* storage unavailable: the choice lasts for this page only */
  }
});

// ---------- Screen-reader announcements ----------
let liveRegion;
function announce(message) {
  if (!liveRegion || !liveRegion.isConnected) {
    liveRegion = document.createElement("div");
    liveRegion.className = "visually-hidden";
    liveRegion.setAttribute("role", "status");
    liveRegion.setAttribute("aria-live", "polite");
    document.body.appendChild(liveRegion);
  }
  liveRegion.textContent = "";
  setTimeout(() => (liveRegion.textContent = message), 30);
}

// ---------- Copy to clipboard (explicit user action only) ----------
async function writeClipboard(text) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }
  // Fallback for plain-HTTP LAN use, where the async Clipboard API is unavailable.
  const area = document.createElement("textarea");
  area.value = text;
  area.setAttribute("readonly", "");
  area.style.position = "fixed";
  area.style.opacity = "0";
  document.body.appendChild(area);
  area.select();
  const ok = document.execCommand("copy");
  area.remove();
  if (!ok) throw new Error("copy failed");
}

async function copyFrom(button) {
  let text = button.getAttribute("data-copy-text");
  const selector = button.getAttribute("data-copy-target");
  const source = selector ? document.querySelector(selector) : null;
  if (text === null && source) text = source.value ?? source.textContent;
  if (text === null) return;
  const label = button.querySelector(".copy-label");
  if (label && button._copyOriginal === undefined) button._copyOriginal = label.textContent;
  const use = button.querySelector("svg.icon use");
  if (use && button._copyHref === undefined) button._copyHref = use.getAttribute("href");
  clearTimeout(button._copyTimer);
  try {
    if (!text.length) {
      announce("Nothing to copy yet");
      return;
    }
    await writeClipboard(text);
    const done = button.getAttribute("data-copied-label") || "Copied";
    button.classList.add("is-copied");
    if (use && button._copyHref) use.setAttribute("href", button._copyHref.replace(/#[^#]+$/, "#check"));
    if (label) label.textContent = done;
    announce(done);
  } catch {
    // The browser refused (permissions, insecure context): select the text so Ctrl/⌘+C works.
    button.classList.add("is-failed");
    if (label) label.textContent = "Press Ctrl+C";
    if (source && typeof source.select === "function") {
      source.focus();
      source.select();
    }
    announce("Couldn't copy automatically. The text is selected: press Ctrl+C or ⌘+C.");
  }
  button._copyTimer = setTimeout(() => {
    button.classList.remove("is-copied", "is-failed");
    if (use && button._copyHref) use.setAttribute("href", button._copyHref);
    if (label && button._copyOriginal !== undefined) label.textContent = button._copyOriginal;
  }, 2000);
}

document.addEventListener("click", (e) => {
  const button = e.target.closest("[data-copy-text], [data-copy-target]");
  if (button && !button.disabled) copyFrom(button);
});

// Read-only fields (join link): select everything on focus so it's easy to copy by hand.
document.addEventListener("focusin", (e) => {
  if (e.target.matches?.("input[data-select-on-focus]")) e.target.select();
});

// ---------- Download the shared text as .txt (done in the browser; nothing is uploaded) ----------
function download(button) {
  const source = document.querySelector(button.getAttribute("data-download-target"));
  if (!source) return;
  const text = source.value ?? source.textContent ?? "";
  if (!text.length) {
    announce("Nothing to download yet");
    return;
  }
  const stamp = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  const name = `${button.getAttribute("data-download-name") || "neuralbridge"}-${stamp.getFullYear()}${pad(stamp.getMonth() + 1)}${pad(stamp.getDate())}-${pad(stamp.getHours())}${pad(stamp.getMinutes())}.txt`;
  const url = URL.createObjectURL(new Blob([text], { type: "text/plain;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.hidden = true;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  announce(`Downloaded ${name}`);
}
document.addEventListener("click", (e) => {
  const button = e.target.closest("[data-download-target]");
  if (button && !button.disabled) download(button);
});

// ---------- Countdowns and relative times ----------
const stampFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const relFmt = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

function formatCountdown(ms) {
  if (ms <= 0) return "0:00";
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

function tick() {
  const now = Date.now();
  document.querySelectorAll("[data-countdown-to]").forEach((el) => {
    const end = Date.parse(el.getAttribute("data-countdown-to"));
    if (Number.isNaN(end)) return;
    const left = end - now;
    el.textContent = formatCountdown(left);
    el.classList.toggle("is-ending", left > 0 && left < 5 * 60 * 1000);
    el.setAttribute("datetime", new Date(end).toISOString());
    el.setAttribute("title", `Ends ${stampFmt.format(new Date(end))}`);
  });
  document.querySelectorAll("[data-relative-time]").forEach((el) => {
    const t = Date.parse(el.getAttribute("data-relative-time"));
    if (Number.isNaN(t)) return;
    const secs = Math.round((t - now) / 1000);
    el.textContent =
      Math.abs(secs) < 5 ? "just now"
      : Math.abs(secs) < 60 ? relFmt.format(secs, "second")
      : Math.abs(secs) < 3600 ? relFmt.format(Math.round(secs / 60), "minute")
      : relFmt.format(Math.round(secs / 3600), "hour");
    el.setAttribute("title", stampFmt.format(new Date(t)));
  });
  document.querySelectorAll("[data-local-time]").forEach((el) => {
    const t = Date.parse(el.getAttribute("data-local-time"));
    if (!Number.isNaN(t)) el.textContent = stampFmt.format(new Date(t));
  });
}
tick();
setInterval(tick, 1000);

// ---------- Blazor error UI dismiss (no inline handlers under CSP) ----------
document.addEventListener("click", (e) => {
  if (e.target.closest("#blazor-error-ui .dismiss")) {
    document.getElementById("blazor-error-ui").style.display = "none";
  }
});

// ---------- Tabs (landing: Create / Join) ----------
function selectTab(container, name, { focusInput = false } = {}) {
  const tabs = [...container.querySelectorAll('[role="tab"]')];
  const tab = tabs.find((t) => t.dataset.tab === name);
  if (!tab) return false;
  tabs.forEach((t) => {
    const on = t === tab;
    t.setAttribute("aria-selected", on ? "true" : "false");
    t.tabIndex = on ? 0 : -1;
    const panel = document.getElementById(t.getAttribute("aria-controls"));
    if (panel) panel.hidden = !on;
  });
  if (focusInput) focusFirstField(document.getElementById(tab.getAttribute("aria-controls")));
  return true;
}

// The forms are interactive islands that may still be starting (disabled fieldset), and
// <FocusOnNavigate> moves focus to the <h1> after navigation: retry briefly, unless the
// user has focused something else in the meantime.
function focusFirstField(panel) {
  if (!panel) return;
  clearInterval(focusFirstField.timer);
  let tries = 0;
  const start = document.activeElement;
  const apply = () => {
    const input = panel.querySelector("input:not([type=radio]):not([disabled])");
    const active = document.activeElement;
    if (active === input) return clearInterval(focusFirstField.timer);
    // Stop as soon as focus lands anywhere else on purpose (Blazor's own move to the <h1> doesn't count).
    const userMoved = active && active !== start && active !== document.body && active.tagName !== "H1" && !panel.contains(active);
    if (++tries > 30 || userMoved) return clearInterval(focusFirstField.timer);
    if (!input) return;
    input.focus({ preventScroll: true });
    if (document.activeElement === input) {
      clearInterval(focusFirstField.timer);
      if (input.getBoundingClientRect().top < 0 || input.getBoundingClientRect().bottom > window.innerHeight) {
        input.scrollIntoView({ block: "center", behavior: reduceMotion.matches ? "auto" : "smooth" });
      }
    }
  };
  apply();
  focusFirstField.timer = setInterval(apply, 100);
}

document.addEventListener("click", (e) => {
  const tab = e.target.closest('[data-tabs] [role="tab"]');
  if (tab) {
    clearInterval(focusFirstField.timer);
    selectTab(tab.closest("[data-tabs]"), tab.dataset.tab);
    history.replaceState(null, "", `#${tab.getAttribute("aria-controls")}`);
    return;
  }
  const link = e.target.closest("[data-tab-link]");
  const container = document.querySelector("[data-tabs]");
  if (link && container) {
    e.preventDefault();
    selectTab(container, link.dataset.tabLink, { focusInput: true });
    history.replaceState(null, "", `#${link.dataset.tabLink}`);
  }
});
document.addEventListener("keydown", (e) => {
  const tab = e.target.closest?.('[data-tabs] [role="tab"]');
  if (!tab || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) return;
  const tabs = [...tab.closest('[role="tablist"]').querySelectorAll('[role="tab"]')];
  clearInterval(focusFirstField.timer); // the person is steering focus themselves
  let i = tabs.indexOf(tab);
  i = e.key === "Home" ? 0 : e.key === "End" ? tabs.length - 1 : (i + (e.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
  e.preventDefault();
  tabs[i].focus();
  selectTab(tab.closest("[data-tabs]"), tabs[i].dataset.tab);
});

// "#join" / "#create" (also the older "#create-name") open the matching tab and focus its first field.
function syncTabsWithHash() {
  const container = document.querySelector("[data-tabs]");
  if (!container) return;
  const hash = location.hash.replace("#", "");
  const name = hash === "create-name" ? "create" : hash;
  if (name === "create" || name === "join") selectTab(container, name, { focusInput: true });
}
window.addEventListener("hashchange", syncTabsWithHash);
onEnhancedLoad(() => setTimeout(syncTabsWithHash, 0));
if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", syncTabsWithHash);
else syncTabsWithHash();

// ---------- Tooltips: one accessible floating tooltip for every [data-tip] (mouse, keyboard, touch) ----------
const tip = document.createElement("div");
tip.className = "tooltip";
tip.id = "nb-tooltip";
tip.setAttribute("role", "tooltip");
tip.hidden = true;
document.body.appendChild(tip);
let tipOwner = null;
let pressTimer = 0;

function showTip(el) {
  const text = (el.disabled && el.getAttribute("data-tip-disabled")) || el.getAttribute("data-tip");
  if (!text) return;
  // Blazor's enhanced navigation re-syncs <body> and can drop nodes it didn't render.
  if (!tip.isConnected) document.body.appendChild(tip);
  tipOwner = el;
  tip.textContent = text;
  tip.hidden = false;
  el.setAttribute("aria-describedby", tip.id);
  const r = el.getBoundingClientRect();
  const t = tip.getBoundingClientRect();
  let top = r.top - t.height - 8;
  if (top < 8) top = r.bottom + 8;
  const left = Math.min(window.innerWidth - t.width - 8, Math.max(8, r.left + r.width / 2 - t.width / 2));
  tip.style.top = `${top}px`;
  tip.style.left = `${left}px`;
  tip.classList.add("is-visible");
}

function hideTip() {
  if (tipOwner) tipOwner.removeAttribute("aria-describedby");
  tipOwner = null;
  tip.classList.remove("is-visible");
  tip.hidden = true;
}

document.addEventListener("pointerover", (e) => {
  const el = e.target.closest("[data-tip]");
  if (el && e.pointerType === "mouse") showTip(el);
});
document.addEventListener("pointerout", (e) => {
  if (tipOwner && !tipOwner.contains(e.relatedTarget)) hideTip();
});
document.addEventListener("focusin", (e) => {
  const el = e.target.closest("[data-tip]");
  if (el && el.matches(":focus-visible")) showTip(el);
});
document.addEventListener("focusout", hideTip);
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") hideTip();
});
// Touch: long-press shows the tooltip without triggering the action.
document.addEventListener(
  "touchstart",
  (e) => {
    const el = e.target.closest("[data-tip]");
    if (!el) return;
    pressTimer = setTimeout(() => {
      showTip(el);
      setTimeout(hideTip, 1800);
    }, 450);
  },
  { passive: true },
);
document.addEventListener("touchend", () => clearTimeout(pressTimer), { passive: true });
window.addEventListener("scroll", hideTip, { passive: true });

// ---------- Loading state for redirecting forms (e.g. Continue with Google) ----------
document.addEventListener("submit", (e) => {
  const form = e.target.closest("form[data-loading-text]");
  if (!form || e.defaultPrevented) return;
  const button = form.querySelector("button[type=submit]");
  if (!button) return;
  const label = button.querySelector(".btn-label");
  if (label) label.textContent = form.getAttribute("data-loading-text");
  button.classList.add("is-loading");
  button.setAttribute("aria-busy", "true");
  // Disable after the submit has been dispatched, so the form still posts.
  setTimeout(() => (button.disabled = true), 0);
});
// Back-navigation from the provider restores the page from bfcache: undo the loading state.
window.addEventListener("pageshow", (e) => {
  if (!e.persisted) return;
  document.querySelectorAll("button.is-loading").forEach((b) => {
    b.classList.remove("is-loading");
    b.disabled = false;
    b.removeAttribute("aria-busy");
  });
});

// ---------- Menus (<details> dropdowns): close on outside click, Escape and navigation ----------
document.addEventListener("click", (e) => {
  document.querySelectorAll("details[data-menu][open]").forEach((d) => {
    if (!d.contains(e.target)) d.removeAttribute("open");
  });
});
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  document.querySelectorAll("details[data-menu][open]").forEach((d) => {
    d.removeAttribute("open");
    d.querySelector("summary")?.focus();
  });
});
document.addEventListener(
  "toggle",
  (e) => {
    const d = e.target;
    if (d.matches?.("details[data-menu]") && d.open) {
      d.querySelector("[role=menu] a, [role=menu] button")?.focus({ preventScroll: true });
    }
  },
  true,
);
function closeMenus() {
  document.querySelectorAll("details[data-menu][open]").forEach((d) => d.removeAttribute("open"));
}
onEnhancedLoad(closeMenus);

// ---------- Avatars: fall back to initials when the picture can't load ----------
// "error" doesn't bubble, so listen in the capture phase (covers images added later by Blazor too).
document.addEventListener(
  "error",
  (e) => {
    if (e.target instanceof HTMLImageElement && e.target.hasAttribute("data-avatar-img")) e.target.remove();
  },
  true,
);

// ---------- Workspace side panels: collapsed on small screens so the editor comes first ----------
const compact = window.matchMedia("(max-width: 900px)");
function collapseForSmallScreens(root = document) {
  root.querySelectorAll?.("details[data-collapse-mobile]:not([data-collapse-applied])").forEach((d) => {
    d.setAttribute("data-collapse-applied", "");
    if (compact.matches) d.removeAttribute("open");
  });
}

// ---------- QR code of the join link (drawn locally when its panel is opened) ----------
let qrModule;
async function renderQr(details) {
  const url = details.getAttribute("data-qr");
  const target = details.querySelector(".qr-target");
  if (!url || !target || !details.open || target.dataset.renderedFor === url) return;
  qrModule ??= import("./vendor/qrcode.mjs");
  const { default: qrcode } = await qrModule;
  const qr = qrcode(0, "M");
  qr.addData(url);
  qr.make();
  const n = qr.getModuleCount();
  const quiet = 2;
  const size = n + quiet * 2;
  let path = "";
  for (let r = 0; r < n; r++) {
    for (let c = 0; c < n; c++) {
      if (qr.isDark(r, c)) path += `M${c + quiet} ${r + quiet}h1v1h-1z`;
    }
  }
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", `0 0 ${size} ${size}`);
  svg.setAttribute("shape-rendering", "crispEdges");
  svg.setAttribute("aria-hidden", "true");
  const bg = document.createElementNS(ns, "rect");
  bg.setAttribute("width", size);
  bg.setAttribute("height", size);
  bg.setAttribute("fill", "#fff");
  const fg = document.createElementNS(ns, "path");
  fg.setAttribute("d", path);
  fg.setAttribute("fill", "#0f172a");
  svg.append(bg, fg);
  target.replaceChildren(svg);
  target.dataset.renderedFor = url;
}
document.addEventListener(
  "toggle",
  (e) => {
    if (e.target.matches?.("details[data-qr]")) renderQr(e.target);
  },
  true,
);

// Blazor renders the workspace after this module runs (and re-renders it when the session ID
// changes), so watch for new panels and changed links.
new MutationObserver((records) => {
  for (const r of records) {
    if (r.type === "attributes" && r.target.matches?.("details[data-qr]")) renderQr(r.target);
    r.addedNodes?.forEach((node) => {
      if (node.nodeType !== 1) return;
      collapseForSmallScreens(node.parentElement || node);
      node.querySelectorAll?.("details[data-qr][open]").forEach(renderQr);
      if (node.matches?.("details[data-qr][open]")) renderQr(node);
    });
  }
}).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-qr", "open"] });

// ---------- Workspace keyboard shortcuts ----------
// Single keys only act when focus is not in a text field; Ctrl/⌘+Enter copies all text from
// anywhere in the workspace (it has no default meaning in a textarea). Browser shortcuts
// such as Ctrl+C, Ctrl+S or Ctrl+Shift+C are never intercepted.
function isTyping(el) {
  return !!el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
}
function openShortcuts() {
  const dialog = document.getElementById("shortcuts");
  if (dialog && !dialog.open) dialog.showModal();
}
document.addEventListener("click", (e) => {
  if (e.target.closest("[data-shortcuts-open]")) openShortcuts();
});
document.addEventListener("keydown", (e) => {
  const workspace = document.querySelector('.workspace[data-view="ready"]');
  if (!workspace || e.defaultPrevented) return;
  const editor = document.getElementById("shared-editor");
  if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key === "Enter") {
    const copy = workspace.querySelector('[data-copy-target="#shared-editor"]');
    if (copy) {
      e.preventDefault();
      copyFrom(copy);
    }
    return;
  }
  if (e.key === "Escape" && document.activeElement === editor) {
    editor.blur();
    return;
  }
  if (e.ctrlKey || e.metaKey || e.altKey || isTyping(document.activeElement) || document.querySelector("dialog[open], .dialog")) return;
  const key = e.key.toLowerCase();
  const click = (selector) => {
    const el = workspace.querySelector(selector);
    if (el && !el.disabled) {
      e.preventDefault();
      el.click();
    }
  };
  if (key === "e" && editor) {
    e.preventDefault();
    editor.focus();
  } else if (key === "c") click('[data-copy-target="#shared-editor"]');
  else if (key === "l") click('[data-copy-target="#share-url"]');
  else if (key === "d") click("[data-download-target]");
  else if (e.key === "?") {
    e.preventDefault();
    openShortcuts();
  }
});

/** Runs after each Blazor enhanced navigation (Blazor may finish starting after this module). */
function onEnhancedLoad(callback) {
  const hook = () => {
    if (typeof window.Blazor?.addEventListener !== "function") return false;
    window.Blazor.addEventListener("enhancedload", callback);
    return true;
  };
  if (!hook()) document.addEventListener("DOMContentLoaded", hook, { once: true });
}
