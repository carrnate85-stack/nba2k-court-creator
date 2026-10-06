import struct
import json
import unittest

from PIL import Image

from court_creator.experimental_lines import (_clip_u, decode_model, decode_floor_surface,
                                             logo_transform, transform_uv, project_polygons,
                                             native_export_scene, derive_court_features,
                                             editor_guides, _polygon_area, derive_two_point_areas,
                                             derive_regulation_lines, regulation_calibration,
                                             _three_point_enclosure, _triangulated_polygons)
from court_creator.court_import import _scene_document
from court_creator.court_template import _strengthen_circle_alpha, _preview_layer_cache_path


class ExperimentalLineTests(unittest.TestCase):
    @staticmethod
    def regulation_fixture():
        def rectangle(x0, z0, x1, z1):
            return [[x0, z0], [x1, z0], [x1, z1], [x0, z1]]
        stroke = 2 / 12
        return [{"id": key, "polygons": value} for key, value in {
            "line_midcourt_center_lowShape": [rectangle(-6, -stroke / 2, 6, stroke / 2)],
            "line_side_base_lowShape": [rectangle(-25 - stroke, -47 - stroke, 25 + stroke, 47 + stroke)],
            "line_charge_circle_lowShape": [rectangle(-4 - stroke, -43, 4 + stroke, -41.75 + 4 + stroke),
                                             rectangle(-4 - stroke, 41.75 - 4 - stroke, 4 + stroke, 43)],
        }.items()]

    def test_regulation_lines_use_outside_edge_dimensions_and_two_inch_width(self):
        from shapely.geometry import Point, Polygon
        from shapely.ops import unary_union

        generated, calibration = derive_regulation_lines(self.regulation_fixture())
        self.assertAlmostEqual(calibration["unitsPerFoot"], 1)
        self.assertEqual(calibration["basketCenters"], [[0, -41.75], [0, 41.75]])
        self.assertEqual([layer["name"] for layer in generated], ["College Three", "High School Three"])
        for layer in generated:
            self.assertFalse(layer["visible"])
            region = unary_union([Polygon(polygon) for polygon in layer["polygons"]])
            radius = layer["dimensions"]["outsideRadiusFeet"]
            corner = layer["dimensions"]["outsideCornerFeet"]
            self.assertAlmostEqual(region.bounds[0], -corner)
            self.assertAlmostEqual(region.bounds[2], corner)
            self.assertAlmostEqual(region.bounds[1], -47)
            self.assertAlmostEqual(region.bounds[3], 47)
            self.assertTrue(region.contains(Point(0, -41.75 + radius - 1 / 12)))
            self.assertFalse(region.contains(Point(0, -41.75 + radius + .01)))
            self.assertFalse(region.contains(Point(0, -41.75 + radius - 2 / 12 - .01)))
            self.assertTrue(region.contains(Point(corner - 1 / 12, -46)))
            self.assertFalse(region.contains(Point(corner - 2 / 12 - .01, -46)))
            self.assertLess(region.symmetric_difference(region.buffer(0)).area, 1e-7)
        self.assertAlmostEqual(generated[0]["dimensions"]["outsideCornerFeet"], 21 + 7.875 / 12)

    def test_calibration_rejects_non_regulation_surface_and_misaligned_baskets(self):
        fixture = self.regulation_fixture()
        fixture[1]["polygons"][0][0][0] -= 10
        with self.assertRaisesRegex(ValueError, "94 by 50"):
            regulation_calibration(fixture)
        fixture = self.regulation_fixture()
        fixture[2]["polygons"][0] = [[x + 10, z] for x, z in fixture[2]["polygons"][0]]
        with self.assertRaisesRegex(ValueError, "basket centers"):
            regulation_calibration(fixture)

    def test_two_point_regions_exclude_key_and_outside_arc_without_lost_area(self):
        from shapely.geometry import Point, Polygon, box
        from shapely.ops import unary_union

        stock_lines = []
        enclosures = []
        paints = []
        for name, baseline, basket_z, direction in (("left", -47, -41.75, 1), ("right", 47, 41.75, -1)):
            outer = _three_point_enclosure([0, basket_z], baseline, direction, 23.75, 22)
            inner = _three_point_enclosure([0, basket_z], baseline, direction, 23.75 - 2 / 12, 22 - 2 / 12)
            stock_lines.extend(_triangulated_polygons(outer.difference(inner)))
            key = box(-8, -47 if direction == 1 else 28, 8, -28 if direction == 1 else 47)
            paints.append({"id": f"paint-{name}", "polygons": [list(key.exterior.coords[:-1])]})
            enclosures.append(outer.difference(key))
        layers = [{"id": "NBA_line_three_point_lowShape", "polygons": stock_lines},
                  {"id": "line_center_circle_outer_lowShape", "polygons": [[[-6, -6], [6, -6], [6, 6], [-6, 6]]]},
                  {"id": "line_side_base_lowShape", "polygons": [[[-25, -47], [25, -47], [25, 47], [-25, 47]]]}]
        areas = derive_two_point_areas(layers, paints)
        for layer, expected in zip(areas, enclosures):
            self.assertFalse(layer["visible"])
            actual = unary_union([Polygon(polygon) for polygon in layer["polygons"]])
            self.assertLess(actual.symmetric_difference(expected).area, 1e-7)
            self.assertAlmostEqual(sum(_polygon_area(p) for p in layer["polygons"]), actual.area)
        left = unary_union([Polygon(polygon) for polygon in areas[0]["polygons"]])
        self.assertTrue(left.contains(Point(12, -35)))
        self.assertFalse(left.contains(Point(0, -40)))
        self.assertFalse(left.contains(Point(24, -35)))

    def test_paints_fill_inner_key_and_secondary_bands_without_overlap(self):
        def rectangle(x0, z0, x1, z1):
            return [[x0, z0], [x1, z0], [x1, z1], [x0, z1]]
        polygons = {
            "line_center_circle_outer_lowShape": [rectangle(-1, -1, 1, 1)],
            "line_midcourt_center_lowShape": [rectangle(-6, -.05, 6, .05)],
            "line_side_base_lowShape": [rectangle(-10.05, -20.05, 10.05, 20.05)],
            "line_lane_inner_lowShape": [rectangle(-3.05, -20.05, 3.05, -11.95), rectangle(-3.05, 11.95, 3.05, 20.05)],
            "line_lane_free_throw_lowShape": [rectangle(-4.05, -20.05, 4.05, -11.95), rectangle(-4.05, 11.95, 4.05, 20.05)],
            "line_free_throw_circle_lowShape": [rectangle(-1, -13, 1, -11), rectangle(-1, 11, 1, 13)],
        }
        paints, anchors = derive_court_features([{"id": key, "polygons": value} for key, value in polygons.items()])
        self.assertEqual(len(paints), 4)
        self.assertEqual(len(anchors), 17)
        self.assertAlmostEqual(sum(_polygon_area(polygon) for paint in paints for polygon in paint["polygons"]), 128)
        self.assertEqual(paints[0]["polygons"][0], rectangle(-3, -20, 3, -12))
        self.assertEqual(next(anchor["point"] for anchor in anchors if anchor["id"] == "left-free-throw"), [0, -12])

    def test_editor_guides_fit_current_bounds_without_mutating_cache(self):
        geometry = {"guides": {"template": {"anchors": [
            {"id": "court-center", "name": "Court Center", "x": 1078 + 6037 / 2, "y": 448 + 3201 / 2},
            {"id": "court-corner-0", "name": "Corner", "x": 1078, "y": 448},
        ]}}}
        guides = editor_guides(geometry, bounds=[10, 20, 600, 300])
        self.assertEqual(guides["center"], {"x": 310, "y": 170})
        self.assertEqual(guides["axes"], {"x": [10, 310], "y": [20, 170]})
        self.assertEqual(geometry["guides"]["template"]["anchors"][1]["x"], 1078)
    def test_material_transform_preserves_center_and_applies_stock_scale(self):
        matrix = logo_transform({"Logo0ScaleX": .969, "Logo0ScaleY": 1.001, "Logo0OffsetY": .0005})
        self.assertAlmostEqual(transform_uv([0, 0], matrix)[0], .0155)
        self.assertAlmostEqual(transform_uv([0, 0], matrix)[1], .0000005)
        rotated = transform_uv([1, .5], logo_transform({"Logo0Rotation": 90}))
        self.assertAlmostEqual(rotated[0], .5)
        self.assertAlmostEqual(rotated[1], 1)

    def test_piecewise_projection_retains_uv_seam_and_covers_surface(self):
        surface = [
            {"points": [[0, 0], [1, 0], [1, 1]], "uvs": [[0, 0], [1, 0], [.8, 1]], "bounds": [0, 0, 1, 1]},
            {"points": [[0, 0], [1, 1], [0, 1]], "uvs": [[0, 0], [.8, 1], [0, 1]], "bounds": [0, 0, 1, 1]},
        ]
        polygons, coverage = project_polygons([[[0, 0], [1, 0], [1, 1], [0, 1]]], surface, logo_transform({}))
        self.assertEqual(len(polygons), 2)
        self.assertAlmostEqual(coverage, 1)
        self.assertTrue(any([6553.6, 4096.0] in polygon for polygon in polygons))
        with self.assertRaisesRegex(ValueError, "coverage"):
            project_polygons([[[0, 0], [2, 0], [2, 2], [0, 2]]], surface, logo_transform({}))

    def test_native_scene_changes_only_secondary_sample_and_preserves_wrapping(self):
        original = {"Scene": {"Model": {"floor": {"Prim": [{"Material": "floor"}]}},
                              "Material": {"floor": {"Resource": {"Logo0Texture": "court.dds", "Logo1Texture": "court.dds"},
                                                     "Parameter": {"Logo0ScaleX": .969, "Logo1OffsetY": -.891406}}}}}
        for wrapped in (True, False):
            text = json.dumps(original)
            changed, actual_wrapped = _scene_document(native_export_scene((text if wrapped else text[1:-1]).encode()))
            self.assertEqual(actual_wrapped, wrapped)
            params = changed["Scene"]["Material"]["floor"]["Parameter"]
            self.assertEqual(params["Logo0ScaleX"], .969)
            self.assertEqual(params["Logo1OffsetX"], 100)
            self.assertEqual(params["Logo1OffsetY"], 100)
            changed["Scene"]["Material"]["floor"]["Parameter"] = original["Scene"]["Material"]["floor"]["Parameter"]
            self.assertEqual(changed, original)
        with self.assertRaises(ValueError):
            native_export_scene(b"{}")

    def test_floor_decoder_rejects_invalid_layout_and_indices(self):
        vertices = b"".join(struct.pack("<fffhh", x, 0, z, u, v) for x, z, u, v in [(0, 0, 0, 0), (1, 0, 32767, 0), (1, 1, 32767, 32767)])
        model = {"VertexStream": [{"Stride": 16}], "VertexFormat": {"POSITION0": {"Format": "R32G32B32_FLOAT"},
                 "TEXCOORD0": {"Format": "R16G16_SNORM", "ByteOffset": 12, "Scale": [1, 1], "Offset": [0, 0]}},
                 "IndexBuffer": {"Format": "R16_UINT"}, "Prim": [{"Mesh": "full_court_floor", "Count": 3}]}
        surface = decode_floor_surface(model, vertices, struct.pack("<3H", 0, 1, 2))
        self.assertEqual(surface[0]["uvs"], [[0, 0], [1, 0], [1, 1]])
        with self.assertRaisesRegex(ValueError, "invalid vertex"):
            decode_floor_surface(model, vertices, struct.pack("<3H", 0, 1, 100))
        with self.assertRaisesRegex(ValueError, "invalid indices"):
            decode_floor_surface(model, vertices, b"")

    def test_circle_boost_preserves_transparency_color_and_source(self):
        source = Image.new("RGBA", (3, 1), (10, 20, 30, 0))
        source.putalpha(Image.frombytes("L", (3, 1), bytes([0, 102, 241])))
        boosted = _strengthen_circle_alpha(source, {"name": "Half Court Circles"})
        self.assertEqual(boosted.getchannel("A").tobytes(), bytes([0, 255, 255]))
        self.assertEqual(source.getpixel((1, 0)), (10, 20, 30, 102))
        self.assertEqual(boosted.getpixel((1, 0)), (10, 20, 30, 255))
        self.assertIs(_strengthen_circle_alpha(source, {"name": "Center line"}), source)

    def test_circle_cache_is_distinct(self):
        key = ("template.psd", 1, 2)
        self.assertNotEqual(_preview_layer_cache_path(key, 29, .25), _preview_layer_cache_path(key, 29, .25, "|circle-alpha-v1"))

    def test_clip_interpolates_geometry_at_visible_stripe(self):
        clipped = _clip_u([(0, 0, 0), (10, 0, 1), (10, 10, 1)], .5, True)
        self.assertEqual(len(clipped), 4)
        self.assertIn((5.0, 0.0, .5), clipped)
        self.assertIn((5.0, 5.0, .5), clipped)

    def test_decoder_skips_floor_and_advances_index_offset(self):
        vertices = b"".join(struct.pack("<fffhh", x, 0, z, u, 0) for x, z, u in [(0, 0, 0), (10, 0, 32767), (10, 10, 32767)])
        model = {"VertexStream": [{"Stride": 16}], "VertexFormat": {"POSITION0": {"Format": "R32G32B32_FLOAT"}, "TEXCOORD0": {"Format": "R16G16_SNORM", "ByteOffset": 12, "Scale": [1], "Offset": [0]}},
                 "IndexBuffer": {"Format": "R16_UINT"}, "Prim": [{"Mesh": "floor", "Count": 3}, {"Mesh": "line_test", "Count": 3}]}
        layers = decode_model(model, vertices, struct.pack("<6H", 0, 1, 2, 0, 1, 2), (.375, .625))
        self.assertEqual(len(layers), 1)
        self.assertTrue(layers[0]["polygons"])
        for polygon in layers[0]["polygons"]:
            for x, z in polygon:
                self.assertGreaterEqual(x, 3.75)
                self.assertLessEqual(x, 6.25)
        with self.assertRaises(ValueError):
            decode_model(model, vertices, struct.pack("<6H", 0, 1, 2, 0, 1, 100), (.375, .625))


if __name__ == "__main__":
    unittest.main()
