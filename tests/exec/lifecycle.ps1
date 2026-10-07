$ErrorActionPreference = 'Stop'
$cli = 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe'
$project = 'D:/Projects/ProjectD/client'
$data = 'C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data'
$csc = (Get-ChildItem "$data/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll" | Select-Object -First 1).FullName
$compilerArgs = @('--csc', $csc, '--dotnet', "$data/DotNetSdk/dotnet.exe")
$temp = Join-Path ([IO.Path]::GetTempPath()) ('exec-lifecycle-' + [Guid]::NewGuid() + '.cs')
$script = "$project/Assets/Editor/UnityCliExecCacheReferenceProbe432.cs"
if (Test-Path $script) { throw 'Temporary script already exists; refusing overwrite' }
$probe = @'
return new Dictionary<string, object> {
    ["mvid"] = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString(),
    ["cache"] = ((System.Collections.IDictionary)typeof(UnityCliConnector.Tools.ExecuteCsharp).GetField("CompiledMethods", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count,
    ["assemblies"] = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null)
};
'@
function Exec([string]$code) {
    [IO.File]::WriteAllText($temp, $code)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $output = & $cli --project $project exec --file $temp @compilerArgs --timeout 120000
    $ms = $timer.Elapsed.TotalMilliseconds
    if ($LASTEXITCODE -ne 0) { throw "exec failed: $output" }
    try { $data = $output -join "`n" | ConvertFrom-Json } catch { throw "Invalid exec JSON: $($output -join ' | ')" }
    return @{ data = $data; roundTripMs = $ms }
}
function Compile {
    & $cli --project $project editor refresh --compile --timeout 300000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Script compilation failed' }
    & $cli instances wait --project $project --timeout 300000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Editor did not return to ready' }
}
$report = [ordered]@{}
$scriptCreated = $false
try {
    $first = Exec $probe
    $hit = Exec $probe
    if ($first.data.mvid -ne $hit.data.mvid) { throw 'Probe was not reused before reload' }
    $report.beforeReload = @{ first = $first; hit = $hit }
    [IO.File]::WriteAllText($temp, "EditorUtility.RequestScriptReload();`nreturn true;")
    $reloadResponse = & $cli --project $project exec --file $temp @compilerArgs --timeout 120000
    if ($LASTEXITCODE -ne 0) { throw 'Domain reload request failed' }
    $report.reloadResponse = $reloadResponse -join "`n"
    & $cli instances wait --project $project --timeout 300000 | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $after = Exec $probe
        if ($after.data.mvid -ne $first.data.mvid) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Domain reload did not replace execution assembly' }
        Start-Sleep -Milliseconds 250
    } while ($true)
    $hit = Exec $probe
    if ($after.data.mvid -ne $hit.data.mvid -or $after.data.cache -ne 1) { throw 'Post-reload cache did not reset and reuse' }
    $report.afterDomainReload = @{ first = $after; hit = $hit }

    [IO.File]::WriteAllText($script, 'public static class UnityCliExecCacheReferenceProbe432 { public const int Value = 1; }')
    $scriptCreated = $true
    Compile
    $added = Exec $probe
    $addedHit = Exec $probe
    $constantCode = 'return UnityCliExecCacheReferenceProbe432.Value;'
    $v1 = Exec $constantCode
    if ($v1.data -ne 1 -or $added.data.mvid -eq $after.data.mvid -or $added.data.mvid -ne $addedHit.data.mvid) { throw 'Script addition/reference v1 failed' }
    $report.afterScriptAddition = @{ first = $added; hit = $addedHit; constant = $v1.data }

    [IO.File]::WriteAllText($script, 'public static class UnityCliExecCacheReferenceProbe432 { public const int Value = 2; }')
    Compile
    $changed = Exec $probe
    $changedHit = Exec $probe
    $v2 = Exec $constantCode
    if ($v2.data -ne 2 -or $changed.data.mvid -eq $added.data.mvid -or $changed.data.mvid -ne $changedHit.data.mvid) { throw 'Script change reused stale constant/method' }
    $report.afterScriptModification = @{ first = $changed; hit = $changedHit; constant = $v2.data }
} finally {
    if ($scriptCreated) {
        Remove-Item $script -Force -ErrorAction SilentlyContinue
        Remove-Item ($script + '.meta') -Force -ErrorAction SilentlyContinue
        Compile
        $clean = Exec $probe
        $cleanHit = Exec $probe
        if ($clean.data.mvid -ne $cleanHit.data.mvid -or $clean.data.cache -ne 1) { throw 'Clean post-removal cache invalid' }
        $report.afterScriptRemoval = @{ first = $clean; hit = $cleanHit }
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $cli --project $project exec 'return UnityCliExecCacheReferenceProbe432.Value;' @compilerArgs --timeout 120000 2>&1
            $removedTypeExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = 'Stop' }
        if ($removedTypeExit -eq 0 -or ($output -join "`n") -notmatch 'Compile error:') { throw 'Temporary type still available after cleanup' }
        $report.removedTypeCompileFailure = $true
    }
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
}
$report | ConvertTo-Json -Depth 8
