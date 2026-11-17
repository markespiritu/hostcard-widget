# Builds a single self-contained portable exe into .\dist
# Usage: .\build.ps1              -> dist\IpWidget.exe        (x64, for normal PCs)
#        .\build.ps1 -Arch arm64  -> dist\IpWidget-arm64.exe  (for ARM PCs)
param([ValidateSet('x64','arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'

$dist    = Join-Path $PSScriptRoot 'dist'
$staging = Join-Path $PSScriptRoot "src\IpWidget\obj\publish-$Arch"
$name    = if ($Arch -eq 'x64') { 'IpWidget.exe' } else { "IpWidget-$Arch.exe" }

dotnet publish (Join-Path $PSScriptRoot 'src\IpWidget\IpWidget.csproj') -c Release -r "win-$Arch" -o $staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $staging 'IpWidget.exe') (Join-Path $dist $name) -Force
Get-Item (Join-Path $dist $name) | Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
