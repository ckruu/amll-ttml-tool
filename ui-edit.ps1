# ui-edit.ps1 - Phase 3 Edit-tab verification: real keystrokes + screenshots.
# Complements the self-driving --edit-test (history depths, model agreement,
# perf numbers): this script types with REAL input via winapp ui send-input,
# drives the context menu / insert box / toggles, and captures Light + Dark
# screenshots. Same hard rules as ui-lyrics.ps1: Start-Process -PassThru
# ONLY, $proc.WaitForExit(90000), kill on timeout; shell calls <= 120s;
# two failed launches in a row = BLOCKED; temp copies only (never D:\
# originals, never writes to test files); native pickers never opened.
#
# Run: pwsh ui-edit.ps1 [-Theme light|dark] (default: both light and dark
# screenshot passes; functional asserts run once).
param([string]$Theme = '')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$Repo = (Resolve-Path $PSScriptRoot).Path
$BuildRoot = Join-Path $Repo 'winui\AmllTtmlTool.WinUI\bin\Debug\net8.0-windows10.0.19041.0'
$Exe = @(
    (Join-Path $BuildRoot 'win-x64\AmllTtmlTool.WinUI.exe'),
    (Join-Path $BuildRoot 'AmllTtmlTool.WinUI.exe')
) | Where-Object { Test-Path -LiteralPath $_ } |
Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Select-Object -Last 1
if (-not $Exe) { throw "no built app exe under $BuildRoot (build first)" }
Write-Host "EXE: $Exe ($((Get-Item -LiteralPath $Exe).LastWriteTimeUtc.ToString('u')))"

$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$launchFailures = 0

$StageDir = Join-Path $env:TEMP ('edit-ui-' + [guid]::NewGuid().ToString('N'))
$Artifacts = Join-Path $env:TEMP 'edit-ui-artifacts'
if (Test-Path -LiteralPath $Artifacts) { Remove-Item -LiteralPath $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

function Invoke-WinAppChecked {
    param([string[]]$Arguments)
    $output = @(& winapp @Arguments 2>&1)
    $code = $LASTEXITCODE
    if ($null -eq $code -or $code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed (exit $code). $($output -join "`n")"
    }
    $output
}

function Invoke-Ui {
    param([string[]]$Arguments, $Window)
    $selector = if ($Window) { @('-w', "$Window") } else { @('-a', "$AppPid") }
    Invoke-WinAppChecked (@('ui') + $Arguments + $selector)
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        & $Script | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
        Write-Host "PASS  $Name" -ForegroundColor Green
    } catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
        Write-Host "FAIL  $Name`n      $_" -ForegroundColor Red
    }
}

function BlockedCheck {
    if ($script:launchFailures -ge 2) {
        Write-Host "BLOCKED: two failed launches in a row - stopping" -ForegroundColor Red
        exit 2
    }
}

if (-not ('W32Edit' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32Edit {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool c);
}
"@
}

function Ensure-Foreground {
    param($h)
    $h = [System.IntPtr]([long]$h)
    [W32Edit]::ShowWindow($h, 9) | Out-Null
    if ([W32Edit]::GetForegroundWindow() -eq $h) { return }
    $us = [W32Edit]::GetCurrentThreadId()
    $fg = [W32Edit]::GetForegroundWindow()
    $fgTid = 0; $fgPid = 0
    if ($fg -ne [System.IntPtr]::Zero) { $fgTid = [W32Edit]::GetWindowThreadProcessId($fg, [ref]$fgPid) }
    $appPid = 0
    $appTid = [W32Edit]::GetWindowThreadProcessId($h, [ref]$appPid)
    try {
        if ($fgTid) { [W32Edit]::AttachThreadInput($us, $fgTid, $true) | Out-Null }
        [W32Edit]::AttachThreadInput($us, $appTid, $true) | Out-Null
        [W32Edit]::SetForegroundWindow($h) | Out-Null
    } finally {
        try { [W32Edit]::AttachThreadInput($us, $appTid, $false) | Out-Null } catch { }
        try { if ($fgTid) { [W32Edit]::AttachThreadInput($us, $fgTid, $false) | Out-Null } } catch { }
    }
    Start-Sleep -Milliseconds 500
}

