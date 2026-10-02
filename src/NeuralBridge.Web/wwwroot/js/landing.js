// Landing atmosphere over the wallpaper: drifting light particles in the photo's petal and
// periwinkle tones, and a very small pointer/scroll parallax on the wallpaper layer.
// Restraint: ≤ 48 particles, ~30 fps, paused when hidden or off-screen; nothing moves when
// the visitor prefers reduced motion.

function css(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function startParticles(canvas) {
  const ctx = canvas.getContext("2d");
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  let w = 0;
  let h = 0;
  let parts = [];
  const palette = () => [css("--petal"), css("--periwinkle"), css("--mist"), css("--orchid")];

  function resize() {
    const r = canvas.getBoundingClientRect();
    w = r.width;
    h = r.height;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const colors = palette();
    const count = Math.round(Math.min(48, Math.max(18, (w * h) / 38000)));
    parts = Array.from({ length: count }, (_, i) => ({
      x: Math.random() * w,
      y: Math.random() * h,
      r: 0.6 + Math.random() * 1.8,
      vy: -(4 + Math.random() * 10),
      vx: (Math.random() - 0.5) * 4,
      a: 0.15 + Math.random() * 0.45,
      phase: Math.random() * Math.PI * 2,
      color: colors[i % colors.length],
    }));
  }

  function draw(t) {
    ctx.clearRect(0, 0, w, h);
    for (const p of parts) {
      const twinkle = 0.65 + 0.35 * Math.sin(t / 1400 + p.phase);
      ctx.globalAlpha = p.a * twinkle;
      ctx.fillStyle = p.color;
      ctx.shadowColor = p.color;
      ctx.shadowBlur = 8;
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    ctx.shadowBlur = 0;
  }

  let visible = true;
  const io = new IntersectionObserver((e) => (visible = e[0].isIntersecting));
  io.observe(canvas);
  let raf = 0;
  let last = 0;
  function frame(t) {
    raf = requestAnimationFrame(frame);
    if (!visible || document.visibilityState !== "visible" || t - last < 33) return;
    const dt = Math.min(0.05, (t - last) / 1000 || 0.016);
    last = t;
    for (const p of parts) {
      p.y += p.vy * dt;
      p.x += p.vx * dt + Math.sin(t / 3000 + p.phase) * 0.08;
      if (p.y < -10) {
        p.y = h + 10;
        p.x = Math.random() * w;
      }
    }
    draw(t);
  }

  resize();
  draw(0);
  window.addEventListener("resize", resize);
  raf = requestAnimationFrame(frame);
  return () => {
    cancelAnimationFrame(raf);
    io.disconnect();
    window.removeEventListener("resize", resize);
  };
}

function startParallax(layer) {
  let tx = 0;
  let ty = 0;
  let raf = 0;
  const apply = () => {
    raf = 0;
    layer.style.setProperty("--px", `${tx.toFixed(2)}px`);
    layer.style.setProperty("--py", `${ty.toFixed(2)}px`);
  };
  const onPointer = (e) => {
    if (e.pointerType !== "mouse") return;
    tx = (e.clientX / window.innerWidth - 0.5) * -14;
    ty = (e.clientY / window.innerHeight - 0.5) * -10 - Math.min(window.scrollY, 600) * 0.04;
    raf ||= requestAnimationFrame(apply);
  };
  const onScroll = () => {
    ty = -Math.min(window.scrollY, 600) * 0.04;
    raf ||= requestAnimationFrame(apply);
  };
  window.addEventListener("pointermove", onPointer, { passive: true });
  window.addEventListener("scroll", onScroll, { passive: true });
  return () => {
    window.removeEventListener("pointermove", onPointer);
    window.removeEventListener("scroll", onScroll);
  };
}

export function startLanding({ reduceMotion }) {
  if (reduceMotion) return () => {};
  const stops = [];
  const canvas = document.querySelector("[data-particles]");
  const layer = document.querySelector(".backdrop[data-parallax]");
  if (canvas) stops.push(startParticles(canvas));
  if (layer) stops.push(startParallax(layer));
  return () => stops.forEach((s) => s());
}
