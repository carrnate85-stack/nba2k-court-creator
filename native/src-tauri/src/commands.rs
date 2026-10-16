//! Typed commands used by the React interface. Rust owns the court document, history, artwork
//! and every file operation; the UI only renders views and temporary gesture previews.
use crate::state::{AssetLoader, Preferences, Session, own_logo};
use crate::{App, worker};
use court_core::cleanup::Cleanup;
use court_core::document::{Logo, Studio};
use court_core::error::{Error, Result};
use court_core::hardwood::{self, Settings};
use court_core::raster::{self, Rgba};
use court_core::{files, geom, project, render};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tauri::{Emitter, Manager};

pub type CommandResult<T> = std::result::Result<T, String>;

fn err(error: impl std::fmt::Display) -> String {
    error.to_string()
}

// --- Views ---

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HardwoodView {
    id: String,
    name: String,
    category: String,
    path: String,
    preview_path: String,
    settings: Settings,
    revision: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LogoView {
    #[serde(flatten)]
    logo: Logo,
    is_text: bool,
    text_settings: Option<Value>,
    missing: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DocView {
    version: u64,
    name: String,
    path: Option<String>,
    dirty: bool,
    can_undo: bool,
    can_redo: bool,
    paints: Vec<court_core::document::Layer>,
    lines: Vec<court_core::document::Layer>,
    outside: court_core::document::Layer,
    floor: Option<HardwoodView>,
    two_point: Option<HardwoodView>,
    two_point_enabled: bool,
    logos: Vec<LogoView>,
    boundary: Option<String>,
    recent: Vec<String>,
}

fn hardwood_view(hardwood: &court_core::document::Hardwood) -> HardwoodView {
    HardwoodView {
        id: hardwood.floor.id.clone(),
        name: hardwood.floor.name.clone(),
        category: hardwood.floor.category.clone(),
        path: hardwood.floor.path.to_string_lossy().into_owned(),
        preview_path: hardwood.floor.preview_path.to_string_lossy().into_owned(),
        settings: hardwood.settings,
        revision: hardwood.revision.clone(),
    }
}

pub fn doc_view(studio: &Studio) -> DocView {
    let doc = &studio.doc;
    DocView {
        version: studio.version,
        name: doc.name.clone(),
        path: doc.path.as_ref().map(|p| p.to_string_lossy().into_owned()),
        dirty: studio.dirty,
        can_undo: !studio.undo.is_empty() || studio.gesture.is_some(),
        can_redo: !studio.redo.is_empty(),
        paints: doc.paints.clone(),
        lines: doc.lines.clone(),
        outside: doc.outside.clone(),
        floor: doc.floor.as_ref().map(hardwood_view),
        two_point: doc.two_point.as_ref().map(hardwood_view),
        two_point_enabled: doc.two_point_enabled,
        logos: doc
            .logos
            .iter()
            .map(|logo| LogoView { logo: logo.clone(), is_text: logo.is_text(), text_settings: logo.artwork.get("textSettings").cloned(), missing: logo.revision.is_none() })
            .collect(),
        boundary: studio.active_three_point().map(str::to_string),
        recent: studio.recent.clone(),
    }
}

fn round(polygons: &[geom::Polygon]) -> Vec<Vec<[f32; 2]>> {
    polygons.iter().map(|p| p.iter().map(|q| [q[0] as f32, q[1] as f32]).collect()).collect()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayerGeometry {
    id: String,
    polygons: Vec<Vec<[f32; 2]>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GeometryView {
    revision: String,
    lines: Vec<LayerGeometry>,
    paints: Vec<LayerGeometry>,
    surface: Vec<Vec<[f32; 2]>>,
    hardwood_bounds: [i64; 4],
    anchors: Vec<court_core::geometry::Anchor>,
}

pub fn geometry_view(geometry: &court_core::geometry::Geometry) -> GeometryView {
    GeometryView {
        revision: geometry.revision.clone(),
        lines: geometry.lines.iter().map(|l| LayerGeometry { id: l.id.clone(), polygons: round(&l.polygons) }).collect(),
        paints: geometry.paints.iter().map(|l| LayerGeometry { id: l.id.clone(), polygons: round(&l.polygons) }).collect(),
        surface: round(&geometry.surface),
        hardwood_bounds: geometry.hardwood_bounds,
        anchors: geometry.anchors.clone(),
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FloorView {
    id: String,
    name: String,
    category: String,
    preview_path: String,
}

pub fn floor_views(studio: &Studio) -> Vec<FloorView> {
    studio
        .library
        .floors
        .iter()
        .map(|f| FloorView { id: f.id.clone(), name: f.name.clone(), category: f.category.clone(), preview_path: f.preview_path.to_string_lossy().into_owned() })
        .collect()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Reply {
    doc: DocView,
    #[serde(skip_serializing_if = "Option::is_none")]
    status: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    selected: Option<String>,
}

pub fn reply(studio: &Studio, status: Option<String>, selected: Option<String>) -> Reply {
    Reply { doc: doc_view(studio), status, selected }
}

/// Runs a document edit on the worker pool, then queues recovery when the court changed.
async fn edit<T: Send + 'static>(app: &tauri::State<'_, App>, job: impl FnOnce(&mut Session) -> Result<T> + Send + 'static) -> CommandResult<T> {
    let session = app.session.clone();
    let recovery = app.recovery.clone();
    worker(move || {
        let mut session = session.lock().map_err(err)?;
        if let Some(busy) = &session.busy {
            return Err(format!("{busy} Wait for it to finish before editing."));
        }
        let before = session.studio.as_ref().map(|s| s.version);
        let result = job(&mut session).map_err(err)?;
        if let Some(studio) = session.studio.as_ref() {
            if Some(studio.version) != before && studio.gesture.is_none() {
                recovery.queue(studio.create_project());
            }
        }
        Ok(result)
    })
    .await
}

// --- Layers, paint and hardwood ---

#[tauri::command]
pub async fn set_layer(app: tauri::State<'_, App>, id: String, visible: Option<bool>, color: Option<String>) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        studio.set_layer(&id, visible, color.as_deref())?;
        Ok(reply(studio, None, None))
    })
    .await
}

#[tauri::command]
pub async fn fill_region(app: tauri::State<'_, App>, x: f64, y: f64) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let color = s.prefs.primary_color.clone();
        let boundary = s.studio()?.active_three_point().map(str::to_string);
        let regions = s.regions(boundary.as_deref()).ok_or_else(|| Error::msg("The court workspace is still loading."))?;
        let studio = s.studio()?;
        let filled = studio.fill_region([x, y], &color, &regions)?;
        let status = filled.as_ref().map(|id| {
            let name = studio.doc.paints.iter().chain(std::iter::once(&studio.doc.outside)).find(|l| &l.id == id).map(|l| l.name.clone()).unwrap_or_default();
            format!("{name} filled with primary color {color}.")
        });
        Ok(reply(studio, status, filled))
    })
    .await
}

#[tauri::command]
pub async fn regions(app: tauri::State<'_, App>, boundary: Option<String>) -> CommandResult<Value> {
    let session = app.session.clone();
    worker(move || {
        let mut session = session.lock().map_err(err)?;
        let regions = session.regions(boundary.as_deref()).ok_or("The court workspace is still loading.")?;
        let paints: Map<String, Value> = regions.paints.iter().map(|(id, p)| (id.clone(), serde_json::to_value(round(p)).unwrap())).collect();
        Ok(json!({"boundary": regions.boundary, "enclosures": round(&regions.enclosures), "paints": paints}))
    })
    .await
}

#[tauri::command]
pub async fn set_two_point_enabled(app: tauri::State<'_, App>, enabled: bool) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        studio.set_two_point_enabled(enabled)?;
        Ok(reply(studio, None, None))
    })
    .await
}

