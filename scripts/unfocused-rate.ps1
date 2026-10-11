#!/usr/bin/env pwsh
# unfocused-rate.ps1 - measures the position text update rate while the app is
# VISIBLE BUT UNFOCUSED (another window in front, ours still showing).
#
# The app samples its own PositionText on the UI thread, so we only have to
# arrange the state: bring our window forward, put a notepad in front of it
# (still partially uncovered so the app stays visible), and let the app measure.
#
# Usage: .\scripts\unfocused-rate.ps1 [-Seconds 10]
param(
    [int]$Seconds = 10
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BuildRoot = Join-Path $Repo 'winui\AmllTtmlTool.WinUI\bin\Debug\net8.0-windows10.0.19041.0'
$Exe = @(
    (Join-Path $BuildRoot 'win-x64\AmllTtmlTool.WinUI.exe'),
    (Join-Path $BuildRoot 'AmllTtmlTool.WinUI.exe')
) | Where-Object { Test-Path -LiteralPath $_ } |
Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Select-Object -Last 1
if (-not $Exe) { throw "no built app exe under $BuildRoot (build first)" }
Write-Host "EXE: $Exe ($((Get-Item -LiteralPath $Exe).LastWriteTimeUtc.ToString('u')))"

$report = Join-Path $env:TEMP 'focus-test-report.txt'
Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue
$stateFile = Join-Path $env:TEMP 'focus-test-state'
Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue

Get-Process -Name AmllTtmlTool.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
$proc = Start-Process -FilePath $Exe -ArgumentList '--focus-test', 'rate', "$Seconds", $report -PassThru
Write-Host "LAUNCH: pid=$($proc.Id) seconds=$Seconds"

# Wait for the app's window.
$w = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $w = (winapp ui list-windows --json | ConvertFrom-Json |
        Where-Object { $_.processId -eq $proc.Id -and $_.hwnd } | Select-Object -First 1)
    if ($w) { break }
}
if (-not $w) { throw "no app window for pid $($proc.Id)" }
$hwnd = [int]$w.hwnd
Write-Host "WINDOW: hwnd=$hwnd ($($w.width)x$($w.height))"

# Put a notepad in FRONT of the app but not covering it: the app must stay
# visible (that is the state under test), just not focused.
$np = Start-Process notepad -PassThru
Start-Sleep -Milliseconds 900
Set-Content -LiteralPath $stateFile -Value 'unfocused' -NoNewline
Write-Host "STATE: unfocused (notepad in front, app still visible)"

# Wait for the app to finish measuring.
$verdict = $null
$deadline = (Get-Date).AddSeconds($Seconds + 90)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path -LiteralPath $report) -and
        (Select-String -Path $report -Pattern 'unfocused \| ' -Quiet)) {
        $verdict = (Select-String -Path $report -Pattern 'unfocused \| (OK|FAIL)' | Select-Object -Last 1).Line.Trim()
        break
    }
    Start-Sleep -Milliseconds 500
}
Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue
Write-Host "--- report ---"
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
Write-Host "VERDICT: $verdict"
Get-Process -Name AmllTtmlTool.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
if ($verdict -notmatch 'OK') { exit 1 }
exit 0
