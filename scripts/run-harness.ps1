#!/usr/bin/env pwsh
# scripts/run-harness.ps1 — THE one way to run app harnesses. Never launch
# the app any other way.
#
# ARGUMENT PASSING (verified): the mode is passed BOTH as a command-line
# argument (--<mode>) and as environment variables (AMLL_HARNESS_MODE /
# AMLL_HARNESS_REPORT / AMLL_HARNESS_TIMEOUT). The env vars are the
# authoritative path: `winapp run ... -- <args>` and Start-Process both
# forward args, but launcher wrapping has proven unreliable in this
# project, so the app reads env first. This script launches the built
# exe directly (no launcher) so exit codes and stdout/stderr are ours.
#
# The run is controlled from OUTSIDE: the in-app harness is never trusted
# to end it. The app writes "<report>.started" (handshake) within 15s and
# the watchdog/hard-kill guarantees termination.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Mode,
    [string[]]$Args = @(),
    [string]$ReportPath,
    [int]$TimeoutSec = 90,
    [int]$HandshakeSec = 15,
    [switch]$Detach,
    [string]$DetachLog = (Join-Path $env:TEMP "harness-detached-$([guid]::NewGuid().ToString('N')).log")
)

$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProcName = 'AmllTtmlTool.WinUI'
# Resolve the FRESHEST real build output. A plain `dotnet build -c Debug` on
# this project writes to bin\Debug\<tfm>\ (no RID subdir); an explicit
# -r/-arch build writes the ...\win-x64\ sibling. Whichever is newer is the
# one that must be launched and timestamp-checked (rule 7).
$BuildRoot = Join-Path $Repo 'winui\AmllTtmlTool.WinUI\bin\Debug\net8.0-windows10.0.19041.0'
$Candidates = @(
    (Join-Path $BuildRoot 'win-x64\AmllTtmlTool.WinUI.exe'),
    (Join-Path $BuildRoot 'AmllTtmlTool.WinUI.exe')
) | Where-Object { Test-Path -LiteralPath $_ }
if (-not $Candidates) {
    Write-Host "NOT-WIRED: no built app exe under $BuildRoot (build first)"
    exit 2
}
$Exe = $Candidates | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Select-Object -Last 1
Write-Host "EXE-RESOLVED: $Exe (newest of $($Candidates.Count) candidate output(s))"
$GraceSec = 5

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "NOT-WIRED: app exe not found: $Exe (build first)"
    exit 2
}
if (-not $ReportPath) { $ReportPath = Join-Path $env:TEMP "$Mode-report.txt" }

$marker = "$ReportPath.started"
$outFile = Join-Path $env:TEMP "harness-$Mode-$PID.out.txt"
$errFile = Join-Path $env:TEMP "harness-$Mode-$PID.err.txt"
foreach ($f in @($ReportPath, $marker, $outFile, $errFile)) {
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
}