#[tauri::command]
pub async fn select_floor(app: tauri::State<'_, App>, id: String, two_point: bool) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let Session { studio, cache, prefs, .. } = s;
        let studio = studio.as_mut().ok_or_else(|| Error::msg("The court workspace is still loading."))?;
        let floor = studio.library.floor(&id).cloned().ok_or_else(|| Error::msg("That hardwood is no longer in the catalog."))?;
        studio.select_floor(floor, two_point, &mut AssetLoader { cache })?;
        prefs.recent = studio.recent.clone();
        Ok(reply(studio, None, None))
    })
    .await
    .inspect(|_| app.save_preferences())
}

/// `phase`: "preview" (live slider), "commit" (end of a gesture) or "set" (single undo step).
#[tauri::command]
pub async fn hardwood_settings(app: tauri::State<'_, App>, two_point: bool, settings: Settings, phase: String) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        match phase.as_str() {
            "preview" => studio.preview_hardwood_settings(two_point, settings)?,
            "commit" => {
                studio.preview_hardwood_settings(two_point, settings)?;
                studio.commit_gesture();
            }
            _ => studio.set_hardwood_settings(two_point, settings)?,
        }
        Ok(reply(studio, None, None))
    })
    .await
}

fn pixels_response(image: &Rgba) -> tauri::ipc::Response {
    let mut bytes = Vec::with_capacity(8 + image.data.len());
    bytes.extend_from_slice(&image.width.to_le_bytes());
    bytes.extend_from_slice(&image.height.to_le_bytes());
    bytes.extend_from_slice(&image.data);
    tauri::ipc::Response::new(bytes)
}

