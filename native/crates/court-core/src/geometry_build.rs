//! Builds the stock court geometry cache from the installed game (port of `_build_geometry` and
//! its helpers in experimental_lines.py): decodes the stock marking meshes and floor surface,
//! derives key paints, two-point areas, regulation lines and anchors, and projects everything
//! into game-UV texture pixels and template coordinates.
use crate::error::{Error, Result, check};
use crate::geom::{self, Point, Polygon};
use crate::geometry::{self, BASE_ENTRY, GEOMETRY_VERSION, Geometry, MAX_GEOMETRY_BYTES, NAMES, OUTPUT_SIZE};
use crate::pyjson::{self, Style};
use crate::scene::{self, SCENE_ENTRY, Scene};
use crate::ziparc::Archive;
use crate::{bail, files};
use serde_json::{Map, Value, json};
use std::path::{Path, PathBuf};

pub const TEMPLATE_BOUNDS: [f64; 4] = [1078.0, 448.0, 6037.0, 3201.0];
const FLOOR_SCRIPT: &str = "floor.b4426d5f497374fc.script";

type P3 = [f64; 3];

/// CPython 3.12 `sum()` over floats (Neumaier compensated summation).
fn py_sum(values: impl IntoIterator<Item = f64>) -> f64 {
    let (mut total, mut compensation) = (0.0f64, 0.0f64);
    for x in values {
        let t = total + x;
        compensation += if total.abs() >= x.abs() { (total - t) + x } else { (x - t) + total };
        total = t;
    }
    if compensation != 0.0 && compensation.is_finite() { total + compensation } else { total }
}

/// Python `round(x, 4)`: correctly rounded, ties to even on the exact binary value.
fn round4(x: f64) -> f64 {
    if !x.is_finite() {
        return x;
    }
    let y = x * 10000.0;
    if ((y.abs().fract()) - 0.5).abs() > 1e-6 {
        return y.round() / 10000.0;
    }
    let text = format!("{:.1100}", x.abs());
    let (integer, fraction) = text.split_once('.').unwrap();
    let (kept, rest) = fraction.split_at(4);
    let mut n: u64 = format!("{integer}{kept}").parse().unwrap();
    let first = rest.as_bytes()[0];
    let tail_zero = rest[1..].bytes().all(|b| b == b'0');
    if first > b'5' || (first == b'5' && (!tail_zero || n % 2 == 1)) {
        n += 1;
    }
    let value = n as f64 / 10000.0;
    if x.is_sign_negative() { -value } else { value }
}

fn polygon_area(polygon: &[Point]) -> f64 {
    if polygon.is_empty() {
        return 0.0;
    }
    py_sum((0..polygon.len()).map(|i| {
        let (a, b) = (polygon[i], polygon[(i + 1) % polygon.len()]);
        a[0] * b[1] - b[0] * a[1]
    }))
    .abs()
        / 2.0
}

fn mean_z(polygon: &[Point]) -> f64 {
    py_sum(polygon.iter().map(|p| p[1])) / polygon.len() as f64
}

fn isclose(a: f64, b: f64, rel_tol: f64) -> bool {
    a == b || (a - b).abs() <= rel_tol * a.abs().max(b.abs())
}

fn number(value: &Value, what: &str) -> Result<f64> {
    value.as_f64().ok_or_else(|| Error::msg(format!("The stock floor scene has an invalid {what}.")))
}

fn integer(value: &Value, what: &str) -> Result<i64> {
    match value.as_i64() {
        Some(v) => Ok(v),
        None => value.as_f64().filter(|v| v.fract() == 0.0).map(|v| v as i64).ok_or_else(|| Error::msg(format!("The stock floor scene has an invalid {what}."))),
    }
}

fn text<'a>(value: &'a Value, key: &str) -> &'a str {
    value.get(key).and_then(Value::as_str).unwrap_or("")
}

/// Port of `_decode_buffer`: Oodle-wrapped buffers are decoded with the game's library.
fn decode_buffer(data: Vec<u8>, game_root: &Path, expected_size: usize) -> Result<Vec<u8>> {
    let mut data = data;
    if data.starts_with(&[0x1f, 0x8b, 0x21]) {
        if data.len() < 24 || u32::from_le_bytes(data[data.len() - 4..].try_into().unwrap()) as usize != expected_size {
            bail!("Unexpected stock vertex-buffer size.");
        }
        data = crate::oodle::decompress(game_root, &data[16..data.len() - 8], expected_size)?;
    }
    if data.len() != expected_size {
        bail!("The stock vertex buffer has an invalid length.");
    }
    Ok(data)
}

/// Port of `_clip_u`: clips a polygon (x, z, u) against a texture-u bound.
fn clip_u(points: &[P3], bound: f64, keep_above: bool) -> Vec<P3> {
    let mut result = Vec::new();
    let Some(&last) = points.last() else { return result };
    let inside = |p: &P3| if keep_above { p[2] >= bound } else { p[2] <= bound };
    let (mut previous, mut previous_inside) = (last, inside(&last));
    for &point in points {
        let point_inside = inside(&point);
        if point_inside != previous_inside {
            let ratio = (bound - previous[2]) / (point[2] - previous[2]);
            result.push(std::array::from_fn(|i| previous[i] + ratio * (point[i] - previous[i])));
        }
        if point_inside {
            result.push(point);
        }
        previous = point;
        previous_inside = point_inside;
    }
    result
}

fn read_indices(indices: &[u8], cursor: usize, count: usize, wide: bool) -> Vec<usize> {
    (0..count)
        .map(|i| {
            if wide {
                u32::from_le_bytes(indices[cursor + i * 4..cursor + i * 4 + 4].try_into().unwrap()) as usize
            } else {
                u16::from_le_bytes([indices[cursor + i * 2], indices[cursor + i * 2 + 1]]) as usize
            }
        })
        .collect()
}

