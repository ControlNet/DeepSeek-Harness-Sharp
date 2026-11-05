# This helper exports only the caller's stopped private ETL and never records events.
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/processing
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/tracestats
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/dumper
function Invoke-PrivatePayloadXperfChild([string] $Application, [string[]] $Arguments, [int] $Role) {
    $child = [Diagnostics.Process]::new()
    $started = $false
    $exited = $false
    $timedOut = 0
    $killRequested = 0
    $drained = 0
    $code = 125
    $failure = 0
    $hresult = 0
    $stdout = $null
    $stderr = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $child.StartInfo.FileName = $Application
        $child.StartInfo.UseShellExecute = $false
        $child.StartInfo.CreateNoWindow = $true
        $child.StartInfo.RedirectStandardOutput = $true
        $child.StartInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $child.StartInfo.ArgumentList.Add($argument) }
        $started = $child.Start()
        if ($started) {
            $stdout = $child.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
            $stderr = $child.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
            $exited = $child.WaitForExit(30000)
            if (-not $exited) { $timedOut = 1 }
        }
    }
    catch { $failure = 1; $hresult = $_.Exception.HResult }
    finally {
        if ($started -and -not $exited) {
            try {
                if (-not $child.HasExited) { $killRequested = 1; $child.Kill($true) }
            }
            catch { $failure = 1; $hresult = $_.Exception.HResult }
            try { $exited = $child.WaitForExit(5000) }
            catch { $failure = 1; $hresult = $_.Exception.HResult }
        }
        if ($exited) {
            try { $code = $child.ExitCode }
            catch { $failure = 1; $hresult = $_.Exception.HResult }
        }
        if ($null -ne $stdout -and $null -ne $stderr) {
            try { $drained = [int][Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000) }
            catch { $failure = 1; $hresult = $_.Exception.HResult }
        }
        $timer.Stop()
        $child.Dispose()
        Write-Host "NATIVE_XPERF_CHILD role=$Role started=$([int]$started) exited=$([int]$exited) exit=$code timeout=$timedOut killRequested=$killRequested drained=$drained failure=$failure hresult=$hresult elapsedMs=$($timer.ElapsedMilliseconds) lifetimeMs=30000 observeMs=5000 drainMs=5000"
    }
    return @{ Success = ($started -and $exited -and $code -eq 0 -and $timedOut -eq 0 -and $drained -eq 1 -and $failure -eq 0); Settled = (-not $started -or $exited) }
}

