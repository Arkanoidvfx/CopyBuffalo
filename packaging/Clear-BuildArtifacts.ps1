[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
$before=(Get-PSDrive -Name ([IO.Path]::GetPathRoot($repo).TrimEnd(':\'))).Free
if (Get-Process -Name Runner.Worker -ErrorAction SilentlyContinue) { throw 'A runner job is active. Retry after it finishes.' }
$current=Join-Path $repo 'dist\v0.2.4-passwords'
$canonical=Join-Path $repo 'dist\v0.2.4'
if (!(Test-Path -LiteralPath $current)) { $current=$canonical }
$evidence=Join-Path $repo '.cache\release-evidence\v0.2.4'
$validation=Get-Content -Raw -LiteralPath (Join-Path $evidence 'validation.json') | ConvertFrom-Json
foreach ($asset in $validation.assets) {
    $path=Join-Path $current ('github\'+$asset.name)
    if ((Get-Item -LiteralPath $path).Length -ne $asset.bytes -or ('sha256:'+(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()) -ne $asset.digest) { throw "Current release asset mismatch: $($asset.name)" }
}
$manifest=Get-Content -Raw -LiteralPath (Join-Path $repo 'dist\manifest.json') | ConvertFrom-Json
$model=Join-Path $repo '.cache\distribution-ai\models\Qwen3.5-4B-Q4_K_M.gguf'
if ((Get-FileHash -LiteralPath $model).Hash.ToLowerInvariant() -ne $manifest.modelSha256) { throw 'Permanent AI build cache is not ready.' }
if (!(Test-Path -LiteralPath (Join-Path $repo '.cache\distribution-licenses\Dotnet-LICENSE.txt'))) { throw 'Permanent license cache is not ready.' }
$targets=@('.tmp','bin','obj','.cache\ocr\target','.cache\ocr\temp','dist\github','dist\v0.2.1','dist\v0.2.2','dist\v0.2.3','dist\v0.2.4') |
    ForEach-Object { Join-Path $repo $_ } | Where-Object { $_ -ne $current }
$targets+=Get-ChildItem -LiteralPath (Join-Path $repo 'dist') -File | Where-Object Name -ne 'manifest.json' | ForEach-Object { $_.FullName }
$targets+=Get-ChildItem -LiteralPath $current -File | ForEach-Object { $_.FullName }
$targets+=Get-ChildItem -LiteralPath (Join-Path $repo '.cache\distribution-tools') -File -Filter '*.zip' | ForEach-Object { $_.FullName }
$targets+=Join-Path $repo '.cache\distribution-tools\innosetup.exe'
$targets+=Join-Path $repo 'publish\ArkCopyBuffer.pdb'
$targets+=Join-Path $repo 'publish\standalone\ArkCopyBuffer.pdb'
$plan=@(foreach ($target in $targets) {
    if (!(Test-Path -LiteralPath $target)) { continue }
    $resolved=(Resolve-Path -LiteralPath $target).Path
    if ($resolved -ne [IO.Path]::GetFullPath($target) -or !$resolved.StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup path: $target" }
    $item=Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked cleanup target: $resolved" }
    $bytes=if ($item.PSIsContainer) { (Get-ChildItem -LiteralPath $resolved -Recurse -File -Force | Measure-Object Length -Sum).Sum } else { $item.Length }
    @{path=$resolved;bytes=$bytes}
})
foreach ($entry in $plan) {
    if ($PSCmdlet.ShouldProcess($entry.path,'Delete generated build artifacts')) { Remove-Item -LiteralPath $entry.path -Recurse -Force }
}
if ($current -ne $canonical -and $PSCmdlet.ShouldProcess($current,'Rename retained current release to dist/v0.2.4')) {
    if (Test-Path -LiteralPath $canonical) { throw 'Current release destination still exists.' }
    Move-Item -LiteralPath $current -Destination $canonical
}
$after=(Get-PSDrive -Name ([IO.Path]::GetPathRoot($repo).TrimEnd(':\'))).Free
$result=@{preview=[bool]$WhatIfPreference;plannedGB=[math]::Round(($plan | Measure-Object bytes -Sum).Sum/1GB,2);freedGB=[math]::Round(($after-$before)/1GB,2);freeGB=[math]::Round($after/1GB,2)}
if (!$WhatIfPreference) { $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'cleanup-result.json') }
$result | ConvertTo-Json
