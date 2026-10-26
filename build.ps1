<#
.SYNOPSIS
  Builds, tests and packages both halves into .\dist:
    dist\client  -> install on the PC you RDP *from* (has the haptic mouse)
    dist\agent   -> copy to the remote PC you RDP *into*
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test tests/RdHaptic.Tests -c Release --nologo -v q
if ($LASTEXITCODE) { throw 'Tests failed' }

Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue

# Client: framework-dependent (.NET 10 runtime must be installed locally); the comhost shim loads it into mstsc.exe.
dotnet publish src/RdHaptic.Client -c Release -o dist/client --nologo -v q
dotnet publish src/hapticctl -c Release -o dist/client --nologo -v q
if ($LASTEXITCODE) { throw 'Client publish failed' }
Copy-Item install/install-client.ps1, install/uninstall-client.ps1 dist/client

# Agent: self-contained single file, so the remote PC needs no .NET runtime.
dotnet publish src/RdHaptic.Agent -c Release -o dist/agent --nologo -v q -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE) { throw 'Agent publish failed' }
Get-ChildItem dist/agent -Exclude RdHapticAgent.exe | Remove-Item -Recurse -Force
Copy-Item install/install-agent.ps1, install/uninstall-agent.ps1, install/haptic.ps1 dist/agent

Get-ChildItem dist -Recurse -File | Select-Object @{n='File';e={$_.FullName.Substring($PSScriptRoot.Length + 1)}}, @{n='KB';e={[int]($_.Length / 1KB)}}
