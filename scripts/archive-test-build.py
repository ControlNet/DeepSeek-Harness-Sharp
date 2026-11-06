#!/usr/bin/env python3
"""Share complete Debug test runtime products between identical clean CI checkouts.

Requires Python 3.12 or later for the documented tarfile.data_filter protection.
Run pack or restore from the repository root with --archive PATH --source SHA --os OS.
Only the explicitly inventoried runtime roots and optional native ABI products are eligible.
The test project copies the MCP server, isolated plugin, observer, guard and probe into its output.
RealHostProcesses and PluginWorkerEntrypointTests also launch the CLI and web apphosts in their own outputs.
PluginTemplateTests loads the template's separate plugin deployment directory.
Dsh.Host and Dsh.Pty are managed dependencies in those complete runtime closures, not separate apphosts.
PluginGuard.props copies its obj/dsh-guard libraries into bin, so no obj or Cargo intermediates are needed.
NativePluginAbiTests optionally loads the frozen-v1 C fixtures and published Linux sample at the exact paths below.
The independently installed SDK, NuGet feed, C# frontends, SSH and Iroh component remain CI prerequisites.
Restore deliberately refuses existing payload destinations instead of merging into an old build.
The JSON summary counts payload entries and regular-file bytes, excluding the manifest and tar padding.
"""
import argparse
import io
import json
import os
from pathlib import Path, PurePosixPath
import platform
import posixpath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile


VERSION = 1
MANIFEST = "test-build-manifest.json"
TEST_OUTPUT = "tests/Dsh.Tests/bin/Debug/net10.0"
CLI_OUTPUT = "DeepSeek-Harness-Sharp/bin/Debug/net10.0"
WEB_OUTPUT = "Dsh.WebHost/bin/Debug/net10.0"
TEMPLATE_OUTPUT = "templates/managed-plugin/bin/Debug/net10.0/plugin"
OUTPUT_ROOTS = (TEST_OUTPUT, CLI_OUTPUT, WEB_OUTPUT, TEMPLATE_OUTPUT)
OS_NAMES = {"Windows": "Windows", "Linux": "Linux", "Darwin": "macOS"}
NATIVE_NAMES = {
    "Windows": ("dsh_plugin_guard.dll", "dsh_fault_probe.dll", "legacy_host.exe", "legacy_v1.dll"),
    "Linux": ("libdsh_plugin_guard.so", "libdsh_fault_probe.so", "legacy_host", "liblegacy_v1.so"),
    "macOS": ("libdsh_plugin_guard.dylib", "libdsh_fault_probe.dylib", "legacy_host", "liblegacy_v1.dylib"),
}
PRIVATE_NAMES = {
    ".git", ".env", ".ssh", ".aws", ".azure", ".dsh", ".dsh-memory", ".omo",
    "config", "configuration", "credentials", "secrets", "plans", "knowledge", "knowledges",
    "sessions", "session", "logs", "sdk-test-home", "__pycache__",
}
BUILD_PATH_METADATA_PREFIXES = (".msCoverageSourceRootsMapping_", "CoverletSourceRootsMapping_")


class ArchiveError(Exception):
    """A safe, path-free diagnostic for an invalid checkout or archive."""


def git(root, *args):
    result = subprocess.run(["git", *args], cwd=root, capture_output=True)
    if result.returncode:
        raise ArchiveError("Git checkout validation failed.")
    return result.stdout


def checkout(root, source, os_name):
    if re.fullmatch(r"[0-9a-f]{40}", source) is None:
        raise ArchiveError("Source must be a full lowercase commit SHA.")
    if OS_NAMES.get(platform.system()) != os_name:
        raise ArchiveError("Requested OS does not match this runner.")
    if git(root, "rev-parse", "--show-prefix").strip():
        raise ArchiveError("Run from the checkout root.")
    if git(root, "rev-parse", "HEAD").decode("ascii").strip() != source:
        raise ArchiveError("Source does not match the checked-out commit.")
    git(root, "diff-index", "--quiet", "HEAD", "--")
    return {os.fsdecode(name) for name in git(root, "ls-files", "-z").split(b"\0") if name}


