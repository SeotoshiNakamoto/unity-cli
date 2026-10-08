param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
New-Item -ItemType Directory (Split-Path $Output) -Force | Out-Null
$r=Invoke-HiddenProcess -File 'go.exe' -Arguments @('build','-o',$Output,'.') -WorkingDirectory (Resolve-Path "$PSScriptRoot/../..").Path
$r.stdout
Write-Output "PASS: temporary CLI built at $Output; installed binary untouched"
