$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/exporter
# No symbols, GUI, installation, product invocation, or profile modification is requested.
function Invoke-PrivateWpa([string] $Executable, [string[]] $Arguments, [string] $Stage, [int] $DeadlineMs) {
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
        $process.StartInfo.WorkingDirectory = $private
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
            $log = [IO.File]::Open((Join-Path $private ($Stage + '-' + $name + '.txt')), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
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
                # A stuck inherited output handle is reported, never awaited without a deadline.
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
        foreach ($pipe in $pipes) {
            $pipe.Log.Dispose()
            $pipe.Stream.Dispose()
        }
        $process.Dispose()
    }
    Write-Host "WPA_EXPORT_NATIVE stage=$Stage started=$([int]$started) exitKnown=$([int]$exitKnown) exit=$code timedOut=$([int]$timedOut) terminated=$([int]$terminated) drained=$([int]$drained) outputLimited=$([int]$limited) outputBytes=$bytes hresult=$hresult"
    return @{ Success = $started -and $terminated -and $drained -and $exitKnown -and $code -eq 0 -and -not $timedOut -and -not $limited -and $hresult -eq 0; Bytes = $bytes }
}

function Read-PrivateWpaTables([string] $OutputDirectory) {
    # Label IDs are exact positions in this public-profile allowlist, not guessed CSV column positions.
    # https://github.com/microsoft/MSO-Scripts/blob/main/src/WPAP/CPU.wpaProfile
    $labels = @('New Process Name', 'New Process', 'New Prev Wait Reason', 'New Thread Id', 'Readying Process', 'Readying Thread Id', 'Waits', 'Count: Waits', 'Count', 'Ready', 'CPU Usage (in view)', 'Last Switch-Out Time', 'Switch-In Time', 'Process', 'Thread Id', 'Duration', 'Start Time', 'End Time', 'Process Name', 'CPU', 'Weight', '% Weight', 'TimeStamp', 'Stack', 'Module', 'Function')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $queue = [Collections.Generic.Queue[object]]::new()
    $queue.Enqueue(@{ Path = $OutputDirectory; Depth = 0 })
    $directories = 0
    $entries = 0
    $files = 0
    $csvFiles = 0
    $tables = 0
    $rows = 0
    $shapeTables = 0
    $bytes = 0L
    $outputBytes = 0L
    $reparseSkipped = 0
    $unknown = 0
    $truncated = $false
    Write-Host 'WPA_EXPORT_INSPECTION_LIMITS directories=16 depth=2 entries=256 files=64 prefixBytes=262144 totalBytes=4194304 rowsPerFile=128 totalRows=2048 columns=256 workerMs=15000'
    while ($queue.Count -gt 0) {
        if ($directories -ge 16 -or $clock.ElapsedMilliseconds -ge 15000) { $truncated = $true; break }
        $item = $queue.Dequeue()
        $enumerator = $null
        try {
            if (([IO.File]::GetAttributes($item.Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $reparseSkipped++; continue }
            $directories++
            $enumerator = [IO.Directory]::EnumerateFileSystemEntries($item.Path).GetEnumerator()
            while ($enumerator.MoveNext()) {
                if ($entries -ge 256 -or $files -ge 64 -or $bytes -ge 4194304 -or $rows -ge 2048 -or $clock.ElapsedMilliseconds -ge 15000) { $truncated = $true; break }
                $entries++
                $path = $enumerator.Current
                $attributes = [IO.File]::GetAttributes($path)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $reparseSkipped++; continue }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    if ($item.Depth -ge 2 -or $queue.Count + $directories -ge 16) { $truncated = $true }
                    else { $queue.Enqueue(@{ Path = $path; Depth = $item.Depth + 1 }) }
                    continue
                }
                $index = $files
                $files++
                $length = ([IO.FileInfo]::new($path)).Length
                $outputBytes += $length
                $isCsv = [string]::Equals([IO.Path]::GetExtension($path), '.csv', [StringComparison]::OrdinalIgnoreCase)
                Write-Host "WPA_EXPORT_FILE file=$index bytes=$length csv=$([int]$isCsv)"
                if (-not $isCsv) { $unknown++; continue }
                $csvFiles++
                $stream = $null
                $memory = $null
                $parser = $null
                $tableRows = 0
                $mismatches = 0
                $columns = 0
                $knownLabels = 0
                $prefixTruncated = $false
                $rowTruncated = $false
                $tailUnknown = $false
                $parsed = $false
                $hresult = 0
                try {
                    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                    $size = [int][Math]::Min($stream.Length, [Math]::Min(262144, 4194304 - $bytes))
                    $buffer = [byte[]]::new($size)
                    $offset = 0
                    while ($offset -lt $size) {
                        $count = $stream.Read($buffer, $offset, $size - $offset)
                        if ($count -eq 0) { throw [IO.EndOfStreamException]::new() }
                        $offset += $count
                        $bytes += $count
                    }
                    $prefixTruncated = $size -lt $stream.Length
                    $memory = [IO.MemoryStream]::new($buffer, $false)
                    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($memory, [Text.Encoding]::UTF8, $true)
                    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
                    $parser.SetDelimiters(',')
                    $parser.HasFieldsEnclosedInQuotes = $true
                    $parser.TrimWhiteSpace = $false
                    if ($parser.EndOfData) { throw [IO.InvalidDataException]::new('CSV has no header.') }
                    [string[]] $header = $parser.ReadFields()
                    $columns = $header.Length
                    if ($columns -lt 2 -or $columns -gt 256 -or ($prefixTruncated -and $parser.EndOfData)) { throw [IO.InvalidDataException]::new('CSV header is unknown.') }
                    $categories = [long[]]::new($columns * 4)
                    for ($column = 0; $column -lt $columns; $column++) {
                        $labelId = -1
                        for ($id = 0; $id -lt $labels.Count; $id++) {
                            if ([string]::Equals($header[$column], $labels[$id], [StringComparison]::OrdinalIgnoreCase)) { $labelId = $id; break }
                        }
                        if ($labelId -ge 0) { $knownLabels++ }
                        Write-Host "WPA_EXPORT_HEADER file=$index column=$column labelId=$labelId"
                    }
                    $tables++
                    while (-not $parser.EndOfData) {
                        if ($tableRows -ge 128 -or $rows -ge 2048 -or $clock.ElapsedMilliseconds -ge 15000) { $rowTruncated = $true; break }
                        try { [string[]] $fields = $parser.ReadFields() }
                        catch {
                            if ($prefixTruncated -and $parser.EndOfData) { $tailUnknown = $true; $rowTruncated = $true; break }
                            throw
                        }
                        # A prefix's last record may end midway through a field and is never classified.
                        if ($prefixTruncated -and $parser.EndOfData) { $rowTruncated = $true; break }
                        $rows++
                        $tableRows++
                        if ($fields.Length -ne $columns) { $mismatches++; continue }
                        for ($column = 0; $column -lt $columns; $column++) {
                            $value = $fields[$column]
                            $kind = 3
                            $number = 0.0
                            if ($value.Length -eq 0) { $kind = 0 }
                            elseif ([regex]::IsMatch($value, '\A[+-]?[0-9]+\z', [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromMilliseconds(50))) { $kind = 1 }
                            elseif ([double]::TryParse($value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and [double]::IsFinite($number)) { $kind = 2 }
                            $categories[$column * 4 + $kind]++
                        }
                    }
                    $parsed = $true
                    if ($tableRows -gt 0 -and $mismatches -eq 0) { $shapeTables++ }
                    for ($column = 0; $column -lt $columns; $column++) {
                        $base = $column * 4
                        Write-Host "WPA_EXPORT_FIELDS file=$index column=$column empty=$($categories[$base]) integerLexeme=$($categories[$base + 1]) invariantReal=$($categories[$base + 2]) otherText=$($categories[$base + 3]) unitsVerified=0"
                    }
                }
                catch {
                    $hresult = $_.Exception.HResult
                    $unknown++
                }
                finally {
                    if ($null -ne $parser) { $parser.Dispose() }
                    if ($null -ne $memory) { $memory.Dispose() }
                    if ($null -ne $stream) { $stream.Dispose() }
                }
                if ($prefixTruncated -or $rowTruncated) { $truncated = $true }
                Write-Host "WPA_EXPORT_TABLE file=$index parsed=$([int]$parsed) columns=$columns knownLabels=$knownLabels unknownLabels=$($columns - $knownLabels) sampledRows=$tableRows rowWidthMismatches=$mismatches prefixTruncated=$([int]$prefixTruncated) rowTruncated=$([int]$rowTruncated) tailUnknown=$([int]$tailUnknown) hresult=$hresult"
            }
        }
        catch { $unknown++; Write-Host "WPA_EXPORT_INSPECTION_UNKNOWN hresult=$($_.Exception.HResult)" }
        finally { if ($null -ne $enumerator) { $enumerator.Dispose() } }
        if ($entries -ge 256 -or $files -ge 64 -or $bytes -ge 4194304 -or $rows -ge 2048) { $truncated = $true; break }
    }
    # Shape observations do not validate event semantics, duration units, or any target's causal waits.
    Write-Host "WPA_EXPORT_SCHEMA directories=$directories entries=$entries files=$files csvFiles=$csvFiles tables=$tables shapeTables=$shapeTables sampledRows=$rows inspectedBytes=$bytes observedOutputBytes=$outputBytes truncated=$([int]$truncated) reparseSkipped=$reparseSkipped unknown=$unknown outputShapeVerified=$([int]($shapeTables -gt 0)) schemaVerified=0 unitsVerified=0 targetAttributionVerified=0"
    return $shapeTables -gt 0
}

$private = Join-Path (Get-Location).Path ('.artifacts/wpa-export-' + [guid]::NewGuid().ToString('N'))
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'capture.etl'
$wpr = $null
$owned = $false
$stopAttempted = $false
$created = $false
$helpVerified = $false
$profileSelected = $false
$captureVerified = $false
$exportAttempted = $false
$exportVerified = $false
$outputShapeVerified = $false
$phase = 0
$exitCode = 1
. (Join-Path $PSScriptRoot 'NativeWaitCapture.ps1')
try {
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    $selection = Join-Path $private 'selected.wpaProfile'
    $reports = Join-Path $private ('reports-' + [guid]::NewGuid().ToString('N'))
    $phase = 1
    $command = Get-Command 'WPAExporter.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    foreach ($base in @([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86), [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles))) {
        if ($null -eq $command -and -not [string]::IsNullOrWhiteSpace($base)) {
            $candidate = Join-Path $base 'Windows Kits\10\Windows Performance Toolkit\WPAExporter.exe'
            $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        }
    }
    Write-Host "WPA_EXPORT_PREREQUISITE phase=$phase available=$([int]($null -ne $command))"
    if ($null -eq $command) { throw 'Installed exporter is unavailable.' }
    $phase = 2
    $help = Invoke-PrivateWpa ([string]$command.Source) @('-h') 'help' 10000
    if ($help.Success -and $help.Bytes -gt 0) {
        $helpText = [IO.File]::ReadAllText((Join-Path $private 'help-out.txt')) + "`n" + [IO.File]::ReadAllText((Join-Path $private 'help-err.txt'))
        $helpVerified = $true
        foreach ($pattern in @('(?<!\w)-i\b', '(?<!\w)-profile\b', '(?<!\w)-outputfolder\b')) {
            if (-not [regex]::IsMatch($helpText, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) { $helpVerified = $false }
        }
        $helpText = $null
    }
    Write-Host "WPA_EXPORT_PREREQUISITE phase=$phase available=$([int]$helpVerified)"
    if (-not $helpVerified) { throw 'Documented exporter capabilities are unknown.' }
    $phase = 3
    & (Join-Path $PSScriptRoot 'Invoke-WpaInstalledProfileInventory.ps1') -SelectionCopyPath $selection
    $inventoryExit = $LASTEXITCODE
    if ($inventoryExit -eq 0 -and [IO.File]::Exists($selection)) {
        $length = ([IO.FileInfo]::new($selection)).Length
        if ($length -gt 0 -and $length -le 2097152) {
            $hash = (Get-FileHash -LiteralPath $selection -Algorithm SHA256).Hash
            $profileSelected = $hash -ceq '248989AFE19CC92AB856AF84D538B079FFE0597F92C7DBD1A61C1AF917E6B0E8'
        }
    }
    Write-Host "WPA_EXPORT_PREREQUISITE phase=$phase available=$([int]$profileSelected) inventoryExit=$inventoryExit"
    if (-not $profileSelected) { throw 'The exact inspected installed profile is unavailable.' }
    $phase = 4
    $start = Start-PrivateNativeWaitCapture
    Write-Host "WPA_EXPORT_CAPTURE started=$([int]$owned) status=$($start.Status) durationMs=4000 memoryMiB=128"
    if (-not $owned) { throw 'Owned capture was not started.' }
    Start-Sleep -Seconds 4
    $stopAttempted = $true
    $stop = Stop-PrivateNativeWaitCapture
    $phase = 5
    $etlBytes = if ([IO.File]::Exists($etl)) { ([IO.FileInfo]::new($etl)).Length } else { -1L }
    $captureVerified = $stop.Code -eq 0 -and $stop.KnownLoss -and $stop.Lost -eq 0 -and $stop.Dropped -eq 0 -and $etlBytes -gt 0 -and $etlBytes -le 268435456
    Write-Host "WPA_EXPORT_CAPTURE_RESULT stopExit=$($stop.Code) lossKnown=$([int]$stop.KnownLoss) lost=$($stop.Lost) dropped=$($stop.Dropped) etlBytes=$etlBytes accepted=$([int]$captureVerified)"
    if (-not $captureVerified) { throw 'Loss-free owned capture prerequisite is unknown or failed.' }
    $phase = 6
    $null = New-Item -ItemType Directory -Path $reports
    $exportAttempted = $true
    $export = Invoke-PrivateWpa ([string]$command.Source) @('-i', $etl, '-profile', $selection, '-outputfolder', $reports) 'export' 60000
    # Even a failed export can leave files, whose bounded shape is useful evidence rather than success.
    $phase = 7
    $outputShapeVerified = Read-PrivateWpaTables $reports
    $exportVerified = $export.Success -and $outputShapeVerified
    if (-not $exportVerified) { throw 'Real export or output shape was not verified.' }
    $exitCode = 0
}
catch { Write-Host "WPA_EXPORT_UNKNOWN phase=$phase hresult=$($_.Exception.HResult)" }
finally {
    if ($owned -and -not $stopAttempted) {
        $stopAttempted = $true
        $stop = Stop-PrivateNativeWaitCapture
        Write-Host "WPA_EXPORT_CAPTURE_RECOVERY stopExit=$($stop.Code)"
        $exitCode = 1
    }
    $removed = -1
    if ($created) {
        try {
            Remove-Item -LiteralPath $private -Recurse -Force
            $removed = [int](-not [IO.Directory]::Exists($private))
            if ($removed -ne 1) { $exitCode = 1 }
        }
        catch { $removed = 0; $exitCode = 1; Write-Host "WPA_EXPORT_CLEANUP_UNKNOWN hresult=$($_.Exception.HResult)" }
    }
    Write-Host "WPA_EXPORT_COMPLETE helpVerified=$([int]$helpVerified) profileSelected=$([int]$profileSelected) recordingStarted=$([int]$owned) captureVerified=$([int]$captureVerified) exportAttempted=$([int]$exportAttempted) exportVerified=$([int]$exportVerified) outputShapeVerified=$([int]$outputShapeVerified) schemaVerified=0 targetAttributionVerified=0 originalTestRun=0 buildRun=0 privateCreated=$([int]$created) privateRemoved=$removed exit=$exitCode"
}
exit $exitCode
