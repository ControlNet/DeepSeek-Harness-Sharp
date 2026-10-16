<#
Prepare the same Windows x64 SQLite runtime for the test host and the real CLI.
The 3.54.0 archive is published at https://www.sqlite.org/download.html.
Its published SHA3-256 is f5fb500c73a3afd89976386bb8e4fdc8b5b65c052e187fb1ada1f5de034cd45b;
the SHA-256 below was measured only after verifying that published checksum.
#>
[CmdletBinding()]
param(
    [string[]]$Destination = @(
        'tests/Dsh.Tests/bin/Debug/net10.0',
        'DeepSeek-Harness-Sharp/bin/Debug/net10.0'
    )
)

$ErrorActionPreference = 'Stop'
$RootDir = Split-Path -Parent $PSScriptRoot
$WorkDir = Join-Path $RootDir 'artifacts020/sqlite-runtime'
$Archive = Join-Path $WorkDir 'sqlite-dll-win-x64-3540000.zip'
$Extracted = Join-Path $WorkDir 'sqlite-dll-win-x64-3540000'
New-Item -ItemType Directory -Force $WorkDir | Out-Null
Invoke-WebRequest 'https://www.sqlite.org/2026/sqlite-dll-win-x64-3540000.zip' -OutFile $Archive
if ((Get-FileHash $Archive -Algorithm SHA256).Hash -ne 'a548a9b5d8f695e669f71bcc4669fdc8b3e1eb79995761c81a9128262126f71c') {
    throw 'Official SQLite 3.54.0 x64 archive checksum mismatch'
}
Expand-Archive $Archive -DestinationPath $Extracted -Force
$Dll = Join-Path $Extracted 'sqlite3.dll'
foreach ($Target in $Destination) {
    $Directory = Join-Path $RootDir $Target
    New-Item -ItemType Directory -Force $Directory | Out-Null
    Copy-Item $Dll $Directory -Force
    Write-Host "SQLite 3.54.0 x64 installed in $Target"
}
