from contextlib import contextmanager
import copy
import os
from pathlib import Path
import subprocess
import struct
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile, ZIP_DEFLATED

from court_creator import court_import, export_io


class IffExportBaseTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="court-iff-base-")
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.png, self.base, self.tool, self.output = (self.root / name for name in ("court.png", "base.iff", "texconv.exe", "output.iff"))
        self.png.write_bytes(b"input artwork")
        self.tool.write_bytes(b"converter")
        self.output.write_bytes(b"previous export")
        self.write_base()
        self.selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}

    def write_base(self, geometry=b"original geometry", comment=b"original comment"):
        with ZipFile(self.base, "w", compression=ZIP_DEFLATED) as archive:
            archive.writestr("floor.dds", b"old DDS")
            archive.writestr("mesh.bin", geometry)
            archive.writestr("level_floor.SCNE", b"original scene")
            archive.comment = comment

    def snapshot(self, *paths):
        return {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in paths}

    @contextmanager
    def converter(self, change=None):
        def convert(args, **_kwargs):
            if change:
                change()
            (Path(args[args.index("-o") + 1]) / "court.DDS").write_bytes(b"encoded placeholder")
            return subprocess.CompletedProcess(args, 0, "", "")
        with patch.object(court_import, "inspect_iff", return_value={"textures": [self.selected]}), patch.object(
                court_import.Image, "open") as image, patch.object(court_import, "run_tool", side_effect=convert) as run, patch.object(
                court_import, "_read_encoded_dds", return_value=b"converted DDS"):
            image.return_value.__enter__.return_value.size = court_import.OUTPUT_SIZE
            image.return_value.__enter__.return_value.format = "PNG"
            yield run

    def export(self):
        return court_import.package_png_into_iff(self.png, self.base, "floor.dds", self.output, self.tool)

    def test_converter_time_base_edit_with_identical_size_and_timestamp_is_rejected(self):
        before = self.snapshot(self.png, self.tool, self.output)
        original_size, original_time = self.base.stat().st_size, self.base.stat().st_mtime_ns
        changed = {}
        def change():
            self.write_base(comment=b"modified comment")
            self.assertEqual(self.base.stat().st_size, original_size)
            os.utime(self.base, ns=(original_time, original_time))
            changed.update(self.snapshot(self.base))
        with self.converter(change), self.assertRaisesRegex(ValueError, "export base.*changed"):
            self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertEqual(self.snapshot(self.base), changed)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_converter_time_geometry_edit_with_identical_size_and_timestamp_is_rejected(self):
        before = self.snapshot(self.png, self.tool, self.output)
        original_size, original_time = self.base.stat().st_size, self.base.stat().st_mtime_ns
        changed = {}
        def change():
            self.write_base(geometry=b"modified geometry")
            self.assertEqual(self.base.stat().st_size, original_size)
            os.utime(self.base, ns=(original_time, original_time))
            changed.update(self.snapshot(self.base))
        with self.converter(change), self.assertRaisesRegex(ValueError, "export base.*changed"):
            self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertEqual(self.snapshot(self.base), changed)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_converter_time_flattened_png_edit_is_rejected_without_replacing_export(self):
        before = self.snapshot(self.base, self.tool, self.output)
        stamp = self.png.stat().st_mtime_ns
        changed = {}
        def change():
            self.png.write_bytes(b"other artwork")
            os.utime(self.png, ns=(stamp, stamp))
            changed.update(self.snapshot(self.png))
        with self.converter(change), self.assertRaisesRegex(ValueError, "flattened court texture.*changed"):
            self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertEqual(self.snapshot(self.png), changed)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_inflated_archive_budget_is_checked_before_converter_work(self):
        self.write_base(geometry=b"\0" * 100000)
        self.assertLess(self.base.stat().st_size, 4096)
        before = self.snapshot(self.png, self.base, self.tool, self.output)
        with self.converter() as run, patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", 4096):
            with self.assertRaisesRegex(ValueError, "archive size limit"):
                self.export()
            run.assert_not_called()
        self.assertEqual(self.snapshot(*before), before)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_compressed_budget_is_checked_before_archive_open(self):
        before = self.snapshot(self.png, self.base, self.tool, self.output)
        with self.converter() as run, patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", self.base.stat().st_size - 1), patch.object(
                Path, "open", side_effect=AssertionError("Oversized base was opened")):
            with self.assertRaisesRegex(ValueError, "archive size limit"):
                self.export()
            run.assert_not_called()
        self.assertEqual(self.snapshot(*before), before)

    def test_inspection_time_change_is_rejected_before_conversion(self):
        before = self.snapshot(self.png, self.tool, self.output)
        def inspect(*_args, **_kwargs):
            self.write_base(comment=b"modified comment")
            return {"textures": [self.selected]}
        with self.converter() as run, patch.object(court_import, "inspect_iff", side_effect=inspect):
            with self.assertRaisesRegex(ValueError, "export base.*changed"):
                self.export()
            run.assert_not_called()
        self.assertEqual(self.snapshot(*before), before)
        with ZipFile(self.base) as archive:
            self.assertEqual(archive.comment, b"modified comment")

    def test_directory_time_base_change_is_rejected_before_conversion(self):
        before = self.snapshot(self.png, self.tool, self.output)
        actual_unique = court_import._unique_entries
        changed = False
        def unique(archive):
            nonlocal changed
            actual_unique(archive)
            if not changed:
                changed = True
                self.write_base(comment=b"modified comment")
        with self.converter() as run, patch.object(court_import, "_unique_entries", side_effect=unique):
            with self.assertRaisesRegex(ValueError, "export base.*changed"):
                self.export()
            run.assert_not_called()
        self.assertTrue(changed)
        self.assertEqual(self.snapshot(*before), before)
        with ZipFile(self.base) as archive:
            self.assertEqual(archive.comment, b"modified comment")

    def test_copy_time_base_change_cannot_publish(self):
        before = self.snapshot(self.png, self.tool, self.output)
        actual_copy = court_import._copy_iff_entry
        changed = {}
        def copy_entry(*args, **kwargs):
            result = actual_copy(*args, **kwargs)
            if args[2].filename == "mesh.bin":
                stamp = self.base.stat().st_mtime_ns
                self.write_base(comment=b"modified comment")
                os.utime(self.base, ns=(stamp, stamp))
                changed.update(self.snapshot(self.base))
            return result
        with self.converter(), patch.object(court_import, "_copy_iff_entry", side_effect=copy_entry):
            with self.assertRaisesRegex(ValueError, "export base.*changed"):
                self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertEqual(self.snapshot(self.base), changed)
        self.assertFalse(list(self.root.glob("*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Windows publication retries")
    def test_retry_time_base_change_stops_before_second_publication(self):
        before = self.snapshot(self.png, self.tool, self.output)
        original_time = self.base.stat().st_mtime_ns
        failure = OSError(13, "controlled sharing failure")
        failure.winerror = 32
        changed = {}
        def change(_delay):
            self.write_base(comment=b"modified comment")
            os.utime(self.base, ns=(original_time, original_time))
            changed.update(self.snapshot(self.base))
        with self.converter(), patch.object(export_io.os, "replace", side_effect=failure) as replace, patch.object(
                export_io.time, "sleep", side_effect=change) as sleep:
            with self.assertRaisesRegex(ValueError, "export base.*changed"):
                self.export()
            replace.assert_called_once()
            sleep.assert_called_once_with(.025)
        self.assertEqual(self.snapshot(*before), before)
        self.assertEqual(self.snapshot(self.base), changed)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_projected_payload_budget_is_checked_before_staging(self):
        before = self.snapshot(self.png, self.base, self.tool, self.output)
        with self.converter(), patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", 4096), patch.object(
                court_import, "_read_encoded_dds", return_value=b"\0" * 4097), patch.object(
                court_import, "staged_export", side_effect=AssertionError("Oversized export was staged")):
            with self.assertRaisesRegex(ValueError, "inflated archive size limit"):
                self.export()
        self.assertEqual(self.snapshot(*before), before)

    def test_generated_compressed_budget_is_checked_before_crc_inflation(self):
        before = self.snapshot(self.png, self.base, self.tool, self.output)
        actual_copy = court_import._copy_iff_entry
        def copy_entry(original, converted, item, replacement=None):
            copied = copy.copy(item)
            if item.filename == "floor.dds":
                copied.extra = struct.pack("<HH", 0xCAFE, 4000) + b"X" * 4000
            return actual_copy(original, converted, copied, replacement)
        with self.converter(), patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", 4096), patch.object(
                court_import, "_copy_iff_entry", side_effect=copy_entry), patch.object(
                ZipFile, "testzip", side_effect=AssertionError("Oversized archive reached CRC inflation")):
            with self.assertRaisesRegex(ValueError, "archive size limit"):
                self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_exact_archive_budget_preserves_unmodified_entries_and_sources(self):
        before = self.snapshot(self.png, self.base, self.tool)
        with self.converter():
            self.export()
        result = self.output.read_bytes()
        budget = max(self.base.stat().st_size, len(result))
        self.output.write_bytes(b"previous export")
        with self.converter(), patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", budget):
            self.export()
        self.assertEqual(self.output.read_bytes(), result)
        self.assertEqual(self.snapshot(*before), before)
        with ZipFile(self.output) as archive:
            self.assertEqual(archive.read("floor.dds"), b"converted DDS")
            self.assertEqual(archive.read("mesh.bin"), b"original geometry")
            self.assertEqual(archive.read("level_floor.SCNE"), b"original scene")
            self.assertEqual(archive.comment, b"original comment")
            self.assertIsNone(archive.testzip())
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_read_only_base_hard_links_remain_supported_and_untouched(self):
        original = self.base
        alias = self.root / "base-alias.iff"
        os.link(original, alias)
        before = self.snapshot(self.png, original, alias, self.tool)
        self.base = alias
        with self.converter():
            self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertTrue(alias.samefile(original))
        self.assertFalse(list(self.root.glob("*.tmp")))

    def test_snapshot_reads_are_bounded_and_file_growth_is_rejected(self):
        path = self.root / "revision.bin"
        path.write_bytes(b"original")
        with path.open("rb", buffering=0) as stream:
            class GrowingReader:
                def __getattr__(self, name):
                    return getattr(stream, name)
                def read(self, size):
                    if size != 1025:
                        raise AssertionError("Snapshot read was not bounded")
                    path.write_bytes(b"changed" * 200)
                    return stream.read(size)
            with patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", 1024):
                with self.assertRaisesRegex(ValueError, "grew beyond.*archive size limit"):
                    court_import._export_base_revision(path, GrowingReader())
        self.assertEqual(path.read_bytes(), b"changed" * 200)

    def test_missing_scene_is_rejected_before_converter_work(self):
        with ZipFile(self.base, "w") as archive:
            archive.writestr("floor.dds", b"old DDS")
        before = self.snapshot(self.png, self.base, self.tool, self.output)
        scene = b'{"Scene":{"Model":{"floor":{"Prim":[{"Mesh":"full_court_floor"}]}}}}'
        with self.converter() as run:
            with self.assertRaisesRegex(ValueError, "no floor scene"):
                court_import.package_png_into_iff(self.png, self.base, "floor.dds", self.output, self.tool, scene_override=scene)
            run.assert_not_called()
        self.assertEqual(self.snapshot(*before), before)

    def test_foreign_shared_staging_is_rejected_before_any_truncation(self):
        personal = self.root / "personal.bin"
        personal.write_bytes(b"never replace personal content")
        before = self.snapshot(self.png, self.base, self.tool, self.output, personal)
        retained = self.root / "retained-owned.tmp"
        foreign = []
        @contextmanager
        def substitute(*args, **kwargs):
            with export_io.staged_export(*args, **kwargs) as owned:
                stage = Path(owned.name) if kwargs.get("writable_stream") else owned
                if kwargs.get("writable_stream"):
                    owned.close()
                stage.rename(retained)
                os.link(personal, stage)
                foreign.append(stage)
                if kwargs.get("writable_stream"):
                    with stage.open("r+b") as replacement:
                        yield replacement
                else:
                    yield stage
        with self.converter(), patch.object(court_import, "staged_export", substitute), self.assertRaisesRegex(OSError, "identity changed|became shared"):
            self.export()
        self.assertEqual(self.snapshot(*before), before)
        self.assertEqual(len(foreign), 1)
        self.assertTrue(foreign[0].samefile(personal))
        self.assertEqual(retained.read_bytes(), b"")


if __name__ == "__main__":
    unittest.main()
