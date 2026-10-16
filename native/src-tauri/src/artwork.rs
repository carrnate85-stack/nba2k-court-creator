//! Edit Artwork: one host-owned Canvas editing session for the selected logo or the main
//! hardwood (port of `StudioWindow.ArtworkEditing` / `StudioArtworkFiles`). The session edits a
//! private `canvas_engine::Document`; the court changes only when Apply succeeds, as one undo step.
//!
//! Canvas's own viewport, selection and text components call the editor commands by their Canvas
//! names (`edit`, `layer_pixels`, `render_view`, `text_begin` …), so this module serves those names
//! for the single artwork document. The command bodies follow `2K Canvas/source/native/src-tauri`.
use crate::state::AssetLoader;
use crate::{App, worker};
use canvas_engine::{Document, DocumentInfo, Point};
use court_core::files;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

pub fn error(err: impl std::fmt::Display) -> String {
    err.to_string()
}

#[derive(Clone, Debug, PartialEq)]
enum Target {
    Logo(String),
    Floor,
}

struct Session {
    id: u64,
    document: Arc<Mutex<Document>>,
    target: Target,
    name: String,
    /// File revision of the court asset when the session opened; Apply refuses a changed source.
    source: PathBuf,
    source_revision: Option<String>,
    /// Studio history version at open; any other court change invalidates the session.
    version: u64,
    width: u32,
    height: u32,
    dds_metadata: Value,
    alpha_is_data: bool,
    channel_labels: [String; 4],
    applying: Arc<AtomicBool>,
    cancelled: Arc<AtomicBool>,
}

/// The one artwork editor this window may host.
#[derive(Default)]
pub struct Host {
    session: Mutex<Option<Session>>,
    next_id: AtomicU64,
    selection_epoch: Arc<AtomicU64>,
    pub text: Arc<crate::artwork_text::Slot>,
}

impl Host {
    pub fn document(&self, id: u64) -> Result<Arc<Mutex<Document>>, String> {
        let session = self.session.lock().map_err(error)?;
        session.as_ref().filter(|s| s.id == id).map(|s| s.document.clone()).ok_or_else(|| "The artwork editor is closed.".into())
    }
    fn close(&self) -> Option<Session> {
        let session = self.session.lock().ok()?.take();
        if let Some(session) = &session {
            session.cancelled.store(true, Ordering::Release);
            self.text.forget(session.id);
        }
        session
    }
    pub fn is_open(&self) -> bool {
        self.session.lock().map(|s| s.is_some()).unwrap_or(true)
    }
}

const BUSY: &str = "Edit Artwork is open.";