fn f32_at(data: &[u8], offset: usize) -> Option<f64> {
    data.get(offset..offset + 4).map(|b| f32::from_le_bytes(b.try_into().unwrap()) as f64)
}

fn i16_at(data: &[u8], offset: usize) -> Option<f64> {
    data.get(offset..offset + 2).map(|b| i16::from_le_bytes([b[0], b[1]]) as f64)
}

fn index_format(model: &Value) -> Result<bool> {
    match text(&model["IndexBuffer"], "Format") {
        "R16_UINT" => Ok(false),
        "R32_UINT" => Ok(true),
        _ => bail!("Unsupported stock index format."),
    }
}

fn prims(model: &Value) -> &[Value] {
    model.get("Prim").and_then(Value::as_array).map(Vec::as_slice).unwrap_or(&[])
}

/// A derived or decoded layer; `extra` keys follow `polygons` in the cached document.
#[derive(Clone, Debug)]
pub struct Layer {
    pub id: String,
    pub name: String,
    pub visible: bool,
    pub color: &'static str,
    pub polygons: Vec<Polygon>,
    pub extra: Map<String, Value>,
}

impl Layer {
    fn new(id: impl Into<String>, name: impl Into<String>, visible: bool, color: &'static str, polygons: Vec<Polygon>) -> Self {
        Self { id: id.into(), name: name.into(), visible, color, polygons, extra: Map::new() }
    }
}

/// Port of `decode_model`: visible marking stripes per line mesh (world x/z polygons).
pub fn decode_model(model: &Value, vertices: &[u8], indices: &[u8], alpha_span: (f64, f64)) -> Result<Vec<Layer>> {
    let stride = integer(&model["VertexStream"][0]["Stride"], "vertex stride")?.max(0) as usize;
    let format = &model["VertexFormat"];
    if text(&format["POSITION0"], "Format") != "R32G32B32_FLOAT" || text(&format["TEXCOORD0"], "Format") != "R16G16_SNORM" {
        bail!("This stock court uses an unsupported vertex layout.");
    }
    let uv = &format["TEXCOORD0"];
    let uv_offset = integer(&uv["ByteOffset"], "texture offset")?.max(0) as usize;
    let uv_scale = number(&uv["Scale"][0], "texture scale")?;
    let uv_bias = number(&uv["Offset"][0], "texture offset")?;
    let wide = index_format(model)?;
    let size = if wide { 4 } else { 2 };
    let mut cursor = 0usize;
    let mut layers = Vec::new();
    for prim in prims(model) {
        let count = integer(&prim["Count"], "primitive count")?;
        if count < 0 || cursor + count as usize * size > indices.len() {
            bail!("A stock primitive references invalid indices.");
        }
        let count = count as usize;
        let ids = read_indices(indices, cursor, count, wide);
        cursor += count * size;
        let mesh = text(prim, "Mesh");
        if !mesh.to_lowercase().contains("line") {
            continue;
        }
        if prim.get("Type").and_then(Value::as_str).unwrap_or("TRIANGLE_LIST") != "TRIANGLE_LIST" || count % 3 != 0 {
            bail!("Unsupported stock marking topology.");
        }
        let mut points: Vec<P3> = Vec::with_capacity(count);
        for index in ids {
            let offset = index * stride;
            if offset + stride > vertices.len() {
                bail!("A marking references an invalid vertex.");
            }
            let (Some(x), Some(z), Some(packed)) = (f32_at(vertices, offset), f32_at(vertices, offset + 8), i16_at(vertices, offset + uv_offset)) else {
                bail!("A marking references an invalid vertex.");
            };
            let u = (packed / 32767.0).max(-1.0) * uv_scale + uv_bias;
            if !(x.is_finite() && z.is_finite() && u.is_finite()) {
                bail!("Non-finite stock geometry.");
            }
            points.push([x, z, u]);
        }
        let mut polygons = Vec::new();
        for triangle in points.chunks(3) {
            let first = triangle.iter().map(|p| p[2]).fold(f64::INFINITY, f64::min).floor() as i64;
            let last = triangle.iter().map(|p| p[2]).fold(f64::NEG_INFINITY, f64::max).floor() as i64;
            if last - first > 8 {
                bail!("Unexpected texture wrapping in marking geometry.");
            }
            for tile in first..=last {
                let mut clipped = clip_u(triangle, tile as f64 + alpha_span.0, true);
                if !clipped.is_empty() {
                    clipped = clip_u(&clipped, tile as f64 + alpha_span.1, false);
                }
                if clipped.len() >= 3 {
                    polygons.push(clipped.iter().map(|p| [p[0], p[1]]).collect());
                }
            }
        }
        let name = NAMES.iter().find(|(id, _)| *id == mesh).map(|(_, name)| *name).unwrap_or(mesh);
        let visible = !mesh.contains("inner_lowShape") || !mesh.contains("center_circle");
        let mut layer = Layer::new(mesh, name, visible, "#FFFFFF", polygons);
        layer.extra.insert("sourceTriangles".into(), json!(count / 3));
        layers.push(layer);
    }
    if cursor != indices.len() {
        bail!("Not all stock indices were accounted for.");
    }
    Ok(layers)
}

fn parameter(parameters: &Value, key: &str, default: f64) -> f64 {
    parameters.get(key).and_then(Value::as_f64).unwrap_or(default)
}

