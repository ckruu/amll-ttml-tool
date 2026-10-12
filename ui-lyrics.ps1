# ui-lyrics.ps1 - Edit-tab lyrics verification (Phase 2 Step 5).
# Drives --load-lyrics holds (SAME import funnel as picker/drop) and
# --lyrics-test self-driving runs, asserting render facts through the REAL
# UIA tree plus committed goldens. Never clicks the Import buttons (native
# picker would hang a run - picker path is manual); never opens D:\ originals
# (temp copies only; lyric originals stay read-only).
#
# Launch discipline (hard rules): Start-Process -PassThru ONLY,
# $proc.WaitForExit(90000), kill on timeout; every shell call <= 120s.
# Run: pwsh ui-lyrics.ps1 [-Theme light|dark] (screenshots honor the theme;
# functional asserts are theme-independent, default dark).
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

$StageDir = Join-Path $env:TEMP ('lyrics-ui-' + [guid]::NewGuid().ToString('N'))
$Artifacts = Join-Path $env:TEMP 'lyrics-ui-artifacts'
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

if (-not ('W32Lyric' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32Lyric {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool c);
}
"@
}

function Ensure-Foreground {
    param($h)
    $h = [System.IntPtr]$h
    [W32Lyric]::ShowWindow($h, 9) | Out-Null
    if ([W32Lyric]::GetForegroundWindow() -eq $h) { return }
    $us = [W32Lyric]::GetCurrentThreadId()
    $fg = [W32Lyric]::GetForegroundWindow()
    $fgTid = 0; $fgPid = 0
    if ($fg -ne [System.IntPtr]::Zero) { $fgTid = [W32Lyric]::GetWindowThreadProcessId($fg, [ref]$fgPid) }
    $appPid = 0
    $appTid = [W32Lyric]::GetWindowThreadProcessId($h, [ref]$appPid)
    try {
        if ($fgTid) { [W32Lyric]::AttachThreadInput($us, $fgTid, $true) | Out-Null }
        [W32Lyric]::AttachThreadInput($us, $appTid, $true) | Out-Null
        [W32Lyric]::SetForegroundWindow($h) | Out-Null
    } finally {
        try { [W32Lyric]::AttachThreadInput($us, $appTid, $false) | Out-Null } catch { }
        try { if ($fgTid) { [W32Lyric]::AttachThreadInput($us, $fgTid, $false) | Out-Null } } catch { }
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
    # NOTE: no --interactive by default. That flag returns interactive
    # elements only and drops structural containers (LineList), TextBlocks
    # (FileNameText, empty-state, dialog titles) - the exact anchors these
    # asserts need. Conversely the full dump leaves data-bound chip TextBoxes
    # nameless; their 'Word N:' / 'Romanization of word N' names only appear
    # in the interactive dump. Chip-level asserts use -Interactive.
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

function Get-ListSubtree {
    param($Window, [switch]$Interactive)
    $tree = Get-Tree $Window -Interactive:$Interactive
    $list = @($tree | Where-Object { $_.automationId -eq 'LineList' })[0]
    if (-not $list) { throw 'LineList missing from tree' }
    $out = [Collections.Generic.List[object]]::new()
    $walkScript = {
        param($nodes)
        foreach ($n in $nodes) {
            if ($null -eq $n) { continue }
            $out.Add($n)
            if ($n.children) { & $walkScript $n.children }
        }
    }
    & $walkScript $list.children
    return $out
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

function Wait-TreeNameAbsent {
    param($Window, [string]$Name, [int]$TimeoutSec = 8, [switch]$Interactive)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $tree = Get-Tree $Window -Interactive:$Interactive
        if (@($tree | Where-Object { $_.name -eq $Name }).Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 400
    }
    throw "'$Name' still present after ${TimeoutSec}s"
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
    # Direct AutomationId lookup (not a tree walk): deterministic presence
    # probe. Tree dumps omit nodes (verified: visible card rows missing from
    # dumps), but direct lookup finds every exposed element.
    param($Window, [string]$Id)
    try {
        Invoke-Ui @('get-value', $Id, '--json') -Window $Window | Out-Null
        return $true
    } catch {
        if ("$_" -match 'element_not_found') { return $false }
        throw
    }
}

function Wait-Element {
    param($Window, [string]$Id, [int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-HasElement $Window $Id) { return $true }
        Start-Sleep -Milliseconds 400
    }
    throw "timed out waiting for element '$Id'"
}

function Wait-ElementAbsent {
    param($Window, [string]$Id, [int]$TimeoutSec = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-HasElement $Window $Id)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    throw "element '$Id' still present after ${TimeoutSec}s"
}

function Select-Line {
    # Foreground-independent selection: raw mouse click / send-keys need the
    # window foregrounded (flaky while the machine is in use); driving the
    # ListViewItem's SelectionItemPattern exercises the same selection ->
    # SelectionService -> ribbon pipeline deterministically. Resolves the
    # exact item by AutomationId-free slug from a fresh dump (text search
    # substring-matches 'Line 1' against 'Line 10'+). Unrealized lines are
    # scrolled to by selecting the deepest realized line first (verified:
    # selection scrolls the ListView).
    param($Window, [int]$Line)
    $item = $null
    for ($i = 0; $i -lt 10; $i++) {
        $sub = Get-ListSubtree $Window
        $item = @($sub | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq "Line $Line" })[0]
        if ($item) { break }
        $nums = @($sub | Where-Object { $_.type -eq 'ListItem' -and $_.name -match '^Line \d+$' } | ForEach-Object { [int]([regex]::Match($_.name, '\d+').Value) } | Sort-Object)
        if (-not $nums) { throw 'no line cards in tree' }
        $item = $null
        $deepest = @($sub | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq "Line $($nums[-1])" })[0]
        if (-not $deepest -or -not $deepest.selector) { throw "cannot scroll: deepest line $($nums[-1]) has no selector" }
        Invoke-Ui @('invoke', $deepest.selector, '--action', 'select') -Window $Window | Out-Null
        Start-Sleep -Milliseconds 800
    }
    if (-not $item) { throw "Line $Line missing from tree after scrolling" }
    if (-not $item.selector) { throw "Line $Line node has no selector" }
    Invoke-Ui @('invoke', $item.selector, '--action', 'select') -Window $Window | Out-Null
    $sel = ''
    $deadline = (Get-Date).AddSeconds(5)
    do {
        $sel = Get-Text 'LineStartBox' -Window $Window
        if ($sel) { break }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)
    if (-not $sel) { throw "selecting line $Line did not reach the ribbon" }
}

function Invoke-UiInput {
    param([string[]]$Arguments, $Window)
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Invoke-Ui $Arguments -Window $Window | Out-Null
            return
        } catch {
            if ("$_" -notmatch 'not in the foreground' -or $i -eq 2) { throw }
        }
        Ensure-Foreground $Window
        Start-Sleep -Milliseconds 500
    }
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

