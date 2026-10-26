$ErrorActionPreference = 'Stop'
$clsid = '{874bf2d7-62ec-4d0b-be18-59fd27596d37}'

if (Get-Process mstsc -ErrorAction SilentlyContinue) {
    throw 'Close all Remote Desktop (mstsc.exe) windows first.'
}
Remove-Item 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\RdHaptic' -Recurse -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Classes\CLSID\$clsid" -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'RdHaptic\client') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'RdHaptic client plugin removed.'
