import copy
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from court_creator import court_import as ci, floor_geometry as fg
from tests.test_stock_extraction import write_floor


class FloorGeometryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.base = self.root / "base.iff"
        self.game = self.root / "game"
        dll = self.game / "data/oodle/oo2core_9_win64.dll"
        dll.parent.mkdir(parents=True)
        dll.touch()
        self.raw = struct.pack("<fff", -975.36, 0, -1889.76) + bytes(16)
        self.packed = b"\x1f\x8b\x21" + bytes(13) + b"oodle-data" + bytes(4) + struct.pack("<I", len(self.raw))
        self.scene = {"level_floor": {"Model": {"floor": {
            "VertexStream": [{"Stride": 28, "Size": 28, "CompressionMethod": 33, "Binary": "VertexBuffer.test.gz"}],
            "IndexBuffer": {"Format": "R16_UINT", "Size": 6, "Binary": "IndexBuffer.test.bin"},
            "Prim": [{"Mesh": "NBA_full_court_floor_lowShape", "Material": "area"}],
        }}}}

    def archive(self, packed=None, scene=None):
        with ZipFile(self.base, "w") as z:
            z.writestr("VertexBuffer.test.bin", self.packed if packed is None else packed)
            z.writestr("IndexBuffer.test.bin", bytes(6))
            z.writestr("level_floor.SCNE", json.dumps(self.scene if scene is None else scene))
            z.writestr("shader", b"unchanged shader")

    def normalize(self):
        with ci.open_iff(self.base) as z:
            return fg.normalized_scene(z, ci.read_iff_scene(z), self.game)

    def test_decodes_vertices_and_preserves_unrelated_scene_fields(self):
        self.archive()
        with patch("tools.export_2k26_court_texture.oodle_decompress", return_value=self.raw) as decode:
            replacements = self.normalize()
        decode.assert_called_once_with(self.game, b"oodle-data", 28)
        self.assertEqual(replacements["VertexBuffer.test.bin"], self.raw)
        actual, _ = ci._scene_document(replacements["level_floor.SCNE"])
        expected = copy.deepcopy(self.scene)
        stream = expected["level_floor"]["Model"]["floor"]["VertexStream"][0]
        stream["Binary"] = "VertexBuffer.test.bin"
        del stream["CompressionMethod"]
        self.assertEqual(actual, expected)
        self.assertNotIn("shader", replacements)

    def test_raw_aliased_buffer_needs_no_game_and_is_idempotent(self):
        self.archive(packed=self.raw)
        with patch.object(ci, "find_nba2k27_root", side_effect=AssertionError("Game discovery")):
            with ci.open_iff(self.base) as z:
                first = fg.normalized_scene(z, ci.read_iff_scene(z))
            with ZipFile(self.root / "normalized.iff", "w") as z:
                for name, raw in first.items():
                    z.writestr(name, raw)
            with ci.open_iff(self.root / "normalized.iff") as z:
                second = fg.normalized_scene(z, ci.read_iff_scene(z))
        self.assertEqual(first, second)

    def test_invalid_sizes_and_stride_rejected_before_codec(self):
        for field, value in (("Size", 0), ("Size", True), ("Size", fg.MAX_BUFFER_BYTES + 1), ("Stride", 0), ("Stride", 3)):
            with self.subTest(field=field, value=value):
                scene = copy.deepcopy(self.scene)
                scene["level_floor"]["Model"]["floor"]["VertexStream"][0][field] = value
                self.archive(scene=scene)
                with patch("tools.export_2k26_court_texture.oodle_decompress", side_effect=AssertionError("Native codec")):
                    with self.assertRaises(ValueError): self.normalize()

    def test_truncated_compressed_header_and_wrong_trailer_rejected(self):
        for data in (self.packed[:20], self.packed[:-4] + struct.pack("<I", 500)):
            self.archive(packed=data)
            with patch("tools.export_2k26_court_texture.oodle_decompress", side_effect=AssertionError("Native codec")):
                with self.assertRaisesRegex(ValueError, "length"): self.normalize()

    def test_codec_errors_are_normal_export_errors(self):
        self.archive()
        with patch("tools.export_2k26_court_texture.oodle_decompress", side_effect=SystemExit("bad data")):
            with self.assertRaisesRegex(ValueError, "could not decode"): self.normalize()
        with patch("tools.export_2k26_court_texture.oodle_decompress", return_value=b"short"):
            with self.assertRaisesRegex(ValueError, "decoded buffer length"): self.normalize()

    def test_missing_buffer_is_rejected(self):
        scene = copy.deepcopy(self.scene)
        scene["level_floor"]["Model"]["floor"]["VertexStream"][0]["Binary"] = "missing.gz"
        self.archive(scene=scene)
        with self.assertRaisesRegex(ValueError, "missing"): self.normalize()

    def test_existing_v2_cache_migrates_atomically_without_extraction(self):
        write_floor(self.base)
        with ZipFile(self.base, "a") as z:
            # The existing fixture scene is replaced below in a fresh ZIP.
            dds = z.read("bigcourt.dds")
        self.archive()
        with ZipFile(self.base, "a") as z:
            z.writestr("bigcourt.dds", dds)
            z.comment = b"CourtCreator-FullCourt-v2"
        before = self.base.read_bytes()
        with patch.object(ci, "find_nba2k27_root", return_value=self.game), patch.object(
                ci, "extracted_stock_floor", side_effect=AssertionError("Unexpected extraction")), patch(
                "tools.export_2k26_court_texture.oodle_decompress", return_value=self.raw), patch.object(
                ci.ZipFile, "testzip", return_value="broken"):
            with self.assertRaisesRegex(ValueError, "validation"):
                ci.prepare_2k27_base(self.base)
        self.assertEqual(self.base.read_bytes(), before)
        with patch.object(ci, "find_nba2k27_root", return_value=self.game), patch.object(
                ci, "extracted_stock_floor", side_effect=AssertionError("Unexpected extraction")), patch(
                "tools.export_2k26_court_texture.oodle_decompress", return_value=self.raw):
            ci.prepare_2k27_base(self.base)
        with ZipFile(self.base) as z:
            self.assertEqual(z.comment, ci.BASE_VERSION)
            self.assertEqual(z.read("VertexBuffer.test.bin"), self.raw)
            self.assertEqual(z.read("bigcourt.dds"), dds)
            self.assertEqual(z.read("shader"), b"unchanged shader")
        self.assertFalse(list(self.root.glob("*.tmp")))

    def lightmap_fixture(self, factor):
        raw = bytearray()
        for x, z in ((-1, -1), (-1, 1), (1, -1), (1, 1), (0, 0)):
            vertex = bytearray(28)
            struct.pack_into("<fff", vertex, 0, x, 0, z)
            struct.pack_into("<hh", vertex, 12, 123, 456)
            struct.pack_into("<hh", vertex, 16, round(x * factor * 32767), round(z * factor * 32767))
            vertex[20:] = b"details!"
            raw.extend(vertex)
        model = {"Min": [-1, 0, -1], "Max": [1, 0, 1], "VertexStream": [{"Stride": 28}],
                 "VertexFormat": {"POSITION0": {"Format": "R32G32B32_FLOAT"},
                                  "TEXCOORD1": {"Format": "R16G16_SNORM", "ByteOffset": 16,
                                                "Scale": [.5, .5], "Offset": [.5, .5]}}}
        return model, bytes(raw)

    def test_regular_lightmap_changes_uv1_only_without_resizing_court(self):
        model, raw = self.lightmap_fixture(1)
        reference, target = self.lightmap_fixture(.8)
        before = copy.deepcopy(model)
        mapped = fg.remap_lightmap(model, raw, reference, target)
        self.assertEqual(mapped, target)
        self.assertEqual(model, before)
        for i in range(0, len(raw), 28):
            self.assertEqual(mapped[i:i + 16], raw[i:i + 16])
            self.assertEqual(mapped[i + 20:i + 28], raw[i + 20:i + 28])

    def test_practice_footprint_cannot_replace_regular_floor(self):
        model, raw = self.lightmap_fixture(1)
        reference = copy.deepcopy(model)
        reference["Max"][0] = 2
        with self.assertRaisesRegex(ValueError, "bounds differ"):
            fg.remap_lightmap(model, raw, reference, raw)

    def test_nonplanar_lightmap_is_rejected(self):
        model, raw = self.lightmap_fixture(1)
        warped = bytearray(raw)
        struct.pack_into("<hh", warped, 4 * 28 + 16, 500, -500)
        with self.assertRaisesRegex(ValueError, "planar lightmap"):
            fg.remap_lightmap(model, raw, model, warped)

    def test_reference_uses_floor_surface_and_excludes_shifted_team_line_vertices(self):
        model, raw = self.lightmap_fixture(1)
        reference, target = self.lightmap_fixture(.8)
        shifted_line = bytearray(target)
        struct.pack_into("<hh", shifted_line, 4 * 28 + 16, 5000, -5000)
        self.assertEqual(fg.remap_lightmap(model, raw, reference, shifted_line, [0, 1, 2, 3]), target)


if __name__ == "__main__":
    unittest.main()
