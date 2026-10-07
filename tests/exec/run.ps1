param([Parameter(Mandatory = $true)][string]$UnityEditorData, [ValidateRange(2, 600)][int]$Repetitions = 600)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ("exec-tests-" + [Guid]::NewGuid())
New-Item -ItemType Directory $temp | Out-Null
try {
    $dotnet = Join-Path $UnityEditorData 'DotNetSdk/dotnet.exe'
    $csc = (Get-ChildItem "$UnityEditorData/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll" | Select-Object -First 1).FullName
    $mono = Join-Path $UnityEditorData 'MonoBleedingEdge/bin/mono.exe'
    $framework = Join-Path $UnityEditorData 'MonoBleedingEdge/lib/mono/4.5'
    $json = Join-Path $UnityEditorData 'Managed/Newtonsoft.Json.dll'
    Copy-Item $json $temp
    $references = @('-nostdlib')
    foreach ($assembly in @('mscorlib', 'System', 'System.Core')) { $references += "-r:$framework/$assembly.dll" }
    $references += "-r:$framework/Facades/netstandard.dll", "-r:$json"
    $exe = Join-Path $temp 'fixture.exe'
    $arguments = @('-nologo', '-langversion:latest', '-target:exe', "-out:$exe") + $references
    $arguments += "$root/tests/exec/Fixture.cs", "$root/unity-connector/Editor/Tools/ExecuteCsharp.cs"
    foreach ($file in @('Core/Response.cs', 'Core/ToolParams.cs', 'Core/ParamCoercion.cs', 'Attributes/UnityCliToolAttribute.cs')) {
        $arguments += "$root/unity-connector/Editor/$file"
    }
    & $dotnet exec $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compile failed' }
    foreach ($version in @(1, 2)) {
        $source = Join-Path $temp "reference$version.cs"
        Set-Content $source "public static class CacheReference { public const int Value = $version; }"
        & $dotnet exec $csc -nologo -target:library "-out:$temp/reference.dll" @references $source
        if ($LASTEXITCODE -ne 0) { throw 'Reference fixture compile failed' }
        Copy-Item "$temp/reference.dll" "$temp/reference$version.dll"
    }
    & $mono $exe $csc $dotnet $UnityEditorData "$temp/reference1.dll" "$temp/reference2.dll" $Repetitions "$root/tests/exec/native.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Exec fixture failed' }
} finally { Remove-Item $temp -Recurse -Force }
