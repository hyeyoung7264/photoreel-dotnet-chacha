param([string]$Output = 'publish')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'setup-assets.ps1') -ValidateOnly
    dotnet publish dotnet/Photoreel/Photoreel.csproj -c Release -r win-x64 --self-contained true -o $Output -p:RestoreLockedMode=true --configfile NuGet.Config --packages .packages
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $assetSource = if ($env:PHOTOREEL_ASSETS) { $env:PHOTOREEL_ASSETS } else { Join-Path $root 'assets_cache' }
    $encoderSource = if ($env:PHOTOREEL_FFMPEG) { $env:PHOTOREEL_FFMPEG } else { Join-Path $root 'native/ffmpeg.exe' }
    New-Item -ItemType Directory -Path (Join-Path $Output 'assets_cache'),(Join-Path $Output 'native') -Force | Out-Null
    Get-ChildItem -LiteralPath $assetSource -File | Copy-Item -Destination (Join-Path $Output 'assets_cache') -Force
    Copy-Item -LiteralPath $encoderSource -Destination (Join-Path $Output 'native/ffmpeg.exe') -Force
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $Output -Recurse -Force
    foreach ($name in @('assets.manifest.json','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $Output -Force }
    Write-Output "Run $Output/Photoreel.exe --serve. No Python or .NET installation is required for this Windows x64 build."
} finally { Pop-Location }
