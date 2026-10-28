param([string]$WorkRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$root=if ($WorkRoot) { [IO.Path]::GetFullPath($WorkRoot) } else { Join-Path $repo '.tmp\release-v0.2.7' }
$commit=(& git -C $repo rev-parse HEAD).Trim()
$version='0.2.7'
$source=Join-Path $root 'source'
$stage=Join-Path $root 'main'
$output=Join-Path $root 'dist'
$ai=Join-Path $repo '.cache\distribution-ai'
$sdk=Join-Path $repo '.cache\distribution-tools\dotnet\dotnet.exe'

$env:TEMP=Join-Path $root 'temp'; $env:TMP=$env:TEMP
[Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
function Run-Low([string]$Exe,[string[]]$Parameters,[string]$Log,[int]$Timeout=1200000) {
    $process=Start-Process -FilePath $Exe -ArgumentList $Parameters -WindowStyle Hidden -PassThru -RedirectStandardOutput $Log -RedirectStandardError ($Log+'.err')
    $process.PriorityClass='BelowNormal'
    if (!$process.WaitForExit($Timeout)) { $process.Kill(); throw "Timed out: $Log" }
    if ($process.ExitCode) { throw "Command failed ($($process.ExitCode)): $Log" }
}
if ((Get-PSDrive ([IO.Path]::GetPathRoot($root).TrimEnd(':\'))).Free -lt 13GB -or (Get-PSDrive C).Free -lt 3GB) { throw 'Insufficient disk space for release build.' }
if ((Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $output)) { throw 'Refusing to overwrite an existing versioned release.' }
New-Item -ItemType Directory -Force $env:TEMP,$source,$stage,$output,(Join-Path $stage 'licenses') | Out-Null
$archive=Join-Path $root 'source.zip'
& git -C $repo archive --format=zip -o $archive $commit
if ($LASTEXITCODE) { throw 'Source export failed.' }
Expand-Archive -LiteralPath $archive -DestinationPath $source
Copy-Item -LiteralPath (Join-Path $repo 'Assets\Ocr') -Destination (Join-Path $source 'Assets') -Recurse
Run-Low $sdk @('publish',(Join-Path $source 'ArkCopyBuffer.csproj'),'-c','Release','-m:1','-p:UseSharedCompilation=false','-p:DebugType=None',('-p:SourceRevisionId='+$commit),'--nologo','-o',$stage) (Join-Path $root 'publish.log')
$exe=Join-Path $stage 'ArkCopyBuffer.exe'
$product=(Get-Item -LiteralPath $exe).VersionInfo
if ($product.ProductVersion -ne ($version+'+'+$commit) -or $product.ProductName -ne 'CopyBuffalo') { throw 'Staged source/version mismatch.' }
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) { Copy-Item -LiteralPath (Join-Path $repo ('publish\'+$name)) -Destination $stage }
Get-ChildItem -LiteralPath (Join-Path $repo '.cache\distribution-licenses') -File | Copy-Item -Destination (Join-Path $stage 'licenses') -Force
foreach ($name in @('INSTALL.txt','INSTALL-RU.txt','THIRD-PARTY-NOTICES.txt')) { Copy-Item -LiteralPath (Join-Path $source ('packaging\'+$name)) -Destination $stage }
$runtime=@(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object { @{name=[IO.Path]::GetRelativePath($stage,$_.FullName);hash=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$runtime | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'runtime-files.json')
$model=Join-Path $ai 'models\Qwen3.5-4B-Q4_K_M.gguf'
$manifest=Get-Content -LiteralPath (Join-Path $repo 'dist\manifest.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $model).Hash.ToLowerInvariant() -ne $manifest.modelSha256) { throw 'Cached model hash mismatch.' }
$compiler=Join-Path $repo '.cache\distribution-tools\inno\ISCC.exe'
$iss=Join-Path $source 'packaging\ArkCopyBuffer.iss'
Run-Low $compiler @(('/DAppVersion='+$version),('/DStageDir='+$stage),('/DOutputDir='+$output),$iss) (Join-Path $root 'core-setup.log')
Run-Low $compiler @('/DFullPack=1',('/DAppVersion='+$version),('/DStageDir='+$stage),('/DAiDir='+$ai),('/DOutputDir='+$output),$iss) (Join-Path $root 'full-setup.log')
foreach ($name in @('INSTALL.txt','INSTALL-RU.txt')) { Copy-Item -LiteralPath (Join-Path $source ('packaging\'+$name)) -Destination $output }
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $output
$manifest.version=$version
$manifest | Add-Member -NotePropertyName sourceCommit -NotePropertyValue $commit -Force
$manifest | Add-Member -NotePropertyName languages -NotePropertyValue @('en','ru','zh-Hans','zh-Hant','ja','hi') -Force
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
Get-ChildItem -LiteralPath $output -Filter '*.exe' -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
& (Join-Path $source 'packaging\Prepare-GitHubRelease.ps1') -Dist $output *> (Join-Path $root 'split.log')
& (Join-Path $source 'packaging\Test-AiSetup.ps1') -Directory (Join-Path $root 'packaged-launcher-check') *> (Join-Path $root 'packaged-launcher-check.log')
if ($LASTEXITCODE -or (Test-Path -LiteralPath (Join-Path $root 'packaged-launcher-check\ERROR.txt'))) { throw 'Packaged AI launcher verification failed.' }
foreach ($language in @('en','ru','zh-Hans','zh-Hant','ja','hi')) {
    $directory=Join-Path $root ('regression-'+$language)
    Run-Low $exe @('--selftest',$directory,$language) (Join-Path $root ('regression-'+$language+'.process.log')) 60000
    $count=(Get-Content -LiteralPath (Join-Path $directory 'selftest.log') | Measure-Object -Line).Lines
    if ($count -lt 542 -or (Select-String -LiteralPath (Join-Path $directory 'selftest.log') -Pattern '^FAIL')) { throw "Incomplete regressions: $language $count" }
    "PASS $language $count checks"
    Run-Low $exe @('--localization-selftest',(Join-Path $root ('locale-'+$language)),$language) (Join-Path $root ('locale-'+$language+'.process.log')) 60000
}
& (Join-Path $source 'packaging\Test-Distribution.ps1') -StageRoot $root *> (Join-Path $root 'distribution-check.log')
& (Join-Path $source 'packaging\Test-Updater.ps1') -Sdk $sdk -App $exe *> (Join-Path $root 'helper-check.log')
if ($LASTEXITCODE) { throw 'Updater helper verification failed.' }
$assets=Join-Path $output 'github'
$env:ARKCOPYBUFFER_VERIFY_ONLY='1'
try { Run-Low $env:ComSpec @('/d','/c',(Join-Path $assets 'CopyBuffalo-Full-Setup.cmd')) (Join-Path $root 'launcher-check.log') 180000 }
finally { Remove-Item Env:ARKCOPYBUFFER_VERIFY_ONLY }
$assembled=[IO.Path]::GetFullPath((Join-Path $assets 'CopyBuffalo-Full-Setup.exe'))
if (!$assembled.StartsWith([IO.Path]::GetFullPath($output)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe assembly cleanup path.' }
if ((Get-FileHash -LiteralPath $assembled).Hash -ne (Get-FileHash -LiteralPath (Join-Path $output 'CopyBuffalo-Full-Setup.exe')).Hash) { throw 'Full installer launcher hash mismatch.' }
Remove-Item -LiteralPath $assembled
$records=@()
foreach ($file in Get-ChildItem -LiteralPath $assets -File) {
    $records+=[ordered]@{name=$file.Name;bytes=$file.Length;digest=('sha256:'+(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant())}
}
if ($records.Count -ne (7 + @(Get-ChildItem -LiteralPath $assets -Filter '*.part*' -File).Count)) { throw 'Incomplete release assets.' }
@{version=$version;sourceCommit=$commit;productVersion=$product.ProductVersion;assets=$records;validated=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'validation.json')
'PASS: installers, six-language regressions, distribution, updater helper, full launcher and hash checks.' | Set-Content -LiteralPath (Join-Path $root 'PASS.txt')
Get-Content -LiteralPath (Join-Path $root 'PASS.txt')
