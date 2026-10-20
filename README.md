# Chao Desk

A personal desktop toolkit for Windows that keeps growing with the tools I use every day. It replaces
uTools and Everything without their ads and bundles:

- **Launcher** (Alt+Space) — one search box for apps (pinyin and initials work: `wx` finds 微信),
  every file on your NTFS drives, Chao Desk commands, quick sums and web addresses.
- **File search** — Everything-style: reads the drives' file tables, finds any of millions of files as
  you type, and stays current by itself.
- **Clipboard history** (Shift+`, the ~ key) — text, images and copied files; search, pin, and paste
  back into the window you came from.
- **Screenshots** (Shift+F2) — Snipaste-style: snap to windows, mark up, copy, save, pin to the screen
  (Shift+F3), quick whole-screen capture (Shift+F1) and scrolling long screenshots (Shift+F4).
- **Text recognition** (Ctrl+`) — pick an area and get its text, offline (PP-OCRv5), with an optional
  translation under it.
- **Quick Look** (Space in Explorer) — preview pictures, PDF, video, music, text and code, Office documents
  and folders without opening them.
- **Translation** — translate recognised text and clipboard entries (Chinese ↔ English, or into another
  language), with free online services and no account.
- **Interactive wallpaper** (optional) — a painted lotus pond behind your desktop icons, with koi that
  chase food when you click the water, a desktop card with calendar, tasks, weather and more.

The whole app is in English or Simplified Chinese (界面支持简体中文), following Windows unless you choose.

The wallpaper is one module among the tools and can be turned off.

## Install

1. Download `ChaoDeskSetup-<version>.exe` from the
   [latest release](https://github.com/TidalJ/chao-desk/releases/latest) (or build it with
   `./build.ps1 -Package`, see below) and run it. No admin rights are needed; it installs to
   `%LOCALAPPDATA%\Programs\ChaoDesk`.
2. **SmartScreen:** the installer is not code-signed, so Windows may show "Windows protected your
   PC". Click **More info → Run anyway**.
3. Chao Desk needs the **.NET 8 Desktop Runtime**; the wallpaper also needs the **Microsoft Edge
   WebView2 Runtime** (ships with Windows 11). The installer checks for both and links to the downloads.

**Updates:** *Settings → General → Updates* checks GitHub for a newer release (also once a day on its
own, which can be turned off). **Download and install** fetches the installer, checks its SHA-256 and
installs it silently; Chao Desk restarts by itself.

It lives in the notification area: right-click the lotus icon for the launcher, clipboard history,
**Settings** and **Exit**; double-click it to open Settings. Uninstall via *Settings → Apps →
Installed apps → Chao Desk*.

## Using it

### Launcher (Alt+Space)

- Type part of an app's name, its pinyin or initials (`weixin`, `wx`, `微x`), or English words of an
  executable (`notepad` finds 记事本). Apps you open often, and what you typed to open them, rise to
  the top.
- Files: all words must be in the name; `ext:mp4;mkv` or `*.pdf` filter by type, `folder:` / `file:` by
  kind, and a word with a backslash (`work\report`) matches the whole path.
- Also: sums like `12*(3+4)` (Enter copies the result), web addresses, and commands (Chao Desk settings,
  clipboard history, pause wallpaper, lock computer, exit).
- Keys: **Tab** switches All / Apps / Files · **Enter** opens · **Shift+Enter** previews a file ·
  **Ctrl+Enter** shows it in its folder · **Ctrl+Shift+Enter** runs as administrator · **Ctrl+P** pins
  (pinned items lead the empty box) · **Ctrl+S** sorts files by relevance, name, date or size ·
  **Ctrl+Shift+C** copies the path · **Alt+1…9** opens a result. File results show size and date.

### File search

The first time, the launcher offers **Set up file search**: Windows asks once for administrator
permission to create a scheduled task. From then on Chao Desk starts a small helper through that task
(no prompts) which reads the NTFS file tables and follows the change journal, so new, renamed and
deleted files show up immediately. The index is saved to disk, so after a restart search works within a
second or two and only the changes since are read. FAT/exFAT drives (USB sticks) are scanned and watched
too, and drives plugged in later are picked up. About 50 bytes of memory per file; Settings → Launcher shows
the status, excluded folders and whether hidden/system files are included.

### Clipboard history (Shift+`)