/// Preview hardwood (fitted to the hardwood bounds at [`PREVIEW_SCALE`], adjusted, placed).
#[tauri::command]
pub async fn hardwood_preview(app: tauri::State<'_, App>, two_point: bool, settings: Option<Settings>) -> CommandResult<tauri::ipc::Response> {
    let session = app.session.clone();
    worker(move || {
        let (decoded, game_data, settings, bounds) = {
            let mut session = session.lock().map_err(err)?;
            let Session { studio, cache, .. } = &mut *session;
            let studio = studio.as_ref().ok_or("The court workspace is still loading.")?;
            let hardwood = studio.hardwood(two_point).ok_or("Choose a hardwood first.")?;
            let game_data = hardwood.floor.source.get("artworkAlphaMode").and_then(Value::as_str) == Some("GameData");
            let decoded = cache.decoded_floor(&hardwood.floor.path, game_data).map_err(err)?;
            (decoded, game_data, settings.unwrap_or(hardwood.settings), studio.library.geometry.hardwood_bounds)
        };
        settings.validate().map_err(err)?;
        let width = ((bounds[2] as f64) * crate::state::PREVIEW_SCALE).round() as u32;
        let height = ((bounds[3] as f64) * crate::state::PREVIEW_SCALE).round() as u32;
        let fitted = {
            let found = session.lock().map_err(err)?.cache.fitted_cached(&decoded.revision, game_data, width, height);
            match found {
                Some(fitted) => fitted,
                None => {
                    let fitted = Arc::new(raster::fit(&decoded.image, width, height, raster::Filter::Lanczos));
                    session.lock().map_err(err)?.cache.store_fitted(&decoded.revision, game_data, width, height, fitted.clone());
                    fitted
                }
            }
        };
        let mut image = (*fitted).clone();
        hardwood::adjust(&mut image, &settings);
        let placed = hardwood::place(&image, &settings);
        Ok(pixels_response(&placed))
    })
    .await
}

#[tauri::command]
pub async fn logo_pixels(app: tauri::State<'_, App>, revision: String) -> CommandResult<tauri::ipc::Response> {
    let session = app.session.clone();
    worker(move || {
        let image = session.lock().map_err(err)?.cache.logo_preview(&revision).ok_or("That logo preview is no longer available.")?;
        Ok(pixels_response(&image))
    })
    .await
}

#[tauri::command]
pub async fn floor_thumbnail(path: String) -> CommandResult<tauri::ipc::Response> {
    worker(move || {
        let bytes = files::read_bounded(Path::new(&path), 64 * 1024 * 1024).map_err(err)?;
        let image = raster::decode(&bytes, Path::new(&path).extension().and_then(|e| e.to_str())).map_err(err)?;
        Ok(pixels_response(&raster::preview(&image, 360)))
    })
    .await
}

// --- Logos ---

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogoPatch {
    name: Option<String>,
    visible: Option<bool>,
    scale_locked: Option<bool>,
    flip_x: Option<bool>,
    flip_y: Option<bool>,
}

#[tauri::command]
pub async fn logo_transform(app: tauri::State<'_, App>, id: String, x: f64, y: f64, width: f64, height: f64, rotation: f64) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        studio.set_logo_transform(&id, x, y, width, height, rotation)?;
        Ok(reply(studio, None, Some(id)))
    })
    .await
}

#[tauri::command]
pub async fn logo_update(app: tauri::State<'_, App>, id: String, patch: LogoPatch) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        if let Some(name) = &patch.name {
            studio.rename_logo(&id, name)?;
        }
        studio.update_logo(&id, |logo| {
            if let Some(v) = patch.visible { logo.visible = v; }
            if let Some(v) = patch.scale_locked { logo.scale_locked = v; }
            if let Some(v) = patch.flip_x { logo.flip_x = v; }
            if let Some(v) = patch.flip_y { logo.flip_y = v; }
        })?;
        Ok(reply(studio, None, Some(id)))
    })
    .await
}

#[tauri::command]
pub async fn logo_opacity(app: tauri::State<'_, App>, id: String, opacity: f64, commit: bool) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        studio.preview_logo_opacity(&id, opacity)?;
        if commit {
            studio.commit_gesture();
        }
        Ok(reply(studio, None, Some(id)))
    })
    .await
}

#[tauri::command]
pub async fn logo_delete(app: tauri::State<'_, App>, id: String) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        let next = studio.delete_logo(&id)?;
        Ok(reply(studio, None, next))
    })
    .await
}

#[tauri::command]
pub async fn logo_duplicate(app: tauri::State<'_, App>, id: String, mirror: Option<String>) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        let copy = studio.duplicate_logo(&id, mirror.and_then(|m| m.chars().next()))?;
        Ok(reply(studio, None, Some(copy)))
    })
    .await
}

#[tauri::command]
pub async fn logo_reorder(app: tauri::State<'_, App>, id: String, direction: i32) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        studio.reorder_logo(&id, direction)?;
        Ok(reply(studio, None, Some(id)))
    })
    .await
}

#[tauri::command]
pub async fn rename_project(app: tauri::State<'_, App>, name: String) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let studio = s.studio()?;
        studio.rename_project(&name)?;
        Ok(reply(studio, None, None))
    })
    .await
}

