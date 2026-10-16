//! The editable court document and its history. Snapshots use the exact version-2 project shape
//! produced by the WPF studio (`CreateProject`) and restore follows `RestoreProjectFromAsync`.
use crate::catalog::{self, Floor};
use crate::error::{Error, Result};
use crate::geometry::{self, Geometry};
use crate::hardwood::Settings;
use crate::render::{normalize_hex, parse_hex};
use serde::Serialize;
use serde_json::{Map, Value, json};
use std::path::{Path, PathBuf};
use std::sync::Arc;

pub const HISTORY_LIMIT: usize = 100;
pub const LOGO_SLOTS: usize = 4;
pub const DEFAULT_PAINT: &str = "#19583F";

#[derive(Clone, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Layer {
    pub id: String,
    pub name: String,
    pub visible: bool,
    pub color: String,
    pub default_visible: bool,
    pub default_color: String,
}

#[derive(Clone, Debug)]
pub struct Hardwood {
    pub floor: Floor,
    pub revision: Option<String>,
    pub settings: Settings,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Logo {
    pub id: String,
    pub name: String,
    pub path: PathBuf,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
    pub rotation: f64,
    pub opacity: f64,
    pub visible: bool,
    pub scale_locked: bool,
    pub flip_x: bool,
    pub flip_y: bool,
    /// Present while the artwork pixels are available (WPF writes it only for loaded images).
    pub revision: Option<String>,
    /// artworkProjectPath/Revision, artworkDdsPath/Revision, artworkAlphaMode and textSettings.
    pub artwork: Map<String, Value>,
    pub pixel_width: u32,
    pub pixel_height: u32,
}

impl Logo {
    pub fn center(&self) -> [f64; 2] {
        [self.x + self.width / 2.0, self.y + self.height / 2.0]
    }
    pub fn is_text(&self) -> bool {
        self.artwork.contains_key("textSettings")
    }
    pub fn game_data(&self) -> bool {
        self.artwork.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData")
    }
    fn set_rotation(&mut self, value: f64) {
        self.rotation = if value.is_finite() { value % 360.0 } else { 0.0 };
    }
    fn set_size(&mut self, width: f64, height: f64) {
        self.width = if width.is_finite() { width.clamp(1.0, 32768.0) } else { 1.0 };
        self.height = if height.is_finite() { height.clamp(1.0, 32768.0) } else { 1.0 };
    }
}

#[derive(Clone, Debug)]
pub struct Document {
    pub name: String,
    pub path: Option<PathBuf>,
    pub paints: Vec<Layer>,
    pub lines: Vec<Layer>,
    pub outside: Layer,
    pub floor: Option<Hardwood>,
    pub two_point: Option<Hardwood>,
    pub two_point_enabled: bool,
    pub logos: Vec<Logo>,
}

/// Artwork the document references; the host decodes, owns and caches the pixels.
pub trait Assets {
    /// Content revision (SHA-256) of a hardwood image after decoding it.
    fn floor(&mut self, path: &Path, game_data: bool) -> Result<String>;
    /// Copies an external logo into app-owned storage when needed; returns (owned path, revision, width, height).
    fn logo(&mut self, path: &Path, game_data: bool) -> Result<(PathBuf, String, u32, u32)>;
}

fn layer(id: &str, name: &str, visible: bool, color: &str) -> Layer {
    Layer { id: id.into(), name: name.into(), visible, color: color.into(), default_visible: visible, default_color: color.into() }
}

fn rgb(hex: &str) -> Value {
    let [r, g, b] = parse_hex(hex).unwrap_or([0, 0, 0]);
    json!([r, g, b])
}

/// Read-only data shared by every document in a session.
pub struct Library {
    pub geometry: Arc<Geometry>,
    pub floors: Vec<Floor>,
    pub loaded_visibility: Map<String, Value>,
    pub default_floor: Option<String>,
    /// Folder that relative (non-portable) asset paths resolve against.
    pub application_root: PathBuf,
    /// Prepared floor libraries (`.../assets`) used when relocating old absolute paths.
    pub library_roots: Vec<PathBuf>,
}

impl Library {
    /// Port of `load_stock_state`'s visibility map (layers, then catalog groups and floors).
    pub fn new(geometry: Arc<Geometry>, images: &[Value], application_root: PathBuf, library_roots: Vec<PathBuf>) -> Self {
        let mut visibility = Map::new();
        for id in ["stock-floors", "stock-paints", "stock-lines", "stock-outside"] {
            visibility.insert(id.into(), Value::Bool(true));
        }
        for layer in geometry.paints.iter().chain(&geometry.lines) {
            visibility.insert(layer.id.clone(), Value::Bool(layer.visible));
        }
        let mut template_index = 0;
        for image in images {
            let id = image.get("id").and_then(Value::as_str).unwrap_or_default().to_string();
            if image.get("isTemplate").and_then(Value::as_bool) == Some(true) {
                let category = image.get("category").and_then(Value::as_str).unwrap_or("Special");
                let group = format!("floor_template_category_{}", catalog::safe_stem(category));
                if !visibility.contains_key(&group) {
                    visibility.insert(group, Value::Bool(template_index == 0));
                }
                visibility.insert(id, Value::Bool(template_index == 0));
                template_index += 1;
            } else {
                visibility.insert(id, Value::Bool(false));
            }
        }
        let floors = catalog::read_many(images, &application_root);
        let default_floor = floors.iter().find(|f| visibility.get(&f.id).and_then(Value::as_bool) == Some(true)).or(floors.first()).map(|f| f.id.clone());
        Self { geometry, floors, loaded_visibility: visibility, default_floor, application_root, library_roots }
    }

