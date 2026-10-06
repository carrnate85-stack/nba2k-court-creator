"""Decode stock marking meshes into independent, recolorable 2D polygons."""
from __future__ import annotations

from io import BytesIO
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import threading

from PIL import Image, ImageDraw, ImageOps

from .court_import import (NBA2K27_BASE_ENTRY, OUTPUT_SIZE, _base_file_lock,
                          _scene_document, _serialize_scene_document, _write_json_atomic, extracted_stock_floor, find_nba2k27_root,
                          open_iff, read_iff_scene)
from .export_io import ensure_new_export
from .asset_io import asset_revision, validate_asset_image, verified_asset_stream
from tools.export_2k26_court_texture import oodle_decompress


GEOMETRY_VERSION = 4
MAX_GEOMETRY_BYTES = 16 * 1024 * 1024
_GEOMETRY_LOCK = threading.Lock()
TEMPLATE_BOUNDS = (1078, 448, 6037, 3201)
NAMES = {
    "line_center_circle_inner_lowShape": "Center Circle - Inner",
    "line_center_circle_outer_lowShape": "Center Circle - Outer",
    "line_midcourt_side_lowShape": "Center Line - Sides",
    "line_midcourt_center_lowShape": "Center Line - Center",
    "NBA_line_three_point_lowShape": "NBA Three",
    "line_free_throw_circle_lowShape": "Free Throw Circles",
    "line_charge_circle_lowShape": "Charge Circles",
    "line_side_base_lowShape": "Court Boundary",
    "line_camera_lowShape": "Media Lines",
    "line_tab_low_3Shape": "Sideline Hash Marks",
    "line_tab_lane_inner_lowShape": "Inner Lane Hash Marks",
    "line_lane_inner_lowShape": "Inner Key Lines",
    "line_tab_lane_lowShape": "Lane Hash Marks",
    "line_lane_free_throw_lowShape": "Key Lines",
}


def _decode_buffer(data: bytes, game_root: Path, expected_size: int) -> bytes:
    if data[:3] == b"\x1f\x8b\x21":
        if len(data) < 24 or struct.unpack_from("<I", data, len(data) - 4)[0] != expected_size:
            raise ValueError("Unexpected stock vertex-buffer size.")
        data = oodle_decompress(game_root, data[16:-8], expected_size)
    if len(data) != expected_size:
        raise ValueError("The stock vertex buffer has an invalid length.")
    return data


def _clip_u(points: list[tuple[float, float, float]], bound: float, keep_above: bool):
    result = []
    previous = points[-1]
    previous_inside = previous[2] >= bound if keep_above else previous[2] <= bound
    for point in points:
        inside = point[2] >= bound if keep_above else point[2] <= bound
        if inside != previous_inside:
            ratio = (bound - previous[2]) / (point[2] - previous[2])
            result.append(tuple(previous[i] + ratio * (point[i] - previous[i]) for i in range(3)))
        if inside:
            result.append(point)
        previous, previous_inside = point, inside
    return result


def decode_model(model: dict, vertices: bytes, indices: bytes, alpha_span: tuple[float, float]) -> list[dict]:
    stream = model["VertexStream"][0]
    stride = int(stream["Stride"])
    vertex_format = model["VertexFormat"]
    if vertex_format["POSITION0"]["Format"] != "R32G32B32_FLOAT" or vertex_format["TEXCOORD0"]["Format"] != "R16G16_SNORM":
        raise ValueError("This stock court uses an unsupported vertex layout.")
    uv = vertex_format["TEXCOORD0"]
    uv_offset = int(uv["ByteOffset"])
    uv_scale = float(uv["Scale"][0])
    uv_bias = float(uv["Offset"][0])
    index_format = model["IndexBuffer"]["Format"]
    if index_format not in {"R16_UINT", "R32_UINT"}:
        raise ValueError("Unsupported stock index format.")
    index_code, index_size = ("H", 2) if index_format == "R16_UINT" else ("I", 4)
    cursor = 0
    layers = []
    for prim in model.get("Prim", []):
        count = int(prim["Count"])
        if count < 0 or cursor + count * index_size > len(indices):
            raise ValueError("A stock primitive references invalid indices.")
        ids = struct.unpack_from("<" + index_code * count, indices, cursor)
        cursor += count * index_size
        mesh = str(prim.get("Mesh", ""))
        if "line" not in mesh.casefold():
            continue
        if prim.get("Type", "TRIANGLE_LIST") != "TRIANGLE_LIST" or count % 3:
            raise ValueError("Unsupported stock marking topology.")
        points = []
        for index in ids:
            offset = index * stride
            if offset + stride > len(vertices):
                raise ValueError("A marking references an invalid vertex.")
            x, _, z = struct.unpack_from("<fff", vertices, offset)
            packed_u = struct.unpack_from("<h", vertices, offset + uv_offset)[0]
            u = max(-1.0, packed_u / 32767) * uv_scale + uv_bias
            if not all(math.isfinite(value) for value in (x, z, u)):
                raise ValueError("Non-finite stock geometry.")
            points.append((x, z, u))
        polygons = []
        for start in range(0, len(points), 3):
            triangle = points[start:start + 3]
            first, last = math.floor(min(p[2] for p in triangle)), math.floor(max(p[2] for p in triangle))
            if last - first > 8:
                raise ValueError("Unexpected texture wrapping in marking geometry.")
            # Mesh ribbons are wider than the visible stripe; retain its alpha mask.
            for tile in range(first, last + 1):
                clipped = _clip_u(triangle, tile + alpha_span[0], True)
                if clipped:
                    clipped = _clip_u(clipped, tile + alpha_span[1], False)
                if len(clipped) >= 3:
                    polygons.append([[p[0], p[1]] for p in clipped])
        layers.append({"id": mesh, "name": NAMES.get(mesh, mesh), "visible": "inner_lowShape" not in mesh or "center_circle" not in mesh,
                       "color": "#FFFFFF", "polygons": polygons, "sourceTriangles": count // 3})
    if cursor != len(indices):
        raise ValueError("Not all stock indices were accounted for.")
    return layers


def logo_transform(parameters: dict) -> list[list[float]]:
    angle = math.radians(float(parameters.get("Logo0Rotation", 0)))
    scale_x = float(parameters.get("Logo0ScaleX", 1))
    scale_y = float(parameters.get("Logo0ScaleY", 1))
    center_x = -.5 + float(parameters.get("Logo0OffsetX", 0))
    center_y = -.5 + float(parameters.get("Logo0OffsetY", 0))
    a, b = math.cos(angle) * scale_x, -math.sin(angle) * scale_x
    c, d = math.sin(angle) * scale_y, math.cos(angle) * scale_y
    # floor.b442...script generates these rows; the stock pixel shader consumes them.
    return [[a, b, .5 + a * center_x + b * center_y],
            [c, d, .5 + c * center_x + d * center_y]]


def transform_uv(uv, matrix):
    return [row[0] * uv[0] + row[1] * uv[1] + row[2] for row in matrix]


def _cross(a, b, p):
    return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])