#[tauri::command]
pub async fn undo(app: tauri::State<'_, App>, redo: bool) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let Session { studio, cache, .. } = s;
        let studio = studio.as_mut().ok_or_else(|| Error::msg("The court workspace is still loading."))?;
        let applied = studio.undo(redo, &mut AssetLoader { cache })?;
        Ok(reply(studio, applied.then(|| if redo { "Redo applied.".to_string() } else { "Undo applied.".to_string() }), None))
    })
    .await
}

#[tauri::command]
pub async fn document(app: tauri::State<'_, App>) -> CommandResult<Reply> {
    let session = app.session.clone();
    worker(move || {
        let mut session = session.lock().map_err(err)?;
        let studio = session.studio().map_err(err)?;
        Ok(reply(studio, None, None))
    })
    .await
}

// --- Logo importer (local cleanup before placement) ---

pub struct Importer {
    source: PathBuf,
    bytes: Vec<u8>,
    revision: String,
    cleanup: Cleanup,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImporterInfo {
    session: u64,
    name: String,
    width: u32,
    height: u32,
    background: String,
    can_undo: bool,
    has_changes: bool,
    removed: usize,
}

fn importer_info(id: u64, importer: &Importer, removed: usize, background: Option<[u8; 4]>) -> ImporterInfo {
    let color = background.unwrap_or_else(|| {
        let p = importer.cleanup.pixel(0, 0);
        if p[3] == 0 { [255, 255, 255, 255] } else { p }
    });
    ImporterInfo {
        session: id,
        name: importer.source.file_name().unwrap_or_default().to_string_lossy().into_owned(),
        width: importer.cleanup.image.width,
        height: importer.cleanup.image.height,
        background: format!("#{:02X}{:02X}{:02X}", color[0], color[1], color[2]),
        can_undo: importer.cleanup.can_undo(),
        has_changes: importer.cleanup.has_changes(),
        removed,
    }
}

#[tauri::command]
pub async fn importer_open(app: tauri::State<'_, App>, window: tauri::WebviewWindow) -> CommandResult<Option<ImporterInfo>> {
    let session = app.session.clone();
    worker(move || {
        let Some(path) = rfd::FileDialog::new()
            .set_parent(&window)
            .add_filter("Logo images", &["dds", "png", "jpg", "jpeg", "bmp", "tga", "gif", "tif", "tiff", "webp"])
            .pick_file()
        else {
            return Ok(None);
        };
        let bytes = files::read_bounded(&path, files::MAX_ASSET_BYTES).map_err(err)?;
        let image = raster::decode(&bytes, path.extension().and_then(|e| e.to_str())).map_err(err)?;
        let cleanup = Cleanup::new(image).map_err(err)?;
        let revision = files::sha256_hex(&bytes);
        let importer = Importer { source: path, bytes, revision, cleanup };
        let mut session = session.lock().map_err(err)?;
        session.next_importer += 1;
        let id = session.next_importer;
        let info = importer_info(id, &importer, 0, None);
        session.importers.clear();
        session.importers.insert(id, importer);
        Ok(Some(info))
    })
    .await
}

#[tauri::command]
pub async fn importer_pixels(app: tauri::State<'_, App>, session: u64) -> CommandResult<tauri::ipc::Response> {
    let state = app.session.clone();
    worker(move || {
        let state = state.lock().map_err(err)?;
        let importer = state.importers.get(&session).ok_or("The logo importer was closed.")?;
        Ok(pixels_response(&importer.cleanup.image))
    })
    .await
}

/// `action`: "sample", "wand", "background", "undo" or "reset".
#[tauri::command]
pub async fn importer_action(app: tauri::State<'_, App>, session: u64, action: String, x: Option<i64>, y: Option<i64>, tolerance: Option<u8>, all: Option<bool>, color: Option<String>) -> CommandResult<ImporterInfo> {
    let state = app.session.clone();
    worker(move || {
        let mut state = state.lock().map_err(err)?;
        let importer = state.importers.get_mut(&session).ok_or("The logo importer was closed.")?;
        let tolerance = tolerance.unwrap_or(20);
        let mut sampled = None;
        let removed = match action.as_str() {
            "sample" => {
                let p = importer.cleanup.pixel(x.unwrap_or(0), y.unwrap_or(0));
                sampled = Some(if p[3] == 0 { [255, 255, 255, 255] } else { p });
                0
            }
            "wand" => importer.cleanup.remove_region(x.unwrap_or(-1), y.unwrap_or(-1), tolerance, all.unwrap_or(false)),
            "background" => {
                let [r, g, b] = color.as_deref().and_then(render::parse_hex).unwrap_or([255, 255, 255]);
                sampled = Some([r, g, b, 255]);
                importer.cleanup.remove_background([r, g, b, 255], tolerance)
            }
            "undo" => {
                importer.cleanup.undo();
                0
            }
            "reset" => {
                importer.cleanup.reset();
                0
            }
            _ => return Err("Unknown cleanup action.".into()),
        };
        Ok(importer_info(session, importer, removed, sampled))
    })
    .await
}

/// Places the (optionally cleaned) logo: untouched sources are copied byte-for-byte; cleaned
/// pixels become a new owned PNG. The original file is never modified.
#[tauri::command]
pub async fn importer_place(app: tauri::State<'_, App>, session: u64) -> CommandResult<Reply> {
    let id = session;
    edit(&app, move |s| {
        let importer = s.importers.remove(&id).ok_or_else(|| Error::msg("The logo importer was closed."))?;
        if s.studio()?.doc.logos.len() >= court_core::document::LOGO_SLOTS {
            return Err(Error::msg("Court export supports four logo slots. Delete a logo before importing another."));
        }
        let name = importer.source.file_stem().unwrap_or_default().to_string_lossy().into_owned();
        let (path, revision, image) = if importer.cleanup.has_changes() {
            let png = raster::encode_png(&importer.cleanup.image, false)?;
            let revision = files::sha256_hex(&png);
            let path = crate::state::logos_directory().join(format!("{revision}.png"));
            if !path.is_file() {
                files::write_atomic(&path, &png, &[&importer.source])?;
            }
            (path, revision, importer.cleanup.image)
        } else {
            if files::file_revision(&importer.source)? != importer.revision {
                return Err(Error::msg("The logo changed after its preview. Choose the image again before placing it."));
            }
            let owned = own_logo(&importer.source, &importer.bytes, &importer.revision)?;
            (owned, importer.revision.clone(), importer.cleanup.image)
        };
        let size = (image.width, image.height);
        s.cache.logos.insert(revision.clone(), Arc::new(raster::preview(&image, crate::state::LOGO_PREVIEW_SIDE)));
        let studio = s.studio()?;
        let logo = studio.add_logo(path, name, revision, size, Map::new())?;
        Ok(reply(studio, Some("Logo placed. Use Move or Transform to position it.".into()), Some(logo)))
    })
    .await
}

#[tauri::command]
pub async fn importer_close(app: tauri::State<'_, App>, session: u64) -> CommandResult<()> {
    let state = app.session.clone();
    worker(move || {
        state.lock().map_err(err)?.importers.remove(&session);
        Ok(())
    })
    .await
}

// --- Text ---

// `text_fonts` is served by the shared Canvas text module (same DirectWrite family list).

#[tauri::command]
pub async fn court_text_preview(settings: Value) -> CommandResult<tauri::ipc::Response> {
    worker(move || {
        let settings = court_core::text::from_json(&settings).map_err(err)?;
        let image = court_core::text::render(&settings).map_err(err)?;
        Ok(pixels_response(&raster::preview(&image, 1600)))
    })
    .await
}

#[tauri::command]
pub async fn text_defaults(app: tauri::State<'_, App>) -> CommandResult<Value> {
    let primary = app.session.lock().map_err(err)?.prefs.primary_color.clone();
    Ok(court_core::text::to_json(&court_core::text::defaults(&primary)))
}

/// Port of `ApplyTextSettings`: renders a transparent PNG kept with its metadata; editing keeps
/// the layer's center and display scale.
#[tauri::command]
pub async fn text_apply(app: tauri::State<'_, App>, settings: Value, x: f64, y: f64, target: Option<String>) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let parsed = court_core::text::from_json(&settings)?;
        let image = court_core::text::render(&parsed)?;
        let png = raster::encode_png(&image, false)?;
        let revision = files::sha256_hex(&png);
        let path = crate::state::logos_directory().join(format!("text-{}.png", uuid::Uuid::new_v4().simple()));
        files::write_atomic(&path, &png, &[])?;
        s.cache.logos.insert(revision.clone(), Arc::new(raster::preview(&image, crate::state::LOGO_PREVIEW_SIDE)));
        let mut artwork = Map::new();
        artwork.insert("textSettings".into(), court_core::text::to_json(&parsed));
        let studio = s.studio()?;
        let result = match target {
            Some(id) => {
                let index = studio.logo_index(&id)?;
                let current = &studio.doc.logos[index];
                if current.artwork.get("textSettings") == artwork.get("textSettings") {
                    let _ = std::fs::remove_file(&path);
                    return Ok(reply(studio, None, Some(id)));
                }
                let factor_x = if current.pixel_width > 0 { current.width / current.pixel_width as f64 } else { 1.0 };
                let factor_y = if current.pixel_height > 0 { current.height / current.pixel_height as f64 } else { 1.0 };
                let width = (image.width as f64 * factor_x).max(1.0);
                let height = (image.height as f64 * factor_y).max(1.0);
                if width > 32768.0 || height > 32768.0 {
                    return Err(Error::msg("Text dimensions exceed the supported layer size."));
                }
                let center = current.center();
                studio.update_logo(&id, |logo| {
                    logo.path = path.clone();
                    logo.revision = Some(revision.clone());
                    logo.pixel_width = image.width;
                    logo.pixel_height = image.height;
                    logo.width = width;
                    logo.height = height;
                    logo.x = center[0] - width / 2.0;
                    logo.y = center[1] - height / 2.0;
                    logo.artwork = artwork.clone();
                })?;
                Ok::<_, Error>((id, "Text updated.".to_string()))
            }
            None => {
                if studio.doc.logos.len() >= court_core::document::LOGO_SLOTS {
                    return Err(Error::msg("Text uses a court artwork slot. Delete a layer before adding text; four slots are supported."));
                }
                let name: String = parsed.text.replace(['\r', '\n'], " ").trim().chars().take(80).collect();
                let id = studio.add_logo(path, name, revision, (image.width, image.height), artwork)?;
                let (width, height) = (image.width as f64, image.height as f64);
                let index = studio.logo_index(&id)?;
                // Place at the clicked point without another undo entry (part of the same insertion).
                let logo = &mut studio.doc.logos[index];
                logo.width = width.clamp(1.0, 32768.0);
                logo.height = height.clamp(1.0, 32768.0);
                logo.x = x - logo.width / 2.0;
                logo.y = y - logo.height / 2.0;
                Ok((id, "Text placed. Use Move or Transform to position it; use Text to edit it.".to_string()))
            }
        }?;
        Ok(reply(studio, Some(result.1), Some(result.0)))
    })
    .await
}

