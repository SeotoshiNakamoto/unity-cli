param([Parameter(Mandatory=$true)][string]$Phase)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$env:UNITY_HTTP_TEST_CLI='D:/tmp/http-split/unity-cli-new.exe'
$env:PYTHONDONTWRITEBYTECODE='1'
$r=Invoke-HiddenProcess -File python.exe -Arguments @("$PSScriptRoot/split-run.py",$Phase,'D:/tmp/http-split/artifacts')
$r.stdout
if($r.stderr){$r.stderr}
