# X-Gates Win: send logs from the test laptop to the development server over Syncthing.
# ASCII only: Windows PowerShell 5.1 reads a script without BOM as ANSI.
#
# Sends only logs\, crashes\ and monitor-log.json from %LOCALAPPDATA%\X-Gates (see .stignore below).
# Subscriptions and settings stay on the laptop. No admin rights and no system changes: Syncthing lives in
# %LOCALAPPDATA%\X-Gates-LogSync and runs until -Stop or the next reboot; run the script again after a reboot.
#
#   powershell -ExecutionPolicy Bypass -File send-logs.ps1 -ServerId <id> -ServerAddress tcp://<ip>:22000
#   powershell -ExecutionPolicy Bypass -File send-logs.ps1 -Stop
param(
    [string]$ServerId,
    [string]$ServerAddress,
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$version = 'v2.1.7'
$sha256 = '3ce7da770f546a8d02b65b2e7ee31f2a3b8b7c3f990ede045590cca4172ecfe1'
$folderId = 'xgates-win-logs'
$root = Join-Path $env:LOCALAPPDATA 'X-Gates-LogSync'
$exe = Join-Path $root 'syncthing.exe'
$stHome = Join-Path $root 'home'
$data = Join-Path $env:LOCALAPPDATA 'X-Gates'

function Get-Running { Get-Process syncthing -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } }

function Invoke-Cli {
    $out = & $exe cli "--home=$stHome" @args
    if ($LASTEXITCODE -ne 0) { throw "syncthing cli $args failed: $out" }
    $out
}

if ($Stop) {
    Get-Running | Stop-Process -Force
    Write-Host 'Log sending stopped.'
    return
}

New-Item -ItemType Directory -Force -Path $root, $data | Out-Null

if (-not (Test-Path $exe)) {
    $name = "syncthing-windows-amd64-$version"
    $url = "https://github.com/syncthing/syncthing/releases/download/$version/$name.zip"
    $zip = Join-Path $root 'syncthing.zip'
    try { Invoke-WebRequest $url -OutFile $zip -UseBasicParsing }
    catch { Invoke-WebRequest $url -OutFile $zip -UseBasicParsing -Proxy 'http://127.0.0.1:10816' }
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() -ne $sha256) {
        Remove-Item $zip -Force
        throw 'Checksum mismatch, the download was not used.'
    }
    $unpack = Join-Path $root 'unpack'
    Expand-Archive $zip $unpack -Force
    Copy-Item (Join-Path $unpack "$name\syncthing.exe") $exe
    Remove-Item $zip, $unpack -Recurse -Force
}

# First match wins: everything that is not listed here stays on the laptop.
Set-Content -Path (Join-Path $data '.stignore') -Encoding Ascii -Value @(
    '!/logs', '!/logs/**', '!/crashes', '!/crashes/**', '!/monitor-log.json', '*'
)

if (-not (Get-Running)) {
    Start-Process $exe -WindowStyle Hidden -ArgumentList @('serve', "--home=`"$stHome`"", '--no-browser', '--no-restart', '--no-upgrade')
}
$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
    Start-Sleep -Seconds 1
    & $exe cli "--home=$stHome" show version *> $null
    $ready = $LASTEXITCODE -eq 0
}
if (-not $ready) { throw 'Syncthing did not start.' }

if (-not ((Invoke-Cli config folders list) -contains $folderId)) {
    Invoke-Cli config folders add --id $folderId --label 'X-Gates logs' --path $data --type sendonly | Out-Null
}
if ($ServerId) {
    if (-not ((Invoke-Cli config devices list) -contains $ServerId)) {
        Invoke-Cli config devices add --device-id $ServerId --name 'dev-server' | Out-Null
    }
    if ($ServerAddress -and -not ((Invoke-Cli config devices $ServerId addresses list) -contains $ServerAddress)) {
        Invoke-Cli config devices $ServerId addresses add $ServerAddress | Out-Null
    }
    if (-not ((Invoke-Cli config folders $folderId devices list) -contains $ServerId)) {
        Invoke-Cli config folders $folderId devices add --device-id $ServerId | Out-Null
    }
}

$id = & $exe device-id "--home=$stHome"
Write-Host ''
Write-Host 'Log sending is running. Send this line to the developer:'
Write-Host $id
