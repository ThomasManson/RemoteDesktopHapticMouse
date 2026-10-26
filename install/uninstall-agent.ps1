$ErrorActionPreference = 'Stop'
Get-Process RdHapticAgent -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RdHapticAgent' -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:LOCALAPPDATA 'RdHaptic\agent') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'RdHaptic agent removed (settings in %APPDATA%\RdHaptic kept).'
