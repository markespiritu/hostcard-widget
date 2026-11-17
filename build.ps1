# Builds a single self-contained portable exe into .\dist\IpWidget.exe
# Usage: .\build.ps1            (x64)
#        .\build.ps1 -Arch arm64
param([ValidateSet('x64','arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'dist'
dotnet publish (Join-Path $PSScriptRoot 'src\IpWidget\IpWidget.csproj') -c Release -r "win-$Arch" -o (Join-Path $out $Arch)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Arch -eq 'x64') { Copy-Item (Join-Path $out 'x64\IpWidget.exe') (Join-Path $out 'IpWidget.exe') -Force }
Get-ChildItem $out -Recurse -Filter IpWidget.exe | Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
