// NeuralBridge calls: peer-to-peer WebRTC (full mesh) with signaling relayed by the server
// through the session. Media never touches the server.
//
// Negotiation uses the "perfect negotiation" pattern: both ends add tracks and negotiate;
// offer collisions are resolved by a deterministic polite/impolite role (higher id = polite).
// Camera and microphone are requested only after a click, released on leave, and the camera
// light turns off when video is switched off (the track is stopped, not just disabled).

const ERRORS = {
  insecure: "Calls need a secure connection (HTTPS) or localhost.",
  denied: "Camera or microphone access was blocked. Allow it in your browser's site settings and try again.",
  nodevice: "No microphone or camera was found.",
  busy: "Your camera or microphone is being used by another app.",
  failed: "Couldn't start the call.",
};

export function createCall(dotnet, stage, options) {
  const selfId = options.selfId;
  const iceServers = options.iceServers || [];
  const peers = new Map(); // id -> peer
  let roster = new Map(); // id -> { displayName, audioEnabled, videoEnabled }
  let local = null; // MediaStream
  let inCall = false;
  let audioCtx = null;
  let meterTimer = 0;
  const meters = new Map(); // id -> { analyser, data }

  const grid = document.createElement("div");
  grid.className = "call-grid";
  grid.setAttribute("aria-live", "polite");
  stage.replaceChildren(grid);

  // ---------- tiles ----------
  function tile(id) {
    let el = grid.querySelector(`[data-peer="${CSS.escape(id)}"]`);
    if (el) return el;
    el = document.createElement("figure");
    el.className = "call-tile no-video";
    el.dataset.peer = id;
    const video = document.createElement("video");
    video.autoplay = true;
    video.playsInline = true;
    if (id === selfId) video.muted = true;
    const avatar = document.createElement("div");
    avatar.className = "call-avatar";
    avatar.setAttribute("aria-hidden", "true");
    const cap = document.createElement("figcaption");
    cap.className = "call-name";
    const mic = document.createElement("span");
    mic.className = "call-mic";
    el.append(video, avatar, cap, mic);
    grid.append(el);
    refreshTile(id);
    return el;
  }

  function refreshTile(id) {
    const el = grid.querySelector(`[data-peer="${CSS.escape(id)}"]`);
    const m = roster.get(id);
    if (!el || !m) return;
    const name = id === selfId ? `${m.displayName} (you)` : m.displayName;
    el.querySelector(".call-name").textContent = name;
    el.querySelector(".call-avatar").textContent = initials(m.displayName);
    el.classList.toggle("no-video", !m.videoEnabled);
    el.classList.toggle("is-muted", !m.audioEnabled);
    el.querySelector(".call-mic").textContent = m.audioEnabled ? "" : "Muted";
    el.setAttribute("aria-label", `${name}${m.audioEnabled ? "" : ", muted"}${m.videoEnabled ? "" : ", camera off"}`);
  }

  function removeTile(id) {
    grid.querySelector(`[data-peer="${CSS.escape(id)}"]`)?.remove();
    meters.delete(id);
  }

  function initials(name) {
    const parts = (name || "?").split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] || "?") + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
  }

  // ---------- audio levels (speaking indicators) ----------
  function meter(id, stream) {
    try {
      audioCtx ||= new AudioContext();
      if (!stream.getAudioTracks().length) return;
      const source = audioCtx.createMediaStreamSource(stream);
      const analyser = audioCtx.createAnalyser();
      analyser.fftSize = 512;
      source.connect(analyser);
      meters.set(id, { analyser, data: new Uint8Array(analyser.fftSize) });
      if (!meterTimer) meterTimer = setInterval(sampleLevels, 100);
    } catch {
      /* level meters are decoration */
    }
  }

  function sampleLevels() {
    for (const [id, m] of meters) {
      m.analyser.getByteTimeDomainData(m.data);
      let sum = 0;
      for (const v of m.data) sum += (v - 128) * (v - 128);
      const level = Math.min(1, Math.sqrt(sum / m.data.length) / 40);
      const el = grid.querySelector(`[data-peer="${CSS.escape(id)}"]`);
      if (el) {
        el.style.setProperty("--level", level.toFixed(2));
        el.classList.toggle("is-speaking", level > 0.12 && roster.get(id)?.audioEnabled !== false);
      }
      if (id === selfId) {
        document.querySelectorAll("[data-mic-level]").forEach((b) => b.style.setProperty("--level", level.toFixed(2)));
      }
    }
  }

  // ---------- peers ----------
  // Short event trail for diagnostics/tests: event names only, never SDP or candidate contents.
  const trail = [];
  function note(event) {
    trail.push(event);
    if (trail.length > 80) trail.shift();
  }

  function send(to, message) {
    note(`send:${message.description?.type ?? "candidate"}`);
    dotnet.invokeMethodAsync("SendSignal", to, JSON.stringify(message)).catch(() => note("send-failed"));
  }

  function ensurePeer(id) {
    let peer = peers.get(id);
    if (peer) return peer;
    const pc = new RTCPeerConnection({ iceServers });
    peer = { id, pc, polite: selfId > id, makingOffer: false, ignoreOffer: false, audioSender: null, videoSender: null };
    peers.set(id, peer);

    pc.onnegotiationneeded = async () => {
      // The first offer always comes from the impolite side; the polite side answers with its
      // own tracks. This avoids "glare" (both sides offering at once) when two people join
      // together. Later renegotiations (camera on/off) may start from either side.
      if (peer.polite && !pc.remoteDescription) {
        note("wait-for-offer");
        return;
      }
      try {
        peer.makingOffer = true;
        await pc.setLocalDescription();
        send(id, { description: pc.localDescription });
      } catch {
        /* a newer negotiation will follow */
      } finally {
        peer.makingOffer = false;
      }
    };
    pc.onicecandidate = ({ candidate }) => {
      if (candidate) send(id, { candidate });
    };
    pc.ontrack = ({ track, streams }) => {
      const el = tile(id);
      const video = el.querySelector("video");
      const stream = streams[0] || new MediaStream([track]);
      if (video.srcObject !== stream) {
        video.srcObject = stream;
        video.play().catch(() => {});
      }
      if (track.kind === "audio" && !meters.has(id)) meter(id, stream);
    };
    // Watchdog: if the first negotiation was lost (dropped message, reload mid-handshake), the
    // impolite side offers again with an ICE restart.
    if (!peer.polite) {
      peer.watchdog = setTimeout(function retry() {
        if (!peers.has(id) || pc.connectionState === "connected" || pc.connectionState === "closed") return;
        if (pc.signalingState === "stable" || pc.signalingState === "have-local-offer") {
          note("renegotiate");
          pc.restartIce();
          if (pc.signalingState === "stable") pc.onnegotiationneeded?.();
        }
        peer.watchdog = setTimeout(retry, 6000);
      }, 6000);
    }
    pc.onconnectionstatechange = () => {
      const el = grid.querySelector(`[data-peer="${CSS.escape(id)}"]`);
      if (el) el.dataset.state = pc.connectionState;
      if (pc.connectionState === "failed") pc.restartIce?.();
    };

    if (local) {
      for (const track of local.getTracks()) {
        const sender = pc.addTrack(track, local);
        if (track.kind === "audio") peer.audioSender = sender;
        else peer.videoSender = sender;
      }
    }

    tile(id);
    return peer;
  }

  function closePeer(id) {
    const peer = peers.get(id);
    if (peer) {
      clearTimeout(peer.watchdog);
      peer.pc.ontrack = peer.pc.onicecandidate = peer.pc.onnegotiationneeded = null;
      peer.pc.close();
      peers.delete(id);
    }
    removeTile(id);
  }

  // Signals are processed strictly one at a time, in arrival order. Interleaving them (an ICE
  // candidate handled while the offer it belongs to is still being applied) makes
  // addIceCandidate fail and drops the candidate, which can leave a connection stuck.
  let signalQueue = Promise.resolve();
  function onSignal(from, json) {
    signalQueue = signalQueue.then(() => handleSignal(from, json)).catch(() => {});
  }

  async function handleSignal(from, json) {
    if (!inCall) return note("recv:not-in-call");
    let message;
    try {
      message = JSON.parse(json);
    } catch {
      return;
    }
    const peer = ensurePeer(from);
    const pc = peer.pc;
    try {
      if (message.description) {
        const description = message.description;
        const collision = description.type === "offer" && (peer.makingOffer || pc.signalingState !== "stable");
        peer.ignoreOffer = !peer.polite && collision;
        note(`recv:${description.type}${peer.ignoreOffer ? ":ignored" : ""}`);
        if (peer.ignoreOffer) return;
        await pc.setRemoteDescription(description);
        if (description.type === "offer") {
          await pc.setLocalDescription();
          send(from, { description: pc.localDescription });
        }
      } else if (message.candidate) {
        try {
          await pc.addIceCandidate(message.candidate);
          note("recv:candidate");
        } catch (e) {
          note("recv:candidate-failed");
          if (!peer.ignoreOffer) throw e;
        }
      }
    } catch (e) {
      note(`error:${e?.name ?? "unknown"}`);
    }
  }

  function onRoster(list) {
    roster = new Map(list.map((m) => [m.participantId, m]));
    if (!inCall) return;
    for (const [id] of roster) {
      if (id !== selfId) ensurePeer(id);
      refreshTile(id);
    }
    for (const id of [...peers.keys()]) {
      if (!roster.has(id)) closePeer(id);
    }
  }

  // ---------- local media ----------
  async function getMedia(constraints) {
    if (!navigator.mediaDevices?.getUserMedia) throw Object.assign(new Error(), { code: "insecure" });
    try {
      return await navigator.mediaDevices.getUserMedia(constraints);
    } catch (e) {
      const code =
        e.name === "NotAllowedError" || e.name === "SecurityError" ? "denied"
        : e.name === "NotFoundError" || e.name === "OverconstrainedError" ? "nodevice"
        : e.name === "NotReadableError" ? "busy"
        : "failed";
      throw Object.assign(new Error(), { code });
    }
  }

  async function join(withVideo) {
    if (inCall) return { ok: true, audio: !!local?.getAudioTracks()[0]?.enabled, video: !!local?.getVideoTracks().length };
    let warning = null;
    try {
      try {
        local = await getMedia({ audio: { echoCancellation: true, noiseSuppression: true }, video: withVideo ? { width: { ideal: 1280 }, height: { ideal: 720 } } : false });
      } catch (e) {
        if (!withVideo || e.code === "insecure") throw e;
        local = await getMedia({ audio: true, video: false }); // camera missing/busy: still join with audio
        warning = "Your camera isn't available, so you joined with audio only.";
      }
    } catch (e) {
      return { ok: false, error: ERRORS[e.code] || ERRORS.failed };
    }
    inCall = true;
    const self = tile(selfId);
    self.classList.add("is-self");
    self.querySelector("video").srcObject = local;
    meter(selfId, local);
    return { ok: true, audio: true, video: local.getVideoTracks().length > 0, warning };
  }

  function setMic(on) {
    local?.getAudioTracks().forEach((t) => (t.enabled = on));
    return !!local;
  }

  async function setCamera(on) {
    if (!local) return { ok: false, error: ERRORS.failed };
    if (!on) {
      for (const t of local.getVideoTracks()) {
        t.stop();
        local.removeTrack(t);
      }
      for (const peer of peers.values()) await peer.videoSender?.replaceTrack(null);
      return { ok: true };
    }
    let stream;
    try {
      stream = await getMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 } } });
    } catch (e) {
      return { ok: false, error: ERRORS[e.code] || ERRORS.failed };
    }
    const track = stream.getVideoTracks()[0];
    local.addTrack(track);
    tile(selfId).querySelector("video").srcObject = local;
    for (const peer of peers.values()) {
      if (peer.videoSender) await peer.videoSender.replaceTrack(track);
      else peer.videoSender = peer.pc.addTrack(track, local); // triggers renegotiation
    }
    return { ok: true };
  }

  function leave() {
    inCall = false;
    for (const id of [...peers.keys()]) closePeer(id);
    local?.getTracks().forEach((t) => t.stop());
    local = null;
    removeTile(selfId);
    clearInterval(meterTimer);
    meterTimer = 0;
    meters.clear();
    audioCtx?.close().catch(() => {});
    audioCtx = null;
    document.querySelectorAll("[data-mic-level]").forEach((b) => b.style.removeProperty("--level"));
  }

  /** Diagnostics for automated tests: connection states and whether remote media is flowing. */
  function stats() {
    return [...peers.values()].map((p) => {
      const video = grid.querySelector(`[data-peer="${CSS.escape(p.id)}"] video`);
      return {
        id: p.id,
        state: p.pc.connectionState,
        remoteTracks: p.pc.getReceivers().filter((r) => r.track && r.track.readyState === "live").length,
        videoWidth: video?.videoWidth || 0,
      };
    });
  }

  window.addEventListener("pagehide", leave);
  // Read-only diagnostics hook used by the end-to-end tests (no media or text is exposed).
  stage.callStats = stats;
  stage.callTrail = () => trail.slice();

  return {
    join,
    setMic,
    setCamera,
    leave,
    onSignal,
    onRoster,
    stats,
    dispose() {
      window.removeEventListener("pagehide", leave);
      leave();
    },
  };
}