// --- Preferences and catalog ---

#[tauri::command]
pub async fn set_preferences(app: tauri::State<'_, App>, dark: Option<bool>, primary_color: Option<String>, favorite: Option<(String, bool)>, color_presets: Option<Value>) -> CommandResult<Preferences> {
    let prefs = {
        let mut session = app.session.lock().map_err(err)?;
        if let Some(dark) = dark {
            session.prefs.dark = dark;
        }
        if let Some(color) = primary_color {
            session.prefs.primary_color = render::normalize_hex(&color).ok_or("Enter a valid primary color.")?;
        }
        if let Some(presets) = color_presets {
            session.prefs.color_presets = Preferences::presets(&presets).ok_or("Color presets must be up to 128 valid hex colors.")?;
        }
        if let Some((id, add)) = favorite {
            session.prefs.favorites.retain(|f| *f != id);
            if add {
                session.prefs.favorites.push(id);
            }
        }
        session.prefs.clone()
    };
    app.save_preferences();
    Ok(prefs)
}

#[tauri::command]
pub async fn add_custom_floor(app: tauri::State<'_, App>, window: tauri::WebviewWindow) -> CommandResult<Option<Value>> {
    let session = app.session.clone();
    worker(move || {
        let Some(path) = rfd::FileDialog::new().set_parent(&window).add_filter("Floor image", &["png", "jpg", "jpeg", "bmp", "tif", "tiff"]).pick_file() else { return Ok(None) };
        let mut image = court_core::catalog::add_custom(&files::data_root(), &path).map_err(err)?;
        let mut session = session.lock().map_err(err)?;
        let studio = session.studio().map_err(err)?;
        image["bbox"] = json!(studio.library.geometry.hardwood_bounds);
        let floor = court_core::catalog::read_floor(&image, &files::data_root(), None).map_err(err)?;
        let id = floor.id.clone();
        studio.library.floors.push(floor);
        Ok(Some(json!({"id": id, "floors": floor_views(studio)})))
    })
    .await
}

