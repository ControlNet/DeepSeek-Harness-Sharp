#!/usr/bin/env bash
#
# 确保本地 NuGet feed(packages/)包含 Dsh.Runtime 依赖的 DryIoc.dll。
# 需要的版本直接从 src/Dsh.Runtime/Dsh.Runtime.csproj 的 PackageReference 读取。
# 构建用的 commit 与 CI 产物 nupkg 的 SHA256 固定在 src/Dsh.Runtime/dryioc.pin, 升级 DryIoc 时只改那一处。
# 该版本不在 nuget.org 的 DryIoc 线上, 且包 ID 是 DryIoc.dll 而非 DryIoc。
# CI 产物: 只取上游默认分支 push 触发、head_sha 等于固定 commit 的成功 run, 下载后 SHA256 必须等于固定值。
# 源码构建: 检出固定 commit 并核对 HEAD; dotnet pack 不可复现, 产物哈希与 CI 产物不同, 由戳记记录本地构建的哈希。
# 每个 nupkg 旁边的 <包名>.commit 戳记文件记录 "<commit> <sha256> <artifact|source>"。
#
# 用法:
#   ./scripts/fetch-dryioc-feed.sh
#   ./scripts/fetch-dryioc-feed.sh --force                       # 不管戳记是否一致都重建
#   ./scripts/fetch-dryioc-feed.sh --proxy http://<host>:<port>   # 访问 GitHub 走指定代理, 不传则直连
#   ./scripts/fetch-dryioc-feed.sh --use-gh-artifact   # 本机有已登录的 gh CLI 时, 优先直接从 CI 产物下载
#
# 依赖: git 与 dotnet SDK; 计算哈希用 sha256sum 或 shasum。
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
FEED_DIR="$ROOT_DIR/packages"
WORK_DIR="$ROOT_DIR/artifacts020/.dryioc-src"
CSPROJ="$ROOT_DIR/src/Dsh.Runtime/Dsh.Runtime.csproj"
PIN_FILE="$ROOT_DIR/src/Dsh.Runtime/dryioc.pin"

PACKAGE_ID="DryIoc.dll"
REPO_URL="https://github.com/dadhi/DryIoc.git"
TARGET_FRAMEWORK="net9.0"

GH_REPO="dadhi/DryIoc"
GH_WORKFLOW="ci.yml"
GH_ARTIFACT="packages"

USE_GH_ARTIFACT=0
PROXY=""
FORCE=0

usage() {
    cat <<'EOF'
用法: fetch-dryioc-feed.sh [选项]

选项:
  --proxy <url>       访问 GitHub 走指定代理(如 http://<host>:<port>), 不传则直连
  --use-gh-artifact   若本机有已登录的 gh CLI, 优先从 CI 产物下载, 失败则回退到源码构建
  --force             不管戳记是否与固定版本一致都重建
  -h, --help          显示本帮助
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --proxy) shift; PROXY="${1:?--proxy 需要一个值}" ;;
        --proxy=*) PROXY="${1#--proxy=}" ;;
        --use-gh-artifact) USE_GH_ARTIFACT=1 ;;
        --force) FORCE=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "未知参数: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# 指定代理时对本脚本启动的子进程生效(git/gh/dotnet 都识别这些标准变量); 未指定则直连。
if [ -n "$PROXY" ]; then
    case "$PROXY" in
        *://*) ;;
        *) PROXY="http://$PROXY" ;;
    esac
    export HTTP_PROXY="$PROXY"
    export HTTPS_PROXY="$PROXY"
    export ALL_PROXY="$PROXY"
    echo "使用代理: $PROXY"
fi

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "缺少命令: $1" >&2
        exit 1
    fi
}

require_dotnet_sdk() {
    require_command dotnet
    if [ -z "$(dotnet --list-sdks 2>/dev/null || true)" ]; then
        echo "dotnet 未安装 .NET SDK(只找到运行时); 请安装 .NET SDK, 或把含 SDK 的 dotnet 目录加入 PATH。" >&2
        exit 1
    fi
}