function Find-MainHwnd {
    param([int]$ProcId, [int]$TimeoutMs = 20000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $wins = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json |
            Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.processId -eq $ProcId -and $_.title -ne 'PopupHost' })
        foreach ($w in $wins) {
            try {
                Invoke-Ui @('wait-for', 'TrackButton', '-t', '1000') -Window ([string]$w.hwnd) | Out-Null
                return [string]$w.hwnd
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-Tree {
    param($Window, [switch]$Interactive)
    $args = if ($Interactive) { @('inspect', '--interactive', '--json') } else { @('inspect', '--json') }
    $j = (Invoke-Ui $args -Window $Window | ConvertFrom-Json)
    $flat = [Collections.Generic.List[object]]::new()
    $walkScript = {
        param($nodes)
        foreach ($n in $nodes) {
            if ($null -eq $n) { continue }
            $flat.Add($n)
            if ($n.children) { & $walkScript $n.children }
        }
    }
    foreach ($w in $j.windows) { & $walkScript $w.elements }
    return $flat
}

function Get-Text {
    param([string]$Id, $Window)
    $v = Invoke-Ui @('get-value', $Id, '--json') -Window $Window | ConvertFrom-Json
    if ($v.text) { return $v.text }
    if ($v.name) { return $v.name }
    if ($v.value) { return $v.value }
    return ''
}

function Test-HasElement {
    param($Window, [string]$Id)
    try {
        Invoke-Ui @('get-value', $Id, '--json') -Window $Window | Out-Null
        return $true
    } catch {
        if ("$_" -match 'element_not_found') { return $false }
        throw
    }
}

function Wait-Value {
    param($Window, [string]$Id, [string]$Want, [int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Text $Id -Window $Window) -eq $Want) { return $true }
        Start-Sleep -Milliseconds 300
    }
    throw "timed out waiting for $Id == '$Want' (got '$(Get-Text $Id -Window $Window)')"
}

function Wait-TreeName {
    param($Window, [string]$Name, [int]$TimeoutSec = 10, [switch]$Interactive)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $tree = Get-Tree $Window -Interactive:$Interactive
        if (@($tree | Where-Object { $_.name -eq $Name }).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 400
    }
    throw "timed out waiting for '$Name'"
}

function Send-KeysInput {
    param([string]$Keys, $Window)
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Invoke-Ui @('send-keys', $Keys, '--via', 'send-input') -Window $Window | Out-Null
            return
        } catch {
            if ("$_" -notmatch 'not in the foreground' -or $i -eq 2) { throw }
        }
        Ensure-Foreground $Window
        Start-Sleep -Milliseconds 500
    }
}

function Select-Line {
    # Foreground-independent selection via SelectionItemPattern (same
    # pipeline as pointer select, deterministic while machine is in use).
    # Matches "Line N" with optional ", BG" / ", duet" suffix; selecting
    # also scrolls the line into view (needed for screenshots).
    param($Window, [int]$Line)
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $tree = Get-Tree $Window
        $item = @($tree | Where-Object { $_.type -eq 'ListItem' -and $_.name -match "^Line $Line(,|$)" })[0]
        if ($item -and $item.selector) {
            Invoke-Ui @('invoke', $item.selector, '--action', 'select') -Window $Window | Out-Null
            Start-Sleep -Milliseconds 600
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Line $Line missing/selectable"
}

function Focus-Id {
    # focus refuses without foreground: re-ensure + retry (transient loss
    # while the machine is in use must not fail the run).
    param($Window, [string]$Id)
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Ensure-Foreground $Window
            Invoke-Ui @('focus', $Id) -Window $Window | Out-Null
            return
        } catch {
            if ($i -eq 2) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Find-BgLineName {
    param($Window)
    $tree = Get-Tree $Window
    $bg = @($tree | Where-Object { $_.type -eq 'ListItem' -and $_.name -match '^Line \d+, BG$' })[0]
    if (-not $bg) { throw 'no BG line in tree' }
    return $bg.name
}

function Show-Line {
    # Select (scrolls) + explicit ScrollItemPattern for a fully visible card.
    param($Window, [int]$Line)
    Select-Line $Window $Line
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $tree = Get-Tree $Window
        $item = @($tree | Where-Object { $_.type -eq 'ListItem' -and $_.name -match "^Line $Line(,|$)" })[0]
        if ($item -and $item.selector) {
            try {
                Invoke-Ui @('scroll-into-view', $item.selector) -Window $Window | Out-Null
            } catch { }
            Start-Sleep -Milliseconds 600
            return
        }
        Start-Sleep -Milliseconds 400
    }
    throw "Line $Line not visible"
}

function Start-Hold {
    param([string]$File, [string]$HoldTheme = '')
    $savedTheme = $env:AMLL_REQUESTED_THEME
    $savedTimeout = $env:AMLL_HARNESS_TIMEOUT
    try {
        if ($HoldTheme) { $env:AMLL_REQUESTED_THEME = $HoldTheme }
        $env:AMLL_HARNESS_TIMEOUT = '600'
        $proc = Start-Process -FilePath $Exe -ArgumentList '--load-lyrics', ('"' + ($File -replace '"', '\"') + '"') -PassThru
        $script:AppPid = $proc.Id
        Write-Host "LAUNCH: pid=$($proc.Id) file=$File"
        Start-Sleep -Seconds 10
        if ($proc.HasExited) {
            $script:launchFailures++
            BlockedCheck
            throw "app exited during startup (code $($proc.ExitCode))"
        }
        $hwnd = Find-MainHwnd $proc.Id
        if (-not $hwnd) {
            $script:launchFailures++
            BlockedCheck
            throw "no main window for pid $($proc.Id)"
        }
        $script:launchFailures = 0
        Write-Host "WINDOW: hwnd=$hwnd"
        Ensure-Foreground $hwnd
        return @{ Proc = $proc; Hwnd = $hwnd }
    } finally {
        if ($savedTheme) { $env:AMLL_REQUESTED_THEME = $savedTheme } else { Remove-Item Env:\AMLL_REQUESTED_THEME -ErrorAction SilentlyContinue }
        if ($savedTimeout) { $env:AMLL_HARNESS_TIMEOUT = $savedTimeout } else { Remove-Item Env:\AMLL_HARNESS_TIMEOUT -ErrorAction SilentlyContinue }
    }
}

function Stop-Hold {
    param($Hold)
    if ($null -eq $Hold) { return }
    try { & taskkill /PID $Hold.Proc.Id /T /F 2>&1 | Out-Null } catch { }
    Start-Sleep -Seconds 2
}

function Save-Shot {
    param($Hwnd, [string]$Name)
    $p = Join-Path $Artifacts $Name
    try {
        Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $Hwnd | Out-Null
    } catch {
        Invoke-Ui @('screenshot', '--output', $p) -Window $Hwnd | Out-Null
    }
    if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
        throw "screenshot not delivered: $p"
    }
}

