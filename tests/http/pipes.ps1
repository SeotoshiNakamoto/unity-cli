$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$r=Invoke-HiddenProcess -File 'python.exe' -Arguments @('-c','import sys; sys.stderr.write("e" * 131072); sys.stdout.write("o" * 131072)')
if($r.stdout.Length -ne 131072 -or $r.stderr.Length -ne 131072){throw 'Hidden process truncated or blocked a pipe'}
try {
    Invoke-HiddenProcess -File 'python.exe' -Arguments @('-c','import sys; sys.stderr.write("e" * 131072); sys.stdout.write("o" * 131072); sys.exit(3)') | Out-Null
    throw 'Nonzero exit was not reported'
} catch {
    if(!$_.Exception.Message.StartsWith('Hidden process failed:') -or $_.Exception.Message.Length -lt 262144){throw}
}
Write-Output 'PASS: drained 128 KiB on each pipe for success and failure; no deadlock/truncation, nonzero exit reported'