get_required_package_version() {
    local version
    version="$(sed -n -E 's/.*Include="DryIoc\.dll"[[:space:]]+Version="([^"]+)".*/\1/p' "$CSPROJ" | head -n1)"
    if [ -z "$version" ]; then
        echo "无法从 $CSPROJ 读取 DryIoc.dll 的版本。" >&2
        exit 1
    fi
    printf '%s' "$version"
}

read_pin() {
    local value
    value="$(sed -n -E "s/^$1=([^[:space:]]+).*/\1/p" "$PIN_FILE" | head -n1)"
    if [ -z "$value" ]; then
        echo "无法从 $PIN_FILE 读取 $1。" >&2
        exit 1
    fi
    printf '%s' "$value"
}

file_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

write_build_stamp() {
    printf '%s %s %s\n' "$2" "$(file_sha256 "$3")" "$4" > "$1"
}

# 本地包与固定版本一致: 戳记 commit 等于固定 commit; CI 产物的哈希须等于固定值, 源码构建的哈希须等于构建时记录的值。
local_package_matches_pin() {
    local target_nupkg="$1"
    local stamp_file="$2"
    [ -f "$target_nupkg" ] && [ -f "$stamp_file" ] || return 1
    local stamp_commit="" stamp_sha="" stamp_origin=""
    read -r stamp_commit stamp_sha stamp_origin < "$stamp_file" || [ -n "$stamp_commit" ] || return 1
    [ "$stamp_commit" = "$PIN_COMMIT" ] || return 1
    local actual
    actual="$(file_sha256 "$target_nupkg")"
    if [ "$stamp_origin" = "source" ]; then
        [ "$actual" = "$stamp_sha" ]
    else
        [ "$actual" = "$PIN_SHA256" ]
    fi
}

# 从固定 commit 的 CI 产物下载; 无 gh CLI、找不到符合条件的 run 或哈希不符时返回非零, 由调用方回退。
fetch_from_gh_artifact() {
    local target_nupkg="$1"
    local stamp_file="$2"

    command -v gh >/dev/null 2>&1 || return 1
    gh auth status >/dev/null 2>&1 || return 1

    local branch run_id
    branch="$(gh api "repos/$GH_REPO" --jq '.default_branch')" || return 1
    run_id="$(gh api "repos/$GH_REPO/actions/workflows/$GH_WORKFLOW/runs?branch=$branch&event=push&status=success&head_sha=$PIN_COMMIT" \
        --jq ".workflow_runs[] | select(.head_sha == \"$PIN_COMMIT\" and .event == \"push\" and .head_branch == \"$branch\") | .id" | head -n1)" || return 1
    if [ -z "$run_id" ]; then
        echo "上游 $branch 分支上没有 commit ${PIN_COMMIT:0:12} 的成功 push run。" >&2
        return 1
    fi

    local staged
    staged="$WORK_DIR/$(basename -- "$target_nupkg")"
    rm -rf "$WORK_DIR"
    mkdir -p "$WORK_DIR"
    if ! gh run download "$run_id" --repo "$GH_REPO" --name "$GH_ARTIFACT" --dir "$WORK_DIR" >/dev/null 2>&1 || [ ! -f "$staged" ]; then
        echo "run $run_id 的 $GH_ARTIFACT 产物不可用(可能已过期)。" >&2
        rm -rf "$WORK_DIR"
        return 1
    fi
    local actual
    actual="$(file_sha256 "$staged")"
    if [ "$actual" != "$PIN_SHA256" ]; then
        echo "CI 产物哈希不符: 期望 $PIN_SHA256, 实际 $actual; 拒绝使用。" >&2
        rm -rf "$WORK_DIR"
        return 1
    fi
    mkdir -p "$FEED_DIR"
    cp "$staged" "$target_nupkg"
    write_build_stamp "$stamp_file" "$PIN_COMMIT" "$target_nupkg" artifact
    rm -rf "$WORK_DIR"
    return 0
}

# 在指定 commit 上本地构建; 与 CI 一样产出包 ID 为 DryIoc.dll 的 nupkg。
build_from_source() {
    local commit="$1"

    rm -rf "$WORK_DIR"
    mkdir -p "$WORK_DIR"

    # 先建好 feed 目录: 本仓库 NuGet.config 的 local-dsh 源指向相对路径 packages/,
    # DryIoc 的 restore 会继承该配置, 目录不存在则报 NU1301。
    mkdir -p "$FEED_DIR"

    echo "从源码构建 $PACKAGE_ID (commit ${commit:0:12})..."
    git -c core.longpaths=true clone --filter=blob:none --no-checkout "$REPO_URL" "$WORK_DIR"
    git -C "$WORK_DIR" fetch --depth 1 origin "$commit"
    git -C "$WORK_DIR" -c core.longpaths=true checkout --quiet "$commit"
    if [ "$(git -C "$WORK_DIR" rev-parse HEAD)" != "$commit" ]; then
        echo "检出的 commit 与固定的 $commit 不一致。" >&2
        exit 1
    fi

    # 部分克隆(blob:none)下 SourceLink 读取 git 会失败, 需关闭版本查询。
    dotnet build "$WORK_DIR/src/DryIoc/DryIoc.csproj" -c Release \
        -p:LatestSupportedNet="$TARGET_FRAMEWORK" -p:TargetFrameworks="$TARGET_FRAMEWORK" \
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

    mkdir -p "$FEED_DIR"
    dotnet pack "$WORK_DIR/src/DryIoc/DryIoc.csproj" -c Release --no-build \
        -p:LatestSupportedNet="$TARGET_FRAMEWORK" -p:TargetFrameworks="$TARGET_FRAMEWORK" \
        -p:NoWarn=NU5129 -p:TreatWarningsAsErrors=false \
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false \
        -o "$FEED_DIR"

    rm -rf "$WORK_DIR"
}

# 检查 feed 里是否出现与 csproj 要求不符的新版本, 给出明确提示。
assert_produced_version() {
    local target_nupkg="$1"
    local version="$2"

    [ -f "$target_nupkg" ] && return 0

    local produced
    # BSD find(macOS) 不支持 -printf, 用 -exec basename 兼容。
    produced="$(find "$FEED_DIR" -maxdepth 1 -name "$PACKAGE_ID.*.nupkg" -exec basename {} \; 2>/dev/null | head -n1 || true)"
    if [ -n "$produced" ]; then
        echo "固定 commit 产出的版本是 $produced, 与 csproj 要求的 $version 不一致; 请同步更新 $CSPROJ 的 PackageReference 或 $PIN_FILE。" >&2
        exit 1
    fi
    echo "构建结束但未在 $FEED_DIR 找到任何 $PACKAGE_ID.*.nupkg, 请检查上面的输出。" >&2
    exit 1
}

PACKAGE_VERSION="$(get_required_package_version)"
TARGET_NUPKG="$FEED_DIR/$PACKAGE_ID.$PACKAGE_VERSION.nupkg"
STAMP_FILE="$TARGET_NUPKG.commit"
PIN_COMMIT="$(read_pin commit)"
PIN_SHA256="$(read_pin sha256)"
if ! printf '%s' "$PIN_COMMIT" | grep -Eqx '[0-9a-f]{40}' || ! printf '%s' "$PIN_SHA256" | grep -Eqx '[0-9a-f]{64}'; then
    echo "$PIN_FILE 的 commit 须为 40 位、sha256 须为 64 位小写十六进制。" >&2
    exit 1
fi

if [ "$FORCE" != "1" ] && local_package_matches_pin "$TARGET_NUPKG" "$STAMP_FILE"; then
    echo "本地 feed 已是固定版本(${PIN_COMMIT:0:12}): $TARGET_NUPKG"
    exit 0
fi

if [ "$USE_GH_ARTIFACT" = "1" ]; then
    if fetch_from_gh_artifact "$TARGET_NUPKG" "$STAMP_FILE"; then
        echo "已从 CI 产物写入本地 feed(SHA256 已校验): $TARGET_NUPKG"
        exit 0
    fi
    echo "gh CLI 不可用或 CI 产物不可用/校验失败, 回退到源码构建。" >&2
fi

# 先移除与固定版本不符的旧包, 免得构建失败或版本不符时旧文件冒充新产物。
require_dotnet_sdk
rm -f "$TARGET_NUPKG" "$STAMP_FILE"
build_from_source "$PIN_COMMIT"
assert_produced_version "$TARGET_NUPKG" "$PACKAGE_VERSION"
write_build_stamp "$STAMP_FILE" "$PIN_COMMIT" "$TARGET_NUPKG" source
echo "源码构建的包哈希为 $(file_sha256 "$TARGET_NUPKG")(dotnet pack 不可复现, 与固定的 CI 产物哈希不同属正常; 来源由固定 commit 保证)。"

echo "已写入本地 feed: $TARGET_NUPKG"
echo "下一步: dotnet restore"
