# This file defines functions only and never records or deletes a caller's trace.
function Invoke-PrivateWpaNative([string] $Executable, [string[]] $Arguments, [string] $Directory, [string] $Stage, [int] $DeadlineMs) {
    $process = [Diagnostics.Process]::new()
    $started = $false
    $terminated = $true
    $drained = $false
    $timedOut = $false
    $limited = $false
    $exitKnown = $false
    $code = -1
    $hresult = 0
    $bytes = 0L
    $pipes = @()
    try {
        $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
        $process.StartInfo.FileName = $Executable
        $process.StartInfo.WorkingDirectory = $Directory
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
        $started = $process.Start()
        if (-not $started) { throw 'Owned process was not started.' }
        $terminated = $false
        foreach ($name in @('out', 'err')) {
            $stream = if ($name -eq 'out') { $process.StandardOutput.BaseStream } else { $process.StandardError.BaseStream }
            $log = [IO.File]::Open((Join-Path $Directory ($Stage + '-' + $name + '.txt')), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            $buffer = [byte[]]::new(4096)
            $pipes += @{ Stream = $stream; Log = $log; Buffer = $buffer; Read = $stream.ReadAsync($buffer, 0, $buffer.Length); Done = $false }
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            foreach ($pipe in $pipes) {
                if (-not $pipe.Done -and $pipe.Read.IsCompleted) {
                    $count = $pipe.Read.GetAwaiter().GetResult()
                    if ($count -eq 0) { $pipe.Done = $true }
                    else {
                        $bytes += $count
                        if ($bytes -gt 1048576) { $limited = $true; break }
                        $pipe.Log.Write($pipe.Buffer, 0, $count)
                        $pipe.Read = $pipe.Stream.ReadAsync($pipe.Buffer, 0, $pipe.Buffer.Length)
                    }
                }
            }
            $terminated = $process.HasExited
            $drained = $pipes.Count -eq 2 -and $pipes[0].Done -and $pipes[1].Done
            if ($limited -or ($terminated -and $drained)) { break }
            if ($clock.ElapsedMilliseconds -ge $DeadlineMs) { $timedOut = $true; break }
            Start-Sleep -Milliseconds 10
        }
    }
    catch { $hresult = $_.Exception.HResult }
    finally {
        if ($started) {
            try {
                if (-not $process.HasExited) { $process.Kill($true) }
                $terminated = $process.WaitForExit(5000)
                if ($terminated) { $code = $process.ExitCode; $exitKnown = $true }
                # Inherited output handles receive a separate finite drain deadline.
                $drainClock = [Diagnostics.Stopwatch]::StartNew()
                while (-not $drained -and $pipes.Count -eq 2 -and $drainClock.ElapsedMilliseconds -lt 1000) {
                    foreach ($pipe in $pipes) {
                        if (-not $pipe.Done -and $pipe.Read.IsCompleted) {
                            $count = $pipe.Read.GetAwaiter().GetResult()
                            if ($count -eq 0) { $pipe.Done = $true }
                            else {
                                $bytes += $count
                                if ($bytes -le 1048576) { $pipe.Log.Write($pipe.Buffer, 0, $count) }
                                else { $limited = $true }
                                $pipe.Read = $pipe.Stream.ReadAsync($pipe.Buffer, 0, $pipe.Buffer.Length)
                            }
                        }
                    }
                    $drained = $pipes[0].Done -and $pipes[1].Done
                    if (-not $drained) { Start-Sleep -Milliseconds 10 }
                }
            }
            catch { $hresult = $_.Exception.HResult }
        }
        foreach ($pipe in $pipes) { $pipe.Log.Dispose(); $pipe.Stream.Dispose() }
        $process.Dispose()
    }
    Write-Host "WPA_EXPORT_NATIVE stage=$Stage started=$([int]$started) exitKnown=$([int]$exitKnown) exit=$code timedOut=$([int]$timedOut) terminated=$([int]$terminated) drained=$([int]$drained) outputLimited=$([int]$limited) outputBytes=$bytes hresult=$hresult"
    return @{ Success = $started -and $terminated -and $drained -and $exitKnown -and $code -eq 0 -and -not $timedOut -and -not $limited -and $hresult -eq 0; Bytes = $bytes }
}

function New-PrivateWpaCsvParser([IO.Stream] $Stream) {
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($Stream, [Text.UTF8Encoding]::new($false, $true), $true, $true)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false
    return $parser
}

function Test-PrivateWpaRawIdentityColumns([string] $ProfilePath) {
    $reader = $null
    $known = $false
    $keyCount = 0U
    $matches = 0
    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 2097152
        $settings.MaxCharactersFromEntities = 1024
        $reader = [Xml.XmlReader]::Create($ProfilePath, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $manager.AddNamespace('w', 'http://tempuri.org/SerializableElement.xsd')
        $presets = $document.SelectNodes('/w:WpaProfileContainer[@Version="2"]/w:Content/w:Views/w:View/w:Graphs/w:Graph[translate(@Guid,"ABCDEF","abcdef")="c58f5fea-0319-4046-932d-e695ebe20b47"]/w:Preset', $manager)
        if ($presets.Count -ne 1) { throw [IO.InvalidDataException]::new() }
        $columns = $presets[0].SelectNodes('w:Columns/w:Column', $manager)
        if (-not [uint32]::TryParse($presets[0].GetAttribute('KeyColumnCount'), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$keyCount) -or $keyCount -ne 0) { throw [IO.InvalidDataException]::new() }
        $identities = [ordered]@{ 'New Process' = 'b065487c-5e32-4f1f-a2cd-581e086ce29e'; 'New Thread Id' = '944ed37a-5774-421e-b2d5-84f17a4b3a05'; 'New Prev Wait Reason' = 'fe7ccd45-6275-413e-a538-3587f0eb452b' }
        foreach ($label in $identities.Keys) {
            $occurrences = 0
            $identity = $false
            for ($i = 0; $i -lt $columns.Count; $i++) {
                if ([string]::Equals($columns[$i].GetAttribute('Name'), $label, [StringComparison]::Ordinal)) {
                    $occurrences++
                    $identity = [string]::Equals($columns[$i].GetAttribute('Guid'), $identities[$label], [StringComparison]::OrdinalIgnoreCase)
                }
            }
            if ($occurrences -eq 1 -and $identity) { $matches++ }
            Write-Host "WPA_TARGET_PROFILE_IDENTITY label=$label occurrences=$occurrences rawIdentityColumn=$([int]($occurrences -eq 1 -and $identity))"
        }
        $known = $matches -eq 3
    }
    catch { Write-Host "WPA_TARGET_PROFILE_KEYS_UNKNOWN hresult=$($_.Exception.HResult)" }
    finally { if ($null -ne $reader) { $reader.Dispose() } }
    Write-Host "WPA_TARGET_PROFILE_KEYS declaredKeyColumns=$keyCount rawIdentityMatches=$matches rawRowGroupingUnknown=$([int](-not $known))"
    return $known
}

function Read-PrivateNativeWaitWpaReports([string] $Directory, [string] $Scratch, [string] $ProfilePath, [uint32] $TargetProcessId, [uint32[]] $TargetThreadIds) {
    $required = @('New Process', 'New Thread Id', 'New Prev Wait Reason', 'Readying Process', 'Readying Thread Id', 'CPU')
    if (-not (Test-PrivateWpaRawIdentityColumns $ProfilePath)) { return @{ Status = 44 } }
    $queue = [Collections.Generic.Queue[object]]::new()
    $queue.Enqueue(@{ Path = $Directory; Depth = 0 })
    $selected = $null
    $selectedHeader = $null
    $selectedHeaderBytes = 0L
    $candidates = 0
    $directories = 0
    $entries = 0
    $files = 0
    $csvFiles = 0
    $headerBytes = 0L
    $discoveryUnknown = 0
    $discoveryTruncated = $false
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Write-Host 'WPA_TARGET_LIMITS directories=16 depth=2 entries=256 files=64 headerBytesPerFile=262144 totalHeaderBytes=4194304 tableBytes=67108864 tableRows=2000000 workerMs=90000'
    while ($queue.Count -gt 0 -and -not $discoveryTruncated) {
        if ($directories -ge 16 -or $clock.ElapsedMilliseconds -ge 15000) { $discoveryTruncated = $true; break }
        $item = $queue.Dequeue()
        $enumerator = $null
        try {
            if (([IO.File]::GetAttributes($item.Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $discoveryUnknown++; continue }
            $directories++
            $enumerator = [IO.Directory]::EnumerateFileSystemEntries($item.Path).GetEnumerator()
            while ($enumerator.MoveNext()) {
                if ($entries -ge 256 -or $files -ge 64 -or $headerBytes -ge 4194304 -or $clock.ElapsedMilliseconds -ge 15000) { $discoveryTruncated = $true; break }
                $entries++
                $path = $enumerator.Current
                $attributes = [IO.File]::GetAttributes($path)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $discoveryUnknown++; continue }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    if ($item.Depth -ge 2 -or $queue.Count + $directories -ge 16) { $discoveryTruncated = $true; break }
                    $queue.Enqueue(@{ Path = $path; Depth = $item.Depth + 1 })
                    continue
                }
                $files++
                if (-not [string]::Equals([IO.Path]::GetExtension($path), '.csv', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $csvFiles++
                $stream = $null
                $memory = $null
                $parser = $null
                try {
                    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                    $size = [int][Math]::Min($stream.Length, [Math]::Min(262144, 4194304 - $headerBytes))
                    $buffer = [byte[]]::new($size)
                    $offset = 0
                    while ($offset -lt $size) {
                        $count = $stream.Read($buffer, $offset, $size - $offset)
                        if ($count -eq 0) { throw [IO.EndOfStreamException]::new() }
                        $offset += $count
                    }
                    $headerBytes += $offset
                    $memory = [IO.MemoryStream]::new($buffer, $false)
                    $parser = New-PrivateWpaCsvParser $memory
                    if ($parser.EndOfData) { throw [IO.InvalidDataException]::new() }
                    [string[]] $header = $parser.ReadFields()
                    if ($header.Length -lt 2 -or $header.Length -gt 256 -or ($size -lt $stream.Length -and $parser.EndOfData)) { throw [IO.InvalidDataException]::new() }
                    $matched = 0
                    $duplicates = 0
                    foreach ($label in $required) {
                        $occurrences = 0
                        foreach ($value in $header) { if ([string]::Equals($value, $label, [StringComparison]::Ordinal)) { $occurrences++ } }
                        if ($occurrences -eq 1) { $matched++ }
                        if ($occurrences -gt 1) { $duplicates++ }
                    }
                    Write-Host "WPA_TARGET_HEADER table=$csvFiles columns=$($header.Length) requiredUnique=$matched requiredDuplicates=$duplicates"
                    if ($duplicates -gt 0) { $discoveryUnknown++ }
                    if ($matched -eq $required.Count -and $duplicates -eq 0) { $candidates++; $selected = $path; $selectedHeader = $header; $selectedHeaderBytes = $size }
                }
                catch { $discoveryUnknown++; Write-Host "WPA_TARGET_HEADER_UNKNOWN table=$csvFiles hresult=$($_.Exception.HResult)" }
                finally {
                    if ($null -ne $parser) { $parser.Dispose() }
                    if ($null -ne $memory) { $memory.Dispose() }
                    if ($null -ne $stream) { $stream.Dispose() }
                }
            }
        }
        catch { $discoveryUnknown++; Write-Host "WPA_TARGET_DISCOVERY_UNKNOWN hresult=$($_.Exception.HResult)" }
        finally { if ($null -ne $enumerator) { $enumerator.Dispose() } }
    }
    Write-Host "WPA_TARGET_DISCOVERY csvFiles=$csvFiles candidates=$candidates unknown=$discoveryUnknown truncated=$([int]$discoveryTruncated) headerBytes=$headerBytes"
    if ($discoveryUnknown -gt 0 -or $discoveryTruncated -or $candidates -ne 1) { return @{ Status = 44 } }

    $indices = [Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $selectedHeader.Length; $i++) {
        if ($required -ccontains $selectedHeader[$i]) { $indices.Add($selectedHeader[$i], $i) }
    }
    # These are exact public wait-reason text labels, never numeric native enum mappings.
    $reasonLabels = @('Executive', 'FreePage', 'PageIn', 'PoolAllocation', 'DelayExecution', 'Suspended', 'UserRequest', 'WrExecutive', 'WrFreePage', 'WrPageIn', 'WrPoolAllocation', 'WrDelayExecution', 'WrSuspended', 'WrUserRequest', 'WrEventPair', 'WrQueue', 'WrLpcReceive', 'WrLpcReply', 'WrVirtualMemory', 'WrPageOut', 'WrRendezvous', 'WrKeyedEvent', 'WrTerminated', 'WrProcessInSwap', 'WrCpuRateControl', 'WrCalloutStack', 'WrKernel', 'WrResource', 'WrPushLock', 'WrMutex', 'WrQuantumEnd', 'WrDispatchInt', 'WrPreempted', 'WrYieldExecution', 'WrFastMutex', 'WrGuardedMutex', 'WrRundown', 'WrAlertByThreadId', 'WrDeferredPreempt', 'WrPhysicalFault', 'WrIoRing', 'WrMdlCache', 'WrRcu')
    $reasons = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
    foreach ($label in $reasonLabels) { $reasons.Add($label, 0L) }
    $threads = [Collections.Generic.HashSet[uint32]]::new()
    foreach ($thread in $TargetThreadIds) { $null = $threads.Add($thread) }
    $durationColumns = [Collections.Generic.List[object]]::new()
    $durationCandidates = 0
    $durationUnknown = 0
    $scales = [ordered]@{ '(s)' = 1.0; '(ms)' = 0.001; '(us)' = 0.000001; '(µs)' = 0.000001; '(ns)' = 0.000000001 }
    for ($i = 0; $i -lt $selectedHeader.Length; $i++) {
        $recognized = $false
        foreach ($base in @('Waits', 'Ready', 'CPU Usage', 'CPU Usage (in view)')) {
            if ([string]::Equals($selectedHeader[$i], $base, [StringComparison]::Ordinal)) { $durationCandidates++; $durationUnknown++; $recognized = $true; break }
            foreach ($suffix in $scales.Keys) {
                if ([string]::Equals($selectedHeader[$i], ($base + ' ' + $suffix), [StringComparison]::Ordinal)) {
                    $durationCandidates++
                    $durationColumns.Add(@{ Column = $i; Label = $base; Unit = $suffix; Scale = $scales[$suffix]; Values = 0L; Unknown = 0L; Seconds = 0.0 })
                    $recognized = $true
                    break
                }
            }
            if ($recognized) { break }
        }
    }
    $source = $null
    $prefix = $null
    $parser = $null
    $readStream = $null
    $rows = 0L
    $targetRows = 0L
    $reasonRows = 0L
    $unknownReasons = 0L
    $processUnknown = 0L
    $threadUnknown = 0L
    $widthUnknown = 0L
    $parseUnknown = 0
    $bytes = 0L
    $truncated = $false
    $byteTruncated = $false
    $complete = $false
    $pidValidated = $false
    try {
        $source = [IO.File]::Open($selected, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $tableLimit = 67108864 - $selectedHeaderBytes
        $byteTruncated = $source.Length -gt $tableLimit
        # Only oversized tables need a bounded private prefix, and its final record is never retained.
        if ($byteTruncated) {
            $prefixPath = Join-Path $Scratch 'selected-prefix.csv'
            $prefix = [IO.File]::Open($prefixPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $buffer = [byte[]]::new(65536)
            while ($bytes -lt $tableLimit) {
                if ($clock.ElapsedMilliseconds -ge 85000) { throw [TimeoutException]::new() }
                $count = $source.Read($buffer, 0, [int][Math]::Min($buffer.Length, $tableLimit - $bytes))
                if ($count -eq 0) { throw [IO.EndOfStreamException]::new() }
                $prefix.Write($buffer, 0, $count)
                $bytes += $count
            }
            $prefix.Dispose(); $prefix = $null
            $source.Dispose(); $source = $null
            $readStream = [IO.File]::OpenRead($prefixPath)
        }
        else { $bytes = $source.Length; $readStream = $source; $source = $null }
        $parser = New-PrivateWpaCsvParser $readStream
        [string[]] $header = $parser.ReadFields()
        if ($header.Length -ne $selectedHeader.Length) { throw [IO.InvalidDataException]::new() }
        for ($i = 0; $i -lt $header.Length; $i++) { if (-not [string]::Equals($header[$i], $selectedHeader[$i], [StringComparison]::Ordinal)) { throw [IO.InvalidDataException]::new() } }
        while (-not $parser.EndOfData) {
            if ($rows -ge 2000000 -or $clock.ElapsedMilliseconds -ge 85000) { $truncated = $true; break }
            [string[]] $fields = $parser.ReadFields()
            if ($byteTruncated -and $parser.EndOfData) { $truncated = $true; break }
            $rows++
            if ($fields.Length -ne $header.Length) { $widthUnknown++; continue }
            $threadId = 0U
            $threadText = $fields[$indices['New Thread Id']]
            if (-not [regex]::IsMatch($threadText, '\A[0-9]+\z', [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromMilliseconds(50)) -or -not [uint32]::TryParse($threadText, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$threadId)) { $threadUnknown++; continue }
            if (-not $threads.Contains($threadId)) { continue }
            $match = [regex]::Match($fields[$indices['New Process']], '\A[^\r\n]+ \(([0-9]+)\)\z', [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromMilliseconds(50))
            $processId = 0U
            if (-not $match.Success -or -not [uint32]::TryParse($match.Groups[1].Value, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$processId)) { $processUnknown++; continue }
            if ($processId -ne $TargetProcessId) { continue }
            $pidValidated = $true
            $targetRows++
            $reason = $fields[$indices['New Prev Wait Reason']]
            if ($reasons.ContainsKey($reason)) { $reasons[$reason]++; $reasonRows++ }
            else { $unknownReasons++ }
            foreach ($duration in $durationColumns) {
                $number = 0.0
                if ([double]::TryParse($fields[$duration.Column], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and [double]::IsFinite($number) -and $number -ge 0) {
                    $seconds = $duration.Seconds + $number * $duration.Scale
                    if ([double]::IsFinite($seconds)) { $duration.Seconds = $seconds; $duration.Values++ }
                    else { $duration.Unknown++ }
                }
                else { $duration.Unknown++ }
            }
        }
        $truncated = $truncated -or $byteTruncated
        $complete = -not $truncated -and $parser.EndOfData
    }
    catch { $parseUnknown++; Write-Host "WPA_TARGET_PARSE_UNKNOWN hresult=$($_.Exception.HResult)" }
    finally {
        if ($null -ne $parser) { $parser.Dispose() }
        if ($null -ne $readStream) { $readStream.Dispose() }
        if ($null -ne $source) { $source.Dispose() }
        if ($null -ne $prefix) { $prefix.Dispose() }
    }
    foreach ($label in $reasonLabels) { Write-Host "WPA_TARGET_REASON label=$label rows=$($reasons[$label])" }
    foreach ($duration in $durationColumns) {
        $durationUnknown += [int]($duration.Unknown -gt 0)
        $secondsText = $duration.Seconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        Write-Host "WPA_TARGET_DURATION column=$($duration.Column) label=$($duration.Label) unit=$($duration.Unit) values=$($duration.Values) unknown=$($duration.Unknown) retainedColumnValueSumSeconds=$secondsText metricAggregationUnknown=1 wallClockAttributionUnknown=1"
    }
    $status = 0
    if ($parseUnknown -gt 0 -or $widthUnknown -gt 0 -or $threadUnknown -gt 0 -or $processUnknown -gt 0) { $status = 45 }
    elseif (-not $pidValidated -or $targetRows -eq 0 -or $reasonRows -eq 0) { $status = 46 }
    elseif ($unknownReasons -gt 0) { $status = 47 }
    Write-Host "WPA_TARGET_MEASUREMENT status=$status rows=$rows targetRows=$targetRows reasonRows=$reasonRows unknownReasons=$unknownReasons pidSuffixValidated=$([int]$pidValidated) processFormatUnknown=$processUnknown threadFormatUnknown=$threadUnknown rowWidthUnknown=$widthUnknown parseUnknown=$parseUnknown tableBytes=$bytes selectedHeaderProbeBytes=$selectedHeaderBytes truncated=$([int]$truncated) retainedCsvComplete=$([int]$complete) durationCandidates=$durationCandidates durationUnitsUnknown=$([int]($durationCandidates -eq 0 -or $durationUnknown -gt 0)) metricAggregationUnknown=1 fullRecordingWindowUnknown=1 nativeStateUnknown=1 nativeModeUnknown=1 nativeVersionUnknown=1 stackUnknown=1 ioAttributionUnknown=1 diskCausalityUnknown=1 rootCauseUnknown=1"
    return @{ Status = $status }
}

# The private view removes grouping from the inspected original preset to export raw rows.
function New-PrivateThreadWaitWpaProfile([string] $SourcePath, [string] $DestinationPath) {
    $reader = $null
    $writer = $null
    $output = $null
    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 2097152
        $settings.MaxCharactersFromEntities = 1024
        $reader = [Xml.XmlReader]::Create($SourcePath, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $true
        $document.XmlResolver = $null
        $document.Load($reader)
        $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $manager.AddNamespace('w', 'http://tempuri.org/SerializableElement.xsd')
        $presets = $document.SelectNodes('/w:WpaProfileContainer[@Version="2"]/w:Content/w:Views/w:View/w:Graphs/w:Graph[translate(@Guid,"ABCDEF","abcdef")="c58f5fea-0319-4046-932d-e695ebe20b47"]/w:Preset', $manager)
        if ($presets.Count -ne 1 -or $document.SelectNodes('//*[local-name()="FileReference"]').Count -ne 0) { throw [IO.InvalidDataException]::new() }
        $preset = $presets[0]
        $columns = $preset.SelectNodes('w:Columns/w:Column', $manager)
        $guids = @('b065487c-5e32-4f1f-a2cd-581e086ce29e', '944ed37a-5774-421e-b2d5-84f17a4b3a05', 'fe7ccd45-6275-413e-a538-3587f0eb452b')
        $labels = @('New Process', 'New Thread Id', 'New Prev Wait Reason')
        if ($columns.Count -ne 28 -or $preset.GetAttribute('KeyColumnCount') -cne '1') { throw [IO.InvalidDataException]::new() }
        for ($index = 0; $index -lt $guids.Count; $index++) {
            if (-not [string]::Equals($columns[$index].GetAttribute('Guid'), $guids[$index], [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($columns[$index].GetAttribute('Name'), $labels[$index], [StringComparison]::Ordinal)) { throw [IO.InvalidDataException]::new() }
        }
        $original = $document.OuterXml
        $null = $preset.SetAttribute('KeyColumnCount', '0')
        $null = $preset.SetAttribute('KeyColumnCount', '1')
        if ($document.OuterXml -cne $original) { throw [IO.InvalidDataException]::new() }
        $null = $preset.SetAttribute('KeyColumnCount', '0')
        $output = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $writerSettings = [Xml.XmlWriterSettings]::new()
        $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
        $writerSettings.Indent = $false
        $writer = [Xml.XmlWriter]::Create($output, $writerSettings)
        $document.Save($writer)
        $writer.Dispose()
        $writer = $null
        $output.Dispose()
        $output = $null
        $hash = (Get-FileHash -LiteralPath $DestinationPath -Algorithm SHA256).Hash
        Write-Host "WPA_TARGET_PROFILE_VIEW derived=1 originalFileModified=0 originalFingerprintVerified=1 precisePresets=1 originalKeys=1 newKeys=0 semanticAttributesChanged=1 otherXmlSemanticsPreserved=1 fileReferences=0 sha256=$hash"
        return $DestinationPath
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
    }
}

function Get-PrivateNativeWpaPrerequisites([string] $Child) {
    $selection = Join-Path $Child 'selected.wpaProfile'
    $status = 40
    try {
        $command = Get-Command 'WPAExporter.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        foreach ($base in @([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86), [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles))) {
            if ($null -eq $command -and -not [string]::IsNullOrWhiteSpace($base)) {
                $command = Get-Command (Join-Path $base 'Windows Kits\10\Windows Performance Toolkit\WPAExporter.exe') -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            }
        }
        if ($null -eq $command) { throw [IO.FileNotFoundException]::new() }
        $status = 41
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo([string]$command.Source)
        $observedVersion = $version.FileMajorPart -eq 11 -and $version.FileMinorPart -eq 7 -and $version.FileBuildPart -eq 395 -and $version.FilePrivatePart -eq 48728
        Write-Host "WPA_TARGET_TOOL fileMajor=$($version.FileMajorPart) fileMinor=$($version.FileMinorPart) fileBuild=$($version.FileBuildPart) filePrivate=$($version.FilePrivatePart) observedVersion=$([int]$observedVersion) helpInvoked=0 capabilityCheckDeferredToExport=1"
        if (-not $observedVersion) { throw [NotSupportedException]::new() }
        $status = 42
        & (Join-Path $PSScriptRoot 'Invoke-WpaInstalledProfileInventory.ps1') -SelectionCopyPath $selection
        $inventoryExit = $LASTEXITCODE
        $profileSelected = $false
        if ($inventoryExit -eq 0 -and [IO.File]::Exists($selection)) {
            $length = ([IO.FileInfo]::new($selection)).Length
            if ($length -eq 108638) { $profileSelected = (Get-FileHash -LiteralPath $selection -Algorithm SHA256).Hash -ceq '248989AFE19CC92AB856AF84D538B079FFE0597F92C7DBD1A61C1AF917E6B0E8' }
        }
        Write-Host "WPA_TARGET_PROFILE verified=$([int]$profileSelected) inventoryExit=$inventoryExit"
        if (-not $profileSelected) { throw [IO.InvalidDataException]::new() }
        $threadProfile = New-PrivateThreadWaitWpaProfile $selection (Join-Path $Child 'thread-wait.wpaProfile')
        return @{ Status = 0; Application = [string]$command.Source; ProfilePath = $threadProfile }
    }
    catch {
        Write-Host "WPA_TARGET_PREREQUISITE_UNKNOWN status=$status hresult=$($_.Exception.HResult)"
        return @{ Status = [int]$status }
    }
}

function Invoke-PrivateNativeWaitWpa {
    param([string] $TracePath, [string] $PrivateDirectory, [uint32] $TargetProcessId, [uint32[]] $TargetThreadIds)
    $child = $null
    $created = $false
    $job = $null
    $status = 40
    try {
        if (-not [IO.File]::Exists($TracePath) -or -not [IO.Directory]::Exists($PrivateDirectory) -or $TargetProcessId -eq 0 -or $null -eq $TargetThreadIds -or $TargetThreadIds.Count -eq 0 -or $TargetThreadIds.Count -gt 1024 -or $TargetThreadIds -contains 0) { throw [ArgumentException]::new() }
        if (([IO.File]::GetAttributes($PrivateDirectory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw [IO.InvalidDataException]::new() }
        $child = Join-Path $PrivateDirectory ('wpa-target-' + [guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $child
        $created = $true
        $reports = Join-Path $child 'reports'
        $readiness = Get-PrivateNativeWpaPrerequisites $child
        $readinessStatus = [int]$readiness.Status
        if ($readinessStatus -ne 0) { $status = $readinessStatus; throw [IO.InvalidDataException]::new() }
        $status = 43
        $selection = $readiness.ProfilePath
        $null = New-Item -ItemType Directory -Path $reports
        $export = Invoke-PrivateWpaNative $readiness.Application @('-i', $TracePath, '-profile', $selection, '-outputfolder', $reports) $child 'export' 60000
        # A failed exporter cannot be redeemed by partial output files.
        if (-not $export.Success) { throw [IO.InvalidDataException]::new() }
        $status = 44
        $module = Join-Path $PSScriptRoot 'NativeWaitWpa.ps1'
        Write-Host 'WPA_TARGET_WORKER_LIMIT waitSeconds=90 stopCallBounded=0 outerWorkflowRequired=1'
        $job = Start-Job -ArgumentList $module, $reports, $child, $selection, $TargetProcessId, $TargetThreadIds -ScriptBlock {
            param($Module, $Reports, $Scratch, $ProfilePath, $ProcessId, $ThreadIds)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . $Module
            try { Read-PrivateNativeWaitWpaReports $Reports $Scratch $ProfilePath ([uint32]$ProcessId) ([uint32[]]$ThreadIds) }
            catch { Write-Host "WPA_TARGET_WORKER_UNKNOWN hresult=$($_.Exception.HResult)"; return @{ Status = 45 } }
        }
        $finished = Wait-Job -Job $job -Timeout 90
        if ($null -eq $finished) { $status = 48; Write-Host 'WPA_TARGET_WORKER timedOut=1 retainedCsvComplete=0 measurementUnknown=1' }
        elseif ($job.State -ne 'Completed') { $status = 45; Write-Host 'WPA_TARGET_WORKER timedOut=0 measurementUnknown=1' }
        else {
            $results = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
            if ($results.Count -eq 1 -and $null -ne $results[0].Status) { $status = [int]$results[0].Status }
            else { $status = 45; Write-Host 'WPA_TARGET_WORKER resultUnknown=1' }
        }
    }
    catch { Write-Host "WPA_TARGET_UNKNOWN status=$status hresult=$($_.Exception.HResult)" }
    finally {
        if ($null -ne $job) {
            try {
                if ($job.State -notin @('Completed', 'Failed', 'Stopped')) { Stop-Job -Job $job -ErrorAction Stop }
                Remove-Job -Job $job -Force -ErrorAction Stop
            }
            catch { $status = 49; Write-Host "WPA_TARGET_JOB_CLEANUP_UNKNOWN hresult=$($_.Exception.HResult)" }
        }
        $removed = -1
        if ($created) {
            try { Remove-Item -LiteralPath $child -Recurse -Force; $removed = [int](-not [IO.Directory]::Exists($child)) }
            catch { $removed = 0 }
            if ($removed -ne 1) { $status = 49 }
        }
        Write-Host "WPA_TARGET_COMPLETE status=$status privateChildCreated=$([int]$created) privateChildRemoved=$removed callerTraceDeleted=0 callerDirectoryDeleted=0 fullRecordingWindowUnknown=1 nativeStateUnknown=1 nativeModeUnknown=1 nativeVersionUnknown=1 stackUnknown=1 ioAttributionUnknown=1 diskCausalityUnknown=1 rootCauseUnknown=1"
    }
    return @{ Status = [int]$status }
}
