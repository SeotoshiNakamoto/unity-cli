param([ValidateRange(2, 600)][int]$Repetitions = 20, [switch]$ExplicitCompiler, [switch]$CscOnly)
$ErrorActionPreference = 'Stop'
$cli = 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe'
$project = 'D:/Projects/ProjectD/client'
$growthLimit = 256L * 1024 * 1024
$value = Get-Random -Minimum 1000000 -Maximum 1000000000
$code = "return $value;"
$data = 'C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data'
$csc = (Get-ChildItem "$data/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll" | Select-Object -First 1).FullName
$helperArgs = @('--csc', $csc, '--dotnet', "$data/DotNetSdk/dotnet.exe")
$compilerArgs = @()
if ($ExplicitCompiler) { $compilerArgs = $helperArgs }
elseif ($CscOnly) { $compilerArgs = @('--csc', $csc) }
$snapshot = @'
return new Dictionary<string, object> {
    ["assemblies"] = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null),
    ["mono"] = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(),
    ["native"] = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()
};
'@
$gcSnapshot = "GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`n" + $snapshot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('exec-transport-' + [Guid]::NewGuid() + '.cs')
function Exec([string]$source) {
    if ($source.Contains('"')) {
        [IO.File]::WriteAllText($temp, $source)
        $output = & $cli --project $project exec --file $temp @helperArgs --timeout 120000
    } else {
        $output = & $cli --project $project exec $source @compilerArgs --timeout 120000
    }
    if ($LASTEXITCODE -ne 0) { throw "exec failed: $output" }
    return ($output -join "`n" | ConvertFrom-Json)
}
try {
Exec $snapshot | Out-Null
Exec $gcSnapshot | Out-Null
$before = Exec $gcSnapshot
$times = New-Object 'Collections.Generic.List[double]'
for ($i = 0; $i -lt $Repetitions; $i++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $result = Exec $code
    $times.Add($timer.Elapsed.TotalMilliseconds)
    if ($result -ne $value) { throw 'Unexpected exec result' }
    if ($i % 25 -eq 0) {
        $current = Exec $snapshot
        if ($current.mono - $before.mono -ge $growthLimit -or $current.native - $before.native -ge $growthLimit) { throw '256 MiB memory limit exceeded' }
    }
}
$preGC = Exec $snapshot
$postGC = Exec $gcSnapshot
if ($postGC.assemblies -ne $before.assemblies + 1) { throw 'Repeated CLI calls loaded duplicate assemblies' }
[ordered]@{
    repetitions = $Repetitions
    compilerDiscovery = $(if ($ExplicitCompiler) { 'explicit compiler and host paths' } elseif ($CscOnly) { 'explicit compiler, automatic host' } else { 'automatic compiler and host' })
    beforeExecAssemblies = $before.assemblies
    afterExecAssemblies = $postGC.assemblies
    assemblyDelta = $postGC.assemblies - $before.assemblies
    firstCompileRoundTripMs = $times[0]
    allHitsRoundTripMeanMs = ($times | Select-Object -Skip 1 | Measure-Object -Average).Average
    earlyHitsRoundTripMeanMs = ($times | Select-Object -Skip 1 -First ([Math]::Min(99,[Math]::Max(1,[int]($Repetitions/2)-1))) | Measure-Object -Average).Average
    lateHitsRoundTripMeanMs = ($times | Select-Object -Skip ([Math]::Max(1,$Repetitions-[Math]::Min(100,[int]($Repetitions/2)))) | Measure-Object -Average).Average
    preGCMonoDeltaBytes = $preGC.mono - $before.mono
    postGCMonoDeltaBytes = $postGC.mono - $before.mono
    preGCNativeAllocatedDeltaBytes = $preGC.native - $before.native
    postGCNativeAllocatedDeltaBytes = $postGC.native - $before.native
} | ConvertTo-Json
} finally { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
