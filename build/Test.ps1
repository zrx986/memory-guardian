param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $projects = @('MemoryGuardian.Core.Tests', 'MemoryGuardian.Native.Tests', 'MemoryGuardian.App.Tests', 'MemoryGuardian.Animation.Tests')
    $evidence = Join-Path $root 'output/test-results'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    foreach ($project in $projects) {
        $arguments = @('run', '--project', "tests/$project", '-c', 'Release')
        if ($project -eq 'MemoryGuardian.Animation.Tests') { $arguments += @('--', (Join-Path $evidence 'animation')) }
        & $Dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence ($project + '.txt'))
        if ($LASTEXITCODE -ne 0) { throw "$project failed with exit code $LASTEXITCODE" }
    }
    Write-Host 'All checks passed. No native cleanup or startup changes were performed.'
} finally { Pop-Location }