    pub fn default_document(&self) -> Document {
        let mut paints: Vec<Layer> = self.geometry.paints.iter().map(|l| layer(&l.id, &l.name, l.visible, &l.color)).collect();
        paints.push(layer("main-court-area", "Three-Point Area", false, DEFAULT_PAINT));
        Document {
            name: "Untitled court".into(),
            path: None,
            paints,
            lines: self.geometry.lines.iter().map(|l| layer(&l.id, &l.name, l.visible, &l.color)).collect(),
            outside: layer("stock-outside", "Outside Color", true, DEFAULT_PAINT),
            floor: None,
            two_point: None,
            two_point_enabled: false,
            logos: Vec::new(),
        }
    }

    pub fn floor(&self, id: &str) -> Option<&Floor> {
        self.floors.iter().find(|f| f.id == id)
    }

    /// Port of `ResolvePath` (project-relative, app-relative and relocated legacy app paths).
    pub fn resolve_path(&self, path: &str, project_path: Option<&Path>, project_relative: bool) -> PathBuf {
        if path.trim().is_empty() {
            return PathBuf::from(path);
        }
        let candidate = PathBuf::from(path.replace('/', "\\"));
        if !candidate.is_absolute() {
            let local = self.application_root.join(&candidate);
            if let Some(project) = project_path.and_then(Path::parent) {
                let adjacent = project.join(&candidate);
                if project_relative || (!local.exists() && adjacent.exists()) {
                    return adjacent;
                }
            }
            if !local.exists() {
                for root in &self.library_roots {
                    let relocated = root.join(&candidate);
                    if relocated.exists() {
                        return relocated;
                    }
                }
            }
            return local;
        }
        if candidate.exists() {
            return candidate;
        }
        let lower = path.to_lowercase().replace('/', "\\");
        for marker in ["\\assets\\", "\\custom_floors\\", "\\logos\\"] {
            if let Some(index) = lower.find(marker) {
                let suffix = &path[index + 1..];
                let suffix_after_assets = &path[index + marker.len()..];
                let mut options = vec![self.application_root.join(suffix)];
                if marker == "\\assets\\" {
                    options.extend(self.library_roots.iter().map(|root| root.join(suffix_after_assets)));
                }
                if let Some(found) = options.into_iter().find(|p| p.exists()) {
                    return found;
                }
            }
        }
        candidate
    }
}

/// Snapshot comparison used to decide whether an edit changed the document.
fn same(a: &Value, b: &Value) -> bool {
    a == b
}

pub struct Studio {
    pub library: Library,
    pub doc: Document,
    pub undo: Vec<Value>,
    pub redo: Vec<Value>,
    pub dirty: bool,
    /// Baseline for a continuous hardwood or opacity gesture (one undo step when committed).
    pub gesture: Option<Value>,
    pub version: u64,
    pub recent: Vec<String>,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RestoreReport {
    pub missing: Vec<String>,
    pub updated: Vec<String>,
}

impl RestoreReport {
    pub fn status(&self) -> String {
        let mut notices = Vec::new();
        if !self.missing.is_empty() {
            notices.push(format!("Missing assets: {}", self.missing.join(", ")));
        }
        if !self.updated.is_empty() {
            notices.push(format!("Updated artwork loaded: {}", self.updated.join(", ")));
        }
        if notices.is_empty() { "Project restored.".into() } else { format!("Project restored. {}", notices.join(". ")) }
    }
}

impl Studio {
    pub fn new(library: Library) -> Self {
        let doc = library.default_document();
        Self { library, doc, undo: Vec::new(), redo: Vec::new(), dirty: false, gesture: None, version: 0, recent: Vec::new() }
    }

    pub fn geometry(&self) -> &Geometry {
        &self.library.geometry
    }

    fn all_layers(&self) -> impl Iterator<Item = &Layer> {
        self.doc.paints.iter().chain(&self.doc.lines).chain(std::iter::once(&self.doc.outside))
    }

    pub fn layer_mut(&mut self, id: &str) -> Option<&mut Layer> {
        if self.doc.outside.id == id {
            return Some(&mut self.doc.outside);
        }
        self.doc.paints.iter_mut().chain(self.doc.lines.iter_mut()).find(|l| l.id == id)
    }

    fn hardwood_snapshot(hardwood: &Hardwood) -> Value {
        let mut data = if hardwood.floor.source.is_object() { hardwood.floor.source.clone() } else { json!({}) };
        data["path"] = Value::String(hardwood.floor.path.to_string_lossy().into_owned());
        data["name"] = Value::String(hardwood.floor.name.clone());
        if let Some(revision) = &hardwood.revision {
            data["sourceRevision"] = Value::String(revision.clone());
        }
        data["textureSettings"] = hardwood.settings.to_json();
        data
    }

