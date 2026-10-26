<#
.SYNOPSIS
  Installs the RdHaptic DVC plugin for the classic Remote Desktop client (mstsc.exe), per user, no admin.
  Run from the dist\client folder on the PC that has the haptic mouse.
#>
$ErrorActionPreference = 'Stop'
$clsid = '{874bf2d7-62ec-4d0b-be18-59fd27596d37}'
$dest = Join-Path $env:LOCALAPPDATA 'RdHaptic\client'

$mstscRunning = [bool](Get-Process mstsc -ErrorAction SilentlyContinue)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (dotnet --list-runtimes | Select-String 'Microsoft.NETCore.App 10\.')) {
    throw '.NET 10 runtime (x64) is required: https://dotnet.microsoft.com/download/dotnet/10.0'
}

New-Item -ItemType Directory -Force $dest | Out-Null
try {
    Copy-Item (Join-Path $PSScriptRoot '*') $dest -Recurse -Force -Exclude 'install-client.ps1'
} catch {
    throw "Could not update $dest (an open Remote Desktop window has the plugin loaded). Close all mstsc.exe windows and retry. $_"
}
$comhost = Join-Path $dest 'RdHaptic.Client.comhost.dll'

# COM class (per user)
$classKey = "HKCU:\Software\Classes\CLSID\$clsid"
New-Item -Force $classKey | Set-ItemProperty -Name '(default)' -Value 'RdHaptic.Plugin'
$inproc = New-Item -Force "$classKey\InprocServer32"
$inproc | Set-ItemProperty -Name '(default)' -Value $comhost
$inproc | Set-ItemProperty -Name 'ThreadingModel' -Value 'Both'

# Register as an mstsc dynamic virtual channel add-in
New-Item -Force 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\RdHaptic' |
    Set-ItemProperty -Name 'Name' -Value $clsid

& (Join-Path $dest 'hapticctl.exe') comtest
if ($LASTEXITCODE) { throw 'COM activation test failed' }

Write-Host "Installed to $dest. Log: $env:LOCALAPPDATA\RdHaptic\client.log"
if ($mstscRunning) {
    Write-Warning 'Remote Desktop is running: plugins load when mstsc.exe starts, so close and reopen your RDP windows.'
}
