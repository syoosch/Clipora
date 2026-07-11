# Clipora

**English** | [简体中文](README.zh-CN.md)

> A local clipboard-history tool for Windows — automatically records what you copy so you can find and reuse it anytime. **Runs fully offline; your data never leaves your device.**

Current version: **v0.4.2** (`0.x` is a pre-1.0 public preview; features may change based on feedback) · [Changelog](CHANGELOG.md)

Clipora automatically captures and classifies the text, images, files, links, code, colors, and rich text you copy. Browse history in reverse-chronological order, grouped into collapsible 8-hour time segments. Search, tag, pin, and recover from a recycle bin. Click any card to copy it and paste it straight back into your previous window. The UI uses the Windows 11 Mica material and follows your system light/dark theme.

---

## ✨ Features

- **Automatic capture & classification**: text / rich text / link / code / color / image / file, auto-detected by type; you can also drag files, text, or images straight into the panel.
- **Time-segment grouping**: reverse-chronological, full load (virtualized scrolling); each day is split into Early / Day / Evening collapsible segments with only the latest expanded by default; pinned items stay fixed at the top. Cards show the source app.
- **Search & filter**: keyword search, filter by Type or Tag.
- **Tag management**: tag cards with existing tags, quick-create new ones; rename / delete / recolor / reorder tags in Settings.
- **Per-item actions**: pin, delete (to a recoverable recycle bin), tag, click-to-reuse (copy and auto-paste back into the previous window), drag out to other apps.
- **Window behavior**: always-on-top toggle, hidden from the taskbar, freely resizable; minimizes to an edge-docked strip (summoned on hover); closing hides it to the tray.
- **Global hotkeys**: `Alt+V` opens the panel by default; additional rebindable hotkeys for "paste as plain text", "sequential paste", and more, with conflict detection.
- **Image OCR**: uses Windows' built-in OCR (offline) so text inside images becomes searchable.
- **Auto-cleanup**: by retention period (1 / 3 / 7 / 30 days / forever; default 3); pinned items are never cleaned up.
- **Appearance**: System / Light / Dark color modes, rounded Mica surfaces, hover-lift and fade-in animations.
- **Privacy & data**: entirely local, zero network requests; configurable app-exclusion list; backup export / import (`.clpbak`).

> Note: Liquid Glass / Sticky Note skins, accent-color customization, and English/Chinese language switching are planned for later releases and are not enabled in this version.

---

## 💻 Requirements

- Windows 10 (version 2004 / build 19041 or later) or Windows 11
- x64
- **No separate .NET install needed** — the installer is a self-contained single file with the runtime bundled

---

## 📦 Installation

1. Download `Clipora-0.1.1-setup.exe`.
2. Run it. The installer is **per-user (no admin rights required)** and installs to your user directory by default.
3. This build is **not code-signed**, so Windows SmartScreen may show "Windows protected your PC": click **"More info" → "Run anyway"** (this is normal for unsigned apps; the installer makes no network connections and uploads nothing).

**Uninstall**: via the Start Menu "Uninstall Clipora" or Windows "Settings → Apps". If Clipora is still running (including minimized to the tray), you'll be prompted and it will be fully closed after you confirm. **Uninstalling keeps your clipboard data by default** so a reinstall can pick up where you left off; data is removed only if you opt in to "also delete all data" during uninstall.

---

## 🚀 Getting started

1. Copy anything (text, a screenshot, files…) — Clipora records it as a card automatically.
2. Press `Alt+V` to open the panel and browse your history.
3. **Click a card** = copy that content and auto-paste it back into the window you were just in.
4. Hover a card to reveal pin / delete / tag buttons; the leftmost title-bar button expands search and filters.
5. Settings let you adjust retention days, data location, the per-item size cap, appearance, hotkeys, the privacy exclusion list, backups, and more.

---

## 🔒 Data & privacy

- **Fully local**: clipboard data is stored only on your PC; the app never connects to the network or uploads anything.
- **Data location**: defaults to `%LOCALAPPDATA%\Clipora`; the installed version lets you customize and safely migrate it (copy → verify → atomic switch, falling back to the old directory on failure, never auto-merging or deleting).
- **Per-item size cap**: 25 MB by default; larger items are flagged and not stored.
- Respects the system "exclude from clipboard history" marker.

---

## 🛠 Tech stack

.NET 10 · WPF · [WPF-UI](https://github.com/lepoco/wpfui) · SQLite

## 🧑‍💻 Building from source

```powershell
# Run a dev build (isolated data directory, won't touch real data)
powershell -ExecutionPolicy Bypass -File scripts/start-dev.ps1 -Build

# Compile
dotnet build src/Clipora

# Publish + build the installer (requires Inno Setup 6)
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

> Dev builds must be launched via `scripts/start-dev.ps1` (it sets `CLIPORA_DATA_DIR` to the in-repo `.dev-data` isolated directory). Never run the Debug exe directly, to avoid touching real data.

---

## 📌 Versioning

Uses Semantic Versioning (`MAJOR.MINOR.PATCH`). `0.x` is a pre-1.0 public preview; the first feature-complete stable release will be `1.0.0`. The version is shown at the bottom of the Settings page.
