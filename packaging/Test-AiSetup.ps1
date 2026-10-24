param([string]$Directory=(Join-Path (Split-Path $PSScriptRoot) '.tmp\ai-setup-check'))
$ErrorActionPreference='Stop'
$Directory=[IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Force $Directory | Out-Null
trap { $_ | Out-String | Set-Content -LiteralPath (Join-Path $Directory 'ERROR.txt'); exit 1 }
function Check([bool]$Condition,[string]$Message) {
    if (!$Condition) { throw $Message }
    'PASS: '+$Message | Add-Content -LiteralPath (Join-Path $Directory 'result.log')
}
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
public sealed class CopyBuffaloAiTestServer : IDisposable {
    readonly HttpListener listener=new HttpListener(); readonly Thread worker; readonly byte[] data;
    public string Url {get;private set;} public int Requests; public bool Corrupt;
    public CopyBuffaloAiTestServer(byte[] bytes) {
        data=bytes;var port=new TcpListener(IPAddress.Loopback,0);port.Start();int number=((IPEndPoint)port.LocalEndpoint).Port;port.Stop();
        Url="http://127.0.0.1:"+number+"/";listener.Prefixes.Add(Url);listener.Start();
        worker=new Thread(Serve);worker.IsBackground=true;worker.Start();
    }
    void Serve() {
        while(listener.IsListening) {
            HttpListenerContext context;try {context=listener.GetContext();}catch {break;}
            Interlocked.Increment(ref Requests);
            try {
                if(context.Request.HttpMethod!="GET"||context.Request.Url.AbsolutePath!="/CopyBuffalo-Full-Setup.exe.part1") {context.Response.StatusCode=404;continue;}
                byte[] bytes=(byte[])data.Clone();if(Corrupt) bytes[0]^=255;
                context.Response.ContentLength64=bytes.Length;context.Response.OutputStream.Write(bytes,0,bytes.Length);
            } finally {context.Response.Close();}
        }
    }
    public void Dispose() {listener.Stop();worker.Join(2000);listener.Close();}
}
'@
$fixture=Join-Path $Directory 'dist'
if (Test-Path -LiteralPath $fixture) { throw 'Use a fresh AI setup check directory.' }
New-Item -ItemType Directory -Force $fixture | Out-Null
$bytes=[byte[]]::new(8192)
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
[IO.File]::WriteAllBytes((Join-Path $fixture 'CopyBuffalo-Full-Setup.exe'),$bytes)
[IO.File]::WriteAllBytes((Join-Path $fixture 'CopyBuffalo-Setup.exe'),[byte[]](1,2,3))
foreach ($name in @('INSTALL.txt','INSTALL-RU.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixture }
Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'LICENSE') -Destination $fixture
$version=([xml](Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'ArkCopyBuffer.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1
@{version=$version} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'manifest.json')
& (Join-Path $PSScriptRoot 'Prepare-GitHubRelease.ps1') -Dist $fixture *> (Join-Path $Directory 'prepare.log')
$generated=Join-Path $fixture 'github\CopyBuffalo-Full-Setup.cmd'
$text=[IO.File]::ReadAllText($generated)
$base='https://github.com/Arkanoidvfx/CopyBuffalo/releases/download/v'+$version+'/'
Check ($text.Contains($base) -and !$text.Contains('/releases/latest/')) 'AI launcher pins its own release version'
Check ((($text -split '\r?\n' | ForEach-Object Length | Measure-Object -Maximum).Maximum) -lt 8191) 'CMD command lines fit the Windows length limit'
Check ((Test-Path -LiteralPath (Join-Path $fixture 'github\CopyBuffalo-Setup.exe')) -and !(Get-ChildItem -LiteralPath (Join-Path $fixture 'github') -Filter 'ArkCopyBuffer*')) 'prepared installer assets use CopyBuffalo filenames'
function Make-Case([string]$Name,[string]$Url) {
    $path=Join-Path $Directory $Name
    New-Item -ItemType Directory -Force $path | Out-Null
    $cmd=Join-Path $path 'CopyBuffalo-Full-Setup.cmd'
    [IO.File]::WriteAllText($cmd,$text.Replace($base,$Url),[Text.Encoding]::ASCII)
    return $cmd
}
function Run-Case([string]$Cmd,[int]$Expected) {
    $log=Join-Path ([IO.Path]::GetDirectoryName($Cmd)) ([Guid]::NewGuid().ToString('N')+'.log')
    $process=Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /c ""'+$Cmd+'""') -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log+'.err')
    if (!$process.WaitForExit(30000)) { $process.Kill();throw 'AI launcher test timed out.' }
    Check ($process.ExitCode -eq $Expected) ('AI launcher exit code '+$Expected)
}
$server=[CopyBuffaloAiTestServer]::new($bytes)
$savedVerify=$env:ARKCOPYBUFFER_VERIFY_ONLY
$env:ARKCOPYBUFFER_VERIFY_ONLY='1'
try {
    $cmd=Make-Case "download with ' quote" $server.Url
    Run-Case $cmd 0
    $casePath=[IO.Path]::GetDirectoryName($cmd)
    $target=Join-Path $casePath 'CopyBuffalo-Full-Setup.exe'
    $part=Join-Path $casePath 'CopyBuffalo-Full-Setup.exe.part1'
    Check ($server.Requests -eq 1 -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath (Join-Path $fixture 'CopyBuffalo-Full-Setup.exe')).Hash) 'missing AI part downloads and assembles exact verified bytes'
    Run-Case $cmd 0
    Check ($server.Requests -eq 1) 'verified assembled installer is reused without downloading'
    $server.Dispose();$server=$null
    Remove-Item -LiteralPath $target
    Run-Case $cmd 0
    Check (Test-Path -LiteralPath $target) 'existing verified parts assemble with no network server'
    [IO.File]::WriteAllBytes($target,[byte[]](1,2,3))
    Run-Case $cmd 1
    Check ((Get-Item -LiteralPath $target).Length -eq 3) 'tampered assembled installer is refused'
    Remove-Item -LiteralPath $target
    [IO.File]::WriteAllBytes($part,[byte[]](1,2,3))
    Run-Case $cmd 1
    Check (!(Test-Path -LiteralPath $target) -and !(Test-Path -LiteralPath ($target+'.assembling'))) 'tampered existing part is refused before assembly'
    $server=[CopyBuffaloAiTestServer]::new($bytes);$server.Corrupt=$true
    $bad=Make-Case 'bad download' $server.Url
    Run-Case $bad 1
    Check (!(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($bad)) -Filter '*.exe*')) 'corrupt download leaves no part, installer or temporary payload'
    $missing=Make-Case 'network failure' ($server.Url+'missing/')
    Run-Case $missing 1
    Check (!(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($missing)) -Filter '*.exe*')) 'failed download cleans the partial payload'
} finally {
    if ($server) { $server.Dispose() }
    $env:ARKCOPYBUFFER_VERIFY_ONLY=$savedVerify
}
'PASS: AI setup download, offline reuse, hashes, tamper refusal, network failure cleanup and quoted paths.' | Add-Content -LiteralPath (Join-Path $Directory 'result.log')
Get-Content -LiteralPath (Join-Path $Directory 'result.log')
