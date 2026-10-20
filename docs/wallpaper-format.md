# Wallpaper Package Format

A Lotus Pond wallpaper is a folder (or a `.zip` of that folder) containing a static web page plus a
manifest. The host serves the folder at `https://wallpaper.lotus/` inside WebView2 and loads
`https://wallpaper.lotus/<entry>`.

```
my-wallpaper/
├── wallpaper.json    # manifest (required)
├── index.html        # entry page (required; name set by "entry")
├── preview.png       # library thumbnail (optional; generated automatically if missing)
└── assets/...        # anything else the page loads, referenced with relative URLs
```

**Rules**

- **No network access.** Every request to a host other than `wallpaper.lotus` is blocked (HTTP 403),
  and navigation away from `wallpaper.lotus` is cancelled. Bundle all scripts, fonts, images and audio.
- Use **relative URLs** (Vite: `base: "./"`) so the package works from any folder.
- A `.zip` may contain the files at its root or inside a single top-level folder.

## `wallpaper.json`

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | string | yes | Unique id: letters, digits, `.`, `_`, `-` (max 64). Also the install folder name. |
| `name` | string | yes | Shown in the library. |
| `author` | string | no | Shown in the library. |
| `version` | string | no | Free-form, e.g. `0.1.0`. |
| `entry` | string | no | Entry page, default `index.html`. Must exist in the package. |
| `preview` | string | no | Thumbnail image, default `preview.png`. |
| `background` | string | no | `#rrggbb` shown before the page paints (avoids a white flash). Defaults to the first `color` property's default, else black. |
| `properties` | object | no | User-editable properties (see below). |
| `scene` | object | no | Wallpaper-specific scene description (see below). The host ignores it. |

### Properties

Each key of `properties` is a property id; the value describes its control in the settings window.

| `type` | Extra fields | Value type |
|---|---|---|
| `slider` | `min`, `max` (numbers, `min < max`), `step` (optional, > 0) | number, clamped to `[min, max]` and snapped to `step` |
| `bool` | — | boolean |
| `color` | — | `#rrggbb` string |
| `combo` | `options` (non-empty string array) | one of `options` |
| `text` | `maxLength` (optional) | string |

Every property has `label` (string, shown in the UI) and `default` (value of the matching type).
Invalid values — in the manifest's defaults or saved by the user — fall back to the default; they never crash
the host or the page.

```json
"properties": {
  "weather":   { "type": "combo",  "label": "Weather",        "options": ["Sunny", "Cloudy", "Rain", "Snow"], "default": "Sunny" },
  "speed":     { "type": "slider", "label": "Swimming speed", "min": 0.3, "max": 2, "step": 0.1, "default": 1 },
  "turtles":   { "type": "bool",   "label": "Turtles",        "default": true }
}
```

## Host ↔ page protocol

The page talks to the host through WebView2's `window.chrome.webview` (messages below). A complete,
small reference implementation is `wallpapers/lotus-pond/lotus-bridge.js`, which adapts an existing
web wallpaper to this protocol without changing it. When `window.chrome.webview` is missing, the page
was opened in a normal browser and should behave as a regular web page.

### Before the page loads

The host injects `window.__LOTUS_INIT__` before any page script runs:

```json
{
  "paused": false,
  "language": "zh",
  "properties": { "fishCount": 14, "waterColor": "#1c3d4a" },
  "settings": { "fpsLimit": 30, "renderScale": 1, "quality": "auto" },
  "monitor": { "name": "\\\\.\\DISPLAY1", "scale": 1.25, "primary": true }
}
```

### Host → page (`chrome.webview` message events)

| Message | Meaning |
|---|---|
| `{ "type": "pointer", "event": "move\|down\|up\|click", "x": 812.5, "y": 344, "button": "left\|right\|middle\|none" }` | Mouse over the empty desktop, in this page's CSS pixels. `move` ≤ 60/s and only while running. `click` = down/up within 5 px and 300 ms (drag-selecting icons never clicks). Clicks on desktop icons are not forwarded. |
| `{ "type": "pause" }` / `{ "type": "resume" }` | Stop / restart rendering. When paused, stop the render loop completely. |
| `{ "type": "properties", "values": { ... } }` | Full set of current property values (live edits from the settings window). |
| `{ "type": "settings", "fpsLimit": 30, "renderScale": 1, "quality": "auto" }` | Host render settings changed. |
| `{ "type": "language", "language": "en\|zh" }` | The app's interface language changed (the init field `language` has the starting one). Text on the page should follow it. |

### Page → host (`chrome.webview.postMessage`)

| Message | Meaning |
|---|---|
| `{ "type": "ready" }` | Page initialised. The host re-sends the pause state after this. |
| `{ "type": "log", "level": "info\|warn\|error", "message": "..." }` | Written to the host log. |
| `{ "type": "stats", "fps": 30, "frameMs": 2.1, "quality": "high" }` | Diagnostics (sent every ~10 s). |

## Performance expectations

Wallpapers run all day. Honour `pause` by fully stopping the render loop, cap the frame rate at
`fpsLimit`, use `renderScale`, and avoid per-frame allocations.