// --- Projects ---

fn suggested(studio: &Studio) -> String {
    project::suggested_file_name(&studio.doc.name)
}

#[tauri::command]
pub async fn new_project(app: tauri::State<'_, App>) -> CommandResult<Reply> {
    edit(&app, move |s| {
        let Session { studio, cache, .. } = s;
        let studio = studio.as_mut().ok_or_else(|| Error::msg("The court workspace is still loading."))?;
        studio.new_project(&mut AssetLoader { cache })?;
        Ok(reply(studio, Some("New court ready.".into()), None))
    })
    .await
}

#[tauri::command]
pub async fn open_project(app: tauri::State<'_, App>, window: tauri::WebviewWindow) -> CommandResult<Option<Reply>> {
    let session = app.session.clone();
    let recovery = app.recovery.clone();
    worker(move || {
        let Some(path) = rfd::FileDialog::new().set_parent(&window).add_filter("Court project", &["json"]).pick_file() else { return Ok(None) };
        let mut value = project::read(&path).map_err(err)?;
        value["_projectPath"] = Value::String(files::normalized(&path).to_string_lossy().into_owned());
        let mut session = session.lock().map_err(err)?;
        let Session { studio, cache, .. } = &mut *session;
        let studio = studio.as_mut().ok_or("The court workspace is still loading.")?;
        let report = studio.restore(&value, true, &mut AssetLoader { cache }).map_err(|e| format!("Project could not be loaded: {e}"))?;
        recovery.queue(studio.create_project());
        Ok(Some(reply(studio, Some(report.status()), None)))
    })
    .await
}

