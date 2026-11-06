"""Boundary tests for archive policy, not substitutes for real built-product smoke tests."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("archive_test_build", ROOT / "scripts" / "archive-test-build.py")
ARCHIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ARCHIVER)
PREFIX = ARCHIVER.TEST_OUTPUT
SOURCE = "1" * 40


def member(name, kind=tarfile.REGTYPE, link="", mode=0o644):
    entry = tarfile.TarInfo(name)
    entry.type, entry.linkname, entry.mode = kind, link, mode
    entry.size = len(b"fixture text") if kind == tarfile.REGTYPE else 0
    return entry


class PathSafetyTests(unittest.TestCase):
    def test_noncanonical_or_cross_platform_unsafe_paths_fail(self):
        for name in (
            "../outside", "/outside", "C:/outside", f"{PREFIX}/../source.cs",
            f"{PREFIX}//double", f"{PREFIX}/./dot", f"{PREFIX}/back\\slash",
            f"{PREFIX}/file:stream", f"{PREFIX}/NUL.txt", f"{PREFIX}/COM1",
            f"{PREFIX}/trailing.", f"{PREFIX}/trailing ", f"{PREFIX}/line\nbreak",
        ):
            with self.subTest(name=name), self.assertRaises(ARCHIVER.ArchiveError):
                ARCHIVER.safe_name(name)

    def test_output_and_configuration_boundaries(self):
        for name in (
            ".git/config", "src/Dsh.Core/Plugin.cs", "native/plugin_guard/guard.c",
            "artifacts/iroh-ssh/linux-x64/iroh-ssh", "packages/DryIoc.dll",
            f"{PREFIX}-outside/output.txt", f"{PREFIX}/.env", f"{PREFIX}/.env.example",
            f"{PREFIX}/sessions/turn.json", f"{PREFIX}/plans/note.txt",
            f"{PREFIX}/credentials/token.json", f"{PREFIX}/settings.yaml",
            f"{PREFIX}/settings.json", f"{PREFIX}/secrets.json", f"{PREFIX}/private.pem",
            f"{PREFIX}/activity.jsonl", f"{PREFIX}/sdk-test-home/output.txt",
        ):
            with self.subTest(name=name), self.assertRaises(ARCHIVER.ArchiveError):
                ARCHIVER.safe_payload(name, "Linux")

    def test_runtime_metadata_and_checked_in_template_are_eligible(self):
        for name in (
            f"{PREFIX}/Dsh.Tests.runtimeconfig.json", f"{PREFIX}/Dsh.Tests.deps.json",
            f"{PREFIX}/Profiles/Templates/settings.yaml", f"{PREFIX}/Web/ApiSamples/search.json",
            f"{ARCHIVER.CLI_OUTPUT}/.dockerignore",
        ):
            with self.subTest(name=name):
                ARCHIVER.safe_payload(name, "Linux")

    def test_duplicate_and_case_colliding_entries_fail(self):
        for second in ("output.txt", "OUTPUT.txt"):
            with self.subTest(second=second), self.assertRaisesRegex(ARCHIVER.ArchiveError, "duplicate"):
                ARCHIVER.validate_members([member(f"{PREFIX}/output.txt"), member(f"{PREFIX}/{second}")], "Linux", set())

    def test_special_files_hardlinks_and_privileged_modes_fail(self):
        for kind in (tarfile.LNKTYPE, tarfile.FIFOTYPE, tarfile.CHRTYPE, tarfile.BLKTYPE):
            with self.subTest(kind=kind), self.assertRaisesRegex(ARCHIVER.ArchiveError, "unsupported"):
                ARCHIVER.validate_members([member(f"{PREFIX}/output.txt", kind)], "Linux", set())
        with self.assertRaisesRegex(ARCHIVER.ArchiveError, "modes"):
            ARCHIVER.validate_members([member(f"{PREFIX}/output.txt", mode=0o4755)], "Linux", set())

    def test_tracked_source_and_tracked_parent_fail(self):
        for tracked in ({f"{PREFIX}/output.txt"}, {f"{PREFIX}/OUTPUT.txt"}, {PREFIX}):
            with self.subTest(tracked=tracked), self.assertRaisesRegex(ARCHIVER.ArchiveError, "tracked"):
                ARCHIVER.validate_members([member(f"{PREFIX}/output.txt")], "Linux", tracked)

    def test_relative_links_and_link_chains_stay_in_payload(self):
        entries = [
            member(f"{PREFIX}/data", tarfile.DIRTYPE, mode=0o755),
            member(f"{PREFIX}/data/output.txt"),
            member(f"{PREFIX}/alias.txt", tarfile.SYMTYPE, "data/output.txt", 0o777),
            member(f"{PREFIX}/data/second.txt", tarfile.SYMTYPE, "../alias.txt", 0o777),
        ]
        self.assertEqual(set(ARCHIVER.validate_members(entries, "Linux", set())), {entry.name for entry in entries})

    def test_escaping_dangling_cyclic_and_linked_parent_entries_fail(self):
        for target in ("../../../../../README.md", "/outside", "C:/outside", "back\\slash", "missing.txt", "alias.txt"):
            with self.subTest(target=target), self.assertRaises(ARCHIVER.ArchiveError):
                ARCHIVER.validate_members([member(f"{PREFIX}/alias.txt", tarfile.SYMTYPE, target)], "Linux", set())
        for directory in ("alias", "ALIAS"):
            with self.subTest(directory=directory), self.assertRaisesRegex(ARCHIVER.ArchiveError, "non-directory"):
                ARCHIVER.validate_members([
                    member(f"{PREFIX}/data", tarfile.DIRTYPE),
                    member(f"{PREFIX}/alias", tarfile.SYMTYPE, "data"),
                    member(f"{PREFIX}/{directory}/output.txt"),
                ], "Linux", set())

    def test_runtime_completeness_is_not_proved_by_empty_payload(self):
        with self.assertRaisesRegex(ARCHIVER.ArchiveError, "missing required"):
            ARCHIVER.require_products({}, "Linux")

    def test_optional_native_products_are_exact_and_os_specific(self):
        for os_name in ("Linux", "Windows", "macOS"):
            for name in ARCHIVER.optional_files(os_name):
                ARCHIVER.safe_payload(name, os_name)
                with self.assertRaises(ARCHIVER.ArchiveError):
                    ARCHIVER.safe_payload(name + ".private", os_name)
        self.assertNotIn("Dsh.NativePluginSample.so", " ".join(ARCHIVER.optional_files("Windows")))

    def test_build_path_maps_are_excluded_without_losing_runtime_files(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            for directory in ARCHIVER.OUTPUT_ROOTS:
                (root / directory).mkdir(parents=True)
            runtime = root / PREFIX / "runtime.txt"
            runtime.write_text("runtime fixture text", encoding="utf-8")
            metadata = [
                root / PREFIX / ".msCoverageSourceRootsMapping_fixture",
                root / PREFIX / "CoverletSourceRootsMapping_fixture",
            ]
            for path in metadata:
                path.write_text("compiler build-path provenance", encoding="utf-8")
            inventory = ARCHIVER.product_paths(root, "Linux")
            self.assertIn(runtime, inventory)
            for path in metadata:
                self.assertNotIn(path, inventory)
                with self.assertRaises(ARCHIVER.ArchiveError):
                    ARCHIVER.safe_payload(path.relative_to(root).as_posix(), "Linux")

    def test_inventory_still_rejects_unexpected_private_material(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            for directory in ARCHIVER.OUTPUT_ROOTS:
                (root / directory).mkdir(parents=True)
            (root / PREFIX / "credentials.json").write_text("private fixture text", encoding="utf-8")
            with self.assertRaises(ARCHIVER.ArchiveError):
                ARCHIVER.product_paths(root, "Linux")


class ArchiveBoundaryTests(unittest.TestCase):
    def write_archive(self, root, manifest, entries=()):
        archive = root / "archive.tar.gz"
        with tarfile.open(archive, "w:gz") as output:
            contents = json.dumps(manifest).encode("utf-8")
            metadata = tarfile.TarInfo(ARCHIVER.MANIFEST)
            metadata.size = len(contents)
            output.addfile(metadata, io.BytesIO(contents))
            for entry in entries:
                output.addfile(entry, io.BytesIO(b"fixture text") if entry.isfile() else None)
        return archive

    def test_mismatched_source_os_and_format_fail_before_extraction(self):
        expected = {"version": ARCHIVER.VERSION, "source": SOURCE, "os": "Linux"}
        for change in ({"source": "2" * 40}, {"os": "Windows"}, {"version": 2}, {"version": True}):
            with self.subTest(change=change), tempfile.TemporaryDirectory(dir=ROOT) as temporary:
                root = Path(temporary)
                marker = root / "tracked.txt"
                marker.write_text("unchanged", encoding="utf-8")
                archive = self.write_archive(root, expected | change)
                with self.assertRaisesRegex(ARCHIVER.ArchiveError, "manifest"):
                    ARCHIVER.restore(root, archive, SOURCE, "Linux", {"tracked.txt"})
                self.assertEqual(marker.read_text(encoding="utf-8"), "unchanged")
                self.assertFalse((root / "tests").exists())

    def test_unsafe_archive_fails_without_creating_output_roots(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            marker = root / "tracked.txt"
            marker.write_text("unchanged", encoding="utf-8")
            archive = self.write_archive(root, {"version": ARCHIVER.VERSION, "source": SOURCE, "os": "Linux"},
                                         [member("../tracked.txt")])
            with self.assertRaisesRegex(ARCHIVER.ArchiveError, "unsafe"):
                ARCHIVER.restore(root, archive, SOURCE, "Linux", {"tracked.txt"})
            self.assertEqual(marker.read_text(encoding="utf-8"), "unchanged")
            self.assertFalse((root / "tests").exists())

    def test_archive_path_cannot_target_source_metadata_or_payload(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            for name in ("tracked.txt", ".git/archive.tar.gz", f"{PREFIX}/archive.tar.gz", *ARCHIVER.optional_files("Linux")):
                with self.subTest(name=name), self.assertRaises(ARCHIVER.ArchiveError):
                    ARCHIVER.archive_location(root, name, {"tracked.txt"})
            self.assertEqual(ARCHIVER.archive_location(root, ".ci-test-build/test-build.tar.gz", set()),
                             root / ".ci-test-build/test-build.tar.gz")

    @unittest.skipUnless(os.name == "posix", "Unix modes and link creation need a Unix runner.")
    def test_documented_filter_preserves_plain_text_modes_and_relative_links(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            archive = self.write_archive(root, {}, [
                member(f"{PREFIX}/output.txt", mode=0o751),
                member(f"{PREFIX}/alias.txt", tarfile.SYMTYPE, "output.txt", 0o777),
            ])
            staging = root / "staging"
            with tarfile.open(archive, "r:gz") as incoming:
                payload = [entry for entry in incoming.getmembers() if entry.name != ARCHIVER.MANIFEST]
                incoming.extractall(staging, members=payload, filter=ARCHIVER.extraction_filter)
            target = staging / PREFIX / "output.txt"
            alias = staging / PREFIX / "alias.txt"
            self.assertEqual(target.stat().st_mode & 0o777, 0o751)
            self.assertTrue(alias.is_symlink())
            self.assertEqual(os.readlink(alias), "output.txt")
            self.assertEqual(alias.read_text(encoding="utf-8"), "fixture text")
            (root / "linked").symlink_to(staging, target_is_directory=True)
            with self.assertRaisesRegex(ARCHIVER.ArchiveError, "parent"):
                ARCHIVER.ordinary_parents(root, "linked/child.txt")


if __name__ == "__main__":
    unittest.main()