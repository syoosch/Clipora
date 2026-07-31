<#
.SYNOPSIS
    Measures the current Clipora history-window behavior under Fluent and LiquidGlass.
.DESCRIPTION
    This Debug-only benchmark uses synthetic data under %TEMP%. It never reads the development
    or production database, refuses to run beside another Clipora process, and exercises only the
    current production scrolling implementation. The visible window is required for WPF/DWM data.
#>

param(
    [ValidateSet('Fluent', 'LiquidGlass')]
    [string[]] $Themes = @('Fluent', 'LiquidGlass'),
    [ValidateSet('Text', 'Mixed')]
    [string[]] $Profiles = @('Text', 'Mixed'),
    [ValidateRange(1, 10)]
    [int] $Repetitions = 3,
    [ValidateRange(1, 500)]
    [int] $WheelCount = 60,
    [ValidateRange(1, 1000)]
    [int] $WheelIntervalMs = 100,
    [ValidateRange(100, 10000)]
    [int] $ItemCount = 1000,
    [string] $OutputRoot,
    [switch] $NoBuild,
    [switch] $Quick,
    [switch] $SmokeTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src\Clipora'
$exe = Join-Path $project 'bin\Debug\net10.0-windows10.0.19041.0\Clipora.exe'

if ($SmokeTest) {
    $Quick = $true
}
if ($Quick) {
    if (-not $PSBoundParameters.ContainsKey('Themes')) { $Themes = @('Fluent', 'LiquidGlass') }
    if (-not $PSBoundParameters.ContainsKey('Profiles')) { $Profiles = @('Text') }
    if (-not $PSBoundParameters.ContainsKey('Repetitions')) { $Repetitions = 1 }
    if (-not $PSBoundParameters.ContainsKey('WheelCount')) { $WheelCount = 6 }
    if (-not $PSBoundParameters.ContainsKey('WheelIntervalMs')) { $WheelIntervalMs = 40 }
    if (-not $PSBoundParameters.ContainsKey('ItemCount')) { $ItemCount = 300 }
}

if (-not $NoBuild) {
    Write-Host 'Building Clipora Debug performance baseline...'
    & dotnet build $project --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Debug build failed with exit code $LASTEXITCODE."
    }
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Debug executable not found: $exe"
}

$existing = Get-Process -Name Clipora -ErrorAction SilentlyContinue
if ($existing) {
    $ids = ($existing | ForEach-Object { $_.Id }) -join ', '
    throw "Clipora is already running (PID: $ids). Exit it before benchmarking."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) "clipora-performance-baseline\$stamp"
}
else {
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}
$rawRoot = Join-Path $OutputRoot 'raw'
New-Item -ItemType Directory -Path $rawRoot -Force | Out-Null

if (-not ('CliporaBenchmarkWindowControl' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CliporaBenchmarkWindowControl
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
}
'@
}

function Get-Median {
    param([object[]] $Values)

    $sorted = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return 0d }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2d
}

function Get-Average {
    param([System.Collections.Generic.List[double]] $Values)

    if ($null -eq $Values -or $Values.Count -eq 0) { return 0d }
    return [double](($Values | Measure-Object -Average).Average)
}

function Get-Maximum {
    param([System.Collections.Generic.List[double]] $Values)

    if ($null -eq $Values -or $Values.Count -eq 0) { return 0d }
    return [double](($Values | Measure-Object -Maximum).Maximum)
}

function New-GpuCounters {
    param([int] $ProcessId)

    try {
        $paths = (Get-Counter -ListSet 'GPU Engine').PathsWithInstances
        $instances = @(
            $paths |
                Where-Object { $_ -match "pid_$($ProcessId)_" -and $_ -match '\\Utilization Percentage$' } |
                ForEach-Object {
                    if ($_ -match 'GPU Engine\((?<instance>.+)\)\\Utilization Percentage$') {
                        $Matches.instance
                    }
                } |
                Sort-Object -Unique
        )
        $counters = New-Object System.Collections.Generic.List[System.Diagnostics.PerformanceCounter]
        foreach ($instance in $instances) {
            try {
                $counter = [System.Diagnostics.PerformanceCounter]::new(
                    'GPU Engine',
                    'Utilization Percentage',
                    $instance,
                    $true)
                [void]$counter.NextValue()
                $counters.Add($counter)
            }
            catch {
                # One engine may disappear between discovery and counter construction.
            }
        }
        return $counters
    }
    catch {
        return (New-Object System.Collections.Generic.List[System.Diagnostics.PerformanceCounter])
    }
}