    pub fn serialize_logo(logo: &Logo) -> Value {
        let mut result = json!({
            "id": logo.id, "name": logo.name, "path": logo.path.to_string_lossy(),
            "x": logo.x, "y": logo.y, "width": logo.width, "height": logo.height, "rotation": logo.rotation,
            "opacity": logo.opacity, "visible": logo.visible, "scaleLocked": logo.scale_locked, "flipX": logo.flip_x, "flipY": logo.flip_y,
        });
        if let Some(revision) = &logo.revision {
            result["sourceRevision"] = Value::String(revision.clone());
        }
        for (key, value) in &logo.artwork {
            result[key] = value.clone();
        }
        result
    }

    /// Port of `CreateProject`.
    pub fn create_project(&self) -> Value {
        let mut visibility = self.library.loaded_visibility.clone();
        let selected = self.doc.floor.as_ref().map(|h| h.floor.id.as_str());
        for floor in &self.library.floors {
            visibility.insert(floor.id.clone(), Value::Bool(Some(floor.id.as_str()) == selected));
        }
        for key in visibility.keys().filter(|k| k.starts_with("floor_template_category_")).cloned().collect::<Vec<_>>() {
            visibility.insert(key, Value::Bool(true));
        }
        let mut colors = Map::new();
        let mut names = Map::new();
        for layer in self.all_layers() {
            visibility.insert(layer.id.clone(), Value::Bool(layer.visible));
            names.insert(layer.id.clone(), Value::String(layer.name.clone()));
            colors.insert(layer.id.clone(), rgb(&layer.color));
        }
        let settings = |layers: &[Layer]| -> Value {
            Value::Object(layers.iter().map(|l| (l.id.clone(), json!({"visible": l.visible, "color": l.color}))).collect())
        };
        let floor = self.doc.floor.as_ref().map(Self::hardwood_snapshot).unwrap_or_else(|| json!({"textureSettings": Settings::default().to_json()}));
        let customs: Vec<Value> = self
            .library
            .floors
            .iter()
            .filter(|f| f.category == "Custom")
            .map(|f| json!({"id": f.id, "name": f.name, "path": f.path.to_string_lossy(), "visible": Some(f.id.as_str()) == selected}))
            .collect();
        json!({
            "version": 2, "buildMode": "game-uv", "mappingMode": "game-uv",
            "floor": floor,
            "twoPointFloor": self.doc.two_point.as_ref().map(Self::hardwood_snapshot).unwrap_or(Value::Null),
            "twoPointHardwoodEnabled": self.doc.two_point_enabled,
            "outsideColor": self.doc.outside.color,
            "outsideVisible": self.doc.outside.visible,
            "visibility": visibility,
            "colorOverrides": colors,
            "layerNames": names,
            "paintSettings": settings(&self.doc.paints),
            "lineSettings": settings(&self.doc.lines),
            "logoImages": self.doc.logos.iter().map(Self::serialize_logo).collect::<Vec<_>>(),
            "customFloorImages": customs,
            "_projectPath": self.doc.path.as_ref().map(|p| Value::String(p.to_string_lossy().into_owned())).unwrap_or(Value::Null),
            "projectName": self.doc.name,
            "suite": {"app": "court-creator", "editor": "TwoK.Studio", "version": 1},
        })
    }

    fn record(&mut self, before: Value) {
        self.undo.push(before);
        if self.undo.len() > HISTORY_LIMIT {
            self.undo.remove(0);
        }
        self.redo.clear();
    }

    fn changed(&mut self) {
        self.dirty = true;
        self.version += 1;
    }

    /// Applies an edit and records one undo step only when the snapshot actually changed.
    pub fn change<T>(&mut self, edit: impl FnOnce(&mut Self) -> Result<T>) -> Result<T> {
        self.commit_gesture();
        let before = self.create_project();
        let result = edit(self)?;
        if !same(&before, &self.create_project()) {
            self.record(before);
            self.changed();
        }
        Ok(result)
    }

    /// Starts (or continues) a continuous gesture whose edits become one undo step.
    pub fn begin_gesture(&mut self) {
        if self.gesture.is_none() {
            self.gesture = Some(self.create_project());
        }
    }

    pub fn commit_gesture(&mut self) {
        if let Some(before) = self.gesture.take() {
            if !same(&before, &self.create_project()) {
                self.record(before);
                self.changed();
            }
        }
    }

    // --- Layers, outside and two-point hardwood ---

    pub fn set_layer(&mut self, id: &str, visible: Option<bool>, color: Option<&str>) -> Result<()> {
        let hex = match color {
            Some(c) => Some(normalize_hex(c).ok_or_else(|| Error::msg("Enter a valid three- or six-digit hex code."))?),
            None => None,
        };
        if !self.all_layers().any(|l| l.id == id) {
            return Err(Error::msg("That court layer is not available."));
        }
        self.change(|studio| {
            let layer = studio.layer_mut(id).unwrap();
            if let Some(visible) = visible {
                layer.visible = visible;
            }
            if let Some(hex) = hex {
                layer.color = hex;
            }
            Ok(())
        })
    }

    pub fn set_two_point_enabled(&mut self, enabled: bool) -> Result<()> {
        self.change(|studio| {
            studio.doc.two_point_enabled = enabled;
            Ok(())
        })
    }

