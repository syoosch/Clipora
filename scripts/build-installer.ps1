<#
.SYNOPSIS
    M6.3c — 用 Inno Setup 把已发布的单文件 EXE 打成 per-user 安装包。
.DESCRIPTION
    步骤：定位 ISCC.exe → 校验发布产物存在（可选先跑 publish.ps1）→ 调 ISCC 编译
    installer\Clipora.iss → 输出到 dist\Clipora-<版本>-setup.exe。

    数据安全：本脚本只编译安装包，不启动应用、不触碰正式数据。安装包本身 per-user
    （无需管理员），卸载默认保留数据目录（详见 installer\Clipora.iss 注释）。
.PARAMETER Publish
    编译前先运行 scripts\publish.ps1 -NoLaunch（restore + verify + 单文件 publish + 产物校验）。
    不带此开关时要求发布产物已存在。
.PARAMETER SkipVerify
    与 -Publish 连用：跳过 publish.ps1 的 verify 步骤（仅在已单独跑过 verify 时）。
#>

param(
    [switch] $Publish,
    [switch] $SkipVerify
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$tfm = "net10.0-windows10.0.19041.0"
$rid = "win-x64"
$publishDir = Join-Path $repo "src\Clipora\bin\Release\$tfm\$rid\publish"
$exe = Join-Path $publishDir "Clipora.exe"
$iss = Join-Path $repo "installer\Clipora.iss"
$distDir = Join-Path $repo "dist"

Write-Host "=== Clipora 安装包打包 (Inno Setup) ===" -ForegroundColor Cyan

# ── 1. 定位 ISCC.exe ──
$iscc = $null
$candidates = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
foreach ($c in $candidates) {
    if ($c -and (Test-Path $c)) { $iscc = $c; break }
}
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    Write-Host "FAIL: 未找到 Inno Setup 编译器 ISCC.exe。" -ForegroundColor Red
    Write-Host "请先安装 Inno Setup 6（任选其一）：" -ForegroundColor Yellow
    Write-Host "  winget install --id JRSoftware.InnoSetup -e" -ForegroundColor Yellow
    Write-Host "  或从 https://jrsoftware.org/isdl.php 下载安装" -ForegroundColor Yellow
    exit 2
}
Write-Host "ISCC: $iscc" -ForegroundColor Green

# ── 2. 发布产物（可选先 publish）──
if ($Publish) {
    $publishScript = Join-Path $PSScriptRoot "publish.ps1"
    Write-Host "`n[发布] 运行 publish.ps1 -NoLaunch ..." -ForegroundColor Yellow
    $publishArgs = @("-NoLaunch")
    if ($SkipVerify) { $publishArgs += "-SkipVerify" }
    & powershell -ExecutionPolicy Bypass -File $publishScript @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: publish.ps1 未通过 (exit=$LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}
if (-not (Test-Path $exe)) {
    Write-Host "FAIL: 未找到发布产物: $exe" -ForegroundColor Red
    Write-Host "请先运行: powershell -ExecutionPolicy Bypass -File scripts/publish.ps1" -ForegroundColor Yellow
    Write-Host "或带 -Publish 重跑本脚本。" -ForegroundColor Yellow
    exit 1
}
$exeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "发布产物: $exe ($exeMb MB)" -ForegroundColor Green

# ── 3. 编译安装包 ──
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Write-Host "`n[编译] ISCC 编译 $iss ..." -ForegroundColor Yellow
& $iscc "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: ISCC 编译失败 (exit=$LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

# ── 4. 报告产物 ──
$setup = Get-ChildItem $distDir -Filter "Clipora-*-setup.exe" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) {
    Write-Host "FAIL: 编译后未在 dist\ 找到安装包" -ForegroundColor Red
    exit 1
}
$setupMb = [math]::Round($setup.Length / 1MB, 1)
Write-Host "`n=== 打包完成 ===" -ForegroundColor Cyan
Write-Host "安装包: $($setup.FullName) ($setupMb MB)" -ForegroundColor Cyan
exit 0