function Start-Hold {
    param([string]$File)
    $savedTheme = $env:AMLL_REQUESTED_THEME
    $savedTimeout = $env:AMLL_HARNESS_TIMEOUT
    try {
        if ($Theme) { $env:AMLL_REQUESTED_THEME = $Theme }
        $env:AMLL_HARNESS_TIMEOUT = '600'
        # Quote the file arg: Start-Process joins -ArgumentList arrays with
        # bare spaces (pwsh), so paths with spaces would split at the C
        # runtime boundary (observed: 'spaced name.ttml' -> 2 argv).
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

function Run-LyricsTest {
    param([string]$File, [int]$TimeoutSec = 600, [string]$SaveAs = '')
    $report = Join-Path $env:TEMP 'lyrics-test-report.txt'
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }
    # Sweep strays first: a previous failure must never pollute this run's
    # shared report path (self-closing runs exit on their own when green).
    $runStart = Get-Date
    $env:AMLL_HARNESS_TIMEOUT = "$TimeoutSec"
    # File via env (exact, spaces intact - Start-Process array joins would
    # otherwise split spaced paths; see run-harness.ps1 header). -Args keeps
    # a pre-quoted copy as fallback.
    $env:AMLL_HARNESS_FILE = $File
    try {
        # Hidden window + output to file: a visible console here would sit
        # blank on the user's desktop for the whole self-driving run.
        $launcherLog = Join-Path $env:TEMP 'lyrics-launcher.log'
        $launcher = Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Repo 'scripts/run-harness.ps1'),'-Mode','lyrics-test','-TimeoutSec',"$TimeoutSec",'-Args',('"' + ($File -replace '"', '\"') + '"'),'-Detach' -WindowStyle Hidden -RedirectStandardOutput $launcherLog -RedirectStandardError ($launcherLog + '.err') -PassThru
        $launcher | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue | Out-Null
    } finally {
        Remove-Item Env:\AMLL_HARNESS_TIMEOUT -ErrorAction SilentlyContinue
        Remove-Item Env:\AMLL_HARNESS_FILE -ErrorAction SilentlyContinue
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $waitStart = Get-Date
    try {
        while ((Get-Date) -lt $deadline) {
            $verdict = $null
            # NOTE: Test-Path guard first - Select-String on a missing path
            # throws even with -ErrorAction SilentlyContinue (PS 7.6).
            if (Test-Path -LiteralPath $report) {
                $verdict = Select-String -Path $report -Pattern '^VERDICT:' -ErrorAction SilentlyContinue | Select-Object -Last 1
            }
        if ($verdict) {
            $line = $verdict.Line
            $base = [System.IO.Path]::GetFileNameWithoutExtension($File) -replace '[^\w\-]', '_'
            Copy-Item -LiteralPath $report -Destination (Join-Path $Artifacts "lyrics-test-$base.txt") -Force -ErrorAction SilentlyContinue
            if ($line -match '^VERDICT: PASS') {
                Write-Host "  numbers ok: $line"
                return
            }
            throw "self-driving failed: $line (see $report)"
        }
            # Heartbeat: a silent 10-minute wait is indistinguishable from a
            # stuck run. Show elapsed, app liveness, and the last report line.
            $elapsed = [int]((Get-Date) - $waitStart).TotalSeconds
            $lastLine = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Tail 1 -ErrorAction SilentlyContinue } else { '<no report yet>' }
            $appAlive = @(Get-Process -Name 'AmllTtmlTool.WinUI' -ErrorAction SilentlyContinue).Count
            Write-Host "  ... waiting ${elapsed}s/${TimeoutSec}s appAlive=$appAlive last=[$lastLine]"
            Start-Sleep -Seconds 15
        }
        throw "self-driving timed out after ${TimeoutSec}s (see $report)"
    } finally {
        Get-Process -Name 'AmllTtmlTool.WinUI' -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -gt $runStart } |
            ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { } }
    }
}

