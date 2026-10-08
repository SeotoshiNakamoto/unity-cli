param([ValidateSet('mark','check')][string]$Action,[string]$Label,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$r=Invoke-HiddenProcess -File 'python.exe' -Arguments @("$PSScriptRoot/log-check.py",$Action,$Label,$OutputDirectory)
$r.stdout
