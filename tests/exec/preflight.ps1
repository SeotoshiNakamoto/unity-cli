param(
    [Parameter(Mandatory = $true)][string]$UnityEditorData,
    [Parameter(Mandatory = $true)][string]$ProjectPath
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$project = (Resolve-Path $ProjectPath).Path
$rsp = Get-ChildItem "$project/Library/Bee/artifacts" -Filter UnityCliConnector.Editor.rsp -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$rsp) { throw 'No actual UnityCliConnector.Editor response file; compile the project first' }
$text = [IO.File]::ReadAllText($rsp.FullName)
if ($text -notmatch 'UnityEditor.CoreModule.dll') { throw 'UnityEditor.CoreModule reference missing' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ("exec-preflight-" + [Guid]::NewGuid())
New-Item -ItemType Directory $temp | Out-Null
Push-Location $project
try {
    $text = [regex]::Replace($text, '(?m)^-out:.*$', ('-out:"' + "$temp/UnityCliConnector.Editor.dll" + '"'))
    $text = [regex]::Replace($text, '(?m)^-refout:.*$', ('-refout:"' + "$temp/UnityCliConnector.Editor.ref.dll" + '"'))
    foreach ($file in @('Tools/ExecuteCsharp.cs', 'ToolDiscovery.cs')) {
        $source = [regex]::Match($text, ('(?m)^"[^"]*/Editor/' + [regex]::Escape($file) + '"\r?$'))
        if (!$source.Success) { throw "$file source entry missing" }
        $text = $text.Replace($source.Value.TrimEnd("`r"), ('"' + "$root/unity-connector/Editor/$file" + '"'))
    }
    $testRsp = Join-Path $temp 'preflight.rsp'
    [IO.File]::WriteAllText($testRsp, $text, (New-Object Text.UTF8Encoding($false)))
    $dotnet = Join-Path $UnityEditorData 'DotNetSdk/dotnet.exe'
    $csc = (Get-ChildItem "$UnityEditorData/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll" | Select-Object -First 1).FullName
    $arguments = @('exec', $csc, '/nostdlib', '/noconfig', '-nologo', "@$testRsp")
    if (Test-Path ($rsp.FullName + '2')) { $arguments += '@' + ($rsp.FullName + '2') }
    $result = Invoke-HiddenProcess -File $dotnet -Arguments $arguments -WorkingDirectory $project
    Write-Output $result.stdout
    if ($result.stderr) { Write-Output $result.stderr }
    Write-Output "PASS: actual Unity response file $($rsp.FullName), including UnityEditor.CoreModule; outputs isolated and removed."
} finally {
    Pop-Location
    Remove-Item $temp -Recurse -Force
}
