param([string]$Dist = (Join-Path (Split-Path $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$Dist = [IO.Path]::GetFullPath($Dist)
$output = Join-Path $Dist 'github'
New-Item -ItemType Directory -Force $output | Out-Null
$full = Join-Path $Dist 'ArkCopyBuffer-Full-Setup.exe'
$hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $full).Length
if ((Get-PSDrive -Name ([IO.Path]::GetPathRoot($Dist).TrimEnd(':\'))).Free -lt $size + 1GB) { throw 'Not enough free disk space to prepare release assets.' }
$inputFile = [IO.File]::OpenRead($full)
$combined = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
$buffer = [byte[]]::new(1MB)
$parts = [Collections.Generic.List[string]]::new()
try {
    for ($index = 1; $inputFile.Position -lt $inputFile.Length; $index++) {
        $part = Join-Path $output "ArkCopyBuffer-Full-Setup.exe.part$index"
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
$partList = ($parts | ForEach-Object { "'$_'" }) -join ','
$launcher = @'
@echo off
setlocal
set "ARKCOPYBUFFER_DOWNLOAD_DIR=%~dp0"
echo Preparing the full CopyBuffalo installer. Please wait...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$ErrorActionPreference='Stop'; try { $dir=$env:ARKCOPYBUFFER_DOWNLOAD_DIR; $target=[IO.Path]::Combine($dir,'ArkCopyBuffer-Full-Setup.exe'); function Verify([string]$path) { $s=[IO.File]::OpenRead($path); $sha=[Security.Cryptography.SHA256]::Create(); try { $actual=[BitConverter]::ToString($sha.ComputeHash($s)).Replace('-',''); if ($actual -ne '__HASH__') { throw 'Installer SHA-256 mismatch. Download the parts again.' } } finally { $s.Dispose(); $sha.Dispose() } }; if (![IO.File]::Exists($target)) { $parts=@(__PARTS__); foreach ($name in $parts) { if (![IO.File]::Exists([IO.Path]::Combine($dir,$name))) { throw ('Missing file: '+$name+'. Put all parts beside Install-Full.cmd.') } }; if (([IO.DriveInfo]::new([IO.Path]::GetPathRoot($dir))).AvailableFreeSpace -lt __SIZE__+536870912) { throw 'Not enough free space to assemble the installer.' }; $temp=$target+'.assembling'; $out=[IO.File]::Create($temp); try { foreach ($name in $parts) { $source=[IO.File]::OpenRead([IO.Path]::Combine($dir,$name)); try { $source.CopyTo($out) } finally { $source.Dispose() } } } finally { $out.Dispose() }; Verify $temp; [IO.File]::Move($temp,$target) }; Verify $target; if ($env:ARKCOPYBUFFER_VERIFY_ONLY -ne '1') { [Diagnostics.Process]::Start($target) | Out-Null }; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"
if errorlevel 1 (
  echo Full installer preparation failed.
  if not "%ARKCOPYBUFFER_VERIFY_ONLY%"=="1" pause
  exit /b 1
)
'@
$launcher.Replace('__HASH__', $hash).Replace('__PARTS__', $partList).Replace('__SIZE__', "$size") |
    Set-Content -LiteralPath (Join-Path $output 'Install-Full.cmd') -Encoding ascii
foreach ($name in @('ArkCopyBuffer-Setup.exe', 'INSTALL.txt', 'INSTALL-RU.txt', 'manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $Dist $name) -Destination $output -Force
}
$sums = @("$hash  ArkCopyBuffer-Full-Setup.exe") + @(Get-ChildItem -LiteralPath $output -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
$sums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem -LiteralPath $output -File | Select-Object Name, Length