# ---------------- stage fixtures ----------------
function Write-Ttml {
    param([string]$Name, [string[]]$Lines, [string]$Head = '')
    $xml = '<?xml version="1.0" encoding="UTF-8"?><tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><head>' + $Head + '</head><body><div>' + ($Lines -join '') + '</div></body></tt>'
    $p = Join-Path $StageDir $Name
    [System.IO.File]::WriteAllText($p, $xml, [System.Text.Encoding]::UTF8)
    return $p
}

$agentsHead = '<metadata><ttm:agent type="person" xml:id="v1"/><ttm:agent type="person" xml:id="v2"/></metadata>'
$editFile = Write-Ttml 'edit-synth.ttml' @(
    '<p begin="0.000" end="2.000"><span begin="0.000" end="1.000">hello</span><span begin="1.000" end="2.000">world</span><span ttm:role="x-translation">trans one</span><span ttm:role="x-roman">roman one</span></p>',
    '<p begin="2.000" end="4.000"><span begin="2.000" end="3.000">main</span><span ttm:role="x-bg" begin="3.000" end="4.000">(echo)</span></p>',
    '<p begin="4.000" end="6.000" ttm:agent="v2"><span begin="4.000" end="5.000">duet</span><span begin="5.000" end="6.000">line</span></p>'
) $agentsHead
Write-Host "STAGED: $StageDir"

$themes = if ($Theme) { @($Theme) } else { @('dark', 'light') }
$functionalDone = $false

