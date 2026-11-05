param(
    [Parameter(Mandatory = $true)] [string] $PrivateDirectory,
    [Parameter(Mandatory = $true)] [string] $CaptureBoundaryDirectory,
    [ValidateSet('Active', 'PendingRead')] [string] $BoundaryMode = 'Active',
    [string] $PipeName = ''
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$durationMs = 4000
$targetProcessId = 0U
$targetThreadId = 0U
$identityVerified = $false
$sameNativeThread = $false
$iterations = 0
$elapsedMs = 0L
$created = $false
$fileRemoved = $false
$loadPath = $null
$exitCode = 1
$hresult = 0
$captureReleaseObserved = $false
$nativeThreadAliveAfterRelease = $false
$releaseDeadlineMs = 30000
$pendingRead = $BoundaryMode -eq 'PendingRead'
$protocolVersion = if ($pendingRead) { 3 } else { 2 }
$pipe = $null
$readReady = $null
$pipeConnected = $false
$readCompleted = $false
$readByte = -1
try {
    if (-not [OperatingSystem]::IsWindows() -or -not [IO.Directory]::Exists($PrivateDirectory)) { throw [InvalidOperationException]::new() }
    $directory = [IO.DirectoryInfo]::new($PrivateDirectory)
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or @($directory.EnumerateFileSystemInfos()).Count -ne 0) { throw [InvalidOperationException]::new() }
    $boundaryDirectory = [IO.DirectoryInfo]::new($CaptureBoundaryDirectory)
    if (-not $boundaryDirectory.Exists -or ($boundaryDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals($directory.Parent.FullName, $boundaryDirectory.FullName, [StringComparison]::OrdinalIgnoreCase)) { throw [InvalidOperationException]::new() }
    $readyPath = Join-Path $boundaryDirectory.FullName 'capture-ready.txt'
    $readyTemporary = Join-Path $boundaryDirectory.FullName 'capture-ready-pending.txt'
    $releasePath = Join-Path $boundaryDirectory.FullName 'capture-release.txt'
    if ([IO.File]::Exists($readyPath) -or [IO.File]::Exists($readyTemporary) -or [IO.File]::Exists($releasePath)) { throw [InvalidOperationException]::new() }
    $loadPath = Join-Path $directory.FullName 'controlled-file.bin'
    Add-Type -Namespace NativeTypedWorkload -Name NativeThread -MemberDefinition @'
[DllImport("kernel32.dll", ExactSpelling = true)] public static extern uint GetCurrentThreadId();
[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool ReadFile(Microsoft.Win32.SafeHandles.SafePipeHandle file, [Out] byte[] data, uint length, out uint read, System.IntPtr overlapped);
public static int ReadAcrossStop(System.IO.Pipes.NamedPipeClientStream pipe, System.Threading.EventWaitHandle ready, uint expectedThread)
{
    if (pipe.IsAsync || GetCurrentThreadId() != expectedThread) throw new System.InvalidOperationException();
    byte[] data = new byte[1];
    ready.Set();
    if (!ReadFile(pipe.SafePipeHandle, data, 1, out uint read, System.IntPtr.Zero))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    if (read != 1 || GetCurrentThreadId() != expectedThread) throw new System.InvalidOperationException();
    return data[0];
}
'@
    $current = [Diagnostics.Process]::GetCurrentProcess()
    try {
        $targetProcessId = [uint32]$current.Id
        $targetThreadId = [NativeTypedWorkload.NativeThread]::GetCurrentThreadId()
        foreach ($thread in $current.Threads) {
            try { if ($thread.Id -eq $targetThreadId) { $identityVerified = $true } }
            finally { $thread.Dispose() }
        }
    }
    finally { $current.Dispose() }
    if (-not $identityVerified -or $targetProcessId -eq 0 -or $targetThreadId -eq 0) { throw [InvalidOperationException]::new() }

    $data = [byte[]]::new(32768)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $durationMs -and $iterations -lt 256) {
        if ([NativeTypedWorkload.NativeThread]::GetCurrentThreadId() -ne $targetThreadId) { throw [InvalidOperationException]::new() }
        $mode = if ($created) { [IO.FileMode]::Create } else { [IO.FileMode]::CreateNew }
        $stream = [IO.File]::Open($loadPath, $mode, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $created = $true
        try { $stream.Write($data, 0, $data.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        $read = [IO.File]::ReadAllBytes($loadPath)
        if ($read.Length -ne $data.Length) { throw [InvalidOperationException]::new() }
        $null = [IO.File]::GetAttributes($loadPath)
        $files = @([IO.Directory]::GetFiles($directory.FullName))
        if ($files.Length -ne 1 -or -not [string]::Equals($files[0], $loadPath, [StringComparison]::OrdinalIgnoreCase)) { throw [InvalidOperationException]::new() }
        $iterations++
        [Threading.Thread]::Sleep(15)
    }
    if ($clock.ElapsedMilliseconds -lt $durationMs) { [Threading.Thread]::Sleep([int]($durationMs - $clock.ElapsedMilliseconds)) }
    $elapsedMs = $clock.ElapsedMilliseconds
    $sameNativeThread = [NativeTypedWorkload.NativeThread]::GetCurrentThreadId() -eq $targetThreadId
    if (-not $sameNativeThread -or $iterations -eq 0 -or $elapsedMs -lt $durationMs) { throw [InvalidOperationException]::new() }
    # The pending-read mode has a synchronous pipe handle; its event precedes ReadFile without another file operation.
    if ($pendingRead) {
        if ($PipeName -cnotmatch '\Anative-typed-[0-9a-f]{32}\z') { throw [InvalidOperationException]::new() }
        $readReady = [Threading.EventWaitHandle]::OpenExisting($PipeName + '-read-ready')
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::In, [IO.Pipes.PipeOptions]::CurrentUserOnly)
        $pipe.Connect($releaseDeadlineMs)
        $pipeConnected = $pipe.IsConnected -and -not $pipe.IsAsync
        if (-not $pipeConnected) { throw [InvalidOperationException]::new() }
    }
    $readySuffix = if ($pendingRead) { ' pipeConnected=1' } else { '' }
    [IO.File]::WriteAllText($readyTemporary, "NATIVE_TYPED_READY protocol=$protocolVersion pid=$targetProcessId tid=$targetThreadId durationMs=$durationMs elapsedMs=$elapsedMs iterations=$iterations$readySuffix")
    [IO.File]::Move($readyTemporary, $readyPath)
    if ($pendingRead) {
        $readByte = [NativeTypedWorkload.NativeThread]::ReadAcrossStop($pipe, $readReady, $targetThreadId)
        $readCompleted = $readByte -eq 90
        if (-not $readCompleted) { throw [InvalidOperationException]::new() }
    }
    else {
        $releaseClock = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists($releasePath)) {
            if ($releaseClock.ElapsedMilliseconds -ge $releaseDeadlineMs) { throw [TimeoutException]::new() }
            [Threading.Thread]::Sleep(20)
        }
    }
    $captureReleaseObserved = [string]::Equals([IO.File]::ReadAllText($releasePath), "NATIVE_TYPED_RELEASE protocol=$protocolVersion captureStopped=1", [StringComparison]::Ordinal)
    $nativeThreadAliveAfterRelease = [NativeTypedWorkload.NativeThread]::GetCurrentThreadId() -eq $targetThreadId
    if (-not $captureReleaseObserved -or -not $nativeThreadAliveAfterRelease) { throw [InvalidOperationException]::new() }
    $exitCode = 0
}
catch { $hresult = $_.Exception.HResult }
finally {
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $readReady) { $readReady.Dispose() }
    if ($created) {
        try { [IO.File]::Delete($loadPath); $fileRemoved = -not [IO.File]::Exists($loadPath) }
        catch { $hresult = $_.Exception.HResult; $exitCode = 1 }
        if (-not $fileRemoved) { $exitCode = 1 }
    }
    $resultSuffix = if ($pendingRead) { " pipeConnected=$([int]$pipeConnected) readCompleted=$([int]$readCompleted) readByte=$readByte" } else { '' }
    [Console]::Out.WriteLine("NATIVE_TYPED_WORKLOAD protocol=$protocolVersion pid=$targetProcessId tid=$targetThreadId identityVerified=$([int]$identityVerified) sameNativeThread=$([int]$sameNativeThread) durationMs=$durationMs elapsedMs=$elapsedMs iterations=$iterations bytesPerWrite=32768$resultSuffix captureReleaseObserved=$([int]$captureReleaseObserved) nativeThreadAliveAfterRelease=$([int]$nativeThreadAliveAfterRelease) fileRemoved=$([int]$fileRemoved) status=$exitCode hresult=$hresult")
}
exit $exitCode