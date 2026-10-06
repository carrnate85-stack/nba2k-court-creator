import copy
import json
import struct
import subprocess
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZIP_DEFLATED, ZipFile

from court_creator import court_import, json_io
from court_creator.experimental_lines import native_export_scene
from test_court_import import solid_dds


def floor_scene():
    return {"Scene": {"Model": {"floor": {"Prim": [{"Mesh": "NBA_full_court_floor_lowShape", "Material": "floor"}]}},
                      "Material": {"floor": {"Resource": {"Logo0Texture": "court.dds", "Logo1Texture": "court.dds"},
                                             "Parameter": {"Logo0ScaleX": .969, "Logo1OffsetY": -.891406}}},
                      "metadata": {"note": "retain"}}}


class SceneValidationTests(unittest.TestCase):
    def test_nonfinite_values_and_excessive_depth_are_rejected(self):
        document = floor_scene()
        for value in (float("nan"), float("inf"), float("-inf")):
            document["Scene"]["Material"]["floor"]["Parameter"]["Logo0ScaleX"] = value
            raw = json.dumps(document).encode()
            for operation in (court_import._scene_document, court_import.clean_floor_scene, native_export_scene):
                with self.subTest(value=value, operation=operation.__name__), self.assertRaises(ValueError):
                    operation(raw)
        for raw in (b'{"metadata":1e400}', b'{"metadata":-1e400}', b'{"nested":' + b'[' * 40 + b'0' + b']' * 40 + b'}'):
            with self.subTest(raw=raw[:40]), self.assertRaises(ValueError):
                court_import._scene_document(raw)

    def test_reserved_scene_shapes_fail_as_clear_value_errors(self):
        original = floor_scene()
        cases = []
        for field in ("Model", "Material", "Effect"):
            for value in (None, [], "not a mapping"):
                document = copy.deepcopy(original); document["Scene"][field] = value; cases.append(document)
        for value in (None, [], "not a model"):
            document = copy.deepcopy(original); document["Scene"]["Model"]["floor"] = value; cases.append(document)
        for value in (None, {}, [None], ["not a primitive"], [{"Mesh": []}], [{"Material": {}}]):
            document = copy.deepcopy(original); document["Scene"]["Model"]["floor"]["Prim"] = value; cases.append(document)
        for field in ("Resource", "Parameter"):
            document = copy.deepcopy(original); document["Scene"]["Material"]["floor"][field] = []; cases.append(document)
        document = copy.deepcopy(original); document["Scene"]["Material"]["floor"] = []; cases.append(document)
        for index, document in enumerate(cases):
            with self.subTest(index=index), self.assertRaisesRegex(ValueError, "floor scene"):
                court_import._scene_document(json.dumps(document).encode())

    def test_scene_byte_limit_precedes_json_parsing(self):
        with patch.object(court_import, "MAX_SCENE_BYTES", 16), patch.object(json_io.json, "loads", side_effect=AssertionError("Oversized scene must not parse")) as parse:
            with self.assertRaisesRegex(ValueError, "floor scene.*size limit"):
                court_import._scene_document(b"{}" + b" " * 16)
            parse.assert_not_called()

    def test_scene_input_types_and_encodings_fail_as_value_errors(self):
        for raw in (None, [], {}, "{}", b'{"name":"\xff"}', "{}".encode("utf-16")):
            with self.subTest(raw=repr(raw)[:30]), self.assertRaisesRegex(ValueError, "floor scene"):
                court_import._scene_document(raw)

    def test_invalid_replacement_scene_precedes_inspection_and_conversion(self):
        with tempfile.TemporaryDirectory(prefix="court-scene-validation-") as temporary:
            root = Path(temporary)
            png, base, tool, output = (root / name for name in ("pixels.png", "base.iff", "texconv.exe", "output.iff"))
            for path in (png, base, tool, output): path.write_bytes(b"protected sentinel: " + path.name.encode())
            before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in (png, base, tool, output)}
            for raw in (b"not a scene", b"{}", b'{"metadata":NaN}', b'{"Scene":{"Model":[]}}', "{}", 1, [], {}):
                with self.subTest(raw=raw), patch.object(court_import, "inspect_iff", side_effect=AssertionError("Invalid replacement must fail before inspecting")) as inspect, patch.object(court_import, "run_tool") as convert, self.assertRaisesRegex(ValueError, "floor scene"):
                    court_import.package_png_into_iff(png, base, "floor.dds", output, tool, scene_override=raw)
                inspect.assert_not_called(); convert.assert_not_called()
                self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before}, before)

    def test_wrapped_fragment_and_bom_inputs_retain_unknown_fields_and_input_bytes(self):
        original = floor_scene()
        original["Scene"]["metadata"].update({"name": "Caf\u00e9 court", "literal": "NaN Infinity", "flags": [True, None, 7]})
        original["version"] = 3
        original["Scene"]["Effect"] = {"court": {"Resource": {"Logo0Texture": {"Texcoord": 0}}, "Parameter": {"extra": [1, 2, 3]}}}
        original["Scene"]["Model"]["floor"]["Prim"].append({"Mesh": "NBA_line_three_point_lowShape", "Count": 9})
        original["Scene"]["Model"]["floor"]["IndexBuffer"] = {"Binary": "floor.bin", "Format": "R16_UINT"}
        text = json.dumps(original, ensure_ascii=False)
        for wrapped in (True, False):
            for bom in (b"", b"\xef\xbb\xbf"):
                raw = bom + b" \r\n" + (text if wrapped else text[1:-1]).encode("utf-8") + b" \r\n"
                before = bytes(raw)
                with self.subTest(wrapped=wrapped, bom=bool(bom)):
                    actual, actual_wrapped = court_import._scene_document(raw)
                    self.assertEqual(actual, original); self.assertEqual(actual_wrapped, wrapped)
                    clean = court_import.clean_floor_scene(raw)
                    cleaned, clean_wrapped = court_import._scene_document(clean)
                    expected = copy.deepcopy(original); expected["Scene"]["Model"]["floor"]["Prim"].pop()
                    self.assertEqual(cleaned, expected); self.assertEqual(clean_wrapped, wrapped)
                    self.assertEqual(court_import.clean_floor_scene(clean), clean)
                    converted, native_wrapped = court_import._scene_document(native_export_scene(clean))
                    self.assertEqual(native_wrapped, wrapped)
                    converted["Scene"]["Material"]["floor"]["Parameter"] = expected["Scene"]["Material"]["floor"]["Parameter"]
                    self.assertEqual(converted, expected); self.assertEqual(raw, before)

    def test_logical_depth_boundary_is_the_same_for_wrapped_and_fragment_scenes(self):
        valid = '{"value":' * json_io.MAX_JSON_DEPTH + '0' + '}' * json_io.MAX_JSON_DEPTH
        invalid = '{"value":' + valid + '}'
        for wrapped in (True, False):
            raw = (valid if wrapped else valid[1:-1]).encode()
            actual, actual_wrapped = court_import._scene_document(raw)
            self.assertIsInstance(actual, dict); self.assertEqual(actual_wrapped, wrapped)
            with self.assertRaisesRegex(ValueError, "floor scene.*nesting"):
                court_import._scene_document((invalid if wrapped else invalid[1:-1]).encode())

    def test_serialized_scene_growth_is_rejected_without_mutating_the_input(self):
        raw = json.dumps(floor_scene(), separators=(",", ":")).encode()
        before = bytes(raw)
        for operation in (court_import.clean_floor_scene, native_export_scene):
            with self.subTest(operation=operation.__name__), patch.object(court_import, "MAX_SCENE_BYTES", len(raw)), self.assertRaisesRegex(ValueError, "prepared floor scene.*size limit"):
                operation(raw)
            self.assertEqual(raw, before)

    def test_serialization_rejects_nonfinite_structured_values(self):
        document = floor_scene(); document["Scene"]["metadata"]["bad"] = float("nan")
        with self.assertRaises(ValueError):
            court_import._serialize_scene_document(document, True)

    def test_raw_json_parser_byte_and_type_budgets_precede_decoding(self):
        for data, limit in ((b"{}", 1), ("{}", 2), (b"{}", True), (b"{}", 0), (b"{}", 2.5)):
            with self.subTest(data=data, limit=limit), patch.object(json_io, "_parse_document", side_effect=AssertionError("Invalid raw input must not decode")) as parse, self.assertRaises(ValueError):
                json_io.parse_document(data, max_bytes=limit)
            parse.assert_not_called()
        self.assertEqual(json_io.parse_document(bytearray(b"{}"), max_bytes=2), {})
        self.assertEqual(json_io.parse_document('\ufeff{"note":"keep"}'.encode("utf-8")), {"note": "keep"})

    def test_absent_or_null_inherited_material_and_opaque_metadata_remain_supported(self):
        document = floor_scene()
        document["Scene"]["Model"]["floor"]["Prim"].extend([{"Mesh": "line_charge_circle_lowShape"}, {"Mesh": "line_center_circle_lowShape", "Material": None}])
        document["opaque"] = ["keep", {"arbitrary": 1}]
        parsed, _wrapped = court_import._scene_document(json.dumps(document).encode())
        self.assertEqual(parsed, document)
        cleaned, _wrapped = court_import._scene_document(court_import.clean_floor_scene(json.dumps(document).encode()))
        self.assertEqual(cleaned["Scene"]["Model"]["floor"]["Prim"], document["Scene"]["Model"]["floor"]["Prim"][:1])
        self.assertEqual(cleaned["opaque"], document["opaque"])

    def test_packaging_freezes_mutable_scene_bytes_before_converter_work(self):
        with tempfile.TemporaryDirectory(prefix="court-scene-snapshot-") as temporary:
            root = Path(temporary)
            png, base, tool, output = (root / name for name in ("pixels.png", "base.iff", "texconv.exe", "output.iff"))
            png.write_bytes(b"protected pixels"); tool.write_bytes(b"protected converter")
            original = json.dumps(floor_scene(), separators=(",", ":")).encode()
            replacement = bytearray(original)
            with ZipFile(base, "w", compression=ZIP_DEFLATED) as archive:
                archive.writestr("floor.dds", b"original pixels")
                archive.writestr("level_floor.SCNE", original)
                archive.writestr("mesh.bin", b"protected geometry")
            before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in (png, base, tool)}
            dds = bytearray(solid_dds(8192, 4096)); struct.pack_into("<I", dds, 28, 1)
            selected = {"name": "floor.dds", "format": "BC1_UNORM", "mipmaps": 1}
            def convert(args, **_options):
                replacement[:] = b"changed to invalid scene during converter work"
                (Path(args[args.index("-o") + 1]) / "court.DDS").write_bytes(dds)
                return subprocess.CompletedProcess(args, 0, "", "")
            with patch.object(court_import, "inspect_iff", return_value={"textures": [selected]}), patch.object(court_import.Image, "open") as image, patch.object(court_import, "run_tool", side_effect=convert), patch.object(court_import, "MAX_SCENE_BYTES", len(original)):
                image.return_value.__enter__.return_value.size = (8192, 4096)
                image.return_value.__enter__.return_value.format = "PNG"
                court_import.package_png_into_iff(png, base, "floor.dds", output, tool, scene_override=replacement)
            with ZipFile(output) as archive:
                self.assertEqual(archive.read("level_floor.SCNE"), original)
                self.assertEqual(archive.read("mesh.bin"), b"protected geometry")
                self.assertEqual(archive.read("floor.dds"), bytes(dds))
                self.assertIsNone(archive.testzip())
            self.assertNotEqual(bytes(replacement), original)
            self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before}, before)
            self.assertFalse(list(root.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
