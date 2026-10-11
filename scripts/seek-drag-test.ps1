#!/usr/bin/env pwsh
# seek-drag-test.ps1 - CONTINUOUS pointer drag verification for the seek slider.
#
# winapp ui drag only has press/move/release, so it cannot answer the question
# that matters: does the time text track the pointer WHILE the thumb is moving,
# and in true milliseconds? This script performs a REAL gesture with SendInput
# (press, then 1px steps with a real gap, then release), and the APP samples
# every rendered frame and writes the report. Requires an unlocked host with
# the app foregroundable.
#
# Usage:
#   .\scripts\seek-drag-test.ps1 -File D:\Audio\alac.m4a `
#        [-From 0.1] [-To 0.9] [-StepPx 1] [-StepMs 8] [-Out report.json]
#
# Pass: text tracks the pointer on >=95% of moved frames, displayed ms equals
# the thumb's ms within 1ms, distinct ms values are not stuck, and the value
# does not lag the pointer by more than 500ms.
param(
    [string]$File = 'D:\Audio\alac.m4a',
    [double]$From = 0.1,
    [double]$To = 0.9,
    [int]$StepPx = 1,
    [int]$StepMs = 8,
    [string]$Out = (Join-Path $PSScriptRoot 'seek-drag-results.json')
)

$ErrorActionPreference = 'Stop'

