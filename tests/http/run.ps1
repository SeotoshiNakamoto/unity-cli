param([ValidateSet('baseline','regression','stress','lifetime','baseline_lifetime','screen','compare','smoke')][string]$Phase, [Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$Cli)
$ErrorActionPreference='Stop'
if($Cli){$env:UNITY_HTTP_TEST_CLI=$Cli}
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$r=Invoke-HiddenProcess -File 'python.exe' -Arguments @("$PSScriptRoot/check.py",$Phase,$OutputDirectory)
$r.stdout
if($r.stderr){$r.stderr}
