import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

from court_creator import backend


class StockWorkspaceTests(unittest.TestCase):
    def test_main_workspace_loads_without_a_psd(self):
        with patch.object(backend, "parse_court_psd_layers", side_effect=AssertionError("No PSD")), patch.object(
            backend, "ensure_preview", side_effect=AssertionError("No PSD preview")
        ):
            state = backend.load_stock_state()
        self.assertEqual(state["buildMode"], "game-uv")
        self.assertEqual(state["templatePath"], "")
        self.assertEqual((state["document"]["width"], state["document"]["height"]), (8192, 4096))
        layers = {item["id"]: item for item in state["document"]["layers"]}
        self.assertEqual(len([item for item in layers.values() if item["parent_id"] == "stock-lines"]), 16)
        self.assertEqual(len([item for item in layers.values() if item["parent_id"] == "stock-paints"]), 6)
        self.assertTrue(layers["NBA_line_three_point_lowShape"]["visible"])
        self.assertFalse(layers["college-three"]["visible"])
        self.assertEqual(layers["stock-outside"]["color"], "#19583F")
        self.assertTrue(state["customFloorImages"])

    def test_native_render_includes_four_logos_and_visibility(self):
        geometry = {"gameUv": {"hardwoodBounds": [0, 0, 8192, 4096],
                               "courtSurfacePolygons": [[[0, 0], [8192, 0], [8192, 4096], [0, 4096]]]},
                    "paints": [], "layers": []}
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            floor, logo, output = root / "floor.png", root / "logo.png", root / "preview.png"
            Image.new("RGBA", (16, 16), "#808080").save(floor)
            Image.new("RGBA", (16, 16), "#FF0000").save(logo)
            logos = [{"id": str(index), "path": str(logo), "x": 1000 + index * 1000, "y": 1000,
                      "width": 400, "height": 200, "rotation": 0, "visible": True}
                     for index in range(4)]
            request = {"buildMode": "game-uv", "mappingMode": "game-uv", "floor": {"path": str(floor)},
                       "logoImages": logos, "outputPath": str(output)}
            with patch("court_creator.experimental_lines.load_geometry", return_value=geometry), patch.object(
                backend, "parse_court_psd_layers", side_effect=AssertionError("No PSD")
            ):
                backend.render_preview(request)
                with Image.open(output) as preview:
                    self.assertEqual(preview.size, (2048, 1024))
                    for index in range(4):
                        self.assertEqual(preview.getpixel((300 + index * 250, 275)), (255, 0, 0, 255))
                logos[0]["visible"] = False
                logos[1]["opacity"] = 0
                backend.render_preview(request)
                with Image.open(output) as preview:
                    self.assertEqual(preview.getpixel((300, 275)), (128, 128, 128, 255))
                    self.assertEqual(preview.getpixel((550, 275)), (128, 128, 128, 255))
                    self.assertEqual(preview.getpixel((800, 275)), (255, 0, 0, 255))

    def test_native_logo_editor_uses_stock_guides_without_psd(self):
        state = backend.load_stock_state()
        with tempfile.TemporaryDirectory() as folder:
            request = {"buildMode": "game-uv", "backgroundOutput": str(Path(folder) / "background.png"),
                       "logoImages": [], "guideBounds": [1, 2, 3, 4]}
            request_path = Path(folder) / "request.json"
            request_path.write_text(json.dumps(request))
            with patch.object(backend, "parse_court_psd_layers", side_effect=AssertionError("No PSD")), patch.object(
                backend, "render_preview"
            ) as render, patch("court_creator.experimental_lines.editor_guides", return_value={"alignment": "Game UV"}) as guides:
                result = backend.prepare_logo_editor(request_path)
            self.assertEqual(guides.call_args.args, (state["geometry"], "game-uv", None))
            self.assertEqual((result["width"], result["height"]), (8192, 4096))
            self.assertEqual(render.call_args.args[0]["buildMode"], "game-uv")
            self.assertEqual(render.call_args.args[0]["logoImages"], [])


if __name__ == "__main__":
    unittest.main()