function Set-Toggles {
    param($Window, [int]$Combo)
    $map = @(@('TransToggle', 1), @('RomanToggle', 2), @('WordRomanToggle', 4))
    foreach ($p in $map) {
        $want = if (($Combo -band $p[1]) -ne 0) { 'On' } else { 'Off' }
        $got = Get-Text $p[0] -Window $Window
        if ($got -ne $want) {
            # Pattern invoke needs no foreground (unlike mouse/keys), so no
            # focus stealing here while the machine is in use.
            Invoke-Ui @('invoke', $p[0]) -Window $Window | Out-Null
            $deadline = (Get-Date).AddSeconds(5)
            do {
                Start-Sleep -Milliseconds 300
                $got = Get-Text $p[0] -Window $Window
            } while ($got -ne $want -and (Get-Date) -lt $deadline)
            if ($got -ne $want) { throw "$($p[0]) stuck at $got, want $want" }
        }
    }
}

function Assert-ComboRows {
    param($Window, [int]$Combo)
    $wantT = ($Combo -band 1) -ne 0
    $wantR = ($Combo -band 2) -ne 0
    $wantW = ($Combo -band 4) -ne 0
    # Row boxes carry per-line AutomationIds (TransBox1/RomanBox1, set at
    # realization): direct lookup is deterministic, unlike tree walks, which
    # omit nodes. Settle by polling presence, then assert exactness.
    if ($wantT) { Wait-Element $Window 'TransBox1' 5 } else { Wait-ElementAbsent $Window 'TransBox1' 5 }
    if ($wantR) { Wait-Element $Window 'RomanBox1' 5 } else { Wait-ElementAbsent $Window 'RomanBox1' 5 }
    if ($wantW) { Wait-TreeName $Window 'Romanization of word 1' 5 -Interactive } else { Wait-TreeNameAbsent $Window 'Romanization of word 1' 5 -Interactive }
    $gotT = Test-HasElement $Window 'TransBox1'
    $gotR = Test-HasElement $Window 'RomanBox1'
    $inter = Get-Tree $Window -Interactive
    $gotW = @($inter | ForEach-Object { $_.name } | Where-Object { $_ -match '^Romanization of word \d+$' }).Count -gt 0
    if ($gotT -ne $wantT) { throw "combo ${Combo}: Translation row present=$gotT want=$wantT (pills T/R/W=$((Get-Text 'TransToggle' -Window $Window))/$(Get-Text 'RomanToggle' -Window $Window)/$(Get-Text 'WordRomanToggle' -Window $Window))" }
    if ($gotR -ne $wantR) { throw "combo ${Combo}: Romanization row present=$gotR want=$wantR" }
    if ($gotW -ne $wantW) { throw "combo ${Combo}: word romaji present=$gotW want=$wantW" }
}

