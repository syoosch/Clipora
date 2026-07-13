# Clipora User Manual

**English** | [简体中文](README.zh-CN.md) · [Back to the project page](../README.md)

This manual applies to Clipora v0.4.3. The current application interface is Simplified Chinese; English names below describe the corresponding controls and settings.

## Contents

- [Install and first run](#install-and-first-run)
- [Main window and window behavior](#main-window-and-window-behavior)
- [Capture clipboard content](#capture-clipboard-content)
- [Find and organize history](#find-and-organize-history)
- [Reuse, open, and drag content](#reuse-open-and-drag-content)
- [Global hotkeys](#global-hotkeys)
- [Settings](#settings)
- [Files and storage](#files-and-storage)
- [Privacy controls](#privacy-controls)
- [Backup and restore](#backup-and-restore)
- [Uninstall and data retention](#uninstall-and-data-retention)
- [Troubleshooting](#troubleshooting)

## Install and first run

1. Open the [latest release](https://github.com/syoosch/Clipora/releases/latest) and download `Clipora-0.4.3-setup.exe`.
2. Run the installer. Clipora installs for the current Windows user and does not require administrator rights.
3. The installer is currently unsigned. If SmartScreen appears, choose **More info**, compare the installer name and SHA-256 with the release, and continue only if the file is trusted.
4. Keep Clipora running in the tray so it can monitor new clipboard changes.

Clipora stores data under `%LOCALAPPDATA%\Clipora` by default. A custom data location can be selected later in Settings.

## Main window and window behavior

- Press `Alt+V` to show the main panel.
- The title bar provides search/filter, always-on-top, minimize, Settings, and close controls.
- History is shown newest first and grouped into collapsible eight-hour periods. Pinned cards remain in a separate section at the top.
- Cards show the detected type, source application, time, preview, and actions that appear on hover.
- Minimizing uses the configured behavior; the default edge-docked strip can be moved between screen edges and recalled by hovering or clicking.
- Closing the window normally keeps Clipora running in the notification area. Use the tray menu when you want to reopen or exit the application.
- Settings can change whether Clipora appears in the taskbar, stays on top, starts silently, or starts with Windows.

## Capture clipboard content

Clipora passively watches normal clipboard changes without replacing `Ctrl+C` or `Ctrl+V`. It recognizes:

- plain text and rich text;
- HTTP/HTTPS URLs;
- code and color values;
- images and screenshots;
- files and folders.

You can also drag files, text, images, or rich text directly into the panel. A manually dragged item is treated as an explicit import and can still be saved while passive capture is paused.

The default per-item limit is 25 MB and can be changed in Settings. Oversized text or image content is not stored. File captures below the limit are copied into Clipora's data directory when possible; folders and larger file sets are stored as references to their original locations.

When duplicate merging is enabled, copying or dragging the same external content again moves the existing item forward instead of creating another card. Reusing an item from Clipora does not change its original timestamp.

## Find and organize history

- Use the search button in the title bar to reveal keyword search and filters.
- Switch between type filtering and tag filtering.
- Image text detected by Windows OCR is included in search results when OCR is enabled.
- Create tags from the filter area or Settings, then rename, recolor, reorder, or delete them in tag management.
- Pin items that must remain at the top and must not be removed by retention cleanup.
- Delete sends an item to the recycle bin. Restore it when needed, or permanently remove recycled items from Settings.
- The optional back-to-top button appears after scrolling through a long history.

## Reuse, open, and drag content

### Reuse a card

Click a card to write its content back to the clipboard. When automatic paste is enabled, Clipora also returns to the previous window and sends paste; if automatic paste cannot complete, the content remains copied so it can be pasted manually.

### Card action

- Text, rich text, code, and color cards open a selectable text view.
- Images and ordinary files can be opened with the default Windows application.
- URL cards open only absolute HTTP/HTTPS links.
- Executables, scripts, shortcuts, registry files, and similar active file types show a confirmation dialog before opening. Cancel is the default choice.

### Drag out

Drag supported cards into another application or File Explorer. A reference-only file card checks the original path when reuse or drag starts. If the source has moved or been deleted, restore it to the original location or capture it again.

## Global hotkeys

| Action | Default | Behavior |
| --- | --- | --- |
| Open panel | `Alt+V` | Shows or activates Clipora. |
| Paste as plain text | `Ctrl+Shift+V` | Removes formatting before pasting into the previous application. |
| Sequential paste | Not assigned | Pastes a captured sequence one item at a time. |

All three shortcuts can be changed in Settings. Clipora reports registration conflicts and keeps the prior valid shortcut when a replacement cannot be registered. It does not intercept the normal system `Ctrl+C`, `Ctrl+V`, or `Win+V` shortcuts.

Sequential paste stops after the last item instead of looping. Capture or import new content before starting another sequence.

## Settings

### General

- automatic paste after reusing a card;
- OCR processing for images;
- start with Windows and silent startup;
- always-on-top, taskbar visibility, minimize and close behavior;
- back-to-top button.

### Storage

- retention: 1, 3, 7, or 30 days, or forever; the default is 3 days;
- per-item size limit, default 25 MB;
- duplicate merging;
- data-directory migration;
- permanent recycle-bin cleanup.

### Hotkeys

Record, reset, and check conflicts for panel, plain-text paste, and sequential-paste shortcuts.

### Privacy

Pause passive recording or exclude selected running applications from capture.

### Appearance

Choose System, Light, or Dark color mode; switch between Fluent and Liquid Glass; configure a local Fluent background and its opacity; or adjust Liquid Glass transparency.

### Backup and restore

Export a `.clpbak` file or inspect and merge a trusted backup into the current history.

## Files and storage

Clipora uses smart file storage:

- Smaller file selections are copied into the managed data directory, so the card remains usable if the original file is later removed.
- Folders and larger selections are stored as references. They use less managed storage but depend on the original path.
- Reference availability is checked only when the item is reused or dragged, not every time the history list loads.

The installed app stores data in `%LOCALAPPDATA%\Clipora` unless a custom directory is selected. Migration copies and verifies the complete data set before switching. If migration fails, Clipora continues using the original directory and does not merge or delete either location automatically.

If a configured data directory is unavailable at startup, Clipora asks you to retry, use the default location, or exit. It does not silently switch directories.

## Privacy controls

- Clipora has no clipboard-data upload or network service; history remains on the local machine.
- Pause stops passive clipboard recording. Explicit drag-in remains available.
- Application exclusions prevent passive capture from selected processes.
- Clipora respects the Windows marker that excludes content from clipboard history. If that marker cannot be read reliably, the item is skipped.
- The SQLite database, payload files, and `.clpbak` backups are not encrypted. Other processes running as the same Windows user may be able to read them.

Read the complete [Privacy Notice](../PRIVACY.md) for data categories, retention, diagnostics, deletion, and backup guidance.

## Backup and restore

### Export

Open Settings, choose **Backup and restore**, and export a `.clpbak` file. The backup contains original clipboard content and is not encrypted. Store it only in a trusted location.

### Restore

1. Select a backup created by you or obtained from a trusted source.
2. Review its creation time and item count.
3. Confirm merge import.

Import validates the archive before changing the current data. Valid items are merged, duplicates are skipped, and existing history is not deleted. A rejected backup may be corrupted, incompatible, or unsafe; do not modify it merely to bypass validation.

## Uninstall and data retention

Uninstall Clipora from the Start menu or Windows Settings. The uninstaller can close a running tray instance after confirmation.

Clipboard data is retained by default so a later reinstall can reuse it. Select the uninstall option to delete all Clipora data only when permanent removal is intended. Export any backup you need before deleting data.

## Troubleshooting

### New content is not captured

- Confirm Clipora is still running in the tray.
- Check whether recording is paused or the source application is excluded.
- The source may have set the Windows clipboard-history exclusion marker.
- The content may exceed the configured size limit.
- Content is skipped when its privacy status cannot be confirmed safely.

### A file card cannot be reused or dragged

The card may contain only a reference and the source path may have moved or been deleted. Restore the source to its original path or capture it again.

### A hotkey does not work

Open Hotkey Settings and check the conflict message. Choose another combination if Windows or another application already owns it.

### The main window disappeared

Check the notification area first. If Clipora was minimized to the edge, move the pointer to the docked strip or click it to restore the panel.

### SmartScreen blocks the installer

Download only from the official release page, compare the SHA-256 with the release checksum, and continue only when the file is trusted. The current installer is unsigned.

### The configured data directory is missing

Reconnect the drive or restore the directory, then retry. Choose the default location only when you intentionally want Clipora to start with a separate default data root; the missing directory is not deleted.

### A backup is rejected

Use the original backup file and verify that it came from a trusted Clipora installation. Corrupted, incompatible, or structurally unsafe archives are intentionally rejected before import.

For bugs, use the repository's structured [issue forms](https://github.com/syoosch/Clipora/issues). Do not attach private clipboard contents or personal files. Report vulnerabilities according to the [Security Policy](../SECURITY.md).
