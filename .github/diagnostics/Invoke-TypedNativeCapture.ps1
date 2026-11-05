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
$private = Join-Path (Get-Location).Path ('.artifacts/typed-calibration-' + [guid]::NewGuid().ToString('N'))
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'capture.etl'
$wpr = $null
$owned = $false
$stopAttempted = $false
$created = $false
$phase = 0
$exitCode = 1
try {
    if (-not [IO.File]::Exists($CpuReader) -or -not [IO.File]::Exists($FileReader)) { throw [IO.FileNotFoundException]::new() }
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    Add-Type -Namespace NativeTypedCapture -Name NativeThread -MemberDefinition '[DllImport("kernel32.dll", ExactSpelling = true)] public static extern uint GetCurrentThreadId();'
    $current = [Diagnostics.Process]::GetCurrentProcess()
    $targetProcessId = [uint32]$current.Id
    $targetThreadId = [NativeTypedCapture.NativeThread]::GetCurrentThreadId()
    $threadVerified = $false
    try {
        foreach ($thread in $current.Threads) {
            try { if ($thread.Id -eq $targetThreadId) { $threadVerified = $true } }
            finally { $thread.Dispose() }
        }
    }
    finally { $current.Dispose() }
    if (-not $threadVerified -or $targetProcessId -eq 0 -or $targetThreadId -eq 0) { throw [InvalidOperationException]::new() }
    Write-Host "NATIVE_TYPED_CALIBRATION identityVerified=1 pid=$targetProcessId tid=$targetThreadId durationMs=$durationMs controlledPrivateFileLoad=1 originalTestRun=0"
    $phase = 1
    $start = Start-PrivateNativeWaitCapture
    if (-not $start.Owned) { throw [InvalidOperationException]::new() }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $data = [byte[]]::new(32768)
    $loadPath = Join-Path $private 'controlled-file.bin'
    for ($iteration = 0; $iteration -lt 16; $iteration++) {
        if ([NativeTypedCapture.NativeThread]::GetCurrentThreadId() -ne $targetThreadId) { throw [InvalidOperationException]::new() }
        $stream = [IO.File]::Open($loadPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.Write($data, 0, $data.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        $null = [IO.File]::ReadAllBytes($loadPath)
        $null = [IO.File]::GetAttributes($loadPath)
        $null = [IO.Directory]::GetFiles($private)
        [Threading.Thread]::Sleep(15)
    }
    if ($clock.ElapsedMilliseconds -lt $durationMs) { [Threading.Thread]::Sleep([int]($durationMs - $clock.ElapsedMilliseconds)) }
    if ([NativeTypedCapture.NativeThread]::GetCurrentThreadId() -ne $targetThreadId) { throw [InvalidOperationException]::new() }
    Write-Host "NATIVE_TYPED_CALIBRATION controlledFileIterations=16 sameNativeThread=1 elapsedMs=$($clock.ElapsedMilliseconds)"
    $phase = 2
    $stopAttempted = $true
    $stop = Stop-PrivateNativeWaitCapture
    $bytes = if ([IO.File]::Exists($etl)) { ([IO.FileInfo]::new($etl)).Length } else { -1L }
    $accepted = $stop.Code -eq 0 -and $stop.KnownLoss -and $stop.Lost -eq 0 -and $stop.Dropped -eq 0 -and $bytes -gt 0 -and $bytes -le $maximumBytes
    Write-Host "NATIVE_TYPED_CAPTURE stopExit=$($stop.Code) knownLoss=$([int]$stop.KnownLoss) lost=$($stop.Lost) dropped=$($stop.Dropped) bytes=$bytes accepted=$([int]$accepted)"
    if (-not $accepted) { throw [InvalidOperationException]::new() }
    $phase = 3
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
    if ($owned -and -not $stopAttempted) {
        $stopAttempted = $true
        $stop = Stop-PrivateNativeWaitCapture
        Write-Host "NATIVE_TYPED_RECOVERY stopExit=$($stop.Code)"
        $exitCode = 1
    }
    $removed = -1
    if ($created) {
        try { Remove-Item -LiteralPath $private -Recurse -Force; $removed = [int](-not [IO.Directory]::Exists($private)) }
        catch { $removed = 0; $exitCode = 1 }
        if ($removed -ne 1) { $exitCode = 1 }
    }
    Write-Host "NATIVE_TYPED_COMPLETE privateCreated=$([int]$created) privateRemoved=$removed originalTestRun=0 originalConsumerFixProven=0 rootCauseUnknown=1 exit=$exitCode"
}
exit $exitCode
