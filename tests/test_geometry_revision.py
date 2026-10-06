import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

from court_creator import backend, court_import, experimental_lines as geometry
from tests.test_geometry_cache import fixture


class GeometryRevisionTests(unittest.TestCase):
    def test_experimental_state_returns_the_geometry_used_to_load_floors(self):
        document = fixture()
        with patch.object(backend, "load_stock_state", return_value={"geometry": document, "customFloorImages": []}) as state:
            result = backend.experimental_state()
            state.assert_called_once()
            self.assertIs(result["geometry"], document)
            self.assertEqual(result["geometryRevision"], geometry.geometry_revision(document))

    def test_revision_is_order_independent_and_changes_with_coordinates(self):
        original = fixture()
        reordered = json.loads(json.dumps(original, sort_keys=True))
        self.assertEqual(geometry.geometry_revision(original), geometry.geometry_revision(reordered))
        reordered["layers"][0]["gameUvPolygons"][0][0][0] += 1
        self.assertNotEqual(geometry.geometry_revision(original), geometry.geometry_revision(reordered))

    def test_requests_reject_changed_missing_corrupt_and_invalid_revisions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            document = fixture()
            request = {"geometryRevision": geometry.geometry_revision(document)}
            path = geometry.geometry_path(root)
            court_import._write_json_atomic(path, document)
            self.assertEqual(geometry.request_geometry(root, request), document)
            self.assertEqual(geometry.request_geometry(root, {}), document)
            changed = copy.deepcopy(document)
            changed["layers"][0]["gameUvPolygons"][0][0][0] += 1
            court_import._write_json_atomic(path, changed)
            with self.assertRaisesRegex(ValueError, "changed while"):
                geometry.request_geometry(root, request)
            self.assertEqual(geometry.load_geometry(root), changed)
            for value in (None, True, 42, "", "0" * 63, "A" * 64):
                # An absent revision supports old clients; malformed supplied values do not.
                with self.assertRaisesRegex(ValueError, "Invalid court geometry revision"):
                    geometry.request_geometry(root, {"geometryRevision": value})
            path.write_bytes(b"broken cache")
            with self.assertRaisesRegex(ValueError, "unavailable"):
                geometry.request_geometry(root, request)
            self.assertEqual(path.read_bytes(), b"broken cache")
            path.unlink()
            with self.assertRaisesRegex(ValueError, "unavailable"):
                geometry.request_geometry(root, request)
            self.assertFalse(path.exists())

    def test_export_and_conversion_reject_mismatch_before_output_or_base_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            document = fixture()
            changed = copy.deepcopy(document)
            changed["guides"]["game-uv"]["anchors"][0]["x"] += 1
            court_import._write_json_atomic(geometry.geometry_path(root), changed)
            background = root / "background.png"
            Image.new("RGB", (32, 16), "yellow").save(background)
            output = root / "existing-output"
            output.write_bytes(b"previous valid export")
            request = {"geometryRevision": geometry.geometry_revision(document), "buildMode": "game-uv",
                       "sourcePath": str(root / "source.iff"), "textureName": "bigcourt.dds", "bounds": [0, 0, 32, 16],
                       "backgroundPath": str(background), "outputPath": str(output)}
            request_path = root / "request.json"
            request_path.write_text(json.dumps(request), encoding="utf-8")
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "_ready_import_base", side_effect=AssertionError("Base touched before validation")), patch.object(backend, "render_texture", side_effect=AssertionError("Conversion started before validation")):
                for operation in (backend.render_preview, backend.export_current_iff, backend.preview_import,
                                  backend.export_import_png, backend.export_import_iff):
                    with self.subTest(operation=operation.__name__), self.assertRaisesRegex(ValueError, "changed while"):
                        operation(request_path)
                    self.assertEqual(output.read_bytes(), b"previous valid export")
            self.assertEqual(geometry.load_geometry(root), changed)

    def test_supplied_geometry_is_used_without_a_second_disk_read(self):
        document = fixture()
        with tempfile.TemporaryDirectory() as folder:
            background = Path(folder) / "background.png"
            Image.new("RGB", (32, 16), "yellow").save(background)
            with patch.object(geometry, "load_geometry", side_effect=AssertionError("Geometry reread during operation")):
                self.assertEqual(court_import.target_court_bounds(geometry=document), [110, 110, 890, 490])
                image = court_import._aligned_image(Image.new("RGB", (32, 16), "red"), [0, 0, 32, 16], background,
                                                    (1200, 600), geometry=document)
                self.assertEqual(image.size, (1200, 600))

    def test_import_backend_passes_one_snapshot_through_render_and_packaging(self):
        document = fixture()
        request = {"geometryRevision": geometry.geometry_revision(document), "outputPath": "output.iff"}
        with patch.object(backend, "_import_arguments", return_value=(request, Path("source.iff"), "bigcourt.dds", [0, 0, 32, 16], Path("background.png"))), patch.object(geometry, "load_geometry", return_value=document) as loader, patch.object(backend, "_ready_import_base", return_value={"selected": "floor.dds"}), patch("zipfile.ZipFile") as archive, patch.object(geometry, "native_export_scene", return_value=b"scene"), patch.object(backend, "build_iff", return_value=Path("output.iff")) as build:
            archive.return_value.__enter__.return_value.read.return_value = b"source-scene"
            backend.export_import_iff(Path("request.json"))
            loader.assert_called_once()
            self.assertIs(build.call_args.kwargs["geometry"], document)


if __name__ == "__main__":
    unittest.main()
