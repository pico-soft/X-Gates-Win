<#
  Проверка X-Gates Win на ноутбуке (Windows 10/11) — то же, что делает .github/workflows/windows.yml, но локально.
  Запуск из корня репозитория в PowerShell (БЕЗ прав администратора):
      powershell -ExecutionPolicy Bypass -File tools\windows\check.ps1            # тесты + сборка + запуск окна
      powershell -ExecutionPolicy Bypass -File tools\windows\check.ps1 -Msi       # + установщик (нужен WiX v5)
  Нужно: .NET SDK 10 (dotnet), git. Системные настройки НЕ меняет: ни прокси Windows, ни автозапуск, ни реестр.
  Результат: out\check\ (журнал тестов, журнал приложения, MSI) — его и прислать/показать.
#>
param([switch]$Msi, [switch]$NoTests)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
Set-Location $root
New-Item -ItemType Directory -Force out\check | Out-Null

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'нет dotnet — установите .NET SDK 10 (https://dotnet.microsoft.com/download)' }
Write-Host "dotnet $(dotnet --version)"

# Ядро xray: закреплённая версия и sha256 — из tools/xray/fetch-xray.sh (один источник правды).
$xray = 'third_party\xray\windows-64\xray.exe'
if (-not (Test-Path $xray)) {
  $s = Get-Content tools\xray\fetch-xray.sh -Raw
  $ver = [regex]::Match($s, 'VERSION="([^"]+)"').Groups[1].Value
  $sha = [regex]::Match($s, '\[windows-64\]="([0-9a-f]+)"').Groups[1].Value
  $zip = Join-Path $env:TEMP 'xray-windows-64.zip'
  Invoke-WebRequest "https://github.com/XTLS/Xray-core/releases/download/$ver/Xray-windows-64.zip" -OutFile $zip
  $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
  if ($got -ne $sha) { throw "sha256 НЕ совпал: $got (ожидался $sha)" }
  Expand-Archive $zip third_party\xray\windows-64 -Force
}
Write-Host "xray: $((& $xray version) | Select-Object -First 1)"

if (-not $NoTests) {
  dotnet test tests\XGates.Core.Tests -c Release --blame-hang-timeout 5m --logger "trx;LogFileName=tests.trx" --results-directory out\check
  if ($LASTEXITCODE -ne 0) { Write-Warning "тесты: код $LASTEXITCODE (подробности — out\check\tests.trx)" }
}

dotnet publish src\XGates.Win -c Release -r win-x64 --self-contained -o out\check\XGatesWin
if ($LASTEXITCODE -ne 0) { throw 'сборка приложения не удалась' }

if ($Msi) {
  $wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
  if (-not (Test-Path $wix)) { dotnet tool install --global wix --version 5.0.2 }
  $ver = [regex]::Match((Get-Content Directory.Build.props -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
  & $wix build installer\XGatesWin.wxs -arch x64 -bindpath publish=out\check\XGatesWin -d Version=$ver -d IconPath=src\XGates.Win\Assets\app.ico -o "out\check\XGatesWin-$ver-win-x64.msi"
  Write-Host "MSI: out\check\XGatesWin-$ver-win-x64.msi — установка двойным щелчком (без прав администратора)"
}

Write-Host 'Запускаю приложение (данные — %LOCALAPPDATA%\X-Gates). Закрытие окна = в трей; выход — меню значка в трее.'
Start-Process out\check\XGatesWin\XGatesWin.exe
