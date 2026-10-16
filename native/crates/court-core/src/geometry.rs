//! Validated stock court geometry (game-UV marking and paint polygons) and derived regions.
use crate::error::{Error, Result};
use crate::geom::{self, Point, Polygon};
use crate::pyjson;
use serde::Serialize;
use serde_json::Value;
use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};

pub const GEOMETRY_VERSION: i64 = 4;
pub const MAX_GEOMETRY_BYTES: usize = 16 * 1024 * 1024;
pub const OUTPUT_SIZE: [u32; 2] = [8192, 4096];
pub const BASE_ENTRY: &str = "levels/floor_myteam2k26_int_floor.iff";
pub const THREE_POINT_IDS: [&str; 3] = ["NBA_line_three_point_lowShape", "college-three", "high-school-three"];
pub const NAMES: [(&str, &str); 14] = [
    ("line_center_circle_inner_lowShape", "Center Circle - Inner"),
    ("line_center_circle_outer_lowShape", "Center Circle - Outer"),
    ("line_midcourt_side_lowShape", "Center Line - Sides"),
    ("line_midcourt_center_lowShape", "Center Line - Center"),
    ("NBA_line_three_point_lowShape", "NBA Three"),
    ("line_free_throw_circle_lowShape", "Free Throw Circles"),
    ("line_charge_circle_lowShape", "Charge Circles"),
    ("line_side_base_lowShape", "Court Boundary"),
    ("line_camera_lowShape", "Media Lines"),
    ("line_tab_low_3Shape", "Sideline Hash Marks"),
    ("line_tab_lane_inner_lowShape", "Inner Lane Hash Marks"),
    ("line_lane_inner_lowShape", "Inner Key Lines"),
    ("line_tab_lane_lowShape", "Lane Hash Marks"),
    ("line_lane_free_throw_lowShape", "Key Lines"),
];

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StockLayer {
    pub id: String,
    pub name: String,
    pub visible: bool,
    pub color: String,
    /// Game-UV polygons in 8192 x 4096 texture pixels.
    pub polygons: Vec<Polygon>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Anchor {
    pub id: String,
    pub name: String,
    pub x: f64,
    pub y: f64,
}

#[derive(Clone, Debug)]
pub struct Geometry {
    pub document: Value,
    pub revision: String,
    pub lines: Vec<StockLayer>,
    pub paints: Vec<StockLayer>,
    pub surface: Vec<Polygon>,
    pub hardwood_bounds: [i64; 4],
    pub anchors: Vec<Anchor>,
}

fn number(value: &Value, maximum: f64) -> bool {
    value.as_f64().is_some_and(|v| v.is_finite() && v.abs() <= maximum) && (value.is_f64() || value.is_i64() || value.is_u64())
}

fn valid_polygons(value: Option<&Value>, count: &mut usize) -> bool {
    let Some(list) = value.and_then(Value::as_array) else { return false };
    if list.is_empty() || list.len() > 50_000 {
        return false;
    }
    for polygon in list {
        let Some(points) = polygon.as_array() else { return false };
        if points.len() < 3 || points.len() > 1024 {
            return false;
        }
        *count += points.len();
        if *count > 250_000 {
            return false;
        }
        for point in points {
            let Some(pair) = point.as_array() else { return false };
            if pair.len() != 2 || !pair.iter().all(|c| number(c, 65536.0)) {
                return false;
            }
        }
    }
    true
}

/// Port of `_valid_geometry`: rejects stale, malformed or inconsistent caches.
pub fn validate(document: &Value) -> bool {
    let Some(root) = document.as_object() else { return false };
    if root.get("version").and_then(Value::as_i64) != Some(GEOMETRY_VERSION) || !root.get("version").is_some_and(Value::is_i64) {
        return false;
    }
    if root.get("size") != Some(&serde_json::json!([8192, 4096])) || root.get("source").and_then(Value::as_str) != Some(BASE_ENTRY) {
        return false;
    }
    let (Some(layers), Some(paints)) = (root.get("layers").and_then(Value::as_array), root.get("paints").and_then(Value::as_array)) else { return false };
    if !(16..=64).contains(&layers.len()) || !(6..=32).contains(&paints.len()) {
        return false;
    }
    let mut ids = HashSet::new();
    let mut count = 0usize;
    let hex = regex::Regex::new("^#[0-9a-fA-F]{6}$").unwrap();
    for layer in layers.iter().chain(paints) {
        let Some(item) = layer.as_object() else { return false };
        let id = item.get("id").and_then(Value::as_str).unwrap_or("");
        let name = item.get("name").and_then(Value::as_str).unwrap_or("");
        if id.is_empty() || id.chars().count() > 256 || ids.contains(id) || name.is_empty() || name.chars().count() > 256 {
            return false;
        }
        if !item.get("visible").is_some_and(Value::is_boolean) || !item.get("color").and_then(Value::as_str).is_some_and(|c| hex.is_match(c)) {
            return false;
        }
        if !valid_polygons(item.get("polygons"), &mut count) || !valid_polygons(item.get("gameUvPolygons"), &mut count) {
            return false;
        }
        ids.insert(id.to_string());
    }
    let layer_ids: HashSet<&str> = layers.iter().filter_map(|l| l.get("id").and_then(Value::as_str)).collect();
    if !NAMES.iter().map(|(id, _)| *id).chain(["college-three", "high-school-three"]).all(|id| layer_ids.contains(id)) {
        return false;
    }
    let paint_ids: HashSet<&str> = paints.iter().filter_map(|l| l.get("id").and_then(Value::as_str)).collect();
    for prefix in ["paint", "secondary-paint", "two-point"] {
        for side in ["left", "right"] {
            if !paint_ids.contains(format!("{prefix}-{side}").as_str()) {
                return false;
            }
        }
    }
    let mut required: Vec<String> = vec!["court-center".into()];
    required.extend((0..4).map(|i| format!("court-corner-{i}")));
    for side in ["left", "right"] {
        for kind in ["free-throw", "paint-center"] {
            required.push(format!("{side}-{kind}"));
        }
        required.extend((0..4).map(|i| format!("{side}-key-{i}")));
    }
    let Some(guides) = root.get("guides").and_then(Value::as_object) else { return false };
    let mut guide_ids = Vec::new();
    for mapping in ["template", "game-uv"] {
        let Some(anchors) = guides.get(mapping).and_then(|g| g.get("anchors")).and_then(Value::as_array) else { return false };
        if !(17..=64).contains(&anchors.len()) {
            return false;
        }
        let mut seen = HashSet::new();
        for anchor in anchors {
            let id = anchor.get("id").and_then(Value::as_str).unwrap_or("");
            let name = anchor.get("name").and_then(Value::as_str).unwrap_or("");
            if id.is_empty() || id.chars().count() > 256 || seen.contains(id) || name.is_empty() || name.chars().count() > 256 {
                return false;
            }
            if !anchor.get("x").is_some_and(|v| number(v, 65536.0)) || !anchor.get("y").is_some_and(|v| number(v, 65536.0)) {
                return false;
            }
            seen.insert(id.to_string());
        }
        if !required.iter().all(|id| seen.contains(id)) {
            return false;
        }
        let corners: Vec<(f64, f64)> = anchors
            .iter()
            .filter(|a| a.get("id").and_then(Value::as_str).is_some_and(|id| id.starts_with("court-corner-")))
            .map(|a| (a["x"].as_f64().unwrap(), a["y"].as_f64().unwrap()))
            .collect();
        let distinct: HashSet<(u64, u64)> = corners.iter().map(|(x, y)| (x.to_bits(), y.to_bits())).collect();
        let (min_x, max_x) = corners.iter().fold((f64::MAX, f64::MIN), |(a, b), c| (a.min(c.0), b.max(c.0)));
        let (min_y, max_y) = corners.iter().fold((f64::MAX, f64::MIN), |(a, b), c| (a.min(c.1), b.max(c.1)));
        if corners.len() != 4 || distinct.len() != 4 || min_x >= max_x || min_y >= max_y
            || corners.iter().any(|(x, y)| !(0.0..=8192.0).contains(&x.round_ties_even()) || !(0.0..=4096.0).contains(&y.round_ties_even()))
        {
            return false;
        }
        guide_ids.push(seen);
    }
    if guide_ids[0] != guide_ids[1] {
        return false;
    }
    let Some(uv) = root.get("gameUv").and_then(Value::as_object) else { return false };
    if uv.get("texcoord").and_then(Value::as_i64) != Some(0) || !uv.get("texcoord").is_some_and(Value::is_i64) {
        return false;
    }
    let Some(matrix) = uv.get("matrix").and_then(Value::as_array) else { return false };
    if matrix.len() != 2 || matrix.iter().any(|row| !row.as_array().is_some_and(|r| r.len() == 3 && r.iter().all(|v| number(v, 65536.0)))) {
        return false;
    }
    let m = |r: usize, c: usize| matrix[r][c].as_f64().unwrap();
    if (m(0, 0) * m(1, 1) - m(0, 1) * m(1, 0)).abs() < 1e-9 {
        return false;
    }
    let Some(bounds) = uv.get("hardwoodBounds").and_then(Value::as_array) else { return false };
    if bounds.len() != 4 || bounds.iter().any(|v| !v.is_i64()) {
        return false;
    }
    let b: Vec<i64> = bounds.iter().map(|v| v.as_i64().unwrap()).collect();
    if b[0] < 0 || b[1] < 0 || b[2] <= 0 || b[3] <= 0 || b[0] + b[2] > 8192 || b[1] + b[3] > 4096 {
        return false;
    }
    if !uv.get("surfaceTriangles").and_then(Value::as_i64).is_some_and(|n| (1..=100_000).contains(&n)) || !uv.get("surfaceTriangles").is_some_and(Value::is_i64) {
        return false;
    }
    let mut surface_count = 0;
    if !valid_polygons(uv.get("courtSurfacePolygons"), &mut surface_count) {
        return false;
    }
    let points: Vec<(f64, f64)> = uv["courtSurfacePolygons"].as_array().unwrap().iter().flat_map(|p| p.as_array().unwrap().iter().map(|q| (q[0].as_f64().unwrap(), q[1].as_f64().unwrap()))).collect();
    let left = points.iter().map(|p| p.0).fold(f64::MAX, f64::min).floor() as i64;
    let top = points.iter().map(|p| p.1).fold(f64::MAX, f64::min).floor() as i64;
    let right = points.iter().map(|p| p.0).fold(f64::MIN, f64::max).ceil() as i64;
    let bottom = points.iter().map(|p| p.1).fold(f64::MIN, f64::max).ceil() as i64;
    if b != vec![left, top, right - left, bottom - top] {
        return false;
    }
    let Some(coverage) = uv.get("projectionCoverage").and_then(Value::as_object) else { return false };
    let coverage_ids: HashSet<String> = coverage.keys().cloned().collect();
    coverage_ids == ids && coverage.values().all(|v| number(v, 65536.0) && (0.999..=1.001).contains(&v.as_f64().unwrap()))
}

/// Same digest as Python's `geometry_revision` (sorted keys, compact separators, ASCII).
pub fn revision(document: &Value) -> String {
    crate::files::sha256_hex(pyjson::dumps(document, pyjson::Style::REVISION).as_bytes())
}

fn polygons(value: &Value) -> Vec<Polygon> {
    value
        .as_array()
        .map(|list| {
            list.iter()
                .filter_map(Value::as_array)
                .map(|points| points.iter().filter_map(|p| Some([p.get(0)?.as_f64()?, p.get(1)?.as_f64()?])).collect())
                .collect()
        })
        .unwrap_or_default()
}

fn stock_layers(value: &Value) -> Vec<StockLayer> {
    value
        .as_array()
        .map(|list| {
            list.iter()
                .map(|item| StockLayer {
                    id: item["id"].as_str().unwrap_or_default().into(),
                    name: item["name"].as_str().unwrap_or_default().into(),
                    visible: item["visible"].as_bool().unwrap_or(false),
                    color: item["color"].as_str().unwrap_or("#FFFFFF").to_uppercase(),
                    polygons: polygons(&item["gameUvPolygons"]),
                })
                .collect()
        })
        .unwrap_or_default()
}

impl Geometry {
    pub fn from_document(document: Value) -> Result<Self> {
        if !validate(&document) {
            return Err(Error::msg("The stock court geometry cache is invalid or stale."));
        }
        let revision = revision(&document);
        let uv = &document["gameUv"];
        let bounds = uv["hardwoodBounds"].as_array().unwrap();
        let anchors = document["guides"]["game-uv"]["anchors"]
            .as_array()
            .unwrap()
            .iter()
            .map(|a| Anchor { id: a["id"].as_str().unwrap().into(), name: a["name"].as_str().unwrap().into(), x: a["x"].as_f64().unwrap(), y: a["y"].as_f64().unwrap() })
            .collect();
        Ok(Self {
            lines: stock_layers(&document["layers"]),
            paints: stock_layers(&document["paints"]),
            surface: polygons(&uv["courtSurfacePolygons"]),
            hardwood_bounds: [bounds[0].as_i64().unwrap(), bounds[1].as_i64().unwrap(), bounds[2].as_i64().unwrap(), bounds[3].as_i64().unwrap()],
            anchors,
            revision,
            document,
        })
    }

    pub fn load(path: &Path) -> Result<Self> {
        let bytes = crate::files::read_bounded(path, MAX_GEOMETRY_BYTES as u64)?;
        let document = pyjson::parse(&bytes, MAX_GEOMETRY_BYTES, 64)?;
        Self::from_document(document)
    }

    pub fn anchor(&self, id: &str) -> Option<Point> {
        self.anchors.iter().find(|a| a.id == id).map(|a| [a.x, a.y])
    }

    pub fn court_center(&self) -> Point {
        self.anchor("court-center").unwrap_or([4096.0, 2048.0])
    }

    pub fn line(&self, id: &str) -> Option<&StockLayer> {
        self.lines.iter().find(|l| l.id == id)
    }

    /// Integer conversion boundary from the four court-corner guides (`target_court_bounds`).
    pub fn court_corner_bounds(&self) -> Result<[i64; 4]> {
        let corners: Vec<&Anchor> = self.anchors.iter().filter(|a| a.id.starts_with("court-corner-")).collect();
        if corners.len() != 4 {
            return Err(Error::msg("The stock court geometry has no supported conversion boundary."));
        }
        let r = |v: f64| v.round_ties_even() as i64;
        let bounds = [
            r(corners.iter().map(|a| a.x).fold(f64::MAX, f64::min)),
            r(corners.iter().map(|a| a.y).fold(f64::MAX, f64::min)),
            r(corners.iter().map(|a| a.x).fold(f64::MIN, f64::max)),
            r(corners.iter().map(|a| a.y).fold(f64::MIN, f64::max)),
        ];
        if !(0 <= bounds[0] && bounds[0] < bounds[2] && bounds[2] <= 8192 && 0 <= bounds[1] && bounds[1] < bounds[3] && bounds[3] <= 4096) {
            return Err(Error::msg("The stock conversion boundary falls outside the court texture."));
        }
        Ok(bounds)
    }
}

/// Visible three-point line that bounds the second hardwood and two-point paint.
pub fn active_three_point<'a>(visible: impl Fn(&str) -> bool) -> Option<&'a str> {
    THREE_POINT_IDS.into_iter().find(|id| visible(id))
}

/// Export-path enclosures: per-side convex hulls split at the line's horizontal midpoint.
pub fn three_point_enclosures(geometry: &Geometry, line_id: &str) -> Vec<Polygon> {
    let Some(layer) = geometry.line(line_id) else { return Vec::new() };
    let points: Vec<Point> = layer.polygons.iter().flatten().copied().collect();
    if points.is_empty() {
        return Vec::new();
    }
    let min_x = points.iter().map(|p| p[0]).fold(f64::MAX, f64::min);
    let max_x = points.iter().map(|p| p[0]).fold(f64::MIN, f64::max);
    let midpoint = (min_x + max_x) / 2.0;
    [true, false]
        .into_iter()
        .filter_map(|left| {
            let side: Vec<Point> = points.iter().copied().filter(|p| (p[0] < midpoint) == left).collect();
            let hull = geom::convex_hull_geos(&side);
            (hull.len() >= 3).then_some(hull)
        })
        .collect()
}

/// Paint regions for the current boundary: two-point paint clipped to the enclosures.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Regions {
    pub boundary: Option<String>,
    pub enclosures: Vec<Polygon>,
    /// Polygons for each paint id (including `main-court-area`).
    pub paints: BTreeMap<String, Vec<Polygon>>,
}