def _clip_polygon(polygon, boundary):
    orientation = 1 if _cross(boundary[0], boundary[1], boundary[2]) >= 0 else -1
    result = polygon
    for start, end in zip(boundary, [*boundary[1:], boundary[0]]):
        if not result:
            break
        source, result = result, []
        previous = source[-1]
        previous_distance = orientation * _cross(start, end, previous)
        for point in source:
            distance = orientation * _cross(start, end, point)
            if (distance >= 0) != (previous_distance >= 0):
                ratio = previous_distance / (previous_distance - distance)
                result.append([previous[i] + ratio * (point[i] - previous[i]) for i in range(2)])
            if distance >= 0:
                result.append(point)
            previous, previous_distance = point, distance
    return result


def _polygon_area(polygon):
    return abs(sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(polygon, [*polygon[1:], polygon[0]]))) / 2 if polygon else 0


def decode_floor_surface(model: dict, vertices: bytes, indices: bytes) -> list[dict]:
    descriptor = model["VertexFormat"]["TEXCOORD0"]
    stride = int(model["VertexStream"][0]["Stride"])
    if (model["VertexFormat"]["POSITION0"]["Format"] != "R32G32B32_FLOAT"
            or descriptor["Format"] != "R16G16_SNORM"
            or model["IndexBuffer"]["Format"] not in {"R16_UINT", "R32_UINT"}):
        raise ValueError("Unsupported stock floor vertex layout.")
    code, size = ("H", 2) if model["IndexBuffer"]["Format"] == "R16_UINT" else ("I", 4)
    cursor = 0
    for prim in model["Prim"]:
        count = int(prim["Count"])
        if count < 0 or cursor + count * size > len(indices):
            raise ValueError("A stock floor references invalid indices.")
        ids = struct.unpack_from("<" + code * count, indices, cursor)
        cursor += count * size
        if "full_court_floor" not in str(prim.get("Mesh", "")):
            continue
        if count % 3 or prim.get("Type", "TRIANGLE_LIST") != "TRIANGLE_LIST":
            raise ValueError("Unsupported stock floor topology.")
        points, uvs = [], []
        for index in ids:
            if index * stride + stride > len(vertices):
                raise ValueError("A stock floor references an invalid vertex.")
            x, _, z = struct.unpack_from("<fff", vertices, index * stride)
            packed = struct.unpack_from("<hh", vertices, index * stride + descriptor["ByteOffset"])
            uv = [max(-1, packed[i] / 32767) * descriptor["Scale"][i] + descriptor["Offset"][i] for i in range(2)]
            if not all(math.isfinite(value) for value in (x, z, *uv)):
                raise ValueError("Non-finite stock floor geometry.")
            points.append([x, z])
            uvs.append(uv)
        triangles = []
        for start in range(0, count, 3):
            triangle = points[start:start + 3]
            if abs(_cross(*triangle)) < 1e-8:
                continue
            triangles.append({"points": triangle, "uvs": uvs[start:start + 3],
                              "bounds": [min(p[0] for p in triangle), min(p[1] for p in triangle),
                                         max(p[0] for p in triangle), max(p[1] for p in triangle)]})
        return triangles
    raise ValueError("The stock floor surface is missing.")


