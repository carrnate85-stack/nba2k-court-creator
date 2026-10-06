from io import BytesIO
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch
import warnings
import zipfile

from court_creator import court_import
from tests.test_court_import import solid_dds
from tests.test_stock_extraction import write_floor


class IffArchiveTests(unittest.TestCase):
    def test_directory_budget_precedes_underlying_read(self):
        stream = Mock()
        reader = court_import._BoundedArchiveReader(stream)
        for size in (-1, court_import.MAX_IFF_DIRECTORY_BYTES + 1):
            with self.assertRaisesRegex(ValueError, "directory"):
                reader.read(size)
        stream.read.assert_not_called()

    def test_forged_directory_size_is_rejected_before_allocation(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "forged.iff"
            # A plausible directory location makes ZipFile reach its eager read.
            end = struct.pack(zipfile.structEndArchive, zipfile.stringEndArchive,
                              0, 0, 1, 1, 513, 0, 0)
            path.write_bytes(b"\0" * 513 + end)
            with patch.object(court_import, "MAX_IFF_DIRECTORY_BYTES", 512), patch.object(zipfile, "ZipInfo") as entry:
                with self.assertRaisesRegex(ValueError, "directory"):
                    with court_import.open_iff(path):
                        self.fail("Unsafe archive opened")
                entry.assert_not_called()
            path.rename(path.with_suffix(".closed"))

    def test_actual_entry_count_is_checked_before_zipinfo_allocations(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "many.iff"
            with zipfile.ZipFile(path, "w") as archive:
                for index in range(4):
                    archive.writestr(str(index), b"content")
            data = bytearray(path.read_bytes())
            end = len(data) - zipfile.sizeEndCentDir
            # Do not trust the end record's advertised count.
            struct.pack_into("<HH", data, end + 8, 1, 1)
            path.write_bytes(data)
            with patch.object(court_import, "MAX_IFF_ENTRIES", 3), patch.object(zipfile, "ZipInfo") as entry:
                with self.assertRaisesRegex(ValueError, "too many entries"):
                    with court_import.open_iff(path):
                        self.fail("Unsafe archive opened")
                entry.assert_not_called()
            path.unlink()

    def test_zip64_comments_prefixes_and_large_members_remain_supported(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "valid.iff"
            payload = b"original pixels\0" * 10000
            stream = BytesIO(b"IFF-compatible prefix")
            stream.seek(0, 2)
            with patch.object(zipfile, "ZIP64_LIMIT", 1):
                with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                    archive.writestr("texture.bin", payload)
                    archive.comment = b"a" * zipfile.ZIP_MAX_COMMENT
            path.write_bytes(stream.getvalue())
            with patch.object(court_import, "MAX_IFF_DIRECTORY_BYTES", 65557):
                with court_import.open_iff(path) as archive:
                    self.assertEqual(archive.comment, b"a" * zipfile.ZIP_MAX_COMMENT)
                    self.assertEqual(archive.read("texture.bin"), payload)
                    self.assertIsNone(archive.testzip())

    def test_duplicate_and_unreadable_archives_release_handles(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "invalid.iff"
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr("same", b"first")
                    archive.writestr("same", b"second")
            with self.assertRaisesRegex(ValueError, "duplicate"):
                with court_import.open_iff(path):
                    self.fail("Ambiguous archive opened")
            path.write_bytes(b"not an archive")
            with self.assertRaisesRegex(ValueError, "readable ZIP-style"):
                with court_import.open_iff(path):
                    self.fail("Invalid archive opened")
            path.unlink()

    def test_scene_budget_is_checked_before_open_and_truncation_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "scene.iff"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("level_floor.SCNE", b"scene")
            with court_import.open_iff(path) as archive:
                with patch.object(court_import, "MAX_SCENE_BYTES", 4), patch.object(archive, "open") as member:
                    with self.assertRaisesRegex(ValueError, "scene exceeds"):
                        court_import.read_iff_scene(archive)
                    member.assert_not_called()
                self.assertEqual(court_import.read_iff_scene(archive), b"scene")
                archive.getinfo("level_floor.SCNE").file_size += 1
                with self.assertRaisesRegex(ValueError, "incomplete"):
                    court_import.read_iff_scene(archive)

    def test_copy_streams_members_preserves_metadata_and_does_not_mutate_source(self):
        with tempfile.TemporaryDirectory() as folder:
            source, target = (Path(folder) / name for name in ("source.iff", "copy.iff"))
            payload = b"preserved binary data\0" * 150000
            info = zipfile.ZipInfo("mesh.bin", (2020, 2, 3, 4, 5, 6))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.comment = b"member comment"
            info.extra = struct.pack("<HH", 0xCAFE, 2) + b"ab"
            info.external_attr = 0o100644 << 16
            with zipfile.ZipFile(source, "w") as archive:
                archive.writestr("padding", b"padding")
                archive.writestr(info, payload)
            before = source.read_bytes()
            with court_import.open_iff(source) as original, zipfile.ZipFile(target, "w") as destination:
                info = original.getinfo("mesh.bin")
                fields = ("header_offset", "file_size", "compress_size", "CRC", "flag_bits",
                          "date_time", "compress_type", "comment", "extra", "external_attr")
                metadata = {field: getattr(info, field) for field in fields}
                with patch.object(zipfile.ZipFile, "read", side_effect=AssertionError("Whole member read")), patch.object(
                        court_import.shutil, "copyfileobj", wraps=court_import.shutil.copyfileobj) as copy_member:
                    court_import._copy_iff_entry(original, destination, info)
                    self.assertEqual(copy_member.call_args.kwargs["length"], 1024 * 1024)
                    court_import._copy_iff_entry(original, destination, original.getinfo("padding"), b"replacement")
                self.assertEqual({field: getattr(info, field) for field in fields}, metadata)
            with court_import.open_iff(target) as archive:
                self.assertEqual(archive.read("mesh.bin"), payload)
                self.assertEqual(archive.read("padding"), b"replacement")
                copied = archive.getinfo("mesh.bin")
                for field in ("date_time", "compress_type", "comment", "extra", "external_attr"):
                    self.assertEqual(getattr(copied, field), metadata[field])
                with patch.object(archive, "read", side_effect=AssertionError("Whole verification read")):
                    self.assertTrue(court_import._entry_matches(archive, "mesh.bin", payload))
                    self.assertFalse(court_import._entry_matches(archive, "mesh.bin", b"wrong size"))
                    self.assertFalse(court_import._entry_matches(archive, "padding", b"replacemenx"))
            self.assertEqual(source.read_bytes(), before)

    def test_clean_base_streams_and_preserves_foreign_staging_on_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "base.iff"
            write_floor(path)
            before = path.read_bytes()
            foreign = path.with_name(path.name + ".clean.tmp")
            foreign.write_bytes(b"another operation")
            with patch.object(zipfile.ZipFile, "read", side_effect=AssertionError("Whole member read")):
                with patch.object(zipfile.ZipFile, "testzip", return_value="broken"), self.assertRaisesRegex(ValueError, "validation"):
                    court_import._clean_base_archive(path)
                self.assertEqual(path.read_bytes(), before)
                self.assertEqual(list(Path(folder).glob("*.tmp")), [foreign])
                court_import._clean_base_archive(path)
            with court_import.open_iff(path) as archive:
                self.assertEqual(archive.comment, court_import.BASE_VERSION)
                self.assertIsNone(archive.testzip())
            self.assertEqual(foreign.read_bytes(), b"another operation")
            self.assertEqual(list(Path(folder).glob("*.tmp")), [foreign])

    def test_package_streams_members_and_verifies_without_whole_archive_reads(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            png, base, tool, output = (root / name for name in ("court.png", "base.iff", "texconv.exe", "output.iff"))
            png.write_bytes(b"input pixels")
            tool.touch()
            payload = b"unchanged mesh\0" * 200000
            with zipfile.ZipFile(base, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr("floor.dds", b"old texture")
                archive.writestr("level_floor.SCNE", b"old scene")
                archive.writestr("mesh.bin", payload)
            before = base.read_bytes()
            dds = bytearray(solid_dds(8192, 4096))
            struct.pack_into("<I", dds, 28, 1)
            dds = bytes(dds)

            def convert(args, **_kwargs):
                (Path(args[args.index("-o") + 1]) / "court.DDS").write_bytes(dds)
                return subprocess.CompletedProcess(args, 0, "", "")

            selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}
            scene = b'{"Scene":{"Model":{"floor":{"Prim":[{"Mesh":"NBA_full_court_floor_lowShape"}]}}}}'
            with patch.object(court_import, "inspect_iff", return_value={"textures": [selected]}), patch.object(
                    court_import.Image, "open") as image, patch.object(court_import, "run_tool", side_effect=convert), patch.object(
                    zipfile.ZipFile, "read", side_effect=AssertionError("Whole member read")):
                image.return_value.__enter__.return_value.size = (8192, 4096)
                image.return_value.__enter__.return_value.format = "PNG"
                court_import.package_png_into_iff(png, base, "floor.dds", output, tool, scene_override=scene)
            with court_import.open_iff(output) as archive:
                self.assertEqual(archive.read("floor.dds"), dds)
                self.assertEqual(archive.read("level_floor.SCNE"), scene)
                self.assertEqual(archive.read("mesh.bin"), payload)
                self.assertIsNone(archive.testzip())
            self.assertEqual(base.read_bytes(), before)

    def test_oversized_replacement_scene_precedes_conversion(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            paths = [root / name for name in ("court.png", "base.iff", "output.iff", "texconv.exe")]
            paths[2].write_bytes(b"previous export")
            with patch.object(court_import, "MAX_SCENE_BYTES", 4), patch.object(court_import, "inspect_iff") as inspect:
                with self.assertRaisesRegex(ValueError, "replacement floor scene"):
                    court_import.package_png_into_iff(*paths[:2], "floor.dds", *paths[2:], scene_override=b"large")
                inspect.assert_not_called()
            self.assertEqual(paths[2].read_bytes(), b"previous export")

    def test_corrupt_unchanged_member_preserves_export_and_cleans_own_staging(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            png, base, tool, output = (root / name for name in ("court.png", "base.iff", "texconv.exe", "output.iff"))
            png.write_bytes(b"input pixels")
            tool.touch()
            output.write_bytes(b"previous export")
            with zipfile.ZipFile(base, "w") as archive:
                archive.writestr("floor.dds", b"old texture")
                archive.writestr("mesh.bin", b"unchanged binary")
            with zipfile.ZipFile(base) as archive:
                info = archive.getinfo("mesh.bin")
                offset = info.header_offset + zipfile.sizeFileHeader + len(info.filename.encode())
            data = bytearray(base.read_bytes())
            data[offset] ^= 1
            base.write_bytes(data)
            before = bytes(data)
            dds = bytearray(solid_dds(8192, 4096))
            struct.pack_into("<I", dds, 28, 1)

            def convert(args, **_kwargs):
                (Path(args[args.index("-o") + 1]) / "court.DDS").write_bytes(dds)
                return subprocess.CompletedProcess(args, 0, "", "")

            selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}
            with patch.object(court_import, "inspect_iff", return_value={"textures": [selected]}), patch.object(
                    court_import.Image, "open") as image, patch.object(court_import, "run_tool", side_effect=convert):
                image.return_value.__enter__.return_value.size = (8192, 4096)
                image.return_value.__enter__.return_value.format = "PNG"
                with self.assertRaisesRegex(zipfile.BadZipFile, "CRC"):
                    court_import.package_png_into_iff(png, base, "floor.dds", output, tool)
            self.assertEqual(base.read_bytes(), before)
            self.assertEqual(output.read_bytes(), b"previous export")
            self.assertFalse(list(root.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
