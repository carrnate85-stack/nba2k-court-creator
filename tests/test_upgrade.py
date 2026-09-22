import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from PIL import Image
from court_creator import backend
from court_creator import court_template
from court_creator.court_template import CourtLayer
import updater

class UpgradeTests(unittest.TestCase):
    def test_legacy_asset_paths_resolve_to_local_project(self):
        with tempfile.TemporaryDirectory() as temporary:
            local_assets = Path(temporary)
            old_floor = backend.LEGACY_ASSET_ROOT / "court_floor_templates" / "nba2k27" / "floor.png"
            old_logo = backend.LEGACY_PROJECT_ROOT / "logos" / "mark.png"
            with patch.object(backend, "ASSET_ROOT", local_assets):
                self.assertEqual(backend.resolve_asset_path(str(old_floor)), local_assets / "court_floor_templates" / "nba2k27" / "floor.png")
            self.assertEqual(backend.resolve_asset_path(str(old_logo)), backend.PROJECT_ROOT / "logos" / "mark.png")

    def test_export_resolution(self):
        with patch.object(backend, "parse_court_psd_layers", return_value=SimpleNamespace(layers=[])), patch.object(backend, "create_visible_court_preview_png") as render:
            backend.render_preview({"templatePath": "test.psd", "exportFullResolution": True})
            self.assertIsNone(render.call_args.kwargs["max_size"])
            backend.render_preview({"templatePath": "test.psd"})
            self.assertEqual(render.call_args.kwargs["max_size"], (2048, 1024))

    def test_update_and_rollback(self):
        for fail in (False, True):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                pending = root / "updates/pending/electron"
                pending.mkdir(parents=True)
                (root / "electron").mkdir()
                (root / "electron/main.js").write_text("old")
                (pending / "main.js").write_text("new")
                (pending.parent / "personal.json").write_text("excluded")
                with patch.object(updater, "ROOT", root), patch.object(updater, "UPDATES", root / "updates"):
                    if fail:
                        with patch.object(updater.os, "replace", side_effect=OSError("busy")):
                            with self.assertRaises(OSError):
                                updater.apply()
                    else:
                        updater.apply()
                self.assertEqual((root / "electron/main.js").read_text(), "old" if fail else "new")
                self.assertEqual((root / "updates/rollback/electron/main.js").read_text(), "old")
                self.assertFalse((root / "personal.json").exists())

    def test_preview_png_is_written_atomically(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "preview.png"
            court_template._save_png_atomic(
                Image.new("RGBA", (8, 4), (12, 34, 56, 255)),
                output,
                fast=True,
            )
            with Image.open(output) as rendered:
                self.assertEqual(rendered.getpixel((0, 0)), (12, 34, 56, 255))
            self.assertFalse(list(output.parent.glob("*.tmp")))

    def test_external_image_cache_refreshes_changed_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "floor.png"
            Image.new("RGB", (16, 8), (10, 20, 30)).save(source)
            first = court_template._cached_external_image(source, (8, 4), fit=True)
            Image.new("RGB", (16, 8), (90, 80, 70)).save(source)
            source.touch()
            second = court_template._cached_external_image(source, (8, 4), fit=True)
            self.assertEqual(first.getpixel((0, 0))[:3], (10, 20, 30))
            self.assertEqual(second.getpixel((0, 0))[:3], (90, 80, 70))

    def test_floor_library_falls_back_when_newest_index_is_invalid(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            old = root / "court_floor_templates" / "nba2k26"
            new = root / "court_floor_templates" / "nba2k27"
            old.mkdir(parents=True)
            new.mkdir(parents=True)
            image = root / "floor.png"
            Image.new("RGB", (8, 4), (1, 2, 3)).save(image)
            (new / "nba2k27_floor_templates.json").write_text("not json")
            (old / "nba2k26_floor_templates.json").write_text(
                '{"name":"NBA 2K26 Floor Templates","templates":['
                f'{{"id":"nba2k26-test","name":"Test Court","path":"{image.as_posix()}","category":"NBA"}}]}}'
            )
            group = CourtLayer("floors", "Court Floors", "group", None, 1, 0, True, 255, "pass", (0, 0, 8, 4))
            base = CourtLayer("base", "Full Floor", "layer", "floors", 2, 1, True, 255, "norm", (0, 0, 8, 4))
            document = SimpleNamespace(layers=(group, base))
            with patch.object(backend, "ASSET_ROOT", root):
                layers, images, name = backend.load_floor_template_layers(document)
            self.assertEqual(name, "NBA 2K26 Courts")
            self.assertEqual(len(images), 1)
            self.assertTrue(any(layer.id == "nba2k26-test" for layer in layers))
            with patch.object(backend, "ASSET_ROOT", root / "missing"), patch.object(backend, "LEGACY_ASSET_ROOT", root):
                _, legacy_images, legacy_name = backend.load_floor_template_layers(document)
            self.assertEqual(legacy_name, "NBA 2K26 Courts")
            self.assertEqual(len(legacy_images), 1)

    def test_floor_library_uses_thumbnail_for_browser_preview(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            library = root / "court_floor_templates" / "nba2k27"
            library.mkdir(parents=True)
            image = library / "floor.png"
            thumbnail = library / "thumbnail.jpg"
            Image.new("RGB", (16, 8), (1, 2, 3)).save(image)
            Image.new("RGB", (8, 4), (4, 5, 6)).save(thumbnail)
            (library / "nba2k27_floor_templates.json").write_text(
                '{"name":"NBA 2K27 Floor Templates","templates":['
                '{"id":"nba2k27-test","name":"Test Court","path":"court_floor_templates/nba2k27/floor.png",'
                '"thumbnailPath":"court_floor_templates/nba2k27/thumbnail.jpg","category":"NBA"}]}'
            )
            group = CourtLayer("floors", "Court Floors", "group", None, 1, 0, True, 255, "pass", (0, 0, 8, 4))
            base = CourtLayer("base", "Full Floor", "layer", "floors", 2, 1, True, 255, "norm", (0, 0, 8, 4))
            document = SimpleNamespace(layers=(group, base))
            with patch.object(backend, "ASSET_ROOT", root):
                _, images, _ = backend.load_floor_template_layers(document)
            self.assertEqual(images[0]["previewPath"], str(thumbnail))
            self.assertEqual(images[0]["path"], "court_floor_templates\\nba2k27\\floor.png")

if __name__ == "__main__":
    unittest.main()