/// Port of `logo_transform`: the primary material's Logo0 UV transform (2 x 3 rows).
pub fn logo_transform(parameters: &Value) -> [[f64; 3]; 2] {
    let angle = parameter(parameters, "Logo0Rotation", 0.0).to_radians();
    let scale_x = parameter(parameters, "Logo0ScaleX", 1.0);
    let scale_y = parameter(parameters, "Logo0ScaleY", 1.0);
    let center_x = -0.5 + parameter(parameters, "Logo0OffsetX", 0.0);
    let center_y = -0.5 + parameter(parameters, "Logo0OffsetY", 0.0);
    let (a, b) = (angle.cos() * scale_x, -angle.sin() * scale_x);
    let (c, d) = (angle.sin() * scale_y, angle.cos() * scale_y);
    [[a, b, 0.5 + a * center_x + b * center_y], [c, d, 0.5 + c * center_x + d * center_y]]
}

/// Port of `transform_uv`.
pub fn transform_uv(uv: [f64; 2], matrix: &[[f64; 3]; 2]) -> [f64; 2] {
    [0, 1].map(|r| matrix[r][0] * uv[0] + matrix[r][1] * uv[1] + matrix[r][2])
}

/// One floor-surface triangle with its TEXCOORD0 values.
#[derive(Clone, Debug)]
pub struct SurfaceTriangle {
    pub points: [Point; 3],
    pub uvs: [[f64; 2]; 3],
    pub bounds: [f64; 4],
}

impl SurfaceTriangle {
    fn sample(&self, point: Point, matrix: &[[f64; 3]; 2]) -> ([f64; 3], [f64; 2]) {
        let [a, b, c] = self.points;
        let denominator = geom::cross(a, b, c);
        let weights = [geom::cross(b, c, point) / denominator, geom::cross(c, a, point) / denominator, geom::cross(a, b, point) / denominator];
        let uv = [0, 1].map(|i| py_sum((0..3).map(|j| weights[j] * self.uvs[j][i])));
        (weights, transform_uv(uv, matrix))
    }
}

/// Port of `decode_floor_surface`: non-degenerate `full_court_floor` triangles.
pub fn decode_floor_surface(model: &Value, vertices: &[u8], indices: &[u8]) -> Result<Vec<SurfaceTriangle>> {
    let descriptor = &model["VertexFormat"]["TEXCOORD0"];
    let stride = integer(&model["VertexStream"][0]["Stride"], "vertex stride")?.max(0) as usize;
    if text(&model["VertexFormat"]["POSITION0"], "Format") != "R32G32B32_FLOAT"
        || text(descriptor, "Format") != "R16G16_SNORM"
        || !matches!(text(&model["IndexBuffer"], "Format"), "R16_UINT" | "R32_UINT")
    {
        bail!("Unsupported stock floor vertex layout.");
    }
    let wide = index_format(model)?;
    let size = if wide { 4 } else { 2 };
    let byte_offset = integer(&descriptor["ByteOffset"], "texture offset")?.max(0) as usize;
    let scale = [number(&descriptor["Scale"][0], "texture scale")?, number(&descriptor["Scale"][1], "texture scale")?];
    let bias = [number(&descriptor["Offset"][0], "texture offset")?, number(&descriptor["Offset"][1], "texture offset")?];
    let mut cursor = 0usize;
    for prim in prims(model) {
        let count = integer(&prim["Count"], "primitive count")?;
        if count < 0 || cursor + count as usize * size > indices.len() {
            bail!("A stock floor references invalid indices.");
        }
        let count = count as usize;
        let ids = read_indices(indices, cursor, count, wide);
        cursor += count * size;
        if !text(prim, "Mesh").contains("full_court_floor") {
            continue;
        }
        if count % 3 != 0 || prim.get("Type").and_then(Value::as_str).unwrap_or("TRIANGLE_LIST") != "TRIANGLE_LIST" {
            bail!("Unsupported stock floor topology.");
        }
        let mut points = Vec::with_capacity(count);
        let mut uvs = Vec::with_capacity(count);
        for index in ids {
            let offset = index * stride;
            if offset + stride > vertices.len() {
                bail!("A stock floor references an invalid vertex.");
            }
            let (Some(x), Some(z), Some(pu), Some(pv)) = (f32_at(vertices, offset), f32_at(vertices, offset + 8), i16_at(vertices, offset + byte_offset), i16_at(vertices, offset + byte_offset + 2)) else {
                bail!("A stock floor references an invalid vertex.");
            };
            let uv = [(pu / 32767.0).max(-1.0) * scale[0] + bias[0], (pv / 32767.0).max(-1.0) * scale[1] + bias[1]];
            if !(x.is_finite() && z.is_finite() && uv[0].is_finite() && uv[1].is_finite()) {
                bail!("Non-finite stock floor geometry.");
            }
            points.push([x, z]);
            uvs.push(uv);
        }
        let mut triangles = Vec::new();
        for start in (0..count).step_by(3) {
            let triangle = [points[start], points[start + 1], points[start + 2]];
            if geom::cross(triangle[0], triangle[1], triangle[2]).abs() < 1e-8 {
                continue;
            }
            let bounds = [
                triangle.iter().map(|p| p[0]).fold(f64::INFINITY, f64::min),
                triangle.iter().map(|p| p[1]).fold(f64::INFINITY, f64::min),
                triangle.iter().map(|p| p[0]).fold(f64::NEG_INFINITY, f64::max),
                triangle.iter().map(|p| p[1]).fold(f64::NEG_INFINITY, f64::max),
            ];
            triangles.push(SurfaceTriangle { points: triangle, uvs: [uvs[start], uvs[start + 1], uvs[start + 2]], bounds });
        }
        return Ok(triangles);
    }
    bail!("The stock floor surface is missing.")
}

fn texture_pixel(sample: [f64; 2]) -> Point {
    [round4(sample[0] * OUTPUT_SIZE[0] as f64), round4(sample[1] * OUTPUT_SIZE[1] as f64)]
}

