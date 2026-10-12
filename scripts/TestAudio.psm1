# TestAudio.psm1 - stage audio test inputs into a fresh temp dir per suite.
#
# Import this module; importing never runs suite code. Suites must NEVER open
# D:\Audio originals directly (a harness once appended its report INTO an
# .m4a, destroying it): call New-TestAudioCopy first and open only copies.
#
#   Import-Module (Join-Path $Repo 'scripts\TestAudio.psm1')
#   $HoldFile = New-TestAudioCopy 'D:\Audio\alac.m4a'
#
# The originals are read-only on disk (+R). New-TestAudioCopy verifies each
# original against scripts/TestAudio.hashes BEFORE copying and FAILS on drift
# (corruption detector — do not "fix" by re-hashing; restore the file). If a
# source name is not in the manifest it is copied unverified with an
# UNPINNED note (ad-hoc inputs). Copies are writable; originals stay +R.
# Every staged file logs an AUDIO-HASH line (tamper-evident suite logs).
function New-TestAudioCopy {
    param([Parameter(Mandatory = $true)][string[]]$Source)
    $modRoot = $PSScriptRoot
    if (-not $modRoot) { $modRoot = Split-Path -Parent $PSCommandPath }
    $manifest = Join-Path $modRoot 'TestAudio.hashes'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "audio hash manifest missing: $manifest"
    }
    $pinned = @{}
    foreach ($line in (Get-Content -LiteralPath $manifest)) {
        $m = [regex]::Match($line, '^([0-9A-Fa-f]{64})\s+(.+)$')
        if ($m.Success) { $pinned[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value.ToUpperInvariant() }
    }
    $dir = Join-Path $env:TEMP ("amll-audio-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $copies = @()
    foreach ($src in $Source) {
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
            throw "audio source missing: $src"
        }
        $name = Split-Path -Leaf $src
        $hash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($pinned.ContainsKey($name)) {
            if ($hash -ne $pinned[$name]) {
                throw "AUDIO CORRUPT: $src sha256=$hash want=$($pinned[$name]) (restore the original, do not re-hash)"
            }
            Write-Host "AUDIO-HASH $hash $name (pinned, ok)"
        } else {
            Write-Host "AUDIO-HASH $hash $name (UNPINNED ad-hoc input)"
        }
        $dst = Join-Path $dir $name
        Copy-Item -LiteralPath $src -Destination $dst -Force
        Set-ItemProperty -LiteralPath $dst -Name IsReadOnly -Value $false
        $copies += $dst
    }
    if ($copies.Count -eq 1) { return $copies[0] }
    return $copies
}

Export-ModuleMember -Function New-TestAudioCopy
