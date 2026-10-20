// Chao Desk host bridge for the pond (pond.js) and the widget card (lotus-widgets.js). It adapts both to the
// host protocol (docs/wallpaper-format.md):
//
//  * pause: holds requestAnimationFrame so nothing at all runs while paused;
//  * pointer clicks on the empty desktop → food, a ripple, or a nudge for a floating leaf;
//  * wallpaper.json properties and host settings (frame rate, quality) → Pond.set;
//  * the app's language (init and "language" messages) → the widget card ("Card language: Same as Chao Desk");
//  * "Day and night: Follow clock" switches night on and off with the local time;
//  * "Weather: Follow real weather" rains or snows on the pond when it does in the city set in the app.
//
// Opened in a plain browser (no host), clicks feed the koi and the query string sets options, e.g.
// index.html?scene=autumn&night=1&weather=rain — handy for trying changes.
(function () {
  'use strict';
  const bridge = window.chrome && window.chrome.webview;
  const P = () => window.Pond;

  if (!bridge) {
    const q = new URLSearchParams(location.search);
    document.addEventListener('DOMContentLoaded', () => {
      P().set({
        scene: q.get('scene') || undefined, night: q.get('night') === '1', weather: q.get('weather') || undefined,
        koi: q.has('koi') ? Number(q.get('koi')) : undefined, koiSize: q.has('koiSize') ? Number(q.get('koiSize')) : undefined, fps: q.has('fps') ? Number(q.get('fps')) : 60, debug: q.get('debug') || undefined,
      });
      document.addEventListener('click', (e) => { if (!P().poke(e.clientX, e.clientY)) P().feed(e.clientX, e.clientY); });
      // ?demo=1: taps the water now and then, for checking ripples in screenshots.
      if (q.get('demo')) setInterval(() => P().feed(innerWidth * (0.35 + Math.random() * 0.4), innerHeight * (0.3 + Math.random() * 0.4)), 1200);
      if (window.PondWidgets) { if (q.get('lang')) window.PondWidgets.setLanguage(q.get('lang')); window.PondWidgets.start(); }
    });
    return;
  }

  const post = (m) => { try { bridge.postMessage(m); } catch (e) { /* host gone */ } };
  const log = (level, message) => post({ type: 'log', level, message: String(message).slice(0, 2000) });
  window.addEventListener('error', (e) => log('error', `${e.message} (${e.filename}:${e.lineno})`));
  window.addEventListener('unhandledrejection', (e) => log('error', `Unhandled rejection: ${e.reason}`));
  // pond.js prints a status line now and then ("[pond] 10 koi · summer · …"): forward it to the host log.
  const info = console.info.bind(console), warn = console.warn.bind(console);
  console.info = (...a) => { info(...a); if (typeof a[0] === 'string' && a[0].startsWith('[pond]')) log('info', a.join(' ')); };
  console.warn = (...a) => { warn(...a); if (typeof a[0] === 'string' && a[0].startsWith('[pond]')) log('warn', a.join(' ')); };

  // ---- Pause: hold the frame loop completely ------------------------------------------------------
  const nativeRaf = window.requestAnimationFrame.bind(window);
  const held = [];
  let paused = false;
  window.requestAnimationFrame = (cb) => {
    if (paused) { held.push(cb); return 0; }
    return nativeRaf(cb);
  };
  function setPaused(value) {
    paused = !!value;
    if (P()) P().pause(paused); // resets the pond's clock: no jump on resume
    if (window.PondWidgets) window.PondWidgets.setPaused(paused);
    if (!paused) for (const cb of held.splice(0)) nativeRaf(cb);
  }

  // ---- Properties ---------------------------------------------------------------------------------
  const SCENES = { 'celadon basin': 'celadon', 'wisteria garden': 'wisteria', 'summer lotus': 'summer', 'autumn leaves': 'autumn' };
  let timeOfDay = 'Follow clock';
  let weatherMode = 'follow real weather', realWeather = null;
  function pondWeather() {
    if (weatherMode !== 'follow real weather') return weatherMode;
    const c = realWeather && realWeather.now ? Number(realWeather.now.code) : 0; // WMO weather codes
    if ((c >= 51 && c <= 67) || (c >= 80 && c <= 82) || c >= 95) return 'rain';
    if ((c >= 71 && c <= 77) || c === 85 || c === 86) return 'snow';
    if (c === 3 || c === 45 || c === 48) return 'cloudy';
    return 'sunny';
  }
  function applyWeather() { if (P()) P().set({ weather: pondWeather() }); }

  function applyTimeOfDay() {
    const h = new Date().getHours();
    const night = timeOfDay === 'Night' || (timeOfDay === 'Follow clock' && (h >= 19 || h < 6));
    if (P()) P().set({ night });
    if (window.PondWidgets) window.PondWidgets.setNight(night);
  }
  setInterval(() => { if (!paused && timeOfDay === 'Follow clock') applyTimeOfDay(); }, 60_000);

  function applyProperties(p) {
    if (!p) return;
    const o = {};
    if (typeof p.scene === 'string') o.scene = SCENES[p.scene.toLowerCase()] || 'celadon';
    if (typeof p.speed === 'number') o.speed = p.speed;
    if (typeof p.koi === 'number') o.koi = p.koi;
    if (typeof p.koiSize === 'number') o.koiSize = p.koiSize;
    if (typeof p.rainAmount === 'number') o.rain = p.rainAmount;
    if (typeof p.snowAmount === 'number') o.snow = p.snowAmount;
    for (const k of ['floating', 'drift', 'fireflies']) if (typeof p[k] === 'boolean') o[k] = p[k];
    if (P()) P().set(o);
    if (typeof p.weather === 'string') weatherMode = p.weather.toLowerCase();
    applyWeather();
    if (typeof p.timeOfDay === 'string') timeOfDay = p.timeOfDay;
    if (window.PondWidgets) window.PondWidgets.apply(p);
    applyTimeOfDay();
  }

  // ---- Idle throttling ------------------------------------------------------------------------------
  // The pond runs at 30 FPS (or the host's lower limit), idle or not: at 20 a tail stroke was only a handful of
  // frames and the koi looked jerky. The idle/active split is kept so the two rates can differ again.
  // All motion is time-based, so the rate never changes speed.
  const ACTIVE_MS = 8_000, IDLE_FPS = 30, ACTIVE_FPS = 30;
  let fpsLimit = 30, idle = true, lastClick = 0;
  const applyFps = () => { if (P()) P().set({ fps: Math.min(idle ? IDLE_FPS : ACTIVE_FPS, fpsLimit) }); };
  function clickActivity() { lastClick = Date.now(); if (idle) { idle = false; applyFps(); } }
  setInterval(() => { if (!idle && !paused && Date.now() - lastClick > ACTIVE_MS) { idle = true; applyFps(); } }, 2_000);

  // ---- Gentle taps ------------------------------------------------------------------------------
  // Feeding has a small budget (3 taps, refilled one every 2.5 s, never more than ~40 pellets afloat);
  // beyond it a click just makes a soft ripple, at most one every 120 ms.
  const FEED_TOKENS = 3, REFILL_MS = 2500, MAX_FOOD = 40, RIPPLE_GAP_MS = 120;
  let tokens = FEED_TOKENS, tokensAt = Date.now(), lastRipple = 0;
  function tap(x, y) {
    const pond = P(), now = Date.now();
    if (!pond) return;
    if (pond.poke(x, y)) return;
    tokens = Math.min(FEED_TOKENS, tokens + (now - tokensAt) / REFILL_MS); tokensAt = now;
    if (tokens >= 1 && pond.foodCount() < MAX_FOOD) { tokens -= 1; pond.feed(x, y); return; }
    if (now - lastRipple >= RIPPLE_GAP_MS) { lastRipple = now; pond.ripple(x, y, 0.35); }
  }

  function applySettings(s) {
    if (!s) return;
    if (s.fpsLimit) { fpsLimit = s.fpsLimit; applyFps(); }
    if (s.quality && P()) P().set({ quality: s.quality === 'low' ? 'eco' : 'high' });
  }

  // ---- Host messages ------------------------------------------------------------------------------
  bridge.addEventListener('message', (ev) => {
    const m = ev.data;
    if (!m || typeof m !== 'object') return;
    switch (m.type) {
      case 'pointer':
        if (m.button !== 'right' && m.button !== 'middle' && (m.event === 'down' || m.event === 'move' || m.event === 'up') && window.PondWidgets) { window.PondWidgets.pointer(m.event, m.x, m.y); break; }
        if (m.event === 'click' && m.button === 'left' && !paused) {
          if (window.PondWidgets && window.PondWidgets.tap(m.x, m.y)) break;
          clickActivity();
          tap(m.x, m.y);
        }
        break;
      case 'pause': setPaused(true); break;
      case 'resume': setPaused(false); break;
      case 'properties': applyProperties(m.values); break;
      case 'settings': applySettings(m); break;
      case 'language': if (window.PondWidgets) window.PondWidgets.setLanguage(m.language); break;
      case 'tasks': if (window.PondWidgets) window.PondWidgets.setTasks(m.items); break;
      case 'rates': if (window.PondWidgets) window.PondWidgets.setRates(m); break;
      case 'holidays': if (window.PondWidgets) window.PondWidgets.setHolidays(m.days); break;
      case 'widgets': if (window.PondWidgets) window.PondWidgets.setWidgets(m); break;
      case 'system': if (window.PondWidgets) window.PondWidgets.setSystem(m); break;
      case 'weather':
        realWeather = m;
        if (window.PondWidgets) window.PondWidgets.setWeather(m);
        if (weatherMode === 'follow real weather') applyWeather();
        break;
    }
  });

  // ---- Start-up -----------------------------------------------------------------------------------
  const init = window.__LOTUS_INIT__ || {};
  if (init.paused) paused = true; // before the pond's first frame
  document.addEventListener('DOMContentLoaded', () => {
    if (window.PondWidgets) {
      window.PondWidgets.onAction = post;
      if (init.language) window.PondWidgets.setLanguage(init.language); // before the first render
      window.PondWidgets.start();
    }
    applySettings(init.settings);
    applyProperties(init.properties);
    if (init.paused && P()) P().pause(true);
    post({ type: 'ready' });
  });
})();
