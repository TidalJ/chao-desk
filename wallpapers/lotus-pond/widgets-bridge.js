// Chao Desk host bridge for widgets.html: the widgets without the pond. The host serves the user's Windows
// wallpaper at https://desktop.lotus/ and tells us how Windows places it ("desktop" message and
// __LOTUS_INIT__.desktop); lotus-widgets.js draws the widgets over it. The host sends the same messages as to
// the pond; only the widget ones matter here. Nothing runs per frame, so pausing only holds the widgets' updates.
(function () {
  'use strict';
  const bridge = window.chrome && window.chrome.webview;
  if (!bridge) return;

  const post = (m) => { try { bridge.postMessage(m); } catch (e) { /* host gone */ } };
  const log = (level, message) => post({ type: 'log', level, message: String(message).slice(0, 2000) });
  window.addEventListener('error', (e) => log('error', `${e.message} (${e.filename}:${e.lineno})`));
  window.addEventListener('unhandledrejection', (e) => log('error', `Unhandled rejection: ${e.reason}`));

  const W = () => window.PondWidgets;

  // ---- The Windows wallpaper ------------------------------------------------------------------------
  // Fill and Span cover the screen, Fit is contained, Stretch fills, Center and Tile show the picture at its
  // real size (in device pixels, so divide by the scale).
  function applyDesktop(d) {
    const el = document.getElementById('desk');
    if (!d || !el) return;
    el.style.backgroundColor = d.color || '#000';
    if (!d.image) { el.style.backgroundImage = 'none'; return; }
    const fit = d.fit || 'cover';
    const img = new Image();
    img.onload = () => {
      el.style.backgroundImage = `url("${d.image}")`;
      el.style.backgroundRepeat = fit === 'tile' ? 'repeat' : 'no-repeat';
      el.style.backgroundPosition = fit === 'tile' ? 'left top' : 'center';
      const dpr = window.devicePixelRatio || 1;
      el.style.backgroundSize = fit === 'contain' ? 'contain' : fit === 'fill' ? '100% 100%'
        : fit === 'center' || fit === 'tile' ? `${img.naturalWidth / dpr}px ${img.naturalHeight / dpr}px` : 'cover';
    };
    img.onerror = () => log('warn', 'could not load the Windows wallpaper');
    img.src = d.image;
  }

  // ---- Host messages ------------------------------------------------------------------------------
  let paused = false;
  bridge.addEventListener('message', (ev) => {
    const m = ev.data;
    if (!m || typeof m !== 'object' || !W()) return;
    switch (m.type) {
      case 'pointer':
        if (m.button !== 'right' && m.button !== 'middle' && (m.event === 'down' || m.event === 'move' || m.event === 'up')) W().pointer(m.event, m.x, m.y);
        else if (m.event === 'click' && m.button === 'left' && !paused) W().tap(m.x, m.y);
        break;
      case 'pause': paused = true; W().setPaused(true); break;
      case 'resume': paused = false; W().setPaused(false); break;
      case 'properties': W().apply(m.values); break;
      case 'language': W().setLanguage(m.language); break;
      case 'tasks': W().setTasks(m.items); break;
      case 'rates': W().setRates(m); break;
      case 'holidays': W().setHolidays(m.days); break;
      case 'widgets': W().setWidgets(m); break;
      case 'system': W().setSystem(m); break;
      case 'weather': W().setWeather(m); break;
      case 'desktop': applyDesktop(m); break;
    }
  });

  // ---- Start-up -----------------------------------------------------------------------------------
  const init = window.__LOTUS_INIT__ || {};
  document.addEventListener('DOMContentLoaded', () => {
    applyDesktop(init.desktop);
    if (W()) {
      W().onAction = post;
      if (init.language) W().setLanguage(init.language); // before the first render
      W().start();
      if (init.properties) W().apply(init.properties);
      if (init.paused) { paused = true; W().setPaused(true); }
    }
    post({ type: 'ready' });
  });
})();
