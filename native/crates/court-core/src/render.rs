//! Full court composition (port of `render_experimental` and `_composite_logo`).
use crate::error::{Error, Result, check};
use crate::geom::Polygon;
use crate::geometry::{self, Geometry};
use crate::hardwood::{self, Settings};
use crate::raster::{self, Filter, Mask, Rgba};
use serde_json::Value;
use std::collections::HashMap;
use std::path::PathBuf;

pub const LOGO_INTERMEDIATE_MAX_PIXELS: u64 = 16 * 1024 * 1024;
pub const DEFAULT_PAINT: &str = "#19583F";

#[derive(Clone, Debug)]
pub struct FloorInput {
    pub path: PathBuf,
    pub revision: Option<String>,
    pub settings: Settings,
    pub game_data_alpha: bool,
}

#[derive(Clone, Debug)]
pub struct LayerSetting {
    pub visible: Option<bool>,
    pub color: Option<[u8; 3]>,
}

#[derive(Clone, Debug)]
pub struct LogoInput {
    pub path: PathBuf,
    pub revision: Option<String>,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
    pub rotation: f64,
    pub opacity: f64,
    pub visible: bool,
    pub flip_x: bool,
    pub flip_y: bool,
    pub game_data_alpha: bool,
}

#[derive(Clone, Debug)]
pub struct RenderRequest {
    pub floor: FloorInput,
    pub two_point: Option<FloorInput>,
    pub two_point_enabled: bool,
    pub outside_visible: bool,
    pub outside_color: [u8; 3],
    pub lines: HashMap<String, LayerSetting>,
    pub paints: HashMap<String, LayerSetting>,
    pub logos: Vec<LogoInput>,
}

pub fn parse_hex(value: &str) -> Option<[u8; 3]> {
    let hex = value.trim().trim_start_matches('#');
    let expanded: String = if hex.len() == 3 && hex.chars().all(|c| c.is_ascii_hexdigit()) {
        hex.chars().flat_map(|c| [c, c]).collect()
    } else {
        hex.to_string()
    };
    if expanded.len() != 6 || !expanded.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    let byte = |i: usize| u8::from_str_radix(&expanded[i..i + 2], 16).ok();
    Some([byte(0)?, byte(2)?, byte(4)?])
}

/// Canonical `#RRGGBB` (accepting three-digit shorthand), as in `StudioImages.Hex`.
pub fn normalize_hex(value: &str) -> Option<String> {
    parse_hex(value).map(|[r, g, b]| format!("#{r:02X}{g:02X}{b:02X}"))
}

fn layer_settings(value: Option<&Value>) -> HashMap<String, LayerSetting> {
    let mut out = HashMap::new();
    if let Some(map) = value.and_then(Value::as_object) {
        for (id, setting) in map {
            out.insert(
                id.clone(),
                LayerSetting { visible: setting.get("visible").and_then(Value::as_bool), color: setting.get("color").and_then(Value::as_str).and_then(parse_hex) },
            );
        }
    }
    out
}

fn floor_input(floor: &Value, resolve: &dyn Fn(&str) -> PathBuf) -> Result<FloorInput> {
    let path = floor.get("path").and_then(Value::as_str).unwrap_or_default();
    let revision = match floor.get("sourceRevision") {
        Some(Value::String(text)) if crate::files::is_revision(text) => Some(text.clone()),
        Some(Value::Null) | None => None,
        Some(_) => return Err(Error::msg("Invalid artwork source revision.")),
    };
    Ok(FloorInput {
        path: resolve(path),
        revision,
        settings: Settings::read(floor)?,
        game_data_alpha: floor.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData"),
    })
}