#[derive(Serialize)]
pub struct Tab {
    id: u64,
    document: DocumentInfo,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Opened {
    tab: Tab,
    name: String,
    target: &'static str,
    dark: bool,
}

/// Starts a session for the selected logo (`logo`) or the main hardwood. The editable `.2kstudio`
/// archive is used when the asset has one (its revision must match), else the court's image.
#[tauri::command]
pub async fn artwork_open(app: tauri::State<'_, App>, logo: Option<String>) -> Result<Opened, String> {
    let session = app.session.clone();
    let host = app.artwork.clone();
    worker(move || {
        let (target, name, path, revision, artwork, version, dark) = {
            let mut guard = session.lock().map_err(error)?;
            if let Some(busy) = &guard.busy {
                return Err(format!("{busy} Finish the current court operation before editing artwork."));
            }
            if host.is_open() {
                return Err("Only one artwork editor can be open.".into());
            }
            let dark = guard.prefs.dark;
            let studio = guard.studio().map_err(error)?;
            studio.commit_gesture();
            let version = studio.version;
            match logo {
                Some(id) => {
                    let index = studio.logo_index(&id).map_err(error)?;
                    let logo = &studio.doc.logos[index];
                    if logo.revision.is_none() {
                        return Err("This logo's artwork is missing. Reload it before editing.".into());
                    }
                    (Target::Logo(id), logo.name.clone(), logo.path.clone(), logo.revision.clone(), logo.artwork.clone(), version, dark)
                }
                None => {
                    let floor = studio.doc.floor.as_ref().ok_or("Select a court texture first.")?;
                    let artwork = floor.floor.source.as_object().cloned().unwrap_or_default();
                    // Hardwood revisions are decoded-image revisions in the cache; compare the file itself.
                    let revision = files::file_revision(&floor.floor.path).ok();
                    (Target::Floor, floor.floor.name.clone(), floor.floor.path.clone(), revision, artwork, version, dark)
                }
            }
        };
        if let Target::Logo(_) = target {
            if files::file_revision(&path).ok() != revision {
                return Err("Artwork changed since the court preview. Reload it before editing.".into());
            }
        }
        let document = match artwork.get("artworkProjectPath").and_then(Value::as_str).filter(|p| !p.is_empty()) {
            Some(project) => {
                court_core::document::check_editable_artwork(&artwork).map_err(error)?;
                Document::open_project(Path::new(project)).map_err(error)?
            }
            None => Document::open(&path).map_err(error)?,
        };
        let info = document.info();
        let mut slot = host.session.lock().map_err(error)?;
        if slot.is_some() {
            return Err("Only one artwork editor can be open.".into());
        }
        {
            let mut guard = session.lock().map_err(error)?;
            if guard.busy.is_some() || guard.studio.as_ref().map(|s| s.version) != Some(version) {
                return Err("The court changed while the artwork was loading. Try again.".into());
            }
            guard.busy = Some(BUSY.into());
        }
        let id = host.next_id.fetch_add(1, Ordering::Relaxed) + 1;
        *slot = Some(Session {
            id,
            document: Arc::new(Mutex::new(document)),
            target: target.clone(),
            name: name.clone(),
            source: path,
            source_revision: revision,
            version,
            width: info.width,
            height: info.height,
            dds_metadata: serde_json::to_value(&info.dds_metadata).map_err(error)?,
            alpha_is_data: info.alpha_is_data,
            channel_labels: info.channel_labels.clone(),
            applying: Arc::default(),
            cancelled: Arc::default(),
        });
        Ok(Opened { tab: Tab { id, document: info }, name, target: if target == Target::Floor { "floor" } else { "logo" }, dark })
    })
    .await
}

/// Cancel, Escape and window close: discards the private draft. Closing during Apply cancels it.
#[tauri::command]
pub async fn artwork_close(app: tauri::State<'_, App>) -> Result<(), String> {
    let applying = app.artwork.session.lock().map_err(error)?.as_ref().map(|s| s.applying.load(Ordering::Acquire)).unwrap_or(false);
    if applying {
        if let Some(session) = app.artwork.session.lock().map_err(error)?.as_ref() {
            session.cancelled.store(true, Ordering::Release);
        }
        return Ok(());
    }
    if app.artwork.close().is_some() {
        let mut session = app.session.lock().map_err(error)?;
        if session.busy.as_deref() == Some(BUSY) {
            session.busy = None;
        }
    }
    Ok(())
}

/// Files of one Apply under `artwork/<unique>`; removed again unless the court took them.
struct Prepared {
    directory: PathBuf,
    written: Vec<(PathBuf, String)>,
    committed: bool,
}

impl Prepared {
    fn new() -> Result<Self, String> {
        let root = files::data_root().join("artwork");
        std::fs::create_dir_all(&root).map_err(error)?;
        let directory = root.join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir(&directory).map_err(error)?;
        Ok(Self { directory, written: Vec::new(), committed: false })
    }
    fn write(&mut self, name: &str, bytes: &[u8]) -> Result<(PathBuf, String), String> {
        let path = self.directory.join(name);
        files::write_atomic(&path, bytes, &[]).map_err(error)?;
        let revision = files::sha256_hex(bytes);
        self.written.push((path.clone(), revision.clone()));
        Ok((path, revision))
    }
    fn track(&mut self, path: &Path) -> Result<String, String> {
        let revision = files::file_revision(path).map_err(error)?;
        self.written.push((path.to_path_buf(), revision.clone()));
        Ok(revision)
    }
    /// Every prepared file must still hold the bytes written for it.
    fn validate(&self) -> Result<(), String> {
        for (path, revision) in &self.written {
            if files::file_revision(path).ok().as_deref() != Some(revision.as_str()) {
                return Err("A prepared artwork file changed before it was applied.".into());
            }
        }
        Ok(())
    }
}

impl Drop for Prepared {
    fn drop(&mut self) {
        if self.committed {
            return;
        }
        // Removes only the unchanged files this Apply wrote, then the directory if it is empty.
        for (path, revision) in &self.written {
            if files::file_revision(path).ok().as_deref() == Some(revision.as_str()) {
                let _ = std::fs::remove_file(path);
            }
        }
        let _ = std::fs::remove_dir(&self.directory);
    }
}

/// Apply: validates the draft, writes the flattened PNG, the editable archive and (for DDS
/// sources) a DDS with the original profile, then updates the court asset as one undo step.
#[tauri::command]
pub async fn artwork_apply(app: tauri::State<'_, App>, id: u64) -> Result<crate::commands::Reply, String> {
    let (document, target, name, source, source_revision, version, width, height, metadata, alpha, labels, applying, cancelled) = {
        let session = app.artwork.session.lock().map_err(error)?;
        let s = session.as_ref().filter(|s| s.id == id).ok_or("The artwork editor is closed.")?;
        if s.applying.swap(true, Ordering::AcqRel) {
            return Err("The artwork is already being applied.".into());
        }
        (s.document.clone(), s.target.clone(), s.name.clone(), s.source.clone(), s.source_revision.clone(), s.version, s.width, s.height, s.dds_metadata.clone(), s.alpha_is_data, s.channel_labels.clone(), s.applying.clone(), s.cancelled.clone())
    };
    let session = app.session.clone();
    let recovery = app.recovery.clone();
    let host = app.artwork.clone();
    let result = worker(move || {
        let check = || if cancelled.load(Ordering::Acquire) { Err::<(), String>("The artwork editor was closed; the court was kept unchanged.".into()) } else { Ok(()) };
        let (pixels, project, profile) = {
            let doc = document.lock().map_err(error)?;
            let info = doc.info();
            if info.width != width || info.height != height {
                return Err("Keep the original artwork dimensions before applying to the court.".into());
            }
            if serde_json::to_value(&info.dds_metadata).map_err(error)? != metadata || info.alpha_is_data != alpha || info.channel_labels != labels {
                return Err("Keep the source DDS metadata, alpha interpretation and channel labels when applying.".into());
            }
            let mut archive = std::io::Cursor::new(Vec::new());
            doc.write_project(&mut archive).map_err(error)?;
            (doc.render_final(), archive.into_inner(), doc.dds_profile().map_err(error)?)
        };
        check()?;
        let mut prepared = Prepared::new()?;
        let image = court_core::raster::Rgba { width, height, data: pixels };
        let (image_path, _) = prepared.write("artwork.png", &court_core::raster::encode_png(&image, false).map_err(error)?)?;
        let (project_path, project_revision) = prepared.write("artwork.2kstudio", &project)?;
        let reopened = Document::open_project(&project_path).map_err(error)?;
        if reopened.info().width != width || reopened.info().height != height {
            return Err("The editable artwork dimensions do not match its pixels.".into());
        }
        let mut data = Map::new();
        data.insert("artworkProjectPath".into(), json!(project_path.to_string_lossy()));
        data.insert("artworkProjectRevision".into(), json!(project_revision));
        data.insert("artworkAlphaMode".into(), json!(if alpha { "GameData" } else { "Transparency" }));
        if let Some(profile) = profile {
            check()?;
            let dds = prepared.directory.join("artwork.dds");
            let flag = cancelled.clone();
            reopened.export_dds(&dds, &profile, Default::default(), &move || flag.load(Ordering::Acquire)).map_err(error)?;
            let revision = prepared.track(&dds)?;
            data.insert("artworkDdsPath".into(), json!(dds.to_string_lossy()));
            data.insert("artworkDdsRevision".into(), json!(revision));
        }
        check()?;
        let mut guard = session.lock().map_err(error)?;
        let crate::state::Session { studio, cache, .. } = &mut *guard;
        let studio = studio.as_mut().ok_or("The court workspace is still loading.")?;
        if studio.version != version || files::file_revision(&source).ok() != source_revision {
            return Err("The source artwork changed; the court was kept unchanged.".into());
        }
        let mut assets = AssetLoader { cache };
        use court_core::document::Assets;
        match &target {
            Target::Logo(logo) => {
                let (owned, revision, w, h) = assets.logo(&image_path, alpha).map_err(error)?;
                prepared.validate()?;
                check()?;
                studio.apply_logo_artwork(logo, owned, revision, (w, h), data).map_err(error)?;
            }
            Target::Floor => {
                let revision = assets.floor(&image_path, alpha).map_err(error)?;
                prepared.validate()?;
                check()?;
                studio.apply_floor_artwork(image_path.clone(), revision, data).map_err(error)?;
            }
        }
        prepared.committed = true;
        recovery.queue(studio.create_project());
        let selected = if let Target::Logo(logo) = &target { Some(logo.clone()) } else { None };
        let reply = crate::commands::reply(studio, Some(format!("Artwork applied: {name}")), selected);
        if guard.busy.as_deref() == Some(BUSY) {
            guard.busy = None;
        }
        drop(guard);
        host.close();
        Ok(reply)
    })
    .await;
    applying.store(false, Ordering::Release);
    // Closing during Apply ends the session once Apply has stopped.
    if result.is_err() && app.artwork.session.lock().map_err(error)?.as_ref().is_some_and(|s| s.cancelled.load(Ordering::Acquire)) {
        artwork_close(app).await?;
    }
    result
}

// --- Canvas editor commands (names and payloads used by the shared Canvas components) ---

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Edit {
    Select { layer: u64 },
    AddLayer,
    DeleteLayer,
    DuplicateLayer,
    RenameLayer { name: String },
    Locked { locked: bool },
    Move { x: f64, y: f64 },
    Transform { geometry: canvas_engine::artwork_geometry::Geometry },
    TransformFrames { steps: Vec<canvas_engine::artwork_transform::FrameTransform> },
    Reorder { direction: i32 },
    Visible { layer: u64, visible: bool },
    Opacity { opacity: f64 },
    PaintStroke { points: Vec<canvas_engine::quick_mask::MaskPoint>, settings: canvas_engine::artwork_paint::StrokeSettings },
    PaintMask { points: Vec<canvas_engine::quick_mask::MaskPoint>, settings: canvas_engine::mask_paint::MaskBrushSettings },
    SelectionShape { shape: canvas_engine::selection::Shape, mode: canvas_engine::selection::Mode, x1: f64, y1: f64, x2: f64, y2: f64 },
    SelectionPolygon { points: Vec<canvas_engine::advanced_selection::Vertex>, mode: canvas_engine::selection::Mode },
    SelectionColor { x: i64, y: i64, tolerance: u8, contiguous: bool, mode: canvas_engine::selection::Mode },
    CreateMask { replace: bool },
    RemoveMask,
    MoveMask { dx: f64, dy: f64 },
    SelectAll,
    Deselect,
    InvertSelection,
    Rectangle { x1: f64, y1: f64, x2: f64, y2: f64, fill: Option<[u8; 4]>, stroke: Option<[u8; 4]>, width: f64, opacity: f64 },
    AddAdjustment { key: String, value: Value },
    SetAdjustment { layer: u64, key: String, value: Value },
    BlendMode { mode: canvas_engine::compositing::BlendMode },
    MergeDown,
    FlattenVisible,
    FillSelection { color: [u8; 4], opacity: f32, clear: bool },
    Bucket { point: Point, color: [u8; 4], opacity: f32, tolerance: u8, contiguous: bool },
    Undo,
    Redo,
}

fn history_label(operation: &Edit) -> &'static str {
    match operation {
        Edit::AddLayer => "New layer",
        Edit::DeleteLayer => "Delete layer",
        Edit::DuplicateLayer => "Duplicate layer",
        Edit::RenameLayer { .. } => "Rename layer",
        Edit::Locked { .. } => "Lock layer",
        Edit::Move { .. } => "Move",
        Edit::Transform { .. } | Edit::TransformFrames { .. } => "Transform",
        Edit::Reorder { .. } => "Reorder layers",
        Edit::Visible { .. } => "Visibility",
        Edit::Opacity { .. } => "Opacity",
        Edit::PaintStroke { .. } => "Brush stroke",
        Edit::PaintMask { .. } => "Mask stroke",
        Edit::SelectionShape { .. } | Edit::SelectionPolygon { .. } | Edit::SelectionColor { .. } | Edit::SelectAll | Edit::Deselect | Edit::InvertSelection => "Selection",
        Edit::FillSelection { clear: true, .. } => "Clear",
        Edit::FillSelection { .. } => "Fill",
        Edit::Bucket { .. } => "Paint bucket",
        Edit::CreateMask { .. } => "Add mask",
        Edit::RemoveMask => "Remove mask",
        Edit::MoveMask { .. } => "Move mask",
        Edit::BlendMode { .. } => "Blend mode",
        Edit::MergeDown => "Merge down",
        Edit::FlattenVisible => "Flatten",
        Edit::AddAdjustment { .. } => "Add adjustment",
        Edit::SetAdjustment { .. } => "Edit adjustment",
        Edit::Rectangle { .. } => "Rectangle",
        _ => "Edit",
    }
}

