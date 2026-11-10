param(
    [string]$App = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\win-x64\ArkCopyBuffer.exe'),
    [string]$Output = (Join-Path $PSScriptRoot '..\.tmp\history-map\checks')
)
$ErrorActionPreference = 'Stop'
$App = (Resolve-Path -LiteralPath $App).Path
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $Output | Out-Null
$env:TEMP = $Output; $env:TMP = $Output
# Reuse the existing isolated-station helper without invoking its build/deployment flow.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Build-Local.ps1') -Raw
$definition = [regex]::Match($source, "(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
if (!$definition.Success) { throw 'Build-Local isolation helper was not found.' }
if (!("LocalBuildIsolation" -as [type])) { Add-Type -TypeDefinition $definition.Groups[1].Value }
$station = [LocalBuildIsolation]::Open()
try {
    $testProcess = [LocalBuildIsolation]::Start($App, ('--history-map-selftest "' + $Output + '"'))
    $result = [LocalBuildIsolation]::Wait($testProcess, 90000)
    if ($result -eq [int]::MinValue) { [LocalBuildIsolation]::Stop($testProcess) }
    Get-Content -LiteralPath (Join-Path $Output 'selftest.log') -Tail 50
    if ($result -ne 0) { throw "History map checks failed: $result" }
    "PASS: isolated history map checks; captures in $Output"
} finally { [LocalBuildIsolation]::Close() }