impl RenderRequest {
    /// Builds a render request from a version-2 game-UV project snapshot.
    pub fn from_project(project: &Value, resolve: &dyn Fn(&str) -> PathBuf) -> Result<Self> {
        let floor = project.get("floor").filter(|v| v.is_object()).ok_or_else(|| Error::msg("Choose a stock hardwood texture."))?;
        let two_point = match project.get("twoPointFloor") {
            Some(value @ Value::Object(_)) => Some(floor_input(value, resolve)?),
            Some(Value::Null) | None => None,
            Some(_) => return Err(Error::msg("Invalid two-point hardwood selection.")),
        };
        let two_point_enabled = match project.get("twoPointHardwoodEnabled") {
            None | Some(Value::Null) => true,
            Some(Value::Bool(flag)) => *flag,
            Some(_) => return Err(Error::msg("Invalid two-point hardwood enabled state.")),
        };
        if project.get("mappingMode").and_then(Value::as_str).is_some_and(|m| m != "game-uv") {
            return Err(Error::msg("Choose a supported marking alignment."));
        }
        let number = |item: &Value, key: &str, default: f64| -> Option<f64> {
            match item.get(key) {
                None | Some(Value::Null) => Some(default),
                Some(value) => value.as_f64(),
            }
        };
        let mut logos = Vec::new();
        for item in project.get("logoImages").and_then(Value::as_array).into_iter().flatten() {
            let width = number(item, "width", 100.0).ok_or_else(|| Error::msg("Logo placement must be numeric."))?;
            let logo = LogoInput {
                path: resolve(item.get("path").and_then(Value::as_str).unwrap_or_default()),
                revision: item.get("sourceRevision").and_then(Value::as_str).map(str::to_string),
                x: number(item, "x", 0.0).unwrap_or(f64::NAN),
                y: number(item, "y", 0.0).unwrap_or(f64::NAN),
                width,
                height: number(item, "height", width).unwrap_or(f64::NAN),
                rotation: number(item, "rotation", 0.0).unwrap_or(f64::NAN),
                opacity: number(item, "opacity", 100.0).unwrap_or(f64::NAN),
                visible: item.get("visible").and_then(Value::as_bool).unwrap_or(true),
                flip_x: item.get("flipX").and_then(Value::as_bool).unwrap_or(false),
                flip_y: item.get("flipY").and_then(Value::as_bool).unwrap_or(false),
                game_data_alpha: item.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData"),
            };
            logos.push(logo);
        }
        Ok(Self {
            floor: floor_input(floor, resolve)?,
            two_point,
            two_point_enabled,
            outside_visible: project.get("outsideVisible").and_then(Value::as_bool).unwrap_or(true),
            outside_color: project.get("outsideColor").and_then(Value::as_str).and_then(parse_hex).unwrap_or([0x19, 0x58, 0x3F]),
            lines: layer_settings(project.get("lineSettings")),
            paints: layer_settings(project.get("paintSettings")),
            logos,
        })
    }

    pub fn line_visible(&self, geometry: &Geometry, id: &str) -> bool {
        let default = geometry.line(id).map(|l| l.visible).unwrap_or(false);
        self.lines.get(id).and_then(|s| s.visible).unwrap_or(default)
    }

    /// Every artwork file this request reads (for overwrite protection).
    pub fn sources(&self) -> Vec<PathBuf> {
        let mut paths = vec![self.floor.path.clone()];
        if let Some(two) = &self.two_point {
            paths.push(two.path.clone());
        }
        paths.extend(self.logos.iter().map(|l| l.path.clone()));
        paths
    }
}

/// Decoded source pixels keyed by path and revision, shared across a render.
pub type ImageCache = HashMap<(PathBuf, Option<String>), Rgba>;

fn load(cache: &mut ImageCache, path: &PathBuf, revision: &Option<String>) -> Result<Rgba> {
    let key = (path.clone(), revision.clone());
    if let Some(image) = cache.get(&key) {
        return Ok(image.clone());
    }
    let image = raster::decode_file(path, revision.as_deref())?;
    cache.insert(key, image.clone());
    Ok(image)
}

fn scaled(points: &Polygon, scale: f64, dx: f64, dy: f64) -> Polygon {
    points.iter().map(|p| [p[0] * scale - dx, p[1] * scale - dy]).collect()
}

fn py_round(value: f64) -> i64 {
    value.round_ties_even() as i64
}

/// Prepares one hardwood layer exactly as the exporter does: adjust, fit, then place.
pub fn hardwood_layer(source: &Rgba, settings: &Settings, game_data_alpha: bool, width: u32, height: u32) -> Rgba {
    let mut pixels = source.clone();
    if game_data_alpha {
        pixels.set_alpha(255);
    }
    hardwood::adjust(&mut pixels, settings);
    let fitted = raster::fit(&pixels, width, height, Filter::Lanczos);
    drop(pixels);
    hardwood::place(&fitted, settings)
}

