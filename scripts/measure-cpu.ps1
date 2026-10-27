param(
    [Parameter(Mandatory)][int]$RamMxPid,
    [ValidateRange(10,3600)][int]$DurationSeconds = 60,
    [ValidateRange(0,60)][int]$WarmupSeconds = 10
)
$ErrorActionPreference = 'Stop'
# Passive process counters only: no UI Automation connection or second all-process sampler.
$rammxTarget = Get-Process -Id $RamMxPid
if ($rammxTarget.ProcessName -ine 'RamMx') { throw 'Target must be RamMx' }
try {
    Start-Sleep -Seconds $WarmupSeconds
    $rammxTarget.Refresh()
    if ($rammxTarget.HasExited) { throw 'RamMx exited before measurement' }
    $rammxStartCpu = $rammxTarget.TotalProcessorTime.TotalSeconds
    $rammxStartHandles = $rammxTarget.HandleCount
    $rammxPeakWorking = 0L
    $rammxPeakCommit = 0L
    $rammxSamples = 0
    $rammxTimer = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Seconds 2
        $rammxTarget.Refresh()
        if ($rammxTarget.HasExited) { throw 'RamMx exited during measurement' }
        $rammxPeakWorking = [Math]::Max($rammxPeakWorking, $rammxTarget.WorkingSet64)
        $rammxPeakCommit = [Math]::Max($rammxPeakCommit, $rammxTarget.PrivateMemorySize64)
        $rammxSamples++
    } while ($rammxTimer.Elapsed.TotalSeconds -lt $DurationSeconds)
    [ordered]@{
        DurationSeconds = $rammxTimer.Elapsed.TotalSeconds
        WarmupSeconds = $WarmupSeconds
        LogicalProcessors = [Environment]::ProcessorCount
        Samples = $rammxSamples
        AppAverageCpuPercent = ($rammxTarget.TotalProcessorTime.TotalSeconds - $rammxStartCpu) / $rammxTimer.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100
        WorkingSetMaxMiB = $rammxPeakWorking / 1MB # Includes shared pages; not private working set.
        PrivateCommitMaxMiB = $rammxPeakCommit / 1MB
        HandlesStart = $rammxStartHandles
        HandlesEnd = $rammxTarget.HandleCount
    } | ConvertTo-Json
} finally { $rammxTarget.Dispose() }