/// Save / Save As: bundles artwork into `<name>.assets` beside the project (portable save).
#[tauri::command]
pub async fn save_project(app: tauri::State<'_, App>, window: tauri::WebviewWindow, save_as: bool) -> CommandResult<Option<Reply>> {
    let session = app.session.clone();
    let recovery = app.recovery.clone();
    worker(move || {
        let (snapshot, current, name) = {
            let mut session = session.lock().map_err(err)?;
            if let Some(busy) = &session.busy {
                return Err(format!("{busy} Wait for it to finish before saving."));
            }
            let studio = session.studio().map_err(err)?;
            studio.commit_gesture();
            (studio.create_project(), studio.doc.path.clone(), suggested(studio))
        };
        let path = match (save_as, current) {
            (false, Some(path)) => path,
            _ => {
                let Some(path) = rfd::FileDialog::new().set_parent(&window).set_file_name(&name).add_filter("Court project", &["json"]).save_file() else { return Ok(None) };
                path
            }
        };
        session.lock().map_err(err)?.busy = Some("Saving the project.".into());
        let saved = project::save_portable(&path, &snapshot, &files::data_root());
        let mut session = session.lock().map_err(err)?;
        session.busy = None;
        saved.map_err(|e| format!("Save failed: {e}"))?;
        let studio = session.studio().map_err(err)?;
        studio.doc.path = Some(files::normalized(&path));
        studio.dirty = false;
        recovery.queue(studio.create_project());
        Ok(Some(reply(studio, Some("Project saved.".into()), None)))
    })
    .await
}

// --- Exports ---

fn export_snapshot(session: &mut Session) -> Result<(Value, Arc<court_core::geometry::Geometry>)> {
    if let Some(busy) = &session.busy {
        return Err(Error::msg(format!("{busy} Wait for it to finish.")));
    }
    let studio = session.studio()?;
    studio.commit_gesture();
    if studio.doc.floor.is_none() {
        return Err(Error::msg("Wait for the court workspace and logo imports to finish loading."));
    }
    let project = studio.create_project();
    project::validate(&project)?;
    Ok((project, studio.library.geometry.clone()))
}

fn request_from(project: &Value) -> Result<render::RenderRequest> {
    let resolve = |p: &str| -> PathBuf { project::absolute(p, &files::data_root()) };
    render::RenderRequest::from_project(project, &resolve)
}

#[tauri::command]
pub async fn export_court(app: tauri::State<'_, App>, window: tauri::WebviewWindow, iff: bool) -> CommandResult<Option<String>> {
    let session = app.session.clone();
    let handle = window.app_handle().clone();
    let cancel = app.cancel.clone();
    worker(move || {
        let (project, geometry) = { let mut guard = session.lock().map_err(err)?; export_snapshot(&mut guard).map_err(err)? };
        let (name, filter, extension) = if iff { ("Court.iff", "NBA 2K27 court", "iff") } else { ("Court.png", "PNG texture", "png") };
        let Some(path) = rfd::FileDialog::new().set_parent(&window).set_file_name(name).add_filter(filter, &[extension]).save_file() else { return Ok(None) };
        let request = request_from(&project).map_err(err)?;
        session.lock().map_err(err)?.busy = Some(if iff { "An IFF export is running.".to_string() } else { "A PNG export is running.".to_string() });
        cancel.store(false, std::sync::atomic::Ordering::Release);
        let report = |text: &str| {
            let _ = handle.emit("status", text);
        };
        let cancelled = || cancel.load(std::sync::atomic::Ordering::Acquire);
        let progress = court_core::export::Progress { report: &report, cancelled: &cancelled };
        let started = std::time::Instant::now();
        let result = if iff { court_core::export::export_iff(&geometry, &request, &path, None, &progress) } else { court_core::export::export_png(&geometry, &request, &path, &progress) };
        session.lock().map_err(err)?.busy = None;
        result.map_err(err)?;
        Ok(Some(format!("Export saved: {} ({:.1} s)", path.display(), started.elapsed().as_secs_f64())))
    })
    .await
}

#[tauri::command]
pub async fn prepare_export_base(app: tauri::State<'_, App>) -> CommandResult<String> {
    let cancel = app.cancel.clone();
    worker(move || {
        let base = court_core::iff::prepare_base(None, &|| cancel.load(std::sync::atomic::Ordering::Acquire)).map_err(err)?;
        let texture = base.textures.first().map(|t| format!("{} × {} {}", t.width, t.height, t.format)).unwrap_or_default();
        Ok(format!("NBA 2K27 export base ready · {texture}"))
    })
    .await
}

