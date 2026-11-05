param(
    [Parameter(Mandatory = $true)] [string] $CpuReader,
    [Parameter(Mandatory = $true)] [string] $FileReader,
    [ValidateSet('Active', 'PendingRead')] [string] $BoundaryMode = 'Active'
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
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$private = Join-Path $projectDirectory ('.artifacts/typed-calibration-' + [guid]::NewGuid().ToString('N'))
$workloadDirectory = Join-Path $private 'workload'
$workloadScript = Join-Path $PSScriptRoot 'Invoke-TypedNativeWorkload.ps1'
$workloadOut = Join-Path $private 'workload-out.txt'
$workloadErr = Join-Path $private 'workload-err.txt'
$readyPath = Join-Path $private 'capture-ready.txt'
$releasePath = Join-Path $private 'capture-release.txt'
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
$pendingRead = $BoundaryMode -eq 'PendingRead'
$protocolVersion = if ($pendingRead) { 3 } else { 2 }
$pipeName = ''
$pipe = $null
$readReady = $null
$pipeCancellation = $null
$pipeConnection = $null
$threadHandle = [IntPtr]::Zero
$pendingBeforeStop = $false
$pendingAfterStop = $false
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
    if ($pendingRead) {
        Add-Type -Namespace NativeTypedCapture -Name ThreadIo -MemberDefinition @'
[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
private static extern System.IntPtr OpenThread(uint access, bool inherit, uint thread);
[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
private static extern uint GetProcessIdOfThread(System.IntPtr thread);
[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool GetThreadIOPendingFlag(System.IntPtr thread, [MarshalAs(UnmanagedType.Bool)] out bool pending);
[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool CloseHandle(System.IntPtr handle);
public static System.IntPtr OpenOwned(uint thread, uint process)
{
    const uint ThreadQueryInformation = 0x0040;
    System.IntPtr handle = OpenThread(ThreadQueryInformation, false, thread);
    if (handle == System.IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    if (GetProcessIdOfThread(handle) == process) return handle;
    CloseHandle(handle);
    throw new System.InvalidOperationException();
}
public static bool IsPending(System.IntPtr thread)
{
    if (!GetThreadIOPendingFlag(thread, out bool pending)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    return pending;
}
public static void Close(System.IntPtr thread)
{
    if (!CloseHandle(thread)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
}
'@
        $pipeName = 'native-typed-' + [guid]::NewGuid().ToString('N')
        $eventCreated = $false
        $readReady = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, ($pipeName + '-read-ready'), [ref]$eventCreated)
        if (-not $eventCreated) { throw [InvalidOperationException]::new() }
        $pipeOptions = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
        $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::Out, 1, [IO.Pipes.PipeTransmissionMode]::Byte, $pipeOptions)
        $pipeCancellation = [Threading.CancellationTokenSource]::new($childDeadlineMs)
        $pipeConnection = $pipe.WaitForConnectionAsync($pipeCancellation.Token)
    }
    $phase = 1
    $start = Start-PrivateNativeWaitCapture
    if (-not $start.Owned) { throw [InvalidOperationException]::new() }

    # Start-Process owns this fresh PID only after the recording has successfully started.
    $phase = 2
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', ('"' + $workloadScript + '"'), '-PrivateDirectory', ('"' + $workloadDirectory + '"'), '-CaptureBoundaryDirectory', ('"' + $private + '"'))
    $arguments += @('-BoundaryMode', $BoundaryMode)
    if ($pendingRead) { $arguments += @('-PipeName', $pipeName) }
    $child = Start-Process -FilePath $powerShellExecutable -ArgumentList $arguments -WorkingDirectory $private -RedirectStandardOutput $workloadOut -RedirectStandardError $workloadErr -NoNewWindow -PassThru
    $childStarted = $true
    $childTerminated = $false
    $null = $child.Handle
    $startedProcessId = [uint32]$child.Id
    $childClock = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($readyPath)) {
        if ($child.HasExited) { throw [InvalidOperationException]::new() }
        $outputBytes = 0L
        foreach ($log in @($workloadOut, $workloadErr)) {
            if ([IO.File]::Exists($log)) { $outputBytes += ([IO.FileInfo]::new($log)).Length }
        }
        if ($outputBytes -gt $childOutputLimit) { $childOutputLimited = $true; break }
        if ($childClock.ElapsedMilliseconds -ge $childDeadlineMs) { $childTimedOut = $true; break }
        Start-Sleep -Milliseconds 20
    }
    if ($childTimedOut -or $childOutputLimited -or $child.HasExited -or ([IO.FileInfo]::new($readyPath)).Length -gt 4096) { throw [InvalidOperationException]::new() }
    $readySuffix = if ($pendingRead) { ' pipeConnected=1' } else { '' }
    $readyPattern = '\ANATIVE_TYPED_READY protocol=' + $protocolVersion + ' pid=([1-9][0-9]{0,9}) tid=([1-9][0-9]{0,9}) durationMs=4000 elapsedMs=([0-9]{1,10}) iterations=([1-9][0-9]{0,2})' + $readySuffix + '\z'
    $ready = [regex]::Match([IO.File]::ReadAllText($readyPath), $readyPattern)
    if (-not $ready.Success) { throw [InvalidOperationException]::new() }
    $readyProcessId = [uint32]::Parse($ready.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    $readyThreadId = [uint32]::Parse($ready.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
    $readyElapsedMs = [int64]::Parse($ready.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
    $readyIterations = [int]::Parse($ready.Groups[4].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($readyProcessId -ne $startedProcessId -or $readyElapsedMs -lt $durationMs -or $readyElapsedMs -gt $childDeadlineMs -or $readyIterations -gt 256) { throw [InvalidOperationException]::new() }
    if ($pendingRead) {
        $pipeConnection.GetAwaiter().GetResult()
        if (-not $pipe.IsConnected -or -not $readReady.WaitOne($childDeadlineMs)) { throw [InvalidOperationException]::new() }
        $threadHandle = [NativeTypedCapture.ThreadIo]::OpenOwned($readyThreadId, $startedProcessId)
        $pendingClock = [Diagnostics.Stopwatch]::StartNew()
        while (-not [NativeTypedCapture.ThreadIo]::IsPending($threadHandle)) {
            if ($child.HasExited -or $pendingClock.ElapsedMilliseconds -ge $childDeadlineMs) { throw [TimeoutException]::new() }
            Start-Sleep -Milliseconds 20
        }
        $pendingBeforeStop = $true
    }

    $phase = 3
    if ($child.HasExited) { throw [InvalidOperationException]::new() }
    $stopAttempted = $true
    $stop = Stop-PrivateNativeWaitCapture
    $activeAfterStop = -not $child.HasExited
    if ($pendingRead) { $pendingAfterStop = [NativeTypedCapture.ThreadIo]::IsPending($threadHandle) }
    $bytes = if ([IO.File]::Exists($etl)) { ([IO.FileInfo]::new($etl)).Length } else { -1L }
    $accepted = $stop.Code -eq 0 -and $stop.KnownLoss -and $stop.Lost -eq 0 -and $stop.Dropped -eq 0 -and $bytes -gt 0 -and $bytes -le $maximumBytes -and $activeAfterStop -and (-not $pendingRead -or ($pendingBeforeStop -and $pendingAfterStop))
    Write-Host "NATIVE_TYPED_CAPTURE stopExit=$($stop.Code) knownLoss=$([int]$stop.KnownLoss) lost=$($stop.Lost) dropped=$($stop.Dropped) bytes=$bytes accepted=$([int]$accepted) freshChild=1 childActiveBeforeStop=1 childActiveAfterStop=$([int]$activeAfterStop)"
    if (-not $accepted) { throw [InvalidOperationException]::new() }

    [IO.File]::WriteAllText($releasePath, "NATIVE_TYPED_RELEASE protocol=$protocolVersion captureStopped=1")
    if ($pendingRead) {
        $pipeCancellation.CancelAfter($childDeadlineMs)
        $releaseByte = [byte[]]@(90)
        $pipe.WriteAsync($releaseByte, 0, 1, $pipeCancellation.Token).GetAwaiter().GetResult()
        $pipe.Flush()
        Write-Host "NATIVE_TYPED_PENDING_READ protocol=3 pipeConnected=1 readArmed=1 threadOwnerVerified=1 threadIoPendingBeforeStop=$([int]$pendingBeforeStop) threadIoPendingAfterStop=$([int]$pendingAfterStop) releaseByteWrittenAfterStop=1 sdkCrossStopActivityUnknown=1"
    }
    $childTerminated = $child.WaitForExit($childDeadlineMs)
    if (-not $childTerminated) { $childTimedOut = $true; throw [TimeoutException]::new() }
    $childExit = $child.ExitCode
    $childExitKnown = $true
    if ($childExit -ne 0) { throw [InvalidOperationException]::new() }

    # Accept one complete numeric record, not a matching substring of arbitrary child output.
    $stdoutBytes = ([IO.FileInfo]::new($workloadOut)).Length
    $stderrBytes = ([IO.FileInfo]::new($workloadErr)).Length
    if ($stdoutBytes -le 0 -or $stdoutBytes -gt 4096 -or $stderrBytes -ne 0) { throw [InvalidOperationException]::new() }
    $resultSuffix = if ($pendingRead) { ' pipeConnected=1 readCompleted=1 readByte=90' } else { '' }
    $resultPattern = '\ANATIVE_TYPED_WORKLOAD protocol=' + $protocolVersion + ' pid=([1-9][0-9]{0,9}) tid=([1-9][0-9]{0,9}) identityVerified=1 sameNativeThread=1 durationMs=4000 elapsedMs=([0-9]{1,10}) iterations=([1-9][0-9]{0,2}) bytesPerWrite=32768' + $resultSuffix + ' captureReleaseObserved=1 nativeThreadAliveAfterRelease=1 fileRemoved=1 status=0 hresult=0\r?\n\z'
    $protocol = [regex]::Match([IO.File]::ReadAllText($workloadOut), $resultPattern)
    if (-not $protocol.Success) { throw [InvalidOperationException]::new() }
    $targetProcessId = [uint32]::Parse($protocol.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    $targetThreadId = [uint32]::Parse($protocol.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
    $elapsedMs = [int64]::Parse($protocol.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
    $iterations = [int]::Parse($protocol.Groups[4].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($targetProcessId -ne $readyProcessId -or $targetThreadId -ne $readyThreadId -or $elapsedMs -ne $readyElapsedMs -or $iterations -ne $readyIterations) { throw [InvalidOperationException]::new() }
    if ($pendingRead) { Write-Host 'NATIVE_TYPED_PENDING_READ protocol=3 readCompleted=1 readByte=90 sameNativeThread=1 normalChildExit=1 sdkCrossStopActivityUnknown=1' }
    Write-Host "NATIVE_TYPED_CALIBRATION identityVerified=1 pid=$targetProcessId tid=$targetThreadId durationMs=$durationMs controlledPrivateFileLoad=1 originalTestRun=0 freshChild=1 activeAtCaptureStop=1"
    Write-Host "NATIVE_TYPED_CALIBRATION controlledFileIterations=$iterations sameNativeThread=1 elapsedMs=$elapsedMs childProtocolVerified=1 childPrivateFileRemoved=1 captureStoppedBeforeRelease=1 nativeThreadAliveAfterRelease=1 readersAfterCaptureStop=1"

    $phase = 4
    $allSucceeded = $true
    foreach ($reader in @(@{ Name = 'cpu'; Executable = $CpuReader; Prefix = 'NATIVE_TYPED_CPU' }, @{ Name = 'file'; Executable = $FileReader; Prefix = 'NATIVE_TYPED_FILE' })) {
        $stage = 'typed-' + $reader.Name
        $result = Invoke-PrivateWpaNative -Executable $reader.Executable -Arguments @($etl, [string]$targetProcessId, [string]$targetThreadId) -Directory $private -Stage $stage -DeadlineMs $readerDeadlineMs -LogPrefix 'NATIVE_TYPED_PROCESS'
        $records = 0
        $processOpenBoundaryRecords = 0
        $threadOpenBoundaryRecords = 0
        foreach ($line in [IO.File]::ReadLines((Join-Path $private ($stage + '-out.txt')))) {
            if ($line -match ('^' + $reader.Prefix + ' [A-Za-z][A-Za-z0-9_]*=[A-Za-z0-9_.+-]+(?: [A-Za-z][A-Za-z0-9_]*=[A-Za-z0-9_.+-]+)*$')) {
                Write-Host $line
                $records++
                if ($reader.Name -eq 'file' -and $line -ceq 'NATIVE_TYPED_FILE process_create_boundary_known=1 process_exit_boundary_known=0') { $processOpenBoundaryRecords++ }
                if ($reader.Name -eq 'file' -and $line -ceq "NATIVE_TYPED_FILE tid=$targetThreadId create_boundary_known=1 exit_boundary_known=0") { $threadOpenBoundaryRecords++ }
            }
        }
        Write-Host "NATIVE_TYPED_READER kind=$($reader.Name) processSuccess=$([int]$result.Success) records=$records rawOutputPublished=0"
        if (-not $result.Success -or $records -eq 0) { $allSucceeded = $false }
        if ($reader.Name -eq 'file') {
            $openBoundariesVerified = $processOpenBoundaryRecords -eq 1 -and $threadOpenBoundaryRecords -eq 1
            Write-Host "NATIVE_TYPED_CALIBRATION processOpenBoundaryRecords=$processOpenBoundaryRecords threadOpenBoundaryRecords=$threadOpenBoundaryRecords activeCaptureBoundariesVerified=$([int]$openBoundariesVerified)"
            if (-not $openBoundariesVerified) { $allSucceeded = $false }
        }
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
    if ($threadHandle -ne [IntPtr]::Zero) {
        try { [NativeTypedCapture.ThreadIo]::Close($threadHandle) }
        catch { $exitCode = 1 }
    }
    if ($null -ne $pipeCancellation) { $pipeCancellation.Cancel() }
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $pipeConnection) {
        try { $pipeConnection.GetAwaiter().GetResult() }
        catch { $exitCode = 1 }
    }
    if ($null -ne $pipeCancellation) { $pipeCancellation.Dispose() }
    if ($null -ne $readReady) { $readReady.Dispose() }
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