<#
确保本地 NuGet feed(packages/)包含 Dsh.Runtime 依赖的 DryIoc.dll。
需要的版本直接从 src/Dsh.Runtime/Dsh.Runtime.csproj 的 PackageReference 读取。
构建用的 commit 与 CI 产物 nupkg 的 SHA256 固定在 src/Dsh.Runtime/dryioc.pin, 升级 DryIoc 时只改那一处。
该版本不在 nuget.org 的 DryIoc 线上, 且包 ID 是 DryIoc.dll 而非 DryIoc。
CI 产物: 只取上游默认分支 push 触发、head_sha 等于固定 commit 的成功 run, 下载后 SHA256 必须等于固定值。
源码构建: 检出固定 commit 并核对 HEAD; dotnet pack 不可复现, 产物哈希与 CI 产物不同, 由戳记记录本地构建的哈希。
每个 nupkg 旁边的 <包名>.commit 戳记文件记录 "<commit> <sha256> <artifact|source>"。

用法:
    pwsh -File scripts/fetch-dryioc-feed.ps1
    pwsh -File scripts/fetch-dryioc-feed.ps1 -Force                      # 不管戳记是否一致都重建
    pwsh -File scripts/fetch-dryioc-feed.ps1 -Proxy http://<host>:<port>   # 访问 GitHub 走指定代理, 不传则直连
    pwsh -File scripts/fetch-dryioc-feed.ps1 -UseGhArtifact   # 本机有已登录的 gh CLI 时, 优先直接从 CI 产物下载

依赖: git 与 dotnet SDK。
#>
[CmdletBinding()]
param(
    [string]$Proxy,
    [switch]$UseGhArtifact,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# 指定代理时对本脚本启动的子进程生效(git/gh/dotnet 都识别这些标准变量); 未指定则直连。
if ($Proxy) {
    if ($Proxy -notmatch '^\w+://') {
        $Proxy = "http://$Proxy"
    }
    $env:HTTP_PROXY = $Proxy
    $env:HTTPS_PROXY = $Proxy
    $env:ALL_PROXY = $Proxy
    Write-Host "使用代理: $Proxy"
}

$RootDir = Split-Path -Parent $PSScriptRoot
$FeedDir = Join-Path $RootDir 'packages'
$WorkDir = Join-Path $RootDir 'artifacts020/.dryioc-src'
$Csproj = Join-Path $RootDir 'src/Dsh.Runtime/Dsh.Runtime.csproj'
$PinFile = Join-Path $RootDir 'src/Dsh.Runtime/dryioc.pin'

$PackageId = 'DryIoc.dll'
$RepoUrl = 'https://github.com/dadhi/DryIoc.git'
$TargetFramework = 'net9.0'

$GhRepo = 'dadhi/DryIoc'
$GhWorkflow = 'ci.yml'
$GhArtifact = 'packages'

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "缺少命令: $Name"
    }
}

function Assert-DotnetSdk {
    Assert-Command dotnet
    if (-not (dotnet --list-sdks 2>$null)) {
        throw 'dotnet 未安装 .NET SDK(只找到运行时); 请安装 .NET SDK, 或把含 SDK 的 dotnet 目录加入 PATH。'
    }
}

function Get-RequiredPackageVersion {
    $text = Get-Content -LiteralPath $Csproj -Raw
    $match = [regex]::Match($text, 'Include="DryIoc\.dll"\s+Version="([^"]+)"')
    if (-not $match.Success) {
        throw "无法从 $Csproj 读取 DryIoc.dll 的版本。"
    }
    return $match.Groups[1].Value
}

function Read-Pin([string]$Key) {
    $match = Select-String -LiteralPath $PinFile -Pattern "^$Key=(\S+)" | Select-Object -First 1
    if (-not $match) {
        throw "无法从 $PinFile 读取 $Key。"
    }
    return $match.Matches[0].Groups[1].Value
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-BuildStamp([string]$StampFile, [string]$Commit, [string]$Nupkg, [string]$Origin) {
    Set-Content -LiteralPath $StampFile -Value "$Commit $(Get-Sha256 $Nupkg) $Origin" -NoNewline
}

# 本地包与固定版本一致: 戳记 commit 等于固定 commit; CI 产物的哈希须等于固定值, 源码构建的哈希须等于构建时记录的值。
function Test-LocalPackageMatchesPin([string]$TargetNupkg, [string]$StampFile) {
    if (-not (Test-Path -LiteralPath $TargetNupkg) -or -not (Test-Path -LiteralPath $StampFile)) {
        return $false
    }
    $stamp = @((Get-Content -LiteralPath $StampFile -Raw).Trim() -split '\s+')
    if ($stamp[0] -ne $PinCommit) {
        return $false
    }
    $actual = Get-Sha256 $TargetNupkg
    if ($stamp.Count -ge 3 -and $stamp[2] -eq 'source') {
        return $actual -eq $stamp[1]
    }
    return $actual -eq $PinSha256
}

# 从固定 commit 的 CI 产物下载; 无 gh CLI、找不到符合条件的 run 或哈希不符时返回 $false, 由调用方回退。
function Get-FromGhArtifact([string]$TargetNupkg, [string]$StampFile) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        return $false
    }
    gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    $branch = gh api "repos/$GhRepo" --jq '.default_branch'
    if ($LASTEXITCODE -ne 0 -or -not $branch) {
        return $false
    }
    $runs = (gh api "repos/$GhRepo/actions/workflows/$GhWorkflow/runs?branch=$branch&event=push&status=success&head_sha=$PinCommit" | ConvertFrom-Json).workflow_runs
    $run = $runs | Where-Object { $_.head_sha -eq $PinCommit -and $_.event -eq 'push' -and $_.head_branch -eq $branch } | Select-Object -First 1
    if (-not $run) {
        Write-Warning "上游 $branch 分支上没有 commit $($PinCommit.Substring(0, 12)) 的成功 push run。"
        return $false
    }

    if (Test-Path -LiteralPath $WorkDir) {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    $staged = Join-Path $WorkDir ([System.IO.Path]::GetFileName($TargetNupkg))
    gh run download $run.id --repo $GhRepo --name $GhArtifact --dir $WorkDir *> $null
    if (-not (Test-Path -LiteralPath $staged)) {
        Write-Warning "run $($run.id) 的 $GhArtifact 产物不可用(可能已过期)。"
        Remove-Item -LiteralPath $WorkDir -Recurse -Force
        return $false
    }
    $actual = Get-Sha256 $staged
    if ($actual -ne $PinSha256) {
        Write-Warning "CI 产物哈希不符: 期望 $PinSha256, 实际 $actual; 拒绝使用。"
        Remove-Item -LiteralPath $WorkDir -Recurse -Force
        return $false
    }
    New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null
    Copy-Item -LiteralPath $staged -Destination $TargetNupkg -Force
    Write-BuildStamp $StampFile $PinCommit $TargetNupkg 'artifact'
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
    return $true
}

