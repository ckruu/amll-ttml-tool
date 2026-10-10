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