# Tokens are exact allowlisted text observations, not a promise of an xperf CSV schema.
function Measure-PrivatePayloadXperfReport([string] $Report, [int] $Role) {
    $eventNames = @('CSwitch', 'ReadyThread', 'FileIo/DirEnum', 'FileIo/OpEnd', 'DiskIo/ReadInit', 'DiskIo/WriteInit', 'DiskIo/Read', 'DiskIo/Write', 'EventTrace/Header', 'DirEnum', 'FileOpEnd', 'DiskReadInit', 'DiskWriteInit', 'DiskRead', 'DiskWrite', 'Header')
    $fieldNames = @('Event Name', 'TimeStamp', 'Process Name ( PID)', 'ThreadID', 'CPU', 'New TID', 'Old TID', 'NewThreadPriority', 'OldThreadPriority', 'PreviousCState', 'SpareByte', 'OldThreadWaitReason', 'OldThreadWaitMode', 'OldThreadState', 'OldThreadWaitIdealProcessor', 'NewThreadWaitTime', 'Reserved', 'NewThreadId', 'OldThreadId', 'ThreadId', 'ProcessId', 'Irp', 'FileObject', 'FileKey', 'ExtraInfo', 'InfoClass', 'FileIndex', 'FileName', 'IoSize', 'IoFlags', 'DiskNumber', 'IrpFlags', 'TransferSize', 'ByteOffset', 'HighResResponseTime', 'IssuingThreadId', 'NtStatus', 'Provider', 'ProviderId', 'Provider Name', 'Provider GUID', 'Event ID', 'EventId', 'Opcode', 'Version', 'Task', 'Channel', 'Level', 'Count', 'Event Count', 'Events Lost', 'Buffers Lost', 'Events', 'Trace Statistics', 'EventTrace', 'Time', 'PID', 'TID')
    $events = [Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    $fields = [Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $eventNames.Count; $i++) { $events[$eventNames[$i]] = $i + 1 }
    for ($i = 0; $i -lt $fieldNames.Count; $i++) { $fields[$fieldNames[$i]] = $i + 1 }
    $state = @{ Lines = 0; Begin = 0; End = 0; Inside = $false; Invalid = 0; LongLines = 0;
        ByteLimit = 0; LineLimit = 0; TimeLimit = 0; Bytes = 0L; Nul = 0L; Failed = 0; HResult = 0;
        Rows = [Collections.Generic.List[object]]::new(); Hits = @{}; ShapeLines = 0 }
    $maxBytes = 8388608
    $maxLines = 20000
    $maxLineBytes = 4096
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $stream = $null
    $totalBytes = -1L
    $complete = 0
    $line = [Text.StringBuilder]::new()
    $overlong = $false
    # Latin1 preserves ASCII delimiters without assuming the machine's ANSI code page.
    $consumeLine = {
        param([string] $Text, [bool] $Overlong)
        $state.Lines++
        if ($Overlong) { $state.LongLines++; if ($state.Inside) { $state.Invalid = 1 }; return }
        $textLine = $Text.Trim()
        $commas = 0
        $tabs = 0
        $colons = 0
        $quotes = 0
        foreach ($character in $Text.ToCharArray()) {
            if ($character -eq ',') { $commas++ }
            elseif ($character -eq "`t") { $tabs++ }
            elseif ($character -eq ':') { $colons++ }
            elseif ($character -eq '"') { $quotes++ }
        }
        if ($state.ShapeLines -lt 16) {
            Write-Host "NATIVE_XPERF_LINE role=$Role line=$($state.Lines) bytes=$($Text.Length) comma=$commas tab=$tabs colon=$colons quote=$quotes blank=$([int]($textLine.Length -eq 0))"
            $state.ShapeLines++
        }
        if ($textLine -ceq 'BeginHeader') {
            $state.Begin++
            if ($state.Inside -or $state.End -ne 0) { $state.Invalid = 1 }
            $state.Inside = $true
            return
        }
        if ($textLine -ceq 'EndHeader') {
            $state.End++
            if (-not $state.Inside) { $state.Invalid = 1 }
            $state.Inside = $false
            return
        }
        # Lexical matches outside a header carry no column or event identity interpretation.
        $tokens = $textLine.Split([char[]]@(',', "`t", ':'))
        for ($index = 0; $index -lt $tokens.Count; $index++) {
            $token = $tokens[$index].Trim()
            $kind = 0
            $id = 0
            if ($events.TryGetValue($token, [ref]$id)) { $kind = 1 }
            elseif ($fields.TryGetValue($token, [ref]$id)) { $kind = 2 }
            if ($kind -ne 0) {
                $key = "$kind/$id"
                if (-not $state.Hits.ContainsKey($key)) { $state.Hits[$key] = @{ Kind = $kind; Id = $id; Count = 0L; FirstLine = $state.Lines; FirstToken = $index } }
                $state.Hits[$key].Count++
            }
        }
        if (-not $state.Inside -or $textLine.Length -eq 0) { return }
        if ($state.Rows.Count -ge 64 -or $quotes -ne 0 -or (($commas -gt 0) -eq ($tabs -gt 0))) { $state.Invalid = 1; return }
        $delimiter = ','
        $delimiterId = 1
        if ($tabs -gt 0) { $delimiter = "`t"; $delimiterId = 2 }
        $columns = $textLine.Split([char]$delimiter)
        if ($columns.Count -gt 128) { $state.Invalid = 1; return }
        $eventId = 0
        $null = $events.TryGetValue($columns[0].Trim(), [ref]$eventId)
        $ids = [int[]]::new($columns.Count - 1)
        $unknown = 0
        for ($index = 1; $index -lt $columns.Count; $index++) {
            $fieldId = 0
            if (-not $fields.TryGetValue($columns[$index].Trim(), [ref]$fieldId)) { $unknown++ }
            $ids[$index - 1] = $fieldId
        }
        $state.Rows.Add(@{ Line = $state.Lines; Event = $eventId; Fields = $ids; Unknown = $unknown; Delimiter = $delimiterId })
    }
    try {
        $stream = [IO.File]::Open($Report, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $totalBytes = $stream.Length
        $buffer = [byte[]]::new(8192)
        :scan while ($state.Bytes -lt $maxBytes -and $state.Lines -lt $maxLines) {
            if ($timer.Elapsed.TotalSeconds -ge 10) { $state.TimeLimit = 1; break }
            $count = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $maxBytes - $state.Bytes))
            if ($count -eq 0) { $complete = 1; break }
            for ($index = 0; $index -lt $count; $index++) {
                $value = $buffer[$index]
                $state.Bytes++
                if ($value -eq 0) { $state.Nul++ }
                if ($value -eq 10) {
                    & $consumeLine $line.ToString() $overlong
                    $null = $line.Clear()
                    $overlong = $false
                    if ($state.Lines -ge $maxLines) { break scan }
                }
                else {
                    if ($line.Length -lt $maxLineBytes) { $null = $line.Append([char]$value) }
                    else { $overlong = $true }
                }
            }
        }
        if ($state.Bytes -eq $totalBytes) { $complete = 1 }
        if ($complete -eq 1 -and ($line.Length -gt 0 -or $overlong)) { & $consumeLine $line.ToString() $overlong }
        if ($complete -eq 0 -and $state.Bytes -ge $maxBytes) { $state.ByteLimit = 1 }
        if ($complete -eq 0 -and $state.Lines -ge $maxLines) { $state.LineLimit = 1 }
    }
    catch { $state.Failed = 1; $state.HResult = $_.Exception.HResult }
    finally { if ($null -ne $stream) { $stream.Dispose() }; $timer.Stop() }
    $header = [int]($state.Begin -eq 1 -and $state.End -eq 1 -and -not $state.Inside -and $state.Invalid -eq 0 -and $state.Nul -eq 0 -and $state.Rows.Count -gt 0)
    if ($header -eq 1) {
        foreach ($row in $state.Rows) {
            Write-Host "NATIVE_XPERF_HEADER role=$Role line=$($row.Line) delimiter=$($row.Delimiter) eventId=$($row.Event) fields=$($row.Fields.Count) fieldIds=$($row.Fields -join ',') unknownFields=$($row.Unknown) decoded=0"
        }
    }
    foreach ($hit in @($state.Hits.Values | Sort-Object Kind, Id)) {
        $name = if ($hit.Kind -eq 1) { $eventNames[$hit.Id - 1] } else { $fieldNames[$hit.Id - 1] }
        Write-Host "NATIVE_XPERF_TOKEN role=$Role kind=$($hit.Kind) id=$($hit.Id) label=[$name] hits=$($hit.Count) firstLine=$($hit.FirstLine) firstToken=$($hit.FirstToken) lexicalOnly=1"
    }
    Write-Host "NATIVE_XPERF_REPORT role=$Role bytes=$totalBytes inspectedBytes=$($state.Bytes) lines=$($state.Lines) complete=$complete beginHeader=$($state.Begin) endHeader=$($state.End) headerShapeKnown=$header structureUnknown=$([int]($header -eq 0)) headerRows=$($state.Rows.Count) invalidHeader=$($state.Invalid) nulBytes=$($state.Nul) longLines=$($state.LongLines) byteLimit=$($state.ByteLimit) lineLimit=$($state.LineLimit) timeLimit=$($state.TimeLimit) failure=$($state.Failed) hresult=$($state.HResult) maxBytes=$maxBytes maxLines=$maxLines maxLineBytes=$maxLineBytes maxSeconds=10 decoded=0 targetProcessCausality=0"
    return ($state.Failed -eq 0)
}

