# This file defines functions only and never starts or stops a recording when imported.
# Dot-sourcing binds these functions to the caller's private, profilePath, profile, etl, wpr, and owned script state.
function Invoke-PrivateWpr([string[]] $Arguments, [string] $Label, [switch] $StartingCapture) {
    $output = Join-Path $private ($Label + '.txt')
    & $wpr @Arguments *> $output
    $code = $LASTEXITCODE
    if ($StartingCapture -and $code -eq 0) { $script:owned = $true }
    Write-Host "NATIVE_WAIT_TOOL stage=$Label exit=$code"
    return @{ Code = $code; Text = [IO.File]::ReadAllText($output) }
}

function Start-PrivateNativeWaitCapture {
    try {
        $script:wpr = (Get-Command wpr.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        [xml]$definition = [IO.File]::ReadAllText($profilePath)
        $collectors = @($definition.WindowsPerformanceRecorder.Profiles.SystemCollector)
        $profiles = @($definition.WindowsPerformanceRecorder.Profiles.Profile)
        if ($collectors.Count -ne 1 -or $profiles.Count -ne 1 -or
            $profiles[0].LoggingMode -ne 'Memory' -or
            $collectors[0].BufferSize.Value -ne '1024' -or
            $collectors[0].Buffers.Value -ne '128' -or
            $collectors[0].Buffers.PercentageOfTotalMemory -ne 'false' -or
            $null -ne $definition.SelectSingleNode('//EventCollector|//HeapEventCollector|//HypervisorEventCollector|//*[@Base]')) {
            throw 'Unknown allocation.'
        }
        $inventory = Invoke-PrivateWpr @('-profiles', $profilePath) 'inventory'
        $details = Invoke-PrivateWpr @('-profiledetails', $profile) 'details'
        $valid = $inventory.Code -eq 0 -and $details.Code -eq 0
        foreach ($keyword in @('ProcessThread', 'CSwitch', 'ReadyThread', 'FileIO', 'FileIOInit', 'DiskIO', 'DiskIOInit', 'FileDirEnum', 'FileOpEnd', 'DiskReadInit', 'DiskWriteInit')) {
            $valid = $valid -and $details.Text -match ('\b' + $keyword + '\b')
        }
        if (-not $valid) { return @{ Owned = $false; Status = 11 } }
        $status = Invoke-PrivateWpr @('-status') 'before'
        if ($status.Code -ne 0 -or $status.Text -notmatch '\bWPR is not recording\b') {
            return @{ Owned = $false; Status = 12 }
        }
        $start = Invoke-PrivateWpr @('-start', $profile, '-recordtempto', $private) 'start' -StartingCapture
        if ($start.Code -eq 0) { return @{ Owned = $true; Status = 13 } }
        return @{ Owned = $false; Status = 14 }
    }
    catch { return @{ Owned = $owned; Status = 15 } }
}

# Callers invoke this function only after their own successful start.
function Stop-PrivateNativeWaitCapture {
    $knownLoss = $false
    $lost = 0L
    $droppedCount = 0L
    $stopExit = 125
    try {
        $status = Invoke-PrivateWpr @('-status', 'collectors', '-details') 'collectors'
        $loss = [regex]::Matches($status.Text, '(?im)^\s*Events Lost\s*:\s*(\d+)\s*$')
        $dropped = [regex]::Match($status.Text, '(?im)^\s*Dropped event\s*:\s*(\d+)\s*$')
        $knownLoss = $status.Code -eq 0 -and $loss.Count -eq 1 -and $dropped.Success
        if ($knownLoss) {
            $lost = [uint64]$loss[0].Groups[1].Value
            $droppedCount = [uint64]$dropped.Groups[1].Value
            Write-Host "NATIVE_WAIT_LIVE_LOSS known=1 eventsLost=$lost droppedEvents=$droppedCount"
        }
        else { Write-Host 'NATIVE_WAIT_LIVE_LOSS known=0' }
    }
    catch { $knownLoss = $false; $lost = 0 }
    finally {
        try {
            $stop = Invoke-PrivateWpr @('-stop', $etl, '-skipPdbGen') 'stop'
            $stopExit = $stop.Code
        }
        catch { $stopExit = 125 }
    }
    return @{ Code = $stopExit; KnownLoss = $knownLoss; Lost = $lost; Dropped = $droppedCount }
}
