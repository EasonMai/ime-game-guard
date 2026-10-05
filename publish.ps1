$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ImeGameGuard\ImeGameGuard.csproj'
$output = Join-Path $PSScriptRoot 'publish'

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
Copy-Item (Join-Path $PSScriptRoot 'ImeGameGuard\config.json') (Join-Path $output 'config.json') -Force
Copy-Item (Join-Path $PSScriptRoot 'ImeGameGuard\gameprocessesdb.json') (Join-Path $output 'gameprocessesdb.json') -Force
Write-Host "Published: $output\ImeGameGuard.exe"