def optional_files(os_name):
    names = NATIVE_NAMES[os_name]
    files = [f"tests/Dsh.NativePluginV1Fixture/bin/{name}" for name in names[2:]]
    if os_name == "Linux":
        files.append("tests/Dsh.NativePluginSample/bin/Release/net10.0/linux-x64/publish/Dsh.NativePluginSample.so")
    return tuple(files)


def required_files(os_name):
    suffix = ".exe" if os_name == "Windows" else ""
    files = set()
    for directory, assembly in ((TEST_OUTPUT, "Dsh.Tests"), (CLI_OUTPUT, "dsharp"), (WEB_OUTPUT, "dsharp-web")):
        files.update(f"{directory}/{assembly}{extension}" for extension in (suffix, ".dll", ".deps.json", ".runtimeconfig.json"))
    files.update(f"{TEST_OUTPUT}/{name}" for name in (
        "Dsh.Host.dll", "Dsh.Pty.dll", "Dsh.ManagedPluginSample.dll", "Dsh.ManagedPluginSample.deps.json",
        "network-gui-observer.dll", "Profiles/Templates/settings.yaml",
        "isolated-plugin/Dsh.IsolatedPluginSample.dll", "isolated-plugin/Dsh.IsolatedPluginSample.deps.json",
        *NATIVE_NAMES[os_name][:2],
    ))
    files.update(f"{TEST_OUTPUT}/mcp-resource-server/Dsh.McpResourceServerSample{extension}"
                 for extension in (suffix, ".dll", ".deps.json", ".runtimeconfig.json"))
    files.update(f"{TEMPLATE_OUTPUT}/DshPluginTemplate{extension}" for extension in (".dll", ".deps.json"))
    return files


def safe_name(name):
    parts = PurePosixPath(name).parts
    if (not parts or name != PurePosixPath(name).as_posix() or name.startswith("/")
            or any(part in (".", "..") or "\\" in part or ":" in part
                   or part.endswith((".", " ")) or any(ord(char) < 32 for char in part)
                   or re.fullmatch(r"(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?", part)
                   for part in parts)):
        raise ArchiveError("Archive contains an unsafe path.")
    return parts


def allowed_payload(name, os_name):
    return any(name == directory or name.startswith(directory + "/") for directory in OUTPUT_ROOTS) or name in optional_files(os_name)


def safe_payload(name, os_name):
    parts = safe_name(name)
    if not allowed_payload(name, os_name):
        raise ArchiveError("Archive contains a path outside the runtime product allowlist.")
    if parts[-1].startswith(BUILD_PATH_METADATA_PREFIXES):
        raise ArchiveError("Runtime output contains build-path provenance.")
    if any(part.lower() in PRIVATE_NAMES or (part.startswith(".") and part != ".dockerignore") for part in parts):
        raise ArchiveError("Runtime output contains private or non-product material.")
    lower = name.lower()
    if (lower.endswith((".pem", ".key", ".pfx", ".p12", ".log", ".jsonl", ".yaml", ".yml"))
            and not name.endswith("/Profiles/Templates/settings.yaml")):
        raise ArchiveError("Runtime output contains a private or configuration file.")
    if parts[-1].lower() in {"settings.json", "config.json", "credentials.json", "secrets.json", "nuget.config"}:
        raise ArchiveError("Runtime output contains a configuration file.")


