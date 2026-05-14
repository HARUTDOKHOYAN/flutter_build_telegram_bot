# BuildChatBot installer — Windows dispatcher.
# Usage:
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -NonInteractive
#   powershell -ExecutionPolicy Bypass -File install.ps1 -SkipDeps
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Force
#   powershell -ExecutionPolicy Bypass -File install.ps1 -WithNdk

[CmdletBinding()]
param(
    [switch]$NonInteractive,
    [switch]$SkipDeps,
    [switch]$Force,
    [switch]$WithNdk
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $here 'install\install-windows.ps1'
if (-not (Test-Path $script)) { throw "Missing $script" }

& $script `
    -NonInteractive:$NonInteractive `
    -SkipDeps:$SkipDeps `
    -Force:$Force `
    -WithNdk:$WithNdk
