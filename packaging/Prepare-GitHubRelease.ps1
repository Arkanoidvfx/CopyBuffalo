param([string]$Dist = (Join-Path (Split-Path $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$Dist = [IO.Path]::GetFullPath($Dist)
$output = Join-Path $Dist 'github'
New-Item -ItemType Directory -Force $output | Out-Null
$full = Join-Path $Dist 'CopyBuffalo-Full-Setup.exe'
$hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $full).Length
if ((Get-PSDrive -Name ([IO.Path]::GetPathRoot($Dist).TrimEnd(':\'))).Free -lt $size + 1GB) { throw 'Not enough free disk space to prepare release assets.' }
$inputFile = [IO.File]::OpenRead($full)
$combined = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
$buffer = [byte[]]::new(1MB)
$parts = [Collections.Generic.List[string]]::new()
try {
    for ($index = 1; $inputFile.Position -lt $inputFile.Length; $index++) {
        $part = Join-Path $output "CopyBuffalo-Full-Setup.exe.part$index"
        $parts.Add([IO.Path]::GetFileName($part))
        $stream = [IO.File]::Create($part)
        try {
            $remaining = 1900000000L
            while ($remaining -gt 0 -and ($count = $inputFile.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))) -gt 0) {
                $stream.Write($buffer, 0, $count)
                $remaining -= $count
            }
        } finally { $stream.Dispose() }
        # Read back the actual files before publishing; combined bytes must match the original installer.
        $verify = [IO.File]::OpenRead($part)
        try { while (($count = $verify.Read($buffer, 0, $buffer.Length)) -gt 0) { $combined.AppendData($buffer, 0, $count) } }
        finally { $verify.Dispose() }
    }
    if ([Convert]::ToHexString($combined.GetHashAndReset()).ToLowerInvariant() -ne $hash) { throw 'Split installer roundtrip SHA-256 mismatch.' }
} finally { $inputFile.Dispose(); $combined.Dispose() }
$version = (Get-Content -LiteralPath (Join-Path $Dist 'manifest.json') -Raw | ConvertFrom-Json).version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version for AI launcher.' }
$partTable = ($parts | ForEach-Object {
    $path = Join-Path $output $_
    "@{Name='$_';Size=$((Get-Item -LiteralPath $path).Length);Hash='$((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant())'}"
}) -join ','
$downloadScript = @'
$ErrorActionPreference='Stop'
$temporary=$null
try {
    $dir=$env:COPYBUFFALO_DOWNLOAD_DIR
    $target=[IO.Path]::Combine($dir,'CopyBuffalo-Full-Setup.exe')
    function Verify([string]$path,[string]$expected) {
        $stream=[IO.File]::OpenRead($path)
        $sha=[Security.Cryptography.SHA256]::Create()
        try {
            $actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')
            if ($actual -ne $expected) { throw ('SHA-256 mismatch: '+[IO.Path]::GetFileName($path)+'. Delete the damaged file and run this launcher again.') }
        } finally { $stream.Dispose();$sha.Dispose() }
    }
    if (![IO.File]::Exists($target)) {
        $parts=@(__PARTS__)
        $required=__SIZE__+536870912
        foreach ($part in $parts) {
            if (![IO.File]::Exists([IO.Path]::Combine($dir,$part.Name))) { $required+=$part.Size }
        }
        if (([IO.DriveInfo]::new([IO.Path]::GetPathRoot($dir))).AvailableFreeSpace -lt $required) { throw 'Not enough free space for the AI download and installer assembly.' }
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        foreach ($part in $parts) {
            $path=[IO.Path]::Combine($dir,$part.Name)
            if (![IO.File]::Exists($path)) {
                Write-Host ('Downloading '+$part.Name+'...')
                $temporary=$path+'.downloading'
                $client=[Net.WebClient]::new()
                try { $client.DownloadFile('https://github.com/Arkanoidvfx/CopyBuffalo/releases/download/v__VERSION__/'+$part.Name,$temporary) }
                finally { $client.Dispose() }
                Verify $temporary $part.Hash
                [IO.File]::Move($temporary,$path)
                $temporary=$null
            }
            Verify $path $part.Hash
        }
        Write-Host 'Verifying and assembling the full CopyBuffalo installer...'
        $temporary=$target+'.assembling'
        $out=[IO.File]::Create($temporary)
        try {
            foreach ($part in $parts) {
                $partStream=[IO.File]::OpenRead([IO.Path]::Combine($dir,$part.Name))
                try { $partStream.CopyTo($out) } finally { $partStream.Dispose() }
            }
        } finally { $out.Dispose() }
        Verify $temporary '__HASH__'
        [IO.File]::Move($temporary,$target)
        $temporary=$null
    }
    Verify $target '__HASH__'
    if ($env:ARKCOPYBUFFER_VERIFY_ONLY -ne '1') { [Diagnostics.Process]::Start($target) | Out-Null }
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message);exit 1 }
finally { if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
'@
$downloadScript=$downloadScript.Replace('__HASH__',$hash).Replace('__PARTS__',$partTable).Replace('__SIZE__',"$size").Replace('__VERSION__',$version)
$launcher = @'
@echo off
setlocal
set "COPYBUFFALO_DOWNLOAD_DIR=%~dp0"
set "COPYBUFFALO_LAUNCHER=%~f0"
echo Preparing the full CopyBuffalo AI installer. Please wait...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$payload=[IO.File]::ReadAllText($env:COPYBUFFALO_LAUNCHER) -split '(?m)^# COPYBUFFALO_PAYLOAD\r?$',2; & ([ScriptBlock]::Create($payload[1]))"
if errorlevel 1 (
  echo Full installer preparation failed.
  if not "%ARKCOPYBUFFER_VERIFY_ONLY%"=="1" pause
  exit /b 1
)
exit /b 0
# COPYBUFFALO_PAYLOAD
__PAYLOAD__
'@
$launcher.Replace('__PAYLOAD__',$downloadScript) |
    Set-Content -LiteralPath (Join-Path $output 'CopyBuffalo-Full-Setup.cmd') -Encoding ascii
foreach ($name in @('CopyBuffalo-Setup.exe', 'INSTALL.txt', 'INSTALL-RU.txt', 'LICENSE', 'manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $Dist $name) -Destination $output -Force
}
$sums = @("$hash  CopyBuffalo-Full-Setup.exe") + @(Get-ChildItem -LiteralPath $output -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
$sums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem -LiteralPath $output -File | Select-Object Name, Length