def validate_members(members, os_name, tracked):
    entries = {}
    folded = {}
    for member in members:
        safe_payload(member.name, os_name)
        if member.name.casefold() in folded:
            raise ArchiveError("Archive contains duplicate or case-colliding paths.")
        folded[member.name.casefold()] = member
        if not (member.isfile() or member.isdir() or member.issym()) or member.size < 0:
            raise ArchiveError("Archive contains an unsupported entry type.")
        if member.mode & ~0o777:
            raise ArchiveError("Archive contains privileged or invalid modes.")
        entries[member.name] = member
    tracked_folded = {name.casefold() for name in tracked}
    for name, member in entries.items():
        ancestors = PurePosixPath(name).parents
        if name.casefold() in tracked_folded or any(parent.as_posix().casefold() in tracked_folded for parent in ancestors):
            raise ArchiveError("Archive would overwrite tracked source.")
        if any(parent.as_posix().casefold() in folded and not folded[parent.as_posix().casefold()].isdir() for parent in ancestors):
            raise ArchiveError("Archive contains a non-directory parent.")
        if member.issym():
            target = member.linkname
            if not target or target.startswith("/") or "\\" in target or ":" in target or any(ord(char) < 32 for char in target):
                raise ArchiveError("Archive contains an unsafe symbolic link.")
            resolved = posixpath.normpath(posixpath.join(posixpath.dirname(name), target))
            safe_payload(resolved, os_name)
            seen = {name}
            while True:
                if resolved in seen or resolved not in entries:
                    raise ArchiveError("Archive contains a cyclic or dangling symbolic link.")
                seen.add(resolved)
                target_member = entries[resolved]
                if not target_member.issym():
                    break
                resolved = posixpath.normpath(posixpath.join(posixpath.dirname(resolved), target_member.linkname))
    return entries


def require_products(entries, os_name):
    for name in required_files(os_name):
        if name not in entries:
            raise ArchiveError("Archive is missing required real test runtime products.")
        member = entries[name]
        while member.issym():
            target = posixpath.normpath(posixpath.join(posixpath.dirname(member.name), member.linkname))
            member = entries[target]
        if not member.isfile() or member.size == 0:
            raise ArchiveError("Archive has an empty or non-file required runtime product.")


def ordinary_parents(root, relative):
    current = root
    for part in PurePosixPath(relative).parts[:-1]:
        current /= part
        if current.is_symlink() or (current.exists() and not current.is_dir()):
            raise ArchiveError("Destination has a linked or non-directory parent.")


def archive_location(root, argument, tracked):
    parts = safe_name(argument)
    if ".git" in (part.lower() for part in parts) or argument.casefold() in {name.casefold() for name in tracked}:
        raise ArchiveError("Archive destination must not overwrite repository metadata or source.")
    if any(allowed_payload(argument, os_name) for os_name in OS_NAMES.values()):
        raise ArchiveError("Archive must be outside the payload roots.")
    ordinary_parents(root, argument)
    path = root / argument
    if path.is_symlink():
        raise ArchiveError("Archive path must not be a symbolic link.")
    return path


def product_paths(root, os_name):
    paths = []
    pending = [root / directory for directory in OUTPUT_ROOTS]
    if any(not directory.is_dir() or directory.is_symlink() for directory in pending):
        raise ArchiveError("Build all required runtime outputs before packing.")
    pending.extend(root / name for name in optional_files(os_name) if os.path.lexists(root / name))
    while pending:
        path = pending.pop()
        # Coverage source-root maps carry compiler path provenance outside these non-coverage runtime consumers.
        if path.name.startswith(BUILD_PATH_METADATA_PREFIXES):
            continue
        name = path.relative_to(root).as_posix()
        ordinary_parents(root, name)
        safe_payload(name, os_name)
        paths.append(path)
        if path.is_dir() and not path.is_symlink():
            pending.extend(path.iterdir())
    return sorted(paths)


def summary(operation, archive, members):
    print(json.dumps({
        "operation": operation,
        "files": sum(member.isfile() for member in members),
        "directories": sum(member.isdir() for member in members),
        "symlinks": sum(member.issym() for member in members),
        "uncompressed_bytes": sum(member.size for member in members if member.isfile()),
        "compressed_bytes": archive.stat().st_size,
    }, sort_keys=True))


