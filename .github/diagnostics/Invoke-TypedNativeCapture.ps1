param(
    [Parameter(Mandatory = $true)] [string] $CpuReader,
    [Parameter(Mandatory = $true)] [string] $FileReader
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'NativeWaitCapture.ps1')
. (Join-Path $PSScriptRoot 'NativeWaitWpa.ps1')

$durationMs = 4000
$maximumBytes = 268435456L
$readerDeadlineMs = 120000
$childDeadlineMs = 30000
$childOutputLimit = 1048576L
$private = Join-Path (Get-Location).Path ('.artifacts/typed-calibration-' + [guid]::NewGuid().ToString('N'))
$workloadDirectory = Join-Path $private 'workload'
$workloadScript = Join-Path $PSScriptRoot 'Invoke-TypedNativeWorkload.ps1'
$workloadOut = Join-Path $private 'workload-out.txt'
$workloadErr = Join-Path $private 'workload-err.txt'
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'capture.etl'
$wpr = $null
$owned = $false
$stopAttempted = $false
$created = $false
$child = $null
$childStarted = $false
$childTerminated = $true
$childExitKnown = $false
$childExit = -1
$childTimedOut = $false
$childOutputLimited = $false
$phase = 0
$exitCode = 1
try {
    if (-not [OperatingSystem]::IsWindows() -or -not [IO.File]::Exists($CpuReader) -or -not [IO.File]::Exists($FileReader) -or -not [IO.File]::Exists($workloadScript)) { throw [IO.FileNotFoundException]::new() }
    $CpuReader = (Resolve-Path -LiteralPath $CpuReader).Path
    $FileReader = (Resolve-Path -LiteralPath $FileReader).Path
    $current = [Diagnostics.Process]::GetCurrentProcess()
    try { $powerShellExecutable = $current.MainModule.FileName }
    finally { $current.Dispose() }
    if (-not [IO.File]::Exists($powerShellExecutable)) { throw [IO.FileNotFoundException]::new() }
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    $null = New-Item -ItemType Directory -Path $workloadDirectory
    $phase = 1
    $start = Start-PrivateNativeWaitCapture
    if (-not $start.Owned) { throw [InvalidOperationException]::new() }

    # Start-Process owns this fresh PID only after the recording has successfully started.
    $phase = 2
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', ('"' + $workloadScript + '"'), '-PrivateDirectory', ('"' + $workloadDirectory + '"'))
    $child = Start-Process -FilePath $powerShellExecutable -ArgumentList $arguments -WorkingDirectory $private -RedirectStandardOutput $workloadOut -RedirectStandardError $workloadErr -NoNewWindow -PassThru
    $childStarted = $true
    $childTerminated = $false
    $null = $child.Handle
    $startedProcessId = [uint32]$child.Id
    $childClock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $child.HasExited) {
        $outputBytes = 0L
        foreach ($log in @($workloadOut, $workloadErr)) {
            if ([IO.File]::Exists($log)) { $outputBytes += ([IO.FileInfo]::new($log)).Length }
        }
        if ($outputBytes -gt $childOutputLimit) { $childOutputLimited = $true; break }
        if ($childClock.ElapsedMilliseconds -ge $childDeadlineMs) { $childTimedOut = $true; break }
        Start-Sleep -Milliseconds 20
    }
    if ($childTimedOut -or $childOutputLimited) { throw [InvalidOperationException]::new() }
    $childTerminated = $child.WaitForExit(5000)
    if (-not $childTerminated) { throw [InvalidOperationException]::new() }
    $childExit = $child.ExitCode
    $childExitKnown = $true

    $phase = 3
    $stopAttempted = $true
    $stop = Stop-PrivateNativeWaitCapture
    $bytes = if ([IO.File]::Exists($etl)) { ([IO.FileInfo]::new($etl)).Length } else { -1L }
    $accepted = $stop.Code -eq 0 -and $stop.KnownLoss -and $stop.Lost -eq 0 -and $stop.Dropped -eq 0 -and $bytes -gt 0 -and $bytes -le $maximumBytes
    Write-Host "NATIVE_TYPED_CAPTURE stopExit=$($stop.Code) knownLoss=$([int]$stop.KnownLoss) lost=$($stop.Lost) dropped=$($stop.Dropped) bytes=$bytes accepted=$([int]$accepted) freshChild=1"
    if (-not $accepted -or $childExit -ne 0) { throw [InvalidOperationException]::new() }

    # Accept one complete numeric record, not a matching substring of arbitrary child output.
    $stdoutBytes = ([IO.FileInfo]::new($workloadOut)).Length
    $stderrBytes = ([IO.FileInfo]::new($workloadErr)).Length
    if ($stdoutBytes -le 0 -or $stdoutBytes -gt 4096 -or $stderrBytes -ne 0) { throw [InvalidOperationException]::new() }
    $protocol = [regex]::Match([IO.File]::ReadAllText($workloadOut), '\ANATIVE_TYPED_WORKLOAD protocol=1 pid=([1-9][0-9]{0,9}) tid=([1-9][0-9]{0,9}) identityVerified=1 sameNativeThread=1 durationMs=4000 elapsedMs=([0-9]{1,10}) iterations=([1-9][0-9]{0,2}) bytesPerWrite=32768 fileRemoved=1 status=0 hresult=0\r?\n\z')
    if (-not $protocol.Success) { throw [InvalidOperationException]::new() }
    $targetProcessId = [uint32]::Parse($protocol.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    $targetThreadId = [uint32]::Parse($protocol.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
    $elapsedMs = [int64]::Parse($protocol.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
    $iterations = [int]::Parse($protocol.Groups[4].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($targetProcessId -ne $startedProcessId -or $targetThreadId -eq 0 -or $elapsedMs -lt $durationMs -or $elapsedMs -gt $childDeadlineMs -or $iterations -gt 256) { throw [InvalidOperationException]::new() }
    Write-Host "NATIVE_TYPED_CALIBRATION identityVerified=1 pid=$targetProcessId tid=$targetThreadId durationMs=$durationMs controlledPrivateFileLoad=1 originalTestRun=0 freshChild=1"
    Write-Host "NATIVE_TYPED_CALIBRATION controlledFileIterations=$iterations sameNativeThread=1 elapsedMs=$elapsedMs childProtocolVerified=1 childPrivateFileRemoved=1"

    $phase = 4
    $allSucceeded = $true
    foreach ($reader in @(@{ Name = 'cpu'; Executable = $CpuReader; Prefix = 'NATIVE_TYPED_CPU' }, @{ Name = 'file'; Executable = $FileReader; Prefix = 'NATIVE_TYPED_FILE' })) {
        $stage = 'typed-' + $reader.Name
        $result = Invoke-PrivateWpaNative -Executable $reader.Executable -Arguments @($etl, [string]$targetProcessId, [string]$targetThreadId) -Directory $private -Stage $stage -DeadlineMs $readerDeadlineMs -LogPrefix 'NATIVE_TYPED_PROCESS'
        $records = 0
        foreach ($line in [IO.File]::ReadLines((Join-Path $private ($stage + '-out.txt')))) {
            if ($line -match ('^' + $reader.Prefix + ' [A-Za-z][A-Za-z0-9_]*=[A-Za-z0-9_.+-]+(?: [A-Za-z][A-Za-z0-9_]*=[A-Za-z0-9_.+-]+)*$')) {
                Write-Host $line
                $records++
            }
        }
        Write-Host "NATIVE_TYPED_READER kind=$($reader.Name) processSuccess=$([int]$result.Success) records=$records rawOutputPublished=0"
        if (-not $result.Success -or $records -eq 0) { $allSucceeded = $false }
    }
    if ($allSucceeded) { $exitCode = 0 }
}
catch { Write-Host "NATIVE_TYPED_CALIBRATION_UNKNOWN phase=$phase hresult=$($_.Exception.HResult)" }
finally {
    if ($childStarted) {
        try {
            if (-not $child.HasExited) { $child.Kill($true) }
            $childTerminated = $child.WaitForExit(5000)
            if ($childTerminated) { $childExit = $child.ExitCode; $childExitKnown = $true }
        }
        catch { $childTerminated = $false; $exitCode = 1 }
        finally { $child.Dispose() }
        if (-not $childTerminated -or -not $childExitKnown -or $childExit -ne 0 -or $childTimedOut -or $childOutputLimited) { $exitCode = 1 }
    }
    Write-Host "NATIVE_TYPED_CHILD started=$([int]$childStarted) exitKnown=$([int]$childExitKnown) exit=$childExit terminated=$([int]$childTerminated) timedOut=$([int]$childTimedOut) outputLimited=$([int]$childOutputLimited) rawOutputPublished=0"
    if ($owned -and -not $stopAttempted) {
        $stopAttempted = $true
        $stop = Stop-PrivateNativeWaitCapture
        Write-Host "NATIVE_TYPED_RECOVERY stopExit=$($stop.Code)"
        $exitCode = 1
    }
    $removed = -1
    if ($created -and $childTerminated) {
        try { Remove-Item -LiteralPath $private -Recurse -Force; $removed = [int](-not [IO.Directory]::Exists($private)) }
        catch { $removed = 0; $exitCode = 1 }
        if ($removed -ne 1) { $exitCode = 1 }
    }
    elseif ($created) { $removed = 0; $exitCode = 1 }
    Write-Host "NATIVE_TYPED_COMPLETE privateCreated=$([int]$created) privateRemoved=$removed originalTestRun=0 originalConsumerFixProven=0 rootCauseUnknown=1 exit=$exitCode"
}
exit $exitCode