function Assert-RomajiUnderKana {
    param($Window)
    # Pair within each line card: word numbers restart per line, and cards
    # share left edges, so a doc-wide first-match pairs across lines (observed:
    # L2 romaji measured against L1 kana). Join by card bounds instead.
    $cards = @(Get-ListSubtree $Window | Where-Object { $_.type -eq 'ListItem' -and $_.name -match '^Line \d+$' })
    if ($cards.Count -eq 0) { throw 'no line cards in tree' }
    $inter = Get-Tree $Window -Interactive
    $pairs = 0
    foreach ($card in $cards) {
        $inCard = @($inter | Where-Object { $_.x -ge $card.x -and ($_.x + $_.width) -le ($card.x + $card.width) -and $_.y -ge $card.y -and ($_.y + $_.height) -le ($card.y + $card.height) })
        $words = @($inCard | Where-Object { $_.name -match '^Word \d+:' })
        $romans = @($inCard | Where-Object { $_.name -match '^Romanization of word \d+$' })
        foreach ($r in $romans) {
            $n = [regex]::Match($r.name, 'word (\d+)').Groups[1].Value
            $k = @($words | Where-Object { $_.name -match "^Word ${n}:" })[0]
            if (-not $k) { throw "$($card.name): kana for $($r.name) missing" }
            if ([Math]::Abs($r.x - $k.x) -gt 2) { throw "$($card.name): $($r.name) x=$($r.x) vs kana '$($k.name)' x=$($k.x)" }
            if ($r.x + $r.width -gt $k.x + $k.width + 2) { throw "$($card.name): $($r.name) [$($r.x)+$($r.width)] overflows kana '$($k.name)' [$($k.x)+$($k.width)] on the right" }
            if ($r.y -lt $k.y + $k.height - 2) { throw "$($card.name): $($r.name) not below kana" }
            $pairs++
        }
    }
    if ($pairs -eq 0) { throw 'no romaji boxes in tree (toggle on?)' }
}

function Write-Ttml {
    param([string]$Name, [string[]]$Lines, [string]$Head = '')
    $div = $Lines -join ''
    $xml = '<?xml version="1.0" encoding="UTF-8"?><tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><head><metadata>' + $Head + '</metadata></head><body><div>' + $div + '</div></body></tt>'
    $p = Join-Path $StageDir $Name
    [System.IO.File]::WriteAllText($p, $xml, [System.Text.Encoding]::UTF8)
    return $p
}

# ---------------- stage fixtures ----------------
$FixtureDir = Join-Path $Repo 'winui\AmllTtmlTool.Core.Tests\Fixtures'
Copy-Item -LiteralPath (Join-Path $FixtureDir 'lrc-multi.lrc') -Destination (Join-Path $StageDir 'multi.lrc')
Copy-Item -LiteralPath (Join-Path $FixtureDir 'gen-rich.xml') -Destination (Join-Path $StageDir 'rich.ttml')
Copy-Item -LiteralPath (Join-Path $FixtureDir 'gen-simple.xml') -Destination (Join-Path $StageDir 'simple.ttml')
if (Test-Path -LiteralPath 'D:\Lyrics') {
    foreach ($f in @('apple_No Poi!.ttml', 'broken_Down.ttml', 'lrc_Wakamonono Subete.lrc')) {
        $src = Join-Path 'D:\Lyrics' $f
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $StageDir $f) }
    }
}