pub fn render(geometry: &Geometry, request: &RenderRequest, scale: f64, cache: &mut ImageCache, cancelled: &dyn Fn() -> bool) -> Result<Rgba> {
    let size = [py_round(8192.0 * scale) as u32, py_round(4096.0 * scale) as u32];
    let outside = request.outside_color;
    let mut canvas = Rgba::new(size[0], size[1], if request.outside_visible { [outside[0], outside[1], outside[2], 255] } else { [0; 4] });
    let [left, top, width, height] = geometry.hardwood_bounds;
    if width <= 0 || height <= 0 || left < 0 || top < 0 || left + width > 8192 || top + height > 4096 {
        return Err(Error::msg("Invalid experimental hardwood bounds."));
    }
    let (left, top, width, height) = (py_round(left as f64 * scale), py_round(top as f64 * scale), py_round(width as f64 * scale) as u32, py_round(height as f64 * scale) as u32);
    if !request.floor.path.is_file() {
        return Err(Error::msg("Choose a stock hardwood texture."));
    }
    check(cancelled)?;
    let main = load(cache, &request.floor.path, &request.floor.revision)?;
    let mut hardwood = hardwood_layer(&main, &request.floor.settings, request.floor.game_data_alpha, width, height);
    drop(main);
    check(cancelled)?;
    let mut mask = Mask::new(width, height);
    for polygon in &geometry.surface {
        raster::fill_mask(&mut mask, &scaled(polygon, scale, left as f64, top as f64), 255);
    }
    raster::multiply_alpha(&mut hardwood, &mask);
    raster::alpha_composite(&mut canvas, &hardwood, left, top);
    drop(hardwood);

    let boundary = geometry::active_three_point(|id| request.line_visible(geometry, id));
    let regions = geometry::regions(geometry, boundary);
    if let (true, Some(two)) = (request.two_point_enabled, &request.two_point) {
        if !two.path.is_file() {
            return Err(Error::msg("The selected two-point hardwood is missing."));
        }
        check(cancelled)?;
        let source = load(cache, &two.path, &two.revision)?;
        let mut secondary = hardwood_layer(&source, &two.settings, two.game_data_alpha, width, height);
        drop(source);
        let mut mask = Mask::new(width, height);
        for polygon in &regions.enclosures {
            raster::fill_mask(&mut mask, &scaled(polygon, scale, left as f64, top as f64), 255);
        }
        raster::multiply_alpha(&mut secondary, &mask);
        raster::alpha_composite(&mut canvas, &secondary, left, top);
    }
    check(cancelled)?;
    let paint_polygons = |id: &str| -> Vec<Polygon> { regions.paints.get(id).cloned().unwrap_or_default() };
    if let Some(setting) = request.paints.get("main-court-area").filter(|s| s.visible == Some(true)) {
        let mut mask = Mask::new(size[0], size[1]);
        for polygon in &geometry.surface {
            raster::fill_mask(&mut mask, &scaled(polygon, scale, 0.0, 0.0), 255);
        }
        for layer in &geometry.paints {
            for polygon in paint_polygons(&layer.id) {
                raster::fill_mask(&mut mask, &scaled(&polygon, scale, 0.0, 0.0), 0);
            }
        }
        let color = setting.color.unwrap_or([0x19, 0x58, 0x3F]);
        raster::paste_color(&mut canvas, [color[0], color[1], color[2], 255], &mask);
    }
    for layer in &geometry.paints {
        let setting = request.paints.get(&layer.id);
        if setting.and_then(|s| s.visible).unwrap_or(layer.visible) {
            let color = setting.and_then(|s| s.color).or_else(|| parse_hex(&layer.color)).unwrap_or([255; 3]);
            for polygon in paint_polygons(&layer.id) {
                raster::fill_polygon(&mut canvas, &scaled(&polygon, scale, 0.0, 0.0), [color[0], color[1], color[2], 255]);
            }
        }
    }
    for layer in &geometry.lines {
        let setting = request.lines.get(&layer.id);
        if setting.and_then(|s| s.visible).unwrap_or(layer.visible) {
            let color = setting.and_then(|s| s.color).or_else(|| parse_hex(&layer.color)).unwrap_or([255; 3]);
            for polygon in &layer.polygons {
                raster::fill_polygon(&mut canvas, &scaled(polygon, scale, 0.0, 0.0), [color[0], color[1], color[2], 255]);
            }
        }
    }
    for logo in &request.logos {
        check(cancelled)?;
        if logo.visible {
            composite_logo(&mut canvas, logo, scale, cache)?;
        }
    }
    Ok(canvas)
}