def project_polygons(polygons, surface, matrix):
    mapped, source_area, mapped_area = [], 0.0, 0.0
    for polygon in polygons:
        source_area += _polygon_area(polygon)
        bounds = [min(p[0] for p in polygon), min(p[1] for p in polygon), max(p[0] for p in polygon), max(p[1] for p in polygon)]
        for triangle in surface:
            b = triangle["bounds"]
            if bounds[0] > b[2] or bounds[2] < b[0] or bounds[1] > b[3] or bounds[3] < b[1]:
                continue
            clipped = _clip_polygon(polygon, triangle["points"])
            area = _polygon_area(clipped)
            if area < 1e-8:
                continue
            mapped_area += area
            a, b, c = triangle["points"]
            denominator = _cross(a, b, c)
            pixels = []
            for point in clipped:
                weights = [_cross(b, c, point) / denominator, _cross(c, a, point) / denominator, _cross(a, b, point) / denominator]
                uv = [sum(weights[j] * triangle["uvs"][j][i] for j in range(3)) for i in range(2)]
                sample = transform_uv(uv, matrix)
                pixels.append([round(sample[0] * OUTPUT_SIZE[0], 4), round(sample[1] * OUTPUT_SIZE[1], 4)])
            mapped.append(pixels)
    coverage = mapped_area / source_area if source_area else 1
    if not .999 <= coverage <= 1.001:
        raise ValueError(f"Stock surface projection coverage is invalid: {coverage:.6f}")
    return mapped, coverage


def native_export_scene(data: bytes) -> bytes:
    scene, wrapped = _scene_document(data)
    changed = 0
    for value in scene.values():
        if not isinstance(value, dict):
            continue
        active = {p.get("Material") for model in value.get("Model", {}).values() for p in model.get("Prim", [])}
        for name in active:
            material = value.get("Material", {}).get(name, {})
            if "Logo0Texture" in material.get("Resource", {}) and "Logo1Texture" in material["Resource"]:
                # A baked full-court texture must not be sampled again as a second logo.
                material.setdefault("Parameter", {}).update({"Logo1OffsetX": 100.0, "Logo1OffsetY": 100.0,
                                                            "Logo1ScaleX": 1.0, "Logo1ScaleY": 1.0, "Logo1Rotation": 0.0})
                changed += 1
    if changed != 1:
        raise ValueError("Could not isolate the primary full-court material.")
    return _serialize_scene_document(scene, wrapped)


def _bounds(polygons):
    points = [point for polygon in polygons for point in polygon]
    if not points:
        raise ValueError("A stock outline has no geometry.")
    return [min(p[0] for p in points), min(p[1] for p in points),
            max(p[0] for p in points), max(p[1] for p in points)]


def _rectangle(bounds):
    x0, z0, x1, z1 = bounds
    if x1 <= x0 or z1 <= z0:
        raise ValueError("Invalid stock-derived paint area.")
    return [[x0, z0], [x1, z0], [x1, z1], [x0, z1]]


def derive_court_features(layers):
    """Build filled key regions and placement anchors from visible stock outlines."""
    by_id = {layer["id"]: layer["polygons"] for layer in layers}
    circle = _bounds(by_id["line_center_circle_outer_lowShape"])
    center = [(circle[0] + circle[2]) / 2, (circle[1] + circle[3]) / 2]
    midcourt = _bounds(by_id["line_midcourt_center_lowShape"])
    half_stroke = (midcourt[3] - midcourt[1]) / 2
    if not 0 < half_stroke < 10:
        raise ValueError("Unexpected stock marking width.")
    boundary = _bounds(by_id["line_side_base_lowShape"])
    court = [boundary[0] + half_stroke, boundary[1] + half_stroke,
             boundary[2] - half_stroke, boundary[3] - half_stroke]
    paints = []
    anchors = [{"id": "court-center", "name": "Court Center", "point": center}]
    for side, below in (("Left", True), ("Right", False)):
        def side_bounds(layer_id):
            polygons = [polygon for polygon in by_id[layer_id]
                        if (sum(p[1] for p in polygon) / len(polygon) < center[1]) == below]
            bounds = _bounds(polygons)
            return [bounds[0] + half_stroke, bounds[1] + half_stroke,
                    bounds[2] - half_stroke, bounds[3] - half_stroke]

        inner = side_bounds("line_lane_inner_lowShape")
        outer = side_bounds("line_lane_free_throw_lowShape")
        if not outer[0] < inner[0] < inner[2] < outer[2]:
            raise ValueError("Stock inner and outer key outlines do not nest.")
        inner[1], inner[3] = outer[1], outer[3]
        paints.append({"id": f"paint-{side.lower()}", "name": f"{side} Paint", "visible": True,
                       "color": "#19583F", "polygons": [_rectangle(inner)]})
        paints.append({"id": f"secondary-paint-{side.lower()}", "name": f"{side} Secondary Paint",
                       "visible": True, "color": "#19583F", "polygons": [
                           _rectangle([outer[0], outer[1], inner[0], outer[3]]),
                           _rectangle([inner[2], outer[1], outer[2], outer[3]])]})
        free_circle = _bounds([polygon for polygon in by_id["line_free_throw_circle_lowShape"]
                               if (sum(p[1] for p in polygon) / len(polygon) < center[1]) == below])
        anchors.append({"id": f"{side.lower()}-free-throw", "name": f"{side} Free Throw Center",
                        "point": [(free_circle[0] + free_circle[2]) / 2, (free_circle[1] + free_circle[3]) / 2]})
        anchors.append({"id": f"{side.lower()}-paint-center", "name": f"{side} Paint Center",
                        "point": [(outer[0] + outer[2]) / 2, (outer[1] + outer[3]) / 2]})
        for index, point in enumerate(_rectangle(outer)):
            anchors.append({"id": f"{side.lower()}-key-{index}", "name": f"{side} Key Corner {index + 1}", "point": point})
    for index, point in enumerate(_rectangle(court)):
        anchors.append({"id": f"court-corner-{index}", "name": f"Court Corner {index + 1}", "point": point})
    return paints, anchors


