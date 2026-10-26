<#
.SYNOPSIS
  Installs the RdHaptic agent on the remote PC (per user, no admin), starts it, and runs it at every logon.
  Run from the dist\agent folder inside your RDP session.
#>
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'RdHaptic\agent'

Get-Process RdHapticAgent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'RdHapticAgent.exe'), (Join-Path $PSScriptRoot 'haptic.ps1'), (Join-Path $PSScriptRoot 'uninstall-agent.ps1') $dest -Force
$exe = Join-Path $dest 'RdHapticAgent.exe'
Unblock-File $exe

Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RdHapticAgent' -Value "`"$exe`""
Start-Process $exe
Start-Sleep -Seconds 3

Write-Host "Installed to $dest (starts at logon)."
Write-Host "Config: $env:APPDATA\RdHaptic\agent.json   Log: $env:LOCALAPPDATA\RdHaptic\agent.log"
& (Join-Path $dest 'haptic.ps1') status
