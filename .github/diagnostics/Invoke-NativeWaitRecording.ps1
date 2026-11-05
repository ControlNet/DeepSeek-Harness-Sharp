$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'NativeWaitCapture.ps1')
. (Join-Path $PSScriptRoot 'NativeWaitWpa.ps1')

# All external output remains inside this invocation's private checkout directory.
$root = (Get-Location).Path
$private = Join-Path $root ('.artifacts/native-wait-' + [guid]::NewGuid().ToString('N'))
try { $null = New-Item -ItemType Directory -Path $private }
catch {
    Write-Host 'NATIVE_WAIT_RESULT originalTestExit=125 measurementStatus=16 stopExit=125'
    exit 125
}
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'native-wait.etl'
$owned = $false
$measurement = 10
$testExit = 125
$stopExit = 125
$test = $null
$wpr = $null


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


function Measure-PrivateTrace {
    $targets = [Collections.Generic.HashSet[uint32]]::new()
    foreach ($file in @('test-out.txt', 'test-error.txt')) {
        foreach ($line in [IO.File]::ReadLines((Join-Path $private $file))) {
            $match = [regex]::Match($line, '^SANDBOX_NATIVE pid=(\d+) owner=\d+ observation=\d+ snapshot=\d+ operation=\S+ tid=(\d+) ')
            if ($match.Success -and [int]$match.Groups[1].Value -eq $test.Id) {
                $null = $targets.Add([uint32]$match.Groups[2].Value)
                if ($targets.Count -gt 64) { return 31 }
            }
        }
    }
    Write-Host "NATIVE_WAIT_TARGET pid=$($test.Id) tids=$($targets.Count)"
    foreach ($tid in $targets) { Write-Host "NATIVE_WAIT_THREAD pid=$($test.Id) tid=$tid" }
    if ($targets.Count -eq 0) { return 31 }
    $result = Invoke-PrivateNativeWaitWpa -TracePath $etl -PrivateDirectory $private `
        -TargetProcessId ([uint32]$test.Id) -TargetThreadIds ([uint32[]]@($targets))
    return [int]$result.Status
}

try {
    $capture = Start-PrivateNativeWaitCapture
    $owned = $capture.Owned
    $measurement = $capture.Status
    Write-Host "NATIVE_WAIT_CAPTURE owned=$([int]$owned) requestedBufferBytes=134217728 status=$measurement"
    $arguments = @('-noColor', '-parallelMode', 'none', '-reporter', 'verbose',
        '-method', 'Dsh.Tests.Mcp.McpSettingsBoundaryTests.OwnedStdio_PreservesToolOutput_ButNeverMutatesSettings',
        '-trait-', 'Category=GpuStress', '-trait-', 'Category=OnlyGpu',
        '-trait-', 'Category=RenderBench', '-trait-', 'Category=LlmSmoke', '-failSkips')
    $test = Start-Process -FilePath (Join-Path $root 'tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests.exe') -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private 'test-out.txt') -RedirectStandardError (Join-Path $private 'test-error.txt')
    $test.WaitForExit()
    $testExit = $test.ExitCode
}
catch { Write-Host 'NATIVE_WAIT_TEST_LAUNCH status=1' }
finally {
    if ($owned) {
        try {
            $stopped = Stop-PrivateNativeWaitCapture
            $stopExit = $stopped.Code
            $knownLoss = $stopped.KnownLoss
            $lost = $stopped.Lost
            $droppedCount = $stopped.Dropped
            if ($stopExit -ne 0 -or -not [IO.File]::Exists($etl)) { $measurement = 20 }
            else {
                Write-Host "NATIVE_WAIT_TRACE bytes=$((Get-Item -LiteralPath $etl).Length)"
                $measurement = Measure-PrivateTrace
                Write-Host "NATIVE_WAIT_DECODER status=$measurement"
                if (-not $knownLoss) { $measurement = 21 }
                elseif ($lost -ne 0 -or $droppedCount -ne 0) { $measurement = 22 }
            }
        }
        catch { $measurement = 23 }
    }
    try { Write-OriginalResult }
    catch { Write-Host 'NATIVE_WAIT_ORIGINAL_RESULT logReadAvailable=0' }
    if ($null -ne $test) { $test.Dispose() }
    try { Remove-Item -LiteralPath $private -Recurse -Force }
    catch { $measurement = 24 }
    Write-Host "NATIVE_WAIT_RESULT originalTestExit=$testExit measurementStatus=$measurement stopExit=$stopExit"
}

# Original consumer failures take precedence over unavailable or unknown measurements.
if ($testExit -ne 0) { exit $testExit }
if ($measurement -ne 0) { exit $measurement }
