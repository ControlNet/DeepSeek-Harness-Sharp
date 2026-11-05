$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'NativeWaitCapture.ps1')
. (Join-Path $PSScriptRoot 'NativeWaitWpa.ps1')

# Preserve the original consumer arguments and keep every raw log invocation-private.
$root = (Get-Location).Path
$private = Join-Path $root ('.artifacts/native-typed-wait-' + [guid]::NewGuid().ToString('N'))
try { $null = New-Item -ItemType Directory -Path $private }
catch {
    Write-Host 'NATIVE_TYPED_WAIT_RESULT originalTestExit=125 measurementStatus=16 stopExit=125 cleanup=0 consumerWindowCompletenessUnknown=1'
    exit 125
}
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'native-wait.etl'
$owned = $false
$stopAttempted = $false
$measurement = 10
$testExit = 125
$testExited = $false
$stopExit = 125
$knownLoss = $false
$lost = 0L
$droppedCount = 0L
$windowStartTicks = 0L
$windowStopTicks = 0L
$frequency = [Diagnostics.Stopwatch]::Frequency
$test = $null
$wpr = $null
$cleanup = 0

function Write-OriginalResult {
    $logsRead = 0
    $deadlineRecognized = $false
    foreach ($file in @('test-out.txt', 'test-error.txt')) {
        $path = Join-Path $private $file
        if (-not [IO.File]::Exists($path)) { continue }
        $logsRead++
        foreach ($line in [IO.File]::ReadLines($path)) {
            $summary = [regex]::Match($line, '^\s*Dsh\.Tests\s+Total: (\d+), Errors: (\d+), Failed: (\d+), Skipped: (\d+), Not Run: (\d+), Time: (\d+(?:\.\d+)?)s\s*$')
            if ($summary.Success) {
                Write-Host "NATIVE_WAIT_ORIGINAL_SUMMARY total=$($summary.Groups[1].Value) errors=$($summary.Groups[2].Value) failed=$($summary.Groups[3].Value) skipped=$($summary.Groups[4].Value) notRun=$($summary.Groups[5].Value) seconds=$($summary.Groups[6].Value)"
            }
            $method = [regex]::Match($line, '^\s*Dsh\.Tests\.Mcp\.McpSettingsBoundaryTests\.OwnedStdio_PreservesToolOutput_ButNeverMutatesSettings \[FINISHED\] Time: (\d+(?:\.\d+)?)s\s*$')
            if ($method.Success) { Write-Host "NATIVE_WAIT_ORIGINAL_METHOD seconds=$($method.Groups[1].Value)" }
            if ($line.Trim() -ceq 'Test execution timed out after 60000 milliseconds') { $deadlineRecognized = $true }
        }
    }
    Write-Host "NATIVE_WAIT_ORIGINAL_RESULT logFiles=$logsRead recognizedOriginalDeadline=$([int]$deadlineRecognized)"
}

function Stop-ObservedTrace {
    if (-not $owned -or $stopAttempted) { return }
    $script:stopAttempted = $true
    # This boundary precedes the helper's collector query and actual stop request.
    $script:windowStopTicks = [Diagnostics.Stopwatch]::GetTimestamp()
    try {
        $stopped = Stop-PrivateNativeWaitCapture
        $script:stopExit = $stopped.Code
        $script:knownLoss = $stopped.KnownLoss
        $script:lost = $stopped.Lost
        $script:droppedCount = $stopped.Dropped
        if ($stopExit -eq 0) { $script:owned = $false }
        else { $script:measurement = 20 }
    }
    catch { $script:measurement = 23 }
    if ($windowStartTicks -gt 0 -and $windowStopTicks -ge $windowStartTicks) {
        $elapsed = [decimal]($windowStopTicks - $windowStartTicks) / $frequency
        Write-Host "NATIVE_TYPED_WINDOW known=1 requestedObservationMs=20000 startTicks=$windowStartTicks stopTicks=$windowStopTicks frequency=$frequency seconds=$($elapsed.ToString('0.000000000', [Globalization.CultureInfo]::InvariantCulture)) consumerWindowCompletenessUnknown=1"
    }
    else { Write-Host 'NATIVE_TYPED_WINDOW known=0 consumerWindowCompletenessUnknown=1' }
}