function Invoke-PrivatePayloadXperf {
    $application = $null
    $hresult = 0
    try {
        $candidates = [Collections.Generic.List[string]]::new()
        $candidates.Add('xperf.exe')
        foreach ($base in @([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86), [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles))) {
            if (-not [string]::IsNullOrWhiteSpace($base)) { $candidates.Add((Join-Path $base 'Windows Kits\10\Windows Performance Toolkit\xperf.exe')) }
        }
        foreach ($candidate in $candidates) {
            $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $command) { $application = $command.Source; break }
        }
    }
    catch { $hresult = $_.Exception.HResult }
    Write-Host "NATIVE_XPERF_DISCOVERY found=$([int]($null -ne $application)) hresult=$hresult tracestatsRole=1 dumperRole=2 recordingFilterChanged=0 decoded=0"
    if ($null -eq $application) { return 40 }
    $status = 0
    foreach ($role in @(1, 2)) {
        $report = Join-Path $private ("xperf-$role.txt")
        $arguments = @('-i', $etl, '-o', $report, '-a')
        if ($role -eq 1) { $arguments += @('tracestats', '-detail') }
        else { $arguments += @('dumper', '-add_rawdata') }
        $result = Invoke-PrivatePayloadXperfChild $application $arguments $role
        Write-Host "NATIVE_XPERF_EXPORT role=$role success=$([int]$result.Success) settled=$([int]$result.Settled) exists=$([int][IO.File]::Exists($report)) decoded=0"
        if (-not $result.Settled) { $status = 41; break }
        if (-not $result.Success) { $status = 41 }
        if ([IO.File]::Exists($report) -and (Get-Item -LiteralPath $report).Length -gt 0) {
            if (-not (Measure-PrivatePayloadXperfReport $report $role) -and $status -eq 0) { $status = 43 }
        }
        elseif ($status -eq 0) { $status = 42 }
    }
    Write-Host "NATIVE_XPERF_COMPLETE status=$status exportsRequested=2 decoded=0 targetProcessCausality=0"
    return $status
}
