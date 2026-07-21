<p align="center">
  <img src="src/Clipora/Assets/clipora-icon.png" width="96" alt="Clipora 图标">
</p>

# Clipora

[![最新版本](https://img.shields.io/github/v/release/syoosch/Clipora?display_name=tag)](https://github.com/syoosch/Clipora/releases/latest)
[![下载量](https://img.shields.io/github/downloads/syoosch/Clipora/total)](https://github.com/syoosch/Clipora/releases)
[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![平台](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)](#系统要求)

[English](README.md) | **简体中文**

Clipora 是一款 Windows 本地剪贴板历史工具。它自动记录并整理你复制的内容，让常用信息能够被快速找回和重用，同时不会把剪贴板数据发送到设备之外。

当前版本：**v0.4.4** · [下载最新版安装包](https://github.com/syoosch/Clipora/releases/latest) · [阅读使用手册](manual/README.zh-CN.md)

## 为什么选择 Clipora

- **快速找回复制内容**：搜索历史记录，按内容类型或标签筛选，并将重要内容置顶。
- **不只保存文字**：支持富文本、链接、代码、颜色、图片、文件，以及直接拖入面板的内容。
- **减少重复操作**：点击卡片即可复制并粘贴回原窗口，也可拖到其他应用，或使用可自定义的全局快捷键。
- **数据由你控制**：Clipora 纯本地运行，支持暂停记录和应用排除，数据库与附件均保存在本机。

## 核心能力

### 捕获与整理

Clipora 会自动识别复制的文字、富文本、URL、代码、颜色、图片和文件，并在卡片上显示来源应用，按时间段折叠分组。你也可以把文件、文字、图片或富文本直接拖入面板，为内容添加标签、置顶重要项目，并从回收站恢复误删内容。

### 查找与重用

通过关键词、内容类型或标签筛选历史记录；Windows 本地 OCR 还能让图片中的文字参与搜索。点击卡片可复制并按设置自动粘贴回之前的窗口，也可以把支持的内容拖到其他应用。HTTP/HTTPS 链接和普通文件可以从卡片打开，可能主动执行的文件会先要求确认。

### 快捷工作流

默认按 `Alt+V` 打开面板，按 `Ctrl+Shift+V` 以纯文本粘贴；需要逐条粘贴一组内容时，可为顺序粘贴设置专用快捷键。Clipora 支持开机启动、托盘常驻、窗口置顶，以及最小化为贴边悬浮条，减少寻找窗口的步骤。

### 数据与隐私

你可以设置历史保留时间和单条大小上限，通过带校验的迁移流程调整数据目录，暂停被动捕获，或排除指定应用。备份可以导出并合并回现有历史。数据库、附件和备份文件目前均未加密；保存敏感内容前请阅读[隐私说明](PRIVACY.md)。

## 快速上手

1. 安装 Clipora，并让它在托盘中保持运行。
2. 像平常一样复制文字、图片或文件，Clipora 会将其记录为卡片。
3. 按 `Alt+V` 打开面板，通过搜索、筛选、标签或置顶找到需要的内容。
4. 点击卡片重用内容，或将卡片拖入其他应用。

备份、存储、隐私、窗口行为和故障排查请查看[使用手册](manual/README.zh-CN.md)。

## 系统要求

- Windows 10 version 2004 / build 19041 及以上，或 Windows 11
- x64 处理器
- 无需单独安装 .NET，安装包已包含所需运行时

## 安装

1. 打开[最新发布页面](https://github.com/syoosch/Clipora/releases/latest)。
2. 下载并运行 `Clipora-0.4.4-setup.exe`。安装范围为当前用户，不需要管理员权限。
3. 当前安装包未做代码签名。若 Windows SmartScreen 出现提示，请选择“更多信息”，核对 Release 中的文件名和校验值；确认文件可信后再选择“仍要运行”。

## 文档

- [使用手册](manual/README.zh-CN.md)
- [隐私说明](PRIVACY.md)
- [更新日志](CHANGELOG.md)
- [安全策略](SECURITY.md)
- [贡献指南](CONTRIBUTING.md)

## 开发与贡献

Clipora 使用 .NET 10、WPF、WPF-UI 和 SQLite。开发版必须通过 `scripts/start-dev.ps1` 启动，以确保使用仓库内的隔离数据目录。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/start-dev.ps1 -Build
```

提交 Pull Request 前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。安全漏洞请按 [SECURITY.md](SECURITY.md) 私下报告，不要创建公开 Issue。

## 许可证

Clipora 使用 [MIT License](LICENSE) 发布。
