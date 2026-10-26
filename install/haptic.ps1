<#
.SYNOPSIS
  Talk to the running RdHaptic agent over \\.\pipe\RdHaptic.
.EXAMPLE
  haptic.ps1 play success        # trigger a pulse (e.g. at the end of a build script)
  haptic.ps1 play align 50
  haptic.ps1 ping                # RDP round-trip latency to the client plugin
  haptic.ps1 status
#>
param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]] $Command)

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'RdHaptic', [System.IO.Pipes.PipeDirection]::InOut)
try {
    $pipe.Connect(2000)
} catch {
    Write-Error 'RdHaptic agent is not running in this session.'
    exit 2
}
$writer = [System.IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
$reader = [System.IO.StreamReader]::new($pipe)
$writer.WriteLine($Command -join ' ')
$reply = $reader.ReadLine()
$pipe.Dispose()
$reply
if ($reply -notlike 'ok*') { exit 1 }
