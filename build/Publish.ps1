param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/MemoryGuardian.App/MemoryGuardian.App.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$output = Join-Path $root 'output'
$publish = Join-Path $output 'MemoryGuardian'
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    if (Test-Path -LiteralPath $publish) {
        $resolved = (Resolve-Path -LiteralPath $publish).Path
        $expected = [IO.Path]::GetFullPath((Join-Path $root 'output/MemoryGuardian'))
        if ($resolved -ne $expected -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to replace an unexpected publish directory.'
        }
        $backup = Join-Path $output ('publish-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
        Move-Item -LiteralPath $resolved -Destination $backup
    }
    & $Dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $publish 'LICENSE')
    foreach ($name in @('USAGE.md', 'DEVELOPMENT.md', 'VALIDATION.md', 'ASSETS.md', 'OTTOPET-THIRD-PARTY-NOTICES.md', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $root "docs/$name") -Destination (Join-Path $publish $name)
    }
    $runtime = Get-Item -LiteralPath (Join-Path $publish 'coreclr.dll')
    if ((Get-AuthenticodeSignature -LiteralPath $runtime.FullName).Status -ne 'Valid') { throw 'Runtime signature check failed' }
    $manifest = [ordered]@{}
    Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($publish.Length + 1)
        $manifest[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'package-manifest.json') -Encoding utf8
    $zip = Join-Path $output "MemoryGuardian-$version-win-x64.zip"
    Compress-Archive -LiteralPath $publish -DestinationPath $zip -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
    [pscustomobject]@{ Version=$version; Path=$zip; SizeMiB=[math]::Round((Get-Item -LiteralPath $zip).Length/1MB,2); SHA256=$hash; ManifestFiles=$manifest.Count } |
        ConvertTo-Json | Tee-Object -FilePath (Join-Path $output 'release.json')
} finally { Pop-Location }