    pub fn active_three_point(&self) -> Option<&'static str> {
        geometry::active_three_point(|id| self.doc.lines.iter().any(|l| l.id == id && l.visible))
    }

    /// Port of `FillCourtRegion`: the region under the point takes the primary color and turns on.
    pub fn fill_region(&mut self, point: [f64; 2], color: &str, regions: &geometry::Regions) -> Result<Option<String>> {
        if !point.iter().all(|v| v.is_finite()) || !(0.0..8192.0).contains(&point[0]) || !(0.0..4096.0).contains(&point[1]) {
            return Ok(None);
        }
        let mut target = None;
        for layer in &self.doc.paints {
            if layer.id == "main-court-area" {
                continue;
            }
            if regions.paints.get(&layer.id).is_some_and(|polygons| crate::geom::any_contains(polygons, point)) {
                target = Some(layer.id.clone());
                break;
            }
        }
        let in_surface = crate::geom::any_contains(&self.library.geometry.surface, point);
        let target = target.unwrap_or_else(|| if in_surface { "main-court-area".into() } else { "stock-outside".into() });
        self.set_layer(&target, Some(true), Some(color))?;
        Ok(Some(target))
    }

    // --- Hardwood selection and settings ---

    pub fn hardwood(&self, two_point: bool) -> Option<&Hardwood> {
        if two_point { self.doc.two_point.as_ref() } else { self.doc.floor.as_ref() }
    }