def pack(root, archive, source, os_name, tracked):
    paths = product_paths(root, os_name)
    archive.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="test-build-pack-", dir=archive.parent) as temporary:
        pending = Path(temporary) / "test-build.tar.gz"
        with tarfile.open(pending, "w:gz", compresslevel=1, dereference=False) as output:
            members = []
            for path in paths:
                member = output.gettarinfo(str(path), arcname=path.relative_to(root).as_posix())
                if member is None:
                    raise ArchiveError("Runtime output contains an unsupported filesystem entry.")
                # Store hard-linked regular products as independent files without changing symbolic links.
                if member.islnk():
                    member.type, member.linkname, member.size = tarfile.REGTYPE, "", path.stat().st_size
                member.uid = member.gid = 0
                member.uname = member.gname = ""
                member.pax_headers = {}
                members.append(member)
            require_products(validate_members(members, os_name, tracked), os_name)
            manifest = json.dumps({"version": VERSION, "source": source, "os": os_name}, sort_keys=True).encode("utf-8")
            metadata = tarfile.TarInfo(MANIFEST)
            metadata.size, metadata.mode = len(manifest), 0o644
            output.addfile(metadata, io.BytesIO(manifest))
            for path, member in zip(paths, members):
                if member.isfile():
                    with path.open("rb") as contents:
                        output.addfile(member, contents)
                else:
                    output.addfile(member)
        os.replace(pending, archive)
    summary("pack", archive, members)


def extraction_filter(member, destination):
    # data_filter rejects outside destinations and unsafe link targets before each extraction.
    filtered = tarfile.data_filter(member, destination)
    # Retain ordinary Unix permissions instead of data_filter's default permission normalization.
    return filtered.replace(mode=None if member.issym() else member.mode)


def restore(root, archive, source, os_name, tracked):
    with tarfile.open(archive, "r:gz") as incoming:
        members = incoming.getmembers()
        manifests = [member for member in members if member.name == MANIFEST]
        if len(manifests) != 1 or not manifests[0].isfile() or manifests[0].size > 1024:
            raise ArchiveError("Archive has no valid unique manifest.")
        with incoming.extractfile(manifests[0]) as contents:
            manifest = json.load(contents)
        if not isinstance(manifest, dict) or type(manifest.get("version")) is not int or manifest != {"version": VERSION, "source": source, "os": os_name}:
            raise ArchiveError("Archive manifest does not match the source, OS and format version.")
        payload = [member for member in members if member.name != MANIFEST]
        entries = validate_members(payload, os_name, tracked)
        require_products(entries, os_name)
        destinations = list(OUTPUT_ROOTS) + [name for name in optional_files(os_name) if name in entries]
        for name in destinations:
            ordinary_parents(root, name)
            if os.path.lexists(root / name):
                raise ArchiveError("Restore requires clean, absent runtime output destinations.")
        with tempfile.TemporaryDirectory(prefix=".test-build-restore-", dir=root) as temporary:
            staging = Path(temporary)
            incoming.extractall(staging, members=payload, filter=extraction_filter)
            installed = []
            try:
                for name in destinations:
                    destination = root / name
                    ordinary_parents(root, name)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    os.replace(staging / name, destination)
                    installed.append(destination)
            except (OSError, ArchiveError):
                for destination in reversed(installed):
                    if destination.is_dir() and not destination.is_symlink():
                        shutil.rmtree(destination)
                    else:
                        destination.unlink()
                raise
    summary("restore", archive, payload)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("pack", "restore"))
    parser.add_argument("--archive", required=True, help="Project-relative tar.gz outside runtime output roots")
    parser.add_argument("--source", required=True, help="Exact checked-out 40-character commit SHA")
    parser.add_argument("--os", required=True, choices=("Windows", "Linux", "macOS"))
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ArchiveError("Python 3.12 or later is required for safe extraction.")
        root = Path.cwd()
        tracked = checkout(root, args.source, args.os)
        archive = archive_location(root, args.archive, tracked)
        if args.operation == "pack":
            pack(root, archive, args.source, args.os, tracked)
        else:
            restore(root, archive, args.source, args.os, tracked)
    except ArchiveError as error:
        print(str(error), file=sys.stderr)
        return 1
    except (OSError, ValueError, tarfile.TarError, EOFError):
        # Library and OS exceptions can embed private absolute paths, so do not echo their messages.
        print("Test build archive operation failed; no tracked source was extracted.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())