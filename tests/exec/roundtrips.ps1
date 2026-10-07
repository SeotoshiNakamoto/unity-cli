$ErrorActionPreference = 'Stop'
$cli = 'D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe'
$project = 'D:/Projects/ProjectD/client'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('exec-roundtrips-' + [Guid]::NewGuid() + '.cs')
$code = 'return Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString();'
try {
    [IO.File]::WriteAllText($temp, $code)
    $inline = (& $cli --project $project exec $code) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Inline failed' }
    $file = (& $cli --project $project exec --file $temp) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $file -ne $inline) { throw 'File did not reuse inline method' }
    $created = (& $cli --project $project exec $code --async) -join "`n" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Async inline submission failed' }
    $asyncInline = (& $cli --project $project job $created.job_id --timeout 120000) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $asyncInline.Trim() -ne $inline.Trim()) { throw 'Async inline did not reuse method' }
    $created = (& $cli --project $project exec --file $temp --async) -join "`n" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Async file submission failed' }
    $asyncFile = (& $cli --project $project job $created.job_id --timeout 120000) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $asyncFile.Trim() -ne $inline.Trim()) { throw 'Async file did not reuse method' }
    [ordered]@{ inlineMvid = $inline.Trim(); fileMvid = $file.Trim(); asyncInlineMvid = $asyncInline.Trim(); asyncFileMvid = $asyncFile.Trim(); allSameLoadedMethod = $true } | ConvertTo-Json
} finally { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
