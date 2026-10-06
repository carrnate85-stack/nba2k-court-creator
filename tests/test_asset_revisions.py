import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image

from court_creator import asset_io, backend, court_import, court_template, experimental_lines as geometry
from tests.test_geometry_cache import fixture
from tests.test_court_import import solid_dds


def revision(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class AssetRevisionTests(unittest.TestCase):
    def test_revision_validation_and_legacy_compatibility(self):
        self.assertIsNone(asset_io.asset_revision({}))
        self.assertEqual(asset_io.asset_revision({"sourceRevision": "a" * 64}), "a" * 64)
        for value in (None, True, 12, "", "a" * 63, "A" * 64, "z" * 64):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "Invalid artwork"):
                asset_io.asset_revision({"sourceRevision": value})

    def test_stream_size_and_changes_during_decode_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "asset.bin"
            path.write_bytes(b"before")
            expected = revision(path)
            with self.assertRaisesRegex(ValueError, "while it was being decoded"):
                with asset_io.verified_asset_stream(path, expected) as stream:
                    self.assertEqual(stream.read(), b"before")
                    path.write_bytes(b"after!")
            with patch.object(asset_io, "MAX_ASSET_BYTES", 4), patch.object(asset_io, "_stream_revision") as digest:
                with self.assertRaisesRegex(ValueError, "file size limit"):
                    with asset_io.verified_asset_stream(path, expected):
                        self.fail("Oversized image stream opened")
                digest.assert_not_called()
            with path.open("rb") as stream, patch.object(asset_io, "MAX_ASSET_BYTES", 4):
                with self.assertRaisesRegex(ValueError, "file size limit"):
                    asset_io._stream_revision(stream)

    def test_pinned_logo_cache_checks_all_bytes_even_with_same_size_and_timestamp(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "logo.bmp"
            Image.new("RGB", (64, 32), "red").save(path)
            expected = revision(path)
            stamp, size = path.stat().st_mtime_ns, path.stat().st_size
            with court_template._PREVIEW_CACHE_LOCK:
                court_template._EXTERNAL_IMAGE_CACHE.clear()
            first = court_template._cached_external_image(path, (64, 32), fit=False, source_revision=expected)
            self.assertEqual(first.getpixel((0, 0))[:3], (255, 0, 0))
            with patch.object(Image, "open", side_effect=AssertionError("Cached image decoded again")):
                cached = court_template._cached_external_image(path, (64, 32), fit=False, source_revision=expected)
                self.assertEqual(cached.getpixel((0, 0))[:3], (255, 0, 0))
            Image.new("RGB", (64, 32), "blue").save(path)
            os.utime(path, ns=(stamp, stamp))
            self.assertEqual(path.stat().st_size, size)
            with patch.object(Image, "open", side_effect=AssertionError("Mismatched pixels decoded")):
                with self.assertRaisesRegex(ValueError, "changed after its preview"):
                    court_template._cached_external_image(path, (64, 32), fit=False, source_revision=expected)
            fresh = court_template._cached_external_image(path, (64, 32), fit=False, source_revision=revision(path))
            self.assertEqual(fresh.getpixel((0, 0))[:3], (0, 0, 255))
            legacy = court_template._cached_external_image(path, (64, 32), fit=False)
            self.assertEqual(legacy.getpixel((0, 0))[:3], (0, 0, 255))

    def test_decoder_prefix_reads_cannot_hide_small_file_replacement(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "small.bin"
            original = b"original image data" * 100
            changed = b"modified image data" * 100
            self.assertEqual(len(original), len(changed))
            path.write_bytes(original)
            stamp = path.stat().st_mtime_ns
            with self.assertRaisesRegex(ValueError, "while it was being decoded"):
                with asset_io.verified_asset_stream(path, revision(path)) as stream:
                    self.assertEqual(stream.read(16), original[:16])
                    stream.seek(0)
                    self.assertEqual(stream.read(32), original[:32])
                    path.write_bytes(changed)
                    os.utime(path, ns=(stamp, stamp))

    def test_pinned_missing_logo_is_not_silently_omitted(self):
        with tempfile.TemporaryDirectory() as folder:
            logo = {"path": str(Path(folder) / "missing.png"), "sourceRevision": "a" * 64}
            canvas = Image.new("RGBA", (32, 16))
            with self.assertRaisesRegex(ValueError, "preview is missing"):
                court_template._composite_logo(canvas, logo, 1)
            logo.pop("sourceRevision")
            court_template._composite_logo(canvas, logo, 1)
            self.assertIsNone(canvas.getbbox())

    def test_png_and_iff_exports_preserve_output_on_floor_or_logo_mismatch(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            floor, logo, output, request_path, base = (root / name for name in ("floor.bmp", "logo.bmp", "previous", "request.json", "base.iff"))
            Image.new("RGB", (64, 32), "red").save(floor)
            Image.new("RGB", (32, 32), "green").save(logo)
            floor_bytes, logo_bytes = floor.read_bytes(), logo.read_bytes()
            document = fixture()
            court_import._write_json_atomic(geometry.geometry_path(root), document)
            with ZipFile(base, "w") as archive:
                archive.writestr("level_floor.SCNE", b"scene")
            request = {"buildMode": "game-uv", "mappingMode": "game-uv", "geometryRevision": geometry.geometry_revision(document),
                       "floor": {"path": str(floor), "sourceRevision": revision(floor)},
                       "logoImages": [{"path": str(logo), "sourceRevision": revision(logo), "x": 100, "y": 100, "width": 100, "height": 100}],
                       "outputPath": str(output), "exportFullResolution": False}
            request_path.write_text(json.dumps(request), encoding="utf-8")
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "NBA2K27_EXPORT_BASE", base), patch.object(
                    backend, "_ready_import_base", return_value={"selected": "floor.dds"}), patch.object(
                    geometry, "native_export_scene", return_value=b"native scene"), patch.object(
                    backend, "package_png_into_iff", side_effect=AssertionError("Changed source reached DDS conversion")):
                for changed in (floor, logo):
                    original = changed.read_bytes()
                    stamp, size = changed.stat().st_mtime_ns, changed.stat().st_size
                    Image.new("RGB", (64, 32) if changed == floor else (32, 32), "blue").save(changed)
                    os.utime(changed, ns=(stamp, stamp))
                    self.assertEqual(changed.stat().st_size, size)
                    output.write_bytes(b"previous valid output")
                    for operation in (backend.render_preview, backend.export_current_iff):
                        with self.subTest(asset=changed.name, operation=operation.__name__), self.assertRaisesRegex(ValueError, "changed after its preview"):
                            operation(request_path)
                        self.assertEqual(output.read_bytes(), b"previous valid output")
                        self.assertFalse(list(root.glob("*.tmp")))
                    changed.write_bytes(original)
            self.assertEqual(floor.read_bytes(), floor_bytes)
            self.assertEqual(logo.read_bytes(), logo_bytes)
            self.assertEqual(json.loads(request_path.read_text()), request)

    def test_library_relocation_does_not_discard_the_expected_revision(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            floor, output = root / "relocated.bmp", root / "output.png"
            Image.new("RGB", (64, 32), "blue").save(floor)
            output.write_bytes(b"previous export")
            request = {"buildMode": "game-uv", "floor": {"id": "same", "path": str(root / "old.bmp"), "sourceRevision": "a" * 64},
                       "outputPath": str(output)}
            with patch.object(backend, "load_stock_state", return_value={"customFloorImages": [{"id": "same", "path": str(floor)}]}):
                with self.assertRaisesRegex(ValueError, "changed after its preview"):
                    backend.render_preview(request, geometry=fixture())
            self.assertEqual(output.read_bytes(), b"previous export")
            self.assertEqual(request["floor"]["sourceRevision"], "a" * 64)

    def test_conversion_background_changes_preserve_preview_and_exports(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            background, source, base, output, request_path = (root / name for name in ("floor.bmp", "source.iff", "base.iff", "output", "request.json"))
            Image.new("RGB", (64, 32), "red").save(background)
            expected = revision(background)
            with ZipFile(source, "w") as archive:
                archive.writestr("bigcourt.dds", solid_dds(64, 32))
            with ZipFile(base, "w") as archive:
                archive.writestr("level_floor.SCNE", b"scene")
            request = {"sourcePath": str(source), "textureName": "bigcourt.dds", "bounds": [0, 0, 64, 32],
                       "backgroundPath": str(background), "backgroundRevision": expected, "outputPath": str(output)}
            request_path.write_text(json.dumps(request), encoding="utf-8")
            stamp, size = background.stat().st_mtime_ns, background.stat().st_size
            Image.new("RGB", (64, 32), "blue").save(background)
            os.utime(background, ns=(stamp, stamp))
            self.assertEqual(background.stat().st_size, size)
            output.write_bytes(b"previous conversion")
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "NBA2K27_EXPORT_BASE", base), patch.object(
                    backend, "_ready_import_base", return_value={"selected": "floor.dds"}), patch.object(
                    geometry, "native_export_scene", return_value=b"native scene"), patch.object(
                    court_import, "package_png_into_iff", side_effect=AssertionError("Changed background reached conversion")):
                for operation in (backend.preview_import, backend.export_import_png, backend.export_import_iff):
                    with self.subTest(operation=operation.__name__), self.assertRaisesRegex(ValueError, "changed after its preview"):
                        operation(request_path)
                    self.assertEqual(output.read_bytes(), b"previous conversion")
                request["backgroundRevision"] = revision(background)
                request_path.write_text(json.dumps(request), encoding="utf-8")
                self.assertTrue(backend.preview_import(request_path)["ok"])
                with Image.open(output) as image:
                    self.assertEqual(image.size, (1200, 600))

    def test_malformed_conversion_background_revision_precedes_loading(self):
        with tempfile.TemporaryDirectory() as folder:
            request_path = Path(folder) / "request.json"
            for value in (None, True, 12, "", "A" * 64):
                request_path.write_text(json.dumps({"backgroundRevision": value}), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Invalid artwork"):
                    backend._import_arguments(request_path)


if __name__ == "__main__":
    unittest.main()