fn apply_edit<C: Fn() -> bool>(doc: &mut Document, operation: Edit, cancelled: &C) -> Result<(), canvas_engine::Error> {
    match operation {
        Edit::Select { layer } => doc.select(layer),
        Edit::AddLayer => doc.add_layer(),
        Edit::DeleteLayer => doc.delete_layer(),
        Edit::DuplicateLayer => doc.duplicate_layer(),
        Edit::RenameLayer { name } => doc.rename_layer(name),
        Edit::Locked { locked } => doc.set_locked(locked),
        Edit::Move { x, y } => doc.move_layer(x, y),
        Edit::Transform { geometry } => doc.transform_layer(geometry),
        Edit::TransformFrames { steps } => doc.transform_in_frames(&steps),
        Edit::Reorder { direction } => doc.reorder(direction),
        Edit::Visible { layer, visible } => doc.set_visible(layer, visible),
        Edit::Opacity { opacity } => doc.set_opacity(opacity),
        Edit::PaintStroke { points, mut settings } => {
            settings.alpha_is_data = doc.alpha_is_data();
            doc.paint_stroke(&points, settings, cancelled)
        }
        Edit::PaintMask { points, settings } => doc.paint_layer_mask(&points, settings, cancelled),
        Edit::SelectionShape { shape, mode, x1, y1, x2, y2 } => doc.selection_shape(shape, mode, x1, y1, x2, y2),
        Edit::SelectionPolygon { points, mode } => doc.selection_polygon(&points, mode, cancelled),
        Edit::SelectionColor { x, y, tolerance, contiguous, mode } => doc.selection_color(x, y, tolerance, contiguous, mode, cancelled),
        Edit::CreateMask { replace } => doc.create_layer_mask(replace, cancelled),
        Edit::RemoveMask => doc.remove_layer_mask(),
        Edit::MoveMask { dx, dy } => doc.move_layer_mask(dx, dy),
        Edit::SelectAll => doc.select_all(),
        Edit::Deselect => doc.deselect(),
        Edit::InvertSelection => doc.invert_selection(),
        Edit::Rectangle { x1, y1, x2, y2, fill, stroke, width, opacity } => doc.draw_rectangle(x1, y1, x2, y2, fill, stroke, width, opacity),
        Edit::AddAdjustment { key, value } => doc.add_adjustment_layer(&key, value),
        Edit::SetAdjustment { layer, key, value } => doc.set_adjustment(layer, &key, value),
        Edit::BlendMode { mode } => doc.set_blend_mode(mode),
        Edit::MergeDown => doc.merge_down(),
        Edit::FlattenVisible => doc.flatten_visible(),
        Edit::FillSelection { color, opacity, clear } => doc.fill_selection(color, opacity, clear),
        Edit::Bucket { point, color, opacity, tolerance, contiguous } => doc.bucket(point, color, opacity, tolerance, contiguous),
        Edit::Undo => {
            doc.undo();
            Ok(())
        }
        Edit::Redo => {
            doc.redo();
            Ok(())
        }
    }
}

