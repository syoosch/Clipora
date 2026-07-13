<p align="center">
  <img src="src/Clipora/Assets/clipora-icon.png" width="96" alt="Clipora icon">
</p>

# Clipora

[![Latest release](https://img.shields.io/github/v/release/syoosch/Clipora?display_name=tag)](https://github.com/syoosch/Clipora/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/syoosch/Clipora/total)](https://github.com/syoosch/Clipora/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)](#requirements)

**English** | [简体中文](README.zh-CN.md)

Clipora is a local clipboard-history tool for Windows. It records the content you copy, keeps it organized, and lets you find and reuse it without sending clipboard data off your device.

Current version: **v0.4.3** · [Download the latest installer](https://github.com/syoosch/Clipora/releases/latest) · [Read the user manual](manual/README.md)

## Why Clipora

- **Find copied content quickly**: search your history, filter by content type or tag, and keep important items pinned.
- **Keep more than text**: work with rich text, links, code, colors, images, files, and content dragged into the panel.
- **Reuse content with fewer steps**: click a card to copy and paste it back, drag items into other apps, or use configurable global hotkeys.
- **Keep control of your data**: Clipora works offline, supports pause and app exclusions, and stores its database and payloads locally.

## Core capabilities

### Capture and organize

Clipora automatically classifies copied text, rich text, URLs, code, colors, images, and files. Cards show their source app and are grouped into collapsible time periods. You can also drag files, text, images, and rich text into the panel, add tags, pin important items, and recover deleted items from the recycle bin.

### Find and reuse

Search across clipboard history, filter by type or tag, and use offline Windows OCR to make text inside images searchable. Click a card to copy it and optionally paste it back into the previous window, or drag supported content into another app. HTTP/HTTPS links and ordinary files can be opened from their cards; potentially active files require confirmation.

### Faster workflows

Press `Alt+V` to open the panel and `Ctrl+Shift+V` to paste as plain text. Assign a shortcut for sequential paste when you need to paste a captured series one item at a time. Clipora can start with Windows, stay in the tray, remain on top, and minimize to an edge-docked strip that can be recalled without searching for a window.

### Data under your control

Choose how long history is retained, set a per-item size limit, move the data directory through a verified migration flow, pause passive capture, and exclude selected apps. Backups can be exported and merged into an existing history. The database, payloads, and backup files are currently not encrypted; see the [Privacy Notice](PRIVACY.md) before storing sensitive material.

## Quick start

1. Install Clipora and leave it running in the tray.
2. Copy text, an image, or a file as usual; Clipora records it as a card.
3. Press `Alt+V`, then search, filter, tag, or pin the item you need.
4. Click a card to reuse it, or drag it into another application.

For backup, storage, privacy, window, and troubleshooting instructions, see the [User Manual](manual/README.md).

## Requirements

- Windows 10 version 2004 / build 19041 or later, or Windows 11
- x64 processor
- No separate .NET installation; the installer contains the required runtime
- The current application interface is Simplified Chinese; English project documentation is available here

## Installation

1. Open the [latest release](https://github.com/syoosch/Clipora/releases/latest).
2. Download `Clipora-0.4.3-setup.exe` and run it. Installation is per-user and does not require administrator rights.
3. The installer is not code-signed. If Windows SmartScreen appears, choose **More info**, verify the publisher context and checksum from the release, then choose **Run anyway** if you trust the file.

## Documentation

- [User Manual](manual/README.md)
- [Privacy Notice](PRIVACY.md)
- [Changelog](CHANGELOG.md)
- [Security Policy](SECURITY.md)
- [Contributing Guide](CONTRIBUTING.md)

## Development

Clipora is built with .NET 10, WPF, WPF-UI, and SQLite. Development builds must be launched through `scripts/start-dev.ps1` so they use the repository's isolated data directory.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/start-dev.ps1 -Build
```

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Report vulnerabilities according to [SECURITY.md](SECURITY.md), not in a public issue.

## License

Clipora is available under the [MIT License](LICENSE).