def _triangulated_polygons(region):
    from shapely import constrained_delaunay_triangles

    if region.is_empty or not region.is_valid:
        raise ValueError("Invalid derived court region.")
    polygons = [list(map(list, triangle.exterior.coords[:-1]))
                for triangle in constrained_delaunay_triangles(region).geoms]
    area = sum(_polygon_area(polygon) for polygon in polygons)
    if not polygons or not math.isclose(area, region.area, rel_tol=1e-7, abs_tol=1e-6):
        raise ValueError("Court region triangulation lost coverage.")
    return polygons


def derive_two_point_areas(layers, paints):
    """Fill the stock NBA three-point enclosure, excluding the entire outer key."""
    from shapely.geometry import MultiPoint, box

    by_id = {layer["id"]: layer["polygons"] for layer in layers}
    center_z = sum(_bounds(by_id["line_center_circle_outer_lowShape"])[i] for i in (1, 3)) / 2
    court = box(*_bounds(by_id["line_side_base_lowShape"]))
    result = []
    for side, below in (("Left", True), ("Right", False)):
        points = [point for polygon in by_id["NBA_line_three_point_lowShape"]
                  if (sum(p[1] for p in polygon) / len(polygon) < center_z) == below
                  for point in polygon]
        key = box(*_bounds([polygon for paint in paints if paint["id"].endswith(side.lower())
                           for polygon in paint["polygons"]]))
        region = MultiPoint(points).convex_hull.intersection(court).difference(key)
        result.append({"id": f"two-point-{side.lower()}", "name": f"{side} Two-Point Area",
                       "visible": False, "color": "#19583F", "polygons": _triangulated_polygons(region),
                       "source": "Stock NBA three-point outline minus stock outer key"})
    return result


def regulation_calibration(layers):
    by_id = {layer["id"]: layer["polygons"] for layer in layers}
    midcourt = _bounds(by_id["line_midcourt_center_lowShape"])
    stroke = midcourt[3] - midcourt[1]
    if not 0 < stroke < 20:
        raise ValueError("Unexpected stock marking width.")
    boundary = _bounds(by_id["line_side_base_lowShape"])
    # Court dimensions are measured at the inside edges, not the line centers.
    court = [boundary[0] + stroke, boundary[1] + stroke,
             boundary[2] - stroke, boundary[3] - stroke]
    width_scale = (court[2] - court[0]) / 50
    length_scale = (court[3] - court[1]) / 94
    if not math.isclose(width_scale, length_scale, rel_tol=.002):
        raise ValueError("Stock court does not match a 94 by 50 foot regulation surface.")
    unit = (width_scale + length_scale) / 2
    if not math.isclose(stroke / unit, 2 / 12, rel_tol=.02):
        raise ValueError("Stock marking width does not match two inches.")
    center_x = (court[0] + court[2]) / 2
    baskets = [[center_x, court[1] + 5.25 * unit], [center_x, court[3] - 5.25 * unit]]
    for basket, below in zip(baskets, (True, False)):
        charge = _bounds([polygon for polygon in by_id["line_charge_circle_lowShape"]
                          if (sum(p[1] for p in polygon) / len(polygon) < (court[1] + court[3]) / 2) == below])
        apex = charge[3] if below else charge[1]
        if abs((charge[0] + charge[2]) / 2 - basket[0]) > .05 * unit or not 3.95 < abs(apex - basket[1]) / unit < 4.25:
            raise ValueError("Inferred basket centers do not align with stock restricted-area arcs.")
    return {"unitsPerFoot": unit, "courtInsideBounds": court, "basketCenters": baskets,
            "basketSetbackFeet": 5.25,
            "source": "Stock boundary inside edges; 94 x 50 feet; 63-inch basket setback; stock charge-arc validation"}


