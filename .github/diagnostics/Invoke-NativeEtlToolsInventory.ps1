$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# Roles 0 and 1 identify xperf and WPAExporter without publishing executable locations.
# Operation and capability IDs follow the fixed query and pattern order below.
# Only Microsoft-documented help commands are executed, never processing or recording commands.
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/help
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/processing
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/dumper
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/tracestats
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cswitch
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/diskio
# https://learn.microsoft.com/en-us/windows-hardware/test/wpt/exporter
$tools = @(
    @{ Role = 0; Name = 'xperf.exe'; Queries = @(
        @{ Arguments = @('-help'); Patterns = @('\bprocessing\b') },
        @{ Arguments = @('-help', 'processing'); Patterns = @('(?<!\w)-i\b', '(?<!\w)-o\b', '(?<!\w)-a\b', '(?<!\w)-target\b') },
        @{ Arguments = @('-help', 'dumper'); Patterns = @('\bdumper\b', '(?<!\w)-provider\b', '(?<!\w)-add_rawdata\b') },
        @{ Arguments = @('-help', 'tracestats'); Patterns = @('\btracestats\b', '(?<!\w)-detail\b', '(?<!\w)-timespan\b') },
        @{ Arguments = @('-help', 'cswitch'); Patterns = @('\bcswitch\b', '(?<!\w)-thread\b', '(?<!\w)-process\b') },
        @{ Arguments = @('-help', 'diskio'); Patterns = @('\bdiskio\b', '(?<!\w)-detail\b', '(?<!\w)-summary\b') }
    ) },
    @{ Role = 1; Name = 'WPAExporter.exe'; Queries = @(
        @{ Arguments = @('-h'); Patterns = @('(?<!\w)-profile\b', '(?<!\w)-outputfolder\b', '(?<!\w)-delimiter\b') }
    ) }
)

function Invoke-HelpInventory($Command, $Query, [int] $Role, [int] $Operation, [string] $PrivateDirectory) {
    $process = $null
    $exitCode = -1
    $exitKnown = $false
    $started = $false
    $timedOut = $false
    $terminated = $true
    $drained = $false
    $inspected = $false
    $hresult = 0
    $bytes = -1L
    $text = ''
    try {
        if ($null -ne $Command) {
            $stdout = Join-Path $PrivateDirectory ("$Role-$Operation-out.txt")
            $stderr = Join-Path $PrivateDirectory ("$Role-$Operation-err.txt")
            # Select one CommandInfo.Source rather than passing an array of application paths.
            [string] $source = $Command.Source
            $process = Start-Process -FilePath $source -ArgumentList $Query.Arguments -WorkingDirectory $PrivateDirectory -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            $started = $true
            $terminated = $false
            if ($process.WaitForExit(10000)) {
                $drained = $process.WaitForExitAsync().Wait(1000)
                $terminated = $true
                $exitCode = $process.ExitCode
                $exitKnown = $true
                $bytes = (Get-Item -LiteralPath $stdout).Length + (Get-Item -LiteralPath $stderr).Length
                if ($drained -and $exitCode -eq 0 -and $bytes -gt 0 -and $bytes -le 1048576) {
                    $text = [IO.File]::ReadAllText($stdout) + "`n" + [IO.File]::ReadAllText($stderr)
                    $inspected = $true
                }
            }
            else {
                $timedOut = $true
            }
        }
    }
    catch {
        $hresult = $_.Exception.HResult
    }
    finally {
        if ($null -ne $process) {
            try {
                if (-not $terminated) {
                    if (-not $process.HasExited) { $process.Kill($true) }
                    $terminated = $process.WaitForExit(5000)
                }
            }
            catch { $hresult = $_.Exception.HResult }
            finally { $process.Dispose() }
        }
    }
    Write-Host "NATIVE_ETL_HELP role=$Role operation=$Operation selected=$([int]($null -ne $Command)) started=$([int]$started) exitKnown=$([int]$exitKnown) exit=$exitCode timedOut=$([int]$timedOut) terminated=$([int]$terminated) drained=$([int]$drained) bytes=$bytes inspected=$([int]$inspected) hresult=$hresult"
    $confirmed = $inspected -and $terminated
    for ($capability = 0; $capability -lt $Query.Patterns.Count; $capability++) {
        $present = -1
        if ($inspected) { $present = [int][regex]::IsMatch($text, $Query.Patterns[$capability], [Text.RegularExpressions.RegexOptions]::IgnoreCase) }
        Write-Host "NATIVE_ETL_CAPABILITY role=$Role operation=$Operation capability=$capability present=$present"
        if ($present -ne 1) { $confirmed = $false }
    }
    if (-not $terminated) { throw 'Owned help process termination was not observed.' }
    return $confirmed
}

