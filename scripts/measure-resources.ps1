param([int]$Seconds = 3600, [int]$IntervalSeconds = 5, [int]$ProcessId = 0)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 1 -or $IntervalSeconds -lt 1) { throw 'Durations must be positive.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProcesses = @(Get-Process NbaLive -ErrorAction SilentlyContinue)
if ($ProcessId -gt 0) { $taskProcess = Get-Process -Id $ProcessId }
elseif ($taskProcesses.Count -eq 1) { $taskProcess = $taskProcesses[0] }
else { throw 'Start exactly one NbaLive instance, or pass -ProcessId.' }
$taskReportDir = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force $taskReportDir | Out-Null
$taskStart = Get-Date
$taskSamples = [Collections.Generic.List[object]]::new()
$taskLastTime = $taskStart
$taskLastCpu = $taskProcess.TotalProcessorTime.TotalSeconds
while (((Get-Date) - $taskStart).TotalSeconds -lt $Seconds) {
    Start-Sleep -Seconds $IntervalSeconds
    $taskProcess.Refresh()
    if ($taskProcess.HasExited) { throw 'App exited before resource test completed.' }
    $taskNow = Get-Date
    $taskCpu = $taskProcess.TotalProcessorTime.TotalSeconds
    $taskSamples.Add([pscustomobject]@{
        Time = $taskNow.ToString('O')
        ElapsedSeconds = [Math]::Round(($taskNow - $taskStart).TotalSeconds, 1)
        CpuPercent = [Math]::Round(100 * ($taskCpu-$taskLastCpu) / ($taskNow-$taskLastTime).TotalSeconds / [Environment]::ProcessorCount, 3)
        WorkingSetMB = [Math]::Round($taskProcess.WorkingSet64 / 1MB, 2)
        PrivateMB = [Math]::Round($taskProcess.PrivateMemorySize64 / 1MB, 2)
        Handles = $taskProcess.HandleCount
    })
    $taskLastTime = $taskNow
    $taskLastCpu = $taskCpu
}
$taskPrefix = Join-Path $taskReportDir ('resources-{0}' -f $taskStart.ToString('yyyyMMdd-HHmmss'))
$taskSamples | Export-Csv -LiteralPath ($taskPrefix + '.csv') -NoTypeInformation -Encoding utf8
$taskSummary = [pscustomobject]@{
    ProcessId = $taskProcess.Id
    DurationSeconds = [Math]::Round(((Get-Date)-$taskStart).TotalSeconds, 1)
    Samples = $taskSamples.Count
    AverageCpuPercent = [Math]::Round(($taskSamples | Measure-Object CpuPercent -Average).Average, 3)
    PeakWorkingSetMB = ($taskSamples | Measure-Object WorkingSetMB -Maximum).Maximum
    FirstWorkingSetMB = $taskSamples[0].WorkingSetMB
    LastWorkingSetMB = $taskSamples[-1].WorkingSetMB
    OneHourCompleted = $Seconds -ge 3600
}
$taskSummary | ConvertTo-Json | Tee-Object -FilePath ($taskPrefix + '.json')