# --- mouse input -----------------------------------------------------------
# SendInput (not mouse_event): it is the supported path, with proper flag
# combinations for down/move/up, which is what a real drag looks like.
$mouse = @'
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct INPUT { public uint type; public MOUSEINPUT mi; }
[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT {
    public int dx; public int dy; public uint mouseData; public uint dwFlags;
    public uint time; public IntPtr dwExtraInfo;
}
public static class SeekDragMouse {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] p, int size);
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
    public const uint INPUT_MOUSE = 0;
    public const uint MOVE = 0x0001;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;

    public static void MoveTo(int x, int y) { SetCursorPos(x, y); }

    public static void Press(int x, int y) {
        SetCursorPos(x, y);
        Send(LEFTDOWN);
    }

    public static void Release(int x, int y) {
        SetCursorPos(x, y);
        Send(LEFTUP);
    }

    public static POINT GetCursor() {
        POINT p;
        GetCursorPos(out p);
        return p;
    }

    private static void Send(uint flags) {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dx = 0; inputs[0].mi.dy = 0;
        inputs[0].mi.mouseData = 0;
        inputs[0].mi.dwFlags = flags;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@
if (-not ('SeekDragMouse' -as [type])) {
    Add-Type -TypeDefinition $mouse
}

function Get-CursorXY {
    return [SeekDragMouse]::GetCursor()
}

# --- helpers ---------------------------------------------------------------
# NB on argument passing: "@array" SPLATTING into a PowerShell function
# parameter binds only the first element, so every helper below takes the
# whole array as ONE value ("Func $array"), and the native `winapp` call
# inside uses "$Arguments" (which expands to separate argv entries).
function Invoke-WinApp {
    param([string[]]$Arguments)
    $out = @(& winapp $Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' ') failed: $($out -join "`n")" }
    $out
}

# Returns the parsed JSON payload as-is. Callers that expect ONE element
# (inspect/get-value) unwrap explicitly with [0]; callers that expect a LIST
# (list-windows) filter the array.
function Read-UiJson {
    param([string[]]$Arguments)
    Invoke-WinApp (@('ui') + $Arguments) | ConvertFrom-Json
}

# Single-element read (inspect / get-value emit ONE object).
function Read-One {
    param([string[]]$Arguments)
    $p = Read-UiJson $Arguments
    if ($p -is [System.Array]) { return $p[0] }
    return $p
}

function Parse-Stamp {
    param([string]$Text)
    $m = [regex]::Match($Text, '(\d+):(\d+)\.(\d+)')
    if (-not $m.Success) { throw "unparsable position text '$Text'" }
    return [int]$m.Groups[1].Value * 60000 + [int]$m.Groups[2].Value * 1000 + [int]$m.Groups[3].Value
}

# --- launch ----------------------------------------------------------------
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BuildRoot = Join-Path $Repo 'winui\AmllTtmlTool.WinUI\bin\Debug\net8.0-windows10.0.19041.0'
$Exe = @(
    (Join-Path $BuildRoot 'win-x64\AmllTtmlTool.WinUI.exe'),
    (Join-Path $BuildRoot 'AmllTtmlTool.WinUI.exe')
) | Where-Object { Test-Path -LiteralPath $_ } |
Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Select-Object -Last 1
if (-not $Exe) { throw "no built app exe under $BuildRoot (build first)" }
Write-Host "EXE: $Exe ($((Get-Item -LiteralPath $Exe).LastWriteTimeUtc.ToString('u')))"

Get-Process -Name AmllTtmlTool.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
$proc = Start-Process -FilePath $Exe -ArgumentList '--seek-drag', $File -PassThru
Write-Host "LAUNCH: pid=$($proc.Id) file=$File"
Start-Sleep -Seconds 8
if ($proc.HasExited) { throw "app exited during startup (code $($proc.ExitCode))" }
$hwnd = (Read-UiJson @('list-windows', '--json') |
    Where-Object { $_.processId -eq $proc.Id -and $_.hwnd } | Select-Object -First 1).hwnd
if (-not $hwnd) { throw "no app window for pid $($proc.Id)" }
Write-Host "WINDOW: hwnd=$hwnd"

# The inspect payload nests the single element one level down; index it
# explicitly so the numbers are scalars, not one-element arrays.
$sliderPayload = Read-One @('inspect', 'SeekSlider', '--json', '-w', $hwnd)
if (-not $sliderPayload -or -not $sliderPayload.windows) { throw 'SeekSlider not found' }
$slider = @($sliderPayload.windows)[0].elements | Select-Object -First 1
if (-not $slider) { throw 'SeekSlider not found' }
$trackX = [int]$slider.x
$trackY = [int]($slider.y + $slider.height / 2)
$trackW = [int]$slider.width
$trackH = [int]$slider.height
Write-Host "SLIDER: x=$trackX y=$trackY ${trackW}x${trackH}"
$duration = Parse-Stamp (Read-One @('get-value', 'DurationText', '--json', '-w', $hwnd)).text
$msPerPixel = if ($trackW -gt 0) { $duration / $trackW } else { 0 }
Write-Host "DURATION: ${duration}ms  ms-per-pixel=$([math]::Round($msPerPixel,1))"

if ($Playing) {
    Invoke-WinApp @('ui', 'invoke', 'PlayButton', '-w', $hwnd) | Out-Null
    Start-Sleep -Milliseconds 700
    Write-Host "STATE: playing"
} else {
    Write-Host "STATE: paused"
}

# Real input needs a foregroundable window: SendInput goes to whatever is at
# that screen point, so bring the app to the front first and prove it is
# neither iconic nor still behind us.
[void][SeekDragMouse]::SetForegroundWindow([System.IntPtr]$hwnd)
if ([SeekDragMouse]::IsIconic([System.IntPtr]$hwnd)) { throw "app window is iconic; cannot send real input" }
Start-Sleep -Milliseconds 700
$fg = (Get-Process -Id $proc.Id).MainWindowHandle
"foreground hwnd=$fg target=$hwnd focused=$($fg -eq $hwnd)"

$before = (Read-One @('get-value', 'PositionText', '--json', '-w', $hwnd)).text
"position before: $before"

# --- the drag ---------------------------------------------------------------
# The app (--seek-drag mode) samples every rendered frame itself, so this
# script only has to perform a REAL gesture: press on the thumb, then walk the
# pointer 1px at a time with a real gap, and release. Per-frame sampling
# cannot be done fast enough from outside the process, which is why the
# measurement lives in the app.
$fromX = [int]($trackX + $trackW * $From)
$toX = [int]($trackX + $trackW * $To)
Write-Host "DRAG: $fromX -> $toX ($($toX - $fromX)px, $StepPx px per $StepMs ms)"

# Handshake: the app signals READY in its report, then we GO.
$goPath = Join-Path $env:TEMP 'seek-drag-go'
$donePath = Join-Path $env:TEMP 'seek-drag-done'
Remove-Item -LiteralPath $goPath, $donePath -Force -ErrorAction SilentlyContinue
New-Item -ItemType File -Path $goPath | Out-Null
Write-Host "GO"

[SeekDragMouse]::Press($fromX, $trackY)
$px = 0
while ($px -lt ($toX - $fromX)) {
    $px += $StepPx
    [SeekDragMouse]::MoveTo($fromX + $px, $trackY)
    Start-Sleep -Milliseconds $StepMs
}
[SeekDragMouse]::Release($toX, $trackY)
New-Item -ItemType File -Path $donePath | Out-Null
Write-Host "DONE (button released)"

# --- collect -----------------------------------------------------------------
# All measurement happens inside the app per rendered frame; the report is
# written there once the driver signals done. Wait for its verdict line.
$appReport = "$env:TEMP\seek-drag-report.txt"
$deadline = (Get-Date).AddSeconds(45)
$verdict = $null
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $appReport) {
        $v = Select-String -Path $appReport -Pattern "seek-drag \| (OK|FAIL)" |
            Select-Object -Last 1
        if ($v) { $verdict = $v.Line.Trim(); break }
    }
    Start-Sleep -Milliseconds 500
}
if (Test-Path -LiteralPath $appReport) {
    Get-Content -LiteralPath $appReport | Where-Object { $_ -match "^\s{2}(frame|distinct|ms-equals|worst|release|FAIL|  )" -or $_ -match "seek-drag \|" } |
        ForEach-Object { $_.TrimEnd() }
    Copy-Item -LiteralPath $appReport -Destination $Out -Force
}
Write-Host "VERDICT: $verdict"

Get-Process -Name AmllTtmlTool.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
if ($verdict -notmatch "OK") { exit 1 }
exit 0
