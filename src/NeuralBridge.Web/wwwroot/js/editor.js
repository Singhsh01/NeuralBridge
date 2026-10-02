// NeuralBridge shared editor: the only place keystrokes are handled.
//
// Why JS? Binding a Blazor Server textarea would send an event to the server on every
// keystroke and re-render the value under the caret. Instead, the textarea is owned here:
//  - local input is debounced (default 250 ms) and sent as whole-text + base version;
//  - at most one send is in flight, and later edits coalesce into the next send;
//  - remote updates arrive through applyRemote(). They are ignored if older than what we
//    have, and deferred if the user has unsent edits (documented last-write-wins: the
//    local text is sent next and becomes the newest version);
//  - setting textarea.value programmatically never fires "input", so remote text can't echo
//    back to the server. The server also excludes the originating circuit from broadcasts.

export function attach(textarea, dotnet, options) {
  const opts = Object.assign(
    { content: "", version: 0, debounceMs: 250, typingThrottleMs: 1500, readOnly: false, countsRoot: null },
    options || {}
  );
  const state = {
    version: opts.version,
    dirty: false,
    inFlight: false,
    resend: false,
    timer: 0,
    lastTyping: 0,
    disposed: false,
  };

  textarea.value = opts.content || "";
  setReadOnly(opts.readOnly);
  updateCounts();

  function countsRoot() {
    return (opts.countsRoot && document.querySelector(opts.countsRoot)) || document;
  }

  function updateCounts() {
    const text = textarea.value;
    const trimmed = text.trim();
    const words = trimmed ? trimmed.split(/\s+/u).length : 0;
    const root = countsRoot();
    root.querySelectorAll("[data-count-chars]").forEach((el) => (el.textContent = text.length.toLocaleString()));
    root.querySelectorAll("[data-count-words]").forEach((el) => (el.textContent = words.toLocaleString()));
    const limit = Number(textarea.getAttribute("maxlength")) || 0;
    root.querySelectorAll("[data-count-limit]").forEach((el) => {
      el.hidden = !(limit && text.length > limit * 0.9);
    });
  }

  function schedule(delay) {
    clearTimeout(state.timer);
    state.timer = setTimeout(flush, delay);
  }

  function onInput() {
    if (textarea.readOnly) return;
    state.dirty = true;
    updateCounts();
    schedule(opts.debounceMs);
    const now = Date.now();
    if (now - state.lastTyping > opts.typingThrottleMs) {
      state.lastTyping = now;
      dotnet.invokeMethodAsync("OnTyping").catch(() => {});
    }
  }

  async function flush() {
    if (state.disposed || !state.dirty) return;
    if (state.inFlight) {
      state.resend = true;
      return;
    }
    state.inFlight = true;
    state.dirty = false;
    const text = textarea.value;
    try {
      const ack = await dotnet.invokeMethodAsync("OnLocalEdit", text, state.version);
      if (ack && ack.ok) {
        state.version = Math.max(state.version, ack.version);
      } else if (ack && ack.retry) {
        state.dirty = true;
        schedule(800);
      }
    } catch {
      // Circuit hiccup (reconnecting): keep the edit and try again shortly.
      state.dirty = true;
      schedule(1500);
    } finally {
      state.inFlight = false;
      if (state.resend) {
        state.resend = false;
        state.dirty = true;
        schedule(0);
      }
    }
  }

  function replacePreservingCaret(next) {
    const prev = textarea.value;
    if (prev === next) return;
    const focused = document.activeElement === textarea;
    let start = textarea.selectionStart;
    let end = textarea.selectionEnd;
    let prefix = 0;
    const max = Math.min(prev.length, next.length);
    while (prefix < max && prev.charCodeAt(prefix) === next.charCodeAt(prefix)) prefix++;
    const delta = next.length - prev.length;
    if (start > prefix) start = Math.max(prefix, start + delta);
    if (end > prefix) end = Math.max(prefix, end + delta);
    const scroll = textarea.scrollTop;
    textarea.value = next;
    textarea.scrollTop = scroll;
    if (focused) textarea.setSelectionRange(Math.min(start, next.length), Math.min(end, next.length));
  }

  function setReadOnly(readOnly) {
    textarea.readOnly = !!readOnly;
    textarea.setAttribute("aria-readonly", readOnly ? "true" : "false");
  }

  function onVisibility() {
    if (document.visibilityState === "hidden") {
      clearTimeout(state.timer);
      flush();
    }
  }

  textarea.addEventListener("input", onInput);
  document.addEventListener("visibilitychange", onVisibility);

  return {
    applyRemote(text, version) {
      if (state.disposed || version <= state.version) return false;
      state.version = version;
      if (state.dirty || state.inFlight) return false; // local edits win and are sent next
      replacePreservingCaret(text);
      updateCounts();
      return true;
    },
    setContent(text, version) {
      clearTimeout(state.timer);
      state.dirty = false;
      state.version = Math.max(state.version, version);
      replacePreservingCaret(text);
      updateCounts();
    },
    setReadOnly,
    focus() {
      textarea.focus();
    },
    flushNow() {
      clearTimeout(state.timer);
      return flush();
    },
    detach() {
      state.disposed = true;
      clearTimeout(state.timer);
      textarea.removeEventListener("input", onInput);
      document.removeEventListener("visibilitychange", onVisibility);
    },
  };
}
