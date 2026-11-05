param([string] $SelectionCopyPath = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Microsoft documents a required profile, not a profileless WPAExporter mode.
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/exporter
# Microsoft documents shipped catalog profiles but does not publish their XML schema here.
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/view-profiles
# The documented installation directory is the Windows Kits 10 Windows Performance Toolkit directory.
# https://learn.microsoft.com/en-us/windows/apps/develop/performance/winui-perf
# Format and identifier observations below come from actual public Microsoft profiles, not an invented schema.
# https://github.com/microsoft/MSO-Scripts/blob/main/src/WPAP/CPU.wpaProfile
# https://github.com/microsoft/MSO-Scripts/blob/main/src/WPAP/FileIO.wpaProfile

$private = Join-Path (Get-Location).Path ('.artifacts/wpa-profiles-' + [guid]::NewGuid().ToString('N'))
$created = $false
$job = $null
$exitCode = 0
$completed = $false
$timedOut = $false
try {
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    $report = Join-Path $private 'inventory.txt'
    $job = Start-Job -ArgumentList $report, $SelectionCopyPath -ScriptBlock {
        param([string] $Report, [string] $SelectionCopyPath)
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        function Emit([string] $Line) { [IO.File]::AppendAllText($Report, $Line + "`n") }

        # The parent requests an owned-job stop after its 30-second wait, while the workflow is the outer deadline.
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $maxDepth = 4
        $maxDirectories = 128
        $maxEntries = 8192
        $maxFiles = 4096
        $maxCandidates = 64
        $maxDocumentBytes = 2097152
        $maxTotalBytes = 16777216
        $directories = 0
        $entries = 0
        $files = 0
        $candidates = 0
        $parsed = 0
        $recognized = 0
        $unknown = 0
        $truncated = $false
        $accessUnknown = 0
        $reparseSkipped = 0
        $absentRoots = 0
        $bytesRead = 0L
        $selectionCopied = $false
        $roots = [Collections.Generic.List[string]]::new()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $queue = [Collections.Generic.Queue[object]]::new()
        try {
            $command = Get-Command -Name 'WPAExporter.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            $programFiles = @(
                [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
                [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            )
            foreach ($base in $programFiles) {
                if (-not [string]::IsNullOrWhiteSpace($base)) {
                    $root = Join-Path $base 'Windows Kits\10\Windows Performance Toolkit'
                    if ($seen.Add([IO.Path]::GetFullPath($root))) { $roots.Add($root) }
                    if ($null -eq $command) {
                        $candidate = Join-Path $root 'WPAExporter.exe'
                        if ([IO.File]::Exists($candidate)) {
                            $command = Get-Command -Name $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
                        }
                    }
                }
            }
            if ($null -ne $command) {
                $sourceDirectory = [IO.Path]::GetDirectoryName([string] $command.Source)
                if ($seen.Add([IO.Path]::GetFullPath($sourceDirectory))) { $roots.Insert(0, $sourceDirectory) }
            }
            Emit "WPA_PROFILE_SELECTION selected=$([int]($null -ne $command)) roots=$($roots.Count)"
            Emit 'WPA_PROFILE_DOCUMENTATION requiredProfile=1 profilelessModeInCitedSources=0 profileXmlSchemaInCitedSources=0 citedSources=5'
            Emit "WPA_PROFILE_LIMITS depth=$maxDepth directories=$maxDirectories entries=$maxEntries files=$maxFiles candidates=$maxCandidates documentBytes=$maxDocumentBytes totalBytes=$maxTotalBytes workerMs=20000 waitBeforeStopSeconds=30 stopCallBounded=0 workflowTimeoutMinutes=4"
            foreach ($root in $roots) { $queue.Enqueue(@{ Path = $root; Depth = 0 }) }

            # Category kind 0 is a graph/table, kind 1 is a view title, and kind 2 is a column/property.
            # Numeric IDs are local allowlist positions and are never inferred from private names or XML text.
            # IDs 0 through 2 identify precise CPU context switches, sampled CPU usage, and file I/O graphs.
            # IDs 3 through 5 identify the public Wait Analysis, CPU Samples, and File I/O view titles.
            # IDs 6 through 8 identify new thread ID, readying thread ID, and previous wait reason properties.
            # IDs 9 through 11 identify file event type, file thread, and file duration properties.
            # Identity 1 means only the version-2 container shape observed in those public examples.
            # Neither recognized format identity nor matching categories establish exporter support or schema validity.
            $identifiers = @(
                @{ Kind = 0; Element = 'Graph'; Attribute = 'Guid'; Value = 'c58f5fea-0319-4046-932d-e695ebe20b47' },
                @{ Kind = 0; Element = 'Graph'; Attribute = 'Guid'; Value = 'b855361e-7be0-4bc8-a754-3e8507715ca5' },
                @{ Kind = 0; Element = 'Graph'; Attribute = 'Guid'; Value = 'e2a7d340-0dbd-4f24-8b26-e5388eeaf562' },
                @{ Kind = 1; Element = 'View'; Attribute = 'Title'; Value = 'Wait Analysis' },
                @{ Kind = 1; Element = 'View'; Attribute = 'Title'; Value = 'CPU Samples' },
                @{ Kind = 1; Element = 'View'; Attribute = 'Title'; Value = 'File I/O' },
                @{ Kind = 2; Element = 'Column'; Attribute = 'Guid'; Value = '944ed37a-5774-421e-b2d5-84f17a4b3a05' },
                @{ Kind = 2; Element = 'Column'; Attribute = 'Guid'; Value = '4ce38ba6-1665-4d3a-91cb-35b64f8c4280' },
                @{ Kind = 2; Element = 'Column'; Attribute = 'Guid'; Value = 'fe7ccd45-6275-413e-a538-3587f0eb452b' },
                @{ Kind = 2; Element = 'Column'; Attribute = 'Guid'; Value = 'db4da22c-8331-4243-9094-44f05969de01' },
                @{ Kind = 2; Element = 'Column'; Attribute = 'Guid'; Value = '71a12a2f-ac40-4906-a979-5760738e4ad7' },
                @{ Kind = 2; Element = 'Column'; Attribute = 'Guid'; Value = '0f0fd63e-942e-441d-829b-3f921965cc9a' }
            )
            $namespace = 'http://tempuri.org/SerializableElement.xsd'
            while ($queue.Count -gt 0) {
                if ($clock.ElapsedMilliseconds -ge 20000 -or $directories -ge $maxDirectories) { $truncated = $true; break }
                $item = $queue.Dequeue()
                $enumerator = $null
                try {
                    # Root ancestors are checked as well so a junction cannot redirect the permitted search.
                    $ancestor = [IO.Path]::GetFullPath($item.Path)
                    $redirected = $false
                    while ($null -ne $ancestor) {
                        $attributes = [IO.File]::GetAttributes($ancestor)
                        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $redirected = $true; break }
                        $parent = [IO.Directory]::GetParent($ancestor)
                        $ancestor = if ($null -eq $parent) { $null } else { $parent.FullName }
                    }
                    if ($redirected) { $reparseSkipped++; continue }
                    $directories++
                    $enumerator = [IO.Directory]::EnumerateFileSystemEntries($item.Path).GetEnumerator()
                    while ($enumerator.MoveNext()) {
                        if ($clock.ElapsedMilliseconds -ge 20000 -or $entries -ge $maxEntries -or $files -ge $maxFiles -or $candidates -ge $maxCandidates) { $truncated = $true; break }
                        $entries++
                        $path = $enumerator.Current
                        $attributes = [IO.File]::GetAttributes($path)
                        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $reparseSkipped++; continue }
                        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                            if ($item.Depth -ge $maxDepth -or $queue.Count + $directories -ge $maxDirectories) { $truncated = $true }
                            elseif ($seen.Add([IO.Path]::GetFullPath($path))) { $queue.Enqueue(@{ Path = $path; Depth = $item.Depth + 1 }) }
                            continue
                        }
                        $files++
                        if (-not [string]::Equals([IO.Path]::GetExtension($path), '.wpaProfile', [StringComparison]::OrdinalIgnoreCase)) { continue }
                        $index = $candidates
                        $candidates++
                        $stream = $null
                        $memory = $null
                        $reader = $null
                        $sha = $null
                        $parseKnown = $false
                        $identity = 0
                        $hresult = 0
                        $length = -1L
                        $fingerprint = '0'
                        try {
                            # FileShare.Read prevents another normal writer from changing the hashed and parsed document.
                            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                            $length = $stream.Length
                            if ($length -gt $maxDocumentBytes -or $bytesRead + $length -gt $maxTotalBytes) {
                                $truncated = $true
                                throw [IO.InvalidDataException]::new('Profile exceeds inventory byte limit.')
                            }
                            $buffer = [byte[]]::new([int] $length)
                            $offset = 0
                            while ($offset -lt $buffer.Length) {
                                $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
                                if ($read -eq 0) { throw [IO.EndOfStreamException]::new() }
                                $offset += $read
                                $bytesRead += $read
                            }
                            $sha = [Security.Cryptography.SHA256]::Create()
                            $fingerprint = [Convert]::ToHexString($sha.ComputeHash($buffer))
                            $settings = [Xml.XmlReaderSettings]::new()
                            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
                            $settings.XmlResolver = $null
                            $settings.MaxCharactersInDocument = $maxDocumentBytes
                            $settings.MaxCharactersFromEntities = 1024
                            $memory = [IO.MemoryStream]::new($buffer, $false)
                            $reader = [Xml.XmlReader]::Create($memory, $settings)
                            $document = [Xml.XmlDocument]::new()
                            $document.XmlResolver = $null
                            $document.Load($reader)
                            $parseKnown = $true
                            $parsed++
                            $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
                            $manager.AddNamespace('w', $namespace)
                            $content = $document.SelectSingleNode('/w:WpaProfileContainer[@Version="2"]/w:Content', $manager)
                            if ($null -ne $content -and $content.GetAttribute('type', 'http://www.w3.org/2001/XMLSchema-instance') -ceq 'WpaProfile2' -and $null -ne $content.SelectSingleNode('w:Views/w:View/w:Graphs/w:Graph/w:Preset', $manager)) {
                                $identity = 1
                                $recognized++
                                $categoryCounts = [int[]]::new($identifiers.Count)
                                for ($id = 0; $id -lt $identifiers.Count; $id++) {
                                    $allow = $identifiers[$id]
                                    $nodes = $content.SelectNodes("w:Views/w:View/descendant-or-self::w:$($allow.Element)", $manager)
                                    $count = 0
                                    foreach ($node in $nodes) {
                                        if ([string]::Equals($node.GetAttribute($allow.Attribute), $allow.Value, [StringComparison]::OrdinalIgnoreCase)) { $count++ }
                                    }
                                    $categoryCounts[$id] = $count
                                    Emit "WPA_PROFILE_CATEGORY candidate=$index kind=$($allow.Kind) id=$id count=$count"
                                }
                                $views = $content.SelectNodes('w:Views/w:View', $manager).Count
                                $graphs = $content.SelectNodes('w:Views/w:View/w:Graphs/w:Graph', $manager).Count
                                $presets = $content.SelectNodes('w:Views/w:View/w:Graphs/w:Graph/w:Preset', $manager).Count
                                $references = $content.SelectNodes('w:Sessions/w:Session/w:FileReferences/w:FileReference', $manager).Count
                                Emit "WPA_PROFILE_STRUCTURE candidate=$index views=$views graphs=$graphs presets=$presets references=$references referencesResolved=0"
                                # Only the exact observed installed profile can leave this inventory as a private byte-for-byte copy.
                                $allReferences = $document.SelectNodes('//*[local-name()="FileReference"]').Count
                                $precise = $content.SelectNodes('w:Views/w:View/w:Graphs/w:Graph[translate(@Guid,"ABCDEF","abcdef")="c58f5fea-0319-4046-932d-e695ebe20b47"]', $manager)
                                $waitProperties = 0
                                foreach ($id in @(6, 7, 8)) {
                                    $present = $false
                                    foreach ($graph in $precise) {
                                        foreach ($column in $graph.SelectNodes('.//w:Column', $manager)) {
                                            if ([string]::Equals($column.GetAttribute('Guid'), $identifiers[$id].Value, [StringComparison]::OrdinalIgnoreCase)) { $present = $true }
                                        }
                                    }
                                    if ($present) { $waitProperties++ }
                                }
                                $cpuPresets = @($precise | ForEach-Object { $_.SelectNodes('w:Preset', $manager) })
                                Emit "WPA_PROFILE_CPU_PRESETS candidate=$index count=$($cpuPresets.Count) fileReferences=$allReferences"
                                $identityGuids = @('b065487c-5e32-4f1f-a2cd-581e086ce29e', '944ed37a-5774-421e-b2d5-84f17a4b3a05', 'fe7ccd45-6275-413e-a538-3587f0eb452b')
                                $identityLabels = @('New Process', 'New Thread Id', 'New Prev Wait Reason')
                                for ($presetIndex = 0; $presetIndex -lt $cpuPresets.Count; $presetIndex++) {
                                    $cpuColumns = $cpuPresets[$presetIndex].SelectNodes('w:Columns/w:Column', $manager)
                                    $declaredKeys = 0U
                                    $keyCountKnown = [uint32]::TryParse($cpuPresets[$presetIndex].GetAttribute('KeyColumnCount'), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$declaredKeys) -and $declaredKeys -gt 0 -and $declaredKeys -le $cpuColumns.Count
                                    Emit "WPA_PROFILE_CPU_KEYS candidate=$index preset=$presetIndex columns=$($cpuColumns.Count) declaredKeys=$declaredKeys keyCountKnown=$([int]$keyCountKnown)"
                                    for ($identity = 0; $identity -lt $identityGuids.Count; $identity++) {
                                        $occurrences = 0
                                        $withinKeys = 0
                                        $labelExact = 0
                                        for ($columnIndex = 0; $columnIndex -lt $cpuColumns.Count; $columnIndex++) {
                                            if ([string]::Equals($cpuColumns[$columnIndex].GetAttribute('Guid'), $identityGuids[$identity], [StringComparison]::OrdinalIgnoreCase)) {
                                                $occurrences++
                                                if ($keyCountKnown -and $columnIndex -lt $declaredKeys) { $withinKeys++ }
                                                if ([string]::Equals($cpuColumns[$columnIndex].GetAttribute('Name'), $identityLabels[$identity], [StringComparison]::Ordinal)) { $labelExact++ }
                                            }
                                        }
                                        Emit "WPA_PROFILE_CPU_IDENTITY candidate=$index preset=$presetIndex identity=$identity guidOccurrences=$occurrences withinKeys=$withinKeys labelExact=$labelExact"
                                    }
                                }
                                if (-not $selectionCopied -and -not [string]::IsNullOrWhiteSpace($SelectionCopyPath) -and
                                    $fingerprint -ceq '248989AFE19CC92AB856AF84D538B079FFE0597F92C7DBD1A61C1AF917E6B0E8' -and
                                    $categoryCounts[0] -gt 0 -and $waitProperties -eq 3 -and $allReferences -eq 0) {
                                    # The caller owns this existing directory and is responsible for its final removal.
                                    $destination = [IO.Path]::GetFullPath($SelectionCopyPath)
                                    $ancestor = [IO.Path]::GetDirectoryName($destination)
                                    while ($null -ne $ancestor) {
                                        if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                                            throw [IO.InvalidDataException]::new('Selection directory is redirected.')
                                        }
                                        $parent = [IO.Directory]::GetParent($ancestor)
                                        $ancestor = if ($null -eq $parent) { $null } else { $parent.FullName }
                                    }
                                    $copy = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                                    try { $copy.Write($buffer, 0, $buffer.Length) }
                                    finally { $copy.Dispose() }
                                    $selectionCopied = $true
                                    Emit "WPA_PROFILE_COPY selected=1 candidate=$index bytes=$length sha256=$fingerprint formatIdentity=$identity preciseCpu=$($categoryCounts[0]) waitProperties=$waitProperties fileReferences=$allReferences exactBytes=1"
                                }
                            }
                        }
                        catch { $hresult = $_.Exception.HResult }
                        finally {
                            if ($null -ne $reader) { $reader.Dispose() }
                            if ($null -ne $memory) { $memory.Dispose() }
                            if ($null -ne $stream) { $stream.Dispose() }
                            if ($null -ne $sha) { $sha.Dispose() }
                        }
                        if (-not $parseKnown -or $identity -eq 0) { $unknown++ }
                        Emit "WPA_PROFILE_DOCUMENT candidate=$index bytes=$length sha256=$fingerprint xmlParsed=$([int]$parseKnown) formatIdentity=$identity supportVerified=0 schemaVerified=0 hresult=$hresult"
                    }
                }
                catch {
                    $failure = $_.Exception
                    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
                    if ($item.Depth -eq 0 -and ($failure -is [IO.DirectoryNotFoundException] -or $failure -is [IO.FileNotFoundException])) { $absentRoots++ }
                    else {
                        $accessUnknown++
                        Emit "WPA_PROFILE_ACCESS_UNKNOWN hresult=$($failure.HResult)"
                    }
                }
                finally { if ($null -ne $enumerator) { $enumerator.Dispose() } }
                if ($entries -ge $maxEntries -or $files -ge $maxFiles -or $candidates -ge $maxCandidates) { $truncated = $true; break }
            }
            # Absence is scoped to accessible permitted roots and is not inferred after truncation or access failures.
            $noneFound = [int]($candidates -eq 0 -and -not $truncated -and $accessUnknown -eq 0 -and $reparseSkipped -eq 0)
            Emit "WPA_PROFILE_SUMMARY directories=$directories entries=$entries files=$files candidates=$candidates xmlParsed=$parsed recognizedFormat=$recognized parseOrFormatUnknown=$unknown traversalTruncated=$([int]$truncated) accessUnknown=$accessUnknown absentRoots=$absentRoots reparseSkipped=$reparseSkipped bytesRead=$bytesRead noTemplateFound=$noneFound usableProfile=-1 supportVerified=0 exportVerified=0 schemaVerified=0 targetAttributionVerified=0"
            Emit "WPA_PROFILE_COPY_RESULT requested=$([int](-not [string]::IsNullOrWhiteSpace($SelectionCopyPath))) selected=$([int]$selectionCopied) supportVerified=0 exportVerified=0 schemaVerified=0"
        }
        catch { Emit "WPA_PROFILE_FAILURE hresult=$($_.Exception.HResult)"; throw 'Installed profile inventory failed.' }
    }
    $null = Wait-Job -Job $job -Timeout 30
    $completed = $job.State -eq 'Completed'
    $timedOut = $job.State -notin @('Completed', 'Failed', 'Stopped')
    if (-not $completed) { $exitCode = 1 }
    if ($timedOut) { Stop-Job -Job $job }
    if ([IO.File]::Exists($report)) {
        if ((Get-Item -LiteralPath $report).Length -gt 262144) { throw 'Inventory report exceeds its fixed limit.' }
        foreach ($line in [IO.File]::ReadAllLines($report)) {
            if ($line -notmatch '^WPA_PROFILE_[A-Z_]+(?: [A-Za-z][A-Za-z0-9]*=(?:-?[0-9]+|[A-F0-9]{64}))*$') { throw 'Inventory report failed the output allowlist.' }
            Write-Host $line
        }
    }
}
catch {
    $exitCode = 1
    Write-Host "WPA_PROFILE_CONTROLLER_FAILURE hresult=$($_.Exception.HResult)"
}
finally {
    if ($null -ne $job) {
        try {
            if ($job.State -notin @('Completed', 'Failed', 'Stopped')) { Stop-Job -Job $job }
            Remove-Job -Job $job -Force
        }
        catch { $exitCode = 1; Write-Host "WPA_PROFILE_JOB_CLEANUP_FAILURE hresult=$($_.Exception.HResult)" }
    }
    $removed = -1
    if ($created) {
        try {
            Remove-Item -LiteralPath $private -Recurse -Force
            $removed = [int](-not [IO.Directory]::Exists($private))
            if ($removed -ne 1) { $exitCode = 1 }
        }
        catch { $removed = 0; $exitCode = 1; Write-Host "WPA_PROFILE_CLEANUP_FAILURE hresult=$($_.Exception.HResult)" }
    }
    Write-Host "WPA_PROFILE_COMPLETE complete=$([int]$completed) timedOut=$([int]$timedOut) privateCreated=$([int]$created) privateRemoved=$removed recordingStarted=0 exportAttempted=0 exportVerified=0 schemaVerified=0 targetAttributionVerified=0 originalTestRun=0 buildRun=0 exit=$exitCode"
}
exit $exitCode
