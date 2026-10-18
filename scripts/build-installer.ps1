<#
.SYNOPSIS
    构建当前 csproj 版本的 per-user 安装包，并生成 SHA-256 与 Release notes。
.PARAMETER Publish
    先运行 publish.ps1 -NoLaunch，再构建安装包。
.PARAMETER SkipVerify
    与 -Publish 连用，跳过 publish.ps1 的 verify 阶段。
#>

[CmdletBinding()]
param(
    [switch] $Publish,
    [switch] $SkipVerify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repo 'src\Clipora\Clipora.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionValues = @($project.SelectNodes('//PropertyGroup/Version') | ForEach-Object { $_.InnerText } | Where-Object { $_ })
if ($versionValues.Count -ne 1 -or $versionValues[0] -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw 'Clipora.csproj must contain exactly one SemVer <Version> value.'
}
$version = $versionValues[0]

$tfm = 'net10.0-windows10.0.19041.0'
$rid = 'win-x64'
$publishDir = Join-Path $repo "src\Clipora\bin\Release\$tfm\$rid\publish"
$exe = Join-Path $publishDir 'Clipora.exe'
$iss = Join-Path $repo 'installer\Clipora.iss'
$distDir = [IO.Path]::GetFullPath((Join-Path $repo 'dist'))
$setup = [IO.Path]::GetFullPath((Join-Path $distDir "Clipora-$version-setup.exe"))
$checksum = [IO.Path]::GetFullPath((Join-Path $distDir "Clipora-$version-SHA256.txt"))
$notes = [IO.Path]::GetFullPath((Join-Path $distDir "Clipora-$version-release-notes.md"))
$distBoundary = $distDir.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($setup, $checksum, $notes)) {
    if (-not $path.StartsWith($distBoundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe dist asset path: $path"
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Test-VersionMatches([string]$Actual, [string]$Expected) {
    if ([string]::IsNullOrWhiteSpace($Actual)) {
        return $false
    }
    $normalized = $Actual.Trim()
    return $normalized -eq $Expected -or $normalized.StartsWith($Expected + '.', [StringComparison]::Ordinal)
}

Write-Host "=== Clipora $version 安装包打包 (Inno Setup) ===" -ForegroundColor Cyan

$iscc = $null
$candidates = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        $iscc = $candidate
        break
    }
}
if (-not $iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $iscc = $command.Source }
}
if (-not $iscc) {
    Write-Host 'FAIL: 未找到 Inno Setup 编译器 ISCC.exe。' -ForegroundColor Red
    exit 2
}
Write-Host "ISCC: $iscc" -ForegroundColor Green

if ($Publish) {
    $publishScript = Join-Path $PSScriptRoot 'publish.ps1'
    Write-Host "`n[发布] 运行 publish.ps1 -NoLaunch ..." -ForegroundColor Yellow
    $publishArgs = @('-NoLaunch')
    if ($SkipVerify) { $publishArgs += '-SkipVerify' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $publishScript @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: publish.ps1 未通过 (exit=$LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "未找到发布产物: $exe"
}
$exeVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($exeVersion) -or -not (Test-VersionMatches $exeVersion $version)) {
    throw "发布 EXE ProductVersion '$exeVersion' 与 csproj Version '$version' 不匹配。"
}
Write-Host "发布产物: $exe (ProductVersion=$exeVersion)" -ForegroundColor Green

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
foreach ($existing in @($setup, $checksum, $notes)) {
    if (Test-Path -LiteralPath $existing -PathType Leaf) {
        Remove-Item -LiteralPath $existing -Force
    }
}

Write-Host "`n[编译] ISCC 编译 $iss ..." -ForegroundColor Yellow
& $iscc "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: ISCC 编译失败 (exit=$LASTEXITCODE)" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "编译后未生成当前版本安装包: $setup"
}

