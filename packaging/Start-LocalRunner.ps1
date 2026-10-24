$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$runner=Join-Path $repo '.cache\actions-runner'
$listener=Join-Path $runner 'bin\Runner.Listener.exe'
if (!(Test-Path -LiteralPath (Join-Path $runner '.runner'))) { throw 'Configure the repository runner first.' }
$running=@(Get-Process -Name 'Runner.Listener' -ErrorAction SilentlyContinue | Where-Object Path -eq $listener)
if ($running.Count) { foreach ($process in $running) { $process.PriorityClass='BelowNormal' }; 'Runner already running, PID '+$running[0].Id; return }
New-Item -ItemType Directory -Force (Join-Path $runner 'temp') | Out-Null
$env:TEMP=Join-Path $runner 'temp';$env:TMP=$env:TEMP
$process=Start-Process -FilePath $listener -ArgumentList 'run' -WorkingDirectory $runner -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runner 'listener.log') -RedirectStandardError (Join-Path $runner 'listener.log.err')
$process.PriorityClass='BelowNormal'
'Started local runner, PID '+$process.Id
