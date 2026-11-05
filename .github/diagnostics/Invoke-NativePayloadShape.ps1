$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'NativeWaitCapture.ps1')
. (Join-Path $PSScriptRoot 'NativePayloadXperf.ps1')

# Get-WinEvent payloads are described only by fixed numeric categories.
function Get-NativePayloadCategory($Value) {
    if ($null -eq $Value) { return 1 }
    if ($Value -is [byte]) { return 2 }
    if ($Value -is [sbyte]) { return 4 }
    if ($Value -is [uint16]) { return 8 }
    if ($Value -is [int16]) { return 16 }
    if ($Value -is [uint32]) { return 32 }
    if ($Value -is [int32]) { return 64 }
    if ($Value -is [uint64]) { return 128 }
    if ($Value -is [int64]) { return 256 }
    if ($Value -is [single]) { return 512 }
    if ($Value -is [double]) { return 1024 }
    if ($Value -is [bool]) { return 2048 }
    if ($Value -is [char]) { return 4096 }
    if ($Value -is [guid]) { return 8192 }
    if ($Value -is [datetime]) { return 16384 }
    if ($Value -is [string]) { return 32768 }
    if ($Value -is [byte[]]) { return 65536 }
    if ($Value -is [intptr]) { return 262144 }
    if ($Value -is [uintptr]) { return 524288 }
    return 131072
}

function Measure-NativePayloadShapes {
    $eventLimit = 100000
    $propertyLimit = 64
    $shapeLimit = 32
    $binaryLengthLimit = 1048576
    $decodeSeconds = 30
    $shapes = @{}
    # The event and time limits bound a sample rather than establish full trace retention.
    # A negative binary length means no binary value within the length bound was observed.
    $statistics = @{ Events = 0L; Recognized = 0L; Other = 0L; IdentityUnknown = 0L;
        UnsupportedShape = 0L; UnknownTypeEvents = 0L; UnknownTypeProperties = 0L;
        ShapeOverflow = 0L; BinaryOversize = 0L; EventLimit = 0; TimeLimit = 0; DecodeFailed = 0 }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "NATIVE_SHAPE_BOUNDS maxEvents=$eventLimit maxProperties=$propertyLimit maxShapes=$shapeLimit maxBinaryLength=$binaryLengthLimit decodeSeconds=$decodeSeconds requestedBufferBytes=134217728 fullRetentionClaim=0"
    Write-Host 'NATIVE_SHAPE_ROLES switch=1 directory=2 fileEnd=3 diskInit=4 diskEnd=5 header=6'
    Write-Host 'NATIVE_SHAPE_MASK null=1 u8=2 i8=4 u16=8 i16=16 u32=32 i32=64 u64=128 i64=256 f32=512 f64=1024 boolean=2048 character=4096 guid=8192 datetime=16384 string=32768 binary=65536 unknown=131072 intptr=262144 uintptr=524288'
    try {
        Get-WinEvent -Path $etl -Oldest -MaxEvents ($eventLimit + 1) -ErrorAction Stop | ForEach-Object {
            $event = $_
            try {
                if ($statistics.Events -ge $eventLimit) { $statistics.EventLimit = 1; return }
                if ($timer.Elapsed.TotalSeconds -ge $decodeSeconds) {
                    $statistics.TimeLimit = 1
                    throw 'Decode time bound reached.'
                }
                $statistics.Events++
                $role = 0
                $provider = $event.ProviderId
                $opcode = $event.Opcode
                if ($null -eq $provider -or $null -eq $opcode) { $statistics.IdentityUnknown++; return }
                if ($provider -eq [guid]'3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c' -and $opcode -eq 36) { $role = 1 }
                elseif ($provider -eq [guid]'90cbdc39-4a3e-11d1-84f4-0000f80464e3') {
                    if ($opcode -eq 72) { $role = 2 }
                    elseif ($opcode -eq 76) { $role = 3 }
                }
                elseif ($provider -eq [guid]'3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c') {
                    if ($opcode -in @(12, 13)) { $role = 4 }
                    elseif ($opcode -in @(10, 11)) { $role = 5 }
                }
                elseif ($provider -eq [guid]'68fdd900-4a3e-11d1-84f4-0000f80464e3' -and $opcode -eq 0) { $role = 6 }
                if ($role -eq 0) { $statistics.Other++; return }
                $statistics.Recognized++
                $version = $event.Version
                $properties = $event.Properties
                if ($null -eq $version -or $version -lt 0 -or $version -gt 255 -or
                    $null -eq $properties -or $properties.Count -gt $propertyLimit) {
                    $statistics.UnsupportedShape++
                    return
                }
                $count = $properties.Count
                $mask = 0
                $slotMasks = [int[]]::new($count)
                $binaryMin = -1
                $binaryMax = -1
                $binaryOversize = 0
                $unknownProperties = 0
                for ($index = 0; $index -lt $count; $index++) {
                    $value = $properties[$index].Value
                    $category = Get-NativePayloadCategory $value
                    $slotMasks[$index] = $category
                    $mask = $mask -bor $category
                    if ($category -eq 131072) { $unknownProperties++ }
                    if ($category -eq 65536) {
                        $length = $value.Length
                        if ($length -gt $binaryLengthLimit) { $binaryOversize++; continue }
                        if ($binaryMin -lt 0 -or $length -lt $binaryMin) { $binaryMin = $length }
                        if ($length -gt $binaryMax) { $binaryMax = $length }
                    }
                }
                $statistics.BinaryOversize += $binaryOversize
                if ($unknownProperties -ne 0) {
                    $statistics.UnknownTypeEvents++
                    $statistics.UnknownTypeProperties += $unknownProperties
                }
                $key = '{0}:{1}:{2}:{3}:{4}' -f $role, $opcode, $version, $count, $mask
                if (-not $shapes.ContainsKey($key)) {
                    if ($shapes.Count -ge $shapeLimit) { $statistics.ShapeOverflow++; return }
                    $shapes[$key] = @{ Role = $role; Opcode = [int]$opcode; Version = [int]$version;
                        Properties = $count; Mask = $mask; Events = 0L; Slots = [int[]]::new($count);
                        BinaryMin = -1; BinaryMax = -1; BinaryOversize = 0L; UnknownProperties = 0L }
                }
                $shape = $shapes[$key]
                $shape.Events++
                $shape.UnknownProperties += $unknownProperties
                $shape.BinaryOversize += $binaryOversize
                if ($binaryMin -ge 0 -and ($shape.BinaryMin -lt 0 -or $binaryMin -lt $shape.BinaryMin)) { $shape.BinaryMin = $binaryMin }
                if ($binaryMax -gt $shape.BinaryMax) { $shape.BinaryMax = $binaryMax }
                for ($index = 0; $index -lt $count; $index++) { $shape.Slots[$index] = $shape.Slots[$index] -bor $slotMasks[$index] }
            }
            finally { $event.Dispose() }
        }
    }
    catch {
        if ($statistics.TimeLimit -eq 0) { $statistics.DecodeFailed = 1 }
    }
    finally { $timer.Stop() }
    $shapeIndex = 0
    foreach ($shape in @($shapes.Values | Sort-Object Role, Opcode, Version, Properties, Mask)) {
        Write-Host "NATIVE_SHAPE_SAMPLE shape=$shapeIndex role=$($shape.Role) opcode=$($shape.Opcode) version=$($shape.Version) properties=$($shape.Properties) mask=$($shape.Mask) events=$($shape.Events) binaryMin=$($shape.BinaryMin) binaryMax=$($shape.BinaryMax) binaryOversize=$($shape.BinaryOversize) unknownProperties=$($shape.UnknownProperties)"
        for ($index = 0; $index -lt $shape.Properties; $index++) {
            Write-Host "NATIVE_SHAPE_SLOT shape=$shapeIndex index=$index mask=$($shape.Slots[$index])"
        }
        $shapeIndex++
    }
    Write-Host "NATIVE_SHAPE_SCOPE events=$($statistics.Events) recognized=$($statistics.Recognized) other=$($statistics.Other) identityUnknown=$($statistics.IdentityUnknown) unsupportedShape=$($statistics.UnsupportedShape) unknownTypeEvents=$($statistics.UnknownTypeEvents) unknownTypeProperties=$($statistics.UnknownTypeProperties) shapes=$($shapes.Count) shapeOverflow=$($statistics.ShapeOverflow) binaryOversize=$($statistics.BinaryOversize) eventLimit=$($statistics.EventLimit) timeLimit=$($statistics.TimeLimit) decoderFailed=$($statistics.DecodeFailed)"
    if ($statistics.DecodeFailed -ne 0) { return 31 }
    if ($shapes.Count -eq 0) { return 32 }
    return 0
}