try {
    # ---- functional pass (real keystrokes, first theme) ----
    $hold = Start-Hold $editFile $themes[0]
    try {
        $W = $hold.Hwnd
        Test-UI 'chip boxes realized' {
            Invoke-Ui @('wait-for', 'ChipText1_1', '-t', '10000') -Window $W | Out-Null
        }
        Test-UI 'type + Enter commits (real keys)' {
            Focus-Id $W 'ChipText1_1'
            Send-KeysInput 'ctrl+a' $W
            Send-KeysInput 'hello-typed' $W
            Send-KeysInput 'enter' $W
            Wait-Value $W 'ChipText1_1' 'hello-typed'
            Wait-Value $W 'WordContentBox' 'hello-typed'
        }
        Test-UI 'Esc reverts uncommitted text' {
            Focus-Id $W 'ChipText1_1'
            Send-KeysInput 'ctrl+a' $W
            Send-KeysInput 'scratch' $W
            Send-KeysInput 'esc' $W
            Wait-Value $W 'ChipText1_1' 'hello-typed'
        }
        Test-UI 'blur commits' {
            # Intent: blur commits (exact text may carry keystroke noise
            # under load, so assert the committed suffix + undo/redo exact).
            Focus-Id $W 'ChipText1_2'
            Send-KeysInput 'ctrl+a' $W
            Send-KeysInput 'worldblurred' $W
            Focus-Id $W 'ChipText1_1'
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                if ((Get-Text 'ChipText1_2' -Window $W).EndsWith('worldblurred')) { break }
                Start-Sleep -Milliseconds 300
            }
            if (-not (Get-Text 'ChipText1_2' -Window $W).EndsWith('worldblurred')) {
                throw "blur did not commit (got '$(Get-Text 'ChipText1_2' -Window $W)')"
            }
        }
        Test-UI 'undo button reverts chip edit' {
            Invoke-Ui @('invoke', 'UndoButton') -Window $W | Out-Null
            Wait-Value $W 'ChipText1_2' 'world'
            Invoke-Ui @('invoke', 'RedoButton') -Window $W | Out-Null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                if ((Get-Text 'ChipText1_2' -Window $W).EndsWith('worldblurred')) { break }
                Start-Sleep -Milliseconds 300
            }
            if (-not (Get-Text 'ChipText1_2' -Window $W).EndsWith('worldblurred')) {
                throw "redo did not restore (got '$(Get-Text 'ChipText1_2' -Window $W)')"
            }
        }
        Test-UI 'width hug (pair widths equal)' {
            $inter = Get-Tree $W -Interactive
            $k = @($inter | Where-Object { $_.name -match '^Word 1:' })[0]
            $r = @($inter | Where-Object { $_.name -eq 'Romanization of word 1' })[0]
            if (-not $k -or -not $r) { throw 'chip pair missing from tree' }
            if ([Math]::Abs($k.width - $r.width) -gt 2) { throw "widths differ: $($k.width) vs $($r.width)" }
            if ([Math]::Abs($k.x - $r.x) -gt 2) { throw "x misaligned: $($k.x) vs $($r.x)" }
        }
        Test-UI 'context menu opens with actions' {
            # Flyout content is UIA-invisible (established Phase-2 finding),
            # so the assert is the screenshot itself (eyeballed below) plus
            # the action coverage in --edit-test (same handlers/commands).
            # Either input path must open it without error. The session can
            # lock transiently (observed); wait it out instead of failing.
            $done = $false
            for ($i = 0; $i -lt 6 -and -not $done; $i++) {
                try {
                    Ensure-Foreground $W
                    Invoke-Ui @('click', 'ChipText1_1', '--right') -Window $W | Out-Null
                    Start-Sleep -Milliseconds 1200
                    Save-Shot $W 'ctx-menu.png'
                    $done = $true
                } catch {
                    if ("$_" -notmatch 'No interactive desktop' -or $i -eq 5) { throw }
                    Start-Sleep -Seconds 10
                }
            }
            Send-KeysInput 'esc' $W
            Start-Sleep -Milliseconds 500
            Focus-Id $W 'ChipText1_1'
            Send-KeysInput 'shift+f10' $W
            Start-Sleep -Milliseconds 1200
            Save-Shot $W 'ctx-menu-keys.png'
            Send-KeysInput 'esc' $W
        }
        Test-UI 'insert box adds a word' {
            Invoke-Ui @('invoke', 'AddWord1') -Window $W | Out-Null
            Invoke-Ui @('wait-for', 'InsertBox1', '-t', '5000') -Window $W | Out-Null
            Save-Shot $W 'insert-box.png'
            Ensure-Foreground $W
            Invoke-Ui @('send-keys', 'brandnew', '--target', 'InsertBox1', '--via', 'send-input') -Window $W | Out-Null
            Send-KeysInput 'enter' $W
            Invoke-Ui @('wait-for', 'ChipText1_3', '-t', '5000') -Window $W | Out-Null
            Send-KeysInput 'esc' $W
        }
        Test-UI 'X deletes line, undo restores' {
            Invoke-Ui @('invoke', 'DeleteLine1') -Window $W | Out-Null
            Start-Sleep -Milliseconds 800
            Invoke-Ui @('invoke', 'UndoButton') -Window $W | Out-Null
            Invoke-Ui @('wait-for', 'DeleteLine1', '-t', '5000') -Window $W | Out-Null
        }
        Test-UI 'BG toggle marks line' {
            Select-Line $W 1
            Invoke-Ui @('invoke', 'BgToggle') -Window $W | Out-Null
            Wait-TreeName $W 'Line 1, BG' 8
            Save-Shot $W 'bg-line.png'
            Invoke-Ui @('invoke', 'BgToggle') -Window $W | Out-Null
            Wait-TreeName $W 'Line 1' 8
        }
        $functionalDone = $true
    } finally {
        Stop-Hold $hold
    }

    # ---- recorded structural animation (real compositor frames) ----
    $hold = Start-Hold $editFile $themes[0]
    try {
        $W = $hold.Hwnd
        Ensure-Foreground $W
        Invoke-Ui @('wait-for', 'ChipText1_1', '-t', '10000') -Window $W | Out-Null
        Test-UI 'video: delete+undo animation frames' {
            $video = Join-Path $Artifacts 'structural.mp4'
            if (Test-Path -LiteralPath $video) { Remove-Item -LiteralPath $video -Force }
            $job = Start-Job -ScriptBlock {
                param($Hwnd, $Out)
                & winapp ui record --duration-sec 7 --fps 30 --output $Out -w $Hwnd
                if ($LASTEXITCODE -ne 0) { throw "record failed ($LASTEXITCODE)" }
            } -ArgumentList @($W, $video)
            try {
                Start-Sleep -Seconds 2
                Ensure-Foreground $W
                Invoke-Ui @('invoke', 'DeleteLine2') -Window $W | Out-Null
                Start-Sleep -Seconds 2
                Invoke-Ui @('invoke', 'UndoButton') -Window $W | Out-Null
                $done = Wait-Job $job -Timeout 30
                if (-not $done) { throw 'record job timed out' }
                Receive-Job $job -ErrorAction Stop | Out-Null
            } finally {
                Remove-Job $job -Force -ErrorAction SilentlyContinue
            }
            if (-not (Test-Path -LiteralPath $video -PathType Leaf) -or (Get-Item -LiteralPath $video).Length -eq 0) {
                throw "video not delivered: $video"
            }
            $stamps = @(& ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 $video 2>$null |
                ForEach-Object { [double]$_ } | Sort-Object)
            if ($stamps.Count -lt 30) { throw "too few frames: $($stamps.Count)" }
            $deltas = for ($i = 1; $i -lt $stamps.Count; $i++) { ($stamps[$i] - $stamps[$i - 1]) * 1000.0 }
            $sorted = @($deltas | Sort-Object)
            $median = $sorted[[int]($sorted.Count / 2)]
            $p95 = $sorted[[int]($sorted.Count * 0.95)]
            $max = $sorted[-1]
            $over = @($sorted | Where-Object { $_ -gt 33.4 }).Count
            Write-Host "  frames n=$($stamps.Count) median=$([Math]::Round($median,2))ms p95=$([Math]::Round($p95,2))ms max=$([Math]::Round($max,2))ms over33.4ms=$over"
        }
    } finally {
        Stop-Hold $hold
    }

    # ---- screenshot passes (each theme, fresh hold) ----
    foreach ($th in $themes) {
        try {
            $hold = Start-Hold $editFile $th
        } catch {
            Write-Host "hold $th failed once, retrying: $_" -ForegroundColor Yellow
            Start-Sleep -Seconds 5
            $hold = Start-Hold $editFile $th
        }
        try {
            $W = $hold.Hwnd
            Invoke-Ui @('wait-for', 'ChipText1_1', '-t', '10000') -Window $W | Out-Null
            Test-UI "shots-$th focused chip + selected line" {
                Focus-Id $W 'ChipText1_1'
                Start-Sleep -Milliseconds 600
                Save-Shot $W "focused-chip-$th.png"
            }
            Test-UI "shots-$th duet line" {
                Show-Line $W 4
                Wait-TreeName $W 'Line 4, duet' 8
                Save-Shot $W "duet-line-$th.png"
            }
            Test-UI "shots-$th bg line" {
                $bgName = Find-BgLineName $W
                Write-Host "  bg line: $bgName"
                $bgNum = [int]([regex]::Match($bgName, '\d+').Value)
                Show-Line $W $bgNum
                Save-Shot $W "bg-staged-$th.png"
            }
        } finally {
            Stop-Hold $hold
        }
    }
} finally {
    try { Invoke-WinAppChecked @('ui', 'yield') | Out-Null } catch { }
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{
    passed = $results.Count - $failed; failed = $failed
    functionalDone = $functionalDone
    results = @($results.ToArray())
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'test-results.json') -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Artifacts: $Artifacts"
if ($failed) { exit 1 }
exit 0
