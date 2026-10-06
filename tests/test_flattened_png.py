from contextlib import contextmanager
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image, PngImagePlugin

from court_creator import court_import, export_io


class FlattenedPngTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="court-png-pin-")
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.png, self.base, self.tool, self.output = (self.root / name for name in
                                                     ("court.png", "base.iff", "texconv.exe", "output.iff"))
        self.size = (32, 16)
        self.write_png(self.png)
        self.tool.write_bytes(b"private converter fixture")
        self.output.write_bytes(b"last good IFF")
        with ZipFile(self.base, "w") as archive:
            archive.writestr("floor.dds", b"old DDS")
            archive.writestr("mesh.bin", b"unchanged geometry")
            archive.comment = b"unchanged comment"
        self.selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}
        self.scratch = []
        self.encoder_input = None
        self.before = self.snapshot(self.png, self.base, self.tool, self.output)

    def write_png(self, path, color=(20, 40, 60, 255), size=None):
        with Image.new("RGBA", size or self.size, color) as image:
            image.save(path, "PNG", compress_level=0)

    def snapshot(self, *paths):
        return {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in paths}

    def change_same_size_time(self, path):
        stamp, size = path.stat().st_mtime_ns, path.stat().st_size
        self.write_png(path, (80, 90, 100, 255))
        self.assertEqual(path.stat().st_size, size)
        os.utime(path, ns=(stamp, stamp))

    @contextmanager
    def converter(self, change=None):
        def run(args, **_kwargs):
            source = Path(args[-1])
            self.encoder_input = source
            self.scratch.append(source.parent)
            self.assertEqual(source.read_bytes(), self.before[self.png][0])
            with Image.open(source) as image:
                self.assertEqual((image.format, image.size, image.getpixel((0, 0))),
                                 ("PNG", self.size, (20, 40, 60, 255)))
            if change:
                change(source)
            (source.parent / "court.DDS").write_bytes(b"encoded placeholder")
            return subprocess.CompletedProcess(args, 0, "", "")
        with patch.object(court_import, "OUTPUT_SIZE", self.size), patch.object(
                court_import, "inspect_iff", return_value={"textures": [self.selected]}), patch.object(
                court_import, "run_tool", side_effect=run) as encoder, patch.object(
                court_import, "_read_encoded_dds", return_value=b"new DDS"):
            yield encoder

    def export(self):
        return court_import.package_png_into_iff(self.png, self.base, "floor.dds", self.output, self.tool)

    def assert_previous_intact(self, *, source_changed=False):
        retained = (self.base, self.tool, self.output) if source_changed else tuple(self.before)
        self.assertEqual(self.snapshot(*retained), {path: self.before[path] for path in retained})
        self.assertFalse(list(self.root.glob("*.tmp")))
        self.assertTrue(all(not directory.exists() for directory in self.scratch))

    def test_valid_png_encoder_copy_and_archive_preserve_source_bytes_and_time(self):
        with self.converter():
            self.assertEqual(self.export(), self.output)
        self.assertEqual(self.snapshot(self.png, self.base, self.tool),
                         {path: self.before[path] for path in (self.png, self.base, self.tool)})
        with ZipFile(self.output) as archive:
            self.assertEqual(archive.read("floor.dds"), b"new DDS")
            self.assertEqual(archive.read("mesh.bin"), b"unchanged geometry")
            self.assertEqual(archive.comment, b"unchanged comment")
            self.assertIsNone(archive.testzip())
        self.assertTrue(all(not directory.exists() for directory in self.scratch))

    def test_runner_failures_preserve_last_good_export_and_remove_encoder_scratch(self):
        failures = (subprocess.TimeoutExpired("private encoder", .1, output="last diagnostic"),
                    RuntimeError("private encoder output exceeded its safety limit"),
                    OSError("private diagnostic pipe failure"))
        for failure in failures:
            with self.subTest(failure=type(failure).__name__), self.converter() as run:
                def fail(args, **kwargs):
                    self.assertEqual(kwargs["timeout"], 600)
                    self.assertEqual(kwargs["label"], "DDS converter")
                    copied = Path(args[-1])
                    self.scratch.append(copied.parent)
                    self.assertEqual(copied.read_bytes(), self.before[self.png][0])
                    raise failure
                run.side_effect = fail
                with self.assertRaises(type(failure)):
                    self.export()
            self.assert_previous_intact()

    def test_borrowed_hardlinked_png_is_supported_and_never_modified(self):
        alias = self.root / "borrowed-alias.png"
        os.link(self.png, alias)
        before = self.snapshot(alias)
        with self.converter():
            self.export()
        self.assertEqual(self.snapshot(alias), before)
        self.assertEqual(self.snapshot(self.png), {self.png: self.before[self.png]})
        self.assertTrue(self.png.samefile(alias))

    def test_source_changes_between_snapshot_and_copy_reject_before_encoder(self):
        actual_copy = court_import._copy_file_snapshot
        changed = {}
        def copy(source, *args, **kwargs):
            if source == self.png:
                self.change_same_size_time(source)
                changed.update(self.snapshot(source))
            return actual_copy(source, *args, **kwargs)
        with self.converter() as run, patch.object(court_import, "_copy_file_snapshot", side_effect=copy):
            with self.assertRaisesRegex(ValueError, "flattened court texture.*changed"):
                self.export()
            run.assert_not_called()
        self.assert_previous_intact(source_changed=True)
        self.assertEqual(self.snapshot(self.png), changed)

    def test_source_edit_during_png_validation_is_rejected_before_encoder(self):
        actual_open = Image.open
        changed = {}
        def open_image(path, *args, **kwargs):
            image = actual_open(path, *args, **kwargs)
            self.change_same_size_time(self.png)
            changed.update(self.snapshot(self.png))
            return image
        with self.converter() as run, patch.object(court_import.Image, "open", side_effect=open_image):
            with self.assertRaisesRegex(ValueError, "flattened court texture.*changed"):
                self.export()
            run.assert_not_called()
        self.assert_previous_intact(source_changed=True)
        self.assertEqual(self.snapshot(self.png), changed)

    def test_private_input_edit_during_validation_is_rejected_before_encoder(self):
        actual_verify = PngImagePlugin.PngImageFile.verify
        def verify_png(image):
            result = actual_verify(image)
            self.change_same_size_time(Path(image.filename))
            return result
        with self.converter() as run, patch.object(PngImagePlugin.PngImageFile, "verify", verify_png):
            with self.assertRaisesRegex(ValueError, "flattened court texture.*changed"):
                self.export()
            run.assert_not_called()
        self.assert_previous_intact()

    def test_converter_time_source_and_private_changes_cannot_publish(self):
        for mode in ("source-edit", "source-replace", "private-edit", "private-replace", "private-link"):
            with self.subTest(mode=mode):
                self.png.write_bytes(self.before[self.png][0])
                os.utime(self.png, ns=(self.before[self.png][1], self.before[self.png][1]))
                foreign = self.root / "foreign.png"
                def change(copied):
                    target = self.png if mode.startswith("source") else copied
                    if mode.endswith("edit"):
                        self.change_same_size_time(target)
                    elif mode.endswith("replace"):
                        stamp = target.stat().st_mtime_ns
                        replacement = target.with_suffix(".new")
                        replacement.write_bytes(target.read_bytes())
                        os.utime(replacement, ns=(stamp, stamp))
                        os.replace(replacement, target)
                    else:
                        os.link(target, foreign)
                failure = (ValueError, PermissionError) if mode == "source-replace" else ValueError
                with self.converter(change), self.assertRaises(failure) as rejected:
                    self.export()
                if isinstance(rejected.exception, ValueError):
                    self.assertRegex(str(rejected.exception), "flattened court texture.*changed|ordinary unshared")
                else:
                    self.assertEqual(self.png.read_bytes(), self.before[self.png][0])
                self.assert_previous_intact(source_changed=mode.startswith("source"))
                if foreign.exists():
                    self.assertEqual(foreign.read_bytes(), self.before[self.png][0])
                    foreign.unlink()

    def test_archive_copy_time_source_change_keeps_previous_export(self):
        actual_copy = court_import._copy_iff_entry
        changed = {}
        def copy(*args, **kwargs):
            result = actual_copy(*args, **kwargs)
            if args[2].filename == "mesh.bin":
                self.change_same_size_time(self.png)
                changed.update(self.snapshot(self.png))
            return result
        with self.converter(), patch.object(court_import, "_copy_iff_entry", side_effect=copy):
            with self.assertRaisesRegex(ValueError, "flattened court texture.*changed"):
                self.export()
        self.assert_previous_intact(source_changed=True)
        self.assertEqual(self.snapshot(self.png), changed)

    @unittest.skipUnless(os.name == "nt", "Windows publication retries")
    def test_retry_time_source_edit_stops_before_second_publication(self):
        error = OSError(13, "private sharing failure")
        error.winerror = 32
        changed = {}
        def change(_delay):
            self.change_same_size_time(self.png)
            changed.update(self.snapshot(self.png))
        with self.converter(), patch.object(export_io.os, "replace", side_effect=error) as replace, patch.object(
                export_io.time, "sleep", side_effect=change) as sleep:
            with self.assertRaisesRegex(ValueError, "flattened court texture.*changed"):
                self.export()
            replace.assert_called_once()
            sleep.assert_called_once_with(.025)
        self.assert_previous_intact(source_changed=True)
        self.assertEqual(self.snapshot(self.png), changed)

    def test_wrong_format_dimensions_corrupt_and_incomplete_png_reject_before_encoder(self):
        for mode in ("bmp", "dimensions", "crc", "truncated", "invalid"):
            with self.subTest(mode=mode):
                self.png.write_bytes(self.before[self.png][0])
                if mode == "bmp":
                    with Image.new("RGB", self.size) as image:
                        image.save(self.png, "BMP")
                elif mode == "dimensions":
                    self.write_png(self.png, size=(64, 16))
                elif mode == "crc":
                    data = bytearray(self.png.read_bytes()); data[45] ^= 1; self.png.write_bytes(data)
                elif mode == "truncated":
                    self.png.write_bytes(self.png.read_bytes()[:100])
                else:
                    self.png.write_bytes(b"not image data")
                changed = self.snapshot(self.png)
                with self.converter() as run, self.assertRaisesRegex(ValueError, "PNG"):
                    self.export()
                run.assert_not_called()
                self.assert_previous_intact(source_changed=True)
                self.assertEqual(self.snapshot(self.png), changed)

    def test_exact_byte_budget_and_oversize_preflight(self):
        count = self.png.stat().st_size
        copied = self.root / "copy.png"
        with patch.object(court_import, "MAX_ASSET_BYTES", count), patch.object(court_import, "OUTPUT_SIZE", self.size):
            with court_import._pinned_flattened_png(self.png, copied) as validate:
                validate()
                self.assertEqual(copied.read_bytes(), self.before[self.png][0])
        copied.unlink()
        with self.converter() as run, patch.object(court_import, "MAX_ASSET_BYTES", count - 1), patch.object(
                court_import, "_copy_file_snapshot", side_effect=AssertionError("Oversized PNG was copied")):
            with self.assertRaisesRegex(ValueError, "file size limit"):
                self.export()
            run.assert_not_called()
        self.assert_previous_intact()

    def test_private_copy_create_new_never_overwrites_existing_file(self):
        copied = self.root / "copy.png"
        copied.write_bytes(b"personal existing file")
        before = self.snapshot(copied)
        with patch.object(court_import, "OUTPUT_SIZE", self.size), self.assertRaises(FileExistsError):
            with court_import._pinned_flattened_png(self.png, copied):
                self.fail("Existing copy destination was accepted.")
        self.assertEqual(self.snapshot(copied), before)
        self.assert_previous_intact()

    def test_source_growth_during_copy_stays_bounded_and_keeps_previous_export(self):
        actual_open = Path.open
        reads = 0
        budget = self.png.stat().st_size + 64
        class GrowingStream:
            def __init__(inner, stream):
                inner.stream = stream
            def __getattr__(inner, name):
                return getattr(inner.stream, name)
            def __enter__(inner):
                return inner
            def __exit__(inner, *args):
                return inner.stream.__exit__(*args)
            def read(inner, size):
                self.assertGreater(size, 0)
                self.assertLessEqual(size, 1024 * 1024)
                with actual_open(self.png, "ab") as stream:
                    stream.write(b"X" * 65)
                return inner.stream.read(size)
        def open_file(path, mode="r", *args, **kwargs):
            nonlocal reads
            stream = actual_open(path, mode, *args, **kwargs)
            if path == self.png and mode == "rb":
                reads += 1
                if reads == 2:
                    return GrowingStream(stream)
            return stream
        with self.converter() as run, patch.object(court_import, "MAX_ASSET_BYTES", budget), patch.object(Path, "open", open_file):
            with self.assertRaisesRegex(ValueError, "grew beyond.*copy size limit"):
                self.export()
            run.assert_not_called()
        self.assertEqual(reads, 2)
        self.assertEqual(self.png.stat().st_size, budget + 1)
        self.assert_previous_intact(source_changed=True)


if __name__ == "__main__":
    unittest.main()
