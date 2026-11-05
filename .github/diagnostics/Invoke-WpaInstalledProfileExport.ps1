$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$private = Join-Path (Get-Location).Path ('.artifacts/wpa-export-' + [guid]::NewGuid().ToString('N'))
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'capture.etl'
$wpr = $null
$owned = $false
$stopAttempted = $false
$created = $false
$captureVerified = $false
$measurementStatus = 40
$phase = 0
$exitCode = 1
$current = $null
$threads = $null
$targetProcessId = 0U
$targetThreadIds = [Collections.Generic.HashSet[uint32]]::new()
. (Join-Path $PSScriptRoot 'NativeWaitCapture.ps1')
. (Join-Path $PSScriptRoot 'NativeWaitWpa.ps1')
try {
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    $readinessDirectory = Join-Path $private 'readiness'
    $null = New-Item -ItemType Directory -Path $readinessDirectory
    $readiness = Get-PrivateNativeWpaPrerequisites $readinessDirectory
    $measurementStatus = [int]$readiness.Status
    Write-Host "WPA_EXPORT_PREREQUISITE status=$measurementStatus recordingStarted=0"
    if ($measurementStatus -ne 0) { throw [InvalidOperationException]::new() }
    $phase = 1
    $current = [Diagnostics.Process]::GetCurrentProcess()
    $targetProcessId = [uint32]$current.Id
    $threads = $current.Threads
    foreach ($thread in $threads) {
        try {
            if ($targetThreadIds.Count -lt 1024 -and $thread.Id -gt 0) { $null = $targetThreadIds.Add([uint32]$thread.Id) }
        }
        finally { $thread.Dispose() }
    }
    $threads = $null
    $current.Dispose()
    $current = $null
    Write-Host "WPA_EXPORT_OWN_TARGET processKnown=$([int]($targetProcessId -gt 0)) threadCount=$($targetThreadIds.Count) threadLimit=1024"
    if ($targetProcessId -eq 0 -or $targetThreadIds.Count -eq 0) { throw [InvalidOperationException]::new() }
    $phase = 2
    $start = Start-PrivateNativeWaitCapture
    Write-Host "WPA_EXPORT_CAPTURE started=$([int]$owned) status=$($start.Status) durationMs=4000 memoryMiB=128"
    if (-not $owned) { throw [InvalidOperationException]::new() }
    Start-Sleep -Seconds 4
    $stopAttempted = $true
    $stop = Stop-PrivateNativeWaitCapture
    $phase = 3
    $etlBytes = if ([IO.File]::Exists($etl)) { ([IO.FileInfo]::new($etl)).Length } else { -1L }
    $captureVerified = $stop.Code -eq 0 -and $stop.KnownLoss -and $stop.Lost -eq 0 -and $stop.Dropped -eq 0 -and $etlBytes -gt 0 -and $etlBytes -le 268435456
    Write-Host "WPA_EXPORT_CAPTURE_RESULT stopExit=$($stop.Code) lossKnown=$([int]$stop.KnownLoss) lost=$($stop.Lost) dropped=$($stop.Dropped) etlBytes=$etlBytes accepted=$([int]$captureVerified)"
    if (-not $captureVerified) { throw [InvalidOperationException]::new() }
    $phase = 4
    $result = Invoke-PrivateNativeWaitWpa -TracePath $etl -PrivateDirectory $private -TargetProcessId $targetProcessId -TargetThreadIds ([uint32[]]@($targetThreadIds))
    $measurementStatus = [int]$result.Status
    if ($measurementStatus -eq 0) { $exitCode = 0 }
}
catch { Write-Host "WPA_EXPORT_UNKNOWN phase=$phase hresult=$($_.Exception.HResult)" }
finally {
    if ($null -ne $threads) { foreach ($thread in $threads) { $thread.Dispose() } }
    if ($null -ne $current) { $current.Dispose() }
    if ($owned -and -not $stopAttempted) {
        $stopAttempted = $true
        $stop = Stop-PrivateNativeWaitCapture
        Write-Host "WPA_EXPORT_CAPTURE_RECOVERY stopExit=$($stop.Code)"
        $exitCode = 1
    }
    $removed = -1
    if ($created) {
        try {
            Remove-Item -LiteralPath $private -Recurse -Force
            $removed = [int](-not [IO.Directory]::Exists($private))
            if ($removed -ne 1) { $exitCode = 1 }
        }
        catch { $removed = 0; $exitCode = 1; Write-Host "WPA_EXPORT_CLEANUP_UNKNOWN hresult=$($_.Exception.HResult)" }
    }
    Write-Host "WPA_EXPORT_COMPLETE recordingStarted=$([int]$owned) captureVerified=$([int]$captureVerified) measurementStatus=$measurementStatus targetWaitReasonVerified=$([int]($measurementStatus -eq 0)) fullRecordingWindowUnknown=1 nativeStateUnknown=1 nativeModeUnknown=1 nativeVersionUnknown=1 stackUnknown=1 ioAttributionUnknown=1 diskCausalityUnknown=1 rootCauseUnknown=1 originalTestRun=0 buildRun=0 privateCreated=$([int]$created) privateRemoved=$removed exit=$exitCode"
}
exit $exitCode
