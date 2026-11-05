# This sampler uses observed header mappings and never publishes private record values.
function Measure-PrivatePayloadXperfValues([string] $Report, [long] $DataOffset, $HeaderRows, $Events, $Fields) {
    $eventRoles = @{
        CSwitch = 1; ReadyThread = 2
        'FileIo/DirEnum' = 3; DirEnum = 3; FileIoDirEnum = 3; FileIoDirectoryEnum = 3
        FileDirectoryEnum = 3; FileDirEnum = 3
        'FileIo/OpEnd' = 4; FileOpEnd = 4; FileIoOpEnd = 4; FileIoOperationEnd = 4
        FileOperationEnd = 4
        'DiskIo/ReadInit' = 5; DiskReadInit = 5; 'DiskIo/WriteInit' = 6; DiskWriteInit = 6
        'DiskIo/Read' = 7; DiskRead = 7; 'DiskIo/Write' = 8; DiskWrite = 8
    }
    # Enum IDs are local lexical dictionary IDs rather than native enum numbers.
    $enumLabels = @(
        @('Initialized', 'Ready', 'Running', 'Standby', 'Terminated', 'Waiting', 'Transition', 'DeferredReady', 'GateWaitObsolete', 'WaitingForProcessInSwap', 'Wait'),
        @('Executive', 'FreePage', 'PageIn', 'PoolAllocation', 'DelayExecution', 'Suspended', 'UserRequest', 'WrExecutive', 'WrFreePage', 'WrPageIn', 'WrPoolAllocation', 'WrDelayExecution', 'WrSuspended', 'WrUserRequest', 'WrEventPair', 'WrQueue', 'WrLpcReceive', 'WrLpcReply', 'WrVirtualMemory', 'WrPageOut', 'WrRendezvous', 'WrKeyedEvent', 'WrTerminated', 'WrProcessInSwap', 'WrCpuRateControl', 'WrCalloutStack', 'WrKernel', 'WrResource', 'WrPushLock', 'WrMutex', 'WrQuantumEnd', 'WrDispatchInt', 'WrPreempted', 'WrYieldExecution', 'WrFastMutex', 'WrGuardedMutex', 'WrRundown', 'WrAlertByThreadId', 'WrDeferredPreempt', 'WrPhysicalFault', 'WrIoRing', 'WrMdlCache', 'WrRcu'),
        @('KernelMode', 'UserMode')
    )
    $enumFieldLabels = @(
        @('Old State', 'OldThreadState', 'Old Thread State'),
        @('Old Wait Reason', 'OldThreadWaitReason', 'Old Thread Wait Reason'),
        @('Old Wait Mode', 'OldThreadWaitMode', 'Old Thread Wait Mode')
    )
    $enumFields = @{}
    for ($kind = 0; $kind -lt $enumFieldLabels.Count; $kind++) {
        foreach ($label in $enumFieldLabels[$kind]) {
            $id = 0
            if ($Fields.TryGetValue($label, [ref]$id)) { $enumFields[$id] = $kind }
        }
    }
    $headers = @{}
    $duplicates = @{}
    foreach ($row in $HeaderRows) {
        if ($row.Event -eq 0) { continue }
        if ($headers.ContainsKey($row.Event)) { $duplicates[$row.Event] = $true }
        else { $headers[$row.Event] = $row }
    }
    $samples = @{}
    foreach ($entry in $Events.GetEnumerator()) {
        if (-not $eventRoles.ContainsKey($entry.Key)) { continue }
        if (-not $headers.ContainsKey($entry.Value)) {
            Write-Host "NATIVE_XPERF_VALUE_HEADER eventId=$($entry.Value) role=$($eventRoles[$entry.Key]) present=0 ambiguous=0 usable=0 absenceClaim=0"
            continue
        }
        $header = $headers[$entry.Value]
        $ambiguous = $duplicates.ContainsKey($entry.Value)
        $fieldCounts = @{}
        foreach ($field in $header.Fields) {
            if ($field -eq 0) { continue }
            if (-not $fieldCounts.ContainsKey($field)) { $fieldCounts[$field] = 0 }
            $fieldCounts[$field]++
        }
        $usable = -not $ambiguous -and $header.Delimiter -eq 1
        Write-Host "NATIVE_XPERF_VALUE_HEADER eventId=$($entry.Value) role=$($eventRoles[$entry.Key]) present=1 ambiguous=$([int]$ambiguous) usable=$([int]$usable) absenceClaim=0"
        if (-not $usable) { continue }
        $slots = [Collections.Generic.List[object]]::new()
        foreach ($field in $header.Fields) {
            $repeated = $field -ne 0 -and $fieldCounts[$field] -gt 1
            $slots.Add(@{ Field = $field; Repeated = $repeated; Counts = [long[]]::new(5); Enums = @{} })
        }
        $samples[$entry.Value] = @{ Role = $eventRoles[$entry.Key]; Rows = 0; WidthUnknown = 0; Capped = 0; Slots = $slots }
    }
    $maxBytes = 2097152
    $maxLines = 12000
    $maxLineBytes = 16384
    $maxPerEvent = 16
    $maxSeconds = 8
    $state = @{ Bytes = 0L; Lines = 0; LongLines = 0; Nul = 0L; ParseUnknown = 0; EventUnknown = 0;
        UnexpectedHeader = 0; Halt = 0; TimeLimit = 0; ByteLimit = 0; LineLimit = 0; Failed = 0; HResult = 0; Eof = 0 }
    $stream = $null
    $totalBytes = -1L
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $consumeLine = {
        param([string] $Text, [bool] $Overlong)
        $state.Lines++
        if ($Overlong) { $state.LongLines++; $state.Halt = 1; return }
        if ($Text.IndexOf([char]0) -ge 0) { $state.ParseUnknown++; $state.Halt = 1; return }
        if ([string]::IsNullOrWhiteSpace($Text)) { return }
        if ($Text.Trim() -cin @('BeginHeader', 'EndHeader')) { $state.UnexpectedHeader++; $state.Halt = 1; return }
        $reader = $null
        $parser = $null
        try {
            $reader = [IO.StringReader]::new($Text)
            $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($reader)
            $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
            $parser.SetDelimiters(',')
            $parser.HasFieldsEnclosedInQuotes = $true
            $parser.TrimWhiteSpace = $true
            $columns = $parser.ReadFields()
            if ($null -eq $columns -or $columns.Count -eq 0 -or $columns.Count -gt 129 -or -not $parser.EndOfData) {
                $state.ParseUnknown++
                $state.Halt = 1
                return
            }
        }
        catch { $state.ParseUnknown++; $state.Halt = 1; return }
        finally {
            if ($null -ne $parser) { $parser.Dispose() }
            if ($null -ne $reader) { $reader.Dispose() }
        }
        $eventId = 0
        if (-not $Events.TryGetValue($columns[0], [ref]$eventId)) { $state.EventUnknown++; return }
        if (-not $samples.ContainsKey($eventId)) { return }
        $sample = $samples[$eventId]
        if ($columns.Count -ne $sample.Slots.Count + 1) { $sample.WidthUnknown++; return }
        if ($sample.Rows -ge $maxPerEvent) { $sample.Capped++; return }
        $sample.Rows++
        for ($index = 1; $index -lt $columns.Count; $index++) {
            $value = $columns[$index]
            $category = 4
            $number = 0.0
            if ($value.Length -eq 0) { $category = 0 }
            elseif ([regex]::IsMatch($value, '\A[+-]?[0-9]+\z')) { $category = 1 }
            elseif ([regex]::IsMatch($value, '\A0[xX][0-9a-fA-F]+\z')) { $category = 2 }
            elseif ([double]::TryParse($value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and [double]::IsFinite($number)) { $category = 3 }
            $slot = $sample.Slots[$index - 1]
            $slot.Counts[$category]++
            if ($category -eq 4 -and -not $slot.Repeated -and $enumFields.ContainsKey($slot.Field)) {
                $kind = $enumFields[$slot.Field]
                $enumId = [Array]::IndexOf($enumLabels[$kind], $value)
                if ($enumId -ge 0) {
                    $key = "$kind/$enumId"
                    if (-not $slot.Enums.ContainsKey($key)) { $slot.Enums[$key] = @{ Kind = $kind; Id = $enumId; Count = 0 } }
                    $slot.Enums[$key].Count++
                }
            }
        }
    }
    try {
        $stream = [IO.File]::Open($Report, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $totalBytes = $stream.Length
        if ($DataOffset -lt 0 -or $DataOffset -gt $totalBytes) { throw [IO.InvalidDataException]::new() }
        $null = $stream.Seek($DataOffset, [IO.SeekOrigin]::Begin)
        $buffer = [byte[]]::new(8192)
        $line = [Text.StringBuilder]::new()
        $overlong = $false
        :scan while ($state.Bytes -lt $maxBytes -and $state.Lines -lt $maxLines) {
            if ($timer.Elapsed.TotalSeconds -ge $maxSeconds) { $state.TimeLimit = 1; break }
            $count = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $maxBytes - $state.Bytes))
            if ($count -eq 0) { $state.Eof = 1; break }
            for ($index = 0; $index -lt $count; $index++) {
                if (($index % 256) -eq 0 -and $timer.Elapsed.TotalSeconds -ge $maxSeconds) { $state.TimeLimit = 1; break scan }
                $value = $buffer[$index]
                $state.Bytes++
                if ($value -eq 0) { $state.Nul++ }
                if ($value -eq 10) {
                    & $consumeLine $line.ToString() $overlong
                    $null = $line.Clear()
                    $overlong = $false
                    if ($state.Halt -eq 1) { break scan }
                    if ($state.Lines -ge $maxLines) { break scan }
                }
                elseif ($line.Length -lt $maxLineBytes) { $null = $line.Append([char]$value) }
                else { $overlong = $true }
            }
        }
        if ($DataOffset + $state.Bytes -eq $totalBytes) { $state.Eof = 1 }
        if ($state.Eof -eq 1 -and ($line.Length -gt 0 -or $overlong)) { & $consumeLine $line.ToString() $overlong }
        if ($state.Eof -eq 0 -and $state.Bytes -ge $maxBytes) { $state.ByteLimit = 1 }
        if ($state.Eof -eq 0 -and $state.Lines -ge $maxLines) { $state.LineLimit = 1 }
    }
    catch { $state.Failed = 1; $state.HResult = $_.Exception.HResult }
    finally { if ($null -ne $stream) { $stream.Dispose() }; $timer.Stop() }
    foreach ($eventId in @($samples.Keys | Sort-Object)) {
        $sample = $samples[$eventId]
        Write-Host "NATIVE_XPERF_VALUE_EVENT eventId=$eventId role=$($sample.Role) sampled=$($sample.Rows) widthUnknown=$($sample.WidthUnknown) capped=$($sample.Capped) absenceClaim=0"
        for ($index = 0; $index -lt $sample.Slots.Count; $index++) {
            $slot = $sample.Slots[$index]
            Write-Host "NATIVE_XPERF_VALUE_SLOT eventId=$eventId column=$($index + 1) fieldId=$($slot.Field) fieldUnknown=$([int]($slot.Field -eq 0)) repeatedLabel=$([int]$slot.Repeated) empty=$($slot.Counts[0]) integer=$($slot.Counts[1]) hex=$($slot.Counts[2]) finiteNumber=$($slot.Counts[3]) unknownText=$($slot.Counts[4]) timestampUnitsKnown=0"
            foreach ($match in @($slot.Enums.Values | Sort-Object Kind, Id)) {
                $label = $enumLabels[$match.Kind][$match.Id]
                Write-Host "NATIVE_XPERF_VALUE_ENUM eventId=$eventId column=$($index + 1) fieldId=$($slot.Field) kind=$($match.Kind + 1) dictionaryId=$($match.Id + 1) label=[$label] matches=$($match.Count) lexicalOnly=1 nativeEnumNumberKnown=0"
            }
        }
    }
    Write-Host "NATIVE_XPERF_VALUE_SCOPE reportBytes=$totalBytes dataOffset=$DataOffset inspectedBytes=$($state.Bytes) lines=$($state.Lines) eof=$($state.Eof) fullReport=0 longLines=$($state.LongLines) nulBytes=$($state.Nul) parseUnknown=$($state.ParseUnknown) eventUnknown=$($state.EventUnknown) unexpectedHeader=$($state.UnexpectedHeader) structureHalt=$($state.Halt) byteLimit=$($state.ByteLimit) lineLimit=$($state.LineLimit) timeLimit=$($state.TimeLimit) failure=$($state.Failed) hresult=$($state.HResult) maxBytes=$maxBytes maxLines=$maxLines maxLineBytes=$maxLineBytes maxPerEvent=$maxPerEvent maxSeconds=$maxSeconds elapsedMs=$($timer.ElapsedMilliseconds) multilineRecordsSupported=0 integerCategoryLexical=1 decoded=0 targetProcessCausality=0"
    return ($state.Failed -eq 0)
}