/// Port of `project_polygons`: world polygons to game-UV texture pixels, with area coverage.
pub fn project_polygons(polygons: &[Polygon], surface: &[SurfaceTriangle], matrix: &[[f64; 3]; 2]) -> Result<(Vec<Polygon>, f64)> {
    let (mut mapped, mut source_area, mut mapped_area) = (Vec::new(), 0.0, 0.0);
    for polygon in polygons {
        source_area += polygon_area(polygon);
        let Some(bounds) = geom::bounds(std::slice::from_ref(polygon)) else { continue };
        for triangle in surface {
            let b = triangle.bounds;
            if bounds[0] > b[2] || bounds[2] < b[0] || bounds[1] > b[3] || bounds[3] < b[1] {
                continue;
            }
            let clipped = geom::clip_convex(polygon, &triangle.points);
            let area = polygon_area(&clipped);
            if area < 1e-8 {
                continue;
            }
            mapped_area += area;
            mapped.push(clipped.iter().map(|&point| texture_pixel(triangle.sample(point, matrix).1)).collect());
        }
    }
    let coverage = if source_area != 0.0 { mapped_area / source_area } else { 1.0 };
    if !(0.999..=1.001).contains(&coverage) {
        bail!("Stock surface projection coverage is invalid: {coverage:.6}");
    }
    Ok((mapped, coverage))
}

/// Port of `project_anchor`: the first surface triangle containing the point.
pub fn project_anchor(point: Point, surface: &[SurfaceTriangle], matrix: &[[f64; 3]; 2]) -> Result<Point> {
    for triangle in surface {
        let (weights, sample) = triangle.sample(point, matrix);
        if weights.iter().copied().fold(f64::INFINITY, f64::min) >= -1e-8 {
            return Ok(texture_pixel(sample));
        }
    }
    bail!("A stock court anchor falls outside the floor surface.")
}

fn bounds_of<'a>(polygons: impl IntoIterator<Item = &'a Polygon>) -> Result<[f64; 4]> {
    let mut points = polygons.into_iter().flatten();
    let first = points.next().ok_or_else(|| Error::msg("A stock outline has no geometry."))?;
    let mut b = [first[0], first[1], first[0], first[1]];
    for p in points {
        b = [b[0].min(p[0]), b[1].min(p[1]), b[2].max(p[0]), b[3].max(p[1])];
    }
    Ok(b)
}

fn rectangle(bounds: [f64; 4]) -> Result<Polygon> {
    let [x0, z0, x1, z1] = bounds;
    if x1 <= x0 || z1 <= z0 {
        bail!("Invalid stock-derived paint area.");
    }
    Ok(vec![[x0, z0], [x1, z0], [x1, z1], [x0, z1]])
}

fn layer<'a>(layers: &'a [Layer], id: &str) -> Result<&'a [Polygon]> {
    layers.iter().find(|l| l.id == id).map(|l| l.polygons.as_slice()).ok_or_else(|| Error::msg(format!("The stock court is missing {id}.")))
}

fn side_polygons<'a>(polygons: &'a [Polygon], split: f64, below: bool) -> impl Iterator<Item = &'a Polygon> {
    polygons.iter().filter(move |p| (mean_z(p) < split) == below)
}

#[derive(Clone, Debug)]
pub struct Anchor {
    pub id: String,
    pub name: String,
    pub point: Point,
}

fn anchor(id: impl Into<String>, name: impl Into<String>, point: Point) -> Anchor {
    Anchor { id: id.into(), name: name.into(), point }
}

/// Port of `derive_court_features`: filled key regions and placement anchors.
pub fn derive_court_features(layers: &[Layer]) -> Result<(Vec<Layer>, Vec<Anchor>)> {
    let circle = bounds_of(layer(layers, "line_center_circle_outer_lowShape")?)?;
    let center = [(circle[0] + circle[2]) / 2.0, (circle[1] + circle[3]) / 2.0];
    let midcourt = bounds_of(layer(layers, "line_midcourt_center_lowShape")?)?;
    let half_stroke = (midcourt[3] - midcourt[1]) / 2.0;
    if !(0.0 < half_stroke && half_stroke < 10.0) {
        bail!("Unexpected stock marking width.");
    }
    let boundary = bounds_of(layer(layers, "line_side_base_lowShape")?)?;
    let court = [boundary[0] + half_stroke, boundary[1] + half_stroke, boundary[2] - half_stroke, boundary[3] - half_stroke];
    let mut paints = Vec::new();
    let mut anchors = vec![anchor("court-center", "Court Center", center)];
    for (side, below) in [("Left", true), ("Right", false)] {
        let lower = side.to_lowercase();
        let side_bounds = |id: &str| -> Result<[f64; 4]> {
            let b = bounds_of(side_polygons(layer(layers, id)?, center[1], below))?;
            Ok([b[0] + half_stroke, b[1] + half_stroke, b[2] - half_stroke, b[3] - half_stroke])
        };
        let mut inner = side_bounds("line_lane_inner_lowShape")?;
        let outer = side_bounds("line_lane_free_throw_lowShape")?;
        if !(outer[0] < inner[0] && inner[0] < inner[2] && inner[2] < outer[2]) {
            bail!("Stock inner and outer key outlines do not nest.");
        }
        inner[1] = outer[1];
        inner[3] = outer[3];
        paints.push(Layer::new(format!("paint-{lower}"), format!("{side} Paint"), true, "#19583F", vec![rectangle(inner)?]));
        paints.push(Layer::new(
            format!("secondary-paint-{lower}"),
            format!("{side} Secondary Paint"),
            true,
            "#19583F",
            vec![rectangle([outer[0], outer[1], inner[0], outer[3]])?, rectangle([inner[2], outer[1], outer[2], outer[3]])?],
        ));
        let free = bounds_of(side_polygons(layer(layers, "line_free_throw_circle_lowShape")?, center[1], below))?;
        anchors.push(anchor(format!("{lower}-free-throw"), format!("{side} Free Throw Center"), [(free[0] + free[2]) / 2.0, (free[1] + free[3]) / 2.0]));
        anchors.push(anchor(format!("{lower}-paint-center"), format!("{side} Paint Center"), [(outer[0] + outer[2]) / 2.0, (outer[1] + outer[3]) / 2.0]));
        for (index, point) in rectangle(outer)?.into_iter().enumerate() {
            anchors.push(anchor(format!("{lower}-key-{index}"), format!("{side} Key Corner {}", index + 1), point));
        }
    }
    for (index, point) in rectangle(court)?.into_iter().enumerate() {
        anchors.push(anchor(format!("court-corner-{index}"), format!("Court Corner {}", index + 1), point));
    }
    Ok((paints, anchors))
}