# 在指定 commit 上本地构建; 与 CI 一样产出包 ID 为 DryIoc.dll 的 nupkg。
function Build-FromSource([string]$Commit) {
    if (Test-Path -LiteralPath $WorkDir) {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

    # 先建好 feed 目录: 本仓库 NuGet.config 的 local-dsh 源指向相对路径 packages/,
    # DryIoc 的 restore 会继承该配置, 目录不存在则报 NU1301。
    New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null

    Write-Host "从源码构建 $PackageId (commit $($Commit.Substring(0, 12)))..."
    git -c core.longpaths=true clone --filter=blob:none --no-checkout $RepoUrl $WorkDir
    git -C $WorkDir fetch --depth 1 origin $Commit
    git -C $WorkDir -c core.longpaths=true checkout --quiet $Commit
    if ((git -C $WorkDir rev-parse HEAD) -ne $Commit) {
        throw "检出的 commit 与固定的 $Commit 不一致。"
    }

    # 部分克隆(blob:none)下 SourceLink 读取 git 会失败, 需关闭版本查询。
    dotnet build (Join-Path $WorkDir 'src/DryIoc/DryIoc.csproj') -c Release `
        -p:LatestSupportedNet=$TargetFramework -p:TargetFrameworks=$TargetFramework `
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

    New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null
    dotnet pack (Join-Path $WorkDir 'src/DryIoc/DryIoc.csproj') -c Release --no-build `
        -p:LatestSupportedNet=$TargetFramework -p:TargetFrameworks=$TargetFramework `
        -p:NoWarn=NU5129 -p:TreatWarningsAsErrors=false `
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false `
        -o $FeedDir

    Remove-Item -LiteralPath $WorkDir -Recurse -Force
}

# 检查 feed 里是否出现与 csproj 要求不符的新版本, 给出明确提示。
function Assert-ProducedVersion([string]$TargetNupkg, [string]$Version) {
    if (Test-Path -LiteralPath $TargetNupkg) {
        return
    }
    $produced = Get-ChildItem -LiteralPath $FeedDir -Filter "$PackageId.*.nupkg" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($produced) {
        throw "固定 commit 产出的版本是 $($produced.Name), 与 csproj 要求的 $Version 不一致; 请同步更新 $Csproj 的 PackageReference 或 $PinFile。"
    }
    throw "构建结束但未在 $FeedDir 找到任何 $PackageId.*.nupkg, 请检查上面的输出。"
}

$PackageVersion = Get-RequiredPackageVersion
$TargetNupkg = Join-Path $FeedDir "$PackageId.$PackageVersion.nupkg"
$StampFile = "$TargetNupkg.commit"
$PinCommit = Read-Pin 'commit'
$PinSha256 = Read-Pin 'sha256'
if ($PinCommit -cnotmatch '^[0-9a-f]{40}$' -or $PinSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw "$PinFile 的 commit 须为 40 位、sha256 须为 64 位小写十六进制。"
}

if (-not $Force -and (Test-LocalPackageMatchesPin $TargetNupkg $StampFile)) {
    Write-Host "本地 feed 已是固定版本($($PinCommit.Substring(0, 12))): $TargetNupkg"
    exit 0
}

if ($UseGhArtifact) {
    if (Get-FromGhArtifact $TargetNupkg $StampFile) {
        Write-Host "已从 CI 产物写入本地 feed(SHA256 已校验): $TargetNupkg"
        exit 0
    }
    Write-Warning 'gh CLI 不可用或 CI 产物不可用/校验失败, 回退到源码构建。'
}

# 先移除与固定版本不符的旧包, 免得构建失败或版本不符时旧文件冒充新产物。
Assert-DotnetSdk
Remove-Item -LiteralPath $TargetNupkg, $StampFile -Force -ErrorAction SilentlyContinue
Build-FromSource $PinCommit
Assert-ProducedVersion $TargetNupkg $PackageVersion
Write-BuildStamp $StampFile $PinCommit $TargetNupkg 'source'
Write-Host "源码构建的包哈希为 $(Get-Sha256 $TargetNupkg)(dotnet pack 不可复现, 与固定的 CI 产物哈希不同属正常; 来源由固定 commit 保证)。"

Write-Host "已写入本地 feed: $TargetNupkg"
Write-Host '下一步: dotnet restore'
