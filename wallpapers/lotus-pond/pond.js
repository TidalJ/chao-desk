// Chao Desk pond — our own engine. A painted pond photo (assets/summer.jpg or autumn.jpg) is the pond bed;
// WebGL2 adds a surface that is never quite still (drifting breeze waves, sun glitter), ripple rings that refract
// the bed, moving caustics, cloud shadows, and day/night.
// Fish swim beneath the surface: AI-made sheets (koi, butterfly koi and goldfish) split into bodies
// (assets/<sheet>-body.png, bent along a spine) and fins (<sheet>-fins.png, described in fish-<sheet>.js) that move
// on their own. Lily pads, lotus, petals or
// leaves can float on the surface.
//
// Passes per frame: ripple simulation steps → "under" framebuffer (pond bed, shadows, koi) → water composite to the
// screen (refraction, light, surface mask) → floating things and food on top. The ripples are a height field stepped
// 90 times a second: waves interfere, reflect, are held still under pads and leaves, and fade at the pond's edge.
// It is kept below the grid's stability limit and given a little viscosity, so grid-scale noise dies out.
// Things painted on the pond's surface (its lily pads, lotus, floating leaves) are found by colour into a mask:
// there the composite shows the picture unrefracted and above the koi, so koi swim under them.
//
// Public API: window.Pond — start(), set(options), pause(bool), feed(x, y), ripple(x, y, strength),
// poke(x, y) (true if it hit a floating thing), foodCount(). Coordinates are CSS px.
(function (root) {
  'use strict';
  const A = root.PondArt;
  const TAU = Math.PI * 2;
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const lerp = (a, b, t) => a + (b - a) * t;
  const rand = (a, b) => a + Math.random() * (b - a);
  const angleTo = (from, to) => { let d = (to - from) % TAU; if (d > Math.PI) d -= TAU; if (d < -Math.PI) d += TAU; return d; };

  // ---- Scenes ------------------------------------------------------------------------------------
  // swim: where the koi wander, in image coordinates (0..1) — the open water of each photo.
  const SCENES = {
    celadon: {
      // A pond like a giant celadon basin: crackle-glazed bottom, lotus top-left, plum branch top-right, stones and pads lower right.
      image: 'assets/celadon.jpg', water: [0.45, 0.62, 0.54],
      swim: [[0.30, 0.08], [0.62, 0.05], [0.73, 0.11], [0.79, 0.30], [0.83, 0.42], [0.79, 0.61], [0.70, 0.70], [0.63, 0.86],
        [0.42, 0.90], [0.35, 0.80], [0.22, 0.63], [0.12, 0.52], [0.14, 0.42], [0.25, 0.40], [0.30, 0.25]],
      surface(r, g, b, h, s, v) {
        // Lotus leaves are much more saturated than the pale celadon water around them.
        if (h > 65 && h < 150 && s > 0.4 && v > 0.2) return smooth(0.4, 0.5, s);
        return 0;
      },
    },
    wisteria: {
      // A Suzhou garden pond: slate-slab bottom, wisteria top-left, pavilion top-right, rocks lower left, lilies right.
      image: 'assets/wisteria.jpg', water: [0.28, 0.52, 0.52],
      swim: [[0.44, 0.06], [0.71, 0.04], [0.76, 0.21], [0.91, 0.26], [0.93, 0.45], [0.86, 0.52], [0.84, 0.90], [0.62, 0.95],
        [0.33, 0.94], [0.23, 0.80], [0.17, 0.63], [0.15, 0.53], [0.22, 0.50], [0.26, 0.31], [0.40, 0.23]],
      surface(r, g, b, h, s, v) {
        if (h > 65 && h < 155 && s > 0.3 && v > 0.22) return smooth(0.3, 0.42, s) * smooth(0.22, 0.32, v); // lily pads, leaves
        if (h > 235 && h < 310 && s > 0.18 && v > 0.45) return smooth(0.18, 0.3, s);                      // wisteria
        if ((h > 300 || h < 15) && r > 170 && s > 0.12) return smooth(170, 200, r);                         // water lilies
        return 0;
      },
    },
    summer: {
      image: 'assets/summer.jpg', water: [0.34, 0.46, 0.32],
      swim: [[0.21, 0.07], [0.90, 0.05], [0.95, 0.13], [0.94, 0.42], [0.89, 0.52], [0.87, 0.74], [0.79, 0.80], [0.62, 0.79],
        [0.48, 0.77], [0.33, 0.73], [0.23, 0.69], [0.18, 0.52], [0.16, 0.30], [0.19, 0.16]],
      surface(r, g, b, h, s, v) {
        if (h > 78 && h < 150 && s > 0.38 && v > 0.36) return smooth(0.38, 0.5, s) * smooth(0.36, 0.46, v); // lily pads
        if ((h > 300 || h < 20) && r > 160 && s > 0.15) return smooth(160, 195, r);                         // lotus
        return 0;
      },
    },
    autumn: {
      image: 'assets/autumn.jpg', water: [0.26, 0.42, 0.38],
      swim: [[0.30, 0.07], [0.87, 0.05], [0.92, 0.16], [0.87, 0.40], [0.84, 0.62], [0.79, 0.76], [0.69, 0.85], [0.48, 0.87],
        [0.33, 0.79], [0.25, 0.62], [0.24, 0.36], [0.27, 0.19]],
      surface(r, g, b, h, s, v) {
        if ((h < 26 || h > 335) && s > 0.5 && v > 0.32) return smooth(0.5, 0.62, s); // maple
        if (h >= 26 && h < 58 && s > 0.55 && v > 0.5) return smooth(0.55, 0.68, s) * smooth(0.5, 0.62, v); // ginkgo
        return 0;
      },
    },
  };
  function smooth(a, b, v) { const t = clamp((v - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
  function hsv(r, g, b) {
    const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
    let h = 0;
    if (d > 0) h = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
    h *= 60; if (h < 0) h += 360;
    return [h, mx ? d / mx : 0, mx / 255];
  }

  // ---- Options -------------------------------------------------------------------------------------
  const opt = {
    scene: 'celadon', night: false, weather: 'sunny', rain: 0.5, snow: 0.5, speed: 1, koi: 10,
    koiSize: 1,
    floating: false, drift: false, fireflies: true, quality: 'high', fps: 30,
  };

  // ---- State ------------------------------------------------------------------------------------
  let canvas, gl, W = 1, H = 1, unit = 1, dpr = 1;
  let ready = false, paused = false, pending = false, lastFrame = 0, time = 0;
  let nightLevel = 0, cloudLevel = 0, rainLevel = 0;
  let cover = [0, 0, 1, 1];      // image uv = cover.xy + screen uv * cover.zw (v from the top)
  let swim = [];                 // the swim polygon in CSS px
  let swimBox = [0, 0, 1, 1];
  const koi = [], food = [], floaters = [], flakes = [], flies = [], drops = [];
  const MAX_DROPS = 24, dropData = new Float32Array(MAX_DROPS * 4);
  let lastDropAt = -1e9, simAcc = 0;
  let sceneImages = {}, sceneMasks = {};

  // ---- Persistence: the same koi every time ---------------------------------------------------------
  const STORE = 'chaodesk.pond.v2';
  function loadSeeds() {
    try { const s = JSON.parse(localStorage.getItem(STORE) || 'null'); if (s && Array.isArray(s.koi)) return s.koi; } catch (e) { /* none */ }
    return [];
  }
  function saveSeeds() {
    try { localStorage.setItem(STORE, JSON.stringify({ koi: koi.map(k => ({ seed: k.seed, size: +k.size.toFixed(3), girth: +k.girth.toFixed(3) })) })); } catch (e) { /* private mode */ }
  }

  // =================================================================================================
  // WebGL
  // =================================================================================================
  const QUAD_VS = `#version 300 es
in vec2 aPos; out vec2 vUv;
void main() { vUv = aPos * 0.5 + 0.5; gl_Position = vec4(aPos, 0.0, 1.0); }`;

  // One step of the ripples: height in r, vertical velocity in g. The wave term (1.8) stays below the grid's limit
  // (2.0, where a checkerboard mode never decays); a touch of viscosity smooths grid-scale noise; the pond's edge
  // absorbs; and the surface mask (pads, leaves, rocks) holds the water still, so ripples break and reflect there.
  const SIM_FS = `#version 300 es
precision highp float;
uniform sampler2D uSim, uMask; uniform vec2 uTexel; uniform vec4 uCover; uniform float uAspect;
uniform vec4 uDrops[${MAX_DROPS}]; uniform int uCount;
in vec2 vUv; out vec4 o;
void main() {
  vec4 s = texture(uSim, vUv);
  for (int i = 0; i < ${MAX_DROPS}; i++) {
    if (i >= uCount) break;
    vec4 d = uDrops[i];
    float r = length((vUv - d.xy) * vec2(uAspect, 1.0)) / d.z;
    if (r < 1.0) s.r += (0.5 + 0.5 * cos(r * 3.14159265)) * d.w;
  }
  float avg = 0.25 * (texture(uSim, vUv + vec2(uTexel.x, 0.0)).r + texture(uSim, vUv - vec2(uTexel.x, 0.0)).r +
                      texture(uSim, vUv + vec2(0.0, uTexel.y)).r + texture(uSim, vUv - vec2(0.0, uTexel.y)).r);
  s.g += (avg - s.r) * 1.8;
  vec2 e = min(vUv, 1.0 - vUv);
  float edge = smoothstep(0.0, 0.04, min(e.x * uAspect, e.y));
  s.g *= mix(0.9, 0.993, edge);
  s.r += s.g;
  s.r = mix(s.r, avg, 0.015) * mix(0.95, 0.9995, edge);
  float still = texture(uMask, uCover.xy + vec2(vUv.x, 1.0 - vUv.y) * uCover.zw).r;
  o = s * (1.0 - still);
}`;

  // Textured triangles (koi, sprites, shadows): premultiplied texture x colour, mixed toward a fog colour.
  const SPRITE_VS = `#version 300 es
uniform vec2 uView;
in vec2 aPos; in vec2 aUv; in vec4 aColor; in vec4 aFog; in float aBias;
out vec2 vUv; out vec4 vColor; out vec4 vFog; out float vBias;
void main() {
  vUv = aUv; vColor = aColor; vFog = aFog; vBias = aBias;
  gl_Position = vec4(aPos.x / uView.x * 2.0 - 1.0, 1.0 - aPos.y / uView.y * 2.0, 0.0, 1.0);
}`;
  const SPRITE_FS = `#version 300 es
precision mediump float;
uniform sampler2D uTex;
in vec2 vUv; in vec4 vColor; in vec4 vFog; in float vBias; out vec4 o;
void main() {
  vec4 t = texture(uTex, vUv, vBias) * vColor;
  t.rgb = mix(t.rgb, vFog.rgb * t.a, vFog.a);
  o = t;
}`;

  // The pond bed into the "under" framebuffer. The picture is upscaled to the screen, so it is sharpened (unsharp
  // mask over its own pixels) and given a touch more contrast and colour: clear water shows a crisp bottom.
  const BED_FS = `#version 300 es
precision mediump float;
uniform sampler2D uBed; uniform vec4 uCover; uniform vec2 uTexel;
in vec2 vUv; out vec4 o;
void main() {
  vec2 uv = uCover.xy + vec2(vUv.x, 1.0 - vUv.y) * uCover.zw;
  vec3 c = texture(uBed, uv).rgb;
  vec3 blur = (texture(uBed, uv + vec2(uTexel.x, 0.0)).rgb + texture(uBed, uv - vec2(uTexel.x, 0.0)).rgb +
               texture(uBed, uv + vec2(0.0, uTexel.y)).rgb + texture(uBed, uv - vec2(0.0, uTexel.y)).rgb) * 0.25;
  c += (c - blur) * 0.7;
  float l = dot(c, vec3(0.3, 0.59, 0.11));
  c = mix(vec3(l), c, 1.08);
  c = (c - 0.5) * 1.06 + 0.5;
  o = vec4(clamp(c, 0.0, 1.0), 1.0);
}`;

  // The water: refraction by the ripples, caustics, glints, cloud shadows, night; the surface mask on top.
  const WATER_FS = `#version 300 es
precision highp float;
uniform sampler2D uUnder, uBed, uMask;
uniform vec4 uCover; uniform float uAspect;
uniform sampler2D uSim; uniform vec2 uSimTexel;
uniform sampler2D uWaves; uniform float uBreeze;
uniform float uTime, uNight, uCloud, uSun, uRain;
uniform vec2 uMoon; uniform int uDebug;
in vec2 vUv; out vec4 o;
float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float noise(vec2 p) {
  vec2 i = floor(p), f = fract(p); vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { s += a * noise(p); p = p * 2.03 + 17.1; a *= 0.5; } return s; }
// Caustics: two drifting layers of domain-warped ridged noise; bright thin lines where both ridges peak.
float caustic(vec2 p, float t) {
  vec2 w = vec2(noise(p * 0.6 + vec2(t * 0.11, 0.0)), noise(p * 0.6 + vec2(5.2, -t * 0.09)));
  float a = 1.0 - abs(noise(p * 1.1 + w * 2.2 + t * 0.07) * 2.0 - 1.0);
  vec2 w2 = vec2(noise(p * 0.5 - vec2(t * 0.08, 3.1)), noise(p * 0.5 + vec2(1.7, t * 0.1)));
  float b = 1.0 - abs(noise(p * 1.6 + w2 * 2.6 - t * 0.05) * 2.0 - 1.0);
  return pow(a, 7.0) * 0.65 + pow(b, 9.0) * 0.55;
}
void main() {
  vec2 uv = vUv;
  vec2 bedUv = uCover.xy + vec2(uv.x, 1.0 - uv.y) * uCover.zw;
  float mask = texture(uMask, bedUv).r;

  // Ripples: the simulated surface's slope (g) and curvature (focus: light gathers under the crests).
  float h0 = texture(uSim, uv).r;
  float hl = texture(uSim, uv - vec2(uSimTexel.x, 0.0)).r, hr = texture(uSim, uv + vec2(uSimTexel.x, 0.0)).r;
  float hd = texture(uSim, uv - vec2(0.0, uSimTexel.y)).r, hu = texture(uSim, uv + vec2(0.0, uSimTexel.y)).r;
  vec2 g = vec2(hr - hl, hu - hd) * 4.0;
  float focus = -(hl + hr + hu + hd - 4.0 * h0) * 10.0;

  // The breeze: three drifting layers of a tileable slope map, at different scales and angles, so the surface is
  // never still and never visibly repeats.
  vec2 px = vec2(uv.x * uAspect, uv.y);
  mat2 r1 = mat2(0.8, -0.6, 0.6, 0.8), r2 = mat2(0.28, 0.96, -0.96, 0.28);
  vec2 w1 = texture(uWaves, r1 * px * 1.7 + vec2(uTime * 0.010, uTime * 0.006)).rg * 2.0 - 1.0;
  vec2 w2 = texture(uWaves, r2 * px * 2.9 + vec2(-uTime * 0.008, uTime * 0.011)).rg * 2.0 - 1.0;
  vec2 w3 = texture(uWaves, px * 5.6 + vec2(uTime * 0.017, -uTime * 0.013)).rg * 2.0 - 1.0;
  vec2 gb = (transpose(r1) * w1 * 0.55 + transpose(r2) * w2 * 0.4 + w3 * 0.22) * uBreeze;
  // Only tap ripples bend the view of the bed much; the breeze barely does, so the bottom stays crisp.
  vec2 off = (g * 0.011 + gb * 0.002) * (1.0 - mask);
  g += gb;
  vec3 col = texture(uUnder, uv + off).rgb;
  float lum = dot(col, vec3(0.3, 0.59, 0.11));

  // Light on the bed: moving caustics (less in the deep, dark water), focusing by the ripples.
  vec2 p = vec2(uv.x * uAspect, uv.y) * 5.0 + g * 0.08;
  float c = caustic(p, uTime);
  float light = uSun * (1.0 - uCloud * 0.7);
  col *= 1.0 + light * c * 0.32 * smoothstep(0.08, 0.45, lum) + (0.35 + light * 0.65) * clamp(focus * 0.3, -0.2, 0.4) * (1.0 - mask);

  // Glints where the ripples tilt the surface toward the sun; a faint sky sheen.
  vec3 n = normalize(vec3(-g * 0.9, 1.0));
  float spec = pow(max(dot(n, normalize(vec3(-0.35, 0.45, 1.0))), 0.0), 160.0);
  col += spec * vec3(1.0, 0.96, 0.88) * (0.8 * light + 0.15) * smoothstep(0.05, 0.4, length(g)) * (1.0 - mask);
  // Sun glitter: tiny fast facets catching the sun, gathered in slowly drifting patches.
  vec2 hf = texture(uWaves, px * 23.0 + vec2(uTime * 0.09, -uTime * 0.07)).rg * 2.0 - 1.0;
  vec2 hf2 = texture(uWaves, r2 * px * 37.0 + vec2(-uTime * 0.12, uTime * 0.05)).rg * 2.0 - 1.0;
  vec3 ng = normalize(vec3(-(g * 0.9 + (hf + hf2) * 0.22), 1.0));
  float patches = smoothstep(0.5, 0.8, noise(px * 3.0 + vec2(uTime * 0.04, -uTime * 0.03)));
  float glitter = pow(max(dot(ng, normalize(vec3(-0.35, 0.45, 1.0))), 0.0), 900.0);
  col += glitter * patches * light * vec3(1.0, 0.97, 0.9) * 1.6 * (1.0 - mask);
  col = mix(col, vec3(0.62, 0.72, 0.78), uRain * 0.08 * (1.0 - mask));

  // Things floating on the photo's surface: unrefracted, above the koi.
  vec3 bed = texture(uBed, bedUv).rgb;
  col = mix(col, bed, mask);

  // Cloud shadows drifting over everything.
  float cl = fbm(vec2(uv.x * uAspect, uv.y) * 1.3 + vec2(uTime * 0.010, uTime * 0.004));
  col *= 1.0 - smoothstep(0.42, 0.78, cl) * (0.06 + uCloud * 0.3) - uCloud * 0.12;

  // Night: deep blue, the moon reflected (and broken up by ripples) in the open water.
  vec3 nightCol = col * vec3(0.20, 0.27, 0.42);
  vec2 md = (uv + g * 0.02 - uMoon) * vec2(uAspect, 1.0);
  float moon = exp(-dot(md, md) * 900.0) * 0.9 + exp(-dot(md, md) * 40.0) * 0.12;
  nightCol += vec3(0.75, 0.82, 0.95) * moon * (1.0 - mask) * (1.0 - uCloud * 0.8);
  col = mix(col, nightCol, uNight);

  vec2 v = uv - 0.5;
  col *= 1.0 - dot(v, v) * 0.18;
  if (uDebug == 1) col = mix(col * 0.35, vec3(1.0, 0.2, 0.6), mask);
  o = vec4(col, 1.0);
}`;

  const prog = {}, tex = {};
  let quadVao, spriteVao, spriteBuf;
  let underFbo = null, underTex = null;
  let simTex = [], simFbo = [], simW = 0, simH = 0, simIndex = 0, floatTarget = true;

  function compile(vs, fs) {
    const make = (type, src) => {
      const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error('shader: ' + gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, make(gl.VERTEX_SHADER, vs)); gl.attachShader(p, make(gl.FRAGMENT_SHADER, fs));
    gl.bindAttribLocation(p, 0, 'aPos');
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error('link: ' + gl.getProgramInfoLog(p));
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) { const info = gl.getActiveUniform(p, i); const name = info.name.replace(/\[0\]$/, ''); u[name] = gl.getUniformLocation(p, info.name); }
    return { p, u };
  }

  function textureFrom(source, mip = true) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    if (mip) gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, mip ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return t;
  }

  function initGl() {
    gl = canvas.getContext('webgl2', { alpha: false, antialias: false, premultipliedAlpha: true, powerPreference: 'low-power', preserveDrawingBuffer: false });
    if (!gl) return false;
    floatTarget = !!gl.getExtension('EXT_color_buffer_float');
    prog.sim = compile(QUAD_VS, SIM_FS);
    prog.bed = compile(QUAD_VS, BED_FS);
    prog.water = compile(QUAD_VS, WATER_FS);
    prog.sprite = compile(SPRITE_VS, SPRITE_FS);
    for (const [i, name] of ['aPos', 'aUv', 'aColor', 'aFog', 'aBias'].entries()) gl.bindAttribLocation(prog.sprite.p, i, name);
    gl.linkProgram(prog.sprite.p); // re-link with the attribute locations fixed
    prog.sprite = { p: prog.sprite.p, u: { uView: gl.getUniformLocation(prog.sprite.p, 'uView'), uTex: gl.getUniformLocation(prog.sprite.p, 'uTex') } };

    quadVao = gl.createVertexArray(); gl.bindVertexArray(quadVao);
    const qb = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, qb);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

    spriteVao = gl.createVertexArray(); gl.bindVertexArray(spriteVao);
    spriteBuf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, spriteBuf);
    gl.bufferData(gl.ARRAY_BUFFER, batch.data.byteLength, gl.DYNAMIC_DRAW);
    const stride = FLOATS * 4;
    gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, stride, 0);
    gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 2, gl.FLOAT, false, stride, 8);
    gl.enableVertexAttribArray(2); gl.vertexAttribPointer(2, 4, gl.FLOAT, false, stride, 16);
    gl.enableVertexAttribArray(3); gl.vertexAttribPointer(3, 4, gl.FLOAT, false, stride, 32);
    gl.enableVertexAttribArray(4); gl.vertexAttribPointer(4, 1, gl.FLOAT, false, stride, 48);
    gl.bindVertexArray(null);

    canvas.addEventListener('webglcontextlost', (e) => { e.preventDefault(); ready = false; }, false);
    canvas.addEventListener('webglcontextrestored', () => { try { initGl(); buildTextures(); resize(); ready = true; schedule(); } catch (err) { console.error(err); } }, false);
    return true;
  }

  // ---- Sprite batch ---------------------------------------------------------------------------------
  const FLOATS = 13; // pos 2, uv 2, colour 4, fog 4, bias 1
  const batch = { data: new Float32Array(FLOATS * 6 * 4000), n: 0, tex: null };
  let fogNone = [0, 0, 0, 0];
  function flush() {
    if (!batch.n) return;
    gl.useProgram(prog.sprite.p);
    gl.uniform2f(prog.sprite.u.uView, W, H);
    gl.uniform1i(prog.sprite.u.uTex, 0);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, batch.tex);
    gl.bindVertexArray(spriteVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, spriteBuf);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, batch.data, 0, batch.n * FLOATS);
    gl.drawArrays(gl.TRIANGLES, 0, batch.n);
    batch.n = 0;
  }
  function vert(x, y, u, v, c, f, bias) {
    const d = batch.data, i = batch.n++ * FLOATS;
    d[i] = x; d[i + 1] = y; d[i + 2] = u; d[i + 3] = v;
    d[i + 4] = c[0]; d[i + 5] = c[1]; d[i + 6] = c[2]; d[i + 7] = c[3];
    d[i + 8] = f[0]; d[i + 9] = f[1]; d[i + 10] = f[2]; d[i + 11] = f[3]; d[i + 12] = bias;
  }
  function use(t) { if (batch.tex !== t || batch.n > batch.data.length / FLOATS - 600) { flush(); batch.tex = t; } }
  /** A rotated textured quad centred at (x, y); uv rect [u0, v0, u1, v1]; colour premultiplied. */
  function quad(t, x, y, w, h, angle, uv, c, f = fogNone, bias = 0) {
    use(t);
    const cs = Math.cos(angle), sn = Math.sin(angle), hw = w / 2, hh = h / 2;
    const px = (dx, dy) => x + dx * cs - dy * sn, py = (dx, dy) => y + dx * sn + dy * cs;
    const x0 = px(-hw, -hh), y0 = py(-hw, -hh), x1 = px(hw, -hh), y1 = py(hw, -hh);
    const x2 = px(-hw, hh), y2 = py(-hw, hh), x3 = px(hw, hh), y3 = py(hw, hh);
    vert(x0, y0, uv[0], uv[1], c, f, bias); vert(x1, y1, uv[2], uv[1], c, f, bias); vert(x2, y2, uv[0], uv[3], c, f, bias);
    vert(x2, y2, uv[0], uv[3], c, f, bias); vert(x1, y1, uv[2], uv[1], c, f, bias); vert(x3, y3, uv[2], uv[3], c, f, bias);
  }
  const FULL = [0, 0, 1, 1];

  // ---- Textures ---------------------------------------------------------------------------------------
  // Fish sheets (window.FISH_SHEETS): 4 columns of cellW x cellH cells, one fish per cell facing +x, with the fish's
  // extent (x0..x1) and fins per cell. Each gets a body and a fin texture.
  const SHEETS = root.FISH_SHEETS || [];
  const KIND_WEIGHT = { koi: 1, fancy: 0.5 }; // how often a fish comes from a sheet of each kind, per cell
  const img = {};
  function loadImage(src) {
    return new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = () => reject(new Error('could not load ' + src)); i.src = src; });
  }

  /** The surface mask of a scene: things floating on the photo, found by colour, softened. */
  function buildMask(name) {
    const sc = SCENES[name], im = sceneImages[name];
    const w = Math.round(im.naturalWidth / 3), h = Math.round(im.naturalHeight / 3);
    const [c, x] = A.canvas(w, h);
    x.drawImage(im, 0, 0, w, h);
    let m = new Float32Array(w * h);
    if (sc.shapes) {
      // Hand-placed circles: drawn soft-edged.
      x.clearRect(0, 0, w, h);
      x.fillStyle = '#fff';
      const k = w / im.naturalWidth;
      for (const [cx, cy, r] of sc.shapes) { x.beginPath(); x.arc(cx * k, cy * k, r * k * 0.97, 0, TAU); x.fill(); }
      const d = x.getImageData(0, 0, w, h).data;
      for (let i = 0; i < m.length; i++) m[i] = d[i * 4] / 255;
      m = filter(filter(m, w, h, 1, null), w, h, 1, null);
    } else {
      // By colour: drop specks (opening), fill holes such as veins and shaded parts (closing), soften the edge.
      const d = x.getImageData(0, 0, w, h).data;
      for (let i = 0, j = 0; i < m.length; i++, j += 4) { const [hh, s, v] = hsv(d[j], d[j + 1], d[j + 2]); m[i] = sc.surface(d[j], d[j + 1], d[j + 2], hh, s, v); }
      m = filter(filter(m, w, h, 1, Math.min), w, h, 1, Math.max);
      m = filter(filter(m, w, h, 2, Math.max), w, h, 2, Math.min);
      m = filter(filter(m, w, h, 1, null), w, h, 1, null);
    }
    const bytes = new Uint8Array(w * h);
    for (let i = 0; i < bytes.length; i++) bytes[i] = clamp(Math.round(smooth(0.25, 0.75, m[i]) * 255), 0, 255);
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, w, h, 0, gl.RED, gl.UNSIGNED_BYTE, bytes);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return t;
  }

  /** Separable square filter of radius r: op = Math.max / Math.min, or null for a box blur. */
  function filter(src, w, h, r, op) {
    const tmp = new Float32Array(src.length), out = new Float32Array(src.length);
    const run = (from, to, len, count, stride, step) => {
      for (let line = 0; line < count; line++) {
        const base = line * stride;
        for (let i = 0; i < len; i++) {
          let acc = op ? from[base + i * step] : 0;
          for (let d = -r; d <= r; d++) {
            const v = from[base + clamp(i + d, 0, len - 1) * step];
            acc = op ? op(acc, v) : acc + v;
          }
          to[base + i * step] = op ? acc : acc / (2 * r + 1);
        }
      }
    };
    run(src, tmp, w, h, w, 1);   // rows
    run(tmp, out, h, w, 1, w);   // columns
    return out;
  }

  /** Each koi's variety (a cell of the sheet) follows from its seed, so a koi keeps its looks. */
  function assignKoiCells() {
    // Every cell of every sheet, weighted by its sheet's kind; a fish's seed picks one, so it keeps its looks.
    const pool = [];
    SHEETS.forEach((sh, si) => sh.cells.forEach((c, ci) => pool.push({ si, ci, w: KIND_WEIGHT[sh.kind] || 1 })));
    const total = pool.reduce((a, p) => a + p.w, 0);
    if (!total) return;
    koi.forEach((k) => {
      let t = (((k.seed * 2654435761) >>> 0) / 4294967296) * total, pick = pool[0];
      for (const p of pool) { t -= p.w; if (t <= 0) { pick = p; break; } }
      const sh = SHEETS[pick.si], cell = sh.cells[pick.ci];
      k.sheet = pick.si; k.cell = pick.ci; k.kind = sh.kind;
      k.span = (cell.x1 - cell.x0) / sh.cellW;        // fish length / cell width
      k.front = (sh.cellW - cell.x1) / sh.cellW;      // gap before the nose, as a fraction of the cell width
      k.aspect = sh.cellH / sh.cellW;
      if (sh.kind === 'fancy' && !k.fancyTuned) { k.fancyTuned = true; k.cruise *= 0.6; k.wiggle *= 1.15; k.beatRate *= 0.9; k.agility *= 0.8; }
      const u0 = (pick.ci % sh.cols) / sh.cols, v0 = Math.floor(pick.ci / sh.cols) / sh.rows;
      k.uv = [u0, v0, u0 + 1 / sh.cols, v0 + 1 / sh.rows];
    });
  }

  /** A tileable map of surface slope (r, g = d/dx, d/dy, centred on 0.5) from ~28 integer-frequency waves. */
  function waveTexture(size = 128) {
    const waves = [];
    for (let i = 0; i < 28; i++) {
      let kx, ky; do { kx = Math.round(rand(-7, 7)); ky = Math.round(rand(-7, 7)); } while (!kx && !ky);
      waves.push([kx, ky, 1 / Math.pow(Math.hypot(kx, ky), 1.25), rand(0, TAU)]);
    }
    const gx = new Float32Array(size * size), gy = new Float32Array(size * size);
    let peak = 0;
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
      let dx = 0, dy = 0;
      for (const [kx, ky, amp, ph] of waves) { const c = Math.cos((kx * x + ky * y) / size * TAU + ph) * amp; dx += c * kx; dy += c * ky; }
      gx[y * size + x] = dx; gy[y * size + x] = dy; peak = Math.max(peak, Math.abs(dx), Math.abs(dy));
    }
    const bytes = new Uint8Array(size * size * 4);
    for (let i = 0; i < size * size; i++) {
      bytes[i * 4] = Math.round((gx[i] / peak * 0.5 + 0.5) * 255); bytes[i * 4 + 1] = Math.round((gy[i] / peak * 0.5 + 0.5) * 255); bytes[i * 4 + 3] = 255;
    }
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
    return t;
  }

  function buildTextures() {
    tex.waves = waveTexture();
    for (const name of Object.keys(sceneImages)) { tex['bed_' + name] = textureFrom(sceneImages[name]); tex['mask_' + name] = buildMask(name); }
    tex.pad = textureFrom(img.pad); tex.lotus = textureFrom(img.lotus); tex.petal = textureFrom(img.petal);
    tex.maple = [1, 2, 3].map(s => textureFrom(A.maple(s * 977)));
    tex.ginkgo = [1, 2].map(s => textureFrom(A.ginkgo(s * 613)));
    tex.glow = textureFrom(A.glow()); tex.pellet = textureFrom(A.pellet());
    for (const sh of SHEETS) { sh.bodyTex = textureFrom(sh.bodyImg); sh.finTex = textureFrom(sh.finImg); }
    assignKoiCells();
  }

  function makeTarget(w, h, internal, format, type, filter) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texImage2D(gl.TEXTURE_2D, 0, internal, w, h, 0, format, type, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const f = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, f);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
    const ok = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    return ok ? { t, f } : null;
  }

  // ---- Size ------------------------------------------------------------------------------------------
  function resize() {
    W = Math.max(1, window.innerWidth); H = Math.max(1, window.innerHeight);
    dpr = window.devicePixelRatio || 1;
    let scale = opt.quality === 'eco' ? 0.6 : 1;
    const maxPixels = 2560 * 1440;
    if (W * dpr * scale * H * dpr * scale > maxPixels) scale = Math.sqrt(maxPixels / (W * dpr * H * dpr));
    canvas.width = Math.round(W * dpr * scale); canvas.height = Math.round(H * dpr * scale);
    canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
    unit = Math.min(W, H) / 1000; // sizes are designed at a 1000 px short side
    if (!gl) return;

    if (underTex) { gl.deleteTexture(underTex); gl.deleteFramebuffer(underFbo); }
    const u = makeTarget(canvas.width, canvas.height, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, gl.LINEAR);
    underTex = u.t; underFbo = u.f;

    // The ripple grid: one cell per ~3 CSS px (4.5 in eco), at most 900 a side.
    for (const t of simTex) gl.deleteTexture(t);
    for (const fb of simFbo) gl.deleteFramebuffer(fb);
    simTex = []; simFbo = [];
    const cell = opt.quality === 'eco' ? 4.5 : 3;
    simW = Math.min(900, Math.ceil(W / cell)); simH = Math.min(900, Math.ceil(H / cell));
    if (floatTarget) {
      for (let i = 0; i < 2; i++) {
        const t = makeTarget(simW, simH, gl.RGBA16F, gl.RGBA, gl.HALF_FLOAT, gl.LINEAR);
        if (!t) { simTex = []; simFbo = []; break; }
        simTex.push(t.t); simFbo.push(t.f);
        gl.bindFramebuffer(gl.FRAMEBUFFER, t.f); gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
      }
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    layoutScene();
  }

  function layoutScene() {
    const im = sceneImages[opt.scene];
    if (!im) return;
    // Cover: the photo fills the screen, centred, cropped on the long side.
    const ia = im.naturalWidth / im.naturalHeight, sa = W / H;
    cover = sa > ia ? [0, (1 - ia / sa) / 2, 1, ia / sa] : [(1 - sa / ia) / 2, 0, sa / ia, 1];
    const toScreen = ([u, v]) => [(u - cover[0]) / cover[2] * W, (v - cover[1]) / cover[3] * H];
    swim = SCENES[opt.scene].swim.map(toScreen);
    swimBox = [Math.min(...swim.map(p => p[0])), Math.min(...swim.map(p => p[1])), Math.max(...swim.map(p => p[0])), Math.max(...swim.map(p => p[1]))];
    for (const k of koi) if (!inside(k.x, k.y)) { const p = randomInside(); k.x = p[0]; k.y = p[1]; }
    placeFloaters();
  }

  function inside(x, y) {
    let c = false;
    for (let i = 0, j = swim.length - 1; i < swim.length; j = i++) {
      const [xi, yi] = swim[i], [xj, yj] = swim[j];
      if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
    }
    return c;
  }
  function randomInside(margin = 0) {
    for (let i = 0; i < 60; i++) {
      const x = rand(swimBox[0], swimBox[2]), y = rand(swimBox[1], swimBox[3]);
      if (inside(x, y) && (!margin || (inside(x + margin, y) && inside(x - margin, y) && inside(x, y + margin) && inside(x, y - margin)))) return [x, y];
    }
    return [(swimBox[0] + swimBox[2]) / 2, (swimBox[1] + swimBox[3]) / 2];
  }

  // =================================================================================================
  // Koi
  // =================================================================================================
  // How a koi moves (all speeds in body lengths per second, so big and small koi look alike):
  //  * the head leads: the front third of the body is stiff, every point behind follows the path the point ahead
  //    of it took (with a limited bend per joint), so the body traces the curve the koi swam;
  //  * beat and glide: a few tail strokes to get up to speed, then a long coast with the tail almost still;
  //  * a turn slows it down (it cannot sprint sideways), a sharp one bends the whole body into a C;
  //  * it notices food late — after its own reaction time plus the time for the news to travel — and eases in.
  const SPINE = 16, RIGID = 5;
  const KOI_LENGTH = 95; // nose to tail tip, px at a 1000 px short side (about 100 px on a 1080p screen)

  // A pond holds koi of all ages: a few youngsters, mostly grown fish, the odd big old one; some slim, some stout.
  function koiBuild() {
    const r = Math.random();
    const size = r < 0.25 ? rand(0.5, 0.7) : r < 0.85 ? rand(0.8, 1.1) : rand(1.2, 1.45);
    // Young koi are slim; big old ones get stout.
    const girth = clamp(rand(0.84, 1.12) + (size - 0.9) * 0.25, 0.8, 1.3);
    return { size, girth };
  }

  function makeKoi(seed, size, girth) {
    const p = swim.length ? randomInside() : [W / 2, H / 2];
    const build = size ? { size, girth: girth || 1 } : koiBuild();
    // Temperament: small koi are quick and twitchy, big ones slow and steady; some like company, some keep apart.
    const youth = clamp((1.1 - build.size) / 0.6, 0, 1);
    return {
      seed, size: build.size, girth: build.girth,
      beatRate: rand(0.9, 1.1) * (0.9 + youth * 0.3), agility: rand(0.85, 1.15) * (0.8 + youth * 0.5),
      social: Math.random() < 0.6 ? rand(0.3, 1) : 0, wiggle: rand(0.9, 1.1), x: p[0], y: p[1], dir: rand(0, TAU), v: 0, turn: 0, thrust: 0, beating: false, strokeEnd: 0,
      amp: 0.2, phase: rand(0, TAU), depth: rand(0.3, 0.8), depthGoal: rand(0.3, 0.8), depthTimer: rand(5, 15),
      goal: null, goalTime: 0, rest: 0, flee: 0, fleeDir: 0, eatCooldown: 0,
      // Fancy fish get slower once their sheet is known (assignKoiCells).
      cruise: rand(0.28, 0.5) * (1 + youth * 0.35), react: rand(0.2, 1.1) * (1 - youth * 0.3), appetite: rand(0.55, 1),
      spine: null, spineSeg: 0, uv: FULL,
    };
  }

  function syncKoiCount() {
    const want = clamp(Math.round(opt.koi), 0, 24);
    if (koi.length === want) return;
    const seeds = loadSeeds();
    while (koi.length > want) koi.pop();
    while (koi.length < want) {
      const s = seeds[koi.length];
      koi.push(makeKoi(s ? s.seed : (Math.random() * 4294967296) >>> 0, s && s.size, s && s.girth));
    }
    assignKoiCells();
    saveSeeds();
  }

  const koiLength = (k) => KOI_LENGTH * unit * k.size * opt.koiSize; // nose to tail tip, CSS px

  /** A wander goal: somewhere in the open water, preferring ahead of the koi and not too close. */
  function pickGoal(k, L) {
    const span = Math.min(W, H);
    let best = null, score = -Infinity;
    for (let i = 0; i < 8; i++) {
      const p = randomInside(L * 0.6), dx = p[0] - k.x, dy = p[1] - k.y, d = Math.hypot(dx, dy);
      const sc = -Math.abs(angleTo(k.dir, Math.atan2(dy, dx))) * 1.2 + Math.min(d, span * 0.6) / span * 2 + Math.random() * 0.6;
      if (sc > score) { score = sc; best = p; }
    }
    k.goal = best; k.goalTime = rand(7, 19);
    k.rest = Math.random() < 0.18 ? rand(2, 6) : 0;
  }

  function updateKoi(k, dt) {
    const L = koiLength(k);
    // (k.x, k.y) is the nose; the mouth is right there.
    let wantDir = k.dir, want, gain = 1.8, maxTurn = 1.1;
    k.flee = Math.max(0, k.flee - dt);
    k.eatCooldown -= dt;

    let target = null;
    if (k.flee <= 0 && k.eatCooldown <= 0 && food.length) {
      let best = Infinity;
      const reach = Math.max(W, H) * k.appetite;
      for (const f of food) {
        if (f.eaten) continue;
        const d = Math.hypot(f.x - k.x, f.y - k.y);
        if (d < best && d < reach && f.age > k.react + d / (650 * unit)) { best = d; target = f; }
      }
    }

    k.target = target;
    if (k.flee > 0) {
      // Startled: a fast C-turn away, then a dash.
      wantDir = k.fleeDir; want = 2.6 * L; gain = 9; maxTurn = 7;
    } else if (target) {
      const dx = target.x - k.x, dy = target.y - k.y, d = Math.hypot(dx, dy);
      wantDir = Math.atan2(dy, dx);
      const err = Math.abs(angleTo(k.dir, wantDir));
      want = clamp(d / L * 1.3, 0.25, 2.2) * L * Math.max(0.12, Math.cos(Math.min(err, Math.PI / 2)) ** 2);
      gain = 3.2; maxTurn = 2.6;
      k.depthGoal = 0.04;
      if (d < L * 0.12) {
        target.eaten = true; k.eatCooldown = rand(0.3, 0.9);
        ripple(target.x, target.y, 0.3, 5); // the gulp
        k.v *= 0.5;
      }
    } else {
      k.goalTime -= dt;
      if (!k.goal || k.goalTime <= 0 || Math.hypot(k.goal[0] - k.x, k.goal[1] - k.y) < L * 0.8) pickGoal(k, L);
      wantDir = Math.atan2(k.goal[1] - k.y, k.goal[0] - k.x);
      const err = Math.abs(angleTo(k.dir, wantDir));
      if (k.rest > 0) { k.rest -= dt; want = 0.04 * L; }
      else want = k.cruise * L * Math.max(0.25, Math.cos(Math.min(err, Math.PI / 2)) ** 2);
      k.depthTimer -= dt;
      if (k.depthTimer <= 0) { k.depthGoal = rand(0.25, 0.9); k.depthTimer = rand(8, 20); }
    }

    // Keep apart from the others and inside the water: nudge the wanted heading. Social koi, while wandering, also
    // drift toward koi a few lengths away and line up with them (a loose shoal that forms and breaks up).
    let ax = 0, ay = 0, hx = 0, hy = 0, gx = 0, gy = 0, mates = 0;
    for (const o of koi) {
      if (o === k) continue;
      const dx = k.x - o.x, dy = k.y - o.y, d2 = dx * dx + dy * dy, r = L * 0.7;
      if (d2 < r * r && d2 > 1) { const d = Math.sqrt(d2), w = (r - d) / r; ax += dx / d * w; ay += dy / d * w; }
      else if (k.social && d2 < (L * 4) * (L * 4)) {
        const w = 0.6 + Math.min(1, o.size / k.size) * 0.4;
        hx += Math.cos(o.dir) * w; hy += Math.sin(o.dir) * w; gx -= dx * w; gy -= dy * w; mates += w;
      }
    }
    if (mates && !target && k.flee <= 0 && k.rest <= 0) {
      const hl = Math.hypot(hx, hy) || 1, gl = Math.hypot(gx, gy) || 1;
      ax += (hx / hl * 0.5 + gx / gl * 0.25) * k.social; ay += (hy / hl * 0.5 + gy / gl * 0.25) * k.social;
    }
    const lookX = k.x + Math.cos(k.dir) * L * 1.2, lookY = k.y + Math.sin(k.dir) * L * 1.2;
    if (!inside(lookX, lookY) || !inside(k.x, k.y)) {
      const cx = (swimBox[0] + swimBox[2]) / 2, cy = (swimBox[1] + swimBox[3]) / 2, d = Math.hypot(cx - k.x, cy - k.y) || 1;
      ax += (cx - k.x) / d * 2.5; ay += (cy - k.y) / d * 2.5;
      if (k.flee <= 0) { gain = Math.max(gain, 2.6); maxTurn = Math.max(maxTurn, 1.8); }
      if (k.rest > 0) k.rest = 0;
    }
    if (ax || ay) wantDir = Math.atan2(Math.sin(wantDir) + ay * 1.4, Math.cos(wantDir) + ax * 1.4);

    // Turning: an angular velocity that eases toward what the heading error asks for.
    const sp = clamp(opt.speed, 0.2, 3);
    const wantTurn = clamp(angleTo(k.dir, wantDir) * gain * k.agility, -maxTurn * k.agility, maxTurn * k.agility);
    k.turn += (wantTurn - k.turn) * Math.min(1, dt * 5);
    k.dir += k.turn * dt * sp;

    // Swimming in bouts: a few unhurried tail strokes, then a long glide with the tail barely swaying, and a few more
    // strokes once the koi has slowed. A bout is counted in whole strokes (tail-wave cycles), so it never stops
    // mid-stroke; more strokes when it needs to go fast. Thrust eases in and out over about half a second.
    if (!k.beating && k.v < want * 0.6) {
      k.beating = true;
      k.strokeEnd = k.phase + TAU * (2 + Math.floor(Math.random() * 3) + (want > 1.2 * L ? 3 : 0));
    }
    if (k.beating && k.phase >= k.strokeEnd) k.beating = false;
    k.thrust += ((k.beating ? 1 : 0) - k.thrust) * Math.min(1, dt * 2.2);
    if (k.beating) k.v += (want * 1.3 - k.v) * (1 - Math.exp(-dt * 1.8 * k.thrust));
    k.v *= Math.exp(-dt * (0.18 + Math.abs(k.turn) * 0.25)); // water drag (a long coast), more in a turn
    k.x += Math.cos(k.dir) * k.v * dt * sp; k.y += Math.sin(k.dir) * k.v * dt * sp;

    // Tail: about one stroke a second cruising, up to ~2.5 in a dash, a slow sway while gliding; a turn adds a little.
    const bl = k.v / L;
    const freq = k.beatRate * (0.35 + k.thrust * (0.75 + 0.7 * Math.min(2.5, bl))) + Math.abs(k.turn) * 0.2;
    k.phase += dt * sp * TAU * freq;
    if (k.phase > TAU * 1000) { k.phase -= TAU * 1000; k.strokeEnd -= TAU * 1000; }
    const ampGoal = 0.1 + 0.6 * k.thrust * Math.min(1, 0.55 + bl * 0.5) + Math.min(0.3, Math.abs(k.turn) * 0.2);
    k.amp += (ampGoal - k.amp) * Math.min(1, dt * 1.6);
    k.depth = lerp(k.depth, k.depthGoal, 1 - Math.exp(-dt * (target ? 1.5 : 0.25)));
    // At the surface: a fine wake from the nose as the koi moves, and now and then it kisses the surface.
    k.wakeT = (k.wakeT || 0) - dt;
    if (k.depth < 0.22 && bl > 0.05 && k.wakeT <= 0) { k.wakeT = 0.14; ripple(k.x, k.y, 0.04 * (1 - k.depth / 0.22) * Math.min(2, bl * 2), 3.2); }
    if (k.depth < 0.08 && !target && Math.random() < dt * 0.04) ripple(k.x, k.y, 0.35, 5);
    updateSpine(k, L);
  }

  /** The head-led chain: SPINE + 1 points from the front edge of the koi's cell back to its tail edge. */
  function updateSpine(k, L) {
    const cellLen = L / (k.span || 0.89), seg = cellLen / SPINE;
    const c = Math.cos(k.dir), s = Math.sin(k.dir);
    const fx = k.x + c * cellLen * (k.front || 0), fy = k.y + s * cellLen * (k.front || 0); // cell front edge
    let p = k.spine;
    if (!p || Math.abs(k.spineSeg - seg) > seg * 0.3 || Math.hypot(p[0] - fx, p[1] - fy) > seg * 3) {
      p = k.spine = new Float32Array((SPINE + 1) * 2);
      for (let i = 0; i <= SPINE; i++) { p[i * 2] = fx - c * seg * i; p[i * 2 + 1] = fy - s * seg * i; }
    }
    k.spineSeg = seg;
    for (let i = 0; i <= RIGID; i++) { p[i * 2] = fx - c * seg * i; p[i * 2 + 1] = fy - s * seg * i; }
    let prev = k.dir;
    for (let i = RIGID + 1; i <= SPINE; i++) {
      const px = p[(i - 1) * 2], py = p[(i - 1) * 2 + 1], lim = 0.1 + 0.16 * i / SPINE;
      const a = prev + clamp(angleTo(prev, Math.atan2(py - p[i * 2 + 1], px - p[i * 2])), -lim, lim);
      p[i * 2] = px - Math.cos(a) * seg; p[i * 2 + 1] = py - Math.sin(a) * seg; prev = a;
    }
  }

  // The drawn pose: the chain plus the travelling tail wave (stronger toward the tail), with normals.
  const pose = new Float32Array((SPINE + 1) * 4);
  function koiPose(k, L) {
    const p = k.spine, amp = k.amp * 0.07 * L * k.wiggle;
    for (let i = 0; i <= SPINE; i++) {
      const a = Math.max(0, i - 1), b = Math.min(SPINE, i + 1);
      let tx = p[a * 2] - p[b * 2], ty = p[a * 2 + 1] - p[b * 2 + 1]; const l = Math.hypot(tx, ty) || 1; tx /= l; ty /= l;
      const u = i / SPINE, env = 0.05 + 0.95 * Math.pow(Math.max(0, (u - 0.2) / 0.8), 1.5);
      const lat = amp * env * Math.sin(k.phase - u * 5.4);
      pose[i * 4] = p[i * 2] - ty * lat; pose[i * 4 + 1] = p[i * 2 + 1] + tx * lat;
    }
    for (let i = 0; i <= SPINE; i++) {
      const a = Math.max(0, i - 1), b = Math.min(SPINE, i + 1);
      const tx = pose[a * 4] - pose[b * 4], ty = pose[a * 4 + 1] - pose[b * 4 + 1], l = Math.hypot(tx, ty) || 1;
      pose[i * 4 + 2] = -ty / l; pose[i * 4 + 3] = tx / l;
    }
  }

  function drawKoi(k, shadow) {
    if (!k.spine) return;
    if (k.sheet === undefined) return;
    const L = koiLength(k), half = L / k.span * k.aspect / 2 * k.girth;
    koiPose(k, L);
    const [u0, v0, u1, v1] = k.uv;
    const depth = k.depth;
    let c, f, bias, ox = 0, oy = 0;
    if (shadow) {
      const off = (8 + (1 - depth) * 22) * unit;
      ox = off * 0.8; oy = off;
      c = [0, 0, 0, 0.3 * (1 - nightLevel * 0.7)]; f = fogNone; bias = 2.2 + (1 - depth) * 1.2;
    } else {
      const w = SCENES[opt.scene].water;
      c = [1, 1, 1, 1]; f = [w[0], w[1], w[2], 0.1 + depth * 0.28]; bias = depth * 0.5;
    }
    drawFins(k, L, half, c, f, bias, ox, oy);
    use(SHEETS[k.sheet].bodyTex);
    for (let i = 0; i < SPINE; i++) {
      const ua = u1 - (u1 - u0) * i / SPINE, ub = u1 - (u1 - u0) * (i + 1) / SPINE;
      const xa = pose[i * 4] + ox, ya = pose[i * 4 + 1] + oy, nxa = pose[i * 4 + 2] * half, nya = pose[i * 4 + 3] * half;
      const j = i + 1, xb = pose[j * 4] + ox, yb = pose[j * 4 + 1] + oy, nxb = pose[j * 4 + 2] * half, nyb = pose[j * 4 + 3] * half;
      vert(xa + nxa, ya + nya, ua, v1, c, f, bias); vert(xa - nxa, ya - nya, ua, v0, c, f, bias); vert(xb + nxb, yb + nyb, ub, v1, c, f, bias);
      vert(xb + nxb, yb + nyb, ub, v1, c, f, bias); vert(xa - nxa, ya - nya, ua, v0, c, f, bias); vert(xb - nxb, yb - nyb, ub, v0, c, f, bias);
    }
  }

  // Pectoral and pelvic fins, cut from the sheet (fish-<sheet>.js gives each fin's box and the pivot where it joins
  // the body, in cell px). Each is drawn at its place on the bent body and turned about its pivot: spread wide while the
  // koi glides or brakes, folded back at speed, the inner one flared in a turn, and sculling gently when it idles.
  const FIN_REST = 0.95; // the spread the fins have in the picture
  function drawFins(k, L, half, c, fog, bias, ox, oy) {
    const sh = SHEETS[k.sheet], meta = sh && sh.cells[k.cell];
    if (!meta || !meta.fins.length) return;
    use(sh.finTex);
    const CW = sh.cellW, CH = sh.cellH;
    const bl = k.v / L, scale = half / (CH / 2), finScale = scale / Math.sqrt(k.girth); // stout koi: wider body, fins a bit less
    const base = 1.05 - 0.6 * clamp(bl / 1.4, 0, 1);
    const cx = (k.cell % sh.cols) * CW, cy = Math.floor(k.cell / sh.cols) * CH, AW = sh.cols * CW, AH = sh.rows * CH;
    for (const fin of meta.fins) {
      const side = fin.side, [px, py] = fin.pivot;
      let spread = base + clamp(side * k.turn * 0.35, -0.3, 0.5);
      if (k.thrust < 0.3) spread += Math.sin(time * 4.2 + k.seed % 97 + side * 0.9 + (fin.front ? 0 : 1.7)) * 0.16;
      const rot = -side * (spread - FIN_REST) * (fin.front ? 1 : 0.55);
      // Where the pivot sits on the posed body: interpolate along the spine, then out along the normal.
      const t = clamp((1 - px / CW) * SPINE, 0, SPINE), i0 = Math.min(SPINE - 1, Math.floor(t)), a = t - i0;
      const X = lerp(pose[i0 * 4], pose[i0 * 4 + 4], a), Y = lerp(pose[i0 * 4 + 1], pose[i0 * 4 + 5], a);
      let nx = lerp(pose[i0 * 4 + 2], pose[i0 * 4 + 6], a), ny = lerp(pose[i0 * 4 + 3], pose[i0 * 4 + 7], a);
      const nl = Math.hypot(nx, ny) || 1; nx /= nl; ny /= nl;
      const tx = ny, ty = -nx; // forward
      const lat = (py - CH / 2) * scale; // the body strip is centred on the cell's middle row
      const pX = X + nx * lat + ox, pY = Y + ny * lat + oy;
      const cs = Math.cos(rot), sn = Math.sin(rot);
      const corner = (bx, by) => {
        const a1 = (bx - px) * finScale, b1 = (by - py) * finScale;
        const a2 = a1 * cs - b1 * sn, b2 = a1 * sn + b1 * cs;
        return [pX + tx * a2 + nx * b2, pY + ty * a2 + ny * b2, (cx + bx) / AW, (cy + by) / AH];
      };
      const [x0, y0, x1, y1] = fin.box;
      const q = [corner(x0, y0), corner(x1, y0), corner(x0, y1), corner(x1, y1)];
      for (const j of [0, 1, 2, 2, 1, 3]) vert(q[j][0], q[j][1], q[j][2], q[j][3], c, fog, bias);
    }
  }

  // =================================================================================================
  // Food, floating things, weather
  // =================================================================================================
  function feed(x, y) {
    ripple(x, y, 0.5, 7);
    const n = 7 + Math.floor(Math.random() * 3);
    for (let i = 0; i < n; i++) {
      const a = rand(0, TAU), r = rand(4, 26) * unit;
      food.push({ x: x + Math.cos(a) * r, y: y + Math.sin(a) * r, vx: Math.cos(a) * r * 0.8, vy: Math.sin(a) * r * 0.8, age: 0, life: 25, drift: rand(0, 6), eaten: false });
      ripple(x + Math.cos(a) * r, y + Math.sin(a) * r, 0.22, 3); // each pellet lands with its own little ring
    }
    // Koi close by startle: a quick dash away and a little deeper, before turning back for the food.
    for (const k of koi) {
      const d = Math.hypot(k.x - x, k.y - y), r = 150 * unit;
      if (d < r) {
        k.flee = 0.45 + (1 - d / r) * 0.6; k.fleeDir = Math.atan2(k.y - y, k.x - x) + rand(-0.45, 0.45);
        k.beating = true; k.strokeEnd = k.phase + TAU * 4; k.v = Math.max(k.v, koiLength(k) * 0.8); k.depthGoal = Math.min(0.95, k.depth + 0.35);
      }
    }
  }

  function updateFood(dt) {
    for (let i = food.length - 1; i >= 0; i--) {
      const f = food[i];
      // Pellets spread from the splash, then wander slowly on the breeze.
      f.age += dt; const k = Math.exp(-dt * 1.8); f.vx *= k; f.vy *= k;
      f.x += (f.vx + Math.sin(time * 0.35 + f.drift) * 1.2 * unit) * dt; f.y += (f.vy + Math.cos(time * 0.29 + f.drift * 1.3) * 1.2 * unit) * dt;
      if (f.eaten || f.age > f.life) food.splice(i, 1);
    }
  }

  function placeFloaters() {
    floaters.length = 0;
    if (!swim.length) return;
    const r = A.rng(opt.scene === 'summer' ? 7 : 11);
    if (opt.floating && opt.scene === 'summer') {
      for (let i = 0; i < 2; i++) {
        const size = (125 + r() * 35) * unit;
        let p = randomInsideSeeded(r, 110 * unit);
        for (let tries = 0; tries < 20 && floaters.some(o => Math.hypot(o.x - p[0], o.y - p[1]) < 300 * unit); tries++) p = randomInsideSeeded(r, 110 * unit);
        floaters.push({ kind: 'pad', x: p[0], y: p[1], size, angle: r() * TAU, spin: (r() - 0.5) * 0.02, vx: 0, vy: 0, bob: r() * TAU, lotus: i === 0 });
      }
    }
    if (opt.drift) {
      const n = opt.scene !== 'autumn' ? 4 : 8;
      for (let i = 0; i < n; i++) floaters.push(newDrifter(r, true));
    }
  }
  function randomInsideSeeded(r, margin) {
    for (let i = 0; i < 80; i++) {
      const x = swimBox[0] + r() * (swimBox[2] - swimBox[0]), y = swimBox[1] + r() * (swimBox[3] - swimBox[1]);
      if (inside(x, y) && inside(x + margin, y) && inside(x - margin, y) && inside(x, y + margin) && inside(x, y - margin)) return [x, y];
    }
    return [(swimBox[0] + swimBox[2]) / 2, (swimBox[1] + swimBox[3]) / 2];
  }
  // Petals (summer) or leaves (autumn) drifting slowly across with the breeze; they leave at an edge and a new one comes.
  function newDrifter(r = Math.random, anywhere = false) {
    const summer = opt.scene !== 'autumn'; // petals everywhere but the autumn pond
    const kind = summer ? 'petal' : (r() < 0.55 ? 'maple' : 'ginkgo');
    const x = anywhere ? r() * W : -60 * unit, y = r() * H;
    return {
      kind, variant: Math.floor(r() * (kind === 'maple' ? 3 : 2)), x, y, size: (summer ? 30 + r() * 14 : 40 + r() * 20) * unit,
      angle: r() * TAU, spin: (r() - 0.5) * 0.15, vx: (5 + r() * 6) * unit, vy: (r() - 0.5) * 3 * unit, bob: r() * TAU, drift: true,
    };
  }

  function updateFloaters(dt) {
    const breeze = opt.weather === 'rain' ? 1.6 : 1;
    for (let i = floaters.length - 1; i >= 0; i--) {
      const o = floaters[i];
      o.bob += dt; o.angle += o.spin * dt;
      if (o.drift) {
        o.x += (o.vx * breeze + Math.sin(o.bob * 0.3) * 2 * unit) * dt; o.y += (o.vy + Math.cos(o.bob * 0.23) * 2 * unit) * dt;
        o.spin *= Math.exp(-dt * 0.05);
        if (o.x > W + 80 * unit || o.y < -80 * unit || o.y > H + 80 * unit || o.x < -100 * unit) floaters[i] = newDrifter();
      } else {
        o.x += o.vx * dt; o.y += o.vy * dt; o.vx *= Math.exp(-dt * 0.6); o.vy *= Math.exp(-dt * 0.6); o.spin *= Math.exp(-dt * 0.3);
        // Pads stay in the open water.
        if (!inside(o.x, o.y)) { const cx = (swimBox[0] + swimBox[2]) / 2, cy = (swimBox[1] + swimBox[3]) / 2; o.vx += (cx - o.x) * 0.02 * dt; o.vy += (cy - o.y) * 0.02 * dt; }
      }
    }
  }

  /** A splash pushes floating things away (and sets them spinning a little). */
  function pushFloaters(x, y, strength) {
    for (const o of floaters) {
      const dx = o.x - x, dy = o.y - y, d = Math.hypot(dx, dy) || 1, reach = 160 * unit;
      if (d > reach) continue;
      const k = (1 - d / reach) * strength * (o.drift ? 40 : 18) * unit;
      o.vx += dx / d * k; o.vy += dy / d * k; o.spin += (Math.random() - 0.5) * strength * 0.6;
    }
  }

  function drawFloater(o, shadow) {
    const t = o.kind === 'maple' ? tex.maple[o.variant % 3] : o.kind === 'ginkgo' ? tex.ginkgo[o.variant % 2] : tex[o.kind];
    const im = o.kind === 'pad' ? img.pad : o.kind === 'petal' ? img.petal : null;
    const aspect = im ? im.naturalHeight / im.naturalWidth : 1;
    const bob = 1 + Math.sin(o.bob * 1.3) * 0.012;
    const dim = 1 - nightLevel * 0.62, night = [dim * 0.9, dim * 0.95, dim * 1.05];
    if (shadow) {
      const off = 14 * unit;
      quad(t, o.x + off * 0.8, o.y + off, o.size * bob, o.size * aspect * bob, o.angle, FULL, [0, 0, 0, 0.28 * (1 - nightLevel * 0.7)], fogNone, 2.5);
      return;
    }
    quad(t, o.x, o.y, o.size * bob, o.size * aspect * bob, o.angle, FULL, [night[0], night[1], night[2], 1]);
    if (o.lotus) {
      const s = o.size * 0.52, la = img.lotus.naturalHeight / img.lotus.naturalWidth;
      const lx = o.x + Math.cos(o.angle + 0.6) * o.size * 0.08, ly = o.y + Math.sin(o.angle + 0.6) * o.size * 0.08;
      quad(tex.glow, lx + 6 * unit, ly + 8 * unit, s * 1.1, s * 1.1, 0, FULL, [0, 0, 0, 0.25]);
      quad(tex.lotus, lx, ly, s, s * la, o.angle * 0.3, FULL, [night[0], night[1], night[2], 1]);
    }
  }

  function updateWeather(dt) {
    // Rain: drops all over the pond; snow: flakes falling toward the water and melting into it.
    rainLevel = lerp(rainLevel, opt.weather === 'rain' ? opt.rain : 0, 1 - Math.exp(-dt * 0.5));
    if (rainLevel > 0.02) {
      let n = rainLevel * 14 * dt * (W * H) / (1600 * 900);
      while (n > 0) { if (Math.random() < n) ripple(Math.random() * W, Math.random() * H, rand(0.2, 0.5), rand(3, 6)); n -= 1; }
    }
    const wantFlakes = opt.weather === 'snow' ? Math.round(opt.snow * 160) : 0;
    while (flakes.length < wantFlakes) flakes.push(newFlake(true));
    for (let i = flakes.length - 1; i >= 0; i--) {
      const f = flakes[i];
      if (f.landed) { f.landed += dt; if (f.landed > 3) { if (flakes.length > wantFlakes) flakes.splice(i, 1); else flakes[i] = newFlake(false); } continue; }
      f.z -= f.fall * dt; f.x += (f.vx + Math.sin(f.phase + time) * 6) * unit * dt; f.y += f.vy * unit * dt;
      if (f.z <= 0) { f.landed = 0.001; if (Math.random() < 0.5) ripple(f.x, f.y, 0.06, 3); }
    }
    const wantFlies = opt.fireflies && nightLevel > 0.5 && opt.weather !== 'rain' && opt.weather !== 'snow' ? (opt.scene !== 'autumn' ? 14 : 6) : 0;
    while (flies.length < wantFlies) flies.push({ x: rand(0, W), y: rand(0, H), a: rand(0, TAU), phase: rand(0, TAU), life: 0 });
    if (flies.length > wantFlies) flies.length = wantFlies;
    for (const f of flies) {
      f.a += rand(-1, 1) * dt * 1.5; f.phase += dt; f.life += dt;
      f.x += Math.cos(f.a) * 18 * unit * dt; f.y += Math.sin(f.a) * 18 * unit * dt;
      if (f.x < 0 || f.x > W) f.a = Math.PI - f.a; if (f.y < 0 || f.y > H) f.a = -f.a;
    }
  }
  function newFlake(anywhere) {
    return { x: rand(0, W), y: rand(0, H), z: anywhere ? rand(0, 1) : 1, fall: rand(0.12, 0.25), vx: rand(-4, 4), vy: rand(2, 8), phase: rand(0, TAU), size: rand(3, 6), landed: 0 };
  }

  // =================================================================================================
  // Ripples
  // =================================================================================================
  /** A drop on the water: radius in CSS px at unit 1 (at least 2.6 grid cells), strength ~0.04 (a wake) to 0.9 (a splash). */
  function ripple(x, y, strength = 0.5, radius = 6) {
    if (drops.length >= MAX_DROPS) drops.shift();
    const r = Math.max(radius * unit, 2.6 * H / Math.max(1, simH));
    drops.push([x / W, 1 - y / H, r / H, strength]);
    lastDropAt = time;
  }

  /** Fixed-rate steps (90 a second, at most 4 a frame); none once the water has been still for 12 s. */
  function stepSim(dt) {
    if (!simTex.length) { drops.length = 0; return; }
    if (time - lastDropAt > 12 && !drops.length) { simAcc = 0; return; }
    simAcc = Math.min(simAcc + dt * 90, 4);
    const steps = Math.floor(simAcc);
    if (!steps) return;
    simAcc -= steps;
    const p = prog.sim;
    gl.useProgram(p.p);
    gl.viewport(0, 0, simW, simH);
    gl.disable(gl.BLEND);
    gl.bindVertexArray(quadVao);
    gl.uniform2f(p.u.uTexel, 1 / simW, 1 / simH);
    gl.uniform1f(p.u.uAspect, W / H);
    gl.uniform4fv(p.u.uCover, cover);
    gl.uniform1i(p.u.uSim, 0); gl.uniform1i(p.u.uMask, 1);
    gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, tex['mask_' + opt.scene]);
    for (let i = 0; i < steps; i++) {
      const n = i === 0 ? drops.length : 0;
      if (n) { dropData.fill(0); drops.forEach((d, j) => dropData.set(d, j * 4)); gl.uniform4fv(p.u.uDrops, dropData); }
      gl.uniform1i(p.u.uCount, n);
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, simTex[simIndex]);
      gl.bindFramebuffer(gl.FRAMEBUFFER, simFbo[1 - simIndex]);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
      simIndex = 1 - simIndex;
    }
    drops.length = 0;
  }

  // =================================================================================================
  // Frame
  // =================================================================================================
  function update(dt) {
    time += dt;
    nightLevel = lerp(nightLevel, opt.night ? 1 : 0, 1 - Math.exp(-dt * 0.4));
    const wantCloud = opt.weather === 'cloudy' ? 0.75 : opt.weather === 'rain' ? 1 : opt.weather === 'snow' ? 0.8 : 0;
    cloudLevel = lerp(cloudLevel, wantCloud, 1 - Math.exp(-dt * 0.3));
    const steps = Math.max(1, Math.ceil(dt / 0.034));
    for (let i = 0; i < steps; i++) for (const k of koi) updateKoi(k, dt / steps);
    updateFood(dt);
    updateFloaters(dt);
    updateWeather(dt);
  }

  function render(dt) {
    stepSim(dt);

    // Under the surface.
    gl.bindFramebuffer(gl.FRAMEBUFFER, underFbo);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.disable(gl.BLEND);
    gl.useProgram(prog.bed.p);
    gl.bindVertexArray(quadVao);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, tex['bed_' + opt.scene]);
    gl.uniform1i(prog.bed.u.uBed, 0);
    gl.uniform4fv(prog.bed.u.uCover, cover);
    const bedImg = sceneImages[opt.scene];
    gl.uniform2f(prog.bed.u.uTexel, 1 / bedImg.naturalWidth, 1 / bedImg.naturalHeight);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
    gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    const order = koi.slice().sort((a, b) => b.depth - a.depth);
    for (const k of order) drawKoi(k, true);
    for (const k of order) drawKoi(k, false);
    for (const o of floaters) drawFloater(o, true);
    for (const f of food) { const a = clamp(Math.min(f.age * 4, (f.life - f.age) / 2), 0, 1); quad(tex.glow, f.x + 5 * unit, f.y + 6 * unit, 9 * unit, 9 * unit, 0, FULL, [0, 0, 0, 0.3 * a]); }
    flush();

    // The water.
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.disable(gl.BLEND);
    const w = prog.water;
    gl.useProgram(w.p);
    gl.bindVertexArray(quadVao);
    const bind = (unitIndex, t, name) => { gl.activeTexture(gl.TEXTURE0 + unitIndex); gl.bindTexture(gl.TEXTURE_2D, t); gl.uniform1i(w.u[name], unitIndex); };
    bind(0, underTex, 'uUnder');
    bind(1, tex['bed_' + opt.scene], 'uBed');
    bind(2, tex['mask_' + opt.scene], 'uMask');
    bind(3, tex.waves, 'uWaves');
    gl.uniform1f(w.u.uBreeze, (0.16 + cloudLevel * 0.06 + rainLevel * 0.12) * (1 - nightLevel * 0.3));
    bind(4, simTex.length ? simTex[simIndex] : underTex, 'uSim');
    gl.uniform2f(w.u.uSimTexel, 1 / Math.max(1, simW), 1 / Math.max(1, simH));
    gl.uniform4fv(w.u.uCover, cover);
    gl.uniform1f(w.u.uAspect, W / H);
    gl.uniform1f(w.u.uTime, time);
    gl.uniform1f(w.u.uNight, nightLevel);
    gl.uniform1f(w.u.uCloud, cloudLevel);
    gl.uniform1f(w.u.uSun, 1);
    gl.uniform1f(w.u.uRain, rainLevel);
    gl.uniform2f(w.u.uMoon, 0.74, 0.78);
    gl.uniform1i(w.u.uDebug, opt.debug === 'mask' ? 1 : 0);
    gl.drawArrays(gl.TRIANGLES, 0, 6);

    // On the surface.
    gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    for (const o of floaters) drawFloater(o, false);
    const dim = 1 - nightLevel * 0.6;
    for (const f of food) {
      const a = clamp(Math.min(f.age * 4, (f.life - f.age) / 2), 0, 1);
      quad(tex.pellet, f.x, f.y, 7 * unit, 7 * unit, 0, FULL, [dim * a, dim * a, dim * a, a]);
    }
    for (const f of flakes) {
      const a = f.landed ? clamp(1 - f.landed / 3, 0, 1) * 0.7 : 0.85;
      const s = f.size * unit * (f.landed ? 1 : 1 + f.z * 1.6);
      quad(tex.glow, f.x, f.y, s, s, 0, FULL, [a, a, a, a]);
    }
    flush();
    if (flies.length) {
      gl.blendFunc(gl.ONE, gl.ONE);
      for (const f of flies) {
        const p = Math.max(0, Math.sin(f.phase * 1.7)) * clamp(f.life / 2, 0, 1) * nightLevel;
        quad(tex.glow, f.x, f.y, 22 * unit, 22 * unit, 0, FULL, [0.75 * p, 0.95 * p, 0.35 * p, 0]);
        quad(tex.glow, f.x, f.y, 5 * unit, 5 * unit, 0, FULL, [p, p, 0.7 * p, 0]);
      }
      flush();
    }
  }

  // ---- Loop: paced to opt.fps with a timer between frames, so a low frame rate means few wake-ups ----------
  function schedule() {
    if (pending || paused || !ready) return;
    pending = true;
    const gap = 1000 / Math.max(1, opt.fps), wait = Math.max(0, gap - (performance.now() - lastFrame) - 3);
    if (wait > 1) setTimeout(() => requestAnimationFrame(frame), wait); else requestAnimationFrame(frame);
  }
  function frame(now) {
    pending = false;
    if (paused || !ready) return;
    const dt = Math.min(0.1, Math.max(0, (now - (lastFrame || now)) / 1000));
    lastFrame = now;
    update(dt);
    render(dt);
    schedule();
  }

  // =================================================================================================
  // Public API
  // =================================================================================================
  async function start() {
    canvas = document.getElementById('pond');
    if (!canvas || canvas.dataset.started) return;
    canvas.dataset.started = '1';
    const names = Object.keys(SCENES);
    try {
      const imgs = await Promise.all([...names.map(n => loadImage(SCENES[n].image)), loadImage('assets/pad.png'), loadImage('assets/lotus.png'), loadImage('assets/petal.png'),
        ...SHEETS.flatMap(sh => [loadImage(`assets/${sh.name}-body.png`), loadImage(`assets/${sh.name}-fins.png`)])]);
      names.forEach((n, i) => { sceneImages[n] = imgs[i]; });
      [img.pad, img.lotus, img.petal] = imgs.slice(names.length, names.length + 3);
      SHEETS.forEach((sh, i) => { sh.bodyImg = imgs[names.length + 3 + i * 2]; sh.finImg = imgs[names.length + 4 + i * 2]; });
    } catch (e) { console.error(e); return; }
    if (!initGl()) { document.body.classList.add('no-webgl'); console.warn('[pond] WebGL2 unavailable: showing the still picture'); return; }
    syncKoiCount();
    buildTextures();
    resize();
    window.addEventListener('resize', () => { resize(); });
    nightLevel = opt.night ? 1 : 0;
    cloudLevel = opt.weather === 'cloudy' ? 0.75 : opt.weather === 'rain' ? 1 : opt.weather === 'snow' ? 0.8 : 0;
    ready = true;
    document.body.classList.add('pond-ready');
    lastFrame = performance.now();
    schedule();
    setInterval(() => { if (!paused) console.info(`[pond] ${koi.length} koi · ${opt.scene} · ${opt.weather}${opt.night ? ' · night' : ''} · ${canvas.width}x${canvas.height} · ${opt.fps} fps max`); }, 10 * 60_000);
  }

  root.Pond = {
    start,
    set(o) {
      if (!o) return;
      const before = { ...opt };
      Object.assign(opt, Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined && v !== null)));
      if (!SCENES[opt.scene]) opt.scene = 'celadon';
      if (!ready) return;
      if (opt.quality !== before.quality) resize();
      if (opt.scene !== before.scene) { layoutScene(); food.length = 0; }
      else if (opt.floating !== before.floating || opt.drift !== before.drift) placeFloaters();
      if (opt.koi !== before.koi) syncKoiCount();
      if (opt.fps !== before.fps) schedule();
    },
    pause(value) {
      paused = !!value;
      if (!paused) { lastFrame = performance.now(); schedule(); }
    },
    feed(x, y) { if (ready) { feed(x, y); pushFloaters(x, y, 1); } },
    ripple(x, y, strength = 0.35) { if (ready) { ripple(x, y, strength * 1.4, 7); pushFloaters(x, y, strength); } },
    /** A click on a floating pad or leaf: it bobs and drifts off instead of food dropping. */
    poke(x, y) {
      if (!ready) return false;
      for (const o of floaters) {
        if (Math.hypot(o.x - x, o.y - y) < o.size * 0.42) {
          const a = Math.atan2(o.y - y, o.x - x);
          o.vx += Math.cos(a) * (o.drift ? 30 : 14) * unit; o.vy += Math.sin(a) * (o.drift ? 30 : 14) * unit; o.spin += rand(-0.4, 0.4);
          ripple(x, y, 0.3, 5);
          return true;
        }
      }
      return false;
    },
    foodCount() { return food.length; },
    /** For measuring motion in a browser: each koi's swimming state (read-only copies). */
    debugKoi() { return koi.map(k => ({ x: k.x, y: k.y, v: k.v / koiLength(k), thrust: k.thrust, beating: k.beating, phase: k.phase, amp: k.amp, turn: k.turn, kind: k.kind, flee: k.flee, target: !!k.target })); },
    get options() { return { ...opt }; },
  };
  document.addEventListener('DOMContentLoaded', start);
})(window);
