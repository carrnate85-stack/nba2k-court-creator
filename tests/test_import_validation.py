import json
from pathlib import Path
import struct
import tempfile
import unittest
import warnings
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image

from court_creator import backend, court_import
from tests.test_court_import import solid_dds


class ImportValidationTests(unittest.TestCase):
    def test_invalid_selected_headers_are_rejected_before_pixel_decode(self):
        original = solid_dds(64, 32)
        cases = []
        for offset, value in ((4, 123), (76, 31), (12, 16385), (16, 16385), (24, 2), (28, 99), (112, 0x200), (112, 0x200000)):
            data = bytearray(original); struct.pack_into("<I", data, offset, value); cases.append(bytes(data))
        cases.extend([original[:127], original[:-1]])
        data = bytearray(original); struct.pack_into("<II", data, 12, 8192, 16384); cases.append(bytes(data))
        with tempfile.TemporaryDirectory() as folder, patch.object(court_import.Image, "open", side_effect=AssertionError("Invalid header reached pixel decoding")):
            path = Path(folder) / "source.iff"
            for data in cases:
                with ZipFile(path, "w") as archive:
                    archive.writestr("bigcourt.dds", data)
                with self.subTest(header=data[:128]), self.assertRaisesRegex(ValueError, "header"):
                    court_import.read_dds(path, "bigcourt.dds")

    def test_dx10_requires_a_single_two_dimensional_surface(self):
        legacy = bytearray(solid_dds(64, 32)); legacy[84:88] = b"DX10"
        body = bytes(legacy[128:]); header = bytes(legacy[:128])
        valid = header + struct.pack("<5I", 71, 3, 0, 1, 0) + body
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.iff"
            for dimension, flags, arrays in ((3, 0, 0), (3, 0, 2), (2, 0, 1), (4, 0, 1), (3, 4, 1)):
                with ZipFile(path, "w") as archive:
                    archive.writestr("bigcourt.dds", header + struct.pack("<5I", 71, dimension, flags, arrays, 0) + body)
                with patch.object(court_import.Image, "open", side_effect=AssertionError("Unsupported surface was decoded")), self.assertRaisesRegex(ValueError, "header"):
                    court_import.read_dds(path, "bigcourt.dds")
            with ZipFile(path, "w") as archive:
                archive.writestr("bigcourt.dds", valid)
            with court_import.read_dds(path, "bigcourt.dds") as image:
                self.assertEqual(image.size, (64, 32))
                self.assertEqual(image.getpixel((32, 16)), (255, 0, 0, 255))

    def test_valid_npot_mip_chain_is_accepted_and_truncation_rejected(self):
        # BC blocks retain a full block at the smallest mip levels.
        data = bytearray(solid_dds(20, 20))
        struct.pack_into("<I", data, 28, 5)
        block = solid_dds(4, 4)[128:]
        data.extend(block * (9 + 4 + 1 + 1))
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.iff"
            for payload, valid in ((bytes(data), True), (bytes(data[:-1]), False)):
                with ZipFile(path, "w") as archive: archive.writestr("bigcourt.dds", payload)
                with ZipFile(path) as archive:
                    self.assertEqual(court_import._dds_info(archive, "bigcourt.dds") is not None, valid)

    def test_duplicate_names_and_missing_selection_are_not_silently_resolved(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.iff"
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with ZipFile(path, "w") as archive:
                    archive.writestr("bigcourt.dds", solid_dds(64, 32))
                    archive.writestr("bigcourt.dds", solid_dds(32, 16))
            for operation in (lambda: court_import.inspect_iff(path), lambda: court_import.read_dds(path, "bigcourt.dds")):
                with self.assertRaisesRegex(ValueError, "duplicate entry names"): operation()
            with ZipFile(path, "w") as archive: archive.writestr("bigcourt.dds", solid_dds(64, 32))
            with self.assertRaisesRegex(ValueError, "no longer available"):
                court_import.inspect_iff(path, selected="missing.dds")
            with self.assertRaisesRegex(ValueError, "no longer in"):
                court_import.read_dds(path, "missing.dds")

    def test_changed_source_content_or_layout_cannot_replace_existing_conversion(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, background, output, base, converter = (root / name for name in ("source.iff", "wood.png", "converted.png", "base.iff", "texconv.exe"))
            data = solid_dds(64, 32)
            def write(payload, comment=b""):
                with ZipFile(source, "w") as archive:
                    archive.writestr("bigcourt.dds", payload); archive.comment = comment
            write(data)
            metadata = court_import.inspect_iff(source)
            Image.new("RGB", (32, 16), "yellow").save(background)
            output.write_bytes(b"previous export"); base.write_bytes(b"unchanged base"); converter.touch()
            changed = bytearray(data); struct.pack_into("<H", changed, 128, 0x001f)
            for payload, comment in ((bytes(changed), b""), (data, b"changed layout metadata")):
                write(payload, comment)
                with patch.object(court_import.Image, "open", side_effect=AssertionError("Changed source reached pixel decoding")):
                    with self.assertRaisesRegex(ValueError, "source court changed"):
                        court_import.render_texture(source, "bigcourt.dds", metadata["sourceBounds"], background, output, source_revision=metadata["sourceRevision"])
                    with self.assertRaisesRegex(ValueError, "source court changed"):
                        court_import.build_iff(source, "bigcourt.dds", metadata["sourceBounds"], background, base, "floor.dds", output, converter, source_revision=metadata["sourceRevision"])
                self.assertEqual(output.read_bytes(), b"previous export")
                self.assertEqual(base.read_bytes(), b"unchanged base")
            current = court_import.inspect_iff(source)
            self.assertNotEqual(current["sourceRevision"], metadata["sourceRevision"])
            court_import.render_texture(source, "bigcourt.dds", current["sourceBounds"], background, output, preview=True, source_revision=current["sourceRevision"])
            with Image.open(output) as image: self.assertEqual(image.size, (1200, 600))

    def test_rgba_decode_retains_loaded_pixels_without_an_extra_full_image_copy(self):
        data = solid_dds(64, 32)
        with patch.object(court_import.Image.Image, "convert", side_effect=AssertionError("RGBA decode made an unnecessary full-image copy")):
            with court_import._decode_dds(data, {"width": 64, "height": 32}) as image:
                self.assertEqual(image.mode, "RGBA")
                self.assertEqual(image.getpixel((32, 16)), (255, 0, 0, 255))

    def test_invalid_source_revision_is_rejected_before_import_work(self):
        with tempfile.TemporaryDirectory() as folder:
            request_path = Path(folder) / "request.json"
            for value in (None, True, 1, "", "f" * 63, "G" * 64):
                request_path.write_text(json.dumps({"sourceRevision": value}), encoding="utf-8")
                for operation in (backend.preview_import, backend.export_import_png, backend.export_import_iff):
                    with self.assertRaisesRegex(ValueError, "Invalid source court revision"): operation(request_path)


if __name__ == "__main__":
    unittest.main()
