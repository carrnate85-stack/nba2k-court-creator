//! Inline editable-text drafts for the Edit Artwork session, adapted from 2K Canvas
//! `native/src-tauri/src/text_edit.rs`. Rendering happens on the shared DirectWrite worker; the
//! artwork document's layers, revision and history change only in `text_finish`.
use crate::artwork::error;
use crate::{App, worker};
use canvas_engine::{text::Settings, text_session::TextSession, DocumentInfo, LayerInfo};
use canvas_text::{preview::PreviewWorker, EditingLayout};
use serde::Serialize;
use std::sync::{Arc, Mutex};

/// Display id for the draft layer, outside the engine's layer ids; its mask uses the negation.
pub const DRAFT_ID: u64 = 1 << 31;

pub struct Draft {
    document: u64,
    session: TextSession,
    layout: Option<EditingLayout>,
    version: u64,
}
#[derive(Default)]
pub struct Slot {
    draft: Mutex<Option<Draft>>,
    renderer: Mutex<Option<Arc<PreviewWorker>>>,
    /// One preview waiter at a time, so a result is never taken by an older request.
    gate: Mutex<()>,
}
impl Slot {
    fn renderer(&self) -> Result<Arc<PreviewWorker>, String> {
        let mut renderer = self.renderer.lock().map_err(error)?;
        if renderer.is_none() { *renderer = Some(Arc::new(PreviewWorker::new().map_err(error)?)); }
        Ok(renderer.clone().unwrap())
    }
    /// Discards the draft for a closing document.
    pub fn forget(&self, document: u64) {
        if let Ok(mut draft) = self.draft.lock() { if draft.as_ref().is_some_and(|d| d.document == document) { *draft = None; } }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DraftInfo {
    /// The layer being edited, or None for new text (inserted above the selected layer).
    original: Option<u64>,
    layer: LayerInfo,
    settings: Settings,
    layout: Option<EditingLayout>,
    superseded: bool,
}
fn info(draft: &Draft, superseded: bool) -> DraftInfo {
    let mut layer = draft.session.draft().info();
    layer.id = DRAFT_ID; layer.pixel_version = draft.version;
    if let Some(mask) = &mut layer.mask { mask.version = draft.version; }
    DraftInfo { original: draft.session.layer(), layer, settings: draft.session.settings().clone(), layout: draft.layout.clone(), superseded }
}

#[tauri::command]
pub fn text_fonts() -> Result<Vec<String>, String> {
    static FONTS: std::sync::OnceLock<Vec<String>> = std::sync::OnceLock::new();
    if let Some(fonts) = FONTS.get() { return Ok(fonts.clone()); }
    let fonts = canvas_text::font_families().map_err(error)?;
    Ok(FONTS.get_or_init(|| fonts).clone())
}

/// Starts a draft; `settings` seeds new text (the UI supplies the original defaults).
#[tauri::command]
pub async fn text_begin(app: tauri::State<'_, App>, id: u64, layer: Option<u64>, x: f64, y: f64, settings: Settings) -> Result<DraftInfo, String> {
    let doc = app.artwork.document(id)?;
    let slot = app.artwork.text.clone();
    worker(move || {
        let doc = doc.lock().map_err(error)?;
        let settings = match layer { Some(layer) => doc.text_settings(layer).ok_or("Choose an unlocked editable text layer.")?, None => settings };
        let session = doc.begin_text(layer, settings, x, y).map_err(error)?;
        let draft = Draft { document: id, session, layout: None, version: 1 };
        let result = info(&draft, false);
        *slot.draft.lock().map_err(error)? = Some(draft);
        Ok(result)
    }).await
}

/// Renders `settings` and installs them in the draft. A newer request supersedes this one.
#[tauri::command]
pub async fn text_preview(app: tauri::State<'_, App>, id: u64, settings: Settings) -> Result<DraftInfo, String> {
    let doc = app.artwork.document(id)?; let slot = app.artwork.text.clone();
    let renderer = slot.renderer()?;
    worker(move || {
        let _gate = slot.gate.lock().map_err(error)?;
        let generation = renderer.submit(settings).map_err(error)?;
        let preview = loop {
            match renderer.recv_timeout(std::time::Duration::from_secs(30)) {
                Some(preview) if preview.generation == generation => break preview,
                Some(_) => continue,
                None => {
                    let draft = slot.draft.lock().map_err(error)?;
                    let draft = draft.as_ref().filter(|d| d.document == id).ok_or("The text draft was closed.")?;
                    return Ok(info(draft, true));
                }
            }
        };
        let rendered = preview.image.map_err(error)?;
        let doc = doc.lock().map_err(error)?;
        let mut guard = slot.draft.lock().map_err(error)?;
        let draft = guard.as_mut().filter(|d| d.document == id).ok_or("The text draft was closed.")?;
        draft.session.preview(&doc, preview.settings, rendered.width, rendered.height, Arc::new(rendered.pixels.clone())).map_err(error)?;
        draft.layout = rendered.editing.clone(); draft.version += 1;
        Ok(info(draft, false))
    }).await
}

/// Renders once on the shared worker, for commands that commit directly.
fn render_now(slot: &Slot, settings: Settings) -> Result<Arc<canvas_text::Rendered>, String> {
    let renderer = slot.renderer()?;
    let _gate = slot.gate.lock().map_err(error)?;
    let generation = renderer.submit(settings).map_err(error)?;
    loop {
        match renderer.recv_timeout(std::time::Duration::from_secs(60)) {
            Some(preview) if preview.generation == generation => return preview.image.map_err(error),
            Some(_) => continue,
            None => return Err("Text rendering timed out.".into()),
        }
    }
}

/// "Add text layer…": a centered layer from the text dialog, as one undo step.
#[tauri::command]
pub async fn text_add_layer(app: tauri::State<'_, App>, id: u64, settings: Settings) -> Result<DocumentInfo, String> {
    let doc = app.artwork.document(id)?; let slot = app.artwork.text.clone();
    worker(move || {
        let rendered = render_now(&slot, settings.clone())?;
        let mut doc = doc.lock().map_err(error)?;
        doc.add_text_layer(&settings, rendered.width, rendered.height, Arc::new(rendered.pixels.clone())).map_err(error)?;
        Ok(doc.info())
    }).await
}

/// The text dialog's Update for an existing layer: one undo step, or no change.
#[tauri::command]
pub async fn text_update_layer(app: tauri::State<'_, App>, id: u64, layer: u64, settings: Settings) -> Result<DocumentInfo, String> {
    let doc = app.artwork.document(id)?; let slot = app.artwork.text.clone();
    worker(move || {
        let rendered = render_now(&slot, settings.clone())?;
        let mut doc = doc.lock().map_err(error)?;
        let mut session = doc.begin_text(Some(layer), settings.clone(), 0., 0.).map_err(error)?;
        session.preview(&doc, settings, rendered.width, rendered.height, Arc::new(rendered.pixels.clone())).map_err(error)?;
        session.finish(&mut doc).map_err(error)?;
        Ok(doc.info())
    }).await
}

#[tauri::command]
pub async fn text_draft_pixels(app: tauri::State<'_, App>, id: u64, mask: Option<bool>) -> Result<tauri::ipc::Response, String> {
    let slot = app.artwork.text.clone();
    worker(move || {
        let draft = slot.draft.lock().map_err(error)?;
        let layer = draft.as_ref().filter(|d| d.document == id).ok_or("No text draft is active.")?.session.draft();
        let pixels = if mask == Some(true) { layer.mask.asset.as_ref().ok_or("The draft has no mask.")?.pixels.as_ref().clone() } else { layer.pixels.as_ref().clone() };
        Ok(tauri::ipc::Response::new(pixels))
    }).await
}

/// Commits the draft as one undo step and closes it.
#[tauri::command]
pub async fn text_finish(app: tauri::State<'_, App>, id: u64) -> Result<DocumentInfo, String> {
    let doc = app.artwork.document(id)?; let slot = app.artwork.text.clone();
    worker(move || {
        let mut doc = doc.lock().map_err(error)?;
        let mut guard = slot.draft.lock().map_err(error)?;
        if !guard.as_ref().is_some_and(|d| d.document == id) { return Err("No text draft is active.".into()); }
        // A failed Apply keeps the draft so the user can correct it or cancel.
        guard.as_ref().unwrap().session.finish(&mut doc).map_err(error)?;
        *guard = None;
        Ok(doc.info())
    }).await
}

/// Discards the draft; the document is untouched.
#[tauri::command]
pub fn text_cancel(app: tauri::State<'_, App>, id: u64) -> Result<(), String> {
    app.artwork.text.forget(id);
    if let Ok(renderer) = app.artwork.text.renderer.lock() { if let Some(renderer) = renderer.as_ref() { renderer.cancel(); } }
    Ok(())
}

/// Settings of a text layer, for the edit dialog.
#[tauri::command]
pub async fn text_settings(app: tauri::State<'_, App>, id: u64, layer: u64) -> Result<Settings, String> {
    let doc = app.artwork.document(id)?;
    worker(move || doc.lock().map_err(error)?.text_settings(layer).ok_or_else(|| "Select an editable text layer.".into())).await
}

/// Dialog preview: little-endian width and height, then straight RGBA.
#[tauri::command]
pub async fn text_render(app: tauri::State<'_, App>, settings: Settings) -> Result<tauri::ipc::Response, String> {
    let slot = app.artwork.text.clone();
    worker(move || {
        let rendered = render_now(&slot, settings)?;
        let mut bytes = Vec::with_capacity(8 + rendered.pixels.len());
        bytes.extend_from_slice(&rendered.width.to_le_bytes()); bytes.extend_from_slice(&rendered.height.to_le_bytes()); bytes.extend_from_slice(&rendered.pixels);
        Ok(tauri::ipc::Response::new(bytes))
    }).await
}
