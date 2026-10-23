$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot)
$cache=Join-Path $repo '.cache\ocr'
$assets=Join-Path $repo 'Assets\Ocr'
$revision='59a6c2c33c72e4dc8e7e5500f544d6674b0e6643'
$env:CARGO_HOME=Join-Path $cache 'cargo-home'
$env:CARGO_TARGET_DIR=Join-Path $cache 'target'
$env:TEMP=Join-Path $cache 'temp';$env:TMP=$env:TEMP
$env:RUSTFLAGS='-C target-feature=+crt-static'
New-Item -ItemType Directory -Path $cache,$assets,$env:TEMP -Force | Out-Null
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
Get-PSDrive -PSProvider FileSystem | Select-Object Name,@{Name='FreeGB';Expression={[math]::Round($_.Free/1GB,1)}} | Format-Table -AutoSize
if ((Get-PSDrive ([IO.Path]::GetPathRoot($repo).TrimEnd('\').TrimEnd(':'))).Free -lt 4GB) {throw 'Need 4 GiB free on the project drive to compile OCR.'}
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    & nvidia-smi --query-gpu=name,memory.total,memory.used,memory.free,utilization.gpu --format=csv,noheader,nounits
    & nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits
}
$vendor=Join-Path $cache "vendor\pure-onnx-ocr-$revision"
$archive=Join-Path $cache 'upstream.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/siska-tech/pure-onnx-ocr/archive/$revision.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne '69E8B03FD0600EAE1A959E0C18F23F7EA9F8A4175F708918BF0789996EF79188') {throw 'Upstream OCR checksum mismatch.'}
if (-not (Test-Path -LiteralPath (Join-Path $vendor 'Cargo.toml'))) {Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $cache 'vendor')}
$models=@(
    @('det','PP-OCRv5_mobile_det_onnx','e6f4fa85f00e168c862bc462aebca69eef9b3d3d','A431985659DC921974177A95ADCFBB90FD9E51989A5E04D70D0B75F597B6E61D','98069072E1B6B37D727FD9D9F11725FAA46D6EA0DE012F2ED26CAEA011C37699'),
    @('rec','cyrillic_PP-OCRv5_mobile_rec_onnx','2cef88145434beb8afa9dd82d77d799eb1ad7b29','5371EE1DDAA7983CC62D0818D99E982B6804638C85E4F960D59A574094E172E5','5C76CC91FA98410178A09F498DB10050D0EC1634A660053D3005AB7BE581F501')
)
foreach ($model in $models) {
    $dir=Join-Path $assets $model[0];New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $files=@('inference.onnx','inference.yml')
    for ($i=0;$i -lt 2;$i++) {
        $path=Join-Path $dir $files[$i]
        if (-not (Test-Path -LiteralPath $path)) {Invoke-WebRequest -Uri "https://huggingface.co/PaddlePaddle/$($model[1])/resolve/$($model[2])/$($files[$i])" -OutFile $path}
        if ((Get-FileHash -LiteralPath $path).Hash -ne $model[$i+3]) {throw "OCR model checksum mismatch: $path"}
    }
}
& cargo build --release --locked --jobs 2 --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') *> (Join-Path $cache 'build.log')
if ($LASTEXITCODE -ne 0) {Select-String -LiteralPath (Join-Path $cache 'build.log') -Pattern '^error|failed' -Context 1,4;throw 'OCR build failed.'}
Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release\copybuffalo-ocr.exe') -Destination $assets -Force
& cargo metadata --locked --format-version 1 --filter-platform x86_64-pc-windows-msvc --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') > (Join-Path $cache 'metadata.json') 2> (Join-Path $cache 'metadata.log')
if ($LASTEXITCODE -ne 0) {throw 'OCR dependency metadata failed.'}
$metadata=Get-Content -LiteralPath (Join-Path $cache 'metadata.json') -Raw | ConvertFrom-Json
$licenses=Join-Path $assets 'licenses';New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$metadata.packages | Sort-Object name,version | ForEach-Object {"$($_.name) $($_.version): $($_.license)"} | Set-Content -LiteralPath (Join-Path $licenses 'dependencies.txt') -Encoding utf8
foreach ($package in $metadata.packages) {
    $source=Split-Path $package.manifest_path
    if (-not ([IO.Path]::GetFullPath($source)).StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'OCR license source outside project.'}
    $files=@(Get-ChildItem -LiteralPath $source -File | Where-Object Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)')
    if ($package.license_file) {
        $licensePath=[IO.Path]::GetFullPath((Join-Path $source $package.license_file))
        if (-not $licensePath.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Invalid OCR dependency license path.'}
        if (Test-Path -LiteralPath $licensePath) {$files+=Get-Item -LiteralPath $licensePath}
    }
    $dest=Join-Path $licenses "$($package.name)-$($package.version)"
    if ($files.Count) {New-Item -ItemType Directory -Path $dest -Force | Out-Null;foreach ($file in $files) {Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dest $file.Name) -Force}}
}
if (-not (Test-Path -LiteralPath (Join-Path $licenses 'Apache-2.0.txt'))) {Invoke-WebRequest -Uri 'https://www.apache.org/licenses/LICENSE-2.0.txt' -OutFile (Join-Path $licenses 'Apache-2.0.txt')}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NOTICE.txt') -Destination (Join-Path $licenses 'NOTICE.txt') -Force
'OCR executable, pinned models and dependency licenses ready in Assets\Ocr.'