fn response(bytes: Vec<u8>) -> tauri::ipc::Response {
    tauri::ipc::Response::new(bytes)
}

#[tauri::command]
pub async fn edit(app: tauri::State<'_, App>, id: u64, operation: Edit) -> Result<DocumentInfo, String> {
    let doc = app.artwork.document(id)?;
    let token = app.artwork.selection_epoch.clone();
    let expected = token.load(Ordering::Acquire);
    worker(move || {
        let cancelled = || token.load(Ordering::Acquire) != expected;
        let mut doc = doc.lock().map_err(error)?;
        doc.set_history_label(history_label(&operation));
        apply_edit(&mut doc, operation, &cancelled).map_err(error)?;
        Ok(doc.info())
    })
    .await
}

#[tauri::command]
pub async fn document_info(app: tauri::State<'_, App>, id: u64) -> Result<DocumentInfo, String> {
    let doc = app.artwork.document(id)?;
    worker(move || Ok(doc.lock().map_err(error)?.info())).await
}

#[tauri::command]
pub async fn history(app: tauri::State<'_, App>, id: u64) -> Result<canvas_engine::History, String> {
    let doc = app.artwork.document(id)?;
    worker(move || Ok(doc.lock().map_err(error)?.history())).await
}

#[tauri::command]
pub fn cancel_selection(app: tauri::State<'_, App>, id: u64) -> Result<(), String> {
    app.artwork.document(id)?;
    app.artwork.selection_epoch.fetch_add(1, Ordering::AcqRel);
    Ok(())
}

