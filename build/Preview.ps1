param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& $Dotnet run --project (Join-Path $root 'build/RenderPreview') -c Release -- (Join-Path $root 'docs/assets')
if ($LASTEXITCODE -ne 0) { throw 'Preview rendering failed' }
