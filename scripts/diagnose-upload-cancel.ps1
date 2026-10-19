$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($env:GITHUB_REPOSITORY -ne 'ControlNet/DeepSeek-Harness-Sharp' -or $env:GITHUB_REF -ne 'refs/heads/diag/flaky-net77-upload') { throw 'Unexpected diagnostic repository or branch' }
if ($root -ne (Resolve-Path $env:GITHUB_WORKSPACE).Path) { throw 'Unexpected diagnostic checkout' }
$owned = Join-Path $root '.qa/net77-win'
$runTmp = Join-Path $owned "run-$([Guid]::NewGuid().ToString('N'))"
$evidence = Join-Path $owned 'evidence'
$cache = Join-Path $owned 'cache'
$rounds = 20
$failed = 0
New-Item -ItemType Directory -Path $runTmp, $evidence, $cache | Out-Null
foreach ($directory in @('home', 'tmp', 'config', 'data', 'state', 'runtime', 'dotnet-home')) { New-Item -ItemType Directory -Path (Join-Path $runTmp $directory) | Out-Null }
$env:HOME = Join-Path $runTmp 'home'
$env:USERPROFILE = $env:HOME
$env:TEMP = Join-Path $runTmp 'tmp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
$env:APPDATA = Join-Path $runTmp 'config'
$env:LOCALAPPDATA = Join-Path $runTmp 'data'
$env:XDG_CONFIG_HOME = $env:APPDATA
$env:XDG_DATA_HOME = $env:LOCALAPPDATA
$env:XDG_STATE_HOME = Join-Path $runTmp 'state'
$env:XDG_RUNTIME_DIR = Join-Path $runTmp 'runtime'
$env:DOTNET_CLI_HOME = Join-Path $runTmp 'dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_PROCESSOR_COUNT = '1'
$env:CARGO_BUILD_JOBS = '1'
$env:CMAKE_BUILD_PARALLEL_LEVEL = '1'
$env:NUGET_PACKAGES = Join-Path $cache 'nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $cache 'nuget-http'
$env:NUGET_SCRATCH = Join-Path $cache 'nuget-scratch'
$env:CARGO_HOME = Join-Path $cache 'cargo'
$diagnosticTest = Join-Path $root 'tests/Dsh.Tests/FilesViewModelTests.cs'
$variants = @(
    @{ Name = 'baseline'; Sha = '15e7c62d487649c3e542b227306799e14a659cf9' },
    @{ Name = 'candidate'; Sha = '9c5d1d1b228202be776502226b948628d628d98d' }
)
try {
    & pwsh -NoProfile -File (Join-Path $root 'scripts/fetch-dryioc-feed.ps1') -UseGhArtifact
    if ($LASTEXITCODE -ne 0) { throw 'Local feed preparation failed' }
    foreach ($variant in $variants) {
        $checkout = Join-Path $owned $variant.Name
        if (Test-Path -LiteralPath $checkout) { throw 'Variant checkout already exists' }
        git -C $root worktree add --detach $checkout $variant.Sha
        if ($LASTEXITCODE -ne 0) { throw 'Variant checkout failed' }
        Copy-Item -LiteralPath $diagnosticTest -Destination (Join-Path $checkout 'tests/Dsh.Tests/FilesViewModelTests.cs')
        Copy-Item -LiteralPath (Join-Path $root 'packages') -Destination (Join-Path $checkout 'packages') -Recurse -Force
        $native = Join-Path $checkout 'artifacts/iroh-ssh'
        New-Item -ItemType Directory -Path $native -Force | Out-Null
        & (Join-Path ([Environment]::GetFolderPath('System')) 'tar.exe') -xf (Join-Path $owned 'native/iroh-ssh-component.tar') -C $native
        if ($LASTEXITCODE -ne 0) { throw 'Native component extraction failed' }
        Push-Location $checkout
        try {
            Write-Output "VARIANT=$($variant.Name) SHA=$($variant.Sha)"
            dotnet build tests/Dsh.Tests/Dsh.Tests.csproj -c Debug --nologo -m:1 -nr:false -p:UseSharedCompilation=false -p:DshNativeCompiler=cl "-p:PathMap=$checkout=/w"
            if ($LASTEXITCODE -ne 0) { throw 'Variant build failed' }
            if (Test-Path -LiteralPath (Join-Path $native 'win-x64/target')) { throw 'Unexpected native component rebuild' }
            for ($round = 1; $round -le $rounds; $round++) {
                $report = Join-Path $evidence "$($variant.Name)-$round.xml"
                & 'tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests.exe' -method Dsh.Tests.FilesViewModelTests.CancelledUpload_LeavesNoRemoteFile -parallelMode none -noColor -result-xml $report
                $code = $LASTEXITCODE
                Write-Output "DIAGNOSTIC variant=$($variant.Name) round=$round exit=$code"
                if ($code -ne 0) { $failed++ }
                if (Test-Path -LiteralPath $report) {
                    [xml]$result = Get-Content -LiteralPath $report -Raw
                    foreach ($output in $result.SelectNodes('//test/output')) { Write-Output $output.InnerText }
                }
            }
        }
        finally { Pop-Location }
    }
    Write-Output "FIXED_DIAGNOSTIC_ROUNDS_PER_VARIANT=$rounds FAILED_RUNS=$failed"
}
finally {
    if (-not $runTmp.StartsWith("$owned\run-", [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup ownership guard failed' }
    Remove-Item -LiteralPath $runTmp -Recurse -Force
    if (Test-Path -LiteralPath $runTmp) { throw 'Owned temporary root remains' }
    Write-Output 'CLEANUP_ABSENT'
}
if ($failed -gt 0) { exit 1 }
