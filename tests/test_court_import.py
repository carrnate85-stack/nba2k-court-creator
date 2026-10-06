import json
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image, ImageChops, ImageDraw

from court_creator import backend, court_import
from tools.export_2k26_court_texture import make_dds


def solid_dds(width, height):
    block = struct.pack("<HHI", 0xF800, 0xF800, 0)
    blocks = ((width + 3) // 4) * ((height + 3) // 4)
    return make_dds(width, height, "DXT1", block * blocks)


class CourtImportTests(unittest.TestCase):
    def test_conversion_target_uses_stock_inside_corners(self):
        geometry = {"size": [8192, 4096], "guides": {"game-uv": {"anchors": [
            {"id": f"court-corner-{i}", "x": x, "y": y}
            for i, (x, y) in enumerate(((1082.2, 443.4), (7109.7, 443.4), (7109.7, 3656.6), (1082.2, 3656.6)))
        ]}}}
        with patch("court_creator.experimental_lines.load_geometry", return_value=geometry):
            self.assertEqual(court_import.target_court_bounds(), [1082, 443, 7110, 3657])
            self.assertEqual(court_import.target_court_bounds((1200, 600)), [158, 65, 1042, 536])
            geometry["guides"]["game-uv"]["anchors"].pop()
            with self.assertRaisesRegex(ValueError, "conversion boundary"):
                court_import.target_court_bounds()
        with patch("court_creator.experimental_lines.load_geometry", return_value=None):
            expected = [round(value * scale) for value, scale in zip(court_import.COURT_EDGES, (8192, 4096, 8192, 4096))]
            self.assertEqual(court_import.target_court_bounds(), expected)

    def test_detects_colored_apron_with_planks_paints_and_logos(self):
        for apron, outline in (("#19583F", "white"), ("#9D1220", "black"), ("#173D99", None)):
            image = Image.new("RGB", (2000, 1000), apron)
            draw = ImageDraw.Draw(image)
            draw.rectangle((260, 110, 1740, 890), fill=(195, 158, 98))
            for y in range(120, 886, 18):
                draw.line((261, y, 1739, y), fill=(164, 132, 80), width=2)
            draw.rectangle((260, 380, 490, 620), fill=apron)
            draw.rectangle((1510, 380, 1740, 620), fill=apron)
            draw.ellipse((920, 440, 1080, 560), fill="black", outline="white", width=4)
            draw.line((1000, 110, 1000, 890), fill="white", width=3)
            if outline:
                draw.rectangle((260, 110, 1740, 890), outline=outline, width=4)
            detected = court_import.detect_court_edges(image)
            for actual, expected in zip(detected, (260, 110, 1740, 890)):
                self.assertLessEqual(abs(actual - expected), 12, (apron, detected))

    def test_no_boundary_is_not_inferred_from_a_center_logo(self):
        image = Image.new("RGB", (1200, 600), (190, 150, 95))
        ImageDraw.Draw(image).rectangle((430, 230, 770, 370), fill="white", outline="black", width=5)
        self.assertEqual(court_import.detect_court_edges(image), [0, 0, 1200, 600])

    def test_recorded_export_layout_avoids_decoding_and_boundary_guessing(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "native.iff"
            layout = {"mapping": "game-uv", "texture": "bigcourt.dds", "size": [64, 32], "courtBounds": [9, 4, 55, 28]}
            with ZipFile(path, "w") as archive:
                archive.writestr("bigcourt.dds", solid_dds(64, 32))
                archive.writestr("other.dds", solid_dds(32, 32))
                archive.comment = court_import.LAYOUT_PREFIX + json.dumps(layout).encode()
            with patch.object(court_import, "read_dds", side_effect=AssertionError("Recorded layout should not decode pixels")):
                metadata = court_import.inspect_iff(path)
            self.assertEqual(metadata["sourceBounds"], layout["courtBounds"])
            self.assertEqual(metadata["alignmentSource"], "recorded-game-uv")
            other = court_import.inspect_iff(path, selected="other.dds")
            self.assertEqual(other["alignmentSource"], "image-detection")
            self.assertEqual(other["sourceBounds"], [0, 0, 32, 32])

    def test_invalid_or_mismatched_layout_is_not_trusted(self):
        texture = {"name": "bigcourt.dds", "width": 64, "height": 32}
        valid = {"mapping": "game-uv", "texture": "bigcourt.dds", "size": [64, 32], "courtBounds": [9, 4, 55, 28]}
        values = [None, [], {}, {**valid, "mapping": "template"}, {**valid, "size": [8192, 4096]},
                  {**valid, "texture": "other.dds"}, {**valid, "courtBounds": [True, 4, 55, 28]},
                  {**valid, "courtBounds": [9, 4, 80, 28]}, {**valid, "courtBounds": [55, 4, 9, 28]}]
        for value in values:
            self.assertIsNone(court_import._recorded_layout(court_import.LAYOUT_PREFIX + json.dumps(value).encode(), texture))
        self.assertIsNone(court_import._recorded_layout(court_import.LAYOUT_PREFIX + b"invalid JSON", texture))
        self.assertIsNone(court_import._recorded_layout(court_import.LAYOUT_PREFIX + b" " * 4096, texture))

    def test_already_aligned_full_court_preserves_pixels_and_apron(self):
        with tempfile.TemporaryDirectory() as folder:
            background = Path(folder) / "background.png"
            Image.new("RGB", (20, 10), "magenta").save(background)
            source = Image.new("RGB", (400, 200), "#19583F")
            ImageDraw.Draw(source).rectangle((40, 20, 359, 179), fill=(195, 158, 98), outline="white", width=2)
            with patch.object(court_import, "target_court_bounds", return_value=[40, 20, 360, 180]):
                aligned = court_import._aligned_image(source, [40, 20, 360, 180], background, source.size)
            self.assertIsNone(ImageChops.difference(source, aligned).getbbox())

    def test_preview_import_uses_isolated_output_and_keeps_legacy_default(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / "source.iff"
            with ZipFile(source, "w") as archive:
                archive.writestr("bigfloor.dds", solid_dds(64, 32))
            background = root / "background.png"
            Image.new("RGB", (32, 16), (200, 180, 140)).save(background)
            request = {"sourcePath": str(source), "textureName": "bigfloor.dds", "bounds": [0, 0, 64, 32], "backgroundPath": str(background)}
            request_path = root / "request.json"
            fallback = root / "legacy-preview.png"
            with patch.object(backend, "IMPORT_PREVIEW", fallback):
                for output in (root / "session-a" / "preview.png", root / "session-b" / "preview.png"):
                    request["outputPath"] = str(output)
                    request_path.write_text(json.dumps(request), encoding="utf-8")
                    self.assertEqual(backend.preview_import(request_path)["previewPath"], str(output))
                    with Image.open(output) as preview:
                        self.assertEqual(preview.size, (1200, 600))
                        self.assertEqual(preview.getpixel((600, 300)), (255, 0, 0))
                self.assertFalse(fallback.exists())
                request.pop("outputPath")
                request_path.write_text(json.dumps(request), encoding="utf-8")
                self.assertEqual(backend.preview_import(request_path)["previewPath"], str(fallback))
                self.assertTrue(fallback.exists())

    def test_selects_large_floor_texture_and_decodes_it(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "legacy.iff"
            with ZipFile(path, "w") as archive:
                archive.writestr("logo.dds", solid_dds(32, 32))
                archive.writestr("bigfloor.dds", solid_dds(64, 32))
            result = court_import.inspect_iff(path)
            self.assertEqual(result["selected"], "bigfloor.dds")
            self.assertEqual(court_import.read_dds(path, "bigfloor.dds").size, (64, 32))

    def test_detects_court_edges_inside_black_border(self):
        image = Image.new("RGB", (200, 100), "black")
        ImageDraw.Draw(image).rectangle((32, 16, 167, 83), fill=(180, 110, 70))
        detected = court_import.detect_court_edges(image)
        for actual, expected in zip(detected, (32, 16, 168, 84)):
            self.assertLessEqual(abs(actual - expected), 3)

    def test_aligned_preview_keeps_art_and_fills_exterior(self):
        with tempfile.TemporaryDirectory() as folder:
            background = Path(folder) / "wood.png"
            Image.new("RGB", (32, 16), (200, 180, 140)).save(background)
            source = Image.new("RGB", (200, 100), "black")
            ImageDraw.Draw(source).rectangle((32, 16, 167, 83), fill=(180, 90, 40))
            result = court_import._aligned_image(source, [32, 16, 168, 84], background, (1200, 600))
            self.assertEqual(result.getpixel((600, 300)), (180, 90, 40))
            self.assertEqual(result.getpixel((20, 20)), (0, 0, 0))

    def test_black_apron_and_transparency_are_distinct(self):
        with tempfile.TemporaryDirectory() as folder:
            background = Path(folder) / "wood.png"
            Image.new("RGB", (20, 10), (200, 180, 140)).save(background)
            source = Image.new("RGBA", (200, 100), (0, 0, 0, 0))
            ImageDraw.Draw(source).rectangle((8, 8, 190, 90), fill=(0, 0, 0, 255))
            result = court_import._aligned_image(source, [32, 16, 168, 84], background, (1200, 600))
            self.assertEqual(result.getpixel((80, 300)), (0, 0, 0))
            self.assertEqual(result.getpixel((0, 0)), (200, 180, 140))

    def test_scene_removes_explicit_and_inherited_markings(self):
        scene = {"level_floor": {"Model": {"floor": {"Prim": [
            {"Mesh": "NBA_full_court_floor_lowShape", "Material": "area", "Count": 1752},
            {"Mesh": "NBA_line_three_point_lowShape", "Material": "lines", "Count": 1296},
            {"Mesh": "line_charge_circle_lowShape", "Count": 1416},
        ], "IndexBuffer": {"Binary": "buffer.bin"}}}}}
        raw = json.dumps(scene)[1:-1].encode()
        cleaned, wrapped = court_import._scene_document(court_import.clean_floor_scene(raw))
        self.assertFalse(wrapped)
        model = cleaned["level_floor"]["Model"]["floor"]
        self.assertEqual(model["Prim"], scene["level_floor"]["Model"]["floor"]["Prim"][:1])
        self.assertEqual(model["IndexBuffer"], {"Binary": "buffer.bin"})
        self.assertEqual(court_import.clean_floor_scene(court_import.clean_floor_scene(raw)), court_import.clean_floor_scene(raw))

    def test_recovery_restores_original_and_retains_interrupted_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            base = root / "cache" / "base.iff"
            base.parent.mkdir()
            live = root / "game" / "mods" / court_import.NBA2K27_BASE_ENTRY
            live.parent.mkdir(parents=True)
            (root / "game" / "mod.exe").touch()
            (root / "game" / "manifest").touch()
            live.write_bytes(b"interrupted extraction")
            backup = base.with_name(base.name + ".held-loose")
            backup.write_bytes(b"original user mod")
            journal = base.with_name(base.name + ".extraction.json")
            journal.write_text(json.dumps({"gameRoot": str(root / "game"), "hadLoose": True}))
            court_import._recover_base_extraction(base)
            self.assertEqual(live.read_bytes(), b"original user mod")
            recovered = list(base.parent.glob("interrupted-floor-*.iff"))
            self.assertEqual(len(recovered), 1)
            self.assertEqual(recovered[0].read_bytes(), b"interrupted extraction")
            self.assertFalse(backup.exists())
            self.assertFalse(journal.exists())

    def test_extraction_timeout_does_not_touch_existing_mod(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            game = root / "game"
            game.mkdir()
            (game / "mod.exe").touch()
            (game / "manifest").write_text(f"{court_import.NBA2K27_BASE_ENTRY},0P,0,1\n", encoding="utf-8")
            base = root / "cache" / "base.iff"
            live = game / "mods" / court_import.NBA2K27_BASE_ENTRY
            live.parent.mkdir(parents=True)
            live.write_bytes(b"original user mod")
            with patch.object(court_import, "GAME_SETTINGS_PATH", root / "settings.json"), patch.object(
                court_import, "run_tool", side_effect=subprocess.TimeoutExpired("mod.exe", 180)
            ):
                with self.assertRaises(subprocess.TimeoutExpired):
                    court_import.prepare_2k27_base(base, game)
            self.assertEqual(live.read_bytes(), b"original user mod")
            self.assertFalse(base.with_name(base.name + ".held-loose").exists())
            self.assertFalse(base.with_name(base.name + ".extraction.json").exists())
            self.assertFalse(list(base.parent.glob("*.tmp")))

    def test_recovery_keeps_a_new_mod_when_no_original_backup_exists(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            base = root / "cache" / "base.iff"
            base.parent.mkdir()
            live = root / "game" / "mods" / court_import.NBA2K27_BASE_ENTRY
            live.parent.mkdir(parents=True)
            (root / "game" / "mod.exe").touch()
            (root / "game" / "manifest").touch()
            live.write_bytes(b"court added after interruption")
            journal = base.with_name(base.name + ".extraction.json")
            journal.write_text(json.dumps({"gameRoot": str(root / "game"), "hadLoose": False}))
            court_import._recover_base_extraction(base)
            preserved = list(base.parent.glob("interrupted-floor-*.iff"))
            self.assertEqual(preserved[0].read_bytes(), b"court added after interruption")
            self.assertFalse(journal.exists())

    def test_invalid_recovery_record_never_deletes_backup(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder) / "base.iff"
            backup = base.with_name(base.name + ".held-loose")
            backup.write_bytes(b"original mod")
            journal = base.with_name(base.name + ".extraction.json")
            journal.write_text("interrupted json")
            with self.assertRaises(ValueError):
                court_import._recover_base_extraction(base)
            self.assertEqual(backup.read_bytes(), b"original mod")
            self.assertTrue(journal.exists())

    def test_saved_installation_and_extra_steam_library(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            steam = root / "programs" / "Steam" / "steamapps"
            steam.mkdir(parents=True)
            library = root / "extra-library"
            game = library / "steamapps" / "common" / "NBA 2K27"
            game.mkdir(parents=True)
            (game / "mod.exe").touch()
            (game / "manifest").touch()
            (steam / "libraryfolders.vdf").write_text(
                '"libraryfolders" { "0" { "path" ' + json.dumps(str(library)) + ' } }'
            )
            settings = root / "settings.json"
            with patch.object(court_import, "GAME_SETTINGS_PATH", settings), patch.dict(
                court_import.os.environ, {"ProgramFiles(x86)": str(root / "programs"), "ProgramFiles": str(root / "programs")}
            ):
                self.assertEqual(court_import.find_nba2k27_root(), game.resolve())
                settings.write_text(json.dumps({"nba2k27Root": str(game)}))
                self.assertEqual(court_import.find_nba2k27_root(), game.resolve())

    def test_target_requires_full_court_dds(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "target.iff"
            with ZipFile(path, "w") as archive:
                archive.writestr("small.dds", solid_dds(64, 32))
            with self.assertRaisesRegex(ValueError, "8192 x 4096"):
                court_import.inspect_iff(path, target=True)

    def test_rejects_overwriting_source_or_base_iff(self):
        path = Path("base.iff")
        with self.assertRaisesRegex(ValueError, "new file"):
            court_import.build_iff(path, "floor.dds", [0, 0, 64, 32], path,
                                   path, "floor.dds", path, path)
        source = Path("source.iff")
        with self.assertRaisesRegex(ValueError, "new file"):
            court_import.build_iff(source, "floor.dds", [0, 0, 64, 32], path,
                                   path, "floor.dds", source, path)


if __name__ == "__main__":
    unittest.main()
