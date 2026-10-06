import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from PIL import Image

from court_creator.court_template import _cached_external_image, _composite_logo
from court_creator.experimental_lines import render_experimental


class ArtworkAlphaTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "data.png"
        with Image.new("RGBA", (8, 8), (33, 144, 77, 0)) as image:
            image.save(self.path)
        self.revision = hashlib.sha256(self.path.read_bytes()).hexdigest()

    def test_cached_preview_separates_transparency_and_game_data_before_resize(self):
        for mode, expected in ((False, (0, 0, 0, 0)), (True, (33, 144, 77, 255)), (False, (0, 0, 0, 0))):
            with _cached_external_image(self.path, (16, 16), fit=False, source_revision=self.revision, data_alpha=mode) as image:
                self.assertEqual(image.getpixel((8, 8)), expected)
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).hexdigest(), self.revision)

    def test_normal_and_large_logo_paths_keep_game_data_visible_without_changing_source(self):
        for large in (False, True):
            with self.subTest(large=large), patch("court_creator.court_template._LOGO_INTERMEDIATE_MAX_PIXELS", 1 if large else 1_000_000):
                with Image.new("RGBA", (32, 32)) as canvas:
                    logo = {"path": str(self.path), "sourceRevision": self.revision, "x": 8, "y": 8, "width": 16, "height": 16,
                            "artworkAlphaMode": "GameData"}
                    _composite_logo(canvas, logo, 1)
                    self.assertEqual(canvas.getpixel((16, 16)), (33, 144, 77, 255))
                with Image.new("RGBA", (32, 32)) as canvas:
                    logo.pop("artworkAlphaMode")
                    _composite_logo(canvas, logo, 1)
                    self.assertEqual(canvas.getpixel((16, 16)), (0, 0, 0, 0))
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).hexdigest(), self.revision)

    def test_floor_mapping_masks_game_data_only_after_opaque_rgb_resampling(self):
        geometry = {"gameUv": {"hardwoodBounds": [8, 8, 16, 16],
                               "courtSurfacePolygons": [[[8, 8], [24, 8], [24, 24], [8, 24]]]}, "paints": [], "layers": []}
        for mode, expected in (("GameData", (33, 144, 77, 255)), ("Transparency", (0, 0, 0, 0))):
            destination = self.root / (mode + ".png")
            request = {"floor": {"path": str(self.path), "sourceRevision": self.revision, "artworkAlphaMode": mode}, "outsideVisible": False}
            with patch("court_creator.experimental_lines.OUTPUT_SIZE", (32, 32)):
                render_experimental(self.root, request, destination, geometry=geometry)
            with Image.open(destination) as result:
                self.assertEqual(result.getpixel((16, 16)), expected)
                self.assertEqual(result.getpixel((0, 0)), (0, 0, 0, 0))
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).hexdigest(), self.revision)


if __name__ == "__main__":
    unittest.main()