function Read-GpuSum {
    param([System.Collections.Generic.List[System.Diagnostics.PerformanceCounter]] $Counters)

    $sum = 0d
    foreach ($counter in $Counters) {
        try { $sum += [Math]::Max(0d, [double]$counter.NextValue()) } catch { }
    }
    return $sum
}

function Dispose-Counters {
    param([System.Collections.IEnumerable] $Counters)

    if ($null -eq $Counters) { return }
    foreach ($counter in $Counters) {
        try { $counter.Dispose() } catch { }
    }
}

$results = New-Object System.Collections.Generic.List[object]
$tempPath = [System.IO.Path]::GetTempPath()
$preexistingBenchmarkRoots = @(
    Get-ChildItem -LiteralPath $tempPath -Directory -Filter 'clipora-performance-baseline-*' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
)
$originalDataRoot = [Environment]::GetEnvironmentVariable('CLIPORA_DATA_DIR', 'Process')
$smokeSentinel = $null

try {
    if ($SmokeTest) {
        $smokeSentinel = Join-Path $tempPath "clipora-performance-forbidden-$([Guid]::NewGuid().ToString('N')).sentinel"
        Set-Content -LiteralPath $smokeSentinel -Value 'must remain untouched' -Encoding ASCII
        # If early argument routing fails, the Debug data-root guard will reject this file path.
        [Environment]::SetEnvironmentVariable('CLIPORA_DATA_DIR', $smokeSentinel, 'Process')
    }

    foreach ($profile in $Profiles) {
        foreach ($theme in $Themes) {
            for ($run = 1; $run -le $Repetitions; $run++) {
                $rawPath = Join-Path $rawRoot "$theme-$profile-$run.json"
                $readyMarker = "$rawPath.ready"
                $measuringMarker = "$rawPath.measuring"
                $argumentLine = @(
                    '--performance-baseline'
                    "--profile=$profile"
                    "--theme=$theme"
                    "--wheel-count=$WheelCount"
                    "--wheel-interval-ms=$WheelIntervalMs"
                    "--item-count=$ItemCount"
                    "--output=`"$rawPath`""
                ) -join ' '

                Write-Host "[$theme/$profile] run $run/$Repetitions"
                $process = $null
                $appGpuCounters = $null
                $dwmGpuCounters = $null
                $dwmCpuCounter = $null
                try {
                    $process = Start-Process -FilePath $exe -ArgumentList $argumentLine -PassThru -WindowStyle Normal

                    $readyDeadline = [DateTime]::UtcNow.AddSeconds(60)
                    while (-not (Test-Path -LiteralPath $readyMarker) -and -not $process.HasExited) {
                        if ([DateTime]::UtcNow -gt $readyDeadline) {
                            throw 'Benchmark did not create its ready marker within 60 seconds.'
                        }
                        Start-Sleep -Milliseconds 50
                        $process.Refresh()
                    }
                    if ($process.HasExited) {
                        throw "Benchmark exited before its ready phase (exit $($process.ExitCode))."
                    }

                    $windowHandle = [Int64](Get-Content -LiteralPath $readyMarker -Raw -Encoding ASCII)
                    $rect = New-Object CliporaBenchmarkWindowControl+Rect
                    $focusSucceeded = [CliporaBenchmarkWindowControl]::SetForegroundWindow([IntPtr]$windowHandle)
                    if ([CliporaBenchmarkWindowControl]::GetWindowRect([IntPtr]$windowHandle, [ref]$rect)) {
                        $cursorX = [int](($rect.Left + $rect.Right) / 2)
                        $cursorY = [int](($rect.Top + $rect.Bottom) / 2)
                        [void][CliporaBenchmarkWindowControl]::SetCursorPos($cursorX, $cursorY)
                    }

                    # Prepare external counters before measurement so discovery time is excluded.
                    $dwm = Get-Process -Name dwm -ErrorAction SilentlyContinue | Select-Object -First 1
                    $appGpuCounters = New-GpuCounters -ProcessId $process.Id
                    $dwmGpuCounters = if ($null -ne $dwm) {
                        New-GpuCounters -ProcessId $dwm.Id
                    }
                    else {
                        New-Object System.Collections.Generic.List[System.Diagnostics.PerformanceCounter]
                    }
                    if ($null -ne $dwm) {
                        try {
                            $dwmCpuCounter = [System.Diagnostics.PerformanceCounter]::new(
                                'Process',
                                '% Processor Time',
                                'dwm',
                                $true)
                            [void]$dwmCpuCounter.NextValue()
                        }
                        catch {
                            $dwmCpuCounter = $null
                        }
                    }

                    $measureDeadline = [DateTime]::UtcNow.AddSeconds(10)
                    while (-not (Test-Path -LiteralPath $measuringMarker) -and -not $process.HasExited) {
                        if ([DateTime]::UtcNow -gt $measureDeadline) {
                            throw 'Benchmark did not enter its measurement phase within 10 seconds.'
                        }
                        Start-Sleep -Milliseconds 20
                        $process.Refresh()
                    }

                    $appGpuSamples = New-Object System.Collections.Generic.List[double]
                    $dwmGpuSamples = New-Object System.Collections.Generic.List[double]
                    $dwmCpuSamples = New-Object System.Collections.Generic.List[double]
                    while (-not $process.HasExited -and (Test-Path -LiteralPath $measuringMarker)) {
                        Start-Sleep -Milliseconds 200
                        $process.Refresh()
                        $appGpuSamples.Add((Read-GpuSum $appGpuCounters))
                        $dwmGpuSamples.Add((Read-GpuSum $dwmGpuCounters))
                        if ($null -ne $dwmCpuCounter) {
                            try { $dwmCpuSamples.Add([double]$dwmCpuCounter.NextValue()) } catch { }
                        }
                    }

                    $process.WaitForExit()
                    if ($process.ExitCode -ne 0) {
                        throw "Benchmark failed for $theme/$profile run $run (exit $($process.ExitCode))."
                    }
                    if (-not (Test-Path -LiteralPath $rawPath)) {
                        throw "Benchmark did not produce: $rawPath"
                    }

                    $result = Get-Content -LiteralPath $rawPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    if (-not $result.Succeeded) {
                        throw "Benchmark reported failure: $($result.ErrorCode)"
                    }
                    $result | Add-Member -NotePropertyName Run -NotePropertyValue $run
                    $result | Add-Member -NotePropertyName DriverFocusSucceeded -NotePropertyValue $focusSucceeded
                    $result | Add-Member -NotePropertyName CliporaGpuAveragePercent -NotePropertyValue (Get-Average $appGpuSamples)
                    $result | Add-Member -NotePropertyName CliporaGpuPeakPercent -NotePropertyValue (Get-Maximum $appGpuSamples)
                    $result | Add-Member -NotePropertyName DwmGpuAveragePercent -NotePropertyValue (Get-Average $dwmGpuSamples)
                    $result | Add-Member -NotePropertyName DwmGpuPeakPercent -NotePropertyValue (Get-Maximum $dwmGpuSamples)
                    $result | Add-Member -NotePropertyName DwmCpuNormalizedAveragePercent -NotePropertyValue (
                        (Get-Average $dwmCpuSamples) / [Environment]::ProcessorCount)
                    $result | Add-Member -NotePropertyName ExternalSampleCount -NotePropertyValue $appGpuSamples.Count
                    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $rawPath -Encoding UTF8
                    $results.Add($result)
                }
                finally {
                    Dispose-Counters $appGpuCounters
                    Dispose-Counters $dwmGpuCounters
                    if ($null -ne $dwmCpuCounter) {
                        try { $dwmCpuCounter.Dispose() } catch { }
                    }
                    if ($null -ne $process -and -not $process.HasExited) {
                        try { $process.Kill() } catch { }
                    }
                }
            }
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('CLIPORA_DATA_DIR', $originalDataRoot, 'Process')
}

$aggregates = New-Object System.Collections.Generic.List[object]
foreach ($group in ($results | Group-Object Theme, Profile)) {
    $first = $group.Group[0]
    $aggregates.Add([pscustomobject]@{
        Theme = $first.Theme
        Profile = $first.Profile
        Runs = $group.Count
        ForegroundRuns = @($group.Group | Where-Object { $_.ForegroundAtStart -and $_.ForegroundAtEnd }).Count
        ActiveWindowRuns = @($group.Group | Where-Object { $_.WindowActiveAtStart -and $_.WindowActiveAtEnd }).Count
        CpuMsPer1000Px = [Math]::Round((Get-Median @($group.Group.CpuMsPer1000Px)), 3)
        NormalizedCpuPercent = [Math]::Round((Get-Median @($group.Group.NormalizedCpuPercent)), 3)
        FrameP50Ms = [Math]::Round((Get-Median @($group.Group.FrameP50Ms)), 3)
        FrameP95Ms = [Math]::Round((Get-Median @($group.Group.FrameP95Ms)), 3)
        FrameP99Ms = [Math]::Round((Get-Median @($group.Group.FrameP99Ms)), 3)
        FramesOver16_7Ms = [Math]::Round((Get-Median @($group.Group.FramesOver16_7Ms)), 1)
        FramesOver33Ms = [Math]::Round((Get-Median @($group.Group.FramesOver33Ms)), 1)
        DispatcherP95Ms = [Math]::Round((Get-Median @($group.Group.DispatcherHeartbeatP95Ms)), 3)
        DispatcherP99Ms = [Math]::Round((Get-Median @($group.Group.DispatcherHeartbeatP99Ms)), 3)
        StopTailMs = [Math]::Round((Get-Median @($group.Group.StopTailMs)), 3)
        ScrollChangedCount = [Math]::Round((Get-Median @($group.Group.ScrollChangedCount)), 1)
        AbsoluteScrollDistance = [Math]::Round((Get-Median @($group.Group.AbsoluteScrollDistance)), 1)
        FinalOffset = [Math]::Round((Get-Median @($group.Group.FinalOffset)), 1)
        ThumbnailLoads = [Math]::Round((Get-Median @($group.Group.ThumbnailLoads)), 1)
        AllocatedMb = [Math]::Round((Get-Median @($group.Group.AllocatedBytes)) / 1MB, 2)
        CliporaGpuAveragePercent = [Math]::Round((Get-Median @($group.Group.CliporaGpuAveragePercent)), 3)
        CliporaGpuPeakPercent = [Math]::Round((Get-Median @($group.Group.CliporaGpuPeakPercent)), 3)
        DwmCpuNormalizedAveragePercent = [Math]::Round((Get-Median @($group.Group.DwmCpuNormalizedAveragePercent)), 3)
        DwmGpuAveragePercent = [Math]::Round((Get-Median @($group.Group.DwmGpuAveragePercent)), 3)
        DwmGpuPeakPercent = [Math]::Round((Get-Median @($group.Group.DwmGpuPeakPercent)), 3)
        ExternalSampleCount = [Math]::Round((Get-Median @($group.Group.ExternalSampleCount)), 1)
    })
}

$commit = (& git -C $repoRoot rev-parse --short HEAD 2>$null).Trim()
$summary = [pscustomobject]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    Commit = $commit
    Version = if ($results.Count -gt 0) { $results[0].Version } else { '' }
    LogicalProcessorCount = [Environment]::ProcessorCount
    WheelCountPerDirection = $WheelCount
    WheelIntervalMs = $WheelIntervalMs
    ItemCount = $ItemCount
    Aggregates = $aggregates
}

$summaryJson = Join-Path $OutputRoot 'summary.json'
$summaryCsv = Join-Path $OutputRoot 'summary.csv'
$summaryMarkdown = Join-Path $OutputRoot 'summary.md'
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryJson -Encoding UTF8
$aggregates | Export-Csv -LiteralPath $summaryCsv -NoTypeInformation -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Clipora current-behavior performance baseline')
$lines.Add('')
$lines.Add("- Commit: $commit")
$lines.Add("- Version: $($summary.Version)")
$lines.Add("- Input: $WheelCount down + $WheelCount up, $WheelIntervalMs ms interval")
$lines.Add("- Synthetic items: $ItemCount")
$lines.Add("- Logical processors: $($summary.LogicalProcessorCount)")
$lines.Add('')
$lines.Add('| Theme | Profile | CPU ms/1000px | CPU % | Frame P50/P95/P99 ms | >16.7/>33 ms | Dispatcher P95/P99 ms | Stop tail ms | Distance |')
$lines.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($aggregate in ($aggregates | Sort-Object Profile, Theme)) {
    $lines.Add("| $($aggregate.Theme) | $($aggregate.Profile) | $($aggregate.CpuMsPer1000Px) | $($aggregate.NormalizedCpuPercent) | $($aggregate.FrameP50Ms)/$($aggregate.FrameP95Ms)/$($aggregate.FrameP99Ms) | $($aggregate.FramesOver16_7Ms)/$($aggregate.FramesOver33Ms) | $($aggregate.DispatcherP95Ms)/$($aggregate.DispatcherP99Ms) | $($aggregate.StopTailMs) | $($aggregate.AbsoluteScrollDistance) |")
}
$lines.Add('')
$lines.Add('| Theme | Profile | Clipora GPU avg/peak % | DWM CPU % | DWM GPU avg/peak % | Alloc MB | Thumbnails | Foreground/active runs | External samples |')
$lines.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($aggregate in ($aggregates | Sort-Object Profile, Theme)) {
    $lines.Add("| $($aggregate.Theme) | $($aggregate.Profile) | $($aggregate.CliporaGpuAveragePercent)/$($aggregate.CliporaGpuPeakPercent) | $($aggregate.DwmCpuNormalizedAveragePercent) | $($aggregate.DwmGpuAveragePercent)/$($aggregate.DwmGpuPeakPercent) | $($aggregate.AllocatedMb) | $($aggregate.ThumbnailLoads) | $($aggregate.ForegroundRuns)/$($aggregate.ActiveWindowRuns)/$($aggregate.Runs) | $($aggregate.ExternalSampleCount) |")
}
$lines | Set-Content -LiteralPath $summaryMarkdown -Encoding UTF8

if ($SmokeTest) {
    $expectedRawCount = $Themes.Count * $Profiles.Count * $Repetitions
    $actualRawCount = @(Get-ChildItem -LiteralPath $rawRoot -File -Filter '*.json').Count
    if ($actualRawCount -ne $expectedRawCount -or
        -not (Test-Path -LiteralPath $summaryJson) -or
        -not (Test-Path -LiteralPath $summaryCsv) -or
        -not (Test-Path -LiteralPath $summaryMarkdown)) {
        throw 'Smoke test did not produce all raw and summary files.'
    }
    if ((Get-Content -LiteralPath $smokeSentinel -Raw -Encoding ASCII).Trim() -ne 'must remain untouched') {
        throw 'Smoke test data-root sentinel was modified.'
    }
    $newBenchmarkRoots = @(
        Get-ChildItem -LiteralPath $tempPath -Directory -Filter 'clipora-performance-baseline-*' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notin $preexistingBenchmarkRoots }
    )
    if ($newBenchmarkRoots.Count -ne 0) {
        throw 'Smoke test left a synthetic benchmark data directory behind.'
    }
    Remove-Item -LiteralPath $smokeSentinel -Force
    Write-Host 'Smoke test passed: isolated root, early routing, result files, cleanup, and exit code.'
}

Write-Host ''
Write-Host "Benchmark complete: $summaryMarkdown"
$aggregates |
    Sort-Object Profile, Theme |
    Format-Table Theme,Profile,CpuMsPer1000Px,NormalizedCpuPercent,FrameP95Ms,StopTailMs,CliporaGpuAveragePercent,DwmCpuNormalizedAveragePercent,DwmGpuAveragePercent -AutoSize
