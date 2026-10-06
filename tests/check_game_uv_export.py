"""Real local export audit; keeps only a small preview and JSON report."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
from zipfile import ZipFile

from PIL import Image, ImageChops

from court_creator import backend
from court_creator.court_import import _scene_document, inspect_iff, read_dds
from court_creator.experimental_lines import render_experimental


def main():
    root = backend.PROJECT_ROOT
    output = root / "outputs" / "game-uv-export-check"
    output.mkdir(parents=True, exist_ok=True)
    state = backend.experimental_state()
    geometry = state["geometry"]
    assert geometry and geometry["gameUv"]["surfaceTriangles"] == 584
    coverage = geometry["gameUv"]["projectionCoverage"]
    assert all(.999 <= value <= 1.001 for value in coverage.values())
    floor = next((item for item in state["floors"] if "celtics" in item["name"].lower()), state["floors"][0])
    base = backend.NBA2K27_EXPORT_BASE
    base_hash = hashlib.sha256(base.read_bytes()).hexdigest()
    request = {"buildMode": "game-uv", "mappingMode": "game-uv", "floor": floor,
               "paintSettings": {"paint-left": {"visible": True, "color": "#2266CC"},
                                 "secondary-paint-left": {"visible": True, "color": "#FF8800"},
                                 "two-point-left": {"visible": True, "color": "#A020F0"},
                                 "two-point-right": {"visible": True, "color": "#00CCCC"}},
               "outsideColor": "#19583F", "lineSettings": {
                   "college-three": {"visible": True, "color": "#00FFFF"},
                   "high-school-three": {"visible": True, "color": "#FFFF00"},
                   "line_center_circle_outer_lowShape": {"visible": True, "color": "#FF0044"}}}
    with tempfile.TemporaryDirectory(prefix="court-game-uv-audit-") as folder:
        temporary = Path(folder)
        logo_path = temporary / "logo.png"
        Image.new("RGBA", (32, 32), "#FF00FF").save(logo_path)
        request["logoImages"] = [{"id": f"audit-logo-{index}", "path": str(logo_path),
                                  "x": 2800 + index * 650, "y": 1000, "width": 300, "height": 250,
                                  "rotation": index * 15, "opacity": 100, "visible": True}
                                 for index in range(4)]
        request["outputPath"] = str(temporary / "converted.iff")
        request_path = temporary / "request.json"
        request_path.write_text(json.dumps(request), encoding="utf-8")
        backend.export_current_iff(request_path)
        target = Path(request["outputPath"])
        inspected = inspect_iff(target, target=True)
        texture = inspected["selected"]
        descriptor = next(item for item in inspected["textures"] if item["name"] == texture)
        assert (descriptor["width"], descriptor["height"], descriptor["format"], descriptor["mipmaps"]) == (8192, 4096, "BC7_UNORM", 14)
        with ZipFile(base) as original, ZipFile(target) as exported:
            assert exported.testzip() is None
            assert original.namelist() == exported.namelist()
            changed = [name for name in original.namelist() if original.read(name) != exported.read(name)]
            assert set(changed) == {texture, "level_floor.SCNE"}, changed
            original_scene, wrapped = _scene_document(original.read("level_floor.SCNE"))
            exported_scene, exported_wrapped = _scene_document(exported.read("level_floor.SCNE"))
            assert wrapped == exported_wrapped
            restored = copy.deepcopy(exported_scene)
            secondary = {}
            for name, value in original_scene.items():
                if not isinstance(value, dict):
                    continue
                prims = [prim for model in value.get("Model", {}).values() for prim in model.get("Prim", [])]
                assert all("full_court_floor" in prim["Mesh"] for prim in prims)
                for prim in prims:
                    material = value["Material"][prim["Material"]]
                    result_material = exported_scene[name]["Material"][prim["Material"]]
                    secondary = {key: result_material["Parameter"][key] for key in ("Logo1OffsetX", "Logo1OffsetY")}
                    assert secondary == {"Logo1OffsetX": 100.0, "Logo1OffsetY": 100.0}
                    restored[name]["Material"][prim["Material"]]["Parameter"] = material["Parameter"]
            assert restored == original_scene
        image = read_dds(target, texture)
        for index in range(4):
            pixel = image.getpixel((2950 + index * 650, 1125))
            assert pixel[0] > 235 and pixel[1] < 30 and pixel[2] > 235, (index, pixel)
        center = image.crop((3600, 1600, 4600, 2500))
        def color_count(region, predicates):
            masks = [region.getchannel(channel).point(lambda value, predicate=predicate: 255 if predicate(value) else 0)
                     for channel, predicate in zip("RGB", predicates)]
            return ImageChops.multiply(ImageChops.multiply(masks[0], masks[1]), masks[2]).histogram()[255]
        red_pixels = color_count(center, [lambda v: v > 210, lambda v: v < 40, lambda v: 30 < v < 110])
        blue_paint_pixels = color_count(image.crop((1300, 1800, 2000, 2300)), [lambda v: v < 60, lambda v: 70 < v < 130, lambda v: v > 170])
        orange_band_pixels = color_count(image.crop((1300, 1550, 2000, 1650)), [lambda v: v > 210, lambda v: 90 < v < 175, lambda v: v < 40])
        assert red_pixels > 1000, red_pixels
        assert blue_paint_pixels > 10000, blue_paint_pixels
        assert orange_band_pixels > 10000, orange_band_pixels
        purple_area_pixels = color_count(image, [lambda v: 130 < v < 190, lambda v: v < 60, lambda v: v > 210])
        teal_area_pixels = color_count(image, [lambda v: v < 40, lambda v: 175 < v < 230, lambda v: 175 < v < 230])
        college_pixels = color_count(image, [lambda v: v < 40, lambda v: v > 235, lambda v: v > 235])
        high_school_pixels = color_count(image, [lambda v: v > 235, lambda v: v > 235, lambda v: v < 40])
        assert purple_area_pixels > 100000, purple_area_pixels
        assert teal_area_pixels > 100000, teal_area_pixels
        assert college_pixels > 1000, college_pixels
        assert high_school_pixels > 1000, high_school_pixels
        image.thumbnail((1600, 800), Image.Resampling.LANCZOS)
        image.save(output / "decoded-export.png")
        render_experimental(root, {**request, "mappingMode": "template"}, output / "template-comparison.png", preview=True)
    assert hashlib.sha256(base.read_bytes()).hexdigest() == base_hash
    report = {"changedEntries": changed, "texture": descriptor, "baseUnchanged": True,
              "stockGeometryCoverage": coverage, "gameUvHardwoodBounds": geometry["gameUv"]["hardwoodBounds"],
              "templateHardwoodBounds": [1078, 448, 6037, 3201], "secondarySample": secondary,
              "centerCircleRedPixels": red_pixels, "inGameVerified": False}
    report["paintPixels"] = {"primaryBlue": blue_paint_pixels, "secondaryOrange": orange_band_pixels}
    report["newLayerPixels"] = {"leftTwoPoint": purple_area_pixels, "rightTwoPoint": teal_area_pixels,
                               "collegeThree": college_pixels, "highSchoolThree": high_school_pixels}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