pub fn regions(geometry: &Geometry, boundary: Option<&str>) -> Regions {
    let enclosures = boundary.map(|id| three_point_enclosures(geometry, id)).unwrap_or_default();
    let mut paints = BTreeMap::new();
    let mut all: Vec<Polygon> = Vec::new();
    for layer in &geometry.paints {
        let polygons: Vec<Polygon> = if layer.id == "two-point-left" || layer.id == "two-point-right" {
            layer
                .polygons
                .iter()
                .flat_map(|polygon| enclosures.iter().map(move |enclosure| geom::clip_convex(polygon, enclosure)))
                .filter(|p| p.len() >= 3 && geom::area(p) > 1e-6)
                .collect()
        } else {
            layer.polygons.clone()
        };
        all.extend(polygons.iter().cloned());
        paints.insert(layer.id.clone(), polygons);
    }
    let main = geom::shape_contours(&geom::difference(&geometry.surface, &all));
    paints.insert("main-court-area".into(), main);
    Regions { boundary: boundary.map(str::to_string), enclosures, paints }
}

/// Locations searched for an existing (validated, read-only) geometry cache.
pub fn legacy_candidates() -> Vec<PathBuf> {
    crate::legacy::roots().into_iter().map(|root| root.join("data").join("generated").join("experimental-stock-lines.json")).collect()
}
