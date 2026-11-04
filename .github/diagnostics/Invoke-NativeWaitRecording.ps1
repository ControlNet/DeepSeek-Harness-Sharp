$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

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

function Invoke-PrivateWpr([string[]] $Arguments, [string] $Label) {
    $output = Join-Path $private ($Label + '.txt')
    & $wpr @Arguments *> $output
    $code = $LASTEXITCODE
    Write-Host "NATIVE_WAIT_TOOL stage=$Label exit=$code"
    return @{ Code = $code; Text = [IO.File]::ReadAllText($output) }
}

function Read-Schema($Class, [string] $Guid, [int[]] $Opcodes, [hashtable] $Required, [hashtable] $Optional = @{}) {
    $properties = @($Class.CimClassProperties | Where-Object { $null -ne $_.Qualifiers['WmiDataId'] } |
        Sort-Object { [int]$_.Qualifiers['WmiDataId'].Value })
    if ($properties.Count -eq 0 -or $properties.Count -gt 64) { throw 'Schema size unsupported.' }
    for ($index = 0; $index -lt $properties.Count; $index++) {
        if ([int]$properties[$index].Qualifiers['WmiDataId'].Value -ne $index + 1) { throw 'Schema order unavailable.' }
    }
    $map = @{}
    foreach ($fields in @($Required, $Optional)) {
        foreach ($name in $fields.Keys) {
            $matches = @($properties | Where-Object { $_.Name -ceq $name })
            if ($matches.Count -eq 0 -and -not $Required.ContainsKey($name)) { continue }
            if ($matches.Count -ne 1 -or $matches[0].CimType -ne [Microsoft.Management.Infrastructure.CimType]($fields[$name])) {
                throw 'Required numeric schema unavailable.'
            }
            $map[$name] = [int]$matches[0].Qualifiers['WmiDataId'].Value - 1
        }
    }
    $version = $null
    $provider = $null
    $types = $Class.CimClassQualifiers['EventType']
    if ($null -eq $types) { throw 'Schema opcode unavailable.' }
    foreach ($opcode in $Opcodes) {
        if ($opcode -notin @($types.Value)) { throw 'Schema opcode mismatch.' }
    }
    $complete = $false
    for ($depth = 0; $depth -lt 8; $depth++) {
        if ($null -eq $version -and $null -ne $Class.CimClassQualifiers['EventVersion']) {
            $version = [int]$Class.CimClassQualifiers['EventVersion'].Value
        }
        if ($null -eq $provider -and $null -ne $Class.CimClassQualifiers['Guid']) {
            $provider = [guid]$Class.CimClassQualifiers['Guid'].Value
        }
        if ([string]::IsNullOrEmpty($Class.CimSuperClassName)) { $complete = $true; break }
        $Class = Get-CimClass -Namespace root/wmi -ClassName $Class.CimSuperClassName
    }
    if (-not $complete -or $null -eq $version -or $version -lt 0 -or $version -gt 255 -or
        $null -eq $provider -or $provider -ne [guid]$Guid) { throw 'Schema identity unavailable.' }
    return @{ Provider = $provider; Version = $version; PropertyCount = $properties.Count; Map = $map }
}

function Read-Number($Event, $Schema, [string] $Name) {
    $value = $Event.Properties[$Schema.Map[$Name]].Value
    if ($value -isnot [byte] -and $value -isnot [sbyte] -and $value -isnot [uint16] -and
        $value -isnot [int16] -and $value -isnot [uint32] -and $value -isnot [int32] -and
        $value -isnot [uint64] -and $value -isnot [int64]) { throw 'Numeric decoding unavailable.' }
    return $value
}

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

