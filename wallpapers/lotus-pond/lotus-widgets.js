// Lotus Pond widgets drawn over the pond. By default only a see-through clock sits in the corner; a
// click opens a glass card with the widgets the user picked, in the user's order (Settings → Widgets;
// the host sends a "widgets" message), and the card closes again by itself. Layouts: stacked widgets
// split into pages by screen height (dots to turn the page), or one widget per tab. Widgets:
//  * calendar — this week as a strip with lunar days and festivals, the lunar date and the next holiday;
//    click it for the month (or week) calendar with the official 休/班 schedule;
//  * clock — a large clock; AUD rate — Bank of China selling / buying price (fetched by the host);
//  * weather — current conditions and a 4-day forecast (host); world clock — other cities (Intl);
//  * tasks; computer status (sampled by the host); shortcuts (opened by the host by id).
// Plain DOM. The desktop icon layer covers the page, so real mouse events never arrive: the host
// forwards clicks and the bridge calls PondWidgets.tap(x, y), which finds the element under the
// point and runs its data-act. Widgets keep their DOM between updates, so unfolding is a CSS
// transition rather than a jump. Text refreshes once a minute at most; nothing runs per frame.
(function (root) {
  'use strict';

  const EASE = 'cubic-bezier(.22, .8, .24, 1)';
  const CSS = `
  #lotus-widgets { position: fixed; inset: 0; pointer-events: none; z-index: 20; font-family: "Noto Serif SC", "Songti SC", "SimSun", "Segoe UI", serif; color: #f6f3ea; }
  .lw-stack { position: absolute; display: flex; flex-direction: column; gap: 12px; pointer-events: auto; }
  .lw-stack.tr { top: 26px; right: 30px; align-items: flex-end; } .lw-stack.tl { top: 26px; left: 30px; align-items: flex-start; }
  .lw-stack.br { bottom: 64px; right: 30px; align-items: flex-end; flex-direction: column-reverse; } .lw-stack.bl { bottom: 64px; left: 30px; align-items: flex-start; flex-direction: column-reverse; }
  /* The card is transparent; its glass is a separate layer (.lw-bg) so a size change can be eased by
     scaling only that layer (compositor), never the text. Measured in the live wallpaper: the backdrop
     blur costs no frames, so the glass stays. */
  .lw-card, .lw-panel { position: relative; isolation: isolate; box-sizing: border-box; padding: 16px 18px 14px; text-shadow: 0 1px 6px rgba(0,0,0,.28); }
  /* Never animate opacity or transform on the card or panel themselves: Chromium drops the glass's
     backdrop blur while an ancestor is fading or scaling and pops it back in at the end (a flash, a
     colour jump, text snapping into place). The glass layer and the content animate separately. */
  .lw-card { width: 300px; }
  .lw-card > .lw-bg, .lw-panel > .lw-bg { transition: opacity .24s ease .02s, transform .34s ${EASE} .02s; }
  .lw-card > :not(.lw-bg), .lw-panel > :not(.lw-bg) { transition: opacity .22s ease .07s; }
  .lw-bg { position: absolute; top: 0; left: 0; right: 0; height: 100%; z-index: -1; border-radius: 18px; background: rgba(24, 44, 40, .36); border: 1px solid rgba(255,255,255,.22);
    box-shadow: 0 10px 34px rgba(8, 20, 16, .28); backdrop-filter: blur(12px) saturate(1.2); -webkit-backdrop-filter: blur(12px) saturate(1.2); transform-origin: top; }
  .lw-card > .lw-bg { transform-origin: top right; }
  .lw-stack.tl .lw-card > .lw-bg { transform-origin: top left; } .lw-stack.br .lw-card > .lw-bg { transform-origin: bottom right; } .lw-stack.bl .lw-card > .lw-bg { transform-origin: bottom left; }
  .lw-panel > .lw-bg { transform-origin: right center; }
  .lw-stack.tl .lw-panel > .lw-bg, .lw-stack.bl .lw-panel > .lw-bg { transform-origin: left center; }
  .lw-hide { display: none !important; }
  /* Peek: a see-through clock in the corner; a click opens the card over the same spot. */
  .lw-peek { position: absolute; top: 0; right: 0; text-align: right; white-space: nowrap; pointer-events: auto;
    text-shadow: 0 2px 14px rgba(0,0,0,.38), 0 1px 3px rgba(0,0,0,.32); transform-origin: top right;
    transition: opacity .3s ease .12s, transform .4s ${EASE} .1s, visibility 0s; }
  .lw-stack.tl .lw-peek, .lw-stack.bl .lw-peek { right: auto; left: 0; text-align: left; transform-origin: top left; }
  .lw-stack.br .lw-peek, .lw-stack.bl .lw-peek { top: auto; bottom: 0; }
  .lw-peek .t { font-size: 62px; font-weight: 300; line-height: 1; letter-spacing: 2px; font-variant-numeric: tabular-nums; }
  .lw-peek .d { margin-top: 7px; font-size: 15px; opacity: .93; }
  .lw-stack.opened .lw-peek { opacity: 0; transform: translateY(-4px) scale(.97); visibility: hidden; pointer-events: none; transition: opacity .1s ease, transform .2s ease, visibility 0s .2s; }
  .lw-stack.peek .lw-card, .lw-panel { visibility: hidden; pointer-events: none; transition: visibility 0s .2s; }
  .lw-stack.peek .lw-card > .lw-bg, .lw-panel > .lw-bg { opacity: 0; transform: scale(.97); transition: opacity .14s ease, transform .2s ease; }
  .lw-stack.peek .lw-card > :not(.lw-bg), .lw-panel > :not(.lw-bg) { opacity: 0; transition: opacity .1s ease; }
  /* The month opens as a panel beside the card, so the card itself never changes. */
  .lw-panel { position: absolute; top: 0; right: calc(100% + 12px); }
  .lw-stack.tl .lw-panel, .lw-stack.bl .lw-panel { right: auto; left: calc(100% + 12px); }
  .lw-stack.br .lw-panel, .lw-stack.bl .lw-panel { top: auto; bottom: 0; }
  .lw-stack.cal .lw-panel { visibility: visible; pointer-events: auto; transition: visibility 0s; }
  .lw-stack.cal .lw-panel > .lw-bg { opacity: 1; transform: none; transition: opacity .22s ease, transform .32s ${EASE}; }
  .lw-stack.cal .lw-panel > :not(.lw-bg) { opacity: 1; transition: opacity .22s ease .08s; }
  .lw-ups { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px; margin-bottom: 10px; padding-bottom: 10px; border-bottom: 1px solid rgba(255,255,255,.16); }
  .lw-ups > div { font-size: 13px; line-height: 1.3; }
  .lw-ups small { display: block; font-size: 11.5px; opacity: .65; }
  .lw-ups b { font-weight: 400; color: #ffe2a8; }
  .lw-ups .term { color: #bfe7d4; }
  /* Week strip (the calendar when folded). */
  .lw-strip { padding-top: 8px; }
  .lw-strip .lw-day { padding: 5px 0 4px; }
  .lw-strip .lw-day .n { font-size: 16px; }
  .lw-cal-info { font-size: 13.5px; }
  .lw-cal-info .lw-count { display: inline; margin: 0; }
  /* Page dots (stacked layout with more than one page). */
  /* Page bar: big ‹ › targets at the sides (they wrap around), dots in between. */
  .lw-dots { display: flex; align-items: center; justify-content: space-between; margin: 8px -6px -4px; }
  .lw-dots .arr { padding: 2px 14px 5px; font-size: 22px; line-height: 1; opacity: .9; border-radius: 8px; background: rgba(255,255,255,.08); }
  .lw-dots .pips { display: flex; gap: 7px; }
  .lw-dots .pips span { padding: 6px 0; }
  .lw-dots .pips i { display: block; width: 6px; height: 6px; border-radius: 3px; background: rgba(255,255,255,.35); transition: transform .3s ${EASE}, background-color .3s; }
  .lw-dots .pips span.cur i { background: rgba(255,255,255,.9); transform: scale(1.35); }
  .lw-fx-title { display: flex; justify-content: space-between; font-size: 12.5px; opacity: .7; margin-bottom: 5px; }
  .lw-fx { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 6px; }
  .lw-fx small { display: block; font-size: 11.5px; opacity: .66; white-space: nowrap; }
  .lw-fx b { display: block; margin-top: 1px; font-weight: 400; font-size: 18px; line-height: 1.2; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .lw-fx > div:first-child b { font-size: 20px; }
  .lw-rate-sub { display: flex; justify-content: space-between; gap: 8px; margin-top: 3px; font-size: 12.5px; opacity: .72; }
  .lw-chev { position: absolute; top: 4px; right: 4px; width: 8px; height: 8px; border-right: 1.5px solid rgba(255,255,255,.55); border-bottom: 1.5px solid rgba(255,255,255,.55);
    transform: rotate(45deg); transition: transform .5s ${EASE}; }
  .lw-chev.open { transform: translateY(4px) rotate(225deg); }
  .lw-chev.inl { position: static; display: inline-block; width: 6px; height: 6px; margin: 0 0 3px 9px; vertical-align: middle; }
  .lw-chev.inl.open { transform: translateY(3px) rotate(225deg); }
  .lw-exp { display: none; }
  .lw-exp.open { display: block; }
  .lw-cal-body { width: 392px; }
  /* The open card lists three upcoming days beside the date: the head grows (smoothly) to fit them. */
  .lw-head { display: flex; }
  .lw-head-main { flex: 1; min-width: 0; }
  /* Absolutely placed so the side column never makes the clock taller. */
  .lw-side-in { position: absolute; top: 0; left: 0; bottom: 0; box-sizing: border-box; width: 150px; margin-top: 4px; padding-left: 14px; border-left: 1px solid rgba(255,255,255,.16); }
  .lw-up { display: flex; justify-content: space-between; align-items: baseline; gap: 6px; padding: 2px 0; font-size: 13px; line-height: 1.35; white-space: nowrap; }
  .lw-up small { margin-left: 5px; font-size: 11px; opacity: .6; }
  .lw-up b { font-weight: 400; font-size: 12.5px; color: #ffe2a8; white-space: nowrap; }
  .lw-up.term span { color: #bfe7d4; }
  .lw-grid-wrap { position: relative; }
  .lw-grid-wrap > .lw-ghost { position: absolute; left: 0; top: 0; width: 100%; pointer-events: none; }
  .lw-mod { position: relative; }
  .lw-spage > .lw-mod + .lw-mod { margin-top: 12px; padding-top: 11px; border-top: 1px solid rgba(255,255,255,.16); }
  .lw-spage > .lw-mod + .lw-mod > .lw-chev { top: 15px; }
  .lw-pager + .lw-tabs, .lw-tabs { margin-top: 12px; }
  .lw-tile { display: flex; align-items: center; gap: 14px; }
  .lw-dnum { flex: none; min-width: 56px; font-size: 48px; line-height: .95; text-align: center; font-variant-numeric: tabular-nums; }
  .lw-dinfo { min-width: 0; }
  .lw-dinfo > div { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .lw-dline { font-size: 15px; }
  .lw-dline .chip { display: inline-block; margin-left: 7px; padding: 0 5px; border-radius: 4px; font-size: 11.5px; vertical-align: 1px; background: #4f9e74; }
  .lw-dline .chip.work { background: #b8733f; }
  .lw-tile .lw-lunar { margin-top: 2px; }
  .lw-tile .lw-count { margin-top: 3px; }
  /* While the open calendar would not fit on the screen, the other widgets fold away. */
  .lw-spage > .lw-mod.lw-away { display: none; }
  .lw-spage > .lw-mod.lw-solo { margin-top: 0; padding-top: 0; border-top-color: transparent; }
  .lw-spage > .lw-mod.lw-solo > .lw-chev { top: 4px; }
  .lw-meter { display: grid; grid-template-columns: 38px 1fr auto; align-items: center; gap: 9px; padding: 3px 0; font-size: 13px; }
  .lw-meter .bar { height: 4px; border-radius: 2px; background: rgba(255,255,255,.14); overflow: hidden; }
  .lw-meter .bar i { display: block; height: 100%; border-radius: 2px; background: #9fd8b8; transform-origin: left; transform: scaleX(0); transition: transform .8s ${EASE}, background-color .4s; }
  .lw-meter .bar i.hot { background: #e8a08e; }
  .lw-meter .v { font-variant-numeric: tabular-nums; opacity: .9; white-space: nowrap; }
  .lw-sc { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px 4px; text-align: center; }
  .lw-sc > div { min-width: 0; padding: 4px 0 2px; border-radius: 10px; transition: background-color .25s; }
  .lw-sc .ic { display: inline-flex; align-items: center; justify-content: center; width: 34px; height: 34px; border-radius: 50%; font-size: 17px;
    background: rgba(255,255,255,.13); border: 1px solid rgba(255,255,255,.18); }
  .lw-sc .nm { margin-top: 4px; font-size: 11.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .lw-time { font-size: 52px; font-weight: 500; letter-spacing: 2px; line-height: 1; font-variant-numeric: tabular-nums; }
  .lw-date { margin-top: 8px; font-size: 15px; opacity: .92; }
  .lw-lunar { margin-top: 3px; font-size: 13.5px; opacity: .8; }
  .lw-count { margin-top: 5px; font-size: 13.5px; color: #ffe2a8; }
  .lw-sec { margin-top: 12px; padding-top: 10px; border-top: 1px solid rgba(255,255,255,.16); }
  .lw-sec-title { display: flex; justify-content: space-between; font-size: 12.5px; opacity: .7; margin-bottom: 6px; }
  .lw-task { display: flex; align-items: flex-start; gap: 8px; padding: 4px 2px; font-size: 14px; line-height: 1.35; border-radius: 6px; transition: opacity .3s; }
  .lw-task i { flex: none; width: 13px; height: 13px; margin-top: 3px; border-radius: 50%; border: 1.5px solid rgba(255,255,255,.75); transition: background-color .3s, border-color .3s; }
  .lw-task.done { opacity: .5; text-decoration: line-through; } .lw-task.done i { background: #9fd8b8; border-color: #9fd8b8; }
  .lw-muted { font-size: 12.5px; opacity: .65; }
  .lw-link { color: #bfe7d4; }
  .lw-cal-bar { display: flex; align-items: center; gap: 6px; font-size: 14px; margin-bottom: 8px; }
  .lw-cal-bar b { flex: 1; text-align: center; font-weight: 500; }
  .lw-btn { padding: 2px 9px; border-radius: 8px; background: rgba(255,255,255,.12); font-size: 13px; transition: background-color .25s; }
  .lw-btn.on { background: rgba(159, 216, 184, .38); }
  .lw-grid { display: grid; grid-template-columns: repeat(7, 1fr); gap: 2px; text-align: center; }
  .lw-wd { font-size: 12px; opacity: .6; padding-bottom: 3px; }
  .lw-day { position: relative; padding: 4px 0 3px; border-radius: 8px; line-height: 1.15; transition: background-color .25s, box-shadow .25s; }
  .lw-day .n { font-size: 15px; font-variant-numeric: tabular-nums; }
  .lw-day .l { font-size: 10.5px; opacity: .72; white-space: nowrap; overflow: hidden; }
  .lw-day.fest .l { color: #ffe2a8; opacity: 1; } .lw-day.term .l { color: #bfe7d4; opacity: .95; }
  .lw-day.other { opacity: .38; } .lw-day.wkend .n { color: #ffd0c4; }
  .lw-day.today { background: rgba(159, 216, 184, .3); } .lw-day.sel { box-shadow: inset 0 0 0 1px rgba(255,255,255,.55); }
  .lw-day u { position: absolute; top: 1px; right: 3px; font-size: 9.5px; text-decoration: none; padding: 0 2px; border-radius: 3px; }
  .lw-day u.off { background: #4f9e74; } .lw-day u.work { background: #b8733f; }
  .lw-week .lw-day { padding: 8px 0; } .lw-week .lw-day .n { font-size: 20px; } .lw-week .lw-day .l { font-size: 11.5px; white-space: normal; }
  .lw-detail { margin-top: 8px; font-size: 13px; opacity: .85; }
  .lw-detail:empty { display: none; }
  .lw-rate { display: flex; align-items: baseline; justify-content: space-between; gap: 10px; padding: 2px 0; }
  .lw-rate .cur { font-size: 14px; } .lw-rate .val { font-size: 24px; font-variant-numeric: tabular-nums; }
  .lw-pair { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .lw-pair small { display: block; font-size: 11.5px; opacity: .65; white-space: nowrap; }
  .lw-pair b { font-weight: 400; font-size: 17px; font-variant-numeric: tabular-nums; }
  .lw-kv { display: flex; justify-content: space-between; align-items: baseline; gap: 10px; font-size: 14px; }
  .lw-kv b { font-weight: 400; font-size: 21px; font-variant-numeric: tabular-nums; }
  .lw-kv .lw-btn { font-size: 12.5px; }
  /* Pages lie on top of each other; only the current one is visible. A page turn slides the current
     and the next page side by side, fully opaque, clipped by the viewport, so text never overlaps. */
  .lw-viewport { overflow: hidden; }
  .lw-track { display: grid; }
  .lw-page { grid-area: 1 / 1; align-self: start; min-width: 0; visibility: hidden; }
  .lw-page.cur, .lw-page.out { visibility: visible; }
  .lw-page.out { pointer-events: none; }
  .lw-page.cur, .lw-page.out { will-change: auto; }

  .lw-tabs { position: relative; display: flex; padding-bottom: 3px; font-size: 12px; }
  .lw-tab { flex: 1; text-align: center; padding: 2px 0 3px; opacity: .5; transition: opacity .3s; }
  .lw-tab.cur { opacity: 1; }
  .lw-tab-ind { position: absolute; left: 0; bottom: 0; height: 2px; border-radius: 1px; background: rgba(255,255,255,.62);
    transition: transform .45s ${EASE}, width .45s ${EASE}; }
  .lw-ico { width: 1em; height: 1em; vertical-align: -.14em; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; }
  .lw-wx { display: flex; align-items: center; gap: 12px; }
  .lw-wx .big { font-size: 40px; line-height: 1; color: #ffe9b8; }
  .lw-wx .temp { font-size: 34px; line-height: 1; font-variant-numeric: tabular-nums; }
  .lw-wx .desc { font-size: 14px; }
  .lw-wx .place { margin-left: auto; text-align: right; font-size: 13px; opacity: .85; }
  .lw-fc { display: grid; grid-template-columns: repeat(4, 1fr); gap: 4px; margin-top: 10px; text-align: center; font-size: 12px; }
  .lw-fc .i { font-size: 20px; margin: 3px 0 2px; color: #ffe9b8; }
  .lw-fc .t { font-variant-numeric: tabular-nums; }
  .lw-fc .r { font-size: 11px; color: #a9d6f0; min-height: 13px; }
  .lw-clk { display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 4px 0; }
  .lw-clk + .lw-clk { border-top: 1px solid rgba(255,255,255,.1); }
  .lw-clk .nm { font-size: 14px; } .lw-clk small { display: block; font-size: 11.5px; opacity: .65; }
  .lw-clk .tm { font-size: 24px; font-variant-numeric: tabular-nums; }
  .lw-clk .dn { font-size: 14px; margin-right: 8px; opacity: .8; }
  .lw-clk > span:last-child { display: flex; align-items: center; white-space: nowrap; }
  body.lw-night .lw-bg { background: rgba(10, 18, 30, .44); }
  `;

  // ---- Calendar ------------------------------------------------------------------------------------
  const WEEK_ZH = ['星期日', '星期一', '星期二', '星期三', '星期四', '星期五', '星期六'];
  const WD_ZH = ['一', '二', '三', '四', '五', '六', '日'], WD_EN = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'];
  const TERMS = ['春分', '清明', '谷雨', '立夏', '小满', '芒种', '夏至', '小暑', '大暑', '立秋', '处暑', '白露',
    '秋分', '寒露', '霜降', '立冬', '小雪', '大雪', '冬至', '小寒', '大寒', '立春', '雨水', '惊蛰'];
  const SOLAR_FEST = { '1-1': '元旦', '2-14': '情人节', '3-8': '妇女节', '3-12': '植树节', '5-1': '劳动节', '5-4': '青年节', '6-1': '儿童节',
    '7-1': '建党节', '8-1': '建军节', '9-10': '教师节', '10-1': '国庆节', '12-25': '圣诞节' };
  const LUNAR_FEST = { '1-1': '春节', '1-15': '元宵节', '2-2': '龙抬头', '5-5': '端午节', '7-7': '七夕', '7-15': '中元节', '8-15': '中秋节', '9-9': '重阳节', '12-8': '腊八', '12-23': '小年' };
  const LUNAR_MONTHS = ['正', '二', '三', '四', '五', '六', '七', '八', '九', '十', '冬', '腊'];

  // Apparent solar longitude (degrees); accurate to ~0.01°, so the solar-term day is right except
  // within an hour or so of a boundary.
  function sunLongitude(date) {
    const n = date.getTime() / 86400000 + 2440587.5 - 2451545.0;
    const L = (280.46 + 0.9856474 * n) % 360, g = ((357.528 + 0.9856003 * n) % 360) * Math.PI / 180;
    return ((L + 1.915 * Math.sin(g) + 0.02 * Math.sin(2 * g)) % 360 + 360) % 360;
  }
  const termCache = new Map();
  function termOn(day) { // the solar term that begins on this calendar day, if any
    const key = `${day.getFullYear()}-${day.getMonth()}-${day.getDate()}`;
    if (termCache.has(key)) return termCache.get(key);
    // Solar-term dates are reckoned in China Standard Time (UTC+8), wherever the PC is: 立春 2026 is 4 February
    // even where it is still the 3rd.
    const a = new Date(Date.UTC(day.getFullYear(), day.getMonth(), day.getDate()) - 8 * 3600000), b = new Date(a.getTime() + 86400000);
    const la = sunLongitude(a), lb = sunLongitude(b), ka = Math.floor(la / 15), kb = Math.floor((lb < la ? lb + 360 : lb) / 15);
    const t = kb !== ka ? TERMS[kb % 24] : null;
    termCache.set(key, t);
    return t;
  }
  function nextTerm(day) {
    for (let i = 1; i <= 17; i++) { const d = new Date(day.getFullYear(), day.getMonth(), day.getDate() + i), t = termOn(d); if (t) return { name: t, days: i }; }
    return null;
  }
  let lunarFmt = null;
  const lunarCache = new Map();
  function lunar(day) {
    const key = `${day.getFullYear()}-${day.getMonth()}-${day.getDate()}`;
    if (lunarCache.has(key)) return lunarCache.get(key);
    let result = null;
    try {
      lunarFmt = lunarFmt || new Intl.DateTimeFormat('zh-CN-u-ca-chinese', { year: 'numeric', month: 'numeric', day: 'numeric' });
      const parts = lunarFmt.formatToParts(new Date(day.getFullYear(), day.getMonth(), day.getDate(), 12)), get = t => (parts.find(p => p.type === t) || {}).value;
      // ICU gives the month as Chinese text ("八月", "闰六月", "十一月"/"冬月") and the lunar year as a
      // Gregorian number (relatedYear); the stem-branch year name is derived from it.
      const monthRaw = get('month') || '', dayNum = Number(get('day')), related = Number(get('relatedYear'));
      const leap = /闰/.test(monthRaw), mName = monthRaw.replace(/[闰月]/g, '');
      const m = /^\d+$/.test(mName) ? Number(mName) : ['正', '二', '三', '四', '五', '六', '七', '八', '九', '十', '十一', '十二'].indexOf(mName) + 1 || (mName === '冬' ? 11 : mName === '腊' ? 12 : 0);
      const yearName = get('yearName') || (related ? '甲乙丙丁戊己庚辛壬癸'[((related - 4) % 10 + 10) % 10] + '子丑寅卯辰巳午未申酉戌亥'[((related - 4) % 12 + 12) % 12] + '年' : '');
      if (m && dayNum) {
        const dayName = dayNum === 10 ? '初十' : dayNum === 20 ? '二十' : dayNum === 30 ? '三十'
          : ['初', '十', '廿', '三'][Math.floor(dayNum / 10)] + '一二三四五六七八九十'[(dayNum - 1) % 10];
        const monthName = `${leap ? '闰' : ''}${LUNAR_MONTHS[m - 1]}月`;
        result = { yearName, month: m, day: dayNum, leap, dayName, monthName, text: `${yearName ? yearName + ' ' : ''}${monthName}${dayName}` };
      }
    } catch { result = null; }
    lunarCache.set(key, result);
    return result;
  }
  // Festivals on a day (solar, lunar, Qingming, New Year's Eve), most important first.
  function festivals(day, l = lunar(day)) {
    const out = [];
    if (l && !l.leap) {
      const f = LUNAR_FEST[`${l.month}-${l.day}`]; if (f) out.push(f);
      const t = lunar(new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1));
      if (t && t.month === 1 && t.day === 1) out.push('除夕');
    }
    const s = SOLAR_FEST[`${day.getMonth() + 1}-${day.getDate()}`]; if (s) out.push(s);
    if (termOn(day) === '清明') out.push('清明节');
    return out;
  }
  function festival(day, l) { return festivals(day, l)[0] || null; }

  // ---- State ---------------------------------------------------------------------------------------
  // lang: the "Card language" property: '中文' or 'English' force a language, anything else ("Same as Chao Desk")
  // follows the app's language (appLang, from the host's init and "language" messages; Chinese until told).
  const opts = { clock: 'Top right', lang: 'Same as Chao Desk', h24: true, clocks: ['北京', '悉尼'], bocLink: false };
  let appLang = 'zh';
  // Until the host says otherwise (older hosts never do).
  let order = ['calendar', 'rate', 'weather', 'worldclock'], layout = 'stack', display = 'peek', perPage = 0;
  let tasks = [], rates = [], ratesAt = '', ratesSource = 'https://www.boc.cn/sourcedb/whpj/', holidays = new Map();
  let weather = null, system = null, shortcuts = [];
  let cardOpen = false, lastTouch = 0, calOpen = false, calMode = 'month', view = new Date(), selected = null;
  let pageIdx = 0;
  let pages = [], paged = '';
  let rootEl, card, ui = {}, mods = {}, paused = false, lastRender = '';
  const AUTO_CLOSE_MS = 30_000;
  const justDone = new Set(); // tasks ticked off on the desktop stay visible (struck through) for a while
  const zh = () => opts.lang === '中文' || (opts.lang !== 'English' && appLang === 'zh');
  // A Settings page path as the app shows it (in the app's language, which may differ from a forced card language).
  const settingsPath = (en, cn) => appLang === 'zh' ? cn : en;
  // 12-hour clock: "上午 9:05" in Chinese, "9:05 am" in English (size: the am/pm text's font size).
  const time12 = (h, m, size) => zh() ? `<span style="font-size:${size}px">${h < 12 ? '上午' : '下午'} </span>${h % 12 || 12}:${pad2(m)}`
    : `${h % 12 || 12}:${pad2(m)}<span style="font-size:${size}px"> ${h < 12 ? 'am' : 'pm'}</span>`;
  const pad2 = n => String(n).padStart(2, '0');
  const ymd = d => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
  const sameDay = (a, b) => a && b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  // Only the Australian dollar is shown (Bank of China name).
  const AUD = '澳大利亚元';
  const MODULES = ['calendar', 'clock', 'rate', 'weather', 'worldclock', 'tasks', 'system', 'shortcuts'];
  const TAB_NAMES = { calendar: ['日历', 'Date'], clock: ['时钟', 'Clock'], rate: ['汇率', 'AUD'], weather: ['天气', 'Weather'],
    worldclock: ['世界钟', 'World'], tasks: ['待办', 'Tasks'], system: ['电脑', 'PC'], shortcuts: ['快捷', 'Links'] };
  const peeking = () => display === 'peek' && !cardOpen;

  function el(tag, cls, parent) { const e = document.createElement(tag); if (cls) e.className = cls; if (parent) parent.appendChild(e); return e; }
  // A section body that unfolds (grid rows 0fr → 1fr).
  function fold(parent, bodyCls) { const exp = el('div', 'lw-exp', parent); return { exp, body: el('div', `lw-exp-body ${bodyCls || ''}`, el('div', 'lw-exp-in', exp)) }; }
  // Writes only when the markup changed, so unchanged nodes keep their transitions.
  function setHtml(node, html) { if (node._html !== html) { node._html = html; node.innerHTML = html; } }

  function build() {
    if (rootEl) return;
    const style = document.createElement('style'); style.textContent = CSS; document.head.appendChild(style);
    rootEl = el('div', '', document.body); rootEl.id = 'lotus-widgets';
    ui.stack = el('div', 'lw-stack tr', rootEl);
    card = el('div', 'lw-card', ui.stack);
    ui.bg = el('i', 'lw-bg', card);
    // The month panel beside the card.
    ui.panel = el('div', 'lw-panel', ui.stack);
    ui.panelBg = el('i', 'lw-bg', ui.panel);
    ui.ups = el('div', 'lw-ups', ui.panel);
    ui.peek = el('div', 'lw-peek', ui.stack); ui.peek.setAttribute('data-act', 'open-card');
    for (const id of MODULES) { mods[id] = el('div', 'lw-mod'); mods[id].setAttribute('data-mod', id); }
    // Calendar: the info line (with upcoming days beside it when open), this week's strip, the month.
    ui.clock = el('div', '', mods.calendar); ui.clock.setAttribute('data-act', 'toggle-cal');
    ui.chev = el('i', 'lw-chev', mods.calendar); ui.chev.setAttribute('data-act', 'toggle-cal');
    ui.strip = { body: el('div', '', mods.calendar) }; ui.strip.body.setAttribute('data-act', 'toggle-cal');
    ui.cal = { body: el('div', 'lw-cal-body', ui.panel) };
    ui.bigClock = el('div', '', mods.clock);
    ui.rate = mods.rate;
    ui.weather = mods.weather; ui.clocks = mods.worldclock; ui.tasks = mods.tasks; ui.system = mods.system; ui.shortcuts = mods.shortcuts;
    // Pages: a viewport with a sliding track; each page holds one or more widgets.
    ui.inner = el('div', 'lw-inner', card);
    ui.view = el('div', 'lw-viewport', ui.inner);
    ui.track = el('div', 'lw-track', ui.view);
    ui.pageEls = [];
    ui.nav = el('div', '', ui.inner);
    if (typeof ResizeObserver === 'function') { const ro = new ResizeObserver(() => syncPager()); for (const id of MODULES) ro.observe(mods[id]); }
    addEventListener('resize', () => { paged = ''; render(true); });
    setInterval(tick, 1000); tick();
  }

  // Room for one page of stacked widgets: the screen minus the card's margins, padding and dots.
  const pageRoom = () => innerHeight - 26 - 90 - 32 - 26;

  // Height of a widget in its resting state (the calendar folded), remembered while it is folded away.
  // Measured without the separator a widget gets when it is not first on its page, so the height is the
  // same wherever the widget sits. (Including it made the split flip between two layouts every second,
  // rebuilding the pages and breaking every animation.)
  function restHeight(id) {
    const m = mods[id];
    if (m.isConnected) { const cs = getComputedStyle(m); m._h = m.offsetHeight - parseFloat(cs.paddingTop) - parseFloat(cs.borderTopWidth); }
    return m._h || 60;
  }

  // Splits the enabled widgets into pages (tabs: one each; stacked: as many as fit) and puts them there.
  function paginate() {
    // Never rebuild pages under a drag or a page turn.
    if (drag && drag.on) return;
    if (ui.pageEls && ui.pageEls.some(p => p.classList.contains('out'))) return;
    let next;
    if (layout === 'tabs') next = order.map(id => [id]);
    else {
      next = []; let cur = [], h = 0;
      for (const id of order) {
        const mh = restHeight(id), gap = cur.length ? 24 : 0; // separator: margin 12 + padding 11 + border 1
        if (cur.length && (h + gap + mh > pageRoom() || (perPage && cur.length >= perPage))) { next.push(cur); cur = [id]; h = mh; }
        else { cur.push(id); h += gap + mh; }
      }
      if (cur.length) next.push(cur);
    }
    const key = layout + '|' + next.map(p => p.join(',')).join('/');
    if (key === paged) return;
    paged = key; pages = next;
    for (const id of MODULES) if (!order.includes(id)) mods[id].remove();
    while (ui.pageEls.length < pages.length) ui.pageEls.push(el('div', 'lw-page'));
    ui.pageEls.forEach((p, i) => {
      if (i >= pages.length) { p.remove(); return; }
      p.className = `lw-page${layout === 'tabs' ? '' : ' lw-spage'}`;
      ui.track.appendChild(p);
      for (const id of pages[i]) p.appendChild(mods[id]);
    });
    if (!order.includes('calendar')) calOpen = false;
    card.classList.toggle('lw-hide', order.length === 0);
    ui.nav._html = null;
  }

  const pageOf = id => pages.findIndex(p => p.includes(id));

  function goPage(i, fromDx = 0) {
    const n = pages.length;
    const dir = i > pageIdx ? 1 : -1; // from the unwrapped index: › always slides forward
    i = ((i % n) + n) % n; // the arrows wrap around
    if (i === pageIdx) return;
    const old = ui.pageEls[pageIdx], nw = ui.pageEls[i], W = slideWidth();
    for (const p of ui.pageEls) { for (const a of p.getAnimations()) a.cancel(); p.classList.remove('out'); p.style.transform = ''; }
    resizeGlass(ui.bg, card, () => { pageIdx = i; syncPager(); });
    old.classList.add('out');
    const opts = { duration: fromDx ? 260 : 340, easing: EASE };
    const out = old.animate([{ transform: `translateX(${fromDx}px)` }, { transform: `translateX(${-dir * W}px)` }], { ...opts, fill: 'forwards' });
    out.onfinish = () => { old.classList.remove('out'); out.cancel(); };
    nw.animate([{ transform: `translateX(${fromDx + dir * W}px)` }, { transform: 'none' }], opts);
  }

  // How far a page travels when it slides out: the page width plus a gap, so two never touch.
  const slideWidth = () => (ui.view.offsetWidth || 264) + 36;

  // Applies a change that alters a box's height, then eases only its glass layer (an empty, absolutely
  // placed element, so this costs no layout) from the old height to the new one. Text is never hidden or
  // scaled; a new call cancels the previous animation, so rapid clicks cannot pile up.
  function resizeGlass(bg, box, change) {
    for (const a of bg.getAnimations()) a.cancel();
    const h0 = box.offsetHeight;
    change();
    const h1 = box.offsetHeight;
    if (h0 && h1 && Math.abs(h0 - h1) > 1) bg.animate([{ height: `${h0}px` }, { height: `${h1}px` }], { duration: 360, easing: EASE });
  }

  function navHtml() {
    if (pages.length < 2) return '';
    if (layout === 'tabs') return pages.map((p, i) => `<span class="lw-tab" data-act="page" data-page="${i}">${TAB_NAMES[p[0]][zh() ? 0 : 1]}</span>`).join('') + '<i class="lw-tab-ind"></i>';
    return `<span class="arr" data-act="page-prev">‹</span><span class="pips">${pages.map((p, i) => `<span data-act="page" data-page="${i}"><i></i></span>`).join('')}</span><span class="arr" data-act="page-next">›</span>`;
  }

  // Current page in view: track position, viewport height, tab / dot highlight.
  function syncPager() {
    if (!ui.track || !pages.length) return;
    pageIdx = Math.max(0, Math.min(pages.length - 1, pageIdx));
    ui.pageEls.forEach((p, i) => p.classList.toggle('cur', i === pageIdx));
    const h = ui.pageEls[pageIdx].offsetHeight;
    if (h) ui.view.style.height = `${h}px`;
    ui.nav.className = pages.length < 2 ? 'lw-hide' : layout === 'tabs' ? 'lw-tabs' : 'lw-dots';
    for (const t of ui.nav.querySelectorAll('[data-page]')) t.classList.toggle('cur', Number(t.getAttribute('data-page')) === pageIdx);
    const ind = ui.nav.querySelector('.lw-tab-ind');
    if (ind) { ind.style.width = `${100 / pages.length}%`; ind.style.transform = `translateX(${pageIdx * 100}%)`; }
    reportRects(); // the card's height follows the page
  }

  // Next official holiday block (from the published schedule), else the next big festival.
  function nextHoliday(now) {
    const today = ymd(now);
    for (const [date, h] of holidays) if (h.off && date >= today) {
      const prev = new Date(date + 'T12:00:00'); prev.setDate(prev.getDate() - 1);
      const p = holidays.get(ymd(prev));
      if (p && p.off && p.name === h.name && ymd(prev) >= today) continue; // not the first day of the block
      const days = Math.round((new Date(date + 'T12:00:00') - new Date(today + 'T12:00:00')) / 86400000);
      if (days <= 60) return { name: h.name, days };
    }
    for (let i = 0; i <= 40; i++) { const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + i), f = festivals(d).find(x => ['春节', '元宵节', '清明节', '端午节', '七夕', '中秋节', '重阳节', '国庆节', '元旦', '劳动节'].includes(x)); if (f) return { name: f, days: i }; }
    return null;
  }

  // ---- Weather and world clock -------------------------------------------------------------------
  // Minimal line icons (24×24, stroked with the text colour).
  const ICON = {
    sun: '<circle cx="12" cy="12" r="4.2"/><path d="M12 2.5v2.2M12 19.3v2.2M2.5 12h2.2M19.3 12h2.2M5.3 5.3l1.6 1.6M17.1 17.1l1.6 1.6M5.3 18.7l1.6-1.6M17.1 6.9l1.6-1.6"/>',
    moon: '<path d="M19.5 14.6A8 8 0 0 1 9.4 4.5a8 8 0 1 0 10.1 10.1z"/>',
    cloud: '<path d="M7 18.5h10.2a4.3 4.3 0 0 0 .6-8.6A6 6 0 0 0 6.3 11.2 3.7 3.7 0 0 0 7 18.5z"/>',
    part: '<path d="M8.5 3.5v1.4M3.5 8.5h1.4M4.9 4.9l1 1M12.1 4.9l-1 1"/><path d="M5.6 10.4a3.2 3.2 0 0 1 5.9-2.2"/><path d="M8.6 19.5h8.8a3.7 3.7 0 0 0 .5-7.4 5.2 5.2 0 0 0-9.9 1.1 3.2 3.2 0 0 0 .6 6.3z"/>',
    rain: '<path d="M7 15.5h10.2a4.3 4.3 0 0 0 .6-8.6A6 6 0 0 0 6.3 8.2 3.7 3.7 0 0 0 7 15.5z"/><path d="M8.5 18l-1 2.5M12.5 18l-1 2.5M16.5 18l-1 2.5"/>',
    snow: '<path d="M7 15.5h10.2a4.3 4.3 0 0 0 .6-8.6A6 6 0 0 0 6.3 8.2 3.7 3.7 0 0 0 7 15.5z"/><path d="M8.5 19h.01M12 20.5h.01M15.5 19h.01" stroke-width="2.4"/>',
    fog: '<path d="M4 9h16M6 13h12M4 17h16"/>',
    storm: '<path d="M7 15.5h10.2a4.3 4.3 0 0 0 .6-8.6A6 6 0 0 0 6.3 8.2 3.7 3.7 0 0 0 7 15.5z"/><path d="M12.5 15.5l-2 3.5h3l-2 3"/>',
  };
  const icon = name => `<svg class="lw-ico" viewBox="0 0 24 24">${ICON[name] || ICON.cloud}</svg>`;
  // WMO weather code → [Chinese, English, icon].
  function wmo(code, day = true) {
    const c = Number(code);
    if (c === 0) return ['晴', 'Clear', day ? 'sun' : 'moon'];
    if (c === 1) return ['晴间多云', 'Mostly clear', day ? 'part' : 'moon'];
    if (c === 2) return ['多云', 'Partly cloudy', 'part'];
    if (c === 3) return ['阴', 'Overcast', 'cloud'];
    if (c === 45 || c === 48) return ['雾', 'Fog', 'fog'];
    if (c >= 51 && c <= 57) return ['毛毛雨', 'Drizzle', 'rain'];
    if (c === 61) return ['小雨', 'Light rain', 'rain'];
    if (c === 63) return ['中雨', 'Rain', 'rain'];
    if (c === 65) return ['大雨', 'Heavy rain', 'rain'];
    if (c === 66 || c === 67) return ['冻雨', 'Freezing rain', 'rain'];
    if (c === 71) return ['小雪', 'Light snow', 'snow'];
    if (c === 73) return ['中雪', 'Snow', 'snow'];
    if (c === 75 || c === 77) return ['大雪', 'Heavy snow', 'snow'];
    if (c === 80) return ['阵雨', 'Showers', 'rain'];
    if (c === 81 || c === 82) return ['强阵雨', 'Heavy showers', 'rain'];
    if (c === 85 || c === 86) return ['阵雪', 'Snow showers', 'snow'];
    if (c >= 95) return ['雷阵雨', 'Thunderstorm', 'storm'];
    return ['—', '—', 'cloud'];
  }
  const beaufort = kmh => [1, 6, 12, 20, 29, 39, 50, 62, 75, 89, 103, 118].filter(x => kmh >= x).length;
  function aqiText(v) {
    if (v == null) return '';
    const zhName = v <= 50 ? '优' : v <= 100 ? '良' : v <= 150 ? '轻度污染' : v <= 200 ? '中度污染' : v <= 300 ? '重度污染' : '严重污染';
    return zh() ? `空气${zhName} ${v}` : `AQI ${v}`;
  }

  function weatherHtml() {
    const w = weather;
    if (!w || !w.city) {
      const where = settingsPath('Settings → General → Weather city', '设置 → 通用 → 天气城市');
      return `<div class="lw-muted">${zh() ? `在 ${where} 填写城市，即可显示天气` : `Set a city in ${where}`}</div>`;
    }
    if (w.error && !w.now) return `<div class="lw-muted">${esc(/not found/i.test(w.error) ? (zh() ? `找不到城市「${w.city}」，换个写法试试` : `City not found: ${w.city}`) : (zh() ? '天气暂时获取不到，稍后会自动重试' : 'Weather is unavailable right now'))}</div>`;
    if (!w.now) return `<div class="lw-muted">${zh() ? '正在获取天气…' : 'Fetching the weather…'}</div>`;
    const n = w.now, [dz, de, ic] = wmo(n.code, n.isDay);
    let html = `<div class="lw-wx"><span class="big">${icon(ic)}</span><span class="temp">${Math.round(n.temp)}°</span><span class="desc">${esc(zh() ? dz : de)}<br><span class="lw-muted">${zh() ? '体感' : 'Feels'} ${n.feels == null ? '—' : Math.round(n.feels)}°</span></span>`;
    html += `<span class="place">${esc(w.place ? w.place.name : w.city)}<br><span class="lw-muted">${esc(w.fetchedAt || '')}</span></span></div>`;
    const bits = [`${zh() ? '湿度' : 'Humidity'} ${n.humidity}%`, n.wind == null ? '' : (zh() ? `风力 ${beaufort(n.wind)} 级` : `Wind ${Math.round(n.wind)} km/h`), aqiText(w.aqi)].filter(Boolean);
    const today = (w.days || [])[0];
    if (today && today.sunrise && today.sunset) bits.push(`${zh() ? '日落' : 'Sunset'} ${today.sunset.slice(11, 16)}`);
    html += `<div class="lw-muted" style="margin-top:7px">${esc(bits.join(' · '))}</div>`;
    const days = (w.days || []).slice(0, 4);
    if (days.length) {
      html += '<div class="lw-fc">';
      days.forEach((d, i) => {
        const date = new Date(d.date + 'T12:00:00'), [, , di] = wmo(d.code, true);
        const label = i === 0 ? (zh() ? '今天' : 'Today') : i === 1 ? (zh() ? '明天' : 'Tmrw') : zh() ? WEEK_ZH[date.getDay()].replace('星期', '周') : date.toLocaleDateString('en-GB', { weekday: 'short' });
        html += `<div><div>${label}</div><div class="i">${icon(di)}</div><div class="t">${d.max == null ? '—' : Math.round(d.max)}° / ${d.min == null ? '—' : Math.round(d.min)}°</div><div class="r">${d.rain >= 30 ? `${d.rain}%` : ''}</div></div>`;
      });
      html += '</div>';
    }
    return html;
  }

  // World clock. Chinese or English city names, or any IANA zone ("Australia/Sydney").
  const ZONES = { '悉尼': 'Australia/Sydney', 'sydney': 'Australia/Sydney', '墨尔本': 'Australia/Melbourne', 'melbourne': 'Australia/Melbourne',
    '布里斯班': 'Australia/Brisbane', 'brisbane': 'Australia/Brisbane', '珀斯': 'Australia/Perth', 'perth': 'Australia/Perth',
    '阿德莱德': 'Australia/Adelaide', 'adelaide': 'Australia/Adelaide', '堪培拉': 'Australia/Sydney', 'canberra': 'Australia/Sydney',
    '奥克兰': 'Pacific/Auckland', 'auckland': 'Pacific/Auckland', '东京': 'Asia/Tokyo', 'tokyo': 'Asia/Tokyo', '首尔': 'Asia/Seoul', 'seoul': 'Asia/Seoul',
    '新加坡': 'Asia/Singapore', 'singapore': 'Asia/Singapore', '香港': 'Asia/Hong_Kong', 'hong kong': 'Asia/Hong_Kong', '北京': 'Asia/Shanghai', 'beijing': 'Asia/Shanghai',
    '迪拜': 'Asia/Dubai', 'dubai': 'Asia/Dubai', '伦敦': 'Europe/London', 'london': 'Europe/London', '巴黎': 'Europe/Paris', 'paris': 'Europe/Paris',
    '柏林': 'Europe/Berlin', 'berlin': 'Europe/Berlin', '纽约': 'America/New_York', 'new york': 'America/New_York', '洛杉矶': 'America/Los_Angeles',
    'los angeles': 'America/Los_Angeles', '旧金山': 'America/Los_Angeles', 'san francisco': 'America/Los_Angeles', '温哥华': 'America/Vancouver',
    'vancouver': 'America/Vancouver', '多伦多': 'America/Toronto', 'toronto': 'America/Toronto' };
  const zoneFmt = new Map();
  function zoneTime(tz, now) {
    try {
      let fmt = zoneFmt.get(tz);
      if (!fmt) { fmt = new Intl.DateTimeFormat('en-GB', { timeZone: tz, year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric', hourCycle: 'h23' }); zoneFmt.set(tz, fmt); }
      const p = Object.fromEntries(fmt.formatToParts(now).map(x => [x.type, x.value]));
      const asUtc = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute);
      const offsetMin = Math.round((asUtc - Math.floor(now.getTime() / 60000) * 60000) / 60000) + now.getTimezoneOffset();
      return { h: +p.hour, m: +p.minute, dayDiff: Math.round((Date.UTC(+p.year, +p.month - 1, +p.day) - Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())) / 86400000), offsetMin };
    } catch (e) { return null; }
  }

  function clocksHtml(now) {
    let html = '';
    for (const name of opts.clocks) {
      const tz = ZONES[name.toLowerCase()] || (name.includes('/') ? name : null), t = tz && zoneTime(tz, now);
      if (!t) { html += `<div class="lw-clk"><span class="nm">${esc(name)}<small>${zh() ? '不认识这个城市' : 'Unknown city'}</small></span></div>`; continue; }
      const off = t.offsetMin, hrs = Math.abs(off) / 60, hrsText = Number.isInteger(hrs) ? String(hrs) : hrs.toFixed(1);
      const rel = off === 0 ? (zh() ? '与本地同时' : 'Same time') : zh() ? `${off > 0 ? '快' : '慢'} ${hrsText} 小时` : `${off > 0 ? '+' : '−'}${hrsText} h`;
      const day = t.dayDiff === 0 ? (zh() ? '今天' : 'Today') : t.dayDiff > 0 ? (zh() ? '明天' : 'Tomorrow') : (zh() ? '昨天' : 'Yesterday');
      const time = opts.h24 ? `${pad2(t.h)}:${pad2(t.m)}` : zh() ? `${t.h < 12 ? '上午' : '下午'} ${t.h % 12 || 12}:${pad2(t.m)}` : `${t.h % 12 || 12}:${pad2(t.m)}${t.h < 12 ? 'am' : 'pm'}`;
      html += `<div class="lw-clk"><span class="nm">${esc(name)}<small>${day} · ${rel}</small></span><span><span class="dn">${icon(t.h >= 6 && t.h < 18 ? 'sun' : 'moon')}</span><span class="tm">${time}</span></span></div>`;
    }
    return html;
  }

  // The next few festivals, holidays and solar terms (the side column of the open card).
  function upcoming(now, count) {
    const out = [], seen = new Set();
    for (let i = 1; i <= 120 && out.length < count; i++) {
      const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + i), h = holidays.get(ymd(d));
      const names = festivals(d).map(n => ({ n, term: false }));
      if (h && h.off) names.unshift({ n: h.name, term: false });
      const t = termOn(d); if (t && t !== '清明') names.push({ n: t, term: true });
      for (const x of names) if (!seen.has(x.n) && out.length < count) { seen.add(x.n); out.push({ name: x.n, term: x.term, date: d, days: i }); }
    }
    return out;
  }

  function upsHtml(now) {
    if (!zh()) return '';
    return upcoming(now, 3).map(u => `<div><span class="${u.term ? 'term' : ''}">${esc(u.name)}</span><small>${u.date.getMonth() + 1}/${u.date.getDate()} · <b>${u.days} 天</b></small></div>`).join('');
  }

  // ---- Widgets --------------------------------------------------------------------------------------
  // Calendar head: lunar date, solar term and the next holiday on one line (upcoming days beside it
  // when open); this week's strip below it folds away when the month opens.
  function calendarHead(now) {
    let html = '<div class="lw-head"><div class="lw-head-main">';
    if (zh()) {
      const l = lunar(now), term = termOn(now), next = term ? null : nextTerm(now), f = festivals(now), nh = nextHoliday(now);
      const count = f.length ? `${esc(f.join(' · '))}快乐` : nh ? (nh.days === 0 ? `今天是${esc(nh.name)}` : `距${esc(nh.name)} ${nh.days} 天`) : '';
      html += `<div class="lw-cal-info">${now.getMonth() + 1}月 · ${esc(l ? `${l.yearName}${l.monthName}${l.dayName}` : '')}</div>`;
      html += `<div class="lw-cal-info lw-muted" style="margin-top:2px">${esc(term ? `今日${term}` : next ? `${next.days}天后${next.name}` : '')}${count ? ` · <span class="lw-count">${count}</span>` : ''}</div>`;
    } else {
      html += `<div class="lw-cal-info">${now.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' })}</div>`;
    }
    return html + '</div></div>';
  }

  function stripHtml(now) {
    const s = weekStart(now);
    let html = `<div class="lw-grid lw-strip">${(zh() ? WD_ZH : WD_EN).map(w => `<div class="lw-wd">${w}</div>`).join('')}`;
    for (let i = 0; i < 7; i++) { const d = new Date(s); d.setDate(s.getDate() + i); html += dayCell(d, null, now, false).replace(' data-act="day"', ''); }
    return html + '</div>';
  }

  function peekHtml(now) {
    const h = now.getHours();
    const time = opts.h24 ? `${pad2(h)}:${pad2(now.getMinutes())}` : time12(h, now.getMinutes(), 22);
    const l = lunar(now);
    const date = zh() ? `${now.getMonth() + 1}月${now.getDate()}日 ${WEEK_ZH[now.getDay()].replace('星期', '周')}${l ? ` · ${l.monthName}${l.dayName}` : ''}`
      : now.toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' });
    return `<div class="t">${time}</div><div class="d">${esc(date)}</div>`;
  }

  function audRate() { return rates.find(r => r.currency === AUD) || null; }
  const money = v => (v == null ? '—' : v.toFixed(2));

  // AUD: everything at a glance, no unfolding.
  function rateHtml() {
    const r = audRate(), sell = r && (r.sell ?? r.cashSell);
    const time = r ? String(r.published || '').split(' ').pop().slice(0, 5) : '';
    const link = opts.bocLink ? ` · <span class="lw-link" data-act="open-boc">${zh() ? '中行' : 'boc.cn'} ↗</span>` : '';
    let html = `<div class="lw-fx-title"><span>${zh() ? '澳元 AUD · 中国银行' : 'AUD · Bank of China'}</span><span>${esc(time)}${link}</span></div>`;
    if (!rates.length) return html + `<div class="lw-muted">${zh() ? '正在获取中国银行牌价…' : 'Fetching the Bank of China rate…'}</div>`;
    if (!r) return html + `<div class="lw-muted">${zh() ? '暂无澳元牌价' : 'No AUD rate yet'}</div>`;
    const col = (label, value) => `<div><small>${label}</small><b>${value}</b></div>`;
    return html + `<div class="lw-fx">${col(zh() ? '现汇卖出' : 'Selling', money(sell))}${col(zh() ? '现汇买入' : 'Buying', money(r.buy))}${col(zh() ? '1 元人民币' : '1 CNY', sell ? (100 / sell).toFixed(4) : '—')}</div>`;
  }

  function bigClockHtml(now) {
    const h = now.getHours();
    const time = opts.h24 ? `${pad2(h)}:${pad2(now.getMinutes())}` : time12(h, now.getMinutes(), 18);
    const day = order.includes('calendar') ? '' : `<div class="lw-date">${zh() ? `${now.getMonth() + 1}月${now.getDate()}日 ${WEEK_ZH[now.getDay()]}` : now.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' })}</div>`;
    return `<div class="lw-time">${time}</div>${day}`;
  }

  // Computer status: persistent rows, so the bars glide from one sample to the next.
  function renderSystem() {
    const s = system;
    if (!s) { setHtml(ui.system, `<div class="lw-muted">${zh() ? '正在读取电脑状态…' : 'Reading computer status…'}</div>`); ui.system._rows = null; return; }
    const rows = [
      ['cpu', 'CPU', s.cpu, `${s.cpu}%`],
      ['mem', zh() ? '内存' : 'RAM', s.mem, `${s.mem}% · ${s.memUsed}/${Math.round(s.memTotal)} GB`],
      s.battery == null ? null : ['bat', zh() ? '电池' : 'Batt', s.battery, `${s.battery}%${s.charging ? (zh() ? ' · 充电中' : ' · charging') : ''}`],
      ['disk', s.drive || 'C:', s.diskTotal ? Math.round(100 * (1 - s.diskFree / s.diskTotal)) : 0, zh() ? `剩余 ${Math.round(s.diskFree)} GB` : `${Math.round(s.diskFree)} GB free`],
    ].filter(Boolean);
    const shape = rows.map(r => r[0]).join() + '|' + zh();
    if (ui.system._rows !== shape) {
      ui.system._rows = shape;
      setHtml(ui.system, rows.map(r => `<div class="lw-meter" data-k="${r[0]}"><span>${esc(r[1])}</span><span class="bar"><i></i></span><span class="v"></span></div>`).join(''));
    }
    for (const [k, , pct, text] of rows) {
      const row = ui.system.querySelector(`[data-k="${k}"]`); if (!row) continue;
      const bar = row.querySelector('i'), low = k === 'bat';
      bar.style.transform = `scaleX(${Math.max(0, Math.min(100, pct)) / 100})`;
      bar.classList.toggle('hot', low ? pct <= 20 : pct >= 85);
      row.querySelector('.v').textContent = text;
    }
  }

  const SC_ICON = {
    web: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c2.6 2.4 3.9 5.2 3.9 8.5s-1.3 6.1-3.9 8.5c-2.6-2.4-3.9-5.2-3.9-8.5s1.3-6.1 3.9-8.5z"/>',
    folder: '<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>',
    app: '<rect x="4.5" y="4.5" width="15" height="15" rx="4"/><path d="M9 12h6M12 9v6"/>',
  };
  function shortcutsHtml() {
    if (!shortcuts.length) {
      return `<div class="lw-muted">${zh() ? `在 ${settingsPath('Settings → Widgets → Shortcuts', '设置 → 小组件 → 快捷方式')} 添加常用网站、文件夹或程序`
        : `Add websites, folders or programs in ${settingsPath('Settings → Widgets', '设置 → 小组件')}`}</div>`;
    }
    return '<div class="lw-sc">' + shortcuts.map(s => `<div data-act="shortcut" data-id="${esc(s.id)}"><span class="ic"><svg class="lw-ico" viewBox="0 0 24 24">${SC_ICON[s.kind] || SC_ICON.app}</svg></span><div class="nm">${esc(s.name)}</div></div>`).join('') + '</div>';
  }

  // ---- Calendar month and tasks ------------------------------------------------------------------
  function tasksHtml() {
    const list = tasks.filter(t => !t.done || justDone.has(t.id)).slice(0, 6);
    const pending = tasks.filter(t => !t.done).length;
    let html = `<div class="lw-sec-title"><span>${zh() ? '我的待办' : 'My tasks'}</span><span>${pending ? (zh() ? `${pending} 项` : `${pending} to do`) : ''}</span></div>`;
    for (const t of list) html += `<div class="lw-task${t.done ? ' done' : ''}" data-act="task" data-id="${esc(t.id)}"><i></i><span>${esc(t.text)}</span></div>`;
    return html;
  }

  function dayCell(d, month, now, week) {
    const l = lunar(d), f = festivals(d, l), term = termOn(d), h = holidays.get(ymd(d));
    let label = '', cls = '';
    if (zh()) {
      if (f.length) { label = f[0]; cls = ' fest'; }
      else if (term) { label = term; cls = ' term'; }
      else if (l) label = l.day === 1 ? l.monthName : l.dayName;
    }
    if (week && zh() && l && !f.length && !term) label = `${l.monthName}${l.dayName}`;
    const wk = d.getDay() === 0 || d.getDay() === 6;
    cls += (month !== null && d.getMonth() !== month ? ' other' : '') + (wk ? ' wkend' : '') + (sameDay(d, now) ? ' today' : '') + (sameDay(d, selected) ? ' sel' : '');
    const badge = h ? `<u class="${h.off ? 'off' : 'work'}">${h.off ? '休' : '班'}</u>` : '';
    return `<div class="lw-day${cls}" data-act="day" data-date="${ymd(d)}">${badge}<div class="n">${d.getDate()}</div><div class="l">${esc(label)}</div></div>`;
  }

  function calendarHtml(now) {
    const y = view.getFullYear(), m = view.getMonth();
    const title = calMode === 'month' ? (zh() ? `${y}年${m + 1}月` : view.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' }))
      : (() => { const s = weekStart(view), e = new Date(s); e.setDate(e.getDate() + 6); return zh() ? `${s.getMonth() + 1}月${s.getDate()}日 – ${e.getMonth() + 1}月${e.getDate()}日` : `${s.getDate()}/${s.getMonth() + 1} – ${e.getDate()}/${e.getMonth() + 1}`; })();
    let html = `<div class="lw-sec"><div class="lw-cal-bar"><span class="lw-btn" data-act="prev">‹</span><b>${title}</b><span class="lw-btn" data-act="next">›</span>`;
    html += `<span class="lw-btn${calMode === 'month' ? ' on' : ''}" data-act="mode-month">${zh() ? '月' : 'M'}</span><span class="lw-btn${calMode === 'week' ? ' on' : ''}" data-act="mode-week">${zh() ? '周' : 'W'}</span><span class="lw-btn" data-act="today">${zh() ? '今天' : 'Today'}</span></div>`;
    html += `<div class="lw-grid-wrap"><div class="lw-grid${calMode === 'week' ? ' lw-week' : ''}">${(zh() ? WD_ZH : WD_EN).map(w => `<div class="lw-wd">${w}</div>`).join('')}`;
    if (calMode === 'month') {
      const first = new Date(y, m, 1), start = new Date(first); start.setDate(1 - ((first.getDay() + 6) % 7));
      for (let i = 0; i < 42; i++) { const d = new Date(start); d.setDate(start.getDate() + i); html += dayCell(d, m, now, false); }
    } else {
      const s = weekStart(view);
      for (let i = 0; i < 7; i++) { const d = new Date(s); d.setDate(s.getDate() + i); html += dayCell(d, null, now, true); }
    }
    html += `</div></div><div class="lw-detail">${selected && !sameDay(selected, now) ? detailText(selected) : ''}</div></div>`;
    return html;
  }

  function weekStart(d) { const s = new Date(d.getFullYear(), d.getMonth(), d.getDate()); s.setDate(s.getDate() - ((s.getDay() + 6) % 7)); return s; }

  function detailText(d) {
    if (!zh()) return esc(d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }));
    const l = lunar(d), f = festivals(d, l), term = termOn(d), h = holidays.get(ymd(d));
    const names = f.filter(x => !h || x !== h.name); // the official holiday line already names it
    const bits = [`${d.getMonth() + 1}月${d.getDate()}日 ${WEEK_ZH[d.getDay()]}`, l ? `农历${l.monthName}${l.dayName}` : '', ...names, term && !f.includes(term) && !f.includes(term + '节') ? term : '', h ? `${h.name}${h.off ? '（休）' : '（调休上班）'}` : ''];
    return esc(bits.filter(Boolean).join(' · '));
  }

  function render(force) {
    if (!rootEl || paused) return;
    const now = new Date(), pos = { 'Top right': 'tr', 'Top left': 'tl', 'Bottom right': 'br', 'Bottom left': 'bl' }[opts.clock];
    const peek = peeking();
    const cls = `lw-stack ${pos || 'tr'}${pos ? '' : ' lw-hide'}${peek ? ' peek' : ' opened'}${calOpen && !peek ? ' cal' : ''}`;
    if (ui.stack.className !== cls) ui.stack.className = cls;
    ui.peek.classList.toggle('lw-hide', display !== 'peek');
    // Rebuild only when something visible changed (the minute, data, or a click).
    const key = `${now.getFullYear()}${now.getMonth()}${now.getDate()}${now.getHours()}${now.getMinutes()}|${zh()}|${opts.h24}`;
    if (force || key !== lastRender) {
      if (key !== lastRender) justDone.clear();
      lastRender = key;
      // Each widget renders on its own: an error in one never leaves the others stale.
      const each = (id, fn) => { if (id && !order.includes(id)) return; try { fn(); } catch (e) { console.error(`widget ${id || 'peek'}: ${e && e.message}`); } };
      if (display === 'peek') each(null, () => setHtml(ui.peek, peekHtml(now)));
      each('calendar', () => { setHtml(ui.clock, calendarHead(now)); setHtml(ui.strip.body, stripHtml(now)); if (calOpen) { setHtml(ui.ups, upsHtml(now)); setHtml(ui.cal.body, calendarHtml(now)); } }); // a closing panel keeps its content while it fades
      each('clock', () => setHtml(ui.bigClock, bigClockHtml(now)));
      each('rate', () => setHtml(ui.rate, rateHtml()));
      each('weather', () => setHtml(ui.weather, weatherHtml()));
      each('worldclock', () => setHtml(ui.clocks, clocksHtml(now)));
      each('tasks', () => setHtml(ui.tasks, tasksHtml()));
      each('system', renderSystem);
      each('shortcuts', () => setHtml(ui.shortcuts, shortcutsHtml()));
    }
    paginate();
    setHtml(ui.nav, navHtml());
    ui.chev.classList.toggle('open', calOpen);
    syncPager();
    reportRects();
  }

  // ---- Dragging ------------------------------------------------------------------------------------
  // The host forwards press / move / release (and keeps presses on the card from Explorer). A
  // horizontal drag on the card pulls the current page along; letting go past a third of the way (or
  // flicking) turns the page, otherwise it springs back. A press that barely moves stays a click.
  let drag = null;
  function neighbour(sign) { const n = pages.length; return ((pageIdx + sign) % n + n) % n; }
  function pointer(kind, x, y) {
    if (kind === 'down') {
      const t = document.elementFromPoint(x, y);
      drag = card && t && card.contains(t) && pages.length > 1 && !peeking() ? { x0: x, y0: y, t: performance.now(), x: x, v: 0, dx: 0, on: false, side: 0 } : null;
      return !!drag;
    }
    if (!drag) return false;
    const cur = ui.pageEls[pageIdx], W = slideWidth();
    if (kind === 'move') {
      const dx = x - drag.x0, dy = y - drag.y0, now = performance.now();
      if (!drag.on && Math.abs(dx) > 8 && Math.abs(dx) > Math.abs(dy)) {
        drag.on = true;
        for (const p of ui.pageEls) { for (const a of p.getAnimations()) a.cancel(); p.classList.remove('out'); p.style.transform = ''; }
      }
      if (!drag.on) return true;
      drag.v = (x - drag.x) / Math.max(1, now - drag.t); drag.x = x; drag.t = now;
      drag.dx = dx;
      // The page after (dragging left) or before (dragging right) waits just beside the current one.
      const side = dx < 0 ? 1 : -1;
      if (side !== drag.side) {
        if (drag.side) { const o = ui.pageEls[neighbour(drag.side)]; if (o !== cur) { o.classList.remove('out'); o.style.transform = ''; } }
        drag.side = side;
      }
      const nb = ui.pageEls[neighbour(side)];
      cur.style.transform = `translateX(${dx}px)`;
      if (nb !== cur) { nb.classList.add('out'); nb.style.transform = `translateX(${dx + side * W}px)`; }
      lastTouch = Date.now();
      return true;
    }
    if (kind === 'up') {
      const d = drag; drag = null;
      if (!d.on) return false; // a click: the click event does the rest
      const side = d.dx < 0 ? 1 : -1, nb = ui.pageEls[neighbour(side)];
      if (Math.abs(d.dx) > W / 4 || Math.abs(d.v) > .45) { goPage(pageIdx + side, d.dx); return true; }
      // Not far enough: both pages glide back.
      cur.style.transform = ''; nb.style.transform = '';
      cur.animate([{ transform: `translateX(${d.dx}px)` }, { transform: 'none' }], { duration: 260, easing: EASE });
      if (nb !== cur) {
        const back = nb.animate([{ transform: `translateX(${d.dx + side * W}px)` }, { transform: `translateX(${side * W}px)` }], { duration: 260, easing: EASE, fill: 'forwards' });
        back.onfinish = () => { nb.classList.remove('out'); back.cancel(); };
      }
      return true;
    }
    return false;
  }

  let reported = '';
  function reportRects() {
    if (!ui.stack) return;
    const boxes = [];
    const add = e => { const r = e.getBoundingClientRect(); if (r.width && r.height) boxes.push([Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)]); };
    if (!ui.stack.classList.contains('lw-hide')) {
      if (peeking()) add(ui.peek);
      else { add(card); if (calOpen) add(ui.panel); }
    }
    const key = JSON.stringify(boxes);
    if (key === reported) return;
    reported = key;
    root.PondWidgets.onAction({ type: 'hit-rects', rects: boxes });
  }

  function closeCard() { cardOpen = false; calOpen = false; render(true); }

  function tick() {
    if (paused) return; // nothing runs while the wallpaper is paused
    if (display === 'peek' && cardOpen && Date.now() - lastTouch > AUTO_CLOSE_MS) { closeCard(); return; }
    render(false);
  }

  function animate(node, frames, ms) { try { if (node && node.animate) node.animate(frames, { duration: ms, easing: EASE }); } catch (e) { /* cosmetic */ } }

  // ---- Clicks -------------------------------------------------------------------------------------
  function act(target) {
    const a = target.getAttribute('data-act');
    let slide = null; // set for calendar changes: -1 / 1 slide direction, 0 cross-fade
    switch (a) {
      case 'open-card': cardOpen = true; pageIdx = 0; break; // always opens on the first page
      case 'toggle-cal':
        calOpen = !calOpen;
        if (calOpen) { view = new Date(); selected = null; }
        break;
      case 'prev': case 'next': {
        slide = a === 'prev' ? -1 : 1;
        if (calMode === 'month') view = new Date(view.getFullYear(), view.getMonth() + slide, 1);
        else view = new Date(view.getFullYear(), view.getMonth(), view.getDate() + 7 * slide);
        break;
      }
      case 'mode-month': if (calMode !== 'month') { resizeGlass(ui.panelBg, ui.panel, () => { calMode = 'month'; morphCalendar(0); }); } return;
      case 'mode-week': if (calMode !== 'week') { resizeGlass(ui.panelBg, ui.panel, () => { calMode = 'week'; if (selected) view = new Date(selected); morphCalendar(0); }); } return;
      case 'today': { const d = new Date(); slide = Math.sign((d.getFullYear() - view.getFullYear()) * 12 + d.getMonth() - view.getMonth()); view = d; selected = new Date(); break; }
      case 'day': { const [y, m, d] = target.getAttribute('data-date').split('-').map(Number); selected = new Date(y, m - 1, d); break; }
      case 'task': {
        const id = target.getAttribute('data-id'), t = tasks.find(x => x.id === id);
        if (t) { t.done = !t.done; if (t.done) justDone.add(id); root.PondWidgets.onAction({ type: 'task-toggle', id }); }
        break;
      }
      case 'shortcut':
        root.PondWidgets.onAction({ type: 'open-shortcut', id: target.getAttribute('data-id') });
        animate(target, [{ transform: 'scale(.92)', backgroundColor: 'rgba(255,255,255,.14)' }, { transform: 'none', backgroundColor: 'rgba(255,255,255,0)' }], 420);
        return;
      case 'page': goPage(Number(target.getAttribute('data-page')) || 0); return;
      case 'page-prev': goPage(pageIdx - 1); return;
      case 'page-next': goPage(pageIdx + 1); return;
      case 'open-boc': root.PondWidgets.onAction({ type: 'open-url', url: ratesSource }); break;
    }
    if (slide !== null && calOpen) morphCalendar(slide);
    else render(true);
  }

  // Re-renders the calendar with a transition: the outgoing grid stays on top as a fading "ghost",
  // the new grid slides in from the side it came from (dir -1 / 1) or fades in (dir 0), and the
  // section's height eases from the old size to the new one.
  function morphCalendar(dir) {
    for (const g of ui.cal.body.querySelectorAll('.lw-ghost')) g.remove();
    const old = ui.cal.body.querySelector('.lw-grid'), ghost = old && old.cloneNode(true);
    if (old) for (const a of old.getAnimations()) a.cancel();
    const oldTitle = ui.cal.body.querySelector('.lw-cal-bar b');
    const titleText = oldTitle && oldTitle.textContent;
    render(true);
    const grid = ui.cal.body.querySelector('.lw-grid'), wrap = ui.cal.body.querySelector('.lw-grid-wrap');
    if (ghost && wrap) {
      ghost.classList.add('lw-ghost');
      wrap.appendChild(ghost);
      const out = ghost.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: dir ? `translateX(${-dir * 28}px)` : 'scale(.98)' }], { duration: 260, easing: 'ease-in', fill: 'forwards' });
      out.onfinish = () => ghost.remove();
    }
    if (grid) animate(grid, [{ opacity: 0, transform: dir ? `translateX(${dir * 28}px)` : 'scale(.98)' }, { opacity: 1, transform: 'none' }], 380);
    const title = ui.cal.body.querySelector('.lw-cal-bar b');
    if (title && title.textContent !== titleText) animate(title, [{ opacity: 0, transform: `translateY(${dir ? 0 : 4}px) translateX(${dir * 10}px)` }, { opacity: 1, transform: 'none' }], 320);
  }

  // ---- Public API ---------------------------------------------------------------------------------
  root.PondWidgets = {
    /** Set by the bridge: sends a message to the host. */
    onAction: () => {},
    start() { build(); },
    apply(p) {
      if (!p) return;
      if (typeof p.clock === 'string') opts.clock = p.clock;
      if (typeof p.clockLanguage === 'string') opts.lang = p.clockLanguage;
      if (typeof p.clock24 === 'boolean') opts.h24 = p.clock24;
      if (typeof p.worldClock === 'string') opts.clocks = p.worldClock.split(/[,，、;；]+/).map(x => x.trim()).filter(Boolean).slice(0, 4);
      if (typeof p.bocLink === 'boolean') opts.bocLink = p.bocLink;
      render(true);
    },
    /** The app's language, "zh" or "en" (init "language" and the host's "language" message). */
    setLanguage(lang) {
      if (lang !== 'zh' && lang !== 'en') return;
      if (lang === appLang) return;
      appLang = lang;
      render(true);
    },
    /** True if the card is in Chinese right now. */
    isChinese() { return zh(); },
    /** Which widgets to show, in order, and how (host message "widgets"). */
    setWidgets(m) {
      if (!m) return;
      if (Array.isArray(m.items)) order = m.items.filter((id, i, a) => MODULES.includes(id) && a.indexOf(id) === i);
      if (m.layout === 'tabs' || m.layout === 'stack') layout = m.layout;
      if (m.display === 'peek' || m.display === 'open') display = m.display;
      if (typeof m.perPage === 'number') perPage = Math.max(0, Math.min(8, Math.round(m.perPage)));
      if (Array.isArray(m.shortcuts)) shortcuts = m.shortcuts.map(s => ({ id: String(s.id), name: String(s.name || ''), kind: String(s.kind || 'app') }));
      render(true);
    },
    setSystem(m) { system = m || null; if (!paused && order.includes('system') && rootEl) renderSystem(); },
    setTasks(items) { tasks = Array.isArray(items) ? items.map(t => ({ id: String(t.id), text: String(t.text || ''), done: !!t.done })) : []; render(true); },
    setRates(m) { rates = Array.isArray(m.rates) ? m.rates : []; ratesAt = m.fetchedAt || ''; if (m.source) ratesSource = m.source; render(true); },
    setHolidays(days) { holidays = new Map((days || []).map(d => [d.date, { name: d.name, off: !!d.off }])); render(true); },
    setWeather(m) { weather = m || null; render(true); },
    /** Press / move / release from the host (CSS px); true if the card used it. */
    pointer(kind, x, y) { return !paused && rootEl ? pointer(kind, x, y) : false; },
    setPaused(value) { paused = !!value; if (!paused) render(true); },
    setNight(night) { document.body.classList.toggle('lw-night', !!night); },
    // A click from the host (CSS px). Returns true if a widget used it.
    tap(x, y) {
      if (!card) return false;
      const target = document.elementFromPoint(x, y);
      if (!target || !ui.stack.contains(target)) {
        // A click elsewhere first folds the month, then (in peek mode) closes the card.
        if (calOpen) { calOpen = false; render(true); return true; }
        if (display === 'peek' && cardOpen) { closeCard(); return true; }
        return false;
      }
      lastTouch = Date.now();
      const actionable = target.closest('[data-act]');
      if (actionable && ui.stack.contains(actionable)) act(actionable);
      return true;
    },
    // Exposed for tests.
    _calendar: { sunLongitude, termOn, nextTerm, lunar, festival, festivals },
  };
})(window);