Everything you copy — text, images, files — up to 100–2000 entries (pinned entries are always kept).
**Enter** pastes into the window you came from, **Shift+Enter** pastes plain text, **Ctrl+C** only
copies, **Ctrl+P** pins, **Del** removes, **Tab** filters by kind. **Ctrl+T** on a text entry shows its
translation (Enter then pastes the translation); on a picture it recognises the text. Pressing Shift+` again while the
history is open closes it and types the `~` the hotkey took. Password managers that mark their copies
private are never recorded; more apps can be excluded in Settings → Clipboard.

Note: Windows does not let a normal app paste into windows running as administrator.

### Screenshots

The default hotkeys (all can be changed in Settings → Screenshot):

- **Shift+F2** — hover to pick a window or panel (click takes it) or drag an area; drag the handles to adjust.
  Mark it up (rectangle, ellipse, arrow, pen, highlighter, text, mosaic, numbered markers; Ctrl+Z undo),
  then **Enter**/double-click copies, **Ctrl+S** saves, **Ctrl+T**/**Shift+F3** pins it, **T** recognises
  its text, **C** copies the colour under the mouse.
- **Shift+F1** — the whole screen, straight to the clipboard (and the screenshot folder).
- **Shift+F4** (or the toolbar's scroll button) — pick a scrolling area; Chao Desk scrolls and stitches
  one long image. Esc stops.
- **Shift+F3** — choose an area and pin it on top of everything; wheel zooms, Ctrl+wheel fades,
  double-click closes. **Ctrl+F3** hides every pin and brings them back.
- **Ctrl+`** — choose an area and read its text (the window can translate it too).
- Copies are always full quality (and appear in the clipboard history); files are PNG or JPEG at the
  quality you choose (Settings → Screenshot).

### Quick Look

Select a file in Explorer or on the desktop and press **Space**. Space or Esc closes, Enter opens, the arrow
keys go through the folder. Office documents use the preview handlers their apps install.

### Appearance

Settings → General → Appearance: light, dark, or the same as Windows. Settings → General → Language:
English, 简体中文, or the same as Windows. The settings window opens on a Home page that shows every tool,
its hotkeys and status.

### Translation

The text-recognition window has a **Translate** button (Ctrl+T), and Ctrl+T translates a text entry in
clipboard history. Settings → General → Translation picks the target (automatic: Chinese into English,
anything else into Chinese) and the service: Google, then Microsoft Bing, which also works in mainland
China. Both are free and need no account; text is sent only when you ask for a translation.

### Wallpaper

- **Click empty desktop space** (not an icon) to drop fish food; the koi race for it.
- **Settings → Wallpaper:** weather, day and night, swimming speed, rain and snow, silver carp,
  turtles, crabs, butterflies, koi names.
- **Desktop card:** calendar with lunar dates, festivals and the official 休/班 schedule, AUD exchange
  rate, weather, world clock, tasks, computer status, shortcuts (Settings → Widgets).
- **Settings → General:** turn the wallpaper off, frame rate, quality, pause rules, mouse interaction,
  start with Windows, online info.

The wallpaper pauses on its own when a full-screen app or a maximised window covers a display, on
battery (configurable), with battery saver on, when the PC is locked, the display is off, or it
sleeps. While paused it uses essentially no CPU or GPU.

## Building from source

Requirements: Windows 10/11 (native, **not WSL**), .NET SDK 8+, and
[Inno Setup 6](https://jrsoftware.org/isdl.php) for the installer.

```powershell
./build.ps1                     # Debug build + tests; wallpapers copied next to the exe
./build.ps1 -Configuration Release
./build.ps1 -SkipTests
./build.ps1 -Package            # app in dist/app (~4 MB) + dist/ChaoDeskSetup-<version>.exe
./build.ps1 -Deploy             # update the installed app in place
```

The Debug app is at `host/src/ChaoDesk.Host/bin/Debug/net8.0-windows/ChaoDesk.exe`.
`ChaoDesk.exe --exit` asks a running instance to quit cleanly. To try the pond in a browser, serve
`wallpapers/lotus-pond/` with any static web server and open `index.html`.

### Releasing

Bump `<Version>` in `host/src/ChaoDesk.Host/ChaoDesk.Host.csproj`, commit, then push a matching tag:

```powershell
git tag v1.1.0
git push origin main v1.1.0
```

GitHub Actions (`.github/workflows/release.yml`) builds, tests and packages on Windows, then publishes a
release with `ChaoDeskSetup-<version>.exe`, its `.sha256` and the commits since the previous tag as
notes. Installed apps see it on their next update check. Every push to `main` is built and tested by
`ci.yml`. Releases come with new features, not with every commit.

## Repository layout

| Path | Contents |
|---|---|
| `host/` | C# / WPF app (launcher, clipboard, file index, wallpaper host) and xUnit tests |
| `wallpapers/lotus-pond/` | The pond: engine files, `lotus-bridge.js` (host adapter), `wallpaper.json` |
| `installer/` | Inno Setup script |
| `docs/` | The wallpaper package format |

## Credits

The code, the pond engine and its pictures are original to this project (the photos and fish sheets
were generated with AI tools for it). Third-party libraries, the OCR models and the Unihan pinyin
data are listed with their licences in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Copyright © 2026 Charlie Jiang. All rights reserved: the source is public to read, but no licence
is granted to reuse it.