# --- Detached long runs -------------------------------------------------
# Placed AFTER the report path is resolved and the stale report is cleared,
# so the detached run owns exactly one clean report. Runs in a background
# process that survives the caller's shell timeout; the caller polls for the
# VERDICT line. Background output must NOT be redirected inside that process:
# redirecting stdout made the caller wait for the pipe to close, which is
# exactly how earlier attempts hung for the full timeout.
if ($Detach) {
    $outerExe = $Exe
    $outerReport = $ReportPath
    $outerMode = $Mode
    $outerTimeout = $TimeoutSec
    $argText = ($Args | ForEach-Object { "'$_'" }) -join ' '
    $inner = Join-Path $env:TEMP "run-harness-inner-$([guid]::NewGuid().ToString('N')).ps1"
    $body = @"
`$ErrorActionPreference = 'Stop'
`$env:AMLL_HARNESS_MODE = '$outerMode'
`$env:AMLL_HARNESS_REPORT = '$outerReport'
`$env:AMLL_HARNESS_TIMEOUT = '$outerTimeout'
& '$outerExe' --$outerMode $argText
"@
    Set-Content -LiteralPath $inner -Value $body -Encoding utf8
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'pwsh'
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$inner`""
    $psi.UseShellExecute = $true
    $psi.WindowStyle = 'Hidden'
    $proc = [System.Diagnostics.Process]::Start($psi)
    Write-Host "DETACHED: harness pid=$($proc.Id) report=$outerReport"
    Write-Host "POLL: wait for a VERDICT line in $outerReport"
    exit 0
}

$startedAt = Get-Date
# Pass the mode to the app as BOTH arg and env var.
$env:AMLL_HARNESS_MODE = $Mode
$env:AMLL_HARNESS_REPORT = $ReportPath
$env:AMLL_HARNESS_TIMEOUT = "$TimeoutSec"
$argList = @("--$Mode") + $Args

function Stop-AppTree {
    param([string]$Reason, [int]$Code)
    try {
        $launched = Get-Process -Name $ProcName -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -gt (Get-Date).AddSeconds(-3600) }
        Get-CimInstance Win32_Process -Filter "Name = '$ProcName.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CreationDate -ge $startedAt } |
            ForEach-Object {
                try { taskkill /PID $_.ProcessId /T /F 2>&1 | Out-Null } catch { }
            }
    }
    catch { }
    Write-Host "CLEANUP: killed leftover app processes ($Reason)"
}

function Read-Tail {
    param([string]$Path, [int]$Lines = 5)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return '<missing>' }
        return ((Get-Content -LiteralPath $Path -Tail $Lines) -join ' | ')
    }
    catch { return '<unreadable>' }
}

# Waits for a file to appear (and be written after $Since) up to a
# timeout. Pure polling, never blocks the shell.
function Wait-ForFile {
    param([string]$Path, [int]$TimeoutSec, [datetime]$Since = (Get-Date))
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        if (Test-Path -LiteralPath $Path) {
            try {
                $fi = Get-Item -LiteralPath $Path
                if ($fi.LastWriteTime -ge $Since) { return $fi.FullName }
            }
            catch { }
        }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 250
    }
}

$verdict = 'NOT-WIRED'
$exitCode = 2
$proc = $null

try {
    Write-Host "RUN: mode=$Mode report=$ReportPath timeout=${TimeoutSec}s exe=$Exe"
    $psi = @{
        FilePath               = $Exe
        ArgumentList           = $argList
        PassThru               = $true
        RedirectStandardOutput = $outFile
        RedirectStandardError  = $errFile
        WindowStyle            = 'Hidden'
    }
    $launcher = Start-Process @psi
    $launcherPid = $launcher.Id
    Write-Host "LAUNCH: exe pid=$launcherPid"

    # (d) handshake: the app must write the marker very early, before the
    # window exists. Without it the wiring is broken — kill and say so.
    $deadline = (Get-Date).AddSeconds($HandshakeSec)
    $found = $null
    while ((Get-Date) -lt $deadline -and -not $found) {
        Start-Sleep -Milliseconds 250
        $found = Get-CimInstance Win32_Process -Filter "Name = '$ProcName.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CreationDate -ge $startedAt } | Select-Object -First 1
    }
    if (-not $found) {
        Write-Host "HARNESS NOT WIRED: the app launched but no app process was found"
        Write-Host "  exit-code-info: process(name=$ProcName) not started after $startedAt"
        Stop-AppTree 'not-wired' 2
        exit 2
    }
    $appPid = $found.ProcessId
    Write-Host "HANDSHAKE: app process pid=$appPid (tracked by name+start time, not launcher pid)"

    if (-not (Wait-ForFile -Path $marker -TimeoutSec $HandshakeSec -Since $startedAt)) {
        Write-Host "HARNESS NOT WIRED: the app launched but never entered test mode"
        Write-Host "  launcher pid=$launcherPid exit=(see below)"
        Write-Host "  stdout: $(Read-Tail $outFile 5)"
        Write-Host "  stderr: $(Read-Tail $errFile 5)"
        $lp = Get-Process -Id $launcherPid -ErrorAction SilentlyContinue
        if ($lp) { Write-Host "  launcher still running (this is why waits hung before)" }
        Stop-AppTree 'no-marker' 2
        exit 2
    }
    $markerInfo = Get-Content -LiteralPath $marker -ErrorAction SilentlyContinue
    Write-Host "MARKER:"; $markerInfo | ForEach-Object { Write-Host "  $_" }
    $exeUtc = (Get-Item -LiteralPath $Exe).LastWriteTimeUtc.ToString('u')
    Write-Host "BUILD: tested exe $exeUtc (marker says the same above)"
    $markerBuild = ($markerInfo | Where-Object { $_ -like 'buildUtc=*' }) -replace 'buildUtc='
    if ($markerBuild -and $markerBuild -ne $exeUtc) {
        Write-Host "WARNING: tested exe timestamp ($exeUtc) != marker buildUtc ($markerBuild)"
    }

    # (e) wait for the report to COMPLETE, polling every 500ms, up to
    # -TimeoutSec. "Seen" means a VERDICT line exists and was written after
    # the start time — NOT merely that some bytes appeared: a harness that
    # is still writing must never be killed mid-run.
    $reportComplete = $false
    $waitDeadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $waitDeadline) {
        if (Test-Path -LiteralPath $ReportPath) {
            $fi = Get-Item -LiteralPath $ReportPath
            if ($fi.LastWriteTime -gt $startedAt) {
                $body = Get-Content -LiteralPath $ReportPath -ErrorAction SilentlyContinue
                if ($body | Where-Object { $_ -like 'VERDICT:*' }) { $reportComplete = $true; break }
            }
        }
        Start-Sleep -Milliseconds 500
    }

    # Track the app's own exit code: Start-Process -PassThru gives us the
    # process object, but a GUI exe launched redirected needs the object
    # kept alive to read ExitCode. Keep it and poll with HasExited.
    $appProc = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if (-not $appProc) { $appProc = $launcher }
    $exitSeen = $null
    $exitWatch = (Get-Date).AddSeconds(($TimeoutSec + $GraceSec + 5))
    while ((Get-Date) -lt $exitWatch) {
        try {
            if ($appProc.HasExited) { $exitSeen = $appProc.ExitCode; break }
        }
        catch { break }
        Start-Sleep -Milliseconds 500
    }

    if ($exitSeen -ne $null -and $exitSeen -ne 0) {
        Write-Host "APP EXITED: exit code=$exitSeen (non-zero)"
    }
    elseif ($exitSeen -eq 0) {
        Write-Host "APP EXITED: clean (code 0)"
    }
    else {
        Write-Host "APP EXITED: still running / not tracked"
    }

    if ($reportComplete) {
        Write-Host "REPORT: seen at $((Get-Item -LiteralPath $ReportPath).LastWriteTime.ToString('u'))"
        # 5s to exit on its own, then kill the whole tree regardless.
        $alive = $true
        try { $alive = [bool](Get-Process -Id $appPid -ErrorAction SilentlyContinue) } catch { $alive = $false }
        if ($alive) {
            $graceDeadline = (Get-Date).AddSeconds($GraceSec)
            while ((Get-Date) -lt $graceDeadline) {
                Start-Sleep -Milliseconds 500
                try { if (-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) { $alive = $false; break } } catch { $alive = $false; break }
            }
        }
        if ($alive) { Write-Host "STILL-ALIVE: app did not exit within ${GraceSec}s; killing" }
        Stop-AppTree 'done' 0

        $last = Read-Tail $ReportPath 3
        $body = Get-Content -LiteralPath $ReportPath -ErrorAction SilentlyContinue
        $vline = $body | Where-Object { $_ -like 'VERDICT:*' } | Select-Object -Last 1
        if ($vline -like '*TIMEOUT*') { $verdict = 'TIMEOUT'; $exitCode = 3 }
        elseif ($vline -like '*PASS*') { $verdict = 'PASS'; $exitCode = 0 }
        elseif ($vline -like '*FAIL*') { $verdict = 'FAIL'; $exitCode = 1 }
        elseif ($body -match 'HARNESS-ERROR') { $verdict = 'FAIL'; $exitCode = 1 }
        else { $verdict = 'FAIL'; $exitCode = 1 }
        Write-Host "REPORT-LAST: $last"
        if ($verdict -eq 'FAIL') { Write-Host "  report: $ReportPath" }
        if ($vline) { Write-Host "  verline: $vline" }
    }
    else {
        Write-Host "TIMEOUT: killed after $TimeoutSec s; last marker/report state:"
        $lp = Get-Process -Id $appPid -ErrorAction SilentlyContinue
        if ($lp) {
            Write-Host "  app process pid=$appPid was ALIVE (hung) - killed"
        }
        else {
            Write-Host "  app process pid=$appPid had already exited (exit code unknown to us)"
        }
        Write-Host "  marker: $(if (Test-Path $marker) { 'present' } else { 'MISSING' })"
        Write-Host "  report: $(if (Test-Path $ReportPath) { Read-Tail $ReportPath 5 } else { 'not written' })"
        Write-Host "  stdout: $(Read-Tail $outFile 5)"
        Write-Host "  stderr: $(Read-Tail $errFile 5)"
        if ((Test-Path $ReportPath) -and ((Get-Content $ReportPath -ErrorAction SilentlyContinue) -match 'TIMEOUT')) {
            $verdict = 'TIMEOUT'; $exitCode = 3
        }
        else {
            $verdict = 'TIMEOUT'; $exitCode = 3
        }
        Stop-AppTree 'timeout' 3
    }
}
finally {
    Stop-AppTree 'final' 0
    Remove-Item Env:AMLL_HARNESS_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:AMLL_HARNESS_REPORT -ErrorAction SilentlyContinue
    Remove-Item Env:AMLL_HARNESS_TIMEOUT -ErrorAction SilentlyContinue
}

# (h) one clear final verdict.
$color = switch ($verdict) { 'PASS' { 'Green' } default { 'Red' } }
Write-Host "VERDICT[$Mode]: $verdict" -ForegroundColor $color
exit $exitCode
