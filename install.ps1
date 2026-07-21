$ErrorActionPreference = "Stop"

$repo = "youngwoocho02/unity-cli"
$installDir = "$env:LOCALAPPDATA\unity-cli"
$exe = "$installDir\unity-cli.exe"
$agentExe = "$installDir\unity-slot-agent.exe"
$desktopExe = "$installDir\unity-slot-desktop.exe"

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$url = "https://github.com/$repo/releases/latest/download/unity-cli-windows-amd64.exe"
Write-Host "Downloading unity-cli for windows/amd64..."
Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing

$agentUrl = "https://github.com/$repo/releases/latest/download/unity-slot-agent-windows-amd64.exe"
Write-Host "Downloading unity-slot-agent for windows/amd64..."
Invoke-WebRequest -Uri $agentUrl -OutFile $agentExe -UseBasicParsing

$desktopUrl = "https://github.com/$repo/releases/latest/download/unity-slot-desktop-windows-amd64.exe"
Write-Host "Downloading unity-slot-desktop for windows/amd64..."
Invoke-WebRequest -Uri $desktopUrl -OutFile $desktopExe -UseBasicParsing

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$installDir;$userPath", "User")
    $env:Path = "$installDir;$env:Path"
    Write-Host "Added $installDir to PATH (restart shell to apply)"
}

Write-Host "Installed unity-cli to $exe"
& $exe version
& $agentExe version