/// Port of `_composite_logo` (LANCZOS resize, flips, BICUBIC expanded rotation, opacity).
pub fn composite_logo(canvas: &mut Rgba, logo: &LogoInput, scale: f64, cache: &mut ImageCache) -> Result<()> {
    if !logo.path.exists() {
        if logo.revision.is_some() {
            return Err(Error::msg("Artwork used in the preview is missing. Reimport the logo before exporting."));
        }
        return Ok(());
    }
    let values = [logo.x, logo.y, logo.width, logo.height, logo.rotation, logo.opacity, scale];
    if values.iter().any(|v| !v.is_finite()) || scale <= 0.0 {
        return Err(Error::msg("Logo placement and scale must be finite numbers with a positive scale."));
    }
    let width = logo.width.max(1.0);
    let height = logo.height.max(1.0);
    let opacity = logo.opacity.clamp(0.0, 100.0) / 100.0;
    let target_w = py_round(width * scale).max(1) as u32;
    let target_h = py_round(height * scale).max(1) as u32;
    let angle = logo.rotation.rem_euclid(360.0).to_radians();
    let (cosine, sine) = (angle.cos(), angle.sin());
    let rotated_w = (cosine.abs() * target_w as f64 + sine.abs() * target_h as f64).ceil() + 2.0;
    let rotated_h = (sine.abs() * target_w as f64 + cosine.abs() * target_h as f64).ceil() + 2.0;
    let center_x = (logo.x + width / 2.0) * scale;
    let center_y = (logo.y + height / 2.0) * scale;
    let clip = [
        ((center_x - rotated_w / 2.0).floor() as i64).max(0),
        ((center_y - rotated_h / 2.0).floor() as i64).max(0),
        ((center_x + rotated_w / 2.0).ceil() as i64).min(canvas.width as i64),
        ((center_y + rotated_h / 2.0).ceil() as i64).min(canvas.height as i64),
    ];
    if clip[0] >= clip[2] || clip[1] >= clip[3] {
        if logo.revision.is_some() {
            crate::files::read_pinned(&logo.path, logo.revision.as_deref())?;
        }
        return Ok(());
    }
    let mut source = load(cache, &logo.path, &logo.revision)?;
    if logo.game_data_alpha {
        source.set_alpha(255);
    }
    if (target_w as u64 * target_h as u64).max((rotated_w * rotated_h) as u64) > LOGO_INTERMEDIATE_MAX_PIXELS {
        // Invert the centered display transform and sample only visible court pixels.
        let sx = source.width as f64 / target_w as f64 * if logo.flip_x { -1.0 } else { 1.0 };
        let sy = source.height as f64 / target_h as f64 * if logo.flip_y { -1.0 } else { 1.0 };
        let (a, b, d, e) = (sx * cosine, sx * sine, -sy * sine, sy * cosine);
        let c = source.width as f64 / 2.0 + a * (clip[0] as f64 - center_x) + b * (clip[1] as f64 - center_y);
        let f = source.height as f64 / 2.0 + d * (clip[0] as f64 - center_x) + e * (clip[1] as f64 - center_y);
        let mut overlay = raster::affine_bicubic(&source, (clip[2] - clip[0]) as u32, (clip[3] - clip[1]) as u32, [a, b, c, d, e, f], true);
        if opacity < 0.999 {
            raster::scale_alpha(&mut overlay, opacity);
        }
        raster::alpha_composite(canvas, &overlay, clip[0], clip[1]);
        return Ok(());
    }
    let mut image = if source.width != target_w || source.height != target_h { raster::resize(&source, target_w, target_h, Filter::Lanczos, None) } else { source };
    if logo.flip_x {
        image = image.mirror();
    }
    if logo.flip_y {
        image = image.flip();
    }
    if logo.rotation.abs() > 0.001 {
        image = raster::rotate_expand(&image, -logo.rotation);
    }
    if opacity < 0.999 {
        raster::scale_alpha(&mut image, opacity);
    }
    let left = py_round(center_x - image.width as f64 / 2.0);
    let top = py_round(center_y - image.height as f64 / 2.0);
    raster::alpha_composite(canvas, &image, left, top);
    Ok(())
}
