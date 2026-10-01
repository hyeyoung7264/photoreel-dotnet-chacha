$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
foreach ($item in @(@{Name='playwright-core'; Version='1.63.0'; Target='playwright'}, @{Name='@huggingface/tokenizers'; Version='0.2.0'; Target='tokenizers/package'})) {
    $target = Join-Path $evidence $item.Target
    if (Test-Path -LiteralPath (Join-Path $target 'package.json')) {
        $installed = Get-Content -LiteralPath (Join-Path $target 'package.json') -Raw | ConvertFrom-Json
        if ($installed.name -ne $item.Name -or $installed.version -ne $item.Version) { throw 'Unexpected cached test tool version' }
        continue
    }
    $metadata = Invoke-RestMethod ('https://registry.npmjs.org/' + $item.Name + '/' + $item.Version)
    $archive = Join-Path $evidence ($item.Name.Replace('@','').Replace('/','-') + '.tgz')
    Invoke-WebRequest -Uri $metadata.dist.tarball -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash -ine $metadata.dist.shasum) { throw 'Official npm package checksum mismatch' }
    $temp = Join-Path $evidence ('unpack-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    & tar -xzf $archive -C $temp
    if ($LASTEXITCODE -ne 0) { throw 'Test tool extraction failed' }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Move-Item -LiteralPath (Join-Path $temp 'package') -Destination $target
    Remove-Item -LiteralPath $temp
}
Invoke-WebRequest -Uri 'https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/d15189d7028b43f1d3e65039190477f6af591c2a/tokenizer_config.json' -OutFile (Join-Path $evidence 'tokenizer_config.json')
Write-Output 'Node and Edge are required for browser checks. App execution has no Node or Python requirement.'
