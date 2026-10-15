#!/usr/bin/env pwsh
# scripts/check-input-env.ps1 — preconditions for scripts/input.ps1 real-input runs.
# Fails with a named missing piece; never touches the host (VM-local only).
# Checks: (1) session id isn't 0, (2) desktop unlocked, (3) screenshot isn't black.
param()
$ErrorActionPreference = 'Stop'
$fail = 0
function Say([string]$name, [bool]$ok, [string]$detail) {
    $s = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host "$s $name $detail"
    if (-not $ok) { $script:fail++ }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InputEnvNative {
    [DllImport("kernel32.dll")] public static extern int WTS_CURRENT_SERVER_HANDLE();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
}
'@
# 1. Session id: current process session must not be 0 (services session).
$procId = [InputEnvNative]::GetCurrentProcessId()
$sessionId = -1
try {
    $line = (query session 2>$null | Select-String "^\s*>") | Select-Object -First 1
    if ($line -match '\s+(\d+)\s+(Active|Disc)') { $sessionId = [int]$Matches[1] }
} catch { }
Say "session-not-zero" ($sessionId -gt 0) "sessionId=$sessionId (need >0, console/interactive)"
# 2. Desktop unlocked: foreground window exists AND input desktop opens.
$fg = [InputEnvNative]::GetForegroundWindow()
$hDesk = [InputEnvNative]::OpenInputDesktop(0, $false, 0x0100) # DESKTOP_READOBJECTS
$deskOk = $hDesk -ne [IntPtr]::Zero
if ($deskOk) { [void][InputEnvNative]::CloseDesktop($hDesk) }
Say "desktop-unlocked" ($fg -ne [IntPtr]::Zero -and $deskOk) "fg=0x$($fg.ToString('x')) inputDesktop=$deskOk"
# 3. Screenshot isn't black: capture primary screen, require brightness variance.
Add-Type -AssemblyName System.Drawing
$black = $true
$detail = ""
try {
    $w = [InputEnvNative]::GetSystemMetrics(0)
    $h = [InputEnvNative]::GetSystemMetrics(1)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $g.Dispose()
    $sum = 0L; $sumSq = 0L; $n = 0
    for ($x = 0; $x -lt $w; $x += 37) {
        for ($y = 0; $y -lt $h; $y += 37) {
            $c = $bmp.GetPixel($x, $y)
            $v = [int]$c.R + [int]$c.G + [int]$c.B
            $sum += $v; $sumSq += [long]$v * $v; $n++
        }
    }
    $bmp.Dispose()
    $mean = $sum / $n
    $var = $sumSq / $n - $mean * $mean
    $black = -not ($mean -gt 12 -and $var -gt 25)
    $detail = "mean=$([math]::Round($mean,1)) var=$([math]::Round($var,1)) ${w}x${h}"
} catch {
    $detail = "capture failed: $_"
}
Say "screenshot-not-black" (-not $black) $detail
if ($fail -gt 0) { Write-Host "check-input-env | FAIL $fail"; exit 1 }
Write-Host "check-input-env | OK"
exit 0
