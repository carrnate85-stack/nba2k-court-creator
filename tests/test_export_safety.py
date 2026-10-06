import json
import os
from pathlib import Path
import subprocess
import struct
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image

from court_creator import backend, court_import, court_template, experimental_lines as geometry, export_io
from tests.test_geometry_cache import fixture
from tests.test_court_import import solid_dds


class ExportSafetyTests(unittest.TestCase):
    def test_atomic_png_failure_preserves_existing_output_and_unrelated_staging_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            output = root / "court.png"
            output.write_bytes(b"previous export")
            foreign = root / f".court.png.{os.getpid()}.tmp"
            foreign.write_bytes(b"another operation")
            before = set(root.iterdir())
            image = Image.new("RGB", (16, 8), "red")
            def fail(path, **_kwargs):
                Path(path).write_bytes(b"partial PNG")
                raise OSError("disk full")
            for failure in ("encode", "flush", "replace"):
                with self.subTest(failure=failure):
                    if failure == "encode":
                        context = patch.object(image, "save", side_effect=fail)
                    else:
                        context = patch.object(export_io.os, "fsync" if failure == "flush" else "replace", side_effect=OSError("disk full"))
                    with context, self.assertRaises(OSError):
                        court_template._save_png_atomic(image, output, fast=True)
                    self.assertEqual(output.read_bytes(), b"previous export")
                    self.assertEqual(foreign.read_bytes(), b"another operation")
                    self.assertEqual(set(root.iterdir()), before)

    def test_concurrent_pngs_use_distinct_staging_and_publish_complete_images(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "court.png"
            images = [Image.new("RGB", (32, 16), color) for color in ("red", "blue")]
            barrier = threading.Barrier(2)
            staging = []
            def render(image):
                save = image.save
                def synchronized(path, **kwargs):
                    staging.append(Path(path))
                    barrier.wait(timeout=5)
                    save(path, **kwargs)
                with patch.object(image, "save", side_effect=synchronized):
                    court_template._save_png_atomic(image, output, fast=True)
            with ThreadPoolExecutor(max_workers=2) as pool:
                list(pool.map(render, images))
            self.assertEqual(len(set(staging)), 2)
            self.assertEqual(list(Path(folder).iterdir()), [output])
            with Image.open(output) as published:
                self.assertEqual(published.size, (32, 16))
                self.assertIn(published.getpixel((16, 8)), [(255, 0, 0), (0, 0, 255)])
                self.assertEqual(len(published.getcolors()), 1)

    def test_sources_are_protected_by_path_hardlink_and_precommit_recheck(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / "floor.png"
            source.write_bytes(b"source asset")
            alias = root / "alias.png"
            os.link(source, alias)
            for output in (source, alias, root / "subdirectory" / ".." / "floor.png"):
                with self.subTest(output=output), self.assertRaisesRegex(ValueError, "new file"):
                    export_io.ensure_new_export(output, source)
            output = root / "court.png"
            with self.assertRaisesRegex(ValueError, "new file"):
                with export_io.staged_export(output, sources=(source,)) as staging:
                    staging.write_bytes(b"complete export")
                    os.link(source, output)
            self.assertEqual(source.read_bytes(), b"source asset")
            self.assertEqual(output.read_bytes(), b"source asset")
            self.assertFalse(list(root.glob("*.tmp")))

    def test_conversion_png_cannot_replace_source_or_background_and_is_atomic(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, background = root / "source.iff", root / "wood.png"
            with ZipFile(source, "w") as archive:
                archive.writestr("floor.dds", solid_dds(32, 16))
            source_bytes = source.read_bytes()
            Image.new("RGB", (32, 16), "yellow").save(background)
            background_bytes = background.read_bytes()
            with patch.object(court_import, "_import_pixels", side_effect=AssertionError("Input protection must run before decode")):
                for output in (source, background):
                    with self.assertRaisesRegex(ValueError, "new file"):
                        court_import.render_texture(source, "floor.dds", [0, 0, 32, 16], background, output)
            output = root / "converted.png"
            output.write_bytes(b"previous export")
            image = Image.new("RGB", (32, 16), "red")
            def fail(path, **_kwargs):
                Path(path).write_bytes(b"partial PNG")
                raise OSError("disk full")
            with patch.object(court_import, "_aligned_image", return_value=image), patch.object(image, "save", side_effect=fail):
                with self.assertRaises(OSError):
                    court_import.render_texture(source, "floor.dds", [0, 0, 32, 16], background, output)
            self.assertEqual(output.read_bytes(), b"previous export")
            self.assertEqual(source.read_bytes(), source_bytes)
            self.assertEqual(background.read_bytes(), background_bytes)
            self.assertFalse(list(root.glob("*.tmp")))
            with self.assertRaises(ValueError):
                image.getpixel((0, 0))
            with court_import._import_pixels(source, "floor.dds") as (cached, _revision):
                self.assertEqual(cached.getpixel((0, 0)), (255, 0, 0, 255))

    def test_backend_protects_floor_logo_project_base_geometry_and_converter(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            document = fixture()
            court_import._write_json_atomic(geometry.geometry_path(root), document)
            floor, logo, project, base, converter = (root / name for name in ("floor.png", "logo.png", "court.court.json", "base.iff", "tools/texconv.exe"))
            converter.parent.mkdir()
            for path in (floor, logo, project, base, converter):
                path.write_bytes(b"protected source")
            request_path = root / "request.json"
            request = {"buildMode": "game-uv", "geometryRevision": geometry.geometry_revision(document),
                       "floor": {"path": str(floor)}, "logoImages": [{"path": str(logo), "visible": False}],
                       "_projectPath": str(project)}
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "NBA2K27_EXPORT_BASE", base), patch.object(backend, "_ready_import_base", side_effect=AssertionError("Must not prepare base for destructive output")):
                for target in (floor, logo, project, base, converter, geometry.geometry_path(root)):
                    original = target.read_bytes()
                    request["outputPath"] = str(target)
                    request_path.write_text(json.dumps(request), encoding="utf-8")
                    for operation in (backend.render_preview, backend.export_current_iff):
                        with self.subTest(target=target.name, operation=operation.__name__), self.assertRaisesRegex(ValueError, "new file"):
                            operation(request_path)
                        self.assertEqual(target.read_bytes(), original)

    def test_conversion_backend_rejects_destructive_output_before_decode_or_base_preparation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, background, base, request_path = (root / name for name in ("source.iff", "floor.png", "base.iff", "request.json"))
            source.write_bytes(b"original source")
            Image.new("RGB", (32, 16), "yellow").save(background)
            base.write_bytes(b"original base")
            request = {"sourcePath": str(source), "textureName": "floor.dds", "bounds": [0, 0, 32, 16], "backgroundPath": str(background)}
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "NBA2K27_EXPORT_BASE", base), patch.object(backend, "_ready_import_base", side_effect=AssertionError("Protected output reached base preparation")), patch.object(backend, "render_texture", side_effect=AssertionError("Protected output reached decode")):
                for target in (source, background, base, request_path):
                    request["outputPath"] = str(target)
                    request_path.write_text(json.dumps(request), encoding="utf-8")
                    original = target.read_bytes()
                    for operation in (backend.preview_import, backend.export_import_png, backend.export_import_iff):
                        with self.subTest(target=target.name, operation=operation.__name__), self.assertRaisesRegex(ValueError, "new file"):
                            operation(request_path)
                        self.assertEqual(target.read_bytes(), original)

    def test_iff_failure_preserves_previous_export_and_foreign_fixed_staging(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            png, base, converter, output = (root / name for name in ("court.png", "base.iff", "texconv.exe", "export.iff"))
            png.write_bytes(b"source pixels")
            converter.touch()
            with ZipFile(base, "w") as archive:
                archive.writestr("floor.dds", b"original texture")
                archive.writestr("level_floor.SCNE", b"original scene")
            base_bytes = base.read_bytes()
            output.write_bytes(b"previous IFF")
            foreign = root / "export.iff.tmp"
            foreign.write_bytes(b"another operation")
            dds = bytearray(solid_dds(8192, 4096))
            struct.pack_into("<I", dds, 28, 1)
            dds = bytes(dds)
            def convert(args, **_kwargs):
                (Path(args[args.index("-o") + 1]) / "court.DDS").write_bytes(dds)
                return subprocess.CompletedProcess(args, 0, "", "")
            selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}
            with patch.object(court_import, "inspect_iff", return_value={"textures": [selected]}), patch.object(court_import.Image, "open") as image, patch.object(court_import, "run_tool", side_effect=convert):
                image.return_value.__enter__.return_value.size = (8192, 4096)
                image.return_value.__enter__.return_value.format = "PNG"
                with patch.object(ZipFile, "testzip", return_value="floor.dds"), self.assertRaisesRegex(RuntimeError, "archive verification"):
                    court_import.package_png_into_iff(png, base, "floor.dds", output, converter)
                self.assertEqual(output.read_bytes(), b"previous IFF")
                self.assertEqual(foreign.read_bytes(), b"another operation")
                self.assertEqual(base.read_bytes(), base_bytes)
                self.assertEqual(list(root.glob("*.tmp")), [foreign])
                court_import.package_png_into_iff(png, base, "floor.dds", output, converter)
                with ZipFile(output) as published:
                    self.assertEqual(published.read("floor.dds"), dds)
                    self.assertEqual(published.read("level_floor.SCNE"), b"original scene")
                self.assertEqual(base.read_bytes(), base_bytes)
                self.assertEqual(foreign.read_bytes(), b"another operation")
                self.assertEqual(list(root.glob("*.tmp")), [foreign])

    def test_iff_packaging_rejects_all_inputs_before_converter_runs(self):
        with tempfile.TemporaryDirectory() as folder:
            paths = [Path(folder) / name for name in ("court.png", "base.iff", "texconv.exe", "logo.png")]
            for path in paths:
                path.write_bytes(b"protected input")
            with patch.object(court_import, "inspect_iff", side_effect=AssertionError("Input protection must run first")):
                for output in paths:
                    with self.assertRaisesRegex(ValueError, "new file"):
                        court_import.package_png_into_iff(paths[0], paths[1], "floor.dds", output, paths[2], protected_sources=(paths[3],))
                    self.assertEqual(output.read_bytes(), b"protected input")


if __name__ == "__main__":
    unittest.main()