    /// Port of `SelectFloorAsync`/`SelectTwoPointFloorAsync` (settings kept when reselecting the same floor).
    pub fn select_floor(&mut self, floor: Floor, two_point: bool, assets: &mut dyn Assets) -> Result<()> {
        let current = self.hardwood(two_point);
        let settings = match current {
            Some(h) if h.floor.id == floor.id && crate::files::same_path(&h.floor.path, &floor.path) => h.settings,
            _ => Settings::read(&floor.source)?,
        };
        let revision = assets.floor(&floor.path, floor.source.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData"))?;
        let id = floor.id.clone();
        let before = self.create_project();
        self.commit_gesture();
        let hardwood = Hardwood { floor, revision: Some(revision), settings };
        if two_point {
            self.doc.two_point = Some(hardwood);
            self.doc.two_point_enabled = true;
            if !same(&before, &self.create_project()) {
                self.record(before);
                self.changed();
            }
        } else {
            self.doc.floor = Some(hardwood);
            self.record(before);
            self.changed();
        }
        self.recent.retain(|r| *r != id);
        self.recent.insert(0, id);
        self.recent.truncate(20);
        Ok(())
    }

    /// Startup selection of the default court (no history entry, like the WPF initial load).
    pub fn initialize_floor(&mut self, floor: Floor, assets: &mut dyn Assets) -> Result<()> {
        let settings = Settings::read(&floor.source)?;
        let revision = assets.floor(&floor.path, floor.source.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData"))?;
        let id = floor.id.clone();
        self.doc.floor = Some(Hardwood { floor, revision: Some(revision), settings });
        self.recent.retain(|r| *r != id);
        self.recent.insert(0, id);
        self.recent.truncate(20);
        self.version += 1;
        Ok(())
    }

    /// Live slider update inside a gesture (no history until [`Studio::commit_gesture`]).
    pub fn preview_hardwood_settings(&mut self, two_point: bool, settings: Settings) -> Result<()> {
        settings.validate()?;
        self.begin_gesture();
        let hardwood = if two_point { self.doc.two_point.as_mut() } else { self.doc.floor.as_mut() };
        let hardwood = hardwood.ok_or_else(|| Error::msg("Choose a hardwood before adjusting its texture."))?;
        hardwood.settings = settings;
        self.dirty = true;
        Ok(())
    }

    /// Single undoable settings change (reset or typed value).
    pub fn set_hardwood_settings(&mut self, two_point: bool, settings: Settings) -> Result<()> {
        settings.validate()?;
        if self.hardwood(two_point).is_none() {
            return Err(Error::msg("Choose a hardwood before adjusting its texture."));
        }
        self.change(|studio| {
            let hardwood = if two_point { studio.doc.two_point.as_mut() } else { studio.doc.floor.as_mut() };
            hardwood.unwrap().settings = settings;
            Ok(())
        })
    }

    // --- Logos ---

    pub fn logo_index(&self, id: &str) -> Result<usize> {
        self.doc.logos.iter().position(|l| l.id == id).ok_or_else(|| Error::msg("That logo is no longer on the court."))
    }

    /// Port of `TryAddLogoAsync` placement (fits 1310 x 820 at the court center, offset per slot).
    pub fn add_logo(&mut self, path: PathBuf, name: String, revision: String, size: (u32, u32), artwork: Map<String, Value>) -> Result<String> {
        if self.doc.logos.len() >= LOGO_SLOTS {
            return Err(Error::msg("Court export supports four logo slots. Delete a logo before importing another."));
        }
        let scale = (1310.0 / size.0 as f64).min(820.0 / size.1 as f64);
        let (width, height) = (size.0 as f64 * scale, size.1 as f64 * scale);
        let center = self.library.geometry.court_center();
        let offset = self.doc.logos.len() as f64 * 40.0;
        let id = uuid::Uuid::new_v4().simple().to_string();
        let logo = Logo {
            id: id.clone(), name, path, x: center[0] - width / 2.0 + offset, y: center[1] - height / 2.0 + offset,
            width: width.clamp(1.0, 32768.0), height: height.clamp(1.0, 32768.0), rotation: 0.0, opacity: 100.0, visible: true,
            scale_locked: true, flip_x: false, flip_y: false, revision: Some(revision), artwork, pixel_width: size.0, pixel_height: size.1,
        };
        self.change(|studio| {
            studio.doc.logos.push(logo);
            Ok(())
        })?;
        Ok(id)
    }

    /// Commits a completed move/resize/rotate gesture or typed placement as one undo step.
    pub fn set_logo_transform(&mut self, id: &str, x: f64, y: f64, width: f64, height: f64, rotation: f64) -> Result<()> {
        if ![x, y, width, height, rotation].iter().all(|v| v.is_finite()) || x.abs() > 1_000_000.0 || y.abs() > 1_000_000.0 {
            return Err(Error::msg("Logo placement must use finite numbers within the supported range."));
        }
        let index = self.logo_index(id)?;
        self.change(|studio| {
            let logo = &mut studio.doc.logos[index];
            logo.x = x;
            logo.y = y;
            logo.set_size(width, height);
            logo.set_rotation(rotation);
            Ok(())
        })
    }

    pub fn update_logo(&mut self, id: &str, edit: impl FnOnce(&mut Logo)) -> Result<()> {
        let index = self.logo_index(id)?;
        self.change(|studio| {
            edit(&mut studio.doc.logos[index]);
            Ok(())
        })
    }

    pub fn rename_logo(&mut self, id: &str, name: &str) -> Result<()> {
        let value = name.trim();
        if value.is_empty() || value.chars().count() > 120 || value.chars().any(char::is_control) {
            return Err(Error::msg("Enter a logo name of 1–120 characters."));
        }
        let value = value.to_string();
        self.update_logo(id, |logo| logo.name = value)
    }

    /// Live opacity preview inside a gesture.
    pub fn preview_logo_opacity(&mut self, id: &str, opacity: f64) -> Result<()> {
        let index = self.logo_index(id)?;
        self.begin_gesture();
        self.doc.logos[index].opacity = if opacity.is_finite() { opacity.clamp(0.0, 100.0) } else { 100.0 };
        Ok(())
    }

    pub fn delete_logo(&mut self, id: &str) -> Result<Option<String>> {
        let index = self.logo_index(id)?;
        self.change(|studio| {
            studio.doc.logos.remove(index);
            Ok(())
        })?;
        Ok(self.doc.logos.last().map(|l| l.id.clone()))
    }

    /// Copy (+40 px) or mirror-position copy across the court center (`DuplicateLogo`).
    pub fn duplicate_logo(&mut self, id: &str, mirror: Option<char>) -> Result<String> {
        let index = self.logo_index(id)?;
        if self.doc.logos.len() >= LOGO_SLOTS {
            return Err(Error::msg("Court export supports four logo slots."));
        }
        let center = self.library.geometry.court_center();
        let source = self.doc.logos[index].clone();
        let mut copy = source.clone();
        copy.id = uuid::Uuid::new_v4().simple().to_string();
        copy.name = format!("{} Copy", source.name);
        match mirror {
            None => {
                copy.x = source.x + 40.0;
                copy.y = source.y + 40.0;
            }
            Some('x') => copy.x = center[0] * 2.0 - source.x - source.width,
            Some(_) => copy.y = center[1] * 2.0 - source.y - source.height,
        }
        let new_id = copy.id.clone();
        self.change(|studio| {
            studio.doc.logos.push(copy);
            Ok(())
        })?;
        Ok(new_id)
    }

    /// Moves a layer within the bottom-first list (-1 toward the back, +1 toward the front).
    pub fn reorder_logo(&mut self, id: &str, direction: i32) -> Result<()> {
        let index = self.logo_index(id)?;
        let target = index as i64 + direction as i64;
        if target < 0 || target >= self.doc.logos.len() as i64 {
            return Ok(());
        }
        self.change(|studio| {
            let logo = studio.doc.logos.remove(index);
            studio.doc.logos.insert(target as usize, logo);
            Ok(())
        })
    }

    pub fn rename_project(&mut self, name: &str) -> Result<()> {
        let name = name.trim();
        if name.is_empty() || name.chars().count() > 120 || name.chars().any(char::is_control) {
            return Err(Error::msg("Enter a project name of 1–120 characters."));
        }
        let name = name.to_string();
        self.change(|studio| {
            studio.doc.name = name;
            Ok(())
        })
    }

    // --- Projects and history ---

    fn read_saved_floor(&self, source: &Value, path: &Path, custom: bool, project_path: Option<&Path>, relative: bool) -> Result<Floor> {
        let mut restored = source.clone();
        restored["path"] = Value::String(path.to_string_lossy().into_owned());
        restored["previewPath"] = Value::String(path.to_string_lossy().into_owned());
        for key in ["artworkProjectPath", "artworkDdsPath"] {
            if let Some(value) = source.get(key).and_then(Value::as_str).filter(|v| !v.is_empty()) {
                restored[key] = Value::String(self.library.resolve_path(value, project_path, relative).to_string_lossy().into_owned());
            }
        }
        if custom {
            restored["category"] = Value::String("Custom".into());
        }
        if restored.get("id").is_none_or(Value::is_null) {
            restored["id"] = Value::String(format!("custom_floor_{}", uuid::Uuid::new_v4().simple()));
        }
        if restored.get("name").is_none_or(Value::is_null) {
            restored["name"] = Value::String(path.file_stem().unwrap_or_default().to_string_lossy().into_owned());
        }
        catalog::read_floor(&restored, &self.library.application_root, if custom { None } else { Some(&self.library.floors) })
    }

    fn artwork_reference(&self, item: &Value, project_path: Option<&Path>, relative: bool) -> Map<String, Value> {
        let mut artwork = Map::new();
        for key in ["artworkProjectPath", "artworkDdsPath"] {
            if let Some(value) = item.get(key).and_then(Value::as_str).filter(|v| !v.is_empty()) {
                artwork.insert(key.into(), Value::String(self.library.resolve_path(value, project_path, relative).to_string_lossy().into_owned()));
            }
        }
        for key in ["artworkProjectRevision", "artworkDdsRevision", "artworkAlphaMode", "textSettings"] {
            if let Some(value) = item.get(key).filter(|v| !v.is_null()) {
                artwork.insert(key.into(), value.clone());
            }
        }
        artwork
    }

    /// Port of `RestoreProjectFromAsync`. Assets are prepared before the document is replaced, so a
    /// failure leaves the open court and its history unchanged.
    pub fn restore(&mut self, project: &Value, opened: bool, assets: &mut dyn Assets) -> Result<RestoreReport> {
        crate::project::validate(project)?;
        self.gesture = None;
        let mut report = RestoreReport::default();
        let project_path = project.get("_projectPath").and_then(Value::as_str).filter(|p| !p.trim().is_empty()).map(PathBuf::from);
        let relative = project.get("assetPathMode").and_then(Value::as_str) == Some("project-relative");
        let resolve = |studio: &Self, value: &str| studio.library.resolve_path(value, project_path.as_deref(), relative);
        let number = |item: &Value, key: &str, default: f64| item.get(key).and_then(Value::as_f64).filter(|v| v.is_finite()).unwrap_or(default);
        let flag = |item: &Value, key: &str, default: bool| item.get(key).and_then(Value::as_bool).unwrap_or(default);

        let mut logos = Vec::new();
        for item in project.get("logoImages").and_then(Value::as_array).into_iter().flatten() {
            let path = resolve(self, item.get("path").and_then(Value::as_str).unwrap_or(""));
            let artwork = self.artwork_reference(item, project_path.as_deref(), relative);
            let mut logo = Logo {
                id: item.get("id").and_then(Value::as_str).map(str::to_string).unwrap_or_else(|| uuid::Uuid::new_v4().simple().to_string()),
                name: item.get("name").and_then(Value::as_str).unwrap_or("Logo").to_string(),
                path: path.clone(),
                x: number(item, "x", 0.0),
                y: number(item, "y", 0.0),
                width: number(item, "width", 400.0).clamp(1.0, 32768.0),
                height: number(item, "height", 400.0).clamp(1.0, 32768.0),
                rotation: number(item, "rotation", 0.0) % 360.0,
                opacity: number(item, "opacity", 100.0).clamp(0.0, 100.0),
                visible: flag(item, "visible", true),
                scale_locked: flag(item, "scaleLocked", true),
                flip_x: flag(item, "flipX", false),
                flip_y: flag(item, "flipY", false),
                revision: None,
                artwork,
                pixel_width: 0,
                pixel_height: 0,
            };
            match assets.logo(&path, logo.game_data()) {
                Ok((owned, revision, width, height)) => {
                    if item.get("sourceRevision").and_then(Value::as_str).is_some_and(|saved| saved != revision) {
                        report.updated.push(logo.name.clone());
                    }
                    logo.path = owned;
                    logo.revision = Some(revision);
                    logo.pixel_width = width;
                    logo.pixel_height = height;
                }
                Err(error) if error.is_cancelled() => return Err(error),
                Err(_) => report.missing.push(logo.name.clone()),
            }
            logos.push(logo);
        }

        let visibility = project.get("visibility").and_then(Value::as_object);
        let floor_id = project
            .get("floor")
            .and_then(|f| f.get("id"))
            .and_then(Value::as_str)
            .map(str::to_string)
            .or_else(|| self.library.floors.iter().find(|f| visibility.and_then(|v| v.get(&f.id)).and_then(Value::as_bool) == Some(true)).map(|f| f.id.clone()))
            .or_else(|| self.library.default_floor.clone());
        let mut customs = Vec::new();
        for item in project.get("customFloorImages").and_then(Value::as_array).into_iter().flatten() {
            let Some(saved) = item.get("path").and_then(Value::as_str).filter(|p| !p.is_empty()) else { continue };
            let path = resolve(self, saved);
            let custom = self.read_saved_floor(item, &path, true, project_path.as_deref(), relative)?;
            if !path.exists() {
                report.missing.push(custom.name.clone());
            }
            customs.push(custom);
        }
        let mut selected: Option<Floor> = floor_id.as_ref().and_then(|id| customs.iter().find(|f| &f.id == id).or_else(|| self.library.floor(id)).cloned());
        if let Some(saved) = project.get("floor").filter(|f| f.get("path").and_then(Value::as_str).is_some()) {
            let path = resolve(self, saved["path"].as_str().unwrap());
            let matches = selected.as_ref().is_some_and(|f| crate::files::same_path(&f.path, &path));
            let exists = path.is_file();
            if exists && (relative || !matches) {
                selected = Some(self.read_saved_floor(saved, &path, false, project_path.as_deref(), relative)?);
            } else if exists && selected.is_some() {
                selected.as_mut().unwrap().source = saved.clone();
            } else if !exists && selected.is_some() && (relative || !matches) {
                report.missing.push(format!("{} hardwood (using the local library copy)", if relative { "bundled" } else { "saved" }));
            }
        }
        if selected.is_none() && floor_id != self.library.default_floor {
            report.missing.push("hardwood (using the default court)".into());
        }
        if let (Some(floor), Some(settings)) = (selected.as_mut(), project.get("floor").and_then(|f| f.get("textureSettings"))) {
            floor.source["textureSettings"] = settings.clone();
        }
        let main = selected
            .or_else(|| self.library.default_floor.as_ref().and_then(|id| self.library.floor(id)).cloned())
            .ok_or_else(|| Error::msg("No hardwood textures were found in the local library."))?;
        let main_settings = Settings::read(&main.source)?;
        let main_revision = assets.floor(&main.path, main.source.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData"))?;
        if project.get("floor").and_then(|f| f.get("sourceRevision")).and_then(Value::as_str).is_some_and(|r| r != main_revision) {
            report.updated.push("hardwood".into());
        }
        let mut two_point = None;
        if let Some(saved) = project.get("twoPointFloor").filter(|v| v.is_object()) {
            let saved_id = saved.get("id").and_then(Value::as_str).unwrap_or("");
            let path = resolve(self, saved.get("path").and_then(Value::as_str).unwrap_or(""));
            let secondary = if path.is_file() {
                Some(self.read_saved_floor(saved, &path, false, project_path.as_deref(), relative)?)
            } else {
                customs.iter().chain(&self.library.floors).find(|f| f.id == saved_id && f.path.is_file()).cloned()
            };
            match secondary {
                None => report.missing.push("two-point hardwood (using main hardwood)".into()),
                Some(mut floor) => {
                    if let Some(settings) = saved.get("textureSettings") {
                        floor.source["textureSettings"] = settings.clone();
                    }
                    let settings = Settings::read(&floor.source)?;
                    let revision = assets.floor(&floor.path, floor.source.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData"))?;
                    if saved.get("sourceRevision").and_then(Value::as_str).is_some_and(|r| r != revision) {
                        report.updated.push("two-point hardwood".into());
                    }
                    two_point = Some(Hardwood { floor, revision: Some(revision), settings });
                }
            }
        }

        // Everything prepared: replace the document.
        let mut doc = self.library.default_document();
        let paint_settings = project.get("paintSettings").and_then(Value::as_object);
        let line_settings = project.get("lineSettings").and_then(Value::as_object);
        let colors = project.get("colorOverrides").and_then(Value::as_object);
        let names = project.get("layerNames").and_then(Value::as_object);
        for layer in doc.paints.iter_mut().chain(doc.lines.iter_mut()).chain(std::iter::once(&mut doc.outside)) {
            let settings = paint_settings.and_then(|p| p.get(&layer.id)).or_else(|| line_settings.and_then(|l| l.get(&layer.id)));
            layer.visible = settings.and_then(|s| s.get("visible")).and_then(Value::as_bool).or_else(|| visibility.and_then(|v| v.get(&layer.id)).and_then(Value::as_bool)).unwrap_or(layer.default_visible);
            let rgb = colors.and_then(|c| c.get(&layer.id)).and_then(Value::as_array).filter(|a| a.len() >= 3);
            layer.color = settings
                .and_then(|s| s.get("color"))
                .and_then(Value::as_str)
                .and_then(normalize_hex)
                .or_else(|| rgb.map(|a| format!("#{:02X}{:02X}{:02X}", a[0].as_f64().unwrap_or(0.0) as u8, a[1].as_f64().unwrap_or(0.0) as u8, a[2].as_f64().unwrap_or(0.0) as u8)))
                .unwrap_or(layer.default_color.clone());
            if let Some(name) = names.and_then(|n| n.get(&layer.id)).and_then(Value::as_str) {
                layer.name = name.to_string();
            }
        }
        if project.get("version").and_then(Value::as_f64) == Some(1.0) {
            restore_legacy_colors(&mut doc, project);
        }
        if let Some(color) = project.get("outsideColor").and_then(Value::as_str).and_then(normalize_hex) {
            doc.outside.color = color;
        }
        if let Some(visible) = project.get("outsideVisible").and_then(Value::as_bool) {
            doc.outside.visible = visible;
        }
        doc.logos = logos;
        for custom in customs {
            match self.library.floors.iter().position(|f| f.id == custom.id && f.category == "Custom") {
                Some(index) => self.library.floors[index] = custom,
                None if !self.library.floors.iter().any(|f| f.id == custom.id) => self.library.floors.push(custom),
                None => {}
            }
        }
        if !self.library.floors.iter().any(|f| f.id == main.id) {
            self.library.floors.push(main.clone());
        }
        doc.path = project_path.clone();
        doc.two_point_enabled = project.get("twoPointHardwoodEnabled").and_then(Value::as_bool).unwrap_or(two_point.is_some());
        doc.floor = Some(Hardwood { floor: main, revision: Some(main_revision), settings: main_settings });
        doc.two_point = two_point;
        doc.name = project
            .get("projectName")
            .and_then(Value::as_str)
            .filter(|n| !n.trim().is_empty())
            .map(str::to_string)
            .or_else(|| project_path.as_ref().map(|p| p.file_stem().unwrap_or_default().to_string_lossy().into_owned()))
            .unwrap_or_else(|| "Untitled court".into());
        if let Some(floor) = &doc.floor {
            let id = floor.floor.id.clone();
            self.recent.retain(|r| *r != id);
            self.recent.insert(0, id);
            self.recent.truncate(20);
        }
        self.doc = doc;
        self.dirty = !report.updated.is_empty();
        self.version += 1;
        if opened {
            self.undo.clear();
            self.redo.clear();
        }
        Ok(report)
    }

    /// Port of `NewProjectAsync` (one undoable reset to stock defaults and the default court).
    pub fn new_project(&mut self, assets: &mut dyn Assets) -> Result<()> {
        self.commit_gesture();
        let before = self.create_project();
        let mut reset = before.clone();
        for layer in self.doc.paints.iter().chain(&self.doc.lines) {
            let key = if self.doc.paints.iter().any(|p| p.id == layer.id) { "paintSettings" } else { "lineSettings" };
            reset[key][&layer.id] = json!({"visible": layer.default_visible, "color": layer.default_color});
        }
        reset["outsideColor"] = Value::String(self.doc.outside.default_color.clone());
        reset["outsideVisible"] = Value::Bool(self.doc.outside.default_visible);
        let default = self.library.default_floor.as_ref().and_then(|id| self.library.floor(id)).ok_or_else(|| Error::msg("No hardwood textures were found in the local library."))?;
        reset["floor"] = default.source.clone();
        reset["twoPointFloor"] = Value::Null;
        reset["twoPointHardwoodEnabled"] = Value::Bool(false);
        reset["logoImages"] = json!([]);
        reset["_projectPath"] = Value::Null;
        reset["projectName"] = Value::String("Untitled court".into());
        self.restore(&reset, false, assets)?;
        self.record(before);
        self.changed();
        Ok(())
    }

    pub fn undo(&mut self, redo: bool, assets: &mut dyn Assets) -> Result<bool> {
        self.commit_gesture();
        let target = if redo { self.redo.last().cloned() } else { self.undo.last().cloned() };
        let Some(target) = target else { return Ok(false) };
        let before = self.create_project();
        self.restore(&target, false, assets)?;
        if redo {
            self.redo.pop();
            self.undo.push(before);
        } else {
            self.undo.pop();
            self.redo.push(before);
        }
        self.dirty = true;
        self.version += 1;
        Ok(true)
    }
}

/// Port of `RestoreLegacyColors` for version-1 template projects.
fn restore_legacy_colors(doc: &mut Document, project: &Value) {
    let aliases: [(&str, &[&str]); 12] = [
        ("paint", &["paint-left", "paint-right"]),
        ("secondary paint color", &["secondary-paint-left", "secondary-paint-right"]),
        ("outside color", &["stock-outside"]),
        ("nba three", &["NBA_line_three_point_lowShape"]),
        ("3 point lines", &["NBA_line_three_point_lowShape"]),
        ("college three", &["college-three"]),
        ("high school three", &["high-school-three"]),
        ("center line", &["line_midcourt_side_lowShape", "line_midcourt_center_lowShape"]),
        ("out of bound line", &["line_side_base_lowShape"]),
        ("half court circle", &["line_center_circle_outer_lowShape"]),
        ("charge circle", &["line_charge_circle_lowShape"]),
        ("media lines", &["line_camera_lowShape"]),
    ];
    let Some(names) = project.get("layerNames").and_then(Value::as_object) else { return };
    for (key, value) in names {
        let name = value.as_str().unwrap_or("").trim().to_lowercase();
        let Some((_, ids)) = aliases.iter().find(|(alias, _)| *alias == name) else { continue };
        for id in *ids {
            let layer = if doc.outside.id == *id { Some(&mut doc.outside) } else { doc.paints.iter_mut().chain(doc.lines.iter_mut()).find(|l| l.id == *id) };
            let Some(layer) = layer else { continue };
            if let Some(visible) = project.get("visibility").and_then(|v| v.get(key)).and_then(Value::as_bool) {
                layer.visible = visible;
            }
            if let Some(rgb) = project.get("colorOverrides").and_then(|c| c.get(key)).and_then(Value::as_array).filter(|a| a.len() >= 3) {
                layer.color = format!("#{:02X}{:02X}{:02X}", rgb[0].as_f64().unwrap_or(0.0) as u8, rgb[1].as_f64().unwrap_or(0.0) as u8, rgb[2].as_f64().unwrap_or(0.0) as u8);
            }
        }
    }
}