def _three_point_enclosure(basket, baseline, direction, radius, corner):
    from shapely.geometry import Polygon

    angle = math.asin(corner / radius)
    arc = [[basket[0] + radius * math.sin(-angle + 2 * angle * i / 160),
            basket[1] + direction * radius * math.cos(-angle + 2 * angle * i / 160)]
           for i in range(161)]
    return Polygon([[basket[0] - corner, baseline], *arc, [basket[0] + corner, baseline]])


def derive_regulation_lines(layers):
    from shapely.geometry import box

    calibration = regulation_calibration(layers)
    unit = calibration["unitsPerFoot"]
    court = calibration["courtInsideBounds"]
    generated = []
    for layer_id, name, radius_feet, corner_feet, source in (
        ("college-three", "College Three", 22 + 1.75 / 12, 21 + 7.875 / 12,
         "https://ncaaorg.s3.amazonaws.com/championships/sports/basketball/rules/common/PRXBB_CourtDiagram.pdf"),
        ("high-school-three", "High School Three", 19.75, 19.75,
         "https://assets.nfhs.org/umbraco/media/7213108/basketball-court-diagram.pdf"),
    ):
        polygons = []
        for basket, baseline, direction in zip(calibration["basketCenters"], (court[1], court[3]), (1, -1)):
            outer = _three_point_enclosure(basket, baseline, direction, radius_feet * unit, corner_feet * unit)
            inner = _three_point_enclosure(basket, baseline, direction,
                                           (radius_feet - 2 / 12) * unit, (corner_feet - 2 / 12) * unit)
            polygons.extend(_triangulated_polygons(outer.difference(inner).intersection(box(*court))))
        generated.append({"id": layer_id, "name": name, "visible": False, "color": "#FFFFFF",
                          "polygons": polygons, "source": source, "generated": True,
                          "dimensions": {"outsideRadiusFeet": radius_feet, "outsideCornerFeet": corner_feet,
                                         "lineWidthInches": 2}})
    return generated, calibration


def project_anchor(point, surface, matrix):
    for triangle in surface:
        a, b, c = triangle["points"]
        denominator = _cross(a, b, c)
        weights = [_cross(b, c, point) / denominator, _cross(c, a, point) / denominator,
                   _cross(a, b, point) / denominator]
        if min(weights) >= -1e-8:
            uv = [sum(weights[j] * triangle["uvs"][j][i] for j in range(3)) for i in range(2)]
            sample = transform_uv(uv, matrix)
            return [round(sample[i] * OUTPUT_SIZE[i], 4) for i in range(2)]
    raise ValueError("A stock court anchor falls outside the floor surface.")


def editor_guides(geometry, mapping="template", bounds=None):
    if mapping not in {"template", "game-uv"}:
        raise ValueError("Unsupported guide alignment.")
    source = geometry["guides"][mapping]
    result = json.loads(json.dumps(source))
    if mapping == "template" and bounds is not None:
        left, top, width, height = bounds
        if width <= 0 or height <= 0:
            raise ValueError("Invalid guide alignment bounds.")
        for anchor in result["anchors"]:
            anchor["x"] = left + (anchor["x"] - TEMPLATE_BOUNDS[0]) * width / TEMPLATE_BOUNDS[2]
            anchor["y"] = top + (anchor["y"] - TEMPLATE_BOUNDS[1]) * height / TEMPLATE_BOUNDS[3]
    center = next(anchor for anchor in result["anchors"] if anchor["id"] == "court-center")
    result["center"] = {"x": center["x"], "y": center["y"]}
    ordered = sorted(result["anchors"], key=lambda anchor: 0 if anchor["id"] == "court-center" else
                     1 if anchor["id"].startswith("court-corner-") else 2 if "free-throw" in anchor["id"] else 3)
    result["axes"] = {}
    for axis in ("x", "y"):
        values = []
        for anchor in ordered:
            if not any(abs(value - anchor[axis]) < 8 for value in values):
                values.append(round(anchor[axis], 3))
        result["axes"][axis] = sorted(values)
    result["alignment"] = "Game UV" if mapping == "game-uv" else "Template Aligned"
    return result


def geometry_path(project_root: Path) -> Path:
    return project_root / "data" / "generated" / "experimental-stock-lines.json"