/// Port of `_triangulated_polygons` (earcut instead of constrained Delaunay; coverage checked).
fn triangulated_polygons(region: &[geom::Shape]) -> Result<Vec<Polygon>> {
    if region.is_empty() || geom::shapes_area(region) <= 0.0 {
        bail!("Invalid derived court region.");
    }
    geom::triangulate(region).map_err(Error::msg)
}

/// Port of `derive_two_point_areas`: NBA three-point enclosure minus the outer key, per side.
pub fn derive_two_point_areas(layers: &[Layer], paints: &[Layer]) -> Result<Vec<Layer>> {
    let circle = bounds_of(layer(layers, "line_center_circle_outer_lowShape")?)?;
    let center_z = py_sum([circle[1], circle[3]]) / 2.0;
    let court = rectangle(bounds_of(layer(layers, "line_side_base_lowShape")?)?)?;
    let three = layer(layers, "NBA_line_three_point_lowShape")?;
    let mut result = Vec::new();
    for (side, below) in [("Left", true), ("Right", false)] {
        let lower = side.to_lowercase();
        let points: Vec<Point> = side_polygons(three, center_z, below).flatten().copied().collect();
        let key = rectangle(bounds_of(paints.iter().filter(|p| p.id.ends_with(&lower)).flat_map(|p| &p.polygons))?)?;
        let hull = geom::convex_hull_geos(&points);
        let clipped = geom::intersection(&[hull], &[court.clone()]);
        let region = geom::difference(&geom::shape_contours(&clipped), &[key]);
        let mut paint = Layer::new(format!("two-point-{lower}"), format!("{side} Two-Point Area"), false, "#19583F", triangulated_polygons(&region)?);
        paint.extra.insert("source".into(), json!("Stock NBA three-point outline minus stock outer key"));
        result.push(paint);
    }
    Ok(result)
}

/// Inferred regulation scale (`regulation_calibration`).
#[derive(Clone, Debug)]
pub struct Calibration {
    pub unit: f64,
    pub court: [f64; 4],
    pub baskets: [Point; 2],
}

impl Calibration {
    pub fn to_value(&self) -> Value {
        json!({
            "unitsPerFoot": self.unit,
            "courtInsideBounds": self.court,
            "basketCenters": self.baskets,
            "basketSetbackFeet": 5.25,
            "source": "Stock boundary inside edges; 94 x 50 feet; 63-inch basket setback; stock charge-arc validation",
        })
    }
}

/// Port of `regulation_calibration`: 94 x 50 ft court inside edges and basket centers.
pub fn regulation_calibration(layers: &[Layer]) -> Result<Calibration> {
    let midcourt = bounds_of(layer(layers, "line_midcourt_center_lowShape")?)?;
    let stroke = midcourt[3] - midcourt[1];
    if !(0.0 < stroke && stroke < 20.0) {
        bail!("Unexpected stock marking width.");
    }
    let boundary = bounds_of(layer(layers, "line_side_base_lowShape")?)?;
    let court = [boundary[0] + stroke, boundary[1] + stroke, boundary[2] - stroke, boundary[3] - stroke];
    let width_scale = (court[2] - court[0]) / 50.0;
    let length_scale = (court[3] - court[1]) / 94.0;
    if !isclose(width_scale, length_scale, 0.002) {
        bail!("Stock court does not match a 94 by 50 foot regulation surface.");
    }
    let unit = (width_scale + length_scale) / 2.0;
    if !isclose(stroke / unit, 2.0 / 12.0, 0.02) {
        bail!("Stock marking width does not match two inches.");
    }
    let center_x = (court[0] + court[2]) / 2.0;
    let baskets = [[center_x, court[1] + 5.25 * unit], [center_x, court[3] - 5.25 * unit]];
    for (basket, below) in baskets.iter().zip([true, false]) {
        let charge = bounds_of(side_polygons(layer(layers, "line_charge_circle_lowShape")?, (court[1] + court[3]) / 2.0, below))?;
        let apex = if below { charge[3] } else { charge[1] };
        let distance = (apex - basket[1]).abs() / unit;
        if ((charge[0] + charge[2]) / 2.0 - basket[0]).abs() > 0.05 * unit || !(3.95 < distance && distance < 4.25) {
            bail!("Inferred basket centers do not align with stock restricted-area arcs.");
        }
    }
    Ok(Calibration { unit, court, baskets })
}

/// Port of `_three_point_enclosure`: corner segments plus a 161-point arc.
fn three_point_enclosure(basket: Point, baseline: f64, direction: f64, radius: f64, corner: f64) -> Polygon {
    let angle = (corner / radius).asin();
    let mut polygon = vec![[basket[0] - corner, baseline]];
    polygon.extend((0..161).map(|i| {
        let t = -angle + 2.0 * angle * i as f64 / 160.0;
        [basket[0] + radius * t.sin(), basket[1] + direction * radius * t.cos()]
    }));
    polygon.push([basket[0] + corner, baseline]);
    polygon
}