$setupInfo = Get-Item -LiteralPath $setup
$fileVersion = $setupInfo.VersionInfo.FileVersion
$productVersion = $setupInfo.VersionInfo.ProductVersion
if (-not (Test-VersionMatches $fileVersion $version) -or -not (Test-VersionMatches $productVersion $version)) {
    throw "安装包版本元数据不匹配：FileVersion=$fileVersion ProductVersion=$productVersion Expected=$version"
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash.ToUpperInvariant()
$setupName = Split-Path -Leaf $setup
$downloadUrl = "https://github.com/syoosch/Clipora/releases/download/v$version/$setupName"
Write-Utf8NoBom $checksum "$hash  $setupName`r`n"
$checksumText = [IO.File]::ReadAllText($checksum)
if (-not $checksumText.Contains($hash) -or -not $checksumText.Contains($setupName)) {
    throw 'SHA-256 文件名或内容校验失败。'
}

$releaseNotes = @"
# Clipora v$version

Clipora v$version adds minimal floating image windows and a collapsible pinned group to this fully local Windows clipboard-history tool. Keep screenshots and reference images visible while using other applications or playing windowed or borderless games.

## What's new

- Floating reference images: image cards and single-image file cards can open an always-on-top window. Drag the image to move it and drag an edge or corner to resize it while preserving the image's aspect ratio. Exclusive fullscreen games are outside this release's scope.
- Minimal viewing controls: open several images at once; reopening the same card reuses its window. Hover to reveal Close, or press Esc in the active window. Floating images remain visible when the main panel is hidden and close when Clipora exits; they are not restored on restart. There is no copy, edit, or extra toolbar.
- Collapsible pinned group: click the pinned heading to collapse or expand its cards. The heading shows the item count, and search or filter refreshes preserve the state; reloading history or restarting returns it to expanded.
- Consistent card actions: a thin-line window icon distinguishes Float from Open without adding visual weight to the toolbar.
- Clearer privacy information: recycle-bin restore and cleanup controls and automatic recycle-bin expiry are not yet available. Retention cleanup applies only to active, unpinned records; deleting history is not an immediate secure erase. All features remain local.
- Updated runtime: the installer bundles .NET / WPF 10.0.13 with Microsoft's October 2026 security updates; no separate .NET installation is needed.
- Correct image reloads: after drag-out repairs invalid image transparency, reloading reads the updated file instead of reusing stale cached pixels.
- Reliable exit: Alt+F4 and other system close actions no longer throw when closing is configured to exit Clipora.

## Download

**Recommended download:** [Download Clipora v$version for Windows (x64)]($downloadUrl)

This is the installer most users should choose. The ``Clipora-$version-SHA256.txt`` asset is only for verifying the download.

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- x64
- Self-contained; no separate .NET installation required
- Per-user installation; administrator rights are not normally required

> The installer is not code-signed. Windows SmartScreen may display a warning. Choose **More info -> Run anyway** only after verifying that the SHA-256 value matches the checksum below.

## Integrity

SHA-256 for ``$setupName``:

``````text
$hash
``````

The same value is provided in ``Clipora-$version-SHA256.txt``.

## Privacy

Clipora follows a local-only, offline privacy model and does not upload clipboard data. Clipboard history is stored on your device under ``%LOCALAPPDATA%\Clipora`` by default. The SQLite database, stored attachments, and ``.clpbak`` backups are not encrypted, so keep them in trusted locations.

See the full [changelog](https://github.com/syoosch/Clipora/blob/main/CHANGELOG.md), [user manual](https://github.com/syoosch/Clipora/blob/main/manual/README.md), and [privacy notice](https://github.com/syoosch/Clipora/blob/main/PRIVACY.md).

---

# Clipora v$version（中文版）

Clipora v$version 为这款全本地 Windows 剪贴板历史工具加入极简悬浮图片窗和可收起的置顶分组。使用其他应用或游玩窗口化、无边框游戏时，可以持续查看截图与参考图片。

## 更新内容

- 悬浮查看参考图片：图片卡片与单个图片文件卡片可打开始终置顶的窗口，直接拖动图片移动，拖动边缘或角时按图片比例缩放。本版暂不适配独占全屏游戏。
- 极简查看操作：支持同时打开多张图片，同一卡片重复打开会复用窗口。鼠标移入显示关闭按钮，激活窗口后也可按 Esc 关闭。主面板隐藏后悬浮图片仍保留，退出 Clipora 时关闭，重启不恢复；不提供复制、编辑或额外工具栏。
- 置顶分组可收起：点击置顶标题可收起或展开卡片，标题显示条目数；搜索或筛选刷新时保留状态，重新加载历史或重启后默认展开。
- 一致的卡片操作图标：使用细线窗口图标区分“悬浮”和“打开”，保持工具栏轻巧简洁。
- 更准确的隐私说明：回收站恢复/清空入口和自动过期清理尚未提供。保存周期只清理未置顶的活动记录，删除历史不等于立即安全擦除；所有功能仍在本地运行。
- 更新运行时：安装包自带包含微软 2026 年 10 月安全更新的 .NET / WPF 10.0.13，无需用户单独安装 .NET。
- 正确重载图片：拖放修复异常透明度后，重新加载会读取更新后的文件，避免重复使用旧缓存中的像素。
- 稳定退出：设置“关闭时退出”后，使用 Alt+F4 等系统关闭操作不再触发异常。

## 下载

**推荐下载：** [下载 Clipora v$version Windows (x64) 安装版]($downloadUrl)

这是大多数用户应该选择的安装程序。``Clipora-$version-SHA256.txt`` 资产仅用于校验下载文件。

- Windows 10 版本 2004（内部版本 19041）或更高版本，或 Windows 11
- x64
- 自包含；无需单独安装 .NET
- 按当前用户安装；通常不需要管理员权限

> 安装程序尚未进行代码签名，Windows SmartScreen 可能显示警告。请先确认下面的 SHA-256 与校验文件一致，再选择“更多信息 → 仍要运行”。

## 完整性校验

``$setupName`` 的 SHA-256：

``````text
$hash
``````

``Clipora-$version-SHA256.txt`` 中也提供了相同的校验值。

## 隐私

Clipora 采用纯本地、离线的隐私模式，不上传任何剪贴板数据。剪贴板历史默认存储在本机的 ``%LOCALAPPDATA%\Clipora``。SQLite 数据库、已保存附件和 ``.clpbak`` 备份当前未加密，请仅保存在可信位置。

查看完整的[更新日志](https://github.com/syoosch/Clipora/blob/main/CHANGELOG.md)、[中文用户手册](https://github.com/syoosch/Clipora/blob/main/manual/README.zh-CN.md)和[隐私声明](https://github.com/syoosch/Clipora/blob/main/PRIVACY.md)。
"@
Write-Utf8NoBom $notes ($releaseNotes.Trim() + "`r`n")
$releaseNotesText = [IO.File]::ReadAllText($notes)
$chineseEditionTitle = "# Clipora v$version（中文版）"
if ([regex]::Matches($releaseNotesText, [regex]::Escape($hash)).Count -ne 2) {
    throw 'Release notes 的中英文版未分别包含最终安装包 SHA-256。'
}
if ([regex]::Matches($releaseNotesText, [regex]::Escape($downloadUrl)).Count -ne 2) {
    throw 'Release notes 的中英文版未分别包含同一个版本专属下载链接。'
}
if (-not $releaseNotesText.Contains($chineseEditionTitle)) {
    throw 'Release notes 未包含完整中文版标题。'
}

$signature = Get-AuthenticodeSignature -LiteralPath $setup
$setupMb = [math]::Round($setupInfo.Length / 1MB, 1)
Write-Host "`n=== 打包完成 ===" -ForegroundColor Cyan
Write-Host "安装包: $setup ($setupMb MB)" -ForegroundColor Cyan
Write-Host "版本: File=$fileVersion Product=$productVersion" -ForegroundColor Cyan
Write-Host "SHA-256: $hash" -ForegroundColor Cyan
Write-Host "签名状态: $($signature.Status)" -ForegroundColor Cyan
Write-Host "校验文件: $checksum" -ForegroundColor Cyan
Write-Host "发布说明: $notes" -ForegroundColor Cyan
exit 0
