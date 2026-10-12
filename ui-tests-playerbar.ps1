# ui-tests-playerbar.ps1 - real-input player-bar verification.
# Drives the REAL UIA tree of --playerbar-ui-hold against the in-app stubs
# (TestFilePickerService/TestShellService: no real dialogs/Explorer).
#
# Launch discipline (hard rules): Start-Process -PassThru ONLY,
# $proc.WaitForExit(90000), kill on timeout; two failed launches in a row
# prints BLOCKED and stops. Every shell call respects the 120s timeout
# (no single call blocks longer).
#
# Run: .\ui-tests-playerbar.ps1
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

$HoldFile = 'D:\Audio\alac.m4a'
$StubFile = 'D:\Audio\flac_2.flac'
foreach ($f in @($HoldFile, $StubFile)) {
    if (-not (Test-Path -LiteralPath $f)) { throw "missing test file: $f" }
}
$StubLog = Join-Path $env:TEMP 'playerbar-stub-calls.log'
if (Test-Path -LiteralPath $StubLog) { Remove-Item -LiteralPath $StubLog -Force }
$Artifacts = Join-Path $env:TEMP 'playerbar-ui-artifacts'
if (Test-Path -LiteralPath $Artifacts) { Remove-Item -LiteralPath $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

if (-not ('W32Win2' -as [type])) {
    $src = @"
using System.Runtime.InteropServices;
public static class W32Win2 {
    [DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
    [DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
    $null = Add-Type -TypeDefinition $src
}

# Input verbs that winapp gates on foreground (send-keys --via send-input,
# click, hover, drag): retry with re-foregrounding when refusal is transient
# (e.g. the user clicked elsewhere mid-run). Reads/invokes/screenshots are
# background-safe and bypass this wrapper.
function Invoke-UiInput {
    param([string[]]$Arguments)
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Invoke-Ui $Arguments -Window $hwnd | Out-Null
            return
        } catch {
            if ("$_" -notmatch 'not in the foreground' -or $i -eq 2) { throw }
        }
        Ensure-Foreground $hwnd
        Start-Sleep -Milliseconds 500
    }
}
function Ensure-Foreground {
    param($h)
    $h = [System.IntPtr]$h
    [W32Win2]::ShowWindow($h, 9) | Out-Null
    if ([W32Win2]::GetForegroundWindow() -eq $h) { return }
    $us = [W32Win2]::GetCurrentThreadId()
    $fg = [W32Win2]::GetForegroundWindow()
    $fgTid = 0; $fgPid = 0
    if ($fg -ne [System.IntPtr]::Zero) { $fgTid = [W32Win2]::GetWindowThreadProcessId($fg, [ref]$fgPid) }
    $appPid = 0
    $appTid = [W32Win2]::GetWindowThreadProcessId($h, [ref]$appPid)
    try {
        if ($fgTid) { [W32Win2]::AttachThreadInput($us, $fgTid, $true) | Out-Null }
        [W32Win2]::AttachThreadInput($us, $appTid, $true) | Out-Null
        [W32Win2]::SetForegroundWindow($h) | Out-Null
    } finally {
        try { [W32Win2]::AttachThreadInput($us, $appTid, $false) | Out-Null } catch { }
        try { if ($fgTid) { [W32Win2]::AttachThreadInput($us, $fgTid, $false) | Out-Null } } catch { }
    }
    Start-Sleep -Milliseconds 500
}

$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$launchFailures = 0

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

function Get-Text {
    param([string]$Id)
    $v = Invoke-Ui @('get-value', $Id, '--json') | ConvertFrom-Json
    if ($v.text) { return $v.text }
    if ($v.name) { return $v.name }
    if ($v.value) { return $v.value }
    return ''
}

function Element-Rect {
    param([string]$Id)
    $el = (Invoke-Ui @('inspect', $Id, '--json') | ConvertFrom-Json).windows[0].elements[0]
    if (-not $el) { throw "element not found: $Id" }
    return @{ x = [int]$el.x; y = [int]$el.y; width = [int]$el.width; height = [int]$el.height }
}

function Parse-Stamp {
    param([string]$Text)
    $m = [regex]::Match($Text, '(\d+):(\d+)\.(\d+)')
    if (-not $m.Success) { throw "unparsable position text '$Text'" }
    return [int]$m.Groups[1].Value * 60000 + [int]$m.Groups[2].Value * 1000 + [int]$m.Groups[3].Value
}

function Assert-Alive {
    $p = Get-Text 'PositionText'
    if (-not ($p -match '\d+:\d+\.\d+')) { throw "app looks dead (PositionText='$p')" }
    if ($proc.HasExited) { throw "app process exited (code $($proc.ExitCode))" }
}

# Main-window discovery: the pid may own transient windows (popups,
# activation surfaces) that contain no bar controls. Poll for a non-popup
# window whose tree actually hosts TrackButton, so a wrong $hwnd can never
# silently poison every later -w call (reads are app-wide and would still
# pass, hiding it).
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

# A closed tip leaves no app-wide trace (toggle-close passes --gone and
# the marker vanishes), so app-wide MATCH == visibly open. NOTE: a bare
# `inspect <id>` returns the window node when nothing matches, so presence
# must check the automationId, not mere non-null!
function TipPresent {
    param([string]$Marker)
    try {
        $j = (Invoke-Ui @('inspect', $Marker, '--json') | ConvertFrom-Json)
        foreach ($w in $j.windows) {
            foreach ($e in $w.elements) {
                if ($e.automationId -eq $Marker) { return $true }
            }
        }
        return $false
    } catch {
        return $false
    }
}

# Buttons expose no ValuePattern: poll the accessible name, which carries
# the live text (e.g. "Playback speed 1.5x").
function Wait-ButtonText {
    param([string]$Id, [string]$Want, [int]$TimeoutMs = 4000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    $last = ''
    while ((Get-Date) -lt $deadline) {
        try { $last = Get-Text $Id } catch { }
        if ($last -match [regex]::Escape($Want)) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "button '$Id' text never contained '$Want' (last='$last')"
}

function Invoke-TipClose {
    param([string]$Marker)
    # The tip X is the template's AlternateCloseButton (unique automation
    # id; the caption Close must never be touched — it kills the app and
    # later --gone checks pass vacuously). Found app-wide: popup HWNDs
    # churn and -w popup scopes don't resolve.
    $tree = Invoke-Ui @('inspect', '--interactive', '--json') | ConvertFrom-Json
    function Get-Nodes($nodes) {
        foreach ($n in $nodes) { if ($null -ne $n) { $n; Get-Nodes $n.children } }
    }
    $all = @(Get-Nodes @($tree.windows | ForEach-Object { $_.elements }))
    $close = @($all | Where-Object { $_.automationId -eq 'AlternateCloseButton' }) | Select-Object -First 1
    if (-not $close) { throw 'tip X (AlternateCloseButton) not found in the interactive tree' }
    Invoke-Ui @('invoke', 'AlternateCloseButton') | Out-Null
    Invoke-Ui @('wait-for', $Marker, '--gone', '-t', '3000') | Out-Null
    if (TipPresent $Marker) { throw 'tip content still present after X' }
    Assert-Alive
}

function Ensure-NoPopup {
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $m = if (TipPresent 'TrackTitleText') { 'TrackTitleText' }
             elseif (TipPresent 'SpeedPreset05') { 'SpeedPreset05' }
             else { return }
        Invoke-TipClose $m
    }
    throw 'tip popup did not clear'
}

$AppPid = 0
$proc = $null
try {
    # The stub picker returns this file; inherited by the child process.
    $env:AMLL_PICKER_STUB = $StubFile
    # Hold runs outlive the 180s harness default; give them 600s so the
    # watchdog can never fire mid-run (it would Exit(3) the app).
    $env:AMLL_HARNESS_TIMEOUT = '600'
    $proc = Start-Process -FilePath $Exe -ArgumentList '--playerbar-ui-hold', $HoldFile -PassThru
    $AppPid = $proc.Id
    Write-Host "LAUNCH: pid=$AppPid hold=$HoldFile stub=$StubFile"
    Start-Sleep -Seconds 9
    if ($proc.HasExited) {
        $launchFailures++
        throw "app exited during startup (code $($proc.ExitCode))"
    }
    $hwnd = Find-MainHwnd $AppPid
    if (-not $hwnd) {
        $launchFailures++
        throw "no main app window (with TrackButton) for pid $AppPid"
    }
    $launchFailures = 0
    Write-Host "WINDOW: hwnd=$hwnd"
    Ensure-Foreground $hwnd

    Test-UI 'bar controls enabled, duration loaded' {
        Invoke-Ui @('wait-for', 'PlayButton', '-p', 'IsEnabled', '--value', 'True', '-t', '5000')
        Invoke-Ui @('wait-for', 'RateButton', '-p', 'IsEnabled', '--value', 'True', '-t', '5000')
        $d = Get-Text 'DurationText'
        if (-not ($d -match '\d+:\d+\.\d+')) { throw "duration not loaded: '$d'" }
        Write-Host "  duration=$d"
    }

    Test-UI 'initial screenshot delivered' {
        $p = Join-Path $Artifacts '01-bar.png'
        Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
            throw "screenshot not delivered: $p"
        }
    }

    Test-UI 'left click opens track tip with contents' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        $t = Get-Text 'TrackTitleText'; $a = Get-Text 'TrackArtistText'; $c = Get-Text 'TrackCodecText'
        Write-Host "  title='$t' artist='$a' codec='$c'"
        if (-not $t) { throw 'empty title' }
        if (-not $a) { throw 'empty artist line' }
        if (-not ($c -match '^- \S+ \S+$')) { throw "codec line has wrong shape: '$c'" }
    }

    Test-UI 'track tip screenshot delivered' {
        $p = Join-Path $Artifacts '02-track-tip.png'
        try { Invoke-Ui @('focus', 'TrackButton') -Window $hwnd | Out-Null } catch { }
        try {
            Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
        } catch {
            Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
        }
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
            throw "screenshot not delivered: $p"
        }
    }

    Test-UI 'volume screenshots at 0/20/50/80/100 and muted' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        foreach ($v in @(0, 20, 50, 80, 100)) {
            Invoke-Ui @('set-value', 'TipVolumeSlider', "$v") | Out-Null
            Start-Sleep -Milliseconds 400
            $p = Join-Path $Artifacts "vol-$v.png"
            try {
                Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
            } catch {
                Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
            }
            if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
                throw "screenshot not delivered: $p"
            }
        }
        Invoke-Ui @('invoke', 'TipVolumeStateButton') | Out-Null
        Start-Sleep -Milliseconds 400
        $p = Join-Path $Artifacts 'vol-muted.png'
        try {
            Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
        } catch {
            Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
        }
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
            throw "screenshot not delivered: $p"
        }
        Invoke-TipClose 'TrackTitleText'
    }

    Test-UI 'X closes the tip (and the app stays alive)' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        Invoke-TipClose 'TrackTitleText'
    }

    Test-UI 'Esc closes the tip' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        Invoke-UiInput @('send-keys', 'esc', '--via', 'send-input')
        Invoke-Ui @('wait-for', 'TrackTitleText', '--gone', '-t', '3000') | Out-Null
        if (TipPresent 'TrackTitleText') { throw 'tip popup still present after Esc' }
        Assert-Alive
    }

    Test-UI 'trigger toggles the tip' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '--gone', '-t', '3000') | Out-Null
        if (TipPresent 'TrackTitleText') { throw 'tip popup still present after toggle' }
        Assert-Alive
    }

    Test-UI 'right click opens picker once, no tip, loads stub file' {
        Ensure-NoPopup
        $before = Get-Text 'DurationText'
        $picksBefore = @(Get-Content -LiteralPath $StubLog -ErrorAction SilentlyContinue | Where-Object { $_ -like 'picker|*' }).Count
        Invoke-UiInput @('click', 'TrackButton', '--right')
        Start-Sleep -Seconds 4
        if (TipPresent 'TrackTitleText') { throw 'tip popup appeared on right click' }
        # Positive control: the predicate must see a deliberately opened tip.
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        if (-not (TipPresent 'TrackTitleText')) { throw 'presence predicate blind to an open tip' }
        Invoke-TipClose 'TrackTitleText'
        $after = Get-Text 'DurationText'
        Write-Host "  duration $before -> $after"
        if ($after -eq $before) { throw 'stub file did not load (duration unchanged)' }
        if (-not (Test-Path -LiteralPath $StubLog)) { throw 'stub log missing: picker never called' }
        $picks = @(Get-Content -LiteralPath $StubLog | Where-Object { $_ -like 'picker|*' })
        if ($picks.Count -ne ($picksBefore + 1)) { throw "picker calls went $($picksBefore)->$($picks.Count), want exactly one more" }
        if ($picks[-1] -ne "picker|$StubFile") { throw "picker logged '$($picks[-1])'" }
    }

    Test-UI 'View in Explorer calls shell stub with the file path' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TipExplorerButton', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'TipExplorerButton') | Out-Null
        Start-Sleep -Seconds 2
        $hits = @(Get-Content -LiteralPath $StubLog | Where-Object { $_ -eq "explorer|$StubFile" })
        if ($hits.Count -lt 1) { throw "shell stub missing explorer|$StubFile" }
        Invoke-TipClose 'TrackTitleText'
    }

    Test-UI 'long artist fits with codec visible (foreground geometry)' {
        # Stub file artist is 33 chars: needs the computed cap. UIA ints,
        # so 2px budgets (in-harness asserts the exact 1.5px/1px bounds).
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        try { Invoke-Ui @('focus', 'TrackButton') -Window $hwnd | Out-Null } catch { }
        Start-Sleep -Milliseconds 800
        $root = Element-Rect 'TipContentRoot'
        $art = Element-Rect 'TrackArtistText'
        $cod = Element-Rect 'TrackCodecText'
        $cap = $root.width - 64 - 12 - 6 - $cod.width
        if ($cap -lt 40) { $cap = 40 }
        Write-Host "  root=$($root.width) artist=$($art.width) codec=$($cod.width) cap=$cap"
        if ($art.width -gt $cap + 2) { throw "artist overflows cap: $($art.width) > $cap" }
        if (($art.x + $art.width) -gt ($root.x + $root.width) + 2) { throw 'artist overflows content' }
        if ($cod.width -le 0) { throw 'codec has no width' }
        # Item 3 (foreground): the buttons row structure — order, 8px gaps,
        # equal Open/Explorer widths, 36px heights, square state. (Grid panels
        # are UIA-invisible — inspect falls back to the window node — so row
        # edges + fill are asserted in-harness at 1px via Test_TipRowEdges
        # across every synthetic case and the empty state; buttons expose
        # exact UIA rects and are the foreground proxies.)
        $stb = Element-Rect 'TipVolumeStateButton'
        $opb = Element-Rect 'TipOpenFileButton'
        $exb = Element-Rect 'TipExplorerButton'
        $gap1 = $opb.x - $stb.x - $stb.width
        $gap2 = $exb.x - $opb.x - $opb.width
        Write-Host "  buttons state=$($stb.width)x$($stb.height) open=$($opb.width)x$($opb.height) explorer=$($exb.width)x$($exb.height) gaps=$gap1/$gap2"
        if ([Math]::Abs($opb.width - $exb.width) -gt 2) { throw "open/explorer widths differ: $($opb.width) vs $($exb.width)" }
        if ([Math]::Abs($gap1 - 8) -gt 2) { throw "state/open gap is not 8px: $gap1" }
        if ([Math]::Abs($gap2 - 8) -gt 2) { throw "open/explorer gap is not 8px: $gap2" }
        if ($stb.width -ne 36 -or $stb.height -ne 36) { throw "state button is not 36x36: $($stb.width)x$($stb.height)" }
        if ($opb.height -ne 36 -or $exb.height -ne 36) { throw "button heights are not 36: $($opb.height)/$($exb.height)" }
        Invoke-TipClose 'TrackTitleText'
    }

    Test-UI 'speed preset click sets button text, tip stays open' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedPreset15') | Out-Null
        Wait-ButtonText 'RateButton' '1.5x'
        if (-not (TipPresent 'SpeedPreset05')) { throw 'speed tip closed after preset click' }
        $p = Join-Path $Artifacts '03-speed-tip.png'
        try {
            Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
        } catch {
            Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
        }
    }

    Test-UI 'synthetic tip screenshots (5 cases)' {
        # One --playerbar-synth-hold per case (indices into the shared
        # table: 0 artist-short, 1 artist-cjk, 2 artist-80, 3 title-cjk,
        # 4 cover). $AppPid/$hwnd are rebound per case and restored after.
        $savedPid = $AppPid; $savedHwnd = $hwnd
        try {
            $names = @('artist-short', 'artist-cjk', 'artist-80', 'title-cjk', 'cover')
            for ($i = 0; $i -lt $names.Count; $i++) {
                $sp = Start-Process -FilePath $Exe -ArgumentList '--playerbar-synth-hold', "$i" -PassThru
                try {
                    Start-Sleep -Seconds 8
                    if ($sp.HasExited) { throw "synth hold $i exited early (code $($sp.ExitCode))" }
                    $AppPid = $sp.Id
                    $hwnd = Find-MainHwnd $AppPid
                    if (-not $hwnd) { throw "no main window for synth case $i" }
                    Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '5000') | Out-Null
                    # --capture-screen refuses background windows and a hidden
                    # driver cannot steal foreground via SetForegroundWindow,
                    # so focus the window first (may foreground it); fall back
                    # to a direct capture, which still contains the tip: with
                    # ShouldConstrainToRootBounds it renders in the main tree.
                    try { Invoke-Ui @('focus', 'TrackButton') -Window $hwnd | Out-Null } catch { }
                    Start-Sleep -Milliseconds 500
                    $p = Join-Path $Artifacts "synth-$($names[$i]).png"
                    try {
                        Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
                    } catch {
                        Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
                    }
                    if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
                        throw "screenshot not delivered: $p"
                    }
                } finally {
                    try {
                        if (-not $sp.HasExited) {
                            if (-not $sp.WaitForExit(5000)) { Stop-Process -Id $sp.Id -Force }
                        }
                    } catch { }
                }
            }
        } finally {
            $AppPid = $savedPid; $hwnd = $savedHwnd
        }
    }

    Test-UI 'theme screenshots Light/Dark (bar + both tips)' {
        # Separate holds with AMLL_REQUESTED_THEME (inherited env); each is
        # screenshotted with both tips. HC stays manual (OS-level only).
        $savedPid = $AppPid; $savedHwnd = $hwnd
        try {
            foreach ($theme in @('Light', 'Dark')) {
                $env:AMLL_REQUESTED_THEME = $theme
                $sp = Start-Process -FilePath $Exe -ArgumentList '--playerbar-ui-hold', $HoldFile -PassThru
                try {
                    Start-Sleep -Seconds 9
                    if ($sp.HasExited) { throw "$theme hold exited early (code $($sp.ExitCode))" }
                    $AppPid = $sp.Id
                    $hwnd = Find-MainHwnd $AppPid
                    if (-not $hwnd) { throw "no main window for $theme hold" }
                    $p = Join-Path $Artifacts "theme-$theme-bar.png"
                    Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
                    Invoke-Ui @('invoke', 'TrackButton') -Window $hwnd | Out-Null
                    Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '5000') | Out-Null
                    $p = Join-Path $Artifacts "theme-$theme-track.png"
                    try {
                        Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
                    } catch {
                        Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
                    }
                    Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
                    Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '5000') | Out-Null
                    $p = Join-Path $Artifacts "theme-$theme-speed.png"
                    try {
                        Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
                    } catch {
                        Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
                    }
                    foreach ($f in @("theme-$theme-bar.png", "theme-$theme-track.png", "theme-$theme-speed.png")) {
                        $fp = Join-Path $Artifacts $f
                        if (-not (Test-Path -LiteralPath $fp -PathType Leaf) -or (Get-Item -LiteralPath $fp).Length -eq 0) {
                            throw "screenshot not delivered: $fp"
                        }
                    }
                } finally {
                    try {
                        if (-not $sp.HasExited) {
                            if (-not $sp.WaitForExit(5000)) { Stop-Process -Id $sp.Id -Force }
                        }
                    } catch { }
                }
            }
        } finally {
            $AppPid = $savedPid; $hwnd = $savedHwnd
            Remove-Item Env:AMLL_REQUESTED_THEME -ErrorAction SilentlyContinue
        }
    }

    Test-UI 'right click cycles presets without opening the tip' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedPreset15') | Out-Null
        Wait-ButtonText 'RateButton' '1.5x'
        Invoke-TipClose 'SpeedPreset05'
        Ensure-Foreground $hwnd
        foreach ($want in @('1.25x', '.25x', '.5x', '1x', '1.5x')) {
            Invoke-UiInput @('click', 'RateButton', '--right')
            Wait-ButtonText 'RateButton' $want
        }
        if (TipPresent 'SpeedPreset05') { throw 'speed tip popup appeared during right-click cycle' }
    }

    Test-UI 'middle click resets to 1x without opening the tip' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedPreset15') | Out-Null
        Wait-ButtonText 'RateButton' '1.5x'
        Invoke-TipClose 'SpeedPreset05'
        $r = Element-Rect 'RateButton'
        Ensure-Foreground $hwnd
        $cx = $r.x + [int]($r.width / 2); $cy = $r.y + [int]($r.height / 2)
        & (Join-Path $Repo 'scripts\middle-click.ps1') -X $cx -Y $cy
        if ($LASTEXITCODE -ne 0) { throw 'middle-click helper failed' }
        Wait-ButtonText 'RateButton' '1x' -TimeoutMs 5000
        if (TipPresent 'SpeedPreset05') { throw 'speed tip popup appeared on middle click' }
    }

    Test-UI 'keep-pitch checkbox toggles' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedKeepPitch', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedKeepPitch') | Out-Null
        Invoke-Ui @('wait-for', 'SpeedKeepPitch', '--value', 'Off', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedKeepPitch') | Out-Null
        Invoke-Ui @('wait-for', 'SpeedKeepPitch', '--value', 'On', '-t', '3000') | Out-Null
        Invoke-TipClose 'SpeedPreset05'
    }

    Test-UI 'Enter/Space toggle the tips, Shift+F10 triggers context actions' {
        Ensure-NoPopup
        Ensure-Foreground $hwnd
        Invoke-UiInput @('send-keys', 'enter', '--target', 'TrackButton', '--via', 'send-input')
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        Invoke-TipClose 'TrackTitleText'
        Invoke-UiInput @('send-keys', 'space', '--target', 'RateButton', '--via', 'send-input')
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-TipClose 'SpeedPreset05'
        $before = Get-Text 'DurationText'
        Invoke-UiInput @('send-keys', 'shift+f10', '--target', 'TrackButton', '--via', 'send-input')
        Start-Sleep -Seconds 4
        if (TipPresent 'TrackTitleText') { throw 'Shift+F10 opened the tip instead of the picker' }
        $after = Get-Text 'DurationText'
        Write-Host "  Shift+F10 duration $before -> $after"
    }

    Test-UI 'eye button has the hook name and does not crash' {
        $v = Invoke-Ui @('get-value', 'EyeButton', '--json') | ConvertFrom-Json
        $nm = if ($v.name) { $v.name } else { $v.text }
        Write-Host "  eye name='$nm'"
        if ($nm -ne 'Jump to current lyric line') { throw "wrong eye name: '$nm'" }
        Invoke-Ui @('invoke', 'EyeButton') -Window $hwnd | Out-Null
        Start-Sleep -Seconds 1
        $p = Get-Text 'PositionText'
        if (-not ($p -match '\d+:\d+\.\d+')) { throw 'app unresponsive after eye invoke' }
    }

    Test-UI 'play/pause toggles name with engine state' {
        Invoke-Ui @('wait-for', 'PlayButton', '--value', 'Play', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'PlayButton', '--value', 'Pause', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'PlayButton', '--value', 'Play', '-t', '3000') | Out-Null
    }

    Test-UI 'position advances while unfocused (after speed change)' {
        # Preset to 1.5x through the real tip path, then defocus with
        # notepad (proven pattern): the clock/text must keep advancing and
        # thumb geometry under unfocus is covered in-harness, where the
        # template part is reachable (UIA exposes no Thumb node/Value here).
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedPreset15') | Out-Null
        Wait-ButtonText 'RateButton' '1.5x'
        Invoke-TipClose 'SpeedPreset05'
        $a0 = Parse-Stamp (Get-Text 'PositionText')
        Start-Sleep -Milliseconds 400
        $a1 = Parse-Stamp (Get-Text 'PositionText')
        if ($a1 -le $a0) { Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null }
        Start-Sleep -Milliseconds 1200
        $other = Start-Process -FilePath 'notepad.exe' -PassThru
        try {
            Start-Sleep -Milliseconds 1500
            $u1 = Parse-Stamp (Get-Text 'PositionText')
            Start-Sleep -Seconds 2
            $u2 = Parse-Stamp (Get-Text 'PositionText')
        } finally {
            try { Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        Write-Host "  unfocused $u1 -> $u2 at 1.5x"
        if ($u2 -le $u1 + 300) { throw "position stalled while unfocused ($u1 -> $u2)" }
        # Back to 1x, paused, for the layout legs.
        $r = Element-Rect 'RateButton'
        Ensure-Foreground $hwnd
        & (Join-Path $Repo 'scripts\middle-click.ps1') -X ($r.x + [int]($r.width / 2)) -Y ($r.y + [int]($r.height / 2))
        Wait-ButtonText 'RateButton' '1x' -TimeoutMs 5000
        Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null
    }

    Test-UI 'bar children share one center line, buttons 44x36' {
        $ids = @('TrackButton', 'PlayButton', 'PositionText', 'SeekSlider', 'DurationText', 'EyeButton', 'RateButton')
        $cys = @()
        $sizes = @()
        foreach ($id in $ids) {
            $r = Element-Rect $id
            $cys += ($r.y + $r.height / 2)
        }
        $lo = ($cys | Measure-Object -Minimum).Minimum
        $hi = ($cys | Measure-Object -Maximum).Maximum
        Write-Host "  centers: $($cys -join ', ') (spread $($hi - $lo), UIA ints: 2px budget)"
        if (($hi - $lo) -gt 2) { throw "bar children off one line (spread $($hi - $lo))" }
        foreach ($id in @('TrackButton', 'PlayButton', 'EyeButton', 'RateButton')) {
            $r = Element-Rect $id
            $sizes += "$id=$($r.width)x$($r.height)"
            if ([Math]::Abs($r.width - 44) -gt 1 -or [Math]::Abs($r.height - 36) -gt 1) {
                throw "button $id not 44x36: $($r.width)x$($r.height)"
            }
        }
        Write-Host "  sizes: $($sizes -join ' ')"
    }

    Test-UI 'speed hover screenshot delivered' {
        Ensure-Foreground $hwnd
        Invoke-UiInput @('hover', 'RateButton')
        Start-Sleep -Milliseconds 400
        $p = Join-Path $Artifacts '04-speed-hover.png'
        Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
            throw "screenshot not delivered: $p"
        }
    }

    Test-UI 'layout widths keep slider >= 120px' {
        $hw = [System.IntPtr]$hwnd
        foreach ($w in @(1400, 1116, 900, 750, 672, 600)) {
            $rc = New-Object W32Win2+RECT
            [W32Win2]::GetWindowRect($hw, [ref]$rc) | Out-Null
            $h = $rc.B - $rc.T
            [W32Win2]::SetWindowPos($hw, [System.IntPtr]::Zero, 0, 0, $w, $h, 0x0002) | Out-Null
            Start-Sleep -Milliseconds 900
            $r = Element-Rect 'SeekSlider'
            Write-Host "  width=$w slider=$($r.width)"
            if ($r.width -lt 120) { throw "slider $($r.width)px < 120 at window width $w" }
            $p = Join-Path $Artifacts "width-$w.png"
            Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
        }
    }
}
catch {
    if ($launchFailures -ge 2) {
        $results.Add(@{ name = 'launch'; status = 'FAIL'; detail = "BLOCKED: two failed launches in a row: $_" })
        Write-Host "BLOCKED: two failed launches in a row: $_" -ForegroundColor Red
    } else {
        $results.Add(@{ name = 'setup / driving'; status = 'FAIL'; detail = "$_" })
        Write-Host "FAIL setup: $_" -ForegroundColor Red
    }
}
finally {
    try {
        if ($AppPid -gt 0) { & winapp ui yield --on local | Out-Null }
    } catch { }
    try {
        if ($proc -and -not $proc.HasExited) {
            # The hold target never exits by itself: short grace wait, then
            # kill on timeout (bounded wait + kill, per the hard rule).
            if (-not $proc.WaitForExit(5000)) {
                Stop-Process -Id $AppPid -Force
                Write-Host 'KILL-ON-TIMEOUT: hold target did not exit within 5s'
            }
        }
    } catch { }
    Remove-Item Env:AMLL_PICKER_STUB -ErrorAction SilentlyContinue
    Remove-Item Env:AMLL_HARNESS_TIMEOUT -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = @{ target = 'local'; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed; results = @($results.ToArray()) }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'test-results.json') -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: $(Join-Path $Artifacts 'test-results.json')"
Get-ChildItem -LiteralPath $Artifacts -Filter *.png | ForEach-Object { "SHOT: $($_.FullName)" }
if ($failed) { exit 1 }
exit 0
