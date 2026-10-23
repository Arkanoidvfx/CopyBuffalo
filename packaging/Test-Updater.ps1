param(
    [string]$Sdk = (Join-Path (Split-Path $PSScriptRoot) '.cache\distribution-tools\dotnet\dotnet.exe'),
    [string]$App = (Join-Path (Split-Path $PSScriptRoot) 'bin\Release\net10.0-windows\win-x64\ArkCopyBuffer.exe')
)
$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path $PSScriptRoot) '.tmp\stability-update\helper-check'
$source = Join-Path $root 'source'
$fixture = Join-Path $root 'fixture'
New-Item -ItemType Directory -Force $source, $fixture | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>UpdateFixture</AssemblyName></PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $source 'Fixture.csproj') -Encoding utf8
@'
using System;
using System.IO;
class Fixture {
    static int Main(string[] args) {
        string root = AppContext.BaseDirectory;
        if (args.Length == 1 && args[0] == "--wait") { System.Threading.Thread.Sleep(400); File.WriteAllText(Path.Combine(root, "old-exited.txt"), "ok"); return 0; }
        if (args.Length == 0) { File.WriteAllText(Path.Combine(root, "restarted.txt"), "ok"); return 0; }
        File.WriteAllLines(Path.Combine(root, "invoked.txt"), args);
        if (File.Exists(Path.Combine(root, "fail"))) return 5;
        foreach (string arg in args) if (arg.StartsWith("/DIR=")) {
            Directory.CreateDirectory(arg.Substring(5));
            File.WriteAllText(Path.Combine(arg.Substring(5), "payload.txt"), "new version");
            return 0;
        }
        return 6;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $source 'Program.cs') -Encoding utf8
& $Sdk build (Join-Path $source 'Fixture.csproj') -c Release -o $fixture --nologo *> (Join-Path $root 'build.log')
if ($LASTEXITCODE) { throw 'Updater fixture build failed; see helper-check/build.log.' }
$env:DOTNET_ROOT = Split-Path $Sdk
$process = Start-Process -FilePath $App -ArgumentList @('--update-helper-selftest', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Updater helper test timed out.' }
if ($process.ExitCode) { throw 'Updater helper test failed; see helper-check/test-error.txt.' }
Get-Content -LiteralPath (Join-Path $root 'result.txt')
