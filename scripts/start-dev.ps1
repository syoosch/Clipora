<#
.SYNOPSIS
    Starts Clipora with an isolated development data directory.
.DESCRIPTION
    Resolves the repository root, optionally builds the project, refuses to
    start while another Clipora process exists, and starts the Debug executable
    with CLIPORA_DATA_DIR set to <repo>\.dev-data for the child process only.
.PARAMETER Build
    Build the project before starting Clipora.
#>

param(
    [switch] $Build
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "Repository root: $repoRoot"

if ($Build) {
    Write-Host 'Building Clipora...'
    dotnet build (Join-Path $repoRoot 'src\Clipora') --no-restore
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine("Build failed with exit code $LASTEXITCODE.")
        exit $LASTEXITCODE
    }
    Write-Host 'Build succeeded.'
}

$existing = Get-Process -Name Clipora -ErrorAction SilentlyContinue
if ($existing) {
    $ids = ($existing | ForEach-Object { $_.Id }) -join ', '
    [Console]::Error.WriteLine("Clipora is already running (PID: $ids). Exit every Clipora instance before starting the development build.")
    exit 2
}

$devDataRoot = Join-Path $repoRoot '.dev-data'
$exe = Join-Path $repoRoot 'src\Clipora\bin\Debug\net10.0-windows10.0.19041.0\Clipora.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    [Console]::Error.WriteLine("Debug executable not found: $exe")
    exit 3
}

Write-Host "Development data root: $devDataRoot"
$previousDataRoot = $env:CLIPORA_DATA_DIR
try {
    $env:CLIPORA_DATA_DIR = $devDataRoot
    $process = Start-Process -FilePath $exe -PassThru
}
finally {
    if ($null -eq $previousDataRoot) {
        Remove-Item Env:CLIPORA_DATA_DIR -ErrorAction SilentlyContinue
    }
    else {
        $env:CLIPORA_DATA_DIR = $previousDataRoot
    }
}

Write-Host "Started Clipora development build (PID: $($process.Id))."
