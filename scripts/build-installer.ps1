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

Clipora v$version is an interaction-stability update for this fully local Windows clipboard-history tool. It makes image previews predictable during hover and scrolling, improves placement across DPI and multi-monitor layouts, and removes a bottom-edge hover loop that could make cards jitter.

## What's new

- Predictable preview timing: image previews open only after 350 ms of continuous hover, and scrolling cancels pending or open previews until the list has been still for 120 ms.
- Safer virtualized scrolling: stale preview targets are discarded when containers are recycled or detached, preventing crashes during very fast history browsing.
- DPI- and monitor-aware placement: the 400 x 400 DIP preview prefers the outside of the panel with a 12 DIP gap and no overlap, including on scaled, negative-coordinate, and small work areas.
- Responsive image loading: decoding runs on a dedicated STA worker with a bounded local cache and latest-request-wins queue.
- Stable card hover: a fixed hit surface and a narrow bottom retention strip prevent the 3 DIP lift animation from repeatedly entering and leaving at the card edge.

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
"@
Write-Utf8NoBom $notes ($releaseNotes.Trim() + "`r`n")
if (-not [IO.File]::ReadAllText($notes).Contains($hash)) {
    throw 'Release notes 未包含最终安装包 SHA-256。'
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
