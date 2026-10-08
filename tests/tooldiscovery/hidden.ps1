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
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    # Drain both pipes concurrently: a large failure on stderr must not block
    # waiting for stdout EOF (and vice versa).
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $result = @{ stdout = $stdout.GetAwaiter().GetResult(); stderr = $stderr.GetAwaiter().GetResult(); exitCode = $p.ExitCode; ms = $timer.Elapsed.TotalMilliseconds }
    $p.Dispose()
    if ($result.exitCode -ne 0) { throw "Hidden process failed: $File ($($result.exitCode)) $($result.stderr) $($result.stdout)" }
    return $result
}
