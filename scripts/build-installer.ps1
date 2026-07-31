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

Clipora v$version is a maintenance and accessibility update for this fully local Windows clipboard-history tool. It improves settings typography and layout, keeps compact scrollbars intact on very long pages, reduces redundant scrolling work, and adds an English-first bilingual installer.

## What's new

- Clearer Chinese settings headings: explicit Microsoft YaHei UI fallback prevents Chinese page titles from falling back to visually inconsistent glyphs.
- Cleaner maintenance pages: the unfinished Empty Recycle Bin row has been removed, and the backup safety notice now aligns with the export and restore cards below it.
- Stable long-page scrollbars: the compact vertical thumb keeps its 24 DIP minimum length instead of being clipped on very long content, while the existing 3 / 5 / 6 DIP visual states and scrolling behavior remain unchanged.
- Lighter scrolling internals: redundant preview-property writes and a per-frame state-array copy are removed without changing scroll distance, timing, preview suppression, virtualization, or wheel behavior.
- English-first bilingual setup: the normal install and uninstall flow, navigation buttons, optional tasks, post-install launch, and data-retention warnings now include Simplified Chinese without adding a language-selection step.
- Full bilingual release information: each GitHub Release now keeps the complete English format first and follows it with a section-for-section Chinese edition containing the same download, integrity, compatibility, signing, and privacy facts.

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

Clipora v$version 是这款全本地 Windows 剪贴板历史工具的维护与易用性更新。本版改进设置页字体和布局，确保紧凑滚动条在超长页面中保持完整，减少无效滚动开销，并加入英文优先的双语安装器。

## 更新内容

- 更清晰的中文设置标题：显式加入 Microsoft YaHei UI 回退字体，避免中文页面标题落入观感不一致的替代字形。
- 更整洁的维护页面：移除尚未实现的“清空回收站”设置行，并让备份安全提示与下方导出、恢复卡片左右对齐。
- 稳定的超长页面滚动条：紧凑竖向滑块保持 24 DIP 最小长度，不再被超长内容裁切；既有 3 / 5 / 6 DIP 视觉状态和滚动行为不变。
- 更轻量的滚动内部处理：移除重复的预览属性写入和每帧状态数组复制，不改变滚动距离、时序、预览抑制、虚拟化或滚轮行为。
- 英文优先双语安装：正常安装与卸载流程、导航按钮、附加任务、安装后启动和数据保留警告均加入简体中文，同时不增加语言选择步骤。
- 完整双语发布信息：每个 GitHub Release 保留完整英文格式在前，随后提供章节一一对应的中文版，两版下载、完整性、兼容性、签名和隐私事实一致。

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
