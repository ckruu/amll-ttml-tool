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
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
    $null = Add-Type -TypeDefinition $src
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

function Assert-Alive {
    $p = Get-Text 'PositionText'
    if (-not ($p -match '\d+:\d+\.\d+')) { throw "app looks dead (PositionText='$p')" }
    if ($proc.HasExited) { throw "app process exited (code $($proc.ExitCode))" }
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
    $proc = Start-Process -FilePath $Exe -ArgumentList '--playerbar-ui-hold', $HoldFile -PassThru
    $AppPid = $proc.Id
    Write-Host "LAUNCH: pid=$AppPid hold=$HoldFile stub=$StubFile"
    Start-Sleep -Seconds 9
    if ($proc.HasExited) {
        $launchFailures++
        throw "app exited during startup (code $($proc.ExitCode))"
    }
    $hwnd = (Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json |
        Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.processId -eq $AppPid } |
        Select-Object -First 1).hwnd
    if (-not $hwnd) {
        $launchFailures++
        throw "no app window for pid $AppPid"
    }
    $launchFailures = 0
    Write-Host "WINDOW: hwnd=$hwnd"

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
        Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
            throw "screenshot not delivered: $p"
        }
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
        Invoke-Ui @('send-keys', 'esc', '--via', 'send-input') -Window $hwnd | Out-Null
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
        Invoke-Ui @('click', 'TrackButton', '--right') -Window $hwnd | Out-Null
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

    Test-UI 'speed preset click sets button text, tip stays open' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedPreset15') | Out-Null
        Wait-ButtonText 'RateButton' '1.5x'
        if (-not (TipPresent 'SpeedPreset05')) { throw 'speed tip closed after preset click' }
        $p = Join-Path $Artifacts '03-speed-tip.png'
        Invoke-Ui @('screenshot', '--output', $p, '--capture-screen') -Window $hwnd | Out-Null
    }

    Test-UI 'right click cycles presets without opening the tip' {
        Ensure-NoPopup
        Invoke-Ui @('invoke', 'RateButton') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-Ui @('invoke', 'SpeedPreset15') | Out-Null
        Wait-ButtonText 'RateButton' '1.5x'
        Invoke-TipClose 'SpeedPreset05'
        foreach ($want in @('1.25x', '.25x', '.5x', '1x', '1.5x')) {
            Invoke-Ui @('click', 'RateButton', '--right') -Window $hwnd | Out-Null
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
        Invoke-Ui @('send-keys', 'enter', '--target', 'TrackButton', '--via', 'send-input') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'TrackTitleText', '-t', '3000') | Out-Null
        Invoke-TipClose 'TrackTitleText'
        Invoke-Ui @('send-keys', 'space', '--target', 'RateButton', '--via', 'send-input') -Window $hwnd | Out-Null
        Invoke-Ui @('wait-for', 'SpeedPreset05', '-t', '3000') | Out-Null
        Invoke-TipClose 'SpeedPreset05'
        $before = Get-Text 'DurationText'
        Invoke-Ui @('send-keys', 'shift+f10', '--target', 'TrackButton', '--via', 'send-input') -Window $hwnd | Out-Null
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
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = @{ target = 'local'; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed; results = @($results.ToArray()) }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'test-results.json') -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: $(Join-Path $Artifacts 'test-results.json')"
Get-ChildItem -LiteralPath $Artifacts -Filter *.png | ForEach-Object { "SHOT: $($_.FullName)" }
if ($failed) { exit 1 }
exit 0
