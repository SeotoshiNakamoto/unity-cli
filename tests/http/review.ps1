param([string]$Phase,[Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$Cli)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$env:UNITY_HTTP_TEST_CLI=$Cli
$r=Invoke-HiddenProcess -File 'python.exe' -Arguments @("$PSScriptRoot/review.py",$Phase,$OutputDirectory)
$r.stdout
if($r.stderr){$r.stderr}
