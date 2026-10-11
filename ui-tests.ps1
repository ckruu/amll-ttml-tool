# ui-tests.ps1 - real-input seek/slider verification (BUG 2b).
# Drives the REAL Windows UI Automation tree of the built app on the local
# target: far-left/far-right clicks, a thumb drag that releases OUTSIDE the
# slider, Home/End keys. After each gesture reads the engine position and
# presses play when the gesture left it paused.
#
# Run: .\ui-tests.ps1  (requires the app built; launches --seek-ui-hold)
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

$AudioFile = if ($args.Count -ge 1) { $args[0] } else { 'D:\Audio\alac.m4a' }
$Artifacts = Join-Path $env:TEMP 'ui-test-artifacts'
if (Test-Path -LiteralPath $Artifacts) { Remove-Item -LiteralPath $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

# One workflow id for every cooperating command so a burst reads as one
# workflow to winapp's desktop arbitration.
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()

# ShowWindow P/Invoke for the minimize/restore legs of the UI tests.
if (-not ('W32Win' -as [type])) {
    $src = @"
using System.Runtime.InteropServices;
public static class W32Win {
    [DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
}
"@
    $null = Add-Type -TypeDefinition $src
}

function Invoke-WinAppChecked {
    param([string[]]$Arguments)
    $output = @(& winapp @Arguments 2>&1)
    $code = $LASTEXITCODE
    if ($null -eq $code -or $code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed (exit $code). $($output -join "`n")"
    }
    $output
}

# Plays only if the engine is not already playing. A second click on the Play
# button is a PAUSE, so a test that "ensures playing" must read the state first
# or it can silently pause a track that was already running.
function EnsurePlaying {
    if (-not (Invoke-Ui @('wait-for', 'PlayButton', '-p', 'IsEnabled', '--value', 'True', '-t', '3000'))) {
        throw 'PlayButton is disabled (no audio loaded?)'
    }
    $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
    $first = Parse-Stamp $v.text
    $paused = $true
    Start-Sleep -Milliseconds 250
    $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
    $second = Parse-Stamp $v.text
    if ($second -gt $first) { $paused = $false }
    if ($paused) { Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null }
    return (-not $paused)
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

$AppPid = 0
$proc = $null
try {
    # Start-Process -PassThru, never in the foreground of a command.
    $proc = Start-Process -FilePath $Exe -ArgumentList '--seek-ui-hold', $AudioFile -PassThru
    $AppPid = $proc.Id
    Write-Host "LAUNCH: pid=$AppPid file=$AudioFile"
    Start-Sleep -Seconds 8
    if ($proc.HasExited) { throw "app exited during startup (code $($proc.ExitCode))" }

    $hwnd = (Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json |
        Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.processId -eq $AppPid } |
        Select-Object -First 1).hwnd
    if (-not $hwnd) { throw "no app window for pid $AppPid" }
    Write-Host "WINDOW: hwnd=$hwnd"

    # Track duration, so every assertion is relative to it rather than guessed.
    $dur = Invoke-Ui @('get-value', 'DurationText', '--json') | ConvertFrom-Json
    function Parse-Stamp {
        param([string]$Text)
        $m = [regex]::Match($Text, '(\d+):(\d+)\.(\d+)')
        if (-not $m.Success) { throw "unparsable position text '$Text'" }
        return [int]$m.Groups[1].Value * 60000 + [int]$m.Groups[2].Value * 1000 + [int]$m.Groups[3].Value
    }
    $maxMs = Parse-Stamp $dur.text
    Write-Host "DURATION: $($dur.text) ($maxMs ms)"
    if ($maxMs -le 0) { throw "duration not loaded yet ($($dur.text))" }

    # Bring the window up and focus it so real input lands on the app.
    Invoke-Ui @('focus', 'SeekSlider') -Window $hwnd | Out-Null

    function Get-Engine {
        param([string]$Label)
        $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        $out = @{ label = $Label; text = $v.text }
        # engine truth from the same process via the slider's UIA value
        $s = Invoke-Ui @('get-value', 'SeekSlider', '--json') | ConvertFrom-Json
        $out.thumb = $s.value
        return $out
    }

    # UIA slider geometry is FLAT on the element (x/y/width/height in physical
    # screen pixels), not a nested bounds object. Screen coordinates must come
    # from here, never from window/client coordinates.
    # 'ui click' takes only element selectors — it cannot address a point on a
    # slider. A press+release at the same screen coordinate is how a real click
    # at an arbitrary position is simulated here.
    function ClickAt {
        param([int]$X, [int]$Y)
        $out = @(& winapp ui drag "$X,$Y" "$X,$Y" -w $hwnd 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "click at ${X},${Y} failed: $($out -join "`n")" }
    }

    function SliderBounds {
        $el = (Invoke-Ui @('inspect', 'SeekSlider', '--json') | ConvertFrom-Json).windows[0].elements[0]
        if (-not $el) { throw 'SeekSlider not found in the UIA tree' }
        Write-Host ("  slider: x={0} y={1} w={2} h={3}" -f $el.x, $el.y, $el.width, $el.height)
        return @{ x = [int]$el.x; y = [int]$el.y; width = [int]$el.width; height = [int]$el.height }
    }

    # --- Click the far-LEFT end: must land at the very beginning ----------
    Test-UI 'click track far left -> position at 0' {
        $r = SliderBounds
        ClickAt ([int]($r.x + $r.width * 0.01)) ([int]($r.y + $r.height / 2))
        Start-Sleep -Milliseconds 700
        $ms = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        "[far-left] pos=$($ms)ms" | Out-Null
        if ($ms -gt $maxMs * 0.05) { throw "far-left click left position at $ms (want < 5% = $($maxMs * 0.05)ms)" }
    }

    # --- Click the far-RIGHT end: must land near the end ------------------
    Test-UI 'click track far right -> position near duration' {
        $r = SliderBounds
        ClickAt ([int]($r.x + $r.width * 0.99)) ([int]($r.y + $r.height / 2))
        Start-Sleep -Milliseconds 700
        $ms = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        "[far-right] pos=$($ms)ms of $($maxMs)ms" | Out-Null
        if ($ms -lt $maxMs * 0.9) { throw "far-right click left position at $ms (want > 90% = $($maxMs * 0.9)ms)" }
    }

    # --- Drag past the LEFT end and release OUTSIDE the slider ------------
    # Starts from a known middle position, so a pass proves the release really
    # committed the new value rather than leaving the old one in place.
    Test-UI 'drag past left end, release outside slider -> position at 0' {
        $r = SliderBounds
        $from = "$([int]($r.x + $r.width * 0.6)),$([int]($r.y + $r.height / 2))"
        $to = "$([int]($r.x - 250)),$([int]($r.y + $r.height / 2))"
        $out = @(& winapp ui drag "$from" "$to" -w $hwnd 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "drag failed: $($out -join "`n")" }
        Start-Sleep -Milliseconds 700
        $ms = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        "[drag-out-left] pos=$($ms)ms" | Out-Null
        if ($ms -gt $maxMs * 0.05) { throw "release outside the left end left position at $ms (want < 5%)" }
    }

    # --- Drag past the RIGHT end and release OUTSIDE the slider -----------
    Test-UI 'drag past right end, release outside slider -> position near end' {
        $r = SliderBounds
        $from = "$([int]($r.x + $r.width * 0.3)),$([int]($r.y + $r.height / 2))"
        $to = "$([int]($r.x + $r.width + 250)),$([int]($r.y + $r.height / 2))"
        $out = @(& winapp ui drag "$from" "$to" -w $hwnd 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "drag failed: $($out -join "`n")" }
        Start-Sleep -Milliseconds 700
        $ms = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        "[drag-out-right] pos=$($ms)ms of $($maxMs)ms" | Out-Null
        if ($ms -lt $maxMs * 0.9) { throw "release outside the right end left position at $ms (want > 90%)" }
    }

    # --- Keyboard Home / End --------------------------------------------
    Test-UI 'Home key -> position 0' {
        Invoke-Ui @('send-keys', 'Home', '--target', 'SeekSlider', '--via', 'send-input') -Window $hwnd | Out-Null
        Start-Sleep -Milliseconds 700
        $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        "[home] pos=$($v.text)" | Out-Null
        if ((Parse-Stamp $v.text) -gt $maxMs * 0.02) { throw "Home left position at $($v.text) (want 00:00.000)" }
    }

    Test-UI 'End key -> position near duration' {
        Invoke-Ui @('send-keys', 'End', '--target', 'SeekSlider', '--via', 'send-input') -Window $hwnd | Out-Null
        Start-Sleep -Milliseconds 700
        $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        $ms = Parse-Stamp $v.text
        "[end] pos=$($v.text)" | Out-Null
        if ($ms -lt $maxMs * 0.9) { throw "End left position at $($v.text) (want > 90%)" }
    }

    # --- THE reported symptom: seek to the beginning, then play -----------
    Test-UI 'seek to beginning then Play starts from the beginning' {
        Invoke-Ui @('send-keys', 'Home', '--target', 'SeekSlider', '--via', 'send-input') -Window $hwnd | Out-Null
        Start-Sleep -Milliseconds 500
        $before = (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        # Self-calibrating bound: the engine itself replays from the first
        # sample within 500ms (measured 0.48s at 1x by --seek-test), so the
        # UI-side allowance only has to cover the UIA invoke + dispatcher
        # latency of reading through the tree. A seek that did NOT happen
        # would still be sitting at ~3 minutes and fails this by a mile.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null
        Start-Sleep -Milliseconds 500
        $after = (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        $sw.Stop()
        $ms = Parse-Stamp $after
        $bound = $sw.ElapsedMilliseconds + 1000
        "[home-then-play] $before -> $after (elapsed=$($sw.ElapsedMilliseconds)ms, bound=${bound}ms)" | Out-Null
        if ($ms -gt $bound) { throw "play after Home began at $after, not the beginning (bound ${bound}ms)" }
    }

    # --- Play resumes and advances from a MIDDLE position -----------------
    Test-UI 'Play advances from a middle position' {
        $r = SliderBounds
        $x = $r.x + [int]($r.width * 0.5)
        $y = $r.y + [int]($r.height / 2)
        & winapp ui click "$x,$y" -w $hwnd | Out-Null
        Start-Sleep -Milliseconds 500
        $a = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        Start-Sleep -Seconds 1
        $b = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        "[mid-play] $($a.text) -> $($b.text)" | Out-Null
        if ((Parse-Stamp $b.text) -le (Parse-Stamp $a.text)) {
            throw "position frozen while playing: $($a.text) -> $($b.text)"
        }
    }

    # --- NEW: the ms counter must follow the thumb MID-DRAG, before release --
    # Two separate real suspicions:
    #  * throttled live seeks leave the engine one 100ms step behind the thumb;
    #  * while playing, every live SeekTo restarts the output and arms the
    #    startup gate, so the clock snapshot is flat at the pre-drag value.
    # Either way the text used to sit still until the pointer was released.
    Test-UI 'counter follows thumb during drag (before release)' {
        $r = SliderBounds
        $from = "$([int]($r.x + $r.width * 0.2)),$([int]($r.y + $r.height / 2))"
        $to = "$([int]($r.x + $r.width * 0.8)),$([int]($r.y + $r.height / 2))"
        # hold-ms keeps the button pressed after arriving, giving a real window
        # in which to read the counter before any release.
        $out = @(& winapp ui drag $from $to -w $hwnd --hold-ms 1200 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "drag failed: $($out -join "`n")" }
        Start-Sleep -Milliseconds 400
        $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        $ms = Parse-Stamp $v.text
        "[mid-drag] pos=$($ms)ms" | Out-Null
        # Dragging to the 80% mark must be visible mid-drag: the reported value
        # has to sit in the upper half, not back near the pre-drag position.
        if ($ms -lt $maxMs * 0.4) {
            throw "counter read $($ms)ms mid-drag (want > 40% = $($maxMs * 0.4)ms) - still tracking the old clock"
        }
    }

    # --- NEW: the same, while the song is PLAYING --------------------------
    # This is the reported symptom: seeking while playing used to keep running
    # the old position until release.
    Test-UI 'counter follows thumb during drag while playing' {
        Invoke-Ui @('invoke', 'PlayButton') -Window $hwnd | Out-Null
        Start-Sleep -Milliseconds 600
        $r = SliderBounds
        $from = "$([int]($r.x + $r.width * 0.75)),$([int]($r.y + $r.height / 2))"
        $to = "$([int]($r.x + $r.width * 0.1)),$([int]($r.y + $r.height / 2))"
        $out = @(& winapp ui drag $from $to -w $hwnd --hold-ms 1500 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "drag failed: $($out -join "`n")" }
        Start-Sleep -Milliseconds 300
        $v = Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json
        $ms = Parse-Stamp $v.text
        "[mid-drag-playing] pos=$($ms)ms" | Out-Null
        # Dragged backwards to the 10% mark: must be in the lower half. A clock
        # that kept running from the pre-drag value would report far more.
        if ($ms -gt $maxMs * 0.35) {
            throw "counter read $($ms)ms mid-drag while playing (want < 35% = $($maxMs * 0.35)ms) - clock kept running"
        }
    }

    # --- NEW: display updates while the window is NOT focused ---------------
    # CompositionTarget.Rendering never fires for a non-foreground window, so
    # this is exactly the case that used to stall. The counter must keep
    # advancing with the app in the background.
    Test-UI 'counter advances while app is unfocused' {
        # Precondition: playback must actually be running, or the clock is
        # legitimately paused and this test would assert against nothing.
        EnsurePlaying
        Start-Sleep -Milliseconds 900
        $f1 = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        Start-Sleep -Milliseconds 900
        $f2 = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        if ($f2 -le $f1 + 200) { throw "precondition failed: not advancing while focused ($f1 -> $f2)" }
        # Steal focus so our window deactivates, then watch it keep moving.
        $other = Start-Process -FilePath 'notepad.exe' -PassThru
        try {
            Start-Sleep -Milliseconds 1200
            $a = (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
            Start-Sleep -Seconds 2
            $b = (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        } finally {
            try { Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        "[unfocused] $a -> $b" | Out-Null
        $am = Parse-Stamp $a; $bm = Parse-Stamp $b
        if ($bm -le $am + 300) {
            throw "counter stalled while unfocused: $a -> $b (want > 300ms of advance)"
        }
    }

    # --- NEW: display updates while the window is MINIMIZED -----------------
    # The dispatcher queue keeps pumping while iconic (proven by the minimize
    # harness round-trips), so the timer must carry the display through it.
    Test-UI 'counter advances while app is minimized' {
        # Play only if not already playing: the previous test leaves it playing,
        # and a second click on Play is a PAUSE.
        EnsurePlaying
        Start-Sleep -Milliseconds 900
        $f1 = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        Start-Sleep -Milliseconds 900
        $f2 = Parse-Stamp (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        if ($f2 -le $f1 + 200) { throw "precondition failed: not advancing focused ($f1 -> $f2)" }
        [W32Win]::ShowWindow([System.IntPtr]$hwnd, 6) | Out-Null   # SW_MINIMIZE
        Start-Sleep -Seconds 1
        $a = (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        Start-Sleep -Seconds 2
        $b = (Invoke-Ui @('get-value', 'PositionText', '--json') | ConvertFrom-Json).text
        [W32Win]::ShowWindow([System.IntPtr]$hwnd, 9) | Out-Null   # SW_RESTORE
        Start-Sleep -Milliseconds 500
        "[minimized] $a -> $b" | Out-Null
        $am = Parse-Stamp $a; $bm = Parse-Stamp $b
        if ($bm -le $am + 300) {
            throw "counter stalled while minimized: $a -> $b (want > 300ms of advance)"
        }
    }

    # --- Visual evidence -------------------------------------------------
    Test-UI 'screenshots delivered to host' {
        foreach ($n in '01-after-clicks.png', '02-after-keys.png') {
            $p = Join-Path $Artifacts $n
            Invoke-Ui @('screenshot', '--output', $p) -Window $hwnd | Out-Null
            if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) {
                throw "screenshot not delivered: $p"
            }
        }
        Get-ChildItem -LiteralPath $Artifacts -Filter *.png | ForEach-Object { $_.FullName } | Out-Null
    }
}
catch {
    $results.Add(@{ name = 'setup / driving'; status = 'FAIL'; detail = "$_" })
}
finally {
    try {
        if ($AppPid -gt 0) { & winapp ui yield --on local | Out-Null }
    } catch { }
    try { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $AppPid -Force } } catch { }
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = @{ target = 'local'; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed; results = @($results.ToArray()) }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'test-results.json') -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: $(Join-Path $Artifacts 'test-results.json')"
Get-ChildItem -LiteralPath $Artifacts -Filter *.png | ForEach-Object { "SHOT: $($_.FullName)" }
if ($failed) { exit 1 }
exit 0
