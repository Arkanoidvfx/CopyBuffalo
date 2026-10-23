param([string]$StageRoot = (Join-Path (Split-Path $PSScriptRoot) '.tmp\distribution'))
$ErrorActionPreference = 'Stop'
$env:TMP = Join-Path $StageRoot 'temp'
$env:TEMP = $env:TMP
New-Item -ItemType Directory -Force $env:TMP | Out-Null
$env:DOTNET_ROOT = Join-Path $StageRoot 'no-dotnet'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$savedPath = $env:PATH
function Check-Process([string]$Exe, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(15000)) { $process.Kill(); throw "Timed out: $Exe" }
    if ($process.ExitCode) { throw "Failed ($($process.ExitCode)): $Exe" }
}
try {
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $exe = Join-Path $StageRoot 'main\ArkCopyBuffer.exe'
    Check-Process $exe @('--selftest')
    $audioDir = Join-Path $StageRoot 'audio-check'
    Check-Process $exe @('--audio-selftest', ('"' + $audioDir + '"'))
    foreach ($part in @('copybuffalo-ocr.exe','det\inference.onnx','det\inference.yml','rec\inference.onnx','rec\inference.yml','licenses\NOTICE.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $StageRoot "main\ocr\$part"))) {throw "Missing OCR bundle file: $part"}
    }
    $ocrDir=Join-Path $StageRoot 'ocr-check';New-Item -ItemType Directory -Path $ocrDir -Force | Out-Null
    Add-Type -AssemblyName System.Drawing
    $bitmap=[Drawing.Bitmap]::new(640,120);$graphics=[Drawing.Graphics]::FromImage($bitmap)
    $font=[Drawing.Font]::new('Segoe UI',18,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.DrawString('Clipboard 0123456789',$font,[Drawing.Brushes]::Black,20,30)
        $bitmap.Save((Join-Path $ocrDir 'smoke.png'),[Drawing.Imaging.ImageFormat]::Png)
    }
    finally {$font.Dispose();$graphics.Dispose();$bitmap.Dispose()}
    ConvertTo-Json -InputObject @(@{id='smoke'}) | Set-Content -LiteralPath (Join-Path $ocrDir 'manifest.json') -Encoding utf8
    Check-Process $exe @('--ocr-selftest',('"'+$ocrDir+'"'))
    $recognized=Get-Content -LiteralPath (Join-Path $ocrDir 'ocr-results.json') -Raw | ConvertFrom-Json
    if ($recognized[0].text -notlike '*0123456789*') {throw 'Bundled OCR synthetic text mismatch.'}
    foreach ($backend in @('cpu','cuda')) {
        $server = Join-Path $StageRoot "ai\$backend\llama-server.exe"
        if (Test-Path $server) { Check-Process $server @('--version') }
    }
    'OK: self-contained app, encrypted history checks, bundled FFmpeg/OCR, optional CPU/CUDA native dependencies.'
}
finally { $env:PATH = $savedPath }