def _valid_geometry(document) -> bool:
    def number(value, maximum=65536):
        return type(value) in (int, float) and abs(value) <= maximum and math.isfinite(value)

    point_count = 0
    def polygons(value):
        nonlocal point_count
        if not isinstance(value, list) or not 1 <= len(value) <= 50000:
            return False
        for polygon in value:
            if not isinstance(polygon, list) or not 3 <= len(polygon) <= 1024:
                return False
            point_count += len(polygon)
            if point_count > 250000 or not all(isinstance(point, list) and len(point) == 2
                                             and all(number(coordinate) for coordinate in point) for point in polygon):
                return False
        return True

    if (not isinstance(document, dict) or type(document.get("version")) is not int
            or document["version"] != GEOMETRY_VERSION or document.get("size") != list(OUTPUT_SIZE)
            or document.get("source") != NBA2K27_BASE_ENTRY):
        return False
    layers, paints = document.get("layers"), document.get("paints")
    if not isinstance(layers, list) or not 16 <= len(layers) <= 64 or not isinstance(paints, list) or not 6 <= len(paints) <= 32:
        return False
    ids = set()
    for layer in [*layers, *paints]:
        if (not isinstance(layer, dict) or not isinstance(layer.get("id"), str) or not 1 <= len(layer["id"]) <= 256
                or layer["id"] in ids or not isinstance(layer.get("name"), str) or not 1 <= len(layer["name"]) <= 256
                or type(layer.get("visible")) is not bool or not isinstance(layer.get("color"), str)
                or re.fullmatch(r"#[0-9a-fA-F]{6}", layer["color"]) is None
                or not polygons(layer.get("polygons")) or not polygons(layer.get("gameUvPolygons"))):
            return False
        ids.add(layer["id"])
    if not set(NAMES).union({"college-three", "high-school-three"}).issubset({layer["id"] for layer in layers}):
        return False
    if not {f"{prefix}-{side}" for prefix in ("paint", "secondary-paint", "two-point") for side in ("left", "right")}.issubset({layer["id"] for layer in paints}):
        return False
    guides = document.get("guides")
    required_anchors = {"court-center", *(f"court-corner-{i}" for i in range(4)),
                        *(f"{side}-{kind}" for side in ("left", "right") for kind in ("free-throw", "paint-center")),
                        *(f"{side}-key-{i}" for side in ("left", "right") for i in range(4))}
    if not isinstance(guides, dict):
        return False
    guide_ids = []
    for mapping in ("template", "game-uv"):
        group = guides.get(mapping)
        anchors = group.get("anchors") if isinstance(group, dict) else None
        if not isinstance(anchors, list) or not 17 <= len(anchors) <= 64:
            return False
        seen = set()
        for anchor in anchors:
            if (not isinstance(anchor, dict) or not isinstance(anchor.get("id"), str) or not 1 <= len(anchor["id"]) <= 256
                    or anchor["id"] in seen or not isinstance(anchor.get("name"), str) or not 1 <= len(anchor["name"]) <= 256
                    or not number(anchor.get("x")) or not number(anchor.get("y"))):
                return False
            seen.add(anchor["id"])
        if not required_anchors.issubset(seen):
            return False
        corners = [anchor for anchor in anchors if anchor["id"].startswith("court-corner-")]
        if (len(corners) != 4 or len({(anchor["x"], anchor["y"]) for anchor in corners}) != 4
                or min(anchor["x"] for anchor in corners) >= max(anchor["x"] for anchor in corners)
                or min(anchor["y"] for anchor in corners) >= max(anchor["y"] for anchor in corners)
                or any(not (0 <= round(anchor["x"]) <= OUTPUT_SIZE[0]
                            and 0 <= round(anchor["y"]) <= OUTPUT_SIZE[1]) for anchor in corners)):
            return False
        guide_ids.append(seen)
    if guide_ids[0] != guide_ids[1]:
        return False
    uv = document.get("gameUv")
    if not isinstance(uv, dict) or type(uv.get("texcoord")) is not int or uv["texcoord"] != 0:
        return False
    matrix = uv.get("matrix")
    if (not isinstance(matrix, list) or len(matrix) != 2
            or any(not isinstance(row, list) or len(row) != 3 or not all(number(value) for value in row) for row in matrix)
            or abs(matrix[0][0] * matrix[1][1] - matrix[0][1] * matrix[1][0]) < 1e-9):
        return False
    bounds = uv.get("hardwoodBounds")
    if (not isinstance(bounds, list) or len(bounds) != 4 or any(type(value) is not int for value in bounds)
            or bounds[0] < 0 or bounds[1] < 0 or bounds[2] <= 0 or bounds[3] <= 0
            or bounds[0] + bounds[2] > OUTPUT_SIZE[0] or bounds[1] + bounds[3] > OUTPUT_SIZE[1]
            or type(uv.get("surfaceTriangles")) is not int or not 1 <= uv["surfaceTriangles"] <= 100000
            or not polygons(uv.get("courtSurfacePolygons"))):
        return False
    points = [point for polygon in uv["courtSurfacePolygons"] for point in polygon]
    left, top = math.floor(min(point[0] for point in points)), math.floor(min(point[1] for point in points))
    if bounds != [left, top, math.ceil(max(point[0] for point in points)) - left, math.ceil(max(point[1] for point in points)) - top]:
        return False
    coverage = uv.get("projectionCoverage")
    return (isinstance(coverage, dict) and set(coverage) == ids
            and all(number(value) and .999 <= value <= 1.001 for value in coverage.values()))


def load_geometry(project_root: Path) -> dict | None:
    path = geometry_path(project_root)
    def reject_constant(_value):
        raise ValueError("Non-finite cached geometry JSON.")
    try:
        with path.open("rb") as stream:
            data = stream.read(MAX_GEOMETRY_BYTES + 1)
        if len(data) > MAX_GEOMETRY_BYTES:
            return None
        document = json.loads(data, parse_constant=reject_constant)
        return document if _valid_geometry(document) else None
    except (FileNotFoundError, ValueError, RecursionError):
        return None


