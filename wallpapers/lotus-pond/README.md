# Lotus Pond — wallpaper

Chao Desk's own pond engine; every file here is ours.

- `pond.js`: the engine (WebGL2). The pond photo is the bed, and the engine adds:
  - a ripple simulation that refracts the photo;
  - moving caustics, glints and cloud shadows;
  - night (with a moon reflection and fireflies), rain and snow;
  - koi that wander, startle and come for food;
  - floating lily pads, petals or autumn leaves.

  The picture's own pads and leaves are found by colour into a mask, so koi swim under them. Koi wander inside
  `SCENES.*.swim`, a polygon of the open water. They swim with a head-led body, beat and glide, and reaction
  delays.
- `pond-art.js`: maple and ginkgo leaves and small round sprites.
- `assets/`: AI-generated pictures made for this project.
  - `celadon.jpg`, `wisteria.jpg`, `summer.jpg` and `autumn.jpg`: pond beds (1672×941) with an open middle;
    each has its swim polygon and mask rule in `SCENES`.
  - `pad.png`, `lotus.png` and `petal.png`: cut from one transparent sheet.
  - Fish sheets `<name>-body.png`, `<name>-fins.png` and `fish-<name>.js`:
    - `koi`: 8 koi (kohaku, sanke, showa, tancho, yamabuki, platinum, orange ogon, asagi), 512 × 256 cells.
    - `fancy`: 4 butterfly koi plus a comet, shubunkin, ryukin and oranda, 512 × 384 cells.
    - Bodies bend along the spine; the cut-out fins move on their own. Each sheet has a script tag in index.html.

  If you replace a photo, update its `swim` polygon and its mask.
- `lotus-bridge.js`: the host protocol.
  - Pause holds every requestAnimationFrame.
  - Clicks feed the koi, with a small budget, or nudge a floating leaf.
  - Properties and frame rate go to `Pond.set`.
  - Day and night follow the clock; the weather follows the real weather.
- `lotus-widgets.js`: the widget card (calendar, clock, rates, weather, world clock, tasks, system, shortcuts).
  `widgets.html` + `widgets-bridge.js` show the card without the pond. The card is in Chinese or English: by
  default it follows the app's language (`language` in the init data and the host's `language` message); the
  "Card language" property can force 中文 or English.

Try it in a browser: serve this folder and open `index.html`. Clicks feed the koi, and the query string sets
options: `?scene=autumn&night=1&weather=rain&koi=16`, `?lang=en` for the card in English, or `?debug=mask` to
see the surface mask.
Tests: `node --test tests/*.test.cjs`.
