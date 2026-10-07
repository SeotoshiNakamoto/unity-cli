param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/hidden.ps1"
$cli = 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe'
$project = 'D:/Projects/ProjectD/client'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function Invoke-UnityProbe([string[]]$argsList) { Invoke-HiddenProcess -File $cli -Arguments (@('--project', $project) + $argsList) }
function Measure-RoundTrips([string]$label) {
    $rows = New-Object 'Collections.Generic.List[object]'
    foreach ($command in @('console', 'exec')) {
        $arguments = if ($command -eq 'console') { @('console', '--lines', '1', '--stacktrace', 'none') } else { @('exec', 'return 1;') }
        Invoke-UnityProbe $arguments | Out-Null
        Invoke-UnityProbe $arguments | Out-Null
        for ($i = 0; $i -lt 20; $i++) {
            $r = Invoke-UnityProbe $arguments
            if ($command -eq 'exec' -and $r.stdout.Trim() -ne '1') { throw 'Unexpected exec response' }
            $rows.Add(@{ command = $command; ms = $r.ms; stderr = $r.stderr })
        }
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$label-roundtrips.json"), ($rows | ConvertTo-Json))
}
$reloadFile = Join-Path ([IO.Path]::GetTempPath()) ('discovery-reload-' + [Guid]::NewGuid() + '.cs')
try {
    [IO.File]::WriteAllText($reloadFile, "EditorUtility.RequestScriptReload();`nreturn true;")
    Invoke-UnityProbe @('exec', '--file', $reloadFile) | Out-Null
    Invoke-UnityProbe @('instances', 'wait', '--timeout', '300000') | Out-Null
    $list = Invoke-UnityProbe @('list')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'tools.json'), $list.stdout)
    # console is measured first in a fresh domain, before any diagnostic exec.
    Measure-RoundTrips 'baseline'
    $inventory = Invoke-UnityProbe @('exec', '--file', "$PSScriptRoot/probe.cs", '--timeout', '120000')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'inventory.json'), $inventory.stdout)
    $accumulate = Invoke-UnityProbe @('exec', '--file', "$PSScriptRoot/accumulate.cs", '--timeout', '180000')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'accumulation.json'), $accumulate.stdout)
    Measure-RoundTrips 'accumulated'
    $inventory = Invoke-UnityProbe @('exec', '--file', "$PSScriptRoot/probe.cs", '--timeout', '120000')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'accumulated-inventory.json'), $inventory.stdout)
} finally {
    try {
        Invoke-UnityProbe @('exec', '--file', $reloadFile) | Out-Null
        Invoke-UnityProbe @('instances', 'wait', '--timeout', '300000') | Out-Null
        $clean = Invoke-UnityProbe @('list')
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'after-reload-tools.json'), $clean.stdout)
    } finally { Remove-Item $reloadFile -Force -ErrorAction SilentlyContinue }
}
Write-Output "Measurements saved: $OutputDirectory"
