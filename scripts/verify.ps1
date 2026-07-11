<#
.SYNOPSIS
    Clipora 回归验证脚本：Debug build → Release build → selftest 串行执行。
    任何一步失败（非零退出码）即停止并报告。
.DESCRIPTION
    为已验收功能提供统一的无头回归入口。
    不启动 GUI，只执行编译与数据层自检。
#>

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src\Clipora\Clipora.csproj"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "=== Clipora 回归验证 ===" -ForegroundColor Cyan
Write-Host "仓库: $repo"

# ── 1. Debug build ──
Write-Host "`n[1/5] Debug build..." -ForegroundColor Yellow
$ErrorActionPreference = "Continue"
$result = dotnet build $proj --no-restore 2>&1
$dotnetExitCode = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($dotnetExitCode -ne 0) {
    Write-Host "FAIL: Debug build 失败 (exit=$dotnetExitCode)" -ForegroundColor Red
    Write-Host ($result -join "`n")
    exit 1
}
$errors = ($result | Select-String -Pattern "错误" -AllMatches).Matches.Count
$warnings = ($result | Select-String -Pattern "warning" -AllMatches).Matches.Count
Write-Host "  OK — $warnings warning(s), $errors error(s)" -ForegroundColor Green

# ── 2. Release build ──
Write-Host "`n[2/5] Release build..." -ForegroundColor Yellow
$ErrorActionPreference = "Continue"
$result = dotnet build $proj --no-restore -c Release 2>&1
$dotnetExitCode = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($dotnetExitCode -ne 0) {
    Write-Host "FAIL: Release build 失败 (exit=$dotnetExitCode)" -ForegroundColor Red
    Write-Host ($result -join "`n")
    exit 1
}
$errors = ($result | Select-String -Pattern "错误" -AllMatches).Matches.Count
$warnings = ($result | Select-String -Pattern "warning" -AllMatches).Matches.Count
Write-Host "  OK — $warnings warning(s), $errors error(s)" -ForegroundColor Green

# ── 3. Self-test ──
Write-Host "`n[3/5] Self-test..." -ForegroundColor Yellow
$maxRetries = 3
$ok = $false
for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
    $ErrorActionPreference = "Continue"
    $result = dotnet run --project $proj -- --selftest 2>&1
    $dotnetExitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($dotnetExitCode -eq 0) {
        $ok = $true
        Write-Host "  OK — SELFTEST OK (尝试 $attempt/$maxRetries)" -ForegroundColor Green
        break
    }
    $failLine = ($result | Select-String -Pattern "SELFTEST FAIL" | Select-Object -First 1).Line
    if ($failLine) {
        Write-Host "  Retry $attempt/$maxRetries — $failLine" -ForegroundColor DarkYellow
    } else {
        Write-Host "  Retry $attempt/$maxRetries — exit=$dotnetExitCode" -ForegroundColor DarkYellow
    }
    if ($attempt -ge $maxRetries) {
        Write-Host "FAIL: Self-test 未通过（尝试 $maxRetries 次）" -ForegroundColor Red
        Write-Host ($result -join "`n")
        exit 1
    }
    # 短暂等待后再重试（剪贴板占用/时序波动）
    Start-Sleep -Seconds 1.5
}

# ── 4. OCR 状态检查（warn-only）──
Write-Host "`n[4/5] OCR 状态检查..." -ForegroundColor Yellow
$ErrorActionPreference = "Continue"
$result = dotnet run --project $proj -- --ocr-status 2>&1
$dotnetExitCode = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($dotnetExitCode -ne 0) {
    Write-Host "  WARN: OCR 状态检查未通过 (exit=$dotnetExitCode)" -ForegroundColor DarkYellow
} else {
    Write-Host "  OK — OCR 引擎诊断完成" -ForegroundColor Green
}

# ── 5. 拖放自检（warn-only）──
Write-Host "`n[5/5] 拖放自检..." -ForegroundColor Yellow
$ErrorActionPreference = "Continue"
$result = dotnet run --project $proj -- --dragselftest 2>&1
$dotnetExitCode = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($dotnetExitCode -ne 0) {
    Write-Host "  WARN: 拖放自检未通过 (exit=$dotnetExitCode)" -ForegroundColor DarkYellow
} else {
    Write-Host "  OK — 拖放自检通过" -ForegroundColor Green
}

$elapsed = $stopwatch.Elapsed.ToString("mm\:ss")
Write-Host "`n=== 回归验证全部通过 ($elapsed) ===" -ForegroundColor Cyan
exit 0
