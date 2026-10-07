param([Parameter(Mandatory = $true)][string]$UnityEditorData)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/hidden.ps1"
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('discovery-fixture-' + [Guid]::NewGuid())
New-Item -ItemType Directory $temp | Out-Null
try {
    $dotnet = "$UnityEditorData/DotNetSdk/dotnet.exe"
    $csc = (Get-ChildItem "$UnityEditorData/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll" | Select-Object -First 1).FullName
    $framework = "$UnityEditorData/MonoBleedingEdge/lib/mono/4.5"
    $json = "$UnityEditorData/Managed/Newtonsoft.Json.dll"
    Copy-Item $json $temp
    $exe = "$temp/fixture.exe"
    $arguments = @('exec', $csc, '-nologo', '-langversion:latest', '-target:exe', '-nostdlib', "-out:$exe")
    foreach ($assembly in @('mscorlib', 'System', 'System.Core')) { $arguments += "-r:$framework/$assembly.dll" }
    $arguments += "-r:$framework/Facades/netstandard.dll", "-r:$json", "$PSScriptRoot/Fixture.cs"
    foreach ($file in @('ToolDiscovery.cs', 'Attributes/UnityCliToolAttribute.cs', 'Core/StringCaseUtility.cs')) { $arguments += "$root/unity-connector/Editor/$file" }
    $compiled = Invoke-HiddenProcess -File $dotnet -Arguments $arguments
    $compiled.stdout
    $tested = Invoke-HiddenProcess -File "$UnityEditorData/MonoBleedingEdge/bin/mono.exe" -Arguments @($exe)
    $tested.stdout
} finally { Remove-Item $temp -Recurse -Force }