/// One-way flattened handoff to 2K Canvas (`court.png` + `court.court.json` in a unique exchange folder).
#[tauri::command]
pub async fn send_to_canvas(app: tauri::State<'_, App>, window: tauri::WebviewWindow) -> CommandResult<Option<String>> {
    let session = app.session.clone();
    worker(move || {
        let (project_value, geometry) = { let mut guard = session.lock().map_err(err)?; export_snapshot(&mut guard).map_err(err)? };
        let executable = find_canvas().or_else(|| rfd::FileDialog::new().set_parent(&window).set_title("Locate 2K Canvas").add_filter("2K Canvas", &["exe"]).pick_file());
        let Some(executable) = executable else { return Ok(None) };
        let directory = files::data_root().join("exchange").join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(&directory).map_err(err)?;
        let texture = directory.join("court.png");
        let request = request_from(&project_value).map_err(err)?;
        court_core::export::export_png(&geometry, &request, &texture, &court_core::export::Progress { report: &|_| {}, cancelled: &|| false }).map_err(err)?;
        project::write(&directory.join("court.court.json"), &project_value, false, &[], None).map_err(err)?;
        std::process::Command::new(&executable).arg("--open").arg(&texture).current_dir(executable.parent().unwrap_or(Path::new("."))).spawn().map_err(err)?;
        Ok(Some("Court texture sent to 2K Canvas. The editable court project remains in Court Creator.".into()))
    })
    .await
}

fn find_canvas() -> Option<PathBuf> {
    let home = std::env::var_os("USERPROFILE").map(PathBuf::from)?;
    let local = std::env::var_os("LOCALAPPDATA").map(PathBuf::from)?;
    [home.join("Desktop").join("2K Canvas Rust Preview.exe"), local.join("Programs").join("2K Canvas").join("app").join("TextureStudio.exe")].into_iter().find(|p| p.is_file())
}

#[tauri::command]
pub fn cancel_operation(app: tauri::State<'_, App>) {
    app.cancel.store(true, std::sync::atomic::Ordering::Release);
}

// --- Import older court (conversion to the game-UV layout) ---

#[tauri::command]
pub async fn import_open(app: tauri::State<'_, App>, window: tauri::WebviewWindow, path: Option<String>, texture: Option<String>) -> CommandResult<Option<court_core::iff::Inspection>> {
    let session = app.session.clone();
    worker(move || {
        let path = match path {
            Some(path) => PathBuf::from(path),
            None => match rfd::FileDialog::new().set_parent(&window).add_filter("Court IFF", &["iff"]).pick_file() {
                Some(path) => path,
                None => return Ok(None),
            },
        };
        let inspection = court_core::import::inspect(&path, texture.as_deref()).map_err(err)?;
        session.lock().map_err(err)?.import = Some(inspection.clone());
        Ok(Some(inspection))
    })
    .await
}

fn import_request(session: &mut Session, bounds: [i64; 4]) -> Result<(court_core::import::ImportRequest, Arc<court_core::geometry::Geometry>)> {
    let inspection = session.import.clone().ok_or_else(|| Error::msg("Choose a source court IFF first."))?;
    let studio = session.studio()?;
    let floor = studio.doc.floor.as_ref().ok_or_else(|| Error::msg("Choose a hardwood first."))?;
    Ok((
        court_core::import::ImportRequest {
            source_path: PathBuf::from(&inspection.path),
            texture_name: inspection.selected.clone(),
            bounds,
            background_path: floor.floor.path.clone(),
            background_revision: floor.revision.clone(),
            source_revision: inspection.source_revision.clone(),
        },
        studio.library.geometry.clone(),
    ))
}

#[tauri::command]
pub async fn import_preview(app: tauri::State<'_, App>, bounds: [i64; 4]) -> CommandResult<tauri::ipc::Response> {
    let session = app.session.clone();
    worker(move || {
        let (request, geometry) = { let mut guard = session.lock().map_err(err)?; import_request(&mut guard, bounds).map_err(err)? };
        let image = court_core::import::preview(&request, &geometry).map_err(err)?;
        Ok(pixels_response(&image))
    })
    .await
}

#[tauri::command]
pub async fn import_export(app: tauri::State<'_, App>, window: tauri::WebviewWindow, bounds: [i64; 4], iff: bool) -> CommandResult<Option<String>> {
    let session = app.session.clone();
    let cancel = app.cancel.clone();
    worker(move || {
        let (request, geometry) = { let mut guard = session.lock().map_err(err)?; import_request(&mut guard, bounds).map_err(err)? };
        let (name, filter, extension) = if iff { ("Converted Court.iff", "NBA 2K27 court", "iff") } else { ("Converted Court.png", "PNG texture", "png") };
        let Some(path) = rfd::FileDialog::new().set_parent(&window).set_file_name(name).add_filter(filter, &[extension]).save_file() else { return Ok(None) };
        cancel.store(false, std::sync::atomic::Ordering::Release);
        session.lock().map_err(err)?.busy = Some("A converted export is running.".into());
        let result = if iff {
            court_core::import::export_iff(&request, &geometry, &path, None, &|| cancel.load(std::sync::atomic::Ordering::Acquire))
        } else {
            court_core::import::export_png(&request, &geometry, &path)
        };
        session.lock().map_err(err)?.busy = None;
        result.map_err(err)?;
        Ok(Some(format!("Converted court saved: {}", path.display())))
    })
    .await
}