function Select-ObservedThreads {
    $targets = [Collections.Generic.HashSet[uint32]]::new()
    $inside = 0
    $outside = 0
    $unknown = 0
    $unknownState = 0
    $frequencyMismatch = 0
    foreach ($file in @('test-out.txt', 'test-error.txt')) {
        foreach ($line in [IO.File]::ReadLines((Join-Path $private $file))) {
            if (-not $line.StartsWith('SANDBOX_NATIVE ', [StringComparison]::Ordinal)) { continue }
            $identity = [regex]::Match($line, '^SANDBOX_NATIVE pid=(\d+) ')
            $sourceProcess = 0U
            if (-not $identity.Success -or -not [uint32]::TryParse($identity.Groups[1].Value, [ref]$sourceProcess)) {
                $unknown++
                continue
            }
            if ($sourceProcess -ne [uint32]$test.Id) { continue }
            $snapshot = [regex]::Match($line, '^SANDBOX_NATIVE pid=\d+ owner=\d+ observation=\d+ snapshot=\d+ operation=\S+ tid=(\d+) started-ticks=\d+ ticks=(\d+) frequency=(\d+)(?: state=\d+)?(?: wait-reason=\d+)?$')
            $threadId = 0U
            $ticks = 0L
            $sourceFrequency = 0L
            if (-not $snapshot.Success -or -not [uint32]::TryParse($snapshot.Groups[1].Value, [ref]$threadId) -or
                $threadId -eq 0 -or -not [long]::TryParse($snapshot.Groups[2].Value, [ref]$ticks) -or
                -not [long]::TryParse($snapshot.Groups[3].Value, [ref]$sourceFrequency)) {
                $unknown++
                continue
            }
            if ($sourceFrequency -ne $frequency) { $frequencyMismatch++; continue }
            if ($ticks -lt $windowStartTicks -or $ticks -gt $windowStopTicks) { $outside++; continue }
            $inside++
            if ($line -notmatch ' state=\d+(?: |$)') { $unknownState++ }
            $null = $targets.Add($threadId)
        }
    }
    Write-Host "NATIVE_TYPED_TARGET pid=$($test.Id) tids=$($targets.Count) inWindowSnapshots=$inside outOfWindowSnapshots=$outside unknownSnapshots=$unknown unknownStateSnapshots=$unknownState frequencyMismatch=$frequencyMismatch"
    if ($unknown -ne 0 -or $frequencyMismatch -ne 0) { return @{ Status = 43; Ids = @() } }
    if ($targets.Count -eq 0 -or $targets.Count -gt 64) { return @{ Status = 41; Ids = @() } }
    return @{ Status = 0; Ids = [uint32[]]@($targets) }
}