/// Port of `derive_regulation_lines`: college and high-school three-point lines.
pub fn derive_regulation_lines(layers: &[Layer]) -> Result<(Vec<Layer>, Calibration)> {
    let calibration = regulation_calibration(layers)?;
    let (unit, court) = (calibration.unit, calibration.court);
    let court_box = rectangle(court)?;
    let mut generated = Vec::new();
    for (id, name, radius_feet, corner_feet, source) in [
        ("college-three", "College Three", 22.0 + 1.75 / 12.0, 21.0 + 7.875 / 12.0, "https://ncaaorg.s3.amazonaws.com/championships/sports/basketball/rules/common/PRXBB_CourtDiagram.pdf"),
        ("high-school-three", "High School Three", 19.75, 19.75, "https://assets.nfhs.org/umbraco/media/7213108/basketball-court-diagram.pdf"),
    ] {
        let mut polygons = Vec::new();
        for ((basket, baseline), direction) in calibration.baskets.iter().zip([court[1], court[3]]).zip([1.0, -1.0]) {
            let outer = three_point_enclosure(*basket, baseline, direction, radius_feet * unit, corner_feet * unit);
            let inner = three_point_enclosure(*basket, baseline, direction, (radius_feet - 2.0 / 12.0) * unit, (corner_feet - 2.0 / 12.0) * unit);
            let ring = geom::difference(&[outer], &[inner]);
            let region = geom::intersection(&geom::shape_contours(&ring), &[court_box.clone()]);
            polygons.extend(triangulated_polygons(&region)?);
        }
        let mut line = Layer::new(id, name, false, "#FFFFFF", polygons);
        line.extra.insert("source".into(), json!(source));
        line.extra.insert("generated".into(), json!(true));
        line.extra.insert("dimensions".into(), json!({"outsideRadiusFeet": radius_feet, "outsideCornerFeet": corner_feet, "lineWidthInches": 2}));
        generated.push(line);
    }
    Ok((generated, calibration))
}

fn polygons_value(polygons: &[Polygon]) -> Value {
    Value::Array(polygons.iter().map(|polygon| Value::Array(polygon.iter().map(|p| json!([p[0], p[1]])).collect())).collect())
}

/// Stock inputs read from the extracted floor archive.
struct Stock {
    layers: Vec<Layer>,
    surface: Vec<SurfaceTriangle>,
    matrix: [[f64; 3]; 2],
    parameters: Map<String, Value>,
}

fn find_in_scenes<'a>(scene: &'a Scene, section: &str, name: &str) -> Option<&'a Value> {
    scene.scenes().find_map(|s| s.get(section).and_then(Value::as_object).and_then(|m| m.get(name)))
}

fn read_stock(archive_path: &Path, game_root: &Path, cancelled: &dyn Fn() -> bool) -> Result<Stock> {
    let archive = Archive::open(archive_path, crate::iff::MAX_BASE_ARCHIVE_BYTES)?;
    let scene = Scene::parse(&archive.read(SCENE_ENTRY, scene::MAX_SCENE_BYTES as u64)?)?;
    let has_floor = |model: &&Value| prims(model).iter().any(|p| text(p, "Mesh").contains("full_court_floor"));
    let model = scene.models().find(has_floor).ok_or_else(|| Error::msg("The stock floor surface is missing."))?;
    let stream = &model["VertexStream"][0];
    let binary = stream.get("Binary").and_then(Value::as_str).ok_or_else(|| Error::msg("The stock floor has no vertex buffer."))?;
    let vertex_name = Path::new(binary).with_extension("bin").to_string_lossy().into_owned();
    let size = integer(&stream["Size"], "vertex buffer size")?;
    if size <= 0 || size as u64 > crate::floor_geometry::MAX_BUFFER_BYTES {
        bail!("Unexpected stock vertex-buffer size.");
    }
    let vertices = decode_buffer(archive.read(&vertex_name, crate::floor_geometry::MAX_BUFFER_BYTES)?, game_root, size as usize)?;
    let index_name = model["IndexBuffer"].get("Binary").and_then(Value::as_str).ok_or_else(|| Error::msg("The stock floor has no index buffer."))?;
    let indices = archive.read(index_name, crate::floor_geometry::MAX_BUFFER_BYTES)?;
    check(cancelled)?;
    let texture = archive.names().find(|n| n.starts_with("floor_line.") && n.ends_with(".dds")).ok_or_else(|| Error::msg("The stock marking texture is missing."))?.to_string();
    let image = crate::raster::decode_dds(&archive.read(&texture, crate::iff::MAX_DDS_BYTES)?)?;
    let (width, height) = (image.width as usize, image.height as usize);
    let (mut left, mut right, mut top, mut bottom) = (usize::MAX, 0usize, usize::MAX, 0usize);
    let (mut partial, mut opaque) = (0usize, 0usize);
    for (index, pixel) in image.data.chunks_exact(4).enumerate() {
        let alpha = pixel[3];
        if alpha == 0 {
            continue;
        }
        let (x, y) = (index % width, index / width);
        (left, right, top, bottom) = (left.min(x), right.max(x + 1), top.min(y), bottom.max(y + 1));
        if alpha == 255 { opaque += 1 } else { partial += 1 }
    }
    if left == usize::MAX || top != 0 || bottom != height || partial != 0 || opaque != (right - left) * height {
        bail!("The stock marking texture is not a supported solid stripe.");
    }
    let layers = decode_model(model, &vertices, &indices, (left as f64 / width as f64, right as f64 / width as f64))?;
    let floor_prim = prims(model).iter().find(|p| text(p, "Mesh").contains("full_court_floor")).unwrap();
    let material_name = text(floor_prim, "Material");
    let material = find_in_scenes(&scene, "Material", material_name).ok_or_else(|| Error::msg("The stock floor material is missing."))?;
    let effect = find_in_scenes(&scene, "Effect", text(material, "Effect")).ok_or_else(|| Error::msg("The stock floor effect is missing."))?;
    if effect["Resource"]["Logo0Texture"]["Texcoord"].as_f64() != Some(0.0) || text(material, "Script") != FLOOR_SCRIPT {
        bail!("This floor needs a different shader UV transform.");
    }
    let parameters = material.get("Parameter").cloned().unwrap_or_else(|| json!({}));
    let matrix = logo_transform(&parameters);
    let surface = decode_floor_surface(model, &vertices, &indices)?;
    let parameters = parameters.as_object().map(|m| m.iter().filter(|(k, _)| k.starts_with("Logo0")).map(|(k, v)| (k.clone(), v.clone())).collect()).unwrap_or_default();
    Ok(Stock { layers, surface, matrix, parameters })
}

