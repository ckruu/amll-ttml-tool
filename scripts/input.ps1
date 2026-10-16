#!/usr/bin/env pwsh
# scripts/input.ps1 — real injected input for VM-local verification.
# user32 SendInput only (no winapp dependency); screenshots via PrintWindow
# with a CopyFromScreen fallback. VM interactive session only: refuses to
# run in session 0 (services/host safety).
#
# Verbs:
#   focus -Hwnd 0x...                 bring window to foreground
#   move -X <px> -Y <px> [-Steps N]   absolute move, interpolated
#   click -X -Y [-Button left|right|middle] [-Steps N]
#   doubleclick -X -Y [-Steps N]
#   down -X -Y [-Button left] | up [-Button left]   (for drags with holds)
#   drag -X1 -Y1 -X2 -Y2 [-Steps N]   move + down + path + up
#   wheel -Delta <clicks*120>         vertical wheel at current pos
#   key -Keys "ctrl+shift+a"          chord: modifiers down, key tap, up
#   type -Text "..."                  unicode per-char SendInput
#   shot -Hwnd 0x... -Out <png>       window screenshot (real pixels)
#   rect -Hwnd 0x...                  print window rect (l,t,r,b, w,h)
#   uia-rect -Hwnd 0x... -AutoId <id> screen rect of AutomationId element
#   keydn -Keys "ctrl" | keyup -Keys "ctrl"   hold/release modifiers
param(
    [Parameter(Position = 0)][string]$Verb = "help",
    [string]$Hwnd = "0",
    [int]$X = -1, [int]$Y = -1,
    [int]$X1 = -1, [int]$Y1 = -1, [int]$X2 = -1, [int]$Y2 = -1,
    [int]$Steps = 24,
    [string]$Button = "left",
    [int]$Delta = 120,
    [string]$Keys = "",
    [string]$Text = "",
    [string]$Out = "",
    [string]$AutoId = "",
    [int]$SettleMs = 30
)
$ErrorActionPreference = 'Stop'
# NOTE: all INPUT structs are built inside C# helpers below. PowerShell
# CANNOT write nested struct fields ($k.u.ki.wVk = x silently writes a
# temporary copy — proven by byte dump: every field arrived zero). Plain
# ints cross the boundary; C# does exact struct assignment.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RealInput {
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx; public int dy; public int mouseData; public int dwFlags;
        public int time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public short wVk; public short wScan; public int dwFlags; public int time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT {
        public int type; public INPUTUNION u;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    private static int InSize() { return Marshal.SizeOf(typeof(INPUT)); }
    public static uint SendMouseMoveAbs(int nx, int ny) {
        var i = new INPUT(); i.type = 0;
        i.u.mi.dx = nx; i.u.mi.dy = ny;
        i.u.mi.dwFlags = 0x0001 | 0x8000;
        return SendInput(1, new INPUT[] { i }, InSize());
    }
    public static uint SendKey(int vk, int scan, int flags) {
        var i = new INPUT(); i.type = 1;
        i.u.ki.wVk = (short)vk; i.u.ki.wScan = (short)scan; i.u.ki.dwFlags = flags;
        return SendInput(1, new INPUT[] { i }, InSize());
    }
    public static uint SendWheel(int delta) {
        var i = new INPUT(); i.type = 0;
        i.u.mi.mouseData = delta; i.u.mi.dwFlags = 0x0800;
        return SendInput(1, new INPUT[] { i }, InSize());
    }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L; public int T; public int R; public int B; }
}
'@
Add-Type -AssemblyName System.Drawing
function Guard-Session {
    $sid = -1
    try {
        $line = (query session 2>$null | Select-String "^\s*>") | Select-Object -First 1
        if ($line -match '\s+(\d+)\s+(Active|Disc)') { $sid = [int]$Matches[1] }
    } catch { }
    if ($sid -le 0) { throw "input.ps1 refuses session $sid (host safety: need interactive session > 0)" }
}
# Virtual-screen-normalized absolute coords (multi-monitor safe).
function Norm-XY([int]$px, [int]$py) {
    $vx = [RealInput]::GetSystemMetrics(76); $vy = [RealInput]::GetSystemMetrics(77)
    $vw = [RealInput]::GetSystemMetrics(78); $vh = [RealInput]::GetSystemMetrics(79)
    return @([int](($px - $vx) * 65535.0 / $vw), [int](($py - $vy) * 65535.0 / $vh))
}
function Send-MouseAbs([int]$nx, [int]$ny) {
    $n = [RealInput]::SendMouseMoveAbs($nx, $ny)
    if ($n -ne 1) { throw "SendInput mouse move failed" }
}
# Proven pattern (seek-drag-test.ps1 + desktop-menu proof): SetCursorPos
# first, then mouse_event button events. SendInput moves/keys are fine, but
# SendInput button flags never arrived here (moves + keys verified working,
# buttons verified dead, mouse_event verified live via a desktop menu).
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ClickEv { [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e); }'
function Send-ClickAt([int]$x, [int]$y, [string]$button, [bool]$down) {
    [void][RealInput]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 15
    $flag = switch ("$button-$(if ($down) { 'd' } else { 'u' })") {
        'left-d' { 0x0002 } 'left-u' { 0x0004 }
        'right-d' { 0x0008 } 'right-u' { 0x0010 }
        'middle-d' { 0x0020 } 'middle-u' { 0x0040 }
    }
    [ClickEv]::mouse_event($flag, 0, 0, 0, [IntPtr]::Zero)
}
function Move-Path([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps) {
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($x1 + ($x2 - $x1) * $i / $steps)
        $y = [int]($y1 + ($y2 - $y1) * $i / $steps)
        $nn = Norm-XY $x $y
        Send-MouseAbs $nn[0] $nn[1]
        Start-Sleep -Milliseconds 5
    }
    [void][RealInput]::SetCursorPos($x2, $y2)
}
function Cur-Pos {
    $p = New-Object RealInput+POINT
    [void][RealInput]::GetCursorPos([ref]$p)
    return @($p.X, $p.Y)
}
function Button-Flags([string]$b, [bool]$down) {
    switch ("$b-$(if ($down) {'d'} else {'u'})") {
        'left-d' { return 0x0002 } 'left-u' { return 0x0004 }
        'right-d' { return 0x0008 } 'right-u' { return 0x0010 }
        'middle-d' { return 0x0020 } 'middle-u' { return 0x0040 }
    }
    throw "unknown button $b"
}
$VK = @{
    ctrl = 0x11; control = 0x11; shift = 0x10; alt = 0x12; menu = 0x12;
    enter = 0x0D; return = 0x0D; esc = 0x1B; escape = 0x1B; tab = 0x09;
    delete = 0x2E; del = 0x2E; back = 0x08; backspace = 0x08; space = 0x20;
    left = 0x25; up = 0x26; right = 0x27; down = 0x28;
    home = 0x24; end = 0x23; pgup = 0x21; pageup = 0x21; pgdn = 0x22; pagedown = 0x22;
    f2 = 0x71; a = 0x41; c = 0x43; d = 0x44; v = 0x56; x = 0x58; y = 0x59; z = 0x5A; o = 0x4F;
}
function Send-Key([int]$vk, [bool]$up) {
    # Real keyboards always carry a scancode: without KEYEVENTF_SCANCODE
    # WinUI reports Key=None for keys like Enter/Escape (proven by trace).
    $scan = [RealInput]::MapVirtualKey([uint32]$vk, 0)
    $flags = 0x0008
    if ($up) { $flags = $flags -bor 0x0002 }
    $n = [RealInput]::SendKey($vk, $scan, $flags)
    if ($n -ne 1) { throw "SendInput key vk=$vk up=$up failed" }
}
function Invoke-Chord([string]$spec) {
    $parts = $spec.ToLower().Split('+') | Where-Object { $_ -ne '' }
    if ($parts.Count -eq 0) { throw "empty -Keys" }
    $mods = @(); $main = $null
    foreach ($p in $parts) {
        if ($p -in @('ctrl', 'control', 'shift', 'alt', 'menu')) { $mods += $p } else { $main = $p }
    }
    if ($null -eq $main) { throw "chord needs a main key: $spec" }
    if (-not $VK.ContainsKey($main)) { throw "unknown key '$main'" }
    foreach ($m in $mods) { Send-Key $VK[$m] $false; Start-Sleep -Milliseconds 12 }
    Send-Key $VK[$main] $false; Start-Sleep -Milliseconds 25
    Send-Key $VK[$main] $true; Start-Sleep -Milliseconds 12
    for ($i = $mods.Count - 1; $i -ge 0; $i--) { Send-Key $VK[$mods[$i]] $true; Start-Sleep -Milliseconds 12 }
}
function Invoke-Type([string]$text) {
    # VK path for ASCII alphanumerics/space (KEYEVENTF_UNICODE was proven
    # silently swallowed here: injected but never inserted). en-US layout.
    foreach ($ch in $text.ToCharArray()) {
        $vk = -1; $shift = $false
        if ($ch -ge 'a' -and $ch -le 'z') { $vk = [int][char]::ToUpper($ch) }
        elseif ($ch -ge 'A' -and $ch -le 'Z') { $vk = [int]$ch; $shift = $true }
        elseif ($ch -ge '0' -and $ch -le '9') { $vk = [int]$ch }
        elseif ($ch -eq ' ') { $vk = 0x20 }
        if ($vk -lt 0) { throw "type: unsupported char '$ch' (extend map as needed)" }
        if ($shift) { Send-Key 0x10 $false; Start-Sleep -Milliseconds 8 }
        Send-Key $vk $false; Start-Sleep -Milliseconds 12
        Send-Key $vk $true; Start-Sleep -Milliseconds 8
        if ($shift) { Send-Key 0x10 $true; Start-Sleep -Milliseconds 8 }
    }
}
function Save-Shot([IntPtr]$hwnd, [string]$path) {
    $r = New-Object RealInput+RECT
    if (-not [RealInput]::GetWindowRect($hwnd, [ref]$r)) { throw "GetWindowRect failed" }
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { throw "bad window rect ${w}x${h}" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $painted = $false
    try {
        foreach ($flag in @(2, 0)) {
            if ([RealInput]::PrintWindow($hwnd, $hdc, $flag)) { $painted = $true; break }
        }
    } finally { $g.ReleaseHdc($hdc) }
    if (-not $painted) {
        # Fallback: copy the window rect off the screen (needs visible window).
        $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    }
    $g.Dispose()
    # Refuse black frames so callers never assert on dead pixels.
    $sum = 0L; $n = 0
    for ($x = 0; $x -lt $w; $x += 41) {
        for ($y = 0; $y -lt $h; $y += 41) {
            $c = $bmp.GetPixel($x, $y); $sum += [int]$c.R + [int]$c.G + [int]$c.B; $n++
        }
    }
    if ($sum / $n -lt 12) { $bmp.Dispose(); throw "screenshot is black (occluded/minimized?)" }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "shot $path ${w}x${h}"
}
Guard-Session
function Get-Hwnd([string]$s) {
    if ($s -match '^0[xX]([0-9a-fA-F]+)$') { return [IntPtr]::new([Convert]::ToInt64($Matches[1], 16)) }
    return [IntPtr]::new([Convert]::ToInt64($s))
}
switch ($Verb.ToLower()) {
    'focus' {
        $Hwnd = Get-Hwnd $Hwnd
        if ($Hwnd -eq [IntPtr]::Zero) { throw "-Hwnd required" }
        if ([RealInput]::IsIconic($Hwnd)) { [void][RealInput]::ShowWindow($Hwnd, 9) }
        [void][RealInput]::ShowWindow($Hwnd, 5)
        [void][RealInput]::SetForegroundWindow($Hwnd)
        Start-Sleep -Milliseconds $SettleMs
        Write-Host "focus fg=0x$(([RealInput]::GetForegroundWindow()).ToString('x'))"
    }
    'move' {
        $c = Cur-Pos; Move-Path $c[0] $c[1] $X $Y $Steps
        $p = Cur-Pos
        Write-Host "move $X,$Y steps=$Steps cursor=$($p[0]),$($p[1])"
    }
    'pos' {
        $p = Cur-Pos
        Write-Host "cursor $($p[0]),$($p[1])"
    }
    'click' {
        $c = Cur-Pos; Move-Path $c[0] $c[1] $X $Y $Steps
        Start-Sleep -Milliseconds $SettleMs
        Send-ClickAt $X $Y $Button $true
        Start-Sleep -Milliseconds 45
        Send-ClickAt $X $Y $Button $false
        Start-Sleep -Milliseconds $SettleMs
        Write-Host "click $Button $X,$Y"
    }
    'doubleclick' {
        $c = Cur-Pos; Move-Path $c[0] $c[1] $X $Y $Steps
        for ($i = 0; $i -lt 2; $i++) {
            Send-ClickAt $X $Y $Button $true; Start-Sleep -Milliseconds 40
            Send-ClickAt $X $Y $Button $false; Start-Sleep -Milliseconds 55
        }
        Write-Host "doubleclick $X,$Y"
    }
    'down' {
        if ($X -ge 0) { $c = Cur-Pos; Move-Path $c[0] $c[1] $X $Y $Steps }
        $p = Cur-Pos
        Send-ClickAt $p[0] $p[1] $Button $true
        Write-Host "down $Button $($p[0]),$($p[1])"
    }
    'up' {
        $p = Cur-Pos
        Send-ClickAt $p[0] $p[1] $Button $false
        Write-Host "up $Button $($p[0]),$($p[1])"
    }
    'drag' {
        $c = Cur-Pos; Move-Path $c[0] $c[1] $X1 $Y1 $Steps
        Send-ClickAt $X1 $Y1 $Button $true
        Start-Sleep -Milliseconds 120
        Move-Path $X1 $Y1 $X2 $Y2 $Steps
        Start-Sleep -Milliseconds 120
        Send-ClickAt $X2 $Y2 $Button $false
        Write-Host "drag $Button $X1,$Y1 -> $X2,$Y2 steps=$Steps"
    }
    'wheel' {
        $n = [RealInput]::SendWheel($Delta)
        if ($n -ne 1) { throw "SendInput wheel failed" }
        Write-Host "wheel $Delta"
    }
    'key' { Invoke-Chord $Keys; Write-Host "key $Keys" }
    'keydn' {
        foreach ($p in ($Keys.ToLower().Split('+') | Where-Object { $_ -ne '' })) {
            if (-not $VK.ContainsKey($p)) { throw "unknown key '$p'" }
            Send-Key $VK[$p] $false; Start-Sleep -Milliseconds 12
        }
        Write-Host "keydn $Keys"
    }
    'keyup' {
        $ps = @($Keys.ToLower().Split('+') | Where-Object { $_ -ne '' })
        for ($i = $ps.Count - 1; $i -ge 0; $i--) {
            if (-not $VK.ContainsKey($ps[$i])) { throw "unknown key '$($ps[$i])'" }
            Send-Key $VK[$ps[$i]] $true; Start-Sleep -Milliseconds 12
        }
        Write-Host "keyup $Keys"
    }
    'uia-rect' {
        $Hwnd = Get-Hwnd $Hwnd
        if ($Hwnd -eq [IntPtr]::Zero) { throw "-Hwnd required" }
        if ([string]::IsNullOrEmpty($AutoId)) { throw "-AutoId required" }
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
        if ($null -eq $root) { throw "FromHandle failed" }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutoId)
        $el = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -eq $el) { throw "AutomationId '$AutoId' not found" }
        $r = $el.Current.BoundingRectangle
        Write-Host "uia-rect $($AutoId) l=$([int]$r.Left) t=$([int]$r.Top) r=$([int]$r.Right) b=$([int]$r.Bottom)"
    }
    'uia-focus' {
        $Hwnd = Get-Hwnd $Hwnd
        if ($Hwnd -eq [IntPtr]::Zero) { throw "-Hwnd required" }
        if ([string]::IsNullOrEmpty($AutoId)) { throw "-AutoId required" }
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
        if ($null -eq $root) { throw "FromHandle failed" }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutoId)
        $el = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -eq $el) { throw "AutomationId '$AutoId' not found" }
        $el.SetFocus()
        Write-Host "uia-focus $AutoId"
    }
    'type' { Invoke-Type $Text; Write-Host "typed $($Text.Length) chars" }
    'shot' {
        $Hwnd = Get-Hwnd $Hwnd
        if ($Hwnd -eq [IntPtr]::Zero) { throw "-Hwnd required" }
        if ([string]::IsNullOrEmpty($Out)) { throw "-Out required" }
        Save-Shot $Hwnd $Out
    }
    'screen' {
        if ([string]::IsNullOrEmpty($Out)) { throw "-Out required" }
        $vx = [RealInput]::GetSystemMetrics(76); $vy = [RealInput]::GetSystemMetrics(77)
        $vw = [RealInput]::GetSystemMetrics(78); $vh = [RealInput]::GetSystemMetrics(79)
        $bmp = New-Object System.Drawing.Bitmap($vw, $vh)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($vx, $vy, 0, 0, $bmp.Size)
        $g.Dispose()
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host "screen $Out ${vw}x${vh}"
    }
    'rect' {
        $Hwnd = Get-Hwnd $Hwnd
        if ($Hwnd -eq [IntPtr]::Zero) { throw "-Hwnd required" }
        $r = New-Object RealInput+RECT
        [void][RealInput]::GetWindowRect($Hwnd, [ref]$r)
        Write-Host "rect l=$($r.L) t=$($r.T) r=$($r.R) b=$($r.B) w=$($r.R - $r.L) h=$($r.B - $r.T)"
    }
    default { throw "unknown verb '$Verb' (focus|move|click|doubleclick|down|up|drag|wheel|key|type|shot|rect)" }
}
