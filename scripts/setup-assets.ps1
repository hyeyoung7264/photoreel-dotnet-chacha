param([switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'assets.manifest.json') -Raw | ConvertFrom-Json
$assets = if ($env:PHOTOREEL_ASSETS) { [IO.Path]::GetFullPath($env:PHOTOREEL_ASSETS) } else { Join-Path $root 'assets_cache' }
New-Item -ItemType Directory -Path $assets -Force | Out-Null
foreach ($asset in $manifest.assets) {
    $target = Join-Path $assets $asset.name
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $asset.sha256) { throw "Cache checksum mismatch: $($asset.name). Preserve the existing file and investigate before replacing it." }
    } else {
        if ($ValidateOnly) { throw "Missing asset: $($asset.name)" }
        $partial = $target + '.download-' + [Guid]::NewGuid().ToString('N')
        try {
            Invoke-WebRequest -Uri $asset.url -OutFile $partial
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $asset.sha256) { throw "Download checksum mismatch: $($asset.name)" }
            Move-Item -LiteralPath $partial -Destination $target
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
    }
    Write-Output "Verified $($asset.name) [$($asset.license)]"
}
$native = Join-Path $root 'native'
New-Item -ItemType Directory -Path $native -Force | Out-Null
$encoder = if ($env:PHOTOREEL_FFMPEG) { [IO.Path]::GetFullPath($env:PHOTOREEL_FFMPEG) } else { Join-Path $native 'ffmpeg.exe' }
if (!(Test-Path -LiteralPath $encoder)) {
    if ($ValidateOnly) { throw 'Missing FFmpeg executable' }
    $temp = Join-Path $native ('setup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $zip = Join-Path $temp 'ffmpeg.zip'
        Invoke-WebRequest -Uri $manifest.ffmpeg.url -OutFile $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $temp
        $file = Get-ChildItem -LiteralPath $temp -Recurse -Filter ffmpeg.exe -File | Select-Object -First 1
        if (!$file -or (Get-FileHash -LiteralPath $file.FullName).Hash -ine $manifest.ffmpeg.executable_sha256) { throw 'FFmpeg checksum mismatch' }
        Copy-Item -LiteralPath $file.FullName -Destination $encoder
    } finally {
        $resolved = [IO.Path]::GetFullPath($temp)
        if (!$resolved.StartsWith([IO.Path]::GetFullPath($native) + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe temporary path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
if ((Get-FileHash -LiteralPath $encoder).Hash -ine $manifest.ffmpeg.executable_sha256) { throw 'FFmpeg checksum mismatch' }
Write-Output 'Verified FFmpeg 7.1 (GPL-3.0-or-later). Setup needs no Python.'
