param([Parameter(Mandatory = $true)][string]$UnityEditorData)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ("framedebug-tests-" + [Guid]::NewGuid())
New-Item -ItemType Directory $temp | Out-Null
try {
    $dotnet = Join-Path $UnityEditorData 'DotNetSdk/dotnet.exe'
    $csc = (Get-ChildItem "$UnityEditorData/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll" | Select-Object -First 1).FullName
    $mono = Join-Path $UnityEditorData 'MonoBleedingEdge/bin/mono.exe'
    $framework = Join-Path $UnityEditorData 'MonoBleedingEdge/lib/mono/4.5'
    $json = Join-Path $UnityEditorData 'Managed/Newtonsoft.Json.dll'
    Copy-Item $json $temp
    foreach ($variant in @('class', 'struct')) {
        $exe = Join-Path $temp "$variant.exe"
        $args = @('-nologo', '-langversion:latest', '-target:exe', '-nostdlib', "-out:$exe")
        if ($variant -eq 'struct') { $args += '-define:STRUCT_DATA' }
        foreach ($assembly in @('mscorlib', 'System', 'System.Core')) { $args += "-r:$framework/$assembly.dll" }
        $args += "-r:$framework/Facades/netstandard.dll", "-r:$json"
        $args += "$root/tests/framedebug/Fixture.cs", "$root/unity-connector/Editor/Tools/ManageFrameDebugger.cs"
        foreach ($file in @('Core/Response.cs', 'Core/ToolParams.cs', 'Core/ParamCoercion.cs', 'Attributes/UnityCliToolAttribute.cs')) {
            $args += "$root/unity-connector/Editor/$file"
        }
        & $dotnet exec $csc @args
        if ($LASTEXITCODE -ne 0) { throw "Fixture $variant compile failed" }
        & $mono $exe
        if ($LASTEXITCODE -ne 0) { throw "Fixture $variant tests failed" }
    }
} finally { Remove-Item $temp -Recurse -Force }
