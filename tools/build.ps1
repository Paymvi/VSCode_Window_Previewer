# Builds everything: companion .vsix, tests, the app (Release, single exe), and a zip in dist\.
# Usage: pwsh tools\build.ps1   (stop a running Project Previews first; its exe is locked while running)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (Get-Process ProjectPreviews -ErrorAction SilentlyContinue) { throw 'Project Previews is running. Exit it from the tray first.' }

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'pack-companion.ps1')
dotnet test tests\ProjectPreviews.Tests -c Release
if ($LASTEXITCODE) { throw 'tests failed' }

$out = Join-Path $root 'publish\ProjectPreviews'
dotnet publish src\ProjectPreviews -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o $out
if ($LASTEXITCODE) { throw 'publish failed' }

$version = ([xml](Get-Content src\ProjectPreviews\ProjectPreviews.csproj)).Project.PropertyGroup.Version | Select-Object -First 1
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
$zip = Join-Path $root "dist\ProjectPreviews-$version-win-x64.zip"
Copy-Item README.md $out -Force
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
"built $zip"
