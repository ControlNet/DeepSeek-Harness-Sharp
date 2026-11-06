"""Boundary tests for archive policy, not substitutes for real built-product smoke tests."""
from contextlib import redirect_stdout
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

    def test_special_files_and_privileged_modes_fail(self):
        for kind in (tarfile.FIFOTYPE, tarfile.CHRTYPE, tarfile.BLKTYPE):
            with self.subTest(kind=kind), self.assertRaisesRegex(ARCHIVER.ArchiveError, "unsupported"):
                ARCHIVER.validate_members([member(f"{PREFIX}/output.txt", kind)], "Linux", set())
        with self.assertRaisesRegex(ARCHIVER.ArchiveError, "modes"):
            ARCHIVER.validate_members([member(f"{PREFIX}/output.txt", mode=0o4755)], "Linux", set())

    def test_transport_references_require_canonical_regular_payload_targets(self):
        regular = member(f"{PREFIX}/output.txt")
        directory = member(f"{PREFIX}/data", tarfile.DIRTYPE)
        symbolic = member(f"{PREFIX}/symbolic.txt", tarfile.SYMTYPE, "output.txt")
        reference = member(f"{PREFIX}/reference.txt", tarfile.LNKTYPE, regular.name)
        for target in (
            "", "../outside", "/outside", "C:/outside", "back\\slash",
            f"{PREFIX}/../output.txt", f"{PREFIX}//output.txt",
            "src/Dsh.Core/Plugin.cs", f"{PREFIX}/credentials.json",
            f"{PREFIX}/missing.txt", f"{PREFIX}/OUTPUT.txt", directory.name,
            symbolic.name, reference.name, f"{PREFIX}/alias.txt",
        ):
            with self.subTest(target=target), self.assertRaises(ARCHIVER.ArchiveError):
                ARCHIVER.validate_members([
                    regular, directory, symbolic, reference,
                    member(f"{PREFIX}/alias.txt", tarfile.LNKTYPE, target),
                ], "Linux", set())

    def test_transport_reference_targets_cannot_be_tracked_or_have_linked_ancestors(self):
        target = member(f"{PREFIX}/data/output.txt")
        reference = member(f"{ARCHIVER.CLI_OUTPUT}/copy.txt", tarfile.LNKTYPE, target.name)
        for tracked in ({target.name}, {target.name.upper()}, {f"{PREFIX}/data"}):
            with self.subTest(tracked=tracked), self.assertRaisesRegex(ARCHIVER.ArchiveError, "tracked"):
                ARCHIVER.validate_members([target, reference], "Linux", tracked)
        with self.assertRaisesRegex(ARCHIVER.ArchiveError, "non-directory"):
            ARCHIVER.validate_members([
                member(f"{PREFIX}/actual", tarfile.DIRTYPE),
                member(f"{PREFIX}/data", tarfile.SYMTYPE, "actual"), target, reference,
            ], "Linux", set())

    def test_transport_reference_metadata_and_modes_are_not_trusted(self):
        target = member(f"{PREFIX}/output.txt")
        for size, mode, headers in (
            (1, 0o644, {}), (0, 0o4755, {}), (0, 0o2644, {}),
            (0, 0o644, {"GNU.sparse.name": target.name}),
            (0, 0o644, {"path": f"{PREFIX}/other.txt"}),
            (0, 0o644, {"linkpath": f"{PREFIX}/other.txt"}),
        ):
            reference = member(f"{PREFIX}/copy.txt", tarfile.LNKTYPE, target.name, mode)
            reference.size, reference.pax_headers = size, headers
            with self.subTest(size=size, mode=mode, headers=headers), self.assertRaises(ARCHIVER.ArchiveError):
                ARCHIVER.validate_members([target, reference], "Linux", set())
        reference = member(f"{PREFIX}/copy.txt", tarfile.LNKTYPE, target.name)
        reference.sparse = []
        with self.assertRaisesRegex(ARCHIVER.ArchiveError, "metadata"):
            ARCHIVER.validate_members([target, reference], "Linux", set())

    def test_valid_reference_is_allowed_with_an_independent_mode(self):
        entries = [
            member(f"{PREFIX}/output.txt", mode=0o640),
            member(f"{ARCHIVER.CLI_OUTPUT}/copy.txt", tarfile.LNKTYPE, f"{PREFIX}/output.txt", 0o751),
        ]
        for os_name in ("Linux", "Windows", "macOS"):
            with self.subTest(os_name=os_name):
                self.assertEqual(set(ARCHIVER.validate_members(entries, os_name, set())),
                                 {entry.name for entry in entries})

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
        for change in ({"source": "2" * 40}, {"os": "Windows"}, {"version": ARCHIVER.VERSION + 1}, {"version": True}):
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

    def test_hostile_transport_reference_fails_before_exposing_products(self):
        for target in ("../tracked.txt", f"{PREFIX}/missing.txt", f"{PREFIX}/alias.txt",
                       f"{PREFIX}/directory", f"{PREFIX}/second.txt", f"{PREFIX}/original.txt"):
            with self.subTest(target=target), tempfile.TemporaryDirectory(dir=ROOT) as temporary:
                root = Path(temporary)
                marker = root / "tracked.txt"
                marker.write_text("unchanged", encoding="utf-8")
                entries = [
                    member(f"{PREFIX}/original.txt"),
                    member(f"{PREFIX}/directory", tarfile.DIRTYPE),
                    member(f"{PREFIX}/alias.txt", tarfile.SYMTYPE, "original.txt"),
                    member(f"{PREFIX}/second.txt", tarfile.LNKTYPE, f"{PREFIX}/original.txt"),
                    member(f"{PREFIX}/copy.txt", tarfile.LNKTYPE, target),
                ]
                archive = self.write_archive(root, {"version": ARCHIVER.VERSION, "source": SOURCE, "os": "Linux"}, entries)
                tracked = {"tracked.txt", f"{PREFIX}/original.txt"} if target.endswith("/original.txt") else {"tracked.txt"}
                with self.assertRaises(ARCHIVER.ArchiveError):
                    ARCHIVER.restore(root, archive, SOURCE, "Linux", tracked)
                self.assertEqual(marker.read_text(encoding="utf-8"), "unchanged")
                self.assertFalse((root / "tests").exists())

    def test_transport_reference_cannot_reach_tarfile_link_extraction(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            with self.assertRaisesRegex(ARCHIVER.ArchiveError, "never extracted"):
                ARCHIVER.extraction_filter(
                    member(f"{PREFIX}/copy.txt", tarfile.LNKTYPE, f"{PREFIX}/original.txt"), temporary)

    @unittest.skipUnless(os.name == "posix", "Unix permission boundaries need a Unix runner.")
    def test_forward_reference_copies_before_restoring_restrictive_modes(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            entries = [
                member(f"{PREFIX}/restricted", tarfile.DIRTYPE, mode=0o500),
                member(f"{PREFIX}/restricted/copy.txt", tarfile.LNKTYPE, f"{PREFIX}/original.txt", 0o751),
                member(f"{PREFIX}/original.txt", mode=0o000),
            ]
            archive = self.write_archive(root, {}, entries)
            staging = root / "staging"
            with tarfile.open(archive, "r:gz") as incoming:
                payload = [entry for entry in incoming.getmembers() if entry.name != ARCHIVER.MANIFEST]
                ARCHIVER.validate_members(payload, "Linux", set())
                ARCHIVER.extract_payload(incoming, payload, staging)
            original = staging / PREFIX / "original.txt"
            copied = staging / PREFIX / "restricted/copy.txt"
            self.assertEqual(copied.read_text(encoding="utf-8"), "fixture text")
            self.assertEqual(original.stat().st_mode & 0o777, 0o000)
            self.assertEqual(copied.stat().st_mode & 0o777, 0o751)
            self.assertEqual(copied.parent.stat().st_mode & 0o777, 0o500)
            self.assertFalse(os.path.samefile(original, copied))

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


class TextTransportTests(unittest.TestCase):
    """Ordinary text exercises transport, never claims built-product completeness."""

    def write_transport(self, root):
        archive = root / "transport.tar.gz"
        paths = ARCHIVER.product_paths(root, "Linux")
        with tarfile.open(archive, "w:gz", compresslevel=1, dereference=False) as output:
            members = ARCHIVER.payload_members(output, root, paths)
            ARCHIVER.validate_members(members, "Linux", set())
            ARCHIVER.add_payload(output, paths, members)
        return archive

    def read_transport(self, root, archive):
        staging = root / "restored"
        with tarfile.open(archive, "r:gz") as incoming:
            members = incoming.getmembers()
            ARCHIVER.validate_members(members, "Linux", set())
            ARCHIVER.extract_payload(incoming, members, staging)
        return staging, members

    def test_equal_contents_across_runtime_roots_restore_as_independent_regular_files(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            contents = "ordinary repeated runtime transport text\n" * 8192
            names = [f"{directory}/transport.txt" for directory in ARCHIVER.OUTPUT_ROOTS]
            modes = (0o640, 0o751, 0o600, 0o644)
            for name, mode in zip(names, modes):
                path = root / name
                path.parent.mkdir(parents=True)
                path.write_text(contents, encoding="utf-8")
                path.chmod(mode)
            archive = self.write_transport(root)
            staging, members = self.read_transport(root, archive)
            regular = [entry for entry in members if entry.isfile()]
            references = [entry for entry in members if entry.islnk()]
            self.assertEqual(len(regular), 1)
            self.assertEqual(len(references), 3)
            self.assertEqual({entry.linkname for entry in references}, {regular[0].name})
            for name, mode in zip(names, modes):
                restored = staging / name
                self.assertTrue(restored.is_file())
                self.assertFalse(restored.is_symlink())
                self.assertEqual(restored.read_text(encoding="utf-8"), contents)
                self.assertEqual((root / name).read_text(encoding="utf-8"), contents)
                self.assertFalse(os.path.samefile(restored, root / name))
                if os.name == "posix":
                    self.assertEqual(restored.stat().st_mode & 0o777, mode)
                for other in names:
                    if other != name:
                        self.assertFalse(os.path.samefile(restored, staging / other))
            (staging / names[1]).write_text("independently modified text", encoding="utf-8")
            (staging / names[2]).unlink()
            self.assertEqual((staging / names[0]).read_text(encoding="utf-8"), contents)
            self.assertEqual((staging / names[3]).read_text(encoding="utf-8"), contents)
            report = io.StringIO()
            with redirect_stdout(report):
                ARCHIVER.summary("restore", archive, members)
            summary = json.loads(report.getvalue())
            self.assertEqual(summary["files"], 4)
            self.assertEqual(summary["transport_references"], 3)
            self.assertEqual(summary["logical_bytes"], len(contents.encode("utf-8")) * 4)
            self.assertEqual(summary["stored_bytes"], len(contents.encode("utf-8")))
            self.assertEqual(summary["compressed_bytes"], archive.stat().st_size)
            for entry in members:
                self.assertEqual((entry.uid, entry.gid, entry.uname, entry.gname), (0, 0, "", ""))

    def test_equal_sizes_different_contents_and_empty_text_keep_correct_identity(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            for directory in ARCHIVER.OUTPUT_ROOTS:
                (root / directory).mkdir(parents=True)
            expected = {"first.txt": "first", "other.txt": "other", "empty.txt": "", "empty-copy.txt": ""}
            for name, text in expected.items():
                (root / PREFIX / name).write_text(text, encoding="utf-8")
            archive = self.write_transport(root)
            staging, members = self.read_transport(root, archive)
            self.assertEqual(sum(entry.isfile() for entry in members), 3)
            self.assertEqual(sum(entry.islnk() for entry in members), 1)
            for name, text in expected.items():
                self.assertEqual((staging / PREFIX / name).read_text(encoding="utf-8"), text)
            self.assertFalse(os.path.samefile(staging / PREFIX / "empty.txt", staging / PREFIX / "empty-copy.txt"))

    @unittest.skipUnless(os.name == "posix", "Filesystem hardlinks and Unix modes need a Unix runner.")
    def test_accidental_source_hardlinks_and_symlinks_do_not_become_restored_hardlinks(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            for directory in ARCHIVER.OUTPUT_ROOTS:
                (root / directory).mkdir(parents=True)
            original = root / PREFIX / "original.txt"
            original.write_text("ordinary hardlinked text fixture", encoding="utf-8")
            hardlink = root / PREFIX / "hardlink.txt"
            os.link(original, hardlink)
            (root / PREFIX / "symbolic.txt").symlink_to("hardlink.txt")
            archive = self.write_transport(root)
            staging, members = self.read_transport(root, archive)
            self.assertEqual(sum(entry.islnk() for entry in members), 1)
            self.assertTrue(os.path.samefile(original, hardlink))
            self.assertFalse(os.path.samefile(staging / PREFIX / "original.txt", staging / PREFIX / "hardlink.txt"))
            self.assertTrue((staging / PREFIX / "symbolic.txt").is_symlink())
            self.assertEqual((staging / PREFIX / "symbolic.txt").read_text(encoding="utf-8"),
                             "ordinary hardlinked text fixture")
            (staging / PREFIX / "hardlink.txt").write_text("independent replacement", encoding="utf-8")
            self.assertEqual((staging / PREFIX / "original.txt").read_text(encoding="utf-8"),
                             "ordinary hardlinked text fixture")
            self.assertEqual(original.read_text(encoding="utf-8"), "ordinary hardlinked text fixture")


if __name__ == "__main__":
    unittest.main()