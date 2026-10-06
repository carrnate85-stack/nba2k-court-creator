import os
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from court_creator import court_import
from tools.export_2k26_court_texture import make_dds


def encoded_header(*, dxgi=None, mipmaps=1, width=8192, height=4096):
    header = bytearray(make_dds(width, height, "DXT1", b""))
    struct.pack_into("<I", header, 28, mipmaps)
    if mipmaps > 1:
        struct.pack_into("<I", header, 8, 0xA1007)
        struct.pack_into("<I", header, 108, 0x401008)
    if dxgi is not None:
        header[84:88] = b"DX10"
        header.extend(struct.pack("<5I", dxgi, 3, 0, 1, 0))
    return bytes(header)


def payload_size(width, height, blocks, mipmaps):
    return sum(max(1, (max(1, width >> level) + 3) // 4)
               * max(1, (max(1, height >> level) + 3) // 4) * blocks
               for level in range(mipmaps))


class DdsExportValidationTests(unittest.TestCase):
    def test_supported_formats_and_npot_mip_chains_share_import_validation(self):
        formats = [(None, b"DXT1", "BC1_UNORM", 8), (None, b"DXT5", "BC3_UNORM", 16)]
        formats.extend((dxgi, b"DX10", name, 8 if dxgi in (71, 72) else 16)
                       for dxgi, name in court_import.DXGI_FORMATS.items())
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "court.DDS"
            archive_path = Path(folder) / "source.iff"
            for dxgi, fourcc, name, block_bytes in formats:
                with self.subTest(format=name, fourcc=fourcc):
                    header = bytearray(encoded_header(dxgi=dxgi, mipmaps=5, width=20, height=20))
                    header[84:88] = fourcc
                    data = bytes(header) + b"\0" * payload_size(20, 20, block_bytes, 5)
                    path.write_bytes(data)
                    with ZipFile(archive_path, "w") as archive:
                        archive.writestr(path.name, data)
                    with ZipFile(archive_path) as archive:
                        descriptor = court_import._dds_info(archive, path.name)
                    self.assertEqual(descriptor, court_import._dds_header_info(data[:148], len(data), path.name, exact_payload=True))
                    self.assertEqual(descriptor["format"], name)
                    with patch.object(court_import, "OUTPUT_SIZE", (20, 20)):
                        self.assertEqual(court_import._read_encoded_dds(path, descriptor), data)
                    for invalid in (data[:-1], data + b"\0"):
                        path.write_bytes(invalid)
                        with self.assertRaisesRegex(RuntimeError, "complete, valid"):
                            court_import._read_encoded_dds(path, descriptor)

    def test_import_allows_trailing_data_but_encoder_requires_exact_payload(self):
        header = encoded_header(width=16, height=16)
        count = 128 + payload_size(16, 16, 8, 1)
        self.assertIsNotNone(court_import._dds_header_info(header, count + 1, "court.dds"))
        self.assertIsNone(court_import._dds_header_info(header, count + 1, "court.dds", exact_payload=True))
        for offset, value in ((80, 0x40), (8, 0x881007)):
            invalid = bytearray(header)
            struct.pack_into("<I", invalid, offset, value)
            self.assertIsNone(court_import._dds_header_info(bytes(invalid), count, "court.dds", exact_payload=True))

    def test_invalid_surface_headers_fail_with_a_complete_payload(self):
        for dxgi, blocks in ((None, 8), (98, 16)):
            header = encoded_header(dxgi=dxgi, width=16, height=16)
            byte_count = len(header) + payload_size(16, 16, blocks, 1)
            changes = [(4, 123), (76, 31), (24, 2), (28, 6), (112, 0x200), (112, 0x200000)]
            if dxgi is not None:
                changes.extend([(132, 2), (132, 4), (136, 4), (140, 0), (140, 2)])
            self.assertIsNotNone(court_import._dds_header_info(header, byte_count, "court.dds", exact_payload=True))
            for offset, value in changes:
                invalid = bytearray(header)
                struct.pack_into("<I", invalid, offset, value)
                with self.subTest(dxgi=dxgi, offset=offset, value=value):
                    self.assertIsNone(court_import._dds_header_info(bytes(invalid), byte_count, "court.dds", exact_payload=True))

    def test_oversized_encoder_output_is_rejected_before_open(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "court.DDS"
            path.write_bytes(encoded_header())
            with patch.object(court_import, "MAX_DDS_BYTES", 127), patch.object(
                    Path, "open", side_effect=AssertionError("Oversized DDS was opened")):
                with self.assertRaisesRegex(RuntimeError, "size limit"):
                    court_import._read_encoded_dds(path, {"format": "BC1_UNORM", "mipmaps": 1})

    def test_exact_size_budget_and_zero_base_mips_accept_one_encoded_mip(self):
        data = encoded_header(width=16, height=16) + b"\0" * payload_size(16, 16, 8, 1)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "court.DDS"
            path.write_bytes(data)
            with patch.object(court_import, "MAX_DDS_BYTES", len(data)), patch.object(court_import, "OUTPUT_SIZE", (16, 16)):
                self.assertEqual(court_import._read_encoded_dds(path, {"format": "BC1_UNORM", "mipmaps": 0}), data)
                for selected in ({"format": "BC7_UNORM", "mipmaps": 1}, {"format": "BC1_UNORM", "mipmaps": 2}):
                    with self.assertRaisesRegex(RuntimeError, "retain the base"):
                        court_import._read_encoded_dds(path, selected)

    def test_encoder_output_changes_during_open_or_read_are_rejected(self):
        data = encoded_header(width=16, height=16) + b"\0" * payload_size(16, 16, 8, 1)
        original_open = Path.open
        class ChangingReader:
            def __init__(self, stream, change):
                self.stream, self.change = stream, change
            def __getattr__(self, name):
                return getattr(self.stream, name)
            def __enter__(self):
                return self
            def __exit__(self, *_args):
                self.stream.close()
            def read(self, size):
                self.assert_bounded(size)
                if size == len(data) + 1:
                    self.change()
                return self.stream.read(size)
            def assert_bounded(self, size):
                if size not in (148, len(data) + 1):
                    raise AssertionError(f"Unexpected DDS read size {size}")
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "court.DDS"
            selected = {"format": "BC1_UNORM", "mipmaps": 1}
            for mode in ("grow", "shrink", "header", "open-replacement"):
                path.write_bytes(data)
                original_time = path.stat().st_mtime_ns
                def change():
                    payload = data + b"more" if mode == "grow" else data[:-1] if mode == "shrink" else b"BAD!" + data[4:]
                    with original_open(path, "wb") as destination:
                        destination.write(payload)
                    if mode == "header":
                        os.utime(path, ns=(original_time, original_time))
                def open_file(target, *args, **kwargs):
                    if target != path or args != ("rb",):
                        return original_open(target, *args, **kwargs)
                    if mode == "open-replacement":
                        replacement = path.with_suffix(".replacement")
                        replacement.write_bytes(data)
                        os.replace(replacement, path)
                        return original_open(target, *args, **kwargs)
                    return ChangingReader(original_open(target, *args, **kwargs), change)
                with self.subTest(mode=mode), patch.object(court_import, "OUTPUT_SIZE", (16, 16)), patch.object(Path, "open", open_file):
                    with self.assertRaisesRegex(RuntimeError, "changed"):
                        court_import._read_encoded_dds(path, selected)

    def test_shared_encoder_output_is_rejected_before_reading(self):
        with tempfile.TemporaryDirectory() as folder:
            source, encoded = (Path(folder) / name for name in ("source.dds", "court.DDS"))
            source.write_bytes(encoded_header())
            before = (source.read_bytes(), source.stat().st_mtime_ns)
            os.link(source, encoded)
            with patch.object(Path, "open", side_effect=AssertionError("Shared DDS was read")):
                with self.assertRaisesRegex(RuntimeError, "ordinary unshared"):
                    court_import._read_encoded_dds(encoded, {"format": "BC1_UNORM", "mipmaps": 1})
            self.assertEqual((source.read_bytes(), source.stat().st_mtime_ns), before)

    def test_invalid_encoder_outputs_cannot_reach_export_staging(self):
        header = encoded_header(dxgi=98, mipmaps=14)
        cases = {"header-only": header, "short-body": header + b"\0" * 1000,
                 "short-legacy": encoded_header() + b"\0" * 1000}
        for offset, value in ((4, 123), (76, 31), (24, 2), (112, 0x200),
                              (112, 0x200000), (132, 2), (132, 4),
                              (136, 4), (140, 0), (140, 2)):
            invalid = bytearray(header + b"\0" * 1000)
            struct.pack_into("<I", invalid, offset, value)
            cases[f"field-{offset}-{value}"] = bytes(invalid)
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            png, base, tool, output = (root / name for name in ("court.png", "base.iff", "texconv.exe", "output.iff"))
            for path in (png, base, tool, output):
                path.write_bytes(path.name.encode())
            with ZipFile(base, "w") as archive:
                archive.writestr("floor.dds", b"original DDS")
                archive.writestr("mesh.bin", b"original geometry")
            before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in (png, base, tool, output)}
            converted_folders = []
            selected = {"name": "floor.dds", "format": "BC7_UNORM", "mipmaps": 14}
            with patch.object(court_import, "inspect_iff", return_value={"textures": [selected]}), patch.object(
                    court_import.Image, "open") as image, patch.object(
                    court_import, "staged_export", side_effect=AssertionError("Invalid DDS reached export staging")):
                image.return_value.__enter__.return_value.size = court_import.OUTPUT_SIZE
                image.return_value.__enter__.return_value.format = "PNG"
                for name, data in cases.items():
                    def convert(args, **_kwargs):
                        temporary = Path(args[args.index("-o") + 1])
                        converted_folders.append(temporary)
                        (temporary / "court.DDS").write_bytes(data)
                        return subprocess.CompletedProcess(args, 0, "", "")
                    with self.subTest(name=name), patch.object(court_import, "run_tool", side_effect=convert):
                        with self.assertRaisesRegex(RuntimeError, "DDS"):
                            court_import.package_png_into_iff(png, base, "floor.dds", output, tool)
                    self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before}, before)
                    self.assertFalse(list(root.glob("*.tmp")))
            self.assertTrue(converted_folders)
            self.assertTrue(all(not path.exists() for path in converted_folders))

    def test_complete_encoded_texture_uses_bounded_reads_and_preserves_archive(self):
        data = encoded_header() + b"\0" * payload_size(8192, 4096, 8, 1)
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            png, base, tool, output = (root / name for name in ("court.png", "base.iff", "texconv.exe", "output.iff"))
            png.write_bytes(b"pixels")
            tool.write_bytes(b"converter")
            with ZipFile(base, "w") as archive:
                archive.writestr("floor.dds", b"old texture")
                archive.writestr("mesh.bin", b"original geometry")
                archive.writestr("level_floor.SCNE", b"original scene")
                archive.comment = b"original layout"
            before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in (png, base, tool)}
            original_read = Path.read_bytes
            def read_bytes(path):
                if path.name == "court.DDS":
                    raise AssertionError("Unbounded DDS read")
                return original_read(path)
            def convert(args, **_kwargs):
                (Path(args[args.index("-o") + 1]) / "court.DDS").write_bytes(data)
                return subprocess.CompletedProcess(args, 0, "", "")
            selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}
            with patch.object(court_import, "inspect_iff", return_value={"textures": [selected]}), patch.object(
                    court_import.Image, "open") as image, patch.object(court_import, "run_tool", side_effect=convert), patch.object(
                    Path, "read_bytes", read_bytes):
                image.return_value.__enter__.return_value.size = court_import.OUTPUT_SIZE
                image.return_value.__enter__.return_value.format = "PNG"
                court_import.package_png_into_iff(png, base, "floor.dds", output, tool)
            with ZipFile(output) as archive:
                self.assertEqual(archive.read("floor.dds"), data)
                self.assertEqual(archive.read("mesh.bin"), b"original geometry")
                self.assertEqual(archive.read("level_floor.SCNE"), b"original scene")
                self.assertEqual(archive.comment, b"original layout")
                self.assertIsNone(archive.testzip())
            self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before}, before)
            self.assertFalse(list(root.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