$root = (Get-Location).Path
$private = Join-Path $root ('.artifacts/native-shape-' + [guid]::NewGuid().ToString('N'))
$profilePath = Join-Path $PSScriptRoot 'native-wait-recording.wprp'
$profile = $profilePath + '!NativeWait.Verbose'
$etl = Join-Path $private 'native-shape.etl'
$owned = $false
$wpr = $null
$measurement = 10
$stopExit = 125
$cleanup = 0
$created = $false
Write-Host 'NATIVE_SHAPE_CONTEXT originalTestRun=0 buildRun=0 targetProcessCausality=0 systemEventsOnly=1 captureSeconds=4 fullRetentionClaim=0'
try {
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    $capture = Start-PrivateNativeWaitCapture
    $owned = $capture.Owned
    $measurement = $capture.Status
    Write-Host "NATIVE_SHAPE_CAPTURE owned=$([int]$owned) requestedBufferBytes=134217728 status=$measurement"
    if ($owned) { Start-Sleep -Seconds 4 }
}
catch { $measurement = 16 }
finally {
    try {
        if ($owned) {
            $stopped = Stop-PrivateNativeWaitCapture
            $stopExit = $stopped.Code
            if ($stopExit -ne 0 -or -not [IO.File]::Exists($etl)) { $measurement = 20 }
            else {
                Write-Host "NATIVE_SHAPE_TRACE bytes=$((Get-Item -LiteralPath $etl).Length) fullRetentionClaim=0"
                $measurement = Measure-NativePayloadShapes
                try {
                    $xperfMeasurement = Invoke-PrivatePayloadXperf
                    if ($measurement -eq 0 -and $xperfMeasurement -ne 0) { $measurement = $xperfMeasurement }
                }
                catch {
                    if ($measurement -eq 0) { $measurement = 44 }
                    Write-Host "NATIVE_XPERF_FAILURE hresult=$($_.Exception.HResult) decoded=0 targetProcessCausality=0"
                }
                if (-not $stopped.KnownLoss) { $measurement = 21 }
                elseif ($stopped.Lost -ne 0 -or $stopped.Dropped -ne 0) { $measurement = 22 }
            }
        }
    }
    catch { $measurement = 23 }
    finally {
        if ($created) {
            try { Remove-Item -LiteralPath $private -Recurse -Force; $cleanup = 1 }
            catch { $measurement = 24 }
        }
        Write-Host "NATIVE_SHAPE_RESULT status=$measurement stopExit=$stopExit cleanup=$cleanup originalTestRun=0 buildRun=0 targetProcessCausality=0 fullRetentionClaim=0"
    }
}
exit $measurement
