import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import updater
from tools import build_release as packaging


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="court-release-test-")
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        for name in (*packaging.ROOT_FILES, *["tools/" + name for name in packaging.TOOL_FILES]):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"release input sentinel")
        (self.root / "package.json").write_text(json.dumps({"version": "1.0.0"}))
        (self.root / "studio-build.json").write_text(json.dumps({"runtime": "wpf-net8"}))
        (self.root / "updater.py").write_bytes(Path(updater.__file__).read_bytes())
        (self.root / "desktop").mkdir()
        for name in updater.NATIVE_FILES:
            (self.root / "desktop" / name).write_bytes(b"native artifact sentinel")
        (self.root / "desktop/NBA2KCourtCreator.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"frameworks": [
            {"name": name, "version": "8.0.0"} for name in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")
        ]}}))
        (self.root / "court_creator/__pycache__").mkdir(parents=True)
        (self.root / "court_creator/backend.py").write_bytes(b"backend source")
        (self.root / "court_creator/__pycache__/ignored.pyc").write_bytes(b"not for release")
        (self.root / "data/palette_sources").mkdir(parents=True)
        (self.root / "data/palette_sources/source.json").write_bytes(b"managed source provenance")
        for name in ("court_presets.json", "game_installation.json", "update_config.json"):
            (self.root / "data" / name).write_bytes(b"personal data")
        (self.root / "runtime/python").mkdir(parents=True)
        (self.root / "runtime/python/python.exe").write_bytes(b"owned runtime")
        (self.root / "logos").mkdir()
        (self.root / "logos/personal.png").write_bytes(b"personal artwork")
        self.output = packaging.build(self.root)
        self.before = (self.output.read_bytes(), self.output.stat().st_mtime_ns)
        self.foreign = self.output.parent / ".court-release-personal-note.tmp"
        self.foreign.write_bytes(b"foreign temporary file")

    def assert_previous_intact(self):
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), self.before)
        self.assertEqual(self.foreign.read_bytes(), b"foreign temporary file")
        self.assertEqual(set(self.output.parent.glob(".court-release-*.tmp")), {self.foreign})

    def test_complete_archive_has_managed_catalog_but_no_runtime_artwork_presets_or_cache(self):
        with zipfile.ZipFile(self.output) as archive:
            names = set(archive.namelist())
            self.assertTrue({"data/team_palettes.json", "data/palette_sources/source.json", "tools/setup_court_creator.py", "updater.py"}.issubset(names))
            self.assertTrue({"desktop/" + name for name in updater.NATIVE_FILES}.issubset(names))
            self.assertFalse(any(name.startswith(("runtime/", "logos/", "templates/")) or "__pycache__" in name for name in names))
            for name in ("court_presets.json", "game_installation.json", "update_config.json"):
                self.assertNotIn("data/" + name, names)
            self.assertIsNone(archive.testzip())
            for name in names:
                self.assertEqual(hashlib.sha256(archive.read(name)).digest(), hashlib.sha256((self.root / name).read_bytes()).digest())

    def test_missing_inputs_and_incomplete_native_build_preserve_previous_archive(self):
        for path in (self.root / "tools/setup_court_creator.py", self.root / "desktop/TwoK.Studio.dll"):
            content = path.read_bytes()
            path.unlink()
            try:
                with self.assertRaises(ValueError):
                    packaging.build(self.root)
                self.assert_previous_intact()
            finally:
                path.write_bytes(content)

    def test_incompatible_runtime_metadata_preserves_previous_archive(self):
        path = self.root / "desktop/NBA2KCourtCreator.runtimeconfig.json"
        path.write_text('{"runtimeOptions":{"frameworks":[]}}')
        with self.assertRaisesRegex(ValueError, "runtime configuration"):
            packaging.build(self.root)
        self.assert_previous_intact()

    def test_verification_and_atomic_publish_failures_leave_old_archive_and_foreign_temp(self):
        with patch.object(packaging, "verify_archive", side_effect=ValueError("controlled ZIP verification failure")):
            with self.assertRaisesRegex(ValueError, "verification failure"):
                packaging.build(self.root)
        self.assert_previous_intact()
        with patch.object(packaging.os, "replace", side_effect=PermissionError("controlled archive publication denial")):
            with self.assertRaises(PermissionError):
                packaging.build(self.root)
        self.assert_previous_intact()

    def test_same_size_timestamp_input_changes_after_zip_write_are_rejected(self):
        source = self.root / "court_creator/backend.py"
        metadata = source.stat()
        verify = packaging.verify_archive
        def mutate(path, records):
            verify(path, records)
            source.write_bytes(b"X" * metadata.st_size)
            os.utime(source, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
        with patch.object(packaging, "verify_archive", side_effect=mutate):
            with self.assertRaisesRegex(ValueError, "changed after packaging"):
                packaging.build(self.root)
        self.assert_previous_intact()

    def test_input_list_changes_after_packaging_are_rejected(self):
        extra = self.root / "desktop/new.dll"
        verify = packaging.verify_archive
        def add_file(path, records):
            verify(path, records)
            extra.write_bytes(b"new input added mid-package")
        with patch.object(packaging, "verify_archive", side_effect=add_file):
            with self.assertRaisesRegex(ValueError, "file list changed"):
                packaging.build(self.root)
        self.assert_previous_intact()

    def test_file_entry_directory_expanded_and_compressed_budgets_preserve_old_archive(self):
        for setting, limit in (("MAX_FILE_BYTES", 10), ("MAX_ENTRIES", 4), ("MAX_DIRECTORY_BYTES", 10),
                               ("MAX_EXPANDED_BYTES", 10), ("MAX_ARCHIVE_BYTES", 200)):
            with self.subTest(setting=setting), patch.object(updater, setting, limit):
                with self.assertRaises(ValueError):
                    packaging.build(self.root)
            self.assert_previous_intact()

    def test_output_source_collisions_and_hard_links_are_rejected(self):
        source = self.root / "package.json"
        before = source.read_bytes()
        with self.assertRaisesRegex(ValueError, "source file"):
            packaging.build(self.root, source)
        alias = self.root / "outputs/alias.zip"
        os.link(source, alias)
        with self.assertRaisesRegex(ValueError, "Hard-linked"):
            packaging.build(self.root, alias)
        self.assertEqual(source.read_bytes(), before)
        self.assertEqual(alias.read_bytes(), before)
        self.assert_previous_intact()

    @unittest.skipUnless(os.name == "nt", "Windows junction guards")
    def test_linked_source_and_output_folders_do_not_touch_external_files(self):
        import _winapi
        with tempfile.TemporaryDirectory(prefix="court-release-external-") as external:
            external = Path(external)
            sentinel = external / "personal.zip"
            sentinel.write_bytes(b"external personal file")
            source_link = self.root / "court_creator/linked"
            _winapi.CreateJunction(str(external), str(source_link))
            try:
                with self.assertRaisesRegex(ValueError, "outside|Linked"):
                    packaging.build(self.root)
                self.assert_previous_intact()
            finally:
                source_link.rmdir()
            output_link = self.root / "linked-output"
            _winapi.CreateJunction(str(external), str(output_link))
            try:
                with self.assertRaisesRegex(ValueError, "outside|Linked"):
                    packaging.build(self.root, output_link / "personal.zip")
                self.assertEqual(sentinel.read_bytes(), b"external personal file")
                self.assert_previous_intact()
            finally:
                output_link.rmdir()

    def test_import_does_not_build_and_cli_relative_output_is_project_relative(self):
        script = self.root / "tools/build_release.py"
        script.write_bytes(Path(packaging.__file__).read_bytes())
        command = [sys.executable, "-I", "-B", "-c", "import sys; sys.path.insert(0, sys.argv[1]); from tools import build_release; print('imported')", str(self.root)]
        result = subprocess.run(command, capture_output=True, text=True, timeout=30, check=True,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertIn("imported", result.stdout)
        self.assert_previous_intact()
        subprocess.run([sys.executable, "-I", "-B", str(script), "--project-root", str(self.root), "--output", "outputs/second.zip"],
                       capture_output=True, text=True, timeout=30, check=True,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertTrue((self.root / "outputs/second.zip").is_file())
        self.assert_previous_intact()


if __name__ == "__main__":
    unittest.main()