function Read-KernelSchemas {
    $threadGuid = '3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c'
    $fileGuid = '90cbdc39-4a3e-11d1-84f4-0000f80464e3'
    $diskGuid = '3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c'
    $switches = @{}
    $candidates = @(Get-CimClass -Namespace root/wmi -QualifierName EventType -PropertyName OldThreadId |
        Where-Object { $null -ne $_.CimClassQualifiers['EventType'] -and 36 -in @($_.CimClassQualifiers['EventType'].Value) } |
        Select-Object -First 9)
    if ($candidates.Count -gt 8) { throw 'Switch discovery limit exceeded.' }
    for ($candidate = 0; $candidate -lt $candidates.Count; $candidate++) {
        try {
            $schema = Read-Schema $candidates[$candidate] $threadGuid @(36) @{
                OldThreadId = 'UInt32'; OldThreadState = 'SInt8'; OldThreadWaitReason = 'SInt8'
            } @{ OldThreadWaitMode = 'SInt8' }
        }
        catch {
            Write-Host "NATIVE_WAIT_SWITCH_CANDIDATE index=$candidate available=0"
            continue
        }
        $key = '{0}:{1}' -f $schema.Version, $schema.PropertyCount
        if ($switches.ContainsKey($key)) { throw 'Ambiguous switch schema.' }
        $switches[$key] = $schema
        Write-Host "NATIVE_WAIT_SWITCH_SCHEMA version=$($schema.Version) properties=$($schema.PropertyCount) modeAvailable=$([int]$schema.Map.ContainsKey('OldThreadWaitMode'))"
    }
    if ($switches.Count -eq 0) { throw 'Switch schemas unavailable.' }
    $directory = Read-Schema (Get-CimClass -Namespace root/wmi -ClassName FileIo_DirEnum) $fileGuid @(72) @{ IrpPtr = 'UInt32'; TTID = 'UInt32' }
    $end = Read-Schema (Get-CimClass -Namespace root/wmi -ClassName FileIo_OpEnd) $fileGuid @(76) @{ IrpPtr = 'UInt32'; NtStatus = 'UInt32' }
    Write-Host "NATIVE_WAIT_SCHEMA switchSchemas=$($switches.Count) directory=$($directory.Version) directoryProperties=$($directory.PropertyCount) opend=$($end.Version) opendProperties=$($end.PropertyCount)"
    $diskAvailable = $false
    $diskStart = $null
    $diskEnd = $null
    try {
        $diskStart = Read-Schema (Get-CimClass -Namespace root/wmi -ClassName DiskIo_TypeGroup2) $diskGuid @(12, 13) @{ Irp = 'UInt32'; IssuingThreadId = 'UInt32' }
        $diskEnd = Read-Schema (Get-CimClass -Namespace root/wmi -ClassName DiskIo_TypeGroup1) $diskGuid @(10, 11) @{ Irp = 'UInt32' }
        $diskAvailable = $true
        Write-Host "NATIVE_WAIT_DISK_SCHEMA available=1 init=$($diskStart.Version) end=$($diskEnd.Version)"
    }
    catch { Write-Host 'NATIVE_WAIT_DISK_SCHEMA available=0' }
    $headerAvailable = $false
    $header = $null
    try {
        $header = Read-Schema (Get-CimClass -Namespace root/wmi -ClassName EventTrace_Header) '68fdd900-4a3e-11d1-84f4-0000f80464e3' @(0) @{
            EventsLost = 'UInt32'; BuffersLost = 'UInt32'; StartTime = 'UInt64'; EndTime = 'UInt64'; PerfFreq = 'UInt64'
        }
        $headerAvailable = $true
    }
    catch { Write-Host 'NATIVE_WAIT_HEADER_SCHEMA available=0' }
    return @{ ThreadProvider = $threadGuid; Switches = $switches; Directory = $directory; End = $end;
        DiskAvailable = $diskAvailable; DiskStart = $diskStart; DiskEnd = $diskEnd;
        HeaderAvailable = $headerAvailable; Header = $header }
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
    try { $schemas = Read-KernelSchemas }
    catch { return 32 }
    $threadGuid = $schemas.ThreadProvider
    $switches = $schemas.Switches
    $directory = $schemas.Directory
    $end = $schemas.End
    $diskAvailable = $schemas.DiskAvailable
    $diskStart = $schemas.DiskStart
    $diskEnd = $schemas.DiskEnd
    $headerAvailable = $schemas.HeaderAvailable
    $header = $schemas.Header
    $waits = @{}
    $statuses = @{}
    $pendingFile = @{}
    $pendingDisk = @{}
    $statistics = @{ Switch = 0L; Directory = 0L; FileEnd = 0L; FileTicks = 0L; DiskInit = 0L; DiskEnd = 0L; DiskTicks = 0L; Unknown = 0L; SwitchUnknown = 0L; ModeMissing = 0L; Events = 0L; Headers = 0L }
    $decodeFailed = $false
    try {
        Get-WinEvent -Path $etl -Oldest -ErrorAction Stop | ForEach-Object {
            $event = $_
            try {
                $statistics.Events++
                $schema = $null
                $kind = 0
                if ($event.ProviderId -eq [guid]$threadGuid -and $event.Opcode -eq 36) {
                    $key = '{0}:{1}' -f $event.Version, $event.Properties.Count
                    if (-not $switches.ContainsKey($key)) { $statistics.Unknown++; $statistics.SwitchUnknown++; return }
                    $schema = $switches[$key]
                    $kind = 1
                }
                elseif ($event.ProviderId -eq $directory.Provider -and $event.Opcode -eq 72) { $schema = $directory; $kind = 2 }
                elseif ($event.ProviderId -eq $end.Provider -and $event.Opcode -eq 76) { $schema = $end; $kind = 3 }
                elseif ($diskAvailable -and $event.ProviderId -eq $diskStart.Provider -and $event.Opcode -in @(12, 13)) { $schema = $diskStart; $kind = 4 }
                elseif ($diskAvailable -and $event.ProviderId -eq $diskEnd.Provider -and $event.Opcode -in @(10, 11)) { $schema = $diskEnd; $kind = 5 }
                elseif ($headerAvailable -and $event.ProviderId -eq $header.Provider -and $event.Opcode -eq 0) { $schema = $header; $kind = 6 }
                if ($null -eq $schema) { return }
                if ($event.Version -ne $schema.Version -or $event.Properties.Count -ne $schema.PropertyCount) {
                    $statistics.Unknown++
                    return
                }
                if ($kind -eq 6) {
                    if ($statistics.Headers -ge 16) { throw 'Header cardinality unsupported.' }
                    $eventsLost = [uint64](Read-Number $event $schema 'EventsLost')
                    $buffersLost = [uint64](Read-Number $event $schema 'BuffersLost')
                    $startTime = [uint64](Read-Number $event $schema 'StartTime')
                    $endTime = [uint64](Read-Number $event $schema 'EndTime')
                    $frequency = [uint64](Read-Number $event $schema 'PerfFreq')
                    $statistics.Headers++
                    Write-Host "NATIVE_WAIT_HEADER decoded=1 version=$($event.Version) eventsLost=$eventsLost buffersLost=$buffersLost start100ns=$startTime end100ns=$endTime frequency=$frequency"
                    return
                }
                if ($kind -eq 1) {
                    $tid = [uint32](Read-Number $event $schema 'OldThreadId')
                    if ($targets.Contains($tid)) {
                        $statistics.Switch++
                        $state = [int](Read-Number $event $schema 'OldThreadState')
                        if ($state -lt 0 -or $state -gt 127) { throw 'Thread state unsupported.' }
                        if ($state -eq 5) {
                            $reason = [int](Read-Number $event $schema 'OldThreadWaitReason')
                            $modeAvailable = $schema.Map.ContainsKey('OldThreadWaitMode')
                            $mode = if ($modeAvailable) { [int](Read-Number $event $schema 'OldThreadWaitMode') } else { -1 }
                            if ($reason -lt 0 -or $reason -gt 127 -or ($modeAvailable -and $mode -notin @(0, 1))) { throw 'Wait value unsupported.' }
                            if (-not $modeAvailable) { $statistics.ModeMissing++ }
                            $key = "$tid $reason $mode $([int]$modeAvailable) $($schema.Version)"
                            if (-not $waits.ContainsKey($key) -and $waits.Count -ge 4096) { throw 'Wait aggregation limit exceeded.' }
                            $waits[$key] = 1L + $waits[$key]
                        }
                    }
                }
                elseif ($kind -in @(2, 4)) {
                    $tidName = if ($kind -eq 2) { 'TTID' } else { 'IssuingThreadId' }
                    $tid = [uint32](Read-Number $event $schema $tidName)
                    if ($targets.Contains($tid)) {
                        $irpName = if ($kind -eq 2) { 'IrpPtr' } else { 'Irp' }
                        $irp = [uint64](Read-Number $event $schema $irpName)
                        $pending = if ($kind -eq 2) { $pendingFile } else { $pendingDisk }
                        if ($pending.ContainsKey($irp) -or $pending.Count -ge 65536 -or $null -eq $event.TimeCreated) { throw 'Pairing unavailable.' }
                        $pending[$irp] = $event.TimeCreated.Ticks
                        if ($kind -eq 2) { $statistics.Directory++ } else { $statistics.DiskInit++ }
                    }
                }
                else {
                    $irpName = if ($kind -eq 3) { 'IrpPtr' } else { 'Irp' }
                    $irp = [uint64](Read-Number $event $schema $irpName)
                    $pending = if ($kind -eq 3) { $pendingFile } else { $pendingDisk }
                    if ($pending.ContainsKey($irp)) {
                        if ($null -eq $event.TimeCreated) { throw 'Timestamp unavailable.' }
                        $ticks = $event.TimeCreated.Ticks - $pending[$irp]
                        if ($ticks -lt 0) { throw 'Timestamp order unsupported.' }
                        $pending.Remove($irp)
                        if ($kind -eq 3) {
                            $statistics.FileEnd++
                            $statistics.FileTicks += $ticks
                            $status = [uint32](Read-Number $event $schema 'NtStatus')
                            if (-not $statuses.ContainsKey($status) -and $statuses.Count -ge 4096) { throw 'Status aggregation limit exceeded.' }
                            $statuses[$status] = 1L + $statuses[$status]
                        }
                        else { $statistics.DiskEnd++; $statistics.DiskTicks += $ticks }
                    }
                }
            }
            finally { $event.Dispose() }
        }
    }
    catch { $decodeFailed = $true }
    Write-Host "NATIVE_WAIT_EVIDENCE partial=1 decoderFailed=$([int]$decodeFailed) headersDecoded=$($statistics.Headers) retainedWindowKnown=0 stacksDecoded=0"
    foreach ($key in $waits.Keys) {
        $numbers = $key.Split(' ')
        Write-Host "NATIVE_WAIT_CSWITCH tid=$($numbers[0]) state=5 reason=$($numbers[1]) mode=$($numbers[2]) modeAvailable=$($numbers[3]) version=$($numbers[4]) count=$($waits[$key])"
    }
    foreach ($status in $statuses.Keys) { Write-Host "NATIVE_WAIT_NTSTATUS value=$status count=$($statuses[$status])" }
    Write-Host "NATIVE_WAIT_EVENTS total=$($statistics.Events) switches=$($statistics.Switch) undecoded=$($statistics.Unknown) unsupportedSwitchEvents=$($statistics.SwitchUnknown) waitingModeUnavailable=$($statistics.ModeMissing)"
    Write-Host "NATIVE_WAIT_FILE starts=$($statistics.Directory) ends=$($statistics.FileEnd) duration100ns=$($statistics.FileTicks) unmatched=$($pendingFile.Count)"
    if ($diskAvailable) {
        Write-Host "NATIVE_WAIT_DISK starts=$($statistics.DiskInit) ends=$($statistics.DiskEnd) duration100ns=$($statistics.DiskTicks) unmatched=$($pendingDisk.Count)"
    }
    if ($decodeFailed) { return 33 }
    if ($statistics.Unknown -ne 0) { return 34 }
    if ($statistics.Switch -eq 0 -or $statistics.Directory -eq 0 -or $statistics.FileEnd -eq 0) { return 35 }
    if (-not $diskAvailable) { return 37 }
    # The measurement reports only retained, positively decoded events.
    return 36
}

