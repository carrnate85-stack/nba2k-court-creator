import json
import codecs
import io
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from court_creator import backend
from court_creator import json_io
from PIL import Image


class JsonInputTests(unittest.TestCase):
    def test_oversized_metadata_returns_fallback_without_changing_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "metadata.json"
            payload = b"{}" + b" " * (8 * 1024 * 1024)
            path.write_bytes(payload)
            before = path.stat().st_mtime_ns
            self.assertEqual(backend.read_json(path, "fallback"), "fallback")
            self.assertEqual(path.read_bytes(), payload)
            self.assertEqual(path.stat().st_mtime_ns, before)

    def test_nonfinite_and_deep_metadata_return_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "metadata.json"
            for text in ('{"value":NaN}', '{"value":Infinity}', '{"value":-Infinity}', '{"value":1e400}', '[' * 80 + '0' + ']' * 80):
                with self.subTest(text=text[:32]):
                    path.write_text(text, encoding="utf-8")
                    self.assertEqual(backend.read_json(path, "fallback"), "fallback")

    def test_utf8_utf16_boms_finite_numbers_and_depth_boundary(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "request.json"
            value = {"name": "Caf\u00e9", "brackets": "[\"{ }\"]", "finite": -1.25e30}
            text = json.dumps(value, ensure_ascii=False)
            for payload in (text.encode("utf-8"), text.encode("utf-8-sig"), codecs.BOM_UTF16_LE + text.encode("utf-16-le"), codecs.BOM_UTF16_BE + text.encode("utf-16-be")):
                path.write_bytes(payload)
                self.assertEqual(json_io.read_request(path), value)
            path.write_text('{"nested":' + '[' * 31 + '0' + ']' * 31 + '}', encoding="utf-8")
            self.assertIsInstance(json_io.read_request(path), dict)
            path.write_text('{"nested":' + '[' * 32 + '0' + ']' * 32 + '}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "nesting"):
                json_io.read_request(path)
            path.write_bytes(b'{"value":"\xff"}')
            with self.assertRaisesRegex(ValueError, "Invalid JSON"):
                json_io.read_request(path)

    def test_read_limit_applies_even_when_file_grows_after_stat(self):
        calls = []
        class Stream(io.BytesIO):
            def fileno(self):
                return 42
            def read(self, size=-1):
                calls.append(size)
                return super().read(size)
        stream = Stream(b" " * 512)
        with patch.object(json_io.Path, "open", return_value=stream), patch.object(json_io.os, "fstat", return_value=SimpleNamespace(st_size=0)):
            with self.assertRaisesRegex(ValueError, "size limit"):
                json_io.read_document("growing.json", max_bytes=128)
        self.assertEqual(calls, [129])
        self.assertTrue(stream.closed)

    def test_stat_limit_rejects_before_reading_and_bad_limits_are_rejected(self):
        stream = io.BytesIO(b"{}")
        with patch.object(json_io.Path, "open", return_value=stream), patch.object(json_io.os, "fstat", return_value=SimpleNamespace(st_size=129)), patch.object(stream, "fileno", return_value=42), patch.object(stream, "read") as read:
            with self.assertRaisesRegex(ValueError, "size limit"):
                json_io.read_document("large.json", max_bytes=128)
            read.assert_not_called()
        for limit in (0, -1, True, 128.0):
            with self.assertRaises(ValueError):
                json_io.read_document("unused.json", max_bytes=limit)

    def test_invalid_requests_stop_all_entry_points_before_output_work(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "request.json"
            for text in ("[]", "null", "0", "not json", '{"value":NaN}'):
                path.write_text(text, encoding="utf-8")
                for operation in (backend.render_preview, backend.prepare_logo_editor, backend._import_arguments, backend.export_current_iff):
                    with self.subTest(operation=operation.__name__, text=text), patch.object(backend, "_ready_import_base", side_effect=AssertionError("No export preparation")):
                        with self.assertRaises(ValueError):
                            operation(path)
                self.assertEqual(set(Path(temporary).iterdir()), {path})
            path.write_bytes(b"{}" + b" " * json_io.MAX_REQUEST_BYTES)
            with self.assertRaisesRegex(ValueError, "16 MiB"):
                backend.render_preview(path)

    def test_metadata_collections_preserve_valid_entries_and_ignore_bad_shapes(self):
        valid = {"league": "NBA", "team": "Example", "colors": [{"name": "Red", "hex": "#FF0000"}], "source": "local"}
        broken = {"league": 7, "team": None, "source": {}, "paletteNote": [], "colors": [None, 1, {"hex": 4}, {"hex": "bad"}, {"hex": "#00FF00", "name": {}}]}
        data = {"palettes": [None, 5, valid, broken]}
        original = json.dumps(data)
        with patch.object(backend, "read_json", return_value=data):
            result = backend.load_team_palettes()
        self.assertEqual(result, [valid, {"colors": [{"hex": "#00FF00"}]}])
        self.assertEqual(json.dumps(data), original)
        for data in ({"palettes": None}, {"palettes": {}}, 1, None):
            with patch.object(backend, "read_json", return_value=data):
                self.assertEqual(backend.load_team_palettes(), [])
        for value in (None, "invalid", {}):
            with patch.object(backend, "read_json", return_value={"presets": value}):
                self.assertEqual(backend.load_presets(), [None] * 5)
        presets = {"presets": [{"name": "Keep"}, None, "bad", 5, [], {"name": "Sixth"}]}
        with patch.object(backend, "read_json", return_value=presets):
            self.assertEqual(backend.load_presets(), [{"name": "Keep"}, None, None, None, None])
        self.assertEqual(len(presets["presets"]), 6)

    def test_mixed_floor_indexes_and_custom_boxes_do_not_break_valid_courts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            image = root / "floor.png"; Image.new("RGB", (8, 4), "red").save(image)
            metadata = root / "custom.json"
            geometry = {"gameUv": {"hardwoodBounds": [0, 0, 8192, 4096]}, "paints": [], "layers": []}
            document = backend.stock_document(geometry)
            items = [None, [], {"path": None}, {"path": "\0bad"}, {"path": str(root)}, {"id": "valid", "path": str(image), "bbox": "bad"}, {"id": "valid2", "path": str(image), "bbox": [None, 0, 10, 10]}]
            metadata.write_text(json.dumps({"floors": items}), encoding="utf-8")
            before = metadata.read_bytes(), metadata.stat().st_mtime_ns
            with patch.object(backend, "CUSTOM_FLOORS_META", metadata):
                layers, floors = backend.load_custom_floor_layers(document)
            self.assertEqual([layer.id for layer in layers], ["valid", "valid2"])
            self.assertTrue(all(tuple(floor["bbox"]) == (0, 0, 8192, 4096) for floor in floors))
            self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
            for version, entries in ((27, [None, {"path": 7}]), (26, [{"id": "older", "path": str(image), "thumbnailPath": {}}])):
                directory = root / "court_floor_templates" / f"nba2k{version}"; directory.mkdir(parents=True)
                (directory / f"nba2k{version}_floor_templates.json").write_text(json.dumps({"templates": entries}), encoding="utf-8")
            with patch.object(backend, "ASSET_ROOT", root):
                _, floors, name = backend.load_floor_template_layers(document)
            self.assertEqual(name, "NBA 2K26 Courts")
            self.assertEqual(floors[0]["id"], "older")
            self.assertEqual(floors[0]["previewPath"], str(image))

    def test_real_team_palette_values_are_unchanged(self):
        data = json_io.read_document(backend.TEAM_PALETTES_PATH)
        self.assertEqual(backend.load_team_palettes(), data["palettes"])

    def test_bad_custom_metadata_blocks_import_before_copy_or_preparation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); directory = root / "custom"; directory.mkdir(); metadata = directory / "catalog.json"
            for payload in (b"not json", b"[]", b'{"floors":null}', b"{}" + b" " * json_io.MAX_METADATA_BYTES):
                metadata.write_bytes(payload)
                before = metadata.read_bytes(), metadata.stat().st_mtime_ns
                with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "CUSTOM_FLOORS_META", metadata), patch.object(backend, "CUSTOM_FLOORS_DIR", directory), patch("court_creator.custom_floor_store.CustomFloorStore.copy_image", side_effect=AssertionError("No copy")) as copy, patch("court_creator.experimental_lines.load_geometry", side_effect=AssertionError("No preparation")):
                    with self.assertRaises(ValueError):
                        backend.add_custom_floor(root / "unused.png", native=True)
                    copy.assert_not_called()
                self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
                self.assertEqual(set(directory.iterdir()), {metadata})

    def test_custom_import_appends_without_discarding_missing_rows_or_extra_fields(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); directory = root / "custom"; directory.mkdir()
            source = root / "new.png"; Image.new("RGB", (8, 4), "red").save(source)
            metadata = directory / "catalog.json"
            original = {"version": 7, "note": "retain", "floors": [None, {"id": "missing", "path": str(root / "unavailable.png"), "extra": {"keep": True}}]}
            metadata.write_text(json.dumps(original), encoding="utf-8")
            geometry = {"gameUv": {"hardwoodBounds": [0, 0, 8192, 4096]}, "paints": [], "layers": []}
            before = source.read_bytes(), source.stat().st_mtime_ns
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "CUSTOM_FLOORS_DIR", directory), patch.object(backend, "CUSTOM_FLOORS_META", metadata), patch("court_creator.experimental_lines.load_geometry", return_value=geometry):
                result = backend.add_custom_floor(source, native=True)
            saved = json_io.read_document(metadata)
            self.assertEqual(saved["floors"][:-1], original["floors"])
            self.assertEqual((saved["version"], saved["note"]), (7, "retain"))
            self.assertEqual(saved["floors"][-1]["id"], result["image"]["id"])
            self.assertEqual(Path(result["image"]["path"]).read_bytes(), source.read_bytes())
            self.assertEqual((source.read_bytes(), source.stat().st_mtime_ns), before)

    def test_real_worker_recovers_after_rejecting_bad_request_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "invalid.json"; path.write_text("[]", encoding="utf-8")
            messages = [{"id": 1, "args": ["render", "--request", str(path)]}, {"id": 2, "args": ["load-stock"]}]
            result = subprocess.run([sys.executable, "-B", "-m", "court_creator.service"], cwd=backend.PROJECT_ROOT,
                                    input="".join(json.dumps(item) + "\n" for item in messages), capture_output=True, text=True, encoding="utf-8", timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            responses = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual([item["id"] for item in responses], [1, 2])
            self.assertIn("JSON object", responses[0]["error"])
            self.assertTrue(responses[1]["result"]["customFloorImages"])
            self.assertEqual(path.read_text(encoding="utf-8"), "[]")

    def test_custom_metadata_writer_limit_preserves_existing_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); metadata = root / "catalog.json"
            metadata.write_bytes(b'{"floors":[],"note":"keep"}')
            before = metadata.read_bytes(), metadata.stat().st_mtime_ns
            with patch.object(backend, "CUSTOM_FLOORS_META", metadata), patch.object(backend, "CUSTOM_FLOORS_DIR", root / "unused"), patch.object(backend, "MAX_METADATA_BYTES", 128):
                with self.assertRaisesRegex(ValueError, "size limit"):
                    backend.save_custom_floor_metadata([{"path": "x" * 256}])
            self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
            self.assertFalse((root / "unused").exists())

    def test_failed_custom_catalog_append_removes_only_the_new_copy(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); directory = root / "custom"; directory.mkdir()
            source = root / "new.png"; Image.new("RGB", (8, 4), "red").save(source)
            metadata = directory / "catalog.json"; metadata.write_bytes(b'{"floors":[]}')
            foreign = directory / "keep.txt"; foreign.write_bytes(b"keep")
            before = source.read_bytes(), metadata.read_bytes(), metadata.stat().st_mtime_ns
            geometry = {"gameUv": {"hardwoodBounds": [0, 0, 8192, 4096]}, "paints": [], "layers": []}
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "CUSTOM_FLOORS_DIR", directory), patch.object(backend, "CUSTOM_FLOORS_META", metadata), patch.object(backend, "MAX_METADATA_BYTES", 128), patch("court_creator.experimental_lines.load_geometry", return_value=geometry):
                with self.assertRaisesRegex(ValueError, "size limit"):
                    backend.add_custom_floor(source, native=True)
            self.assertEqual((source.read_bytes(), metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
            self.assertEqual(set(directory.iterdir()), {metadata, foreign, metadata.with_name(metadata.name + ".lock")})
            self.assertEqual(foreign.read_bytes(), b"keep")


if __name__ == "__main__":
    unittest.main()
