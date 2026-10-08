param([ValidateSet('baseline_limit','candidate_limit','fixed_limit')][string]$Label,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$r=Invoke-HiddenProcess -File 'python.exe' -Arguments @("$PSScriptRoot/limit-check.py",$Label,$OutputDirectory)
$r.stdout
if($r.stderr){$r.stderr}
