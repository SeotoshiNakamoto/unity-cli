$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$root=(Resolve-Path "$PSScriptRoot/../..").Path
Set-Location $root
foreach($entry in @(
    @{file='go.exe';args=@('clean','-testcache')},
    @{file='gofmt.exe';args=@('-w','.')},
    @{file="$env:USERPROFILE/go/bin/golangci-lint.exe";args=@('run','./...')},
    @{file="$env:USERPROFILE/go/bin/golangci-lint.exe";args=@('fmt','--diff')},
    @{file='go.exe';args=@('test','./...')}
)) {
    $r=Invoke-HiddenProcess -File $entry.file -Arguments $entry.args -WorkingDirectory $root
    $r.stdout
    if($r.stderr){$r.stderr}
}
$r=Invoke-HiddenProcess -File 'git.exe' -Arguments @('diff','--check') -WorkingDirectory $root
$r.stdout
Write-Output 'PASS: required Verification; lint executed by hidden PowerShell'
