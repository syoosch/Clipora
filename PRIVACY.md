# Clipora 隐私说明 / Privacy Notice

最后更新 / Last updated: 2026-07-13

## 简体中文

Clipora 是纯本地 Windows 剪贴板历史工具。应用本身不发起网络请求，不上传剪贴板内容、设置、诊断或使用数据，也没有账号与云端服务。

### 存储的数据

- 剪贴板文字、富文本、URL、代码、颜色、图片、文件副本或文件原路径引用；
- 来源应用名称/图标、标签、置顶/删除状态、创建时间和本地 OCR 结果；
- 应用设置，以及你主动导出的 `.clpbak` 备份；
- 发生错误时的本地脱敏诊断元数据。

安装版默认数据目录为 `%LOCALAPPDATA%\Clipora`，也可在设置中迁移到你选择的本地目录。默认保留 3 天，可改为 1/3/7/30 天或永久；置顶项不参与自动过期清理。删除项先进入回收站，之后按应用的清理规则移除。

### 明文与本机访问风险

当前版本的 SQLite 数据库、图片/附件/富文本 sidecar 和 `.clpbak` 备份均**未加密**。它们不会被 Clipora 上传，但在同一 Windows 用户权限下运行的其他进程、拥有相应文件权限的人，或能够读取你备份位置的人，可能访问这些内容。请使用受信任的 Windows 账户和磁盘保护措施，并把备份保存到可信位置；只导入自己创建或来源可信的备份。

### 你可以控制什么

- 在托盘或隐私设置中暂停被动记录；
- 将指定应用加入排除名单；Clipora 也尊重 Windows 的“排除剪贴板历史”标记；
- 删除单条记录、清理历史或调整保留期；
- 卸载时选择“同时删除所有数据”。默认卸载会保留数据，便于重装恢复；
- 手动删除已导出的 `.clpbak`。应用删除历史时不会自动删除你另存到其他位置的备份。

### 本地诊断

Release 诊断保存在 `%LOCALAPPDATA%\Clipora\diagnostics`，仅含 UTC 时间、应用版本、随机错误编号、稳定错误码、异常类型、HResult 和 Clipora 内部组件名；不含剪贴板正文、用户名、文件路径、异常消息或完整堆栈。诊断从不上传，并自动删除 7 天以上或超出最近 20 份的文件。

## English

Clipora is a local-only Windows clipboard-history application. The app itself makes no network requests and does not upload clipboard content, settings, diagnostics, or usage data. It has no account or cloud service.

### Data stored

- Clipboard text, rich text, URLs, code, colors, images, copied files, or references to original file paths;
- Source-application names/icons, tags, pin/deletion state, timestamps, and local OCR results;
- App settings and `.clpbak` backups that you explicitly export;
- Redacted local diagnostic metadata when an error occurs.

The installed app stores data in `%LOCALAPPDATA%\Clipora` by default and can migrate it to another local folder selected in Settings. Retention defaults to 3 days and can be set to 1/3/7/30 days or forever; pinned items are exempt from automatic expiry. Deleted items first enter the recycle bin and are later removed under the app's cleanup rules.

### Plaintext and local-access risk

The current SQLite database, images/attachments/rich-text sidecars, and `.clpbak` backups are **not encrypted**. Clipora does not upload them, but another process running as the same Windows user, anyone with the relevant file permissions, or anyone who can read your backup location may access their contents. Use a trusted Windows account and appropriate disk protection, keep backups in trusted locations, and import only backups you created or obtained from a trusted source.

### Your controls

- Pause passive capture from the tray or Privacy settings;
- Exclude selected applications; Clipora also respects Windows' “exclude from clipboard history” marker;
- Delete individual records, clear history, or change retention;
- Choose “also delete all data” during uninstall. Uninstall keeps data by default so a reinstall can recover it;
- Manually delete exported `.clpbak` files. Deleting Clipora history does not remove backups you saved elsewhere.

### Local diagnostics

Release diagnostics are stored in `%LOCALAPPDATA%\Clipora\diagnostics`. They contain only UTC time, app version, a random error ID, a stable error code, exception type, HResult, and the first Clipora component name—never clipboard content, usernames, file paths, exception messages, or full stack traces. Diagnostics are never uploaded and files older than 7 days or beyond the newest 20 are automatically removed.