$private = Join-Path (Get-Location).Path ('.artifacts/native-etl-tools-' + [guid]::NewGuid().ToString('N'))
$inventoryExit = 0
$created = $false
$complete = $false
try {
    $null = New-Item -ItemType Directory -Path $private
    $created = $true
    $roots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    )
    foreach ($tool in $tools) {
        $role = $tool.Role
        $matches = @(Get-Command -Name $tool.Name -CommandType Application -ErrorAction SilentlyContinue)
        $command = $matches | Select-Object -First 1
        $selection = if ($null -ne $command) { 0 } else { -1 }
        Write-Host "NATIVE_ETL_DISCOVERY role=$role pathMatches=$($matches.Count) onPath=$([int]($null -ne $command))"
        for ($candidate = 0; $candidate -lt $roots.Count; $candidate++) {
            $exists = $false
            $resolved = $false
            if (-not [string]::IsNullOrWhiteSpace($roots[$candidate])) {
                $path = Join-Path $roots[$candidate] ('Windows Kits\10\Windows Performance Toolkit\' + $tool.Name)
                $exists = [IO.File]::Exists($path)
                if ($exists) {
                    $installed = Get-Command -Name $path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
                    $resolved = $null -ne $installed
                    if ($null -eq $command -and $resolved) {
                        $command = $installed
                        $selection = $candidate + 1
                    }
                }
            }
            Write-Host "NATIVE_ETL_CANDIDATE role=$role candidate=$candidate exists=$([int]$exists) resolved=$([int]$resolved)"
        }
        Write-Host "NATIVE_ETL_SELECTION role=$role selected=$([int]($null -ne $command)) sourceRole=$selection"
        $confirmed = 0
        for ($operation = 0; $operation -lt $tool.Queries.Count; $operation++) {
            if (Invoke-HelpInventory $command $tool.Queries[$operation] $role $operation $private) { $confirmed++ }
            else { $inventoryExit = 1 }
        }
        Write-Host "NATIVE_ETL_ROLE role=$role queries=$($tool.Queries.Count) helpConfirmed=$confirmed exportAttempted=0 exportVerified=0 schemaVerified=0 profileSupplied=0"
    }
    $complete = $true
}
catch {
    $inventoryExit = 1
    Write-Host "NATIVE_ETL_FAILURE hresult=$($_.Exception.HResult)"
}
finally {
    $removed = -1
    if ($created) {
        try {
            Remove-Item -LiteralPath $private -Recurse -Force
            $removed = [int](-not [IO.Directory]::Exists($private))
            if ($removed -ne 1) { $inventoryExit = 1 }
        }
        catch {
            $removed = 0
            $inventoryExit = 1
            Write-Host "NATIVE_ETL_CLEANUP_FAILURE hresult=$($_.Exception.HResult)"
        }
    }
    Write-Host "NATIVE_ETL_COMPLETE complete=$([int]$complete) privateCreated=$([int]$created) privateRemoved=$removed recordingStarted=0 exportAttempted=0 originalTestRun=0 buildRun=0 exit=$inventoryExit"
}
exit $inventoryExit
