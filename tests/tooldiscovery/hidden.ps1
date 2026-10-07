function Invoke-HiddenProcess {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory = (Get-Location).Path)
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    $result = @{ stdout = $stdout; stderr = $stderr; exitCode = $p.ExitCode; ms = $timer.Elapsed.TotalMilliseconds }
    $p.Dispose()
    if ($result.exitCode -ne 0) { throw "Hidden process failed: $File ($($result.exitCode)) $stderr $stdout" }
    return $result
}