try {
    try {
        $wpr = (Get-Command wpr.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
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
        if (-not $valid) { $measurement = 11 }
        else {
            $status = Invoke-PrivateWpr @('-status') 'before'
            if ($status.Code -ne 0 -or $status.Text -notmatch '\bWPR is not recording\b') { $measurement = 12 }
            else {
                $start = Invoke-PrivateWpr @('-start', $profile, '-recordtempto', $private) 'start'
                if ($start.Code -eq 0) { $owned = $true; $measurement = 13 }
                else { $measurement = 14 }
            }
        }
    }
    catch { $measurement = 15 }
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
            $status = Invoke-PrivateWpr @('-status', 'collectors', '-details') 'collectors'
            $loss = [regex]::Matches($status.Text, '(?im)^\s*Events Lost\s*:\s*(\d+)\s*$')
            $dropped = [regex]::Match($status.Text, '(?im)^\s*Dropped event\s*:\s*(\d+)\s*$')
            $knownLoss = $status.Code -eq 0 -and $loss.Count -eq 1 -and $dropped.Success
            $lost = if ($knownLoss) { [uint64]$loss[0].Groups[1].Value } else { 0 }
            $droppedCount = if ($knownLoss) { [uint64]$dropped.Groups[1].Value } else { 0 }
            if ($knownLoss) { Write-Host "NATIVE_WAIT_LIVE_LOSS known=1 eventsLost=$lost droppedEvents=$droppedCount" }
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
        try {
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

# Codes 31 through 37 distinguish target, schema, decoder, coverage, and retention prerequisites.
if ($testExit -ne 0) { exit $testExit }
if ($measurement -ne 0) { exit $measurement }