def geometry_revision(document: dict) -> str:
    payload = json.dumps(document, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def request_geometry(project_root: Path, request: dict) -> dict:
    expected = request.get("geometryRevision")
    if "geometryRevision" in request and (not isinstance(expected, str) or re.fullmatch(r"[0-9a-f]{64}", expected) is None):
        raise ValueError("Invalid court geometry revision.")
    document = load_geometry(project_root)
    if document is None:
        raise ValueError("The stock court geometry is unavailable. Reopen the app to prepare it before exporting.")
    if expected is not None and geometry_revision(document) != expected:
        raise ValueError("The stock court geometry changed while this workspace was open. Reopen the app before exporting; your project is unchanged.")
    return document


def prepare_geometry(project_root: Path) -> dict:
    root = Path(project_root).resolve()
    with _GEOMETRY_LOCK, _base_file_lock(geometry_path(root)):
        cached = load_geometry(root)
        if cached:
            return cached
        document = _build_geometry()
        if not _valid_geometry(document):
            raise ValueError("The generated stock geometry did not pass cache validation.")
        if len(json.dumps(document, separators=(",", ":"), allow_nan=False).encode("utf-8")) > MAX_GEOMETRY_BYTES:
            raise ValueError("The generated stock geometry exceeds the cache size limit.")
        _write_json_atomic(geometry_path(root), document)
        return document


def _build_geometry() -> dict:
    game_root = find_nba2k27_root()
    with extracted_stock_floor(game_root) as archive_path:
        with open_iff(archive_path) as archive:
            scene, _ = _scene_document(read_iff_scene(archive))
            models = [model for value in scene.values() if isinstance(value, dict) for model in value.get("Model", {}).values()]
            model = next(model for model in models if any("full_court_floor" in p.get("Mesh", "") for p in model.get("Prim", [])))
            stream = model["VertexStream"][0]
            vertex_name = str(Path(stream["Binary"]).with_suffix(".bin"))
            vertices = _decode_buffer(archive.read(vertex_name), game_root, int(stream["Size"]))
            indices = archive.read(model["IndexBuffer"]["Binary"])
            line_texture = next(name for name in archive.namelist() if name.startswith("floor_line.") and name.endswith(".dds"))
            alpha = Image.open(BytesIO(archive.read(line_texture))).convert("RGBA").getchannel("A")
            bbox = alpha.getbbox()
            histogram = alpha.histogram()
            if (not bbox or bbox[1] != 0 or bbox[3] != alpha.height or sum(histogram[1:255])
                    or histogram[255] != (bbox[2] - bbox[0]) * alpha.height):
                raise ValueError("The stock marking texture is not a supported solid stripe.")
            layers = decode_model(model, vertices, indices, (bbox[0] / alpha.width, bbox[2] / alpha.width))
            floor_prim = next(p for p in model["Prim"] if "full_court_floor" in p.get("Mesh", ""))
            material = next(value["Material"][floor_prim["Material"]] for value in scene.values()
                            if isinstance(value, dict) and floor_prim["Material"] in value.get("Material", {}))
            effect = next(value["Effect"][material["Effect"]] for value in scene.values()
                          if isinstance(value, dict) and material["Effect"] in value.get("Effect", {}))
            if effect["Resource"]["Logo0Texture"]["Texcoord"] != 0 or material["Script"] != "floor.b4426d5f497374fc.script":
                raise ValueError("This floor needs a different shader UV transform.")
            matrix = logo_transform(material.get("Parameter", {}))
            surface = decode_floor_surface(model, vertices, indices)
    boundary = next(layer for layer in layers if layer["id"] == "line_side_base_lowShape")
    points = [point for polygon in boundary["polygons"] for point in polygon]
    min_x, max_x = min(p[0] for p in points), max(p[0] for p in points)
    min_z, max_z = min(p[1] for p in points), max(p[1] for p in points)
    paints, anchors = derive_court_features(layers)
    paints.extend(derive_two_point_areas(layers, paints))
    regulation_lines, calibration = derive_regulation_lines(layers)
    nba_index = next(i for i, layer in enumerate(layers) if layer["id"] == "NBA_line_three_point_lowShape")
    layers[nba_index + 1:nba_index + 1] = regulation_lines
    coverage = {}
    for layer in [*paints, *layers]:
        layer["gameUvPolygons"], coverage[layer["id"]] = project_polygons(layer["polygons"], surface, matrix)
    court_surface, _ = project_polygons([[[min_x, min_z], [max_x, min_z], [max_x, max_z], [min_x, max_z]]], surface, matrix)
    surface_points = [p for polygon in court_surface for p in polygon]
    native_left = math.floor(min(p[0] for p in surface_points))
    native_top = math.floor(min(p[1] for p in surface_points))
    native_bounds = [native_left, native_top, math.ceil(max(p[0] for p in surface_points)) - native_left,
                     math.ceil(max(p[1] for p in surface_points)) - native_top]
    left, top, width, height = TEMPLATE_BOUNDS
    def template_point(point):
        x, z = point
        return [round(left + (z - min_z) / (max_z - min_z) * width, 4),
                round(top + (max_x - x) / (max_x - min_x) * height, 4)]

    guides = {mapping: {"anchors": []} for mapping in ("template", "game-uv")}
    for anchor in anchors:
        for mapping in guides:
            x, y = (template_point(anchor["point"]) if mapping == "template" else project_anchor(anchor["point"], surface, matrix))
            guides[mapping]["anchors"].append({"id": anchor["id"], "name": anchor["name"], "x": x, "y": y})
    for layer in [*paints, *layers]:
        layer["polygons"] = [[template_point([x, z])
                              for x, z in polygon] for polygon in layer["polygons"]]
    document = {"version": GEOMETRY_VERSION, "source": NBA2K27_BASE_ENTRY, "size": list(OUTPUT_SIZE),
                "alignment": "Template aligned or stock TEXCOORD0 + primary material transform", "layers": layers,
                "paints": paints, "guides": guides, "regulationCalibration": calibration,
                "gameUv": {"texcoord": 0, "matrix": matrix, "surfaceTriangles": len(surface),
                           "courtSurfacePolygons": court_surface, "hardwoodBounds": native_bounds,
                           "projectionCoverage": coverage,
                           "materialParameters": {key: value for key, value in material.get("Parameter", {}).items() if key.startswith("Logo0")},
                           "shaderTransform": "UV sampled after floor.b4426d5f497374fc.script and PS.a6d99aa7f8ccf828.shader",
                           "inGameVerified": False}}
    return document


def render_experimental(project_root: Path, request: dict, output_path: Path, *, preview: bool = False,
                        geometry: dict | None = None, protected_sources=()) -> Path:
    geometry = request_geometry(project_root, request) if geometry is None else geometry
    floor = request.get("floor") or {}
    revision = asset_revision(floor)
    path = Path(str(floor.get("path", "")))
    if not path.is_file():
        raise ValueError("Choose a stock hardwood texture.")
    sources = (path, geometry_path(project_root), *protected_sources,
               *(Path(item["path"]) for item in request.get("logoImages", []) if item.get("path")))
    ensure_new_export(output_path, *sources)
    scale = 0.25 if preview else 1.0
    canvas = Image.new("RGBA", tuple(round(value * scale) for value in OUTPUT_SIZE),
                       request.get("outsideColor", "#19583F") if request.get("outsideVisible", True) else (0, 0, 0, 0))
    mapping = request.get("mappingMode", "game-uv")
    if mapping not in {"game-uv", "template"}:
        raise ValueError("Choose a supported marking alignment.")
    native = mapping == "game-uv"
    bounds = geometry["gameUv"]["hardwoodBounds"] if native else floor.get("bbox", [1078, 448, 6037, 3201])
    left, top, width, height = [int(value) for value in bounds]
    if min(width, height) <= 0 or left < 0 or top < 0 or left + width > OUTPUT_SIZE[0] or top + height > OUTPUT_SIZE[1]:
        raise ValueError("Invalid experimental hardwood bounds.")
    left, top, width, height = [round(value * scale) for value in (left, top, width, height)]
    with verified_asset_stream(path, revision) as stream, Image.open(stream) as source:
        validate_asset_image(source)
        pixels = source.convert("RGBA")
        if floor.get("artworkAlphaMode") == "GameData":
            pixels.putalpha(255)
        hardwood = ImageOps.fit(pixels, (width, height), method=Image.Resampling.LANCZOS)
        pixels.close()
        if native:
            from PIL import ImageChops
            mask = Image.new("L", (width, height))
            mask_draw = ImageDraw.Draw(mask)
            for polygon in geometry["gameUv"]["courtSurfacePolygons"]:
                mask_draw.polygon([(p[0] * scale - left, p[1] * scale - top) for p in polygon], fill=255)
            hardwood.putalpha(ImageChops.multiply(hardwood.getchannel("A"), mask))
        canvas.alpha_composite(hardwood, (left, top))
    settings = request.get("lineSettings", {})
    paint_settings = request.get("paintSettings", {})
    draw = ImageDraw.Draw(canvas)
    paint_ids = {layer["id"] for layer in geometry["paints"]}
    for layer in [*geometry["paints"], *geometry["layers"]]:
        setting = (paint_settings if layer["id"] in paint_ids else settings).get(layer["id"], {})
        if setting.get("visible", layer["visible"]):
            color = setting.get("color", layer["color"])
            for polygon in layer["gameUvPolygons"] if native else layer["polygons"]:
                draw.polygon([(point[0] * scale, point[1] * scale) for point in polygon], fill=color)
    from .court_template import _save_png_atomic, _composite_logo
    for logo in request.get("logoImages", []):
        if logo.get("visible", True):
            _composite_logo(canvas, logo, scale)
    _save_png_atomic(canvas, output_path, fast=preview, sources=sources)
    return output_path
