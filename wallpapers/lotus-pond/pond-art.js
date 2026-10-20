// Chao Desk pond — procedural art: autumn leaves and small round sprites (the koi are assets/koi.png).
// Everything is drawn once into canvases at start-up; pond.js packs them into textures.
(function (root) {
  'use strict';
  const TAU = Math.PI * 2;

  function rng(seed) { let s = (seed >>> 0) || 1; return () => { s = (1664525 * s + 1013904223) >>> 0; return s / 4294967296; }; }
  function canvas(w, h) { const c = document.createElement('canvas'); c.width = w; c.height = h; return [c, c.getContext('2d')]; }

  // ---- Autumn leaves ---------------------------------------------------------------------------
  function maple(seed) {
    const r = rng(seed), [c, x] = canvas(128, 128);
    x.translate(64, 64);
    const hue = r() < 0.6 ? 4 + r() * 14 : 18 + r() * 18;
    // Five pointed lobes fanned around the top (tips at odd steps, notches between), stem at the bottom.
    const p = new Path2D(), lobes = 5, span = Math.PI * 1.55;
    for (let i = 0; i <= lobes * 2; i++) {
      const a = -Math.PI / 2 - span / 2 + i * span / (lobes * 2);
      const len = i % 2 === 1 ? (i === lobes ? 56 : 40 + r() * 10) : 18 + r() * 6;
      const px = Math.cos(a) * len, py = Math.sin(a) * len;
      i ? p.lineTo(px, py) : p.moveTo(px, py);
    }
    p.lineTo(0, 10); p.closePath();
    const g = x.createRadialGradient(0, 4, 4, 0, 0, 60);
    g.addColorStop(0, `hsl(${hue + 12},85%,52%)`); g.addColorStop(1, `hsl(${hue},80%,40%)`);
    x.fillStyle = g; x.fill(p);
    x.strokeStyle = `hsla(${hue},70%,28%,0.45)`; x.lineWidth = 1.4;
    for (let i = 0; i < 5; i++) { const a = -Math.PI / 2 + (i - 2) * 0.62; x.beginPath(); x.moveTo(0, 8); x.lineTo(Math.cos(a) * 44, Math.sin(a) * 44); x.stroke(); }
    x.strokeStyle = `hsl(${hue},60%,30%)`; x.lineWidth = 2; x.beginPath(); x.moveTo(0, 8); x.lineTo(2, 34); x.stroke();
    return c;
  }

  function ginkgo(seed) {
    const r = rng(seed), [c, x] = canvas(128, 128);
    x.translate(64, 84);
    const hue = 44 + r() * 8;
    const p = new Path2D();
    p.moveTo(0, 0);
    p.bezierCurveTo(-30, -14, -54, -40, -50, -62);
    p.quadraticCurveTo(-26, -76, -4, -64);
    p.lineTo(0, -54); p.lineTo(4, -64);
    p.quadraticCurveTo(26, -76, 50, -62);
    p.bezierCurveTo(54, -40, 30, -14, 0, 0);
    const g = x.createRadialGradient(0, -10, 4, 0, -40, 66);
    g.addColorStop(0, `hsl(${hue - 6},90%,48%)`); g.addColorStop(1, `hsl(${hue},92%,60%)`);
    x.fillStyle = g; x.fill(p);
    x.save(); x.clip(p);
    x.strokeStyle = `hsla(${hue - 10},70%,35%,0.25)`; x.lineWidth = 0.8;
    for (let i = -10; i <= 10; i++) { x.beginPath(); x.moveTo(0, 0); x.lineTo(i * 6, -80); x.stroke(); }
    x.restore();
    x.strokeStyle = `hsl(${hue - 4},60%,38%)`; x.lineWidth = 2.2; x.beginPath(); x.moveTo(0, 0); x.quadraticCurveTo(3, 18, -2, 36); x.stroke();
    return c;
  }

  // ---- Small sprites -----------------------------------------------------------------------------
  function glow() {
    const [c, x] = canvas(64, 64);
    const g = x.createRadialGradient(32, 32, 0, 32, 32, 32);
    g.addColorStop(0, 'rgba(255,255,255,1)'); g.addColorStop(0.25, 'rgba(255,255,255,0.65)'); g.addColorStop(1, 'rgba(255,255,255,0)');
    x.fillStyle = g; x.fillRect(0, 0, 64, 64);
    return c;
  }

  function pellet() {
    const [c, x] = canvas(32, 32);
    const g = x.createRadialGradient(13, 12, 1, 16, 16, 12);
    g.addColorStop(0, '#d8a466'); g.addColorStop(0.6, '#9a6634'); g.addColorStop(1, '#5e3a1c');
    x.fillStyle = g; x.beginPath(); x.arc(16, 16, 11, 0, TAU); x.fill();
    return c;
  }

  root.PondArt = { maple, ginkgo, glow, pellet, rng, canvas };
})(typeof window !== 'undefined' ? window : globalThis);
