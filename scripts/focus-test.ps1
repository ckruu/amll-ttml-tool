#!/usr/bin/env pwsh
# focus-test.ps1 - drives --focus-test through the four window states and
# reports how often the app's per-frame paint path actually runs in each.
#
# The app samples CompositionTarget.Rendering callbacks ONLY (no painting),
# and writes a READY <state> line; this script then arranges that state and
# flags it with a marker file.
#
# States:
#   focused   - our app is foreground (done via winapp ui focus)
#   unfocused - a notepad is foreground, our app still fully visible beside it
#   covered   - a notepad is moved over our app, fully covering it
#   minimized - our app is iconic
#
# Usage: .\scripts\focus-test.ps1   (no args)
param(
    [string]$Report = (Join-Path $PSScriptRoot 'focus-test-report.txt')
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
Write-Host "EXE: $Exe"

Remove-Item -LiteralPath $Report -Force -ErrorAction SilentlyContinue
$stateFile = Join-Path $env:TEMP 'focus-test-state'
Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue

Get-Process -Name AmllTtmlTool.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
$proc = Start-Process -FilePath $Exe -ArgumentList '--focus-test', $Report -PassThru
Write-Host "LAUNCH: pid=$($proc.Id) report=$Report"
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')

# Wait for the app's window handle through the UI tree.
$w = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $w = (winapp ui list-windows --json | ConvertFrom-Json |
        Where-Object { $_.processId -eq $proc.Id -and $_.hwnd } | Select-Object -First 1)
    if ($w) { break }
}
if (-not $w) { throw "no app window for pid $($proc.Id)" }
$hwnd = $w.hwnd
Write-Host "WINDOW: hwnd=$hwnd ($($w.width)x$($w.height))"

if (-not ('W32' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll", SetLastError=true)] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int cx, int cy, bool repaint);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@
}

function Wait-Ready {
    param([string]$State)
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Report) {
            $line = Select-String -Path $Report -Pattern "READY $State" | Select-Object -Last 1
            if ($line) { return $true }
        }
        Start-Sleep -Milliseconds 300
    }
    Write-Host "TIMEOUT waiting for $State"
    return $false
}

function Signal { param([string]$State) Set-Content -LiteralPath $stateFile -Value $State -NoNewline }

foreach ($state in 'focused', 'unfocused', 'covered', 'minimized') {
    if (-not (Wait-Ready $state)) { continue }
    switch ($state) {
        'focused' {
            winapp ui focus SeekSlider -w $hwnd | Out-Null
            Write-Host "STATE: focused"
        }
        'unfocused' {
            # Notepad foreground, our app still fully visible beside it.
            $np = Start-Process notepad -PassThru
            Start-Sleep -Milliseconds 700
            $null = $np
            Write-Host "STATE: unfocused (notepad in front)"
            $script:notepad = $np
        }
        'covered' {
            if ($script:notepad) {
                [void][W32]::MoveWindow([System.IntPtr]$script:notepad.MainWindowHandle, 100, 100, 1500, 800, $true)
                Start-Sleep -Milliseconds 500
            }
            Write-Host "STATE: covered (notepad moved over the app)"
        }
        'minimized' {
            if ($script:notepad) { Stop-Process -Id $script:notepad.Id -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Milliseconds 300
            [void][W32]::ShowWindow([System.IntPtr]$hwnd, 6)   # SW_MINIMIZE
            Start-Sleep -Milliseconds 300
            Write-Host "STATE: minimized"
        }
    }
    Signal $state
    Start-Sleep -Seconds 4
    if ($state -eq 'minimized') { [void][W32]::ShowWindow([System.IntPtr]$hwnd, 9) }
}

# Tear down.
Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 20; $i++) {
    if ((Test-Path -LiteralPath $Report) -and (Select-String -Path $Report -Pattern 'focus \| ' -Quiet)) { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "--- report ---"
if (Test-Path -LiteralPath $Report) {
    Get-Content -LiteralPath $Report
} else {
    Write-Host "no report at $Report"
}

Get-Process -Name AmllTtmlTool.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
$verdict = (Select-String -Path $Report -Pattern 'focus \| (OK|FAIL)' | Select-Object -Last 1).Line
if ($verdict -notmatch 'OK') { exit 1 }
exit 0