# synth-all: trans + line-roman + word-roman data + width pairs (short kana /
# long romaji AND long kana / short romaji) for combo + width asserts.
$synthLines = @(
    '<p begin="0.000" end="2.000" itunes:key="L1"><span begin="0.000" end="0.500">強が</span><span begin="0.500" end="1.000">り</span><span ttm:role="x-translation">strong</span><span ttm:role="x-roman">suyoga ri</span></p>',
    '<p begin="2.000" end="4.000" itunes:key="L2"><span begin="2.000" end="2.800">屋上遊園地案内所</span><span begin="2.800" end="4.000">a</span></p>',
    '<p begin="4.000" end="5.000" itunes:key="L3"><span begin="4.000" end="5.000">plain</span></p>'
)
$synthHead = '<iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><transliterations><transliteration><text for="L1"><span begin="0.000" end="0.500">suyoga</span><span begin="0.500" end="1.000">ri</span></text></transliteration><transliteration><text for="L2"><span begin="2.000" end="2.800">oku</span><span begin="2.800" end="4.000">alongromajiword</span></text></transliteration></transliterations></iTunesMetadata>'
$synthFile = Write-Ttml 'synth-all.ttml' $synthLines $synthHead

# beats: empty words carrying empty beats (-> "Space ×n" chips) + a normal word.
$beatsFile = Write-Ttml 'beats.ttml' @(
    '<p begin="0.000" end="2.000"><span begin="0.000" end="1.000">la</span><span begin="1.000" end="2.000" amll:empty-beat="2" xmlns:amll="http://www.example.com/ns/amll"></span></p>'
)

# anchor-40: 40 tall lines so the list scrolls with slack both ways. The
# 3-line synth cannot satisfy anchoring on hide at scroll offset 0 (content
# above shrinks with nowhere to scroll back to - dy=32 there is geometry,
# not a bug); line 20 scrolled into view has room to compensate.
$anchorLines = [Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt 40; $i++) {
    $bMs = $i * 2000
    $anchorLines.Add("<p begin=`"$bMs`" end=`"$($bMs + 2000)`" itunes:key=`"A$($i + 1)`"><span begin=`"$bMs`" end=`"$($bMs + 1000)`">line$($i + 1)word</span><span ttm:role=`"x-translation`">trans$($i + 1)</span><span ttm:role=`"x-roman`">roman$($i + 1)</span></p>")
}
$anchorFile = Write-Ttml 'anchor-40.ttml' $anchorLines.ToArray()

# stress-300x10: 300 lines x 10 words, trans every 5th line, word-roman every
# 3rd word of those lines (transliteration block, exact spans).
# 3rd word of those lines (transliteration block, exact spans).
$stressLines = [Collections.Generic.List[string]]::new()
$stressTr = [Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt 300; $i++) {
    $bMs = $i * 2000
    $spans = [Collections.Generic.List[string]]::new()
    $ros = [Collections.Generic.List[string]]::new()
    for ($j = 0; $j -lt 10; $j++) {
        $ws = $bMs + $j * 200
        $we = $ws + 200
        $spans.Add("<span begin=`"$ws`" end=`"$we`">w$($i)_$($j)</span>")
        if ($i % 5 -eq 0 -and $j % 3 -eq 0) {
            $ros.Add("<span begin=`"$ws`" end=`"$we`">ro$($i)_$($j)</span>")
        }
    }
    $tr = if ($i % 5 -eq 0) { "<span ttm:role=`"x-translation`">tr$i</span>" } else { '' }
    $stressLines.Add("<p begin=`"$bMs`" end=`"$($bMs + 2000)`" itunes:key=`"L$($i + 1)`">$($spans -join '')$tr</p>")
    if ($ros.Count -gt 0) {
        $stressTr.Add("<transliteration><text for=`"L$($i + 1)`">$($ros -join '')</text></transliteration>")
    }
}
$stressHead = '<iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><transliterations>' + ($stressTr -join '') + '</transliterations></iTunesMetadata>'
$stressFile = Write-Ttml 'stress-300x10.ttml' $stressLines.ToArray() $stressHead

# stress-2000: 2000 lines x 3 words, trans every 10th.
$bigLines = [Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt 2000; $i++) {
    $bMs = $i * 1000
    $tr = if ($i % 10 -eq 0) { "<span ttm:role=`"x-translation`">tr$i</span>" } else { '' }
    $bigLines.Add("<p begin=`"$bMs`" end=`"$($bMs + 1000)`"><span begin=`"$bMs`" end=`"$($bMs + 300)`">a$i</span><span begin=`"$($bMs + 300)`" end=`"$($bMs + 600)`">b$i</span><span begin=`"$($bMs + 600)`" end=`"$($bMs + 1000)`">c$i</span>$tr</p>")
}
$bigFile = Write-Ttml 'stress-2000.ttml' $bigLines.ToArray()