/// Builds the version-4 geometry document (same JSON schema/key order as Python) from the installed game.
pub fn build(game_root: &Path, cancelled: &dyn Fn() -> bool) -> Result<Value> {
    check(cancelled)?;
    let extracted = crate::game::extract_stock_floor(game_root, BASE_ENTRY, cancelled)?;
    let stock = read_stock(extracted.path(), game_root, cancelled)?;
    drop(extracted);
    check(cancelled)?;
    build_document(stock, cancelled)
}

fn build_document(stock: Stock, cancelled: &dyn Fn() -> bool) -> Result<Value> {
    let Stock { mut layers, surface, matrix, parameters } = stock;
    let [min_x, min_z, max_x, max_z] = bounds_of(layer(&layers, "line_side_base_lowShape")?)?;
    let (mut paints, anchors) = derive_court_features(&layers)?;
    let two_point = derive_two_point_areas(&layers, &paints)?;
    paints.extend(two_point);
    let (regulation_lines, calibration) = derive_regulation_lines(&layers)?;
    let nba = layers.iter().position(|l| l.id == "NBA_line_three_point_lowShape").unwrap();
    layers.splice(nba + 1..nba + 1, regulation_lines);
    check(cancelled)?;
    let mut coverage = Map::new();
    let mut game_uv = Vec::new();
    for item in paints.iter().chain(&layers) {
        let (mapped, ratio) = project_polygons(&item.polygons, &surface, &matrix)?;
        coverage.insert(item.id.clone(), json!(ratio));
        game_uv.push(mapped);
        check(cancelled)?;
    }
    let (court_surface, _) = project_polygons(&[vec![[min_x, min_z], [max_x, min_z], [max_x, max_z], [min_x, max_z]]], &surface, &matrix)?;
    let [left, top, right, bottom] = bounds_of(&court_surface)?;
    let (native_left, native_top) = (left.floor() as i64, top.floor() as i64);
    let native_bounds = [native_left, native_top, right.ceil() as i64 - native_left, bottom.ceil() as i64 - native_top];
    let [t_left, t_top, t_width, t_height] = TEMPLATE_BOUNDS;
    let template_point = |p: Point| -> Point { [round4(t_left + (p[1] - min_z) / (max_z - min_z) * t_width), round4(t_top + (max_x - p[0]) / (max_x - min_x) * t_height)] };
    let mut template = Vec::new();
    let mut native = Vec::new();
    for item in &anchors {
        let [x, y] = template_point(item.point);
        template.push(json!({"id": item.id, "name": item.name, "x": x, "y": y}));
        let [x, y] = project_anchor(item.point, &surface, &matrix)?;
        native.push(json!({"id": item.id, "name": item.name, "x": x, "y": y}));
    }
    let serialize = |item: &Layer, mapped: &[Polygon]| -> Value {
        let mut map = Map::new();
        map.insert("id".into(), json!(item.id));
        map.insert("name".into(), json!(item.name));
        map.insert("visible".into(), json!(item.visible));
        map.insert("color".into(), json!(item.color));
        let polygons: Vec<Polygon> = item.polygons.iter().map(|polygon| polygon.iter().map(|&p| template_point(p)).collect()).collect();
        map.insert("polygons".into(), polygons_value(&polygons));
        map.extend(item.extra.clone());
        map.insert("gameUvPolygons".into(), polygons_value(mapped));
        Value::Object(map)
    };
    let paint_values: Vec<Value> = paints.iter().zip(&game_uv).map(|(item, mapped)| serialize(item, mapped)).collect();
    let layer_values: Vec<Value> = layers.iter().zip(&game_uv[paints.len()..]).map(|(item, mapped)| serialize(item, mapped)).collect();
    Ok(json!({
        "version": GEOMETRY_VERSION,
        "source": BASE_ENTRY,
        "size": OUTPUT_SIZE,
        "alignment": "Template aligned or stock TEXCOORD0 + primary material transform",
        "layers": layer_values,
        "paints": paint_values,
        "guides": {"template": {"anchors": template}, "game-uv": {"anchors": native}},
        "regulationCalibration": calibration.to_value(),
        "gameUv": {
            "texcoord": 0,
            "matrix": matrix,
            "surfaceTriangles": surface.len(),
            "courtSurfacePolygons": polygons_value(&court_surface),
            "hardwoodBounds": native_bounds,
            "projectionCoverage": coverage,
            "materialParameters": parameters,
            "shaderTransform": "UV sampled after floor.b4426d5f497374fc.script and PS.a6d99aa7f8ccf828.shader",
            "inGameVerified": false,
        },
    }))
}

/// Cache path: crate::files::data_root().join("cache").join("experimental-stock-lines.json").
pub fn cache_path() -> PathBuf {
    files::data_root().join("cache").join("experimental-stock-lines.json")
}

