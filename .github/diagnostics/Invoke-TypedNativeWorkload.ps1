param(
    [Parameter(Mandatory = $true)] [string] $PrivateDirectory
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
try {
    if (-not [OperatingSystem]::IsWindows() -or -not [IO.Directory]::Exists($PrivateDirectory)) { throw [InvalidOperationException]::new() }
    $directory = [IO.DirectoryInfo]::new($PrivateDirectory)
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or @($directory.EnumerateFileSystemInfos()).Count -ne 0) { throw [InvalidOperationException]::new() }
    $loadPath = Join-Path $directory.FullName 'controlled-file.bin'
    Add-Type -Namespace NativeTypedWorkload -Name NativeThread -MemberDefinition '[DllImport("kernel32.dll", ExactSpelling = true)] public static extern uint GetCurrentThreadId();'
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
    $exitCode = 0
}
catch { $hresult = $_.Exception.HResult }
finally {
    if ($created) {
        try { [IO.File]::Delete($loadPath); $fileRemoved = -not [IO.File]::Exists($loadPath) }
        catch { $hresult = $_.Exception.HResult; $exitCode = 1 }
        if (-not $fileRemoved) { $exitCode = 1 }
    }
    [Console]::Out.WriteLine("NATIVE_TYPED_WORKLOAD protocol=1 pid=$targetProcessId tid=$targetThreadId identityVerified=$([int]$identityVerified) sameNativeThread=$([int]$sameNativeThread) durationMs=$durationMs elapsedMs=$elapsedMs iterations=$iterations bytesPerWrite=32768 fileRemoved=$([int]$fileRemoved) status=$exitCode hresult=$hresult")
}
exit $exitCode