<#
.SYNOPSIS
    Clipora 发布前自动化：restore → verify（Debug/Release build + selftest）→ 单文件
    self-contained publish → 发布产物校验 → 隔离存活检查。任何一步失败即停止并报告。
.DESCRIPTION
    将发布流程固化为一条可重复命令，确保构建、校验和隔离存活检查按固定顺序执行。
    发布模型固定：win-x64、--self-contained、PublishSingleFile=true、
    IncludeNativeLibrariesForSelfExtract=true（见 Clipora.csproj），本脚本不得改 TFM/RID/发布模型。

    数据安全：存活检查启动的是 Release 单文件 EXE，必须经 CLIPORA_DATA_DIR 指向
    临时隔离目录，绝不触碰正式数据 %LOCALAPPDATA%\Clipora。检查后强制结束进程并清理临时目录。
.PARAMETER SkipVerify
    跳过 verify.ps1（Debug/Release build + selftest）。仅在已单独跑过 verify 时使用。
.PARAMETER NoLaunch
    跳过发布产物的隔离存活检查（仅构建+打包+产物校验）。
.PARAMETER KeepPdb
    允许发布目录保留 Clipora.pdb（默认也允许 PDB，本开关仅用于显式说明意图，不影响判定）。
#>

param(
    [switch] $SkipVerify,
    [switch] $NoLaunch,
    [switch] $KeepPdb
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src\Clipora\Clipora.csproj"
$tfm = "net10.0-windows10.0.19041.0"
$rid = "win-x64"
$publishDir = Join-Path $repo "src\Clipora\bin\Release\$tfm\$rid\publish"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "=== Clipora 发布前自动化 ===" -ForegroundColor Cyan
Write-Host "仓库: $repo"
Write-Host "发布目标: $rid 单文件 self-contained"

# 辅助：执行原生命令并严格按退出码判定（规避 PS5.1 把 dotnet stderr 误判为 NativeCommandError）
function Invoke-Native {
    param([string] $Label, [scriptblock] $Command)
    Write-Host "`n$Label" -ForegroundColor Yellow
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $out = & $Command 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) {
        Write-Host "FAIL: $Label (exit=$code)" -ForegroundColor Red
        Write-Host ($out -join "`n")
        exit 1
    }
    return $out
}

# ── 0. 不得与正在运行的实例同时构建/启动 ──
$running = Get-Process -Name Clipora -ErrorAction SilentlyContinue
if ($running) {
    $ids = ($running | ForEach-Object { $_.Id }) -join ', '
    Write-Host "FAIL: 检测到正在运行的 Clipora (PID: $ids)；发布前请先全部退出。" -ForegroundColor Red
    exit 2
}

# ── 1. restore（普通，供 verify 的 --no-restore 构建使用）──
Invoke-Native "[1/5] restore..." { dotnet restore $proj -p:NuGetAudit=false } | Out-Null
Write-Host "  OK — restore 完成" -ForegroundColor Green

# ── 2. verify（Debug/Release build + selftest，含既有 3 次重试契约）──
if ($SkipVerify) {
    Write-Host "`n[2/5] verify... 跳过（-SkipVerify）" -ForegroundColor DarkYellow
} else {
    $verify = Join-Path $PSScriptRoot "verify.ps1"
    Write-Host "`n[2/5] verify.ps1 (Debug/Release build + selftest)..." -ForegroundColor Yellow
    & powershell -ExecutionPolicy Bypass -File $verify
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: verify.ps1 未通过 (exit=$LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    Write-Host "  OK — verify 全部通过" -ForegroundColor Green
}

# ── 3. 单文件 self-contained publish（先 RID 专用 restore）──
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
Invoke-Native "[3/5] restore ($rid)..." { dotnet restore $proj -r $rid -p:NuGetAudit=false } | Out-Null
Invoke-Native "[3/5] publish (单文件 self-contained $rid)..." {
    dotnet publish $proj -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true --no-restore -p:NuGetAudit=false
} | Out-Null
Write-Host "  OK — publish 完成" -ForegroundColor Green

# ── 4. 发布产物校验：除可选 Clipora.pdb 外只允许 Clipora.exe ──
Write-Host "`n[4/5] 发布产物校验..." -ForegroundColor Yellow
if (-not (Test-Path $publishDir)) {
    Write-Host "FAIL: 发布目录不存在: $publishDir" -ForegroundColor Red
    exit 1
}
$files = Get-ChildItem $publishDir -File
$exe = $files | Where-Object { $_.Name -eq "Clipora.exe" }
if (-not $exe) {
    Write-Host "FAIL: 发布目录缺少 Clipora.exe" -ForegroundColor Red
    Write-Host ($files | ForEach-Object { "  - $($_.Name)" }) -join "`n"
    exit 1
}
$allowed = @("Clipora.exe", "Clipora.pdb")
$unexpected = $files | Where-Object { $allowed -notcontains $_.Name }
if ($unexpected) {
    Write-Host "FAIL: 发布目录存在未预期文件（应仅 Clipora.exe + 可选 Clipora.pdb）：" -ForegroundColor Red
    $unexpected | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Red }
    exit 1
}
$exeMb = [math]::Round($exe.Length / 1MB, 1)
$pdb = $files | Where-Object { $_.Name -eq "Clipora.pdb" }
$pdbNote = if ($pdb) { "（含 PDB）" } else { "（无 PDB）" }
Write-Host "  OK — 仅 Clipora.exe$pdbNote，大小 $exeMb MB" -ForegroundColor Green

# ── 5. 隔离存活检查（绝不触碰正式数据）──
if ($NoLaunch) {
    Write-Host "`n[5/5] 存活检查... 跳过（-NoLaunch）" -ForegroundColor DarkYellow
} else {
    Write-Host "`n[5/5] 隔离存活检查..." -ForegroundColor Yellow
    $tempData = Join-Path ([System.IO.Path]::GetTempPath()) ("clipora-publish-smoke-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempData -Force | Out-Null
    $prevData = $env:CLIPORA_DATA_DIR
    $proc = $null
    try {
        $env:CLIPORA_DATA_DIR = $tempData
        $proc = Start-Process -FilePath $exe.FullName -PassThru
        Start-Sleep -Seconds 6
        $live = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $live) {
            Write-Host "FAIL: 发布 EXE 启动后进程已退出（存活检查失败）" -ForegroundColor Red
            $crash = Join-Path ([System.IO.Path]::GetTempPath()) "clipora-crash.txt"
            if (Test-Path $crash) { Write-Host (Get-Content $crash -TotalCount 15 | Out-String) }
            exit 1
        }
        $responding = $live.Responding
        Write-Host "  OK — 进程存活 (PID=$($proc.Id), Responding=$responding)，使用隔离数据目录" -ForegroundColor Green
    }
    finally {
        if ($proc) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
            # 等待进程真正退出，确保释放隔离数据目录的文件句柄后再清理
            try { Wait-Process -Id $proc.Id -Timeout 5 -ErrorAction SilentlyContinue } catch {}
        }
        if ($null -eq $prevData) {
            Remove-Item Env:CLIPORA_DATA_DIR -ErrorAction SilentlyContinue
        } else {
            $env:CLIPORA_DATA_DIR = $prevData
        }
        # 句柄释放可能略有延迟，短重试清理临时隔离目录
        for ($r = 1; $r -le 3; $r++) {
            if (-not (Test-Path $tempData)) { break }
            try { Remove-Item $tempData -Recurse -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 }
        }
    }
}

$elapsed = $stopwatch.Elapsed.ToString("mm\:ss")
Write-Host "`n=== 发布前自动化全部通过 ($elapsed) ===" -ForegroundColor Cyan
Write-Host "发布产物: $($exe.FullName)" -ForegroundColor Cyan
exit 0
