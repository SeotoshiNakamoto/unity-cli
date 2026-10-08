param([ValidateSet('fixture','baseline','implementation','cleanup','rollback')][string]$Mode, [string]$Cli = 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe')
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../tooldiscovery/hidden.ps1"
$root=(Resolve-Path "$PSScriptRoot/../..").Path
$dest='D:/Projects/ProjectD/tools/unity-cli/unity-connector/Editor'
$log='D:/Projects/ProjectD/client/Logs/Editor.log'
$before=(Get-Item $log).Length
$files=@('HttpServer.cs','AsyncJobManager.cs','CommandRouter.cs','Tools/ManageFrameDebugger.cs')
if($Mode -eq 'fixture'){Copy-Item "$PSScriptRoot/HttpThreadProbe.cs" "$dest/HttpThreadProbe.cs"}
if($Mode -eq 'implementation'){foreach($f in $files){Copy-Item "$root/unity-connector/Editor/$f" "$dest/$f"}}
if($Mode -eq 'baseline'){
    Invoke-HiddenProcess -File 'git.exe' -Arguments (@('-C','D:/Projects/ProjectD','restore','--') + ($files | ForEach-Object {"tools/unity-cli/unity-connector/Editor/$_"})) | Out-Null
}
if($Mode -in @('cleanup','rollback')){Remove-Item "$dest/HttpThreadProbe.cs","$dest/HttpThreadProbe.cs.meta" -Force -ErrorAction SilentlyContinue}
if($Mode -eq 'rollback'){
    # Refresh artifacts before restore, retaining the last tested candidate on failure.
    $patch=Invoke-HiddenProcess -File 'git.exe' -Arguments @('-C',$root,'diff','--binary','--','unity-connector/Editor/HttpServer.cs','unity-connector/Editor/CommandRouter.cs','unity-connector/Editor/AsyncJobManager.cs','internal/client/client.go','cmd/status.go')
    if($patch.stdout.Trim()){[IO.File]::WriteAllText("$PSScriptRoot/candidate.patch",$patch.stdout,(New-Object Text.UTF8Encoding($false)))}
    $limit=Invoke-HiddenProcess -File 'git.exe' -Arguments @('-C',$root,'diff','--binary','--','unity-connector/Editor/Tools/ManageFrameDebugger.cs','tests/framedebug/Fixture.cs')
    if($limit.stdout.Contains('diff --git a/unity-connector/Editor/Tools/ManageFrameDebugger.cs')){[IO.File]::WriteAllText("$PSScriptRoot/../framedebug/limit-restore.patch",$limit.stdout,(New-Object Text.UTF8Encoding($false)))}
    foreach($repo in @($root,'D:/Projects/ProjectD')){
        $paths=if($repo -eq $root){@('unity-connector/Editor/HttpServer.cs','unity-connector/Editor/AsyncJobManager.cs','unity-connector/Editor/CommandRouter.cs','unity-connector/Editor/Tools/ManageFrameDebugger.cs','internal/client/client.go','cmd/status.go')}else{@('tools/unity-cli/unity-connector/Editor/HttpServer.cs','tools/unity-cli/unity-connector/Editor/AsyncJobManager.cs','tools/unity-cli/unity-connector/Editor/CommandRouter.cs','tools/unity-cli/unity-connector/Editor/Tools/ManageFrameDebugger.cs')}
        Invoke-HiddenProcess -File 'git.exe' -Arguments (@('-C',$repo,'restore','--')+$paths) | Out-Null
    }
}
$r=Invoke-HiddenProcess -File $Cli -Arguments @('--project','D:/Projects/ProjectD/client','editor','refresh','--compile','--timeout','300000')
$r.stdout
$stream=[IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
try{$stream.Position=[Math]::Min($before,$stream.Length);$reader=New-Object IO.StreamReader($stream);$tail=$reader.ReadToEnd()}finally{$stream.Dispose()}
if($tail -match 'error CS\d+' -or $tail -match '(?m)^(NullReferenceException|InvalidOperationException|ObjectDisposedException|AggregateException|SynchronizationLockException|ThreadAbortException)'){
    if($Mode -ne 'rollback'){
        & "$PSScriptRoot/deploy.ps1" -Mode rollback
    }
    throw "New compile/lifecycle error during $Mode; rolled back: $tail"
}
Write-Output "PASS: $Mode, compiler/reload log checked"