Set-Content -LiteralPath (Join-Path $StageDir 'malformed.ttml') -Value '<tt><body><div><p begin=' -NoNewline
Set-Content -LiteralPath (Join-Path $StageDir 'empty.ttml') -Value '' -NoNewline
Set-Content -LiteralPath (Join-Path $StageDir 'bad.qrc') -Value 'not qrc content' -NoNewline
Write-Host "STAGED: $StageDir"

try {
    # ---- self-driving runs (numbers) ----
    # Budgets: green runs take ~10s; a hang must surface in ~2 min, not 10.
    # The app-side HarnessHost watchdog follows TimeoutSec via env, so both
    # sides agree. Stress docs get more room for the 8-combo matrix walk.
    Test-UI 'lyrics-test synth-all' { Run-LyricsTest $synthFile -TimeoutSec 120 }
    Test-UI 'lyrics-test rich' { Run-LyricsTest (Join-Path $StageDir 'rich.ttml') -TimeoutSec 120 }
    Test-UI 'lyrics-test multi.lrc' { Run-LyricsTest (Join-Path $StageDir 'multi.lrc') -TimeoutSec 120 }
    Test-UI 'lyrics-test beats' { Run-LyricsTest (Join-Path $StageDir 'beats.ttml') -TimeoutSec 120 }
    if (Test-Path -LiteralPath (Join-Path $StageDir 'apple_No Poi!.ttml')) {
        Test-UI 'lyrics-test apple' { Run-LyricsTest (Join-Path $StageDir 'apple_No Poi!.ttml') -TimeoutSec 180 }
    }
    if (Test-Path -LiteralPath (Join-Path $StageDir 'broken_Down.ttml')) {
        Test-UI 'lyrics-test broken' { Run-LyricsTest (Join-Path $StageDir 'broken_Down.ttml') -TimeoutSec 120 }
    }
    if (Test-Path -LiteralPath (Join-Path $StageDir 'lrc_Wakamonono Subete.lrc')) {
        Test-UI 'lyrics-test real lrc' { Run-LyricsTest (Join-Path $StageDir 'lrc_Wakamonono Subete.lrc') -TimeoutSec 120 }
    }
    Test-UI 'lyrics-test stress-300x10' { Run-LyricsTest $stressFile -TimeoutSec 300 }
    Test-UI 'lyrics-test stress-2000' { Run-LyricsTest $bigFile -TimeoutSec 300 }

    # ---- render holds (UIA asserts) ----
    $hold = $null
    try {
        $hold = Start-Hold $synthFile
        $hwnd = $hold.Hwnd

        Test-UI 'title shows file name' {
            $t = Get-Text 'FileNameText' -Window $hwnd
            if ($t -ne 'synth-all.ttml') { throw "title='$t'" }
        }

        Test-UI 'line cards equal model (3)' {
            $sub = Get-ListSubtree $hwnd
            $cards = @($sub | Where-Object { $_.type -eq 'ListItem' -and $_.name -match '^Line \d+$' })
            if ($cards.Count -ne 3) { throw "cards=$($cards.Count)" }
        }

        Test-UI 'chip counts equal model' {
            $sub = Get-ListSubtree $hwnd
            $l1 = @($sub | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'Line 1' })[0]
            if (-not $l1) { throw 'Line 1 missing' }
            # Chips carry names only in the interactive dump, which drops the
            # ListView container; join by bounds against the card rect instead.
            $inter = Get-Tree $hwnd -Interactive
            $chips = @($inter | Where-Object { $_.name -match '^Word \d+:' -and $_.x -ge $l1.x -and ($_.x + $_.width) -le ($l1.x + $l1.width) -and $_.y -ge $l1.y -and ($_.y + $_.height) -le ($l1.y + $l1.height) })
            if ($chips.Count -ne 2) { throw "line1 chips=$($chips.Count)" }
        }

        Test-UI 'sampled chip texts' {
            $sub = Get-Tree $hwnd -Interactive
            foreach ($want in @('強が', '屋上遊園地案内所')) {
                if (@($sub | Where-Object { $_.name -match "^Word \d+: $([regex]::Escape($want))$" }).Count -eq 0) {
                    throw "chip '$want' missing"
                }
            }
        }

        Test-UI 'click selects line (pill visible)' {
            Select-Line $hwnd 1
            Save-Shot $hwnd 'sel-pill.png'
        }

        Test-UI 'keyboard moves selection (ribbon proves it)' {
            # Selection moves through the same pipeline as arrow keys
            # (ListView SelectionChanged -> SelectionService -> ribbon); raw
            # send-keys needs foreground and is covered manually instead.
            Select-Line $hwnd 1
            $a = Get-Text 'LineStartBox' -Window $hwnd
            if ($a -ne '00:00.000') { throw "line1 start='$a'" }
            Select-Line $hwnd 2
            $b = Get-Text 'LineStartBox' -Window $hwnd
            if ($b -ne '00:02.000') { throw "line2 start='$b'" }
            Select-Line $hwnd 1
            $c = Get-Text 'LineStartBox' -Window $hwnd
            if ($c -ne '00:00.000') { throw "back to line1 start='$c'" }
            Select-Line $hwnd 3
            $d = Get-Text 'LineStartBox' -Window $hwnd
            if ($d -ne '00:04.000') { throw "last line start='$d'" }
            Select-Line $hwnd 1
            $e = Get-Text 'LineStartBox' -Window $hwnd
            if ($e -ne '00:00.000') { throw "first line start='$e'" }
        }

        Test-UI '8 toggle combos show exact rows' {
            foreach ($combo in 0..7) {
                Set-Toggles $hwnd $combo
                Assert-ComboRows $hwnd $combo
            }
            Save-Shot $hwnd 'combo-all-off.png'
            Set-Toggles $hwnd 7
            Save-Shot $hwnd 'combo-all-on.png'
        }

        Test-UI 'bar and overflow toggles agree' {
            Set-Toggles $hwnd 7
            foreach ($p in @('TransToggle', 'RomanToggle', 'WordRomanToggle')) {
                $v = Get-Text $p -Window $hwnd
                if ($v -ne 'On') { throw "$p is $v" }
            }
            [W32Lyric]::MoveWindow([System.IntPtr]$hwnd, 40, 40, 600, 760, $true) | Out-Null
            Start-Sleep -Milliseconds 800
            Save-Shot $hwnd 'narrow-600.png'
            Invoke-UiInput @('invoke', 'CenterToggleOverflow') -Window $hwnd
            Start-Sleep -Milliseconds 500
            Save-Shot $hwnd 'overflow-menu.png'
            Invoke-UiInput @('invoke', 'WordRomanOverflowItem') -Window $hwnd
            # Re-widen first: at 600px the titlebar pills collapse into the
            # overflow by design (MasterTogglePanel visible only at level<=2),
            # so the BAR pill is unreadable until wide again.
            [W32Lyric]::MoveWindow([System.IntPtr]$hwnd, 40, 40, 1400, 800, $true) | Out-Null
            Start-Sleep -Milliseconds 800
            $v = Get-Text 'WordRomanToggle' -Window $hwnd
            if ($v -ne 'Off') { throw "pill did not follow overflow (WordRomanToggle=$v)" }
            Wait-TreeNameAbsent $hwnd 'Romanization of word 1' -Interactive
            [W32Lyric]::MoveWindow([System.IntPtr]$hwnd, 40, 40, 1400, 800, $true) | Out-Null
            Start-Sleep -Milliseconds 800
        }
    } finally {
        Stop-Hold $hold
    }

    # ---- anchor hold (own doc: needs scroll slack, see fixture note) ----
    $holdA = $null
    try {
        $holdA = Start-Hold $anchorFile
        $hwnd = $holdA.Hwnd
        Test-UI 'selected line anchored within 4px across toggle' {
            Select-Line $hwnd 20
            $sub = Get-ListSubtree $hwnd
            $y0 = (@($sub | Where-Object { $_.name -eq 'Line 20' })[0]).y
            $cur = Get-Text 'RomanToggle' -Window $hwnd
            Invoke-Ui @('invoke', 'RomanToggle') -Window $hwnd | Out-Null
            Start-Sleep -Seconds 1
            $sub2 = Get-ListSubtree $hwnd
            $y1 = (@($sub2 | Where-Object { $_.name -eq 'Line 20' })[0]).y
            if ([Math]::Abs($y1 - $y0) -gt 4) { throw "dy=$([Math]::Abs($y1 - $y0)) (y0=$y0 y1=$y1)" }
            $now = Get-Text 'RomanToggle' -Window $hwnd
            if ($now -eq $cur) { throw 'toggle did not flip' }
        }
    } finally {
        Stop-Hold $holdA
    }

    # ---- wrapping widths ----
    $hold2 = $null
    try {
        $hold2 = Start-Hold $synthFile
        $hwnd = $hold2.Hwnd
        Set-Toggles $hwnd 7
        foreach ($w in @(1400, 900, 672, 600)) {
            Test-UI "wrap at ${w}px keeps romaji under kana" {
                [W32Lyric]::MoveWindow([System.IntPtr]$hwnd, 40, 40, $w, 760, $true) | Out-Null
                Start-Sleep -Milliseconds 800
                Assert-RomajiUnderKana $hwnd
            }
        }
    } finally {
        Stop-Hold $hold2
    }

    # ---- beats render ----
    $holdB = $null
    try {
        $holdB = Start-Hold (Join-Path $StageDir 'beats.ttml')
        $hwnd = $holdB.Hwnd
        Test-UI 'empty beats render as Space chips' {
            Wait-TreeName $hwnd 'Word 2: Space ×2' -Interactive
        }
    } finally {
        Stop-Hold $holdB
    }

    # ---- errors ----
    $hold3 = $null
    try {
        $hold3 = Start-Hold (Join-Path $StageDir 'malformed.ttml')
        $hwnd = $hold3.Hwnd
        Test-UI 'malformed shows message UI' {
            Wait-TreeName $hwnd 'Import failed'
            # NOTE: dialog custom content (message + Details expander) is not
            # exposed to UIA (verified: only Title + buttons surface), so assert
            # the title here and keep a screenshot for the record; the OK
            # button is exercised by the dismiss step below. Message/Details
            # content is covered by the self-driving error-cases (typed
            # exceptions, messages logged) instead.
            Save-Shot $hwnd 'error-dialog.png'
        }
        Test-UI 'dismiss keeps empty state, no crash' {
            Invoke-Ui @('invoke', 'CloseButton') -Window $hwnd | Out-Null
            Wait-TreeName $hwnd 'No lyrics loaded'
            $t = Get-Text 'FileNameText' -Window $hwnd
            if ($t -ne 'Untitled') { throw "title='$t'" }
        }
    } finally {
        Stop-Hold $hold3
    }

    $hold4 = $null
    try {
        $hold4 = Start-Hold (Join-Path $StageDir 'bad.qrc')
        $hwnd = $hold4.Hwnd
        Test-UI 'unsupported extension shows message UI' {
            Wait-TreeName $hwnd 'Import failed'
        }
    } finally {
        Stop-Hold $hold4
    }

    $hold5 = $null
    try {
        $hold5 = Start-Hold (Join-Path $StageDir 'empty.ttml')
        $hwnd = $hold5.Hwnd
        Test-UI 'empty file, empty state, no dialog' {
            Wait-TreeName $hwnd 'No lyrics loaded'
            $t = Get-Text 'FileNameText' -Window $hwnd
            if ($t -ne 'empty.ttml') { throw "title='$t'" }
        }
        Save-Shot $hwnd 'empty-state.png'
    } finally {
        Stop-Hold $hold5
    }

    # ---- persistence across restart ----
    $hold6 = $null
    try {
        $hold6 = Start-Hold $synthFile
        $hwnd = $hold6.Hwnd
        Test-UI 'set all toggles on' { Set-Toggles $hwnd 7 }
    } finally {
        Stop-Hold $hold6
    }
    Test-UI 'toggle state persists across restart' {
        $settings = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'AmllTtmlTool\settings.json') -Raw | ConvertFrom-Json
        if ($settings.showTranslation -ne $true) { throw 'showTranslation not persisted' }
        if ($settings.showRomanization -ne $true) { throw 'showRomanization not persisted' }
        if ($settings.showWordRomanization -ne $true) { throw 'showWordRomanization not persisted' }
    }
    $hold7 = $null
    try {
        $hold7 = Start-Hold $synthFile
        $hwnd = $hold7.Hwnd
        Test-UI 'relaunched pills match persisted state' {
            foreach ($p in @('TransToggle', 'RomanToggle', 'WordRomanToggle')) {
                $v = Get-Text $p -Window $hwnd
                if ($v -ne 'On') { throw "$p is $v after restart" }
            }
        }
    } finally {
        Stop-Hold $hold7
    }
} finally {
    Remove-Item -LiteralPath $StageDir -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{
    target = 'local'; passed = $results.Count - $failed; failed = $failed
    results = @($results.ToArray())
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'test-results.json') -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: $Artifacts\test-results.json"
if ($failed) { exit 1 }
exit 0

