param([Parameter(Mandatory=$true)][string]$GameId, [int]$Seconds = 1800)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$taskLog = Join-Path $taskRoot ('artifacts\live-observation-{0}.jsonl' -f (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Force (Split-Path $taskLog) | Out-Null
& $taskDotnet run --project (Join-Path $taskRoot 'tests') -c Release -- --observe --game $GameId --seconds $Seconds | Tee-Object -FilePath $taskLog
if ($LASTEXITCODE -ne 0) { throw 'No live game was observed. See the log; this is not a passed live acceptance test.' }