#[tauri::command]
pub async fn render(app: tauri::State<'_, App>, id: u64) -> Result<tauri::ipc::Response, String> {
    let doc = app.artwork.document(id)?;
    worker(move || Ok(response(doc.lock().map_err(error)?.render_final()))).await
}

#[tauri::command]
pub async fn render_view(app: tauri::State<'_, App>, id: u64, channel: u8) -> Result<tauri::ipc::Response, String> {
    if channel > 9 {
        return Err(error(canvas_engine::Error::Parameters));
    }
    let doc = app.artwork.document(id)?;
    worker(move || {
        let mut pixels = doc.lock().map_err(error)?.render_final();
        canvas_engine::channel_view::apply(&mut pixels, channel).map_err(error)?;
        Ok(response(pixels))
    })
    .await
}

#[tauri::command]
pub async fn render_preview(app: tauri::State<'_, App>, id: u64, channel: u8, preview: canvas_engine::adjustments::Preview) -> Result<tauri::ipc::Response, String> {
    if channel > 9 {
        return Err(error(canvas_engine::Error::Parameters));
    }
    let doc = app.artwork.document(id)?;
    worker(move || {
        let mut pixels = doc.lock().map_err(error)?.render_preview(&preview).map_err(error)?;
        canvas_engine::channel_view::apply(&mut pixels, channel).map_err(error)?;
        Ok(response(pixels))
    })
    .await
}

