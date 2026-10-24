param(
    [switch]$WithAI,
    [string]$Sdk,
    [string]$Iscc,
    [string]$FfmpegDir,
    [string]$Model,
    [string]$VcRuntimeDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (!(Test-Path -LiteralPath (Join-Path $repo 'Assets\Ocr\copybuffalo-ocr.exe'))) {
    throw 'Build OCR first: pwsh -File tools/ocr/Build-Ocr.ps1'
}
$tools = Join-Path $repo '.cache\distribution-tools'
if (!$Sdk) { $Sdk = Join-Path $tools 'dotnet\dotnet.exe'; if (!(Test-Path $Sdk)) { $Sdk = (Get-Command dotnet).Source } }
if ([int]((& $Sdk --version).Split('.')[0]) -lt 10) { throw '.NET 10 SDK required.' }
if (!$Iscc) { $Iscc = Join-Path $tools 'inno\ISCC.exe' }
if (!(Test-Path $Iscc)) { throw 'Specify -Iscc with the Inno Setup 7 compiler path.' }
if (!$FfmpegDir) { $FfmpegDir = Split-Path (Get-Command ffmpeg).Source }
if (!$Model) { $Model = Join-Path $repo '.cache\models\Qwen3.5-4B-Q4_K_M.gguf' }
if (!$VcRuntimeDir) {
    $VcRuntimeDir = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022\*\VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT' -Directory -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
$stage = Join-Path $repo '.tmp\distribution\main'
$out = Join-Path $repo 'dist'
# Reused publish directories retain removed files and synthetic previews from previous checks.
foreach ($part in @('main') + $(if ($WithAI) { @('ai') } else { @() })) {
    $cleanTarget = [IO.Path]::GetFullPath((Join-Path $repo ('.tmp\distribution\' + $part)))
    if (!$cleanTarget.StartsWith([IO.Path]::GetFullPath($repo).TrimEnd('\') + '\.tmp\distribution\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path.' }
    if (Test-Path -LiteralPath $cleanTarget) {
        if ((Get-Item -LiteralPath $cleanTarget).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging directory must not be a link.' }
        Remove-Item -LiteralPath $cleanTarget -Recurse -Force
    }
}
New-Item -ItemType Directory -Force $stage, $out, (Join-Path $stage 'licenses') | Out-Null
$env:TMP = Join-Path $repo '.tmp\distribution\temp'
$env:TEMP = $env:TMP
New-Item -ItemType Directory -Force $env:TMP | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$version = ([xml](Get-Content (Join-Path $repo 'ArkCopyBuffer.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$log = Join-Path $repo '.tmp\distribution\publish.log'
& $Sdk publish (Join-Path $repo 'ArkCopyBuffer.csproj') -c Release --nologo -o $stage -p:DebugType=None *> $log
if ($LASTEXITCODE) { Get-Content $log -Tail 30; throw 'Publish failed.' }
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) { Copy-Item -LiteralPath (Join-Path $FfmpegDir $name) -Destination $stage -Force }
foreach ($notice in @(@('LICENSE', 'FFmpeg-GPLv3.txt'), @('README.txt', 'FFmpeg-build.txt'))) {
    $source = Join-Path (Split-Path $FfmpegDir) $notice[0]
    if (!(Test-Path -LiteralPath $source)) { $source = Join-Path $FfmpegDir ('licenses\' + $notice[1]) }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage ('licenses\' + $notice[1])) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL-RU.txt') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL.txt') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $stage -Force
foreach ($name in @('Dotnet-LICENSE.txt','Dotnet-THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $tools $name) -Destination (Join-Path $stage "licenses\$name") -Force
}
& $Iscc "/DAppVersion=$version" "/DStageDir=$stage" "/DOutputDir=$out" (Join-Path $PSScriptRoot 'ArkCopyBuffer.iss') *> (Join-Path $repo '.tmp\distribution\setup.log')
if ($LASTEXITCODE) { Get-Content (Join-Path $repo '.tmp\distribution\setup.log') -Tail 25; throw 'Setup compilation failed.' }
if ($WithAI) {
    $ai = Join-Path $repo '.tmp\distribution\ai'
    foreach ($backend in @('cpu','cuda')) {
        $target = Join-Path $ai $backend
        $source = Join-Path $tools "llama-$backend"
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($pattern in @('llama-server.exe','llama-server-impl.dll','llama-common.dll','llama.dll','mtmd.dll','ggml*.dll','libomp*.dll','cublas*.dll','cudart*.dll')) {
            Get-ChildItem -LiteralPath $source -Filter $pattern -File | Copy-Item -Destination $target -Force
        }
        if (!$VcRuntimeDir -or !(Test-Path $VcRuntimeDir)) { throw 'Specify -VcRuntimeDir with the official app-local Microsoft VC runtime files.' }
        Get-ChildItem -LiteralPath $VcRuntimeDir -Filter '*.dll' -File | Copy-Item -Destination $target -Force
    }
    New-Item -ItemType Directory -Force (Join-Path $ai 'models'), (Join-Path $ai 'licenses') | Out-Null
    Copy-Item -LiteralPath $Model -Destination (Join-Path $ai 'models\Qwen3.5-4B-Q4_K_M.gguf') -Force
    foreach ($name in @('llama-LICENSE.txt','Qwen-LICENSE.txt','CUDA-EULA.html','VC-REDIST.txt')) {
        Copy-Item -LiteralPath (Join-Path $tools $name) -Destination (Join-Path $ai "licenses\$name") -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $ai -Force
    & $Iscc '/DFullPack=1' "/DAppVersion=$version" "/DStageDir=$stage" "/DAiDir=$ai" "/DOutputDir=$out" (Join-Path $PSScriptRoot 'ArkCopyBuffer.iss') *> (Join-Path $repo '.tmp\distribution\ai-setup.log')
    if ($LASTEXITCODE) { Get-Content (Join-Path $repo '.tmp\distribution\ai-setup.log') -Tail 25; throw 'AI setup compilation failed.' }
    # Offscreen self-tests write preview files beside the executable; run after both installers are sealed.
    & (Join-Path $PSScriptRoot 'Test-Distribution.ps1')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL-RU.txt') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL.txt') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $out -Force
$manifest = [ordered]@{
    version = $version
    platform = 'Windows 10 1809+ / Windows 11, Intel/AMD x64'
    dotnet = '10.0.12 self-contained'
    ffmpeg = '9.0.1 full_build-www.gyan.dev'
    ai = if ($WithAI) { 'llama.cpp b9728, CUDA 13.3 + CPU, Qwen3.5-4B-Q4_K_M' } else { $null }
    modelSha256 = if ($WithAI) { (Get-FileHash $Model).Hash.ToLowerInvariant() } else { $null }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest.json') -Encoding utf8
Get-ChildItem $out -Filter '*.exe' -File | ForEach-Object { (Get-FileHash $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name } |
    Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem $out -File | Select-Object Name,Length