fn adopt_legacy(destination: &Path) -> Option<Geometry> {
    for candidate in geometry::legacy_candidates() {
        if !candidate.is_file() {
            continue;
        }
        let Ok(bytes) = files::read_bounded(&candidate, MAX_GEOMETRY_BYTES as u64) else { continue };
        let Ok(document) = pyjson::parse(&bytes, MAX_GEOMETRY_BYTES, 64) else { continue };
        if !geometry::validate(&document) || files::write_atomic(destination, &bytes, &[&candidate]).is_err() {
            continue;
        }
        if let Ok(result) = Geometry::from_document(document) {
            return Some(result);
        }
    }
    None
}

/// Port of `prepare_geometry`: reuse a valid cache, adopt (copy) a validated legacy cache, or
/// build from the installed game; the cache is written atomically under a cross-process lock.
pub fn prepare(game_root: Option<&Path>, cancelled: &dyn Fn() -> bool) -> Result<Geometry> {
    let destination = cache_path();
    let _lock = crate::lock::FileLock::acquire(&destination.with_extension("json.lock"), std::time::Duration::from_secs(300))?;
    if let Ok(cached) = Geometry::load(&destination) {
        return Ok(cached);
    }
    if let Some(adopted) = adopt_legacy(&destination) {
        return Ok(adopted);
    }
    let root = crate::game::find_root(game_root)?;
    let document = build(&root, cancelled)?;
    if !geometry::validate(&document) {
        bail!("The generated stock geometry did not pass cache validation.");
    }
    let data = pyjson::dumps(&document, Style::COMPACT);
    if data.len() > MAX_GEOMETRY_BYTES {
        bail!("The generated stock geometry exceeds the cache size limit.");
    }
    check(cancelled)?;
    files::write_atomic(&destination, data.as_bytes(), &[])?;
    Geometry::from_document(document)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn python_rounding_and_sum() {
        assert_eq!(round4(0.03125), 0.0312);
        assert_eq!(round4(-0.03125), -0.0312);
        assert_eq!(round4(1.00005), 1.0001);
        assert_eq!(round4(2.00015), 2.0002);
        assert_eq!(round4(4096.56484999), 4096.5648);
        assert_eq!(round4(0.09375), 0.0938);
        assert_eq!(round4(-1234.56785), -1234.5678);
        assert_eq!(py_sum([0.1; 10]), 1.0);
        assert_eq!(py_sum([1e16, 1.0, -1e16]), 1.0);
    }

    #[test]
    fn clip_u_keeps_stripe() {
        let triangle = [[0.0, 0.0, 0.0], [2.0, 0.0, 1.0], [0.0, 2.0, 1.0]];
        let above = clip_u(&triangle, 0.25, true);
        assert_eq!(above.len(), 4);
        assert!(above.iter().all(|p| p[2] >= 0.25));
        let stripe = clip_u(&above, 0.75, false);
        assert_eq!(stripe.len(), 4);
        assert!(stripe.iter().all(|p| (0.25..=0.75).contains(&p[2])));
        assert!(clip_u(&triangle, 2.0, true).is_empty());
        assert_eq!(clip_u(&triangle, -1.0, true), triangle.to_vec());
    }

    #[test]
    fn logo_transform_matches_python() {
        let matrix = logo_transform(&json!({"Logo0OffsetY": 0.0005, "Logo0ScaleX": 0.969, "Logo0ScaleY": 1.001}));
        assert_eq!(matrix, [[0.969, -0.0, 0.015500000000000014], [0.0, 1.001, 5.00000000069889e-07]]);
        assert!(matrix[0][1].is_sign_negative());
        assert_eq!(logo_transform(&json!({})), [[1.0, -0.0, 0.0], [0.0, 1.0, 0.0]]);
        assert_eq!(transform_uv([0.25, 0.5], &[[2.0, 0.0, 0.1], [0.0, 1.0, -0.5]]), [0.6, 0.0]);
    }

    fn unit_surface() -> Vec<SurfaceTriangle> {
        let corners = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
        [[0, 1, 2], [0, 2, 3]]
            .iter()
            .map(|t| SurfaceTriangle { points: t.map(|i| corners[i]), uvs: t.map(|i| corners[i]), bounds: [0.0, 0.0, 1.0, 1.0] })
            .collect()
    }

    #[test]
    fn projection_preserves_coverage() {
        let identity = [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0]];
        let square = vec![vec![[0.25, 0.25], [0.75, 0.25], [0.75, 0.75], [0.25, 0.75]]];
        let (mapped, coverage) = project_polygons(&square, &unit_surface(), &identity).unwrap();
        assert_eq!(mapped.len(), 2);
        assert!((coverage - 1.0).abs() < 1e-12);
        let area: f64 = mapped.iter().map(|p| geom::area(p)).sum();
        assert!((area - 0.25 * 8192.0 * 4096.0).abs() < 1.0);
        let outside = vec![vec![[0.5, 0.5], [1.5, 0.5], [1.5, 1.5], [0.5, 1.5]]];
        assert!(project_polygons(&outside, &unit_surface(), &identity).is_err());
        assert_eq!(project_anchor([0.5, 0.25], &unit_surface(), &identity).unwrap(), [4096.0, 1024.0]);
        assert!(project_anchor([2.0, 2.0], &unit_surface(), &identity).is_err());
    }

    #[test]
    fn three_point_enclosure_spans_corners() {
        let polygon = three_point_enclosure([0.0, 0.0], -10.0, 1.0, 20.0, 19.0);
        assert_eq!(polygon.len(), 163);
        assert_eq!(polygon[0], [-19.0, -10.0]);
        assert!((polygon[81][1] - 20.0).abs() < 1e-12);
        assert!((polygon[1][0] + 19.0).abs() < 1e-9);
    }
}
