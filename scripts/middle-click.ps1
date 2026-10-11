# scripts/middle-click.ps1 — real middle-button click at screen coordinates.
# winapp ui has no middle-click verb; the speed button's middle-reset path
# needs genuine pointer input. Uses SetCursorPos + mouse_event.
param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [int]$SettleMs = 150,
    [int]$DownMs = 60
)

$ErrorActionPreference = 'Stop'
if (-not ('W32Mouse' -as [type])) {
    $src = @"
using System.Runtime.InteropServices;
public static class W32Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
}
"@
    $null = Add-Type -TypeDefinition $src
}
[W32Mouse]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds $SettleMs
[W32Mouse]::mouse_event(0x20, 0, 0, 0, [System.UIntPtr]::Zero)  # MIDDLEDOWN
Start-Sleep -Milliseconds $DownMs
[W32Mouse]::mouse_event(0x40, 0, 0, 0, [System.UIntPtr]::Zero)  # MIDDLEUP
Write-Host "MIDDLE-CLICK: $X,$Y"