function Build-PrivateReaders {
    if (-not $testExited -or $owned -or -not $stopAttempted -or $stopExit -ne 0) { return 45 }
    $config = Join-Path $private 'nuget.config'
    $packages = Join-Path $private 'packages'
    [IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    foreach ($kind in @('Cpu', 'File')) {
        $name = $kind.ToLowerInvariant()
        $intermediate = (Join-Path $private ('obj-' + $name)) + [IO.Path]::DirectorySeparatorChar
        $output = Join-Path $private ('reader-' + $name)
        $project = Join-Path $PSScriptRoot "Trace${kind}Reader/Trace${kind}Reader.csproj"
        $arguments = @('build', $project, '-c', 'Release', '-o', $output, '-m:1', '--nologo',
            '-p:BuildInParallel=false', '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false',
            '-p:DebugType=None', '-p:DebugSymbols=false', '-p:EnableSourceControlManagerQueries=false', '-p:EnableSourceLink=false',
            "-p:RestoreConfigFile=$config", "-p:RestorePackagesPath=$packages", "-p:BaseIntermediateOutputPath=$intermediate",
            "-p:MSBuildProjectExtensionsPath=$intermediate")
        $compiled = Invoke-PrivateWpaNative -Executable $dotnet -Arguments $arguments -Directory $private `
            -Stage "typed-$name-build" -DeadlineMs 300000 -LogPrefix 'NATIVE_TYPED_PROCESS'
        Write-Host "NATIVE_TYPED_BUILD kind=$name success=$([int]$compiled.Success) afterTestExit=$([int]$testExited) afterCaptureStop=$([int]($stopAttempted -and $stopExit -eq 0 -and -not $owned))"
        if (-not $compiled.Success -or -not [IO.File]::Exists((Join-Path $output "Trace${kind}Reader.exe"))) { return 45 }
    }
    return 0
}

function Write-ApprovedTypedOutput([string] $Path, [ValidateSet('CPU', 'FILE')] [string] $Kind) {
    $prefix = "NATIVE_TYPED_$Kind "
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $numeric = if ($Kind -ceq 'CPU') {
        @('hresult', 'status', 'sdkMajor', 'sdkMinor', 'sdkPatch', 'toolkitMajor', 'toolkitMinor', 'toolkitPatch',
          'allowLostEvents', 'allowTimeInversion', 'lostEvents', 'lostBuffers', 'traceHeaderKernelEventVersion',
          'traceHeaderDurationSeconds', 'analyzerDisplayedDurationKnown', 'analyzerDisplayedDurationSeconds',
          'eventSchemaUnknown', 'eventVersionUnknown', 'nativeModeUnknown', 'stackAvailabilityUnknown', 'rootCauseUnknown',
          'consumerWindowCompletenessUnknown', 'intervalEnumerationCompletenessUnknown', 'unobservedLeadingIntervalsUnknown',
          'unobservedTrailingIntervalsUnknown', 'contextSwitchRecords', 'readyThreadRecords', 'cpuTimeSliceRecords',
          'threadActivityRecords', 'pid', 'tid', 'identityInstances', 'identitySourceTypedRow', 'identityBoundaryUnknown',
          'activityCount', 'waitMissing', 'readyMissing', 'waitIntervals', 'waitComplete', 'waitIncomplete', 'waitDuplicateLinks',
          'waitSeconds', 'readyIntervals', 'readyComplete', 'readyIncomplete', 'readyDuplicateLinks', 'readySeconds', 'reasonUnavailable')
    }
    else {
        @('hresult', 'status', 'input_bytes', 'requested_threads', 'allow_lost_events', 'allow_time_inversion',
          'symbols_requested', 'syscall_data_available', 'machine_disk_io_used', 'capture_window_completeness_unknown',
          'raw_unpaired_events_available', 'duration_sum_is_wallclock_partition', 'lost_events', 'lost_buffers',
          'target_process_identity_count', 'tid', 'identity_count', 'create_boundary_known', 'exit_boundary_known',
          'process_create_boundary_known', 'process_exit_boundary_known', 'target_observations', 'identity_conflicts',
          'invalid_durations', 'category_observed_in_trace', 'target_count_unknown', 'trace_observations',
          'missing_issuing_thread', 'unattributed_target_process', 'paired_endpoints', 'missing_start', 'missing_stop',
          'both_endpoints_missing', 'duration_unknown', 'partial_timestamp_pairs', 'sdk_endpoint_duration_sum_seconds',
          'sdk_endpoint_duration_max_seconds', 'duration_sum_unknown', 'duration_max_unknown')
    }
    foreach ($key in $numeric) { $null = $keys.Add($key) }
    $labels = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $categoricalKey = if ($Kind -ceq 'CPU') { 'waitReason' } else { 'category' }
    $publicLabels = if ($Kind -ceq 'CPU') {
        @('Executive', 'FreePage', 'PageIn', 'PoolAllocation', 'DelayExecution', 'Suspended', 'UserRequest',
          'WrExecutive', 'WrFreePage', 'WrPageIn', 'WrPoolAllocation', 'WrDelayExecution', 'WrSuspended', 'WrUserRequest',
          'WrEventPair', 'WrQueue', 'WrLpcReceive', 'WrLpcReply', 'WrVirtualMemory', 'WrPageOut', 'WrRendezvous', 'WrKeyedEvent',
          'WrTerminated', 'WrProcessInSwap', 'WrCpuRateControl', 'WrCalloutStack', 'WrKernel', 'WrResource', 'WrPushLock',
          'WrMutex', 'WrQuantumEnd', 'WrDispatchInt', 'WrPreempted', 'WrYieldExecution', 'WrFastMutex', 'WrGuardedMutex',
          'WrRundown', 'WrAlertByThreadId', 'WrDeferredPreempt', 'WrPhysicalFault', 'WrIoRing')
    }
    else {
        @('create_open', 'directory_enumeration', 'query_information', 'read', 'write', 'directory_change_notification',
          'change_capacity', 'cleanup', 'close', 'delete_on_close', 'flush', 'rename', 'resize', 'set_information',
          'file_system_control', 'set_security', 'query_security', 'set_extended_attributes', 'query_extended_attributes')
    }
    foreach ($label in $publicLabels) { $null = $labels.Add($label) }
    $statusPattern = if ($Kind -ceq 'CPU') { '^NATIVE_TYPED_CPU status=(0|10|20|30|31|32|33)$' } else { '^NATIVE_TYPED_FILE status=([0-9]) hresult=-?\d+$' }
    $records = 0
    $rejected = 0
    $statusCount = 0
    $readerStatus = 125
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if (-not $line.StartsWith($prefix, [StringComparison]::Ordinal)) { continue }
        $records++
        if ($records -gt 4096) { $rejected++; break }
        $valid = $true
        $seenKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($field in $line.Substring($prefix.Length).Split(' ')) {
            $pair = $field.Split('=')
            if ($pair.Count -ne 2 -or -not $seenKeys.Add($pair[0])) { $valid = $false; break }
            if ($pair[0] -ceq $categoricalKey) {
                if (-not $labels.Contains($pair[1])) { $valid = $false; break }
            }
            elseif (-not $keys.Contains($pair[0]) -or $pair[1] -notmatch '^-?\d+(?:\.\d+)?$') { $valid = $false; break }
        }
        if (-not $valid) { $rejected++; continue }
        $status = [regex]::Match($line, $statusPattern)
        if ($seenKeys.Contains('status')) {
            if (-not $status.Success) { $rejected++; continue }
            $statusCount++
            $readerStatus = [int]$status.Groups[1].Value
        }
        Write-Host $line
    }
    Write-Host "NATIVE_TYPED_DECODER kind=$Kind records=$records rejected=$rejected statusRecords=$statusCount status=$readerStatus"
    if ($rejected -ne 0 -or $statusCount -ne 1) { return 47 }
    return $readerStatus
}

function Measure-PrivateTrace {
    if (-not $testExited -or $owned -or $stopExit -ne 0 -or $windowStartTicks -le 0 -or $windowStopTicks -lt $windowStartTicks) { return 20 }
    if (-not [IO.File]::Exists($etl)) { return 20 }
    $traceBytes = (Get-Item -LiteralPath $etl).Length
    Write-Host "NATIVE_TYPED_TRACE bytes=$traceBytes"
    if ($traceBytes -le 0 -or $traceBytes -gt 268435456) { return 40 }
    $selected = Select-ObservedThreads
    if ($selected.Status -ne 0) { return [int]$selected.Status }
    $build = Build-PrivateReaders
    if ($build -ne 0) { return [int]$build }
    $measurementStatus = 0
    foreach ($kind in @('Cpu', 'File')) {
        $name = $kind.ToLowerInvariant()
        $reader = Join-Path $private "reader-$name/Trace${kind}Reader.exe"
        $result = Invoke-PrivateWpaNative -Executable $reader -Arguments @($etl, [string]$test.Id, ($selected.Ids -join ',')) `
            -Directory $private -Stage "typed-$name-window" -DeadlineMs 120000 -LogPrefix 'NATIVE_TYPED_PROCESS'
        $readerStatus = Write-ApprovedTypedOutput -Path (Join-Path $private "typed-$name-window-out.txt") -Kind $kind.ToUpperInvariant()
        if ($readerStatus -ne 0 -and $measurementStatus -eq 0) { $measurementStatus = [int]$readerStatus }
        if (-not $result.Success -and $measurementStatus -eq 0) { $measurementStatus = 46 }
    }
    return $measurementStatus
}

try {
    $arguments = @('-noColor', '-parallelMode', 'none', '-reporter', 'verbose',
        '-method', 'Dsh.Tests.Mcp.McpSettingsBoundaryTests.OwnedStdio_PreservesToolOutput_ButNeverMutatesSettings',
        '-trait-', 'Category=GpuStress', '-trait-', 'Category=OnlyGpu',
        '-trait-', 'Category=RenderBench', '-trait-', 'Category=LlmSmoke', '-failSkips')
    $capture = Start-PrivateNativeWaitCapture
    if ($capture.Owned) { $windowStartTicks = [Diagnostics.Stopwatch]::GetTimestamp() }
    $owned = $capture.Owned
    $measurement = $capture.Status
    Write-Host "NATIVE_WAIT_CAPTURE owned=$([int]$owned) requestedBufferBytes=134217728 status=$measurement beforeTestLaunch=1"
    $test = Start-Process -FilePath (Join-Path $root 'tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests.exe') -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private 'test-out.txt') -RedirectStandardError (Join-Path $private 'test-error.txt')
    if ($owned) {
        $remainingTicks = 20L * $frequency - ([Diagnostics.Stopwatch]::GetTimestamp() - $windowStartTicks)
        $remainingMs = [Math]::Max(0, [int][Math]::Ceiling(1000.0 * $remainingTicks / $frequency))
        if ($remainingMs -gt 0) { $null = $test.WaitForExit($remainingMs) }
        Stop-ObservedTrace
    }
}
catch { Write-Host "NATIVE_TYPED_WAIT_CAPTURE hresult=$($_.Exception.HResult)"; $measurement = 23 }
finally {
    # Stop the owned recording before waiting for the unchanged consumer's natural exit.
    Stop-ObservedTrace
    if ($null -ne $test) {
        try {
            $test.WaitForExit()
            $testExit = $test.ExitCode
            $testExited = $true
        }
        catch { Write-Host "NATIVE_TYPED_WAIT_TEST hresult=$($_.Exception.HResult)" }
    }
    try { Write-OriginalResult }
    catch { Write-Host 'NATIVE_WAIT_ORIGINAL_RESULT logReadAvailable=0' }
    if ($testExited -and $stopAttempted -and $stopExit -eq 0 -and -not $owned) {
        try { $measurement = Measure-PrivateTrace }
        catch { Write-Host "NATIVE_TYPED_WAIT_MEASURE hresult=$($_.Exception.HResult)"; $measurement = 23 }
        Write-Host "NATIVE_TYPED_WAIT_MEASUREMENT status=$measurement"
        if (-not $knownLoss) { $measurement = 21 }
        elseif ($lost -ne 0 -or $droppedCount -ne 0) { $measurement = 22 }
    }
    if ($null -ne $test) {
        try {
            if (-not $test.HasExited) { $test.Kill($true); $null = $test.WaitForExit(5000) }
            Write-Host "NATIVE_TYPED_WAIT_TEST_CLEANUP exited=$([int]$test.HasExited)"
        }
        catch { $measurement = 24 }
        finally { $test.Dispose() }
    }
    try { Remove-Item -LiteralPath $private -Recurse -Force; $cleanup = [int](-not [IO.Directory]::Exists($private)) }
    catch { $measurement = 24 }
    Write-Host "NATIVE_TYPED_WAIT_RESULT originalTestExit=$testExit measurementStatus=$measurement stopExit=$stopExit cleanup=$cleanup consumerWindowCompletenessUnknown=1"
}

# Measurement availability never redeems an original consumer failure.
if ($testExit -ne 0) { exit $testExit }
if ($measurement -ne 0) { exit $measurement }