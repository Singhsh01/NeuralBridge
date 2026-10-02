// Site-wide progressive enhancements. Everything is delegated or polled from the DOM, so it
// keeps working after Blazor enhanced navigation and interactive re-renders.
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

// ---------- Theme ----------
function currentTheme() {
  const explicit = document.documentElement.getAttribute("data-theme");
  if (explicit) return explicit;
  return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

document.addEventListener("click", (e) => {
  const toggle = e.target.closest("[data-theme-toggle]");
  if (!toggle) return;
  const next = currentTheme() === "dark" ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", next);
  try {
    localStorage.setItem("nb.theme", next);
  } catch {
    /* ignore */
  }
});

// ---------- Copy to clipboard (explicit click only) ----------
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

let liveRegion;
function announce(message) {
  if (!liveRegion) {
    liveRegion = document.createElement("div");
    liveRegion.className = "visually-hidden";
    liveRegion.setAttribute("role", "status");
    liveRegion.setAttribute("aria-live", "polite");
    document.body.appendChild(liveRegion);
  }
  liveRegion.textContent = "";
  setTimeout(() => (liveRegion.textContent = message), 30);
}

document.addEventListener("click", async (e) => {
  const button = e.target.closest("[data-copy-text], [data-copy-target]");
  if (!button || button.disabled) return;
  let text = button.getAttribute("data-copy-text");
  const selector = button.getAttribute("data-copy-target");
  if (text === null && selector) {
    const source = document.querySelector(selector);
    text = source ? source.value ?? source.textContent : "";
  }
  if (text === null) return;
  const label = button.querySelector(".copy-label");
  const original = label ? label.textContent : null;
  const use = button.querySelector("svg.icon use");
  const originalHref = use ? use.getAttribute("href") : null;
  clearTimeout(button._copyTimer);
  try {
    await writeClipboard(text);
    const done = button.getAttribute("data-copied-label") || "Copied";
    button.classList.add("is-copied");
    if (use && originalHref) use.setAttribute("href", originalHref.replace(/#[^#]+$/, "#check"));
    if (label) label.textContent = done;
    announce(text.length ? done : "Nothing to copy yet");
  } catch {
    button.classList.add("is-failed");
    if (label) label.textContent = "Copy failed";
    announce("Copy failed. Select the text and copy it manually.");
  }
  button._copyTimer = setTimeout(() => {
    button.classList.remove("is-copied", "is-failed");
    if (use && originalHref) use.setAttribute("href", originalHref);
    if (label && original !== null) label.textContent = original;
  }, 1800);
});

// ---------- Local time, countdowns, relative times ----------
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" });
const dateFmt = new Intl.DateTimeFormat(undefined, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
const stampFmt = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const relFmt = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

function skyPhase(hour) {
  if (hour >= 5 && hour < 8) return "dawn";
  if (hour >= 8 && hour < 17) return "day";
  if (hour >= 17 && hour < 20) return "dusk";
  return "night";
}

function formatCountdown(ms) {
  if (ms <= 0) return "0:00";
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

function tick() {
  const now = new Date();
  document.querySelectorAll("[data-local-clock]").forEach((el) => {
    el.textContent = timeFmt.format(now);
    el.setAttribute("datetime", now.toISOString());
  });
  document.querySelectorAll("[data-local-date]").forEach((el) => (el.textContent = dateFmt.format(now)));
  document.querySelectorAll("[data-local-zone]").forEach((el) => {
    if (!el.dataset.filled) {
      el.textContent = Intl.DateTimeFormat().resolvedOptions().timeZone.replace(/_/g, " ");
      el.dataset.filled = "1";
    }
  });
  document.querySelectorAll("[data-sky]").forEach((el) => (el.dataset.sky = skyPhase(now.getHours())));
  document.querySelectorAll("[data-countdown-to]").forEach((el) => {
    const end = Date.parse(el.getAttribute("data-countdown-to"));
    if (Number.isNaN(end)) return;
    const left = end - now.getTime();
    el.textContent = formatCountdown(left);
    el.classList.toggle("is-ending", left > 0 && left < 5 * 60 * 1000);
    el.setAttribute("title", `Ends ${stampFmt.format(new Date(end))}`);
  });
  document.querySelectorAll("[data-relative-time]").forEach((el) => {
    const t = Date.parse(el.getAttribute("data-relative-time"));
    if (Number.isNaN(t)) return;
    const secs = Math.round((t - now.getTime()) / 1000);
    el.textContent = Math.abs(secs) < 5 ? "just now" : Math.abs(secs) < 60 ? relFmt.format(secs, "second") : relFmt.format(Math.round(secs / 60), "minute");
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

// ---------- Wallpaper (fade in when decoded, particles + parallax on the landing page) ----------
let landingCleanup = null;
function revealWallpaper() {
  document.querySelectorAll("img[data-wallpaper]").forEach((img) => {
    const done = () => img.closest(".backdrop")?.classList.add("is-loaded");
    if (img.complete && img.naturalWidth > 0) {
      (img.decode ? img.decode().catch(() => {}) : Promise.resolve()).then(done);
    } else {
      img.addEventListener("load", done, { once: true });
      img.addEventListener("error", done, { once: true });
    }
  });
}

async function initLanding() {
  revealWallpaper();
  if (landingCleanup) {
    landingCleanup();
    landingCleanup = null;
  }
  if (!document.querySelector("[data-particles]")) return;
  const { startLanding } = await import("./landing.js");
  landingCleanup = startLanding({ reduceMotion: reduceMotion.matches });
}
initLanding();
onEnhancedLoad(initLanding);
reduceMotion.addEventListener("change", initLanding);

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
  let top = r.top - t.height - 10;
  let placement = "top";
  if (top < 8) {
    top = r.bottom + 10;
    placement = "bottom";
  }
  const left = Math.min(window.innerWidth - t.width - 8, Math.max(8, r.left + r.width / 2 - t.width / 2));
  tip.style.top = `${top}px`;
  tip.style.left = `${left}px`;
  tip.dataset.placement = placement;
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
document.addEventListener("touchstart", (e) => {
  const el = e.target.closest("[data-tip]");
  if (!el) return;
  pressTimer = setTimeout(() => {
    showTip(el);
    setTimeout(hideTip, 1800);
  }, 450);
}, { passive: true });
document.addEventListener("touchend", () => clearTimeout(pressTimer), { passive: true });
window.addEventListener("scroll", hideTip, { passive: true });

// ---------- Press feedback: ripple from the pointer (or the centre for keyboard) ----------
function ripple(el, x, y) {
  if (reduceMotion.matches) return;
  const r = el.getBoundingClientRect();
  const dot = document.createElement("span");
  dot.className = "ripple";
  const size = Math.max(r.width, r.height) * 2;
  dot.style.width = dot.style.height = `${size}px`;
  dot.style.left = `${(x ?? r.left + r.width / 2) - r.left - size / 2}px`;
  dot.style.top = `${(y ?? r.top + r.height / 2) - r.top - size / 2}px`;
  el.appendChild(dot);
  dot.addEventListener("animationend", () => dot.remove(), { once: true });
}
document.addEventListener("pointerdown", (e) => {
  const el = e.target.closest(".btn, .icon-btn");
  if (el && !el.disabled) ripple(el, e.clientX, e.clientY);
});
document.addEventListener("keydown", (e) => {
  if ((e.key === "Enter" || e.key === " ") && e.target.matches?.(".btn, .icon-btn") && !e.target.disabled) ripple(e.target);
});

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
document.addEventListener("toggle", (e) => {
  const d = e.target;
  if (d.matches?.("details[data-menu]") && d.open) {
    d.querySelector("[role=menu] a, [role=menu] button")?.focus({ preventScroll: true });
  }
}, true);
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

// ---------- "Continue as guest": jump to the create form and put the cursor in it ----------
// After a navigation, <FocusOnNavigate> moves focus to the <h1> and the create form may be
// re-rendered when its interactive island starts, so keep (re)applying focus for a moment,
// unless the user has meanwhile focused something else themselves.
let guestFocusTimer = 0;
function focusFromHash() {
  clearInterval(guestFocusTimer);
  if (location.hash !== "#create-name") return;
  let tries = 0;
  let scrolled = false;
  const apply = () => {
    const input = document.getElementById("create-name");
    const active = document.activeElement;
    const userMoved = active && active !== document.body && active.tagName !== "H1" && active.id !== "create-name";
    if (++tries > 20 || userMoved) return clearInterval(guestFocusTimer);
    if (!input || active === input) return;
    if (!scrolled) {
      input.scrollIntoView({ block: "center", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
      scrolled = true;
    }
    input.focus({ preventScroll: true });
  };
  apply();
  guestFocusTimer = setInterval(apply, 100);
}
window.addEventListener("hashchange", focusFromHash);
onEnhancedLoad(focusFromHash);
document.addEventListener("click", (e) => {
  const link = e.target.closest('a[href$="#create-name"]');
  if (link && location.pathname === "/" && location.hash === "#create-name") {
    // Same hash again: hashchange won't fire, so focus directly.
    e.preventDefault();
    focusFromHash();
  }
});
if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", focusFromHash);
else focusFromHash();

/** Runs after each Blazor enhanced navigation (Blazor may finish starting after this module). */
function onEnhancedLoad(callback) {
  const hook = () => {
    if (typeof window.Blazor?.addEventListener !== "function") return false;
    window.Blazor.addEventListener("enhancedload", callback);
    return true;
  };
  if (!hook()) document.addEventListener("DOMContentLoaded", hook, { once: true });
}