/// Renders the document as it would look after `operation`, without changing it.
#[tauri::command]
pub async fn render_operation_preview(app: tauri::State<'_, App>, id: u64, channel: u8, operation: Edit) -> Result<tauri::ipc::Response, String> {
    if channel > 9 {
        return Err(error(canvas_engine::Error::Parameters));
    }
    let doc = app.artwork.document(id)?;
    worker(move || {
        let mut scratch = doc.lock().map_err(error)?.scratch();
        apply_edit(&mut scratch, operation, &|| false).map_err(error)?;
        let mut pixels = scratch.render_final();
        canvas_engine::channel_view::apply(&mut pixels, channel).map_err(error)?;
        Ok(response(pixels))
    })
    .await
}

#[tauri::command]
pub async fn selection_pixels(app: tauri::State<'_, App>, id: u64, mask_id: u64) -> Result<tauri::ipc::Response, String> {
    let doc = app.artwork.document(id)?;
    worker(move || Ok(response(doc.lock().map_err(error)?.selection_info_pixels(mask_id).map_err(error)?))).await
}

#[tauri::command]
pub async fn sample_pixel(app: tauri::State<'_, App>, id: u64, x: u32, y: u32) -> Result<[u8; 4], String> {
    let doc = app.artwork.document(id)?;
    worker(move || doc.lock().map_err(error)?.sample_pixel(x, y).map_err(error)).await
}

#[tauri::command]
pub async fn layer_pixels(app: tauri::State<'_, App>, id: u64, layer: u64) -> Result<tauri::ipc::Response, String> {
    let doc = app.artwork.document(id)?;
    worker(move || Ok(response(doc.lock().map_err(error)?.layer_pixels(layer).map_err(error)?))).await
}

#[tauri::command]
pub async fn mask_pixels(app: tauri::State<'_, App>, id: u64, layer: u64) -> Result<tauri::ipc::Response, String> {
    let doc = app.artwork.document(id)?;
    worker(move || Ok(response(doc.lock().map_err(error)?.mask_pixels(layer).map_err(error)?.as_ref().clone()))).await
}

#[tauri::command]
pub async fn snap_to_edges(app: tauri::State<'_, App>, id: u64, points: Vec<canvas_engine::quick_mask::MaskPoint>, radius: f64) -> Result<Vec<canvas_engine::quick_mask::MaskPoint>, String> {
    let doc = app.artwork.document(id)?;
    worker(move || doc.lock().map_err(error)?.snap_to_edges(&points, radius).map_err(error)).await
}
