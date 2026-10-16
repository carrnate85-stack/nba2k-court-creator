//! Startup: show the window immediately, then load geometry and the floor catalog off the UI
//! thread. Missing floors are prepared from the installed game in a cancellable background job
//! that publishes the first usable floor early and refreshes the catalog without touching edits.
use crate::App;
use crate::commands::{doc_view, floor_views, geometry_view};
use crate::state::{AssetLoader, Session};
use court_core::catalog::{self, Library as Catalog};
use court_core::document::{Library, Studio};
use court_core::error::{Error, Result};
use court_core::{files, geometry_build, prepare, project};
use serde::Serialize;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::Ordering;
use tauri::{Emitter, Manager};

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StartupState {
    /// "loading", "preparing", "needs-game", "ready" or "failed".
    pub phase: String,
    pub message: String,
    pub preparing: bool,
    pub retry: bool,
}

fn set_state(app: &tauri::AppHandle, update: impl FnOnce(&mut StartupState)) {
    let state = app.state::<App>();
    let snapshot = {
        let mut current = state.startup.lock().unwrap();
        update(&mut current);
        current.clone()
    };
    let _ = app.emit("startup", snapshot);
}

/// Team palettes shipped with the source (`data/team_palettes.json`), sanitized like `load_team_palettes`.
pub fn palettes() -> Value {
    static SOURCE: &str = include_str!("../../../data/team_palettes.json");
    let data: Value = serde_json::from_str(SOURCE).unwrap_or(Value::Null);
    let entries = data.as_array().cloned().or_else(|| data.get("palettes").and_then(Value::as_array).cloned()).unwrap_or_default();
    let mut out = Vec::new();
    for entry in entries {
        let Some(map) = entry.as_object() else { continue };
        let mut palette = serde_json::Map::new();
        for key in ["league", "team", "source", "paletteNote"] {
            if let Some(Value::String(text)) = map.get(key) {
                palette.insert(key.into(), Value::String(text.clone()));
            }
        }
        let colors: Vec<Value> = map
            .get("colors")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter_map(|c| {
                let hex = c.get("hex")?.as_str()?;
                court_core::render::normalize_hex(hex).filter(|_| hex.trim().trim_start_matches('#').len() == 6)?;
                let mut color = serde_json::Map::new();
                color.insert("hex".into(), Value::String(hex.to_string()));
                if let Some(Value::String(name)) = c.get("name") {
                    color.insert("name".into(), Value::String(name.clone()));
                }
                Some(Value::Object(color))
            })
            .collect();
        palette.insert("colors".into(), Value::Array(colors));
        out.push(Value::Object(palette));
    }
    Value::Array(out)
}

/// Data sent to the UI once the court workspace is usable.
pub fn studio_data(session: &mut Session) -> Option<Value> {
    let prefs = session.prefs.clone();
    let name = session.library_name.clone();
    let complete = session.library_complete;
    let studio = session.studio.as_mut()?;
    Some(json!({
        "geometry": geometry_view(&studio.library.geometry),
        "floors": floor_views(studio),
        "defaultFloor": studio.library.default_floor,
        "palettes": palettes(),
        "prefs": prefs,
        "doc": doc_view(studio),
        "libraryName": name,
        "libraryComplete": complete,
    }))
}

fn usable_library() -> Option<Catalog> {
    let own = files::data_root().join("library");
    if let Some(library) = catalog::load_library(&own).filter(|l| l.complete) {
        return Some(library);
    }
    // Existing installations' validated libraries are referenced read-only.
    crate::legacy_roots().into_iter().filter_map(|root| catalog::load_library(&root)).find(|l| l.complete)
}

fn images_for(library: &Catalog, bounds: [i64; 4]) -> Vec<Value> {
    let mut images = catalog::load_custom(&files::data_root());
    images.extend(library.images.iter().cloned());
    for image in &mut images {
        image["bbox"] = json!(bounds);
    }
    images
}

fn allow_assets(app: &tauri::AppHandle, roots: &[PathBuf]) {
    for root in roots {
        if root.exists() {
            let _ = app.asset_protocol_scope().allow_directory(root, true);
        }
    }
}

/// Builds the studio from a geometry and catalog, restoring the last recovery when available.
fn open_studio(app: &tauri::AppHandle, geometry: Arc<court_core::geometry::Geometry>, library: &Catalog) -> Result<String> {
    let state = app.state::<App>();
    let images = images_for(library, geometry.hardwood_bounds);
    let mut roots = vec![library.assets_root.clone()];
    roots.extend(crate::legacy_roots());
    allow_assets(app, &[library.assets_root.clone(), files::data_root().join("custom_floors")]);
    let mut studio = Studio::new(Library::new(geometry, &images, files::data_root(), roots));
    let mut session = state.session.lock().map_err(|_| Error::msg("Studio state unavailable."))?;
    studio.recent = session.prefs.recent.clone();
    let Session { cache, .. } = &mut *session;
    let mut status = "Studio ready.".to_string();
    let recovered = project::read_recovery(&project::recovery_path());
    let mut restored = false;
    if let Some(recovery) = recovered {
        match studio.restore(&recovery, false, &mut AssetLoader { cache }) {
            Ok(report) => {
                restored = true;
                status = if report.missing.is_empty() && report.updated.is_empty() { "Project restored in the native studio.".into() } else { report.status() };
            }
            Err(error) => status = format!("Recovery could not be loaded: {error}"),
        }
    }
    if !restored {
        let floor = studio.library.default_floor.as_ref().and_then(|id| studio.library.floor(id)).cloned().ok_or_else(|| Error::msg("No hardwood textures were found in the local library."))?;
        studio.initialize_floor(floor, &mut AssetLoader { cache })?;
    }
    session.prefs.recent = studio.recent.clone();
    session.library_name = library.name.clone();
    session.library_complete = library.complete;
    session.studio = Some(studio);
    session.geometry_regions.clear();
    Ok(status)
}

/// Refreshes only the catalog (current court, artwork and undo history are retained).
fn refresh_catalog(app: &tauri::AppHandle, library: &Catalog) {
    let state = app.state::<App>();
    let mut session = state.session.lock().unwrap();
    allow_assets(app, &[library.assets_root.clone()]);
    if let Some(studio) = session.studio.as_mut() {
        let images = images_for(library, studio.library.geometry.hardwood_bounds);
        let fresh = Library::new(studio.library.geometry.clone(), &images, files::data_root(), studio.library.library_roots.clone());
        // Keep custom/project floors the current document references.
        let mut floors = fresh.floors;
        for floor in &studio.library.floors {
            if !floors.iter().any(|f| f.id == floor.id) {
                floors.push(floor.clone());
            }
        }
        studio.library.floors = floors;
        studio.library.loaded_visibility = fresh.loaded_visibility;
        if studio.library.default_floor.is_none() {
            studio.library.default_floor = fresh.default_floor;
        }
    }
    session.library_name = library.name.clone();
    session.library_complete = library.complete;
    let floors = session.studio.as_ref().map(floor_views);
    drop(session);
    let _ = app.emit("catalog", json!({"floors": floors, "libraryName": library.name, "complete": library.complete}));
}

pub fn start(app: tauri::AppHandle, game_root: Option<PathBuf>) {
    let state = app.state::<App>();
    if state.preparing.swap(true, Ordering::AcqRel) {
        return;
    }
    std::thread::Builder::new()
        .name("court-startup".into())
        .spawn(move || {
            let result = run(&app, game_root.as_deref());
            app.state::<App>().preparing.store(false, Ordering::Release);
            if let Err(error) = result {
                if error.is_cancelled() {
                    return;
                }
                let needs_game = error.0.contains("NBA 2K27 was not found");
                set_state(&app, |s| {
                    s.phase = if s.phase == "ready" { "ready".into() } else if needs_game { "needs-game".into() } else { "failed".into() };
                    s.message = if needs_game { "Choose your NBA 2K27 folder (the folder containing mod.exe and manifest) to prepare the court floors.".into() } else { format!("Floor preparation: {error}") };
                    s.preparing = false;
                    s.retry = true;
                });
            }
        })
        .expect("startup thread");
}

fn run(app: &tauri::AppHandle, game_root: Option<&Path>) -> Result<()> {
    let cancel = app.state::<App>().closing.clone();
    let cancelled = move || cancel.load(Ordering::Acquire);
    let ready = app.state::<App>().session.lock().map(|s| s.studio.is_some()).unwrap_or(false);
    set_state(app, |s| {
        if !ready {
            s.phase = "loading".into();
        }
        s.message = "Loading Game UV court library...".into();
        s.retry = false;
    });
    let started = std::time::Instant::now();
    let geometry = Arc::new(geometry_build::prepare(game_root, &cancelled)?);
    if let Some(library) = usable_library() {
        if !ready {
            let status = open_studio(app, geometry, &library)?;
            set_state(app, |s| {
                s.phase = "ready".into();
                s.message = format!("{status} ({} ms)", started.elapsed().as_millis());
                s.preparing = false;
            });
            let _ = app.emit("studio-ready", ());
        } else {
            refresh_catalog(app, &library);
        }
        return Ok(());
    }
    let game = court_core::game::find_root(game_root)?;
    let _ = court_core::game::remember_root(&game);
    let own = files::data_root().join("library");
    set_state(app, |s| {
        s.preparing = true;
        s.message = "Preparing court floors…".into();
        if s.phase != "ready" {
            s.phase = "preparing".into();
        }
    });
    let handle = app.clone();
    let opened = std::sync::Mutex::new(ready);
    let geometry_for_first = geometry.clone();
    let report = |event: prepare::Event| match event {
        prepare::Event::FirstFloorReady => {
            let mut opened = opened.lock().unwrap();
            if !*opened {
                if let Some(library) = catalog::load_library(&own) {
                    match open_studio(&handle, geometry_for_first.clone(), &library) {
                        Ok(status) => {
                            *opened = true;
                            set_state(&handle, |s| {
                                s.phase = "ready".into();
                                s.message = status;
                            });
                            let _ = handle.emit("studio-ready", ());
                        }
                        Err(error) => set_state(&handle, |s| s.message = format!("Floor preparation: {error}")),
                    }
                }
            }
            set_state(&handle, |s| s.message = "Preparing remaining court floors…".into());
        }
        prepare::Event::Progress { done, total } => set_state(&handle, |s| s.message = format!("Preparing floors: {done}/{total}")),
        prepare::Event::Status(text) => set_state(&handle, |s| s.message = text),
    };
    let count = prepare::prepare(&own, &game, &report, &cancelled)?;
    let library = catalog::load_library(&own).ok_or_else(|| Error::msg("The new floor catalog failed its load check."))?;
    if !*opened.lock().unwrap() {
        let status = open_studio(app, geometry, &library)?;
        set_state(app, |s| {
            s.phase = "ready".into();
            s.message = status;
        });
        let _ = app.emit("studio-ready", ());
    } else {
        refresh_catalog(app, &library);
    }
    set_state(app, |s| {
        s.preparing = false;
        s.message = format!("{count} court floors ready.");
    });
    Ok(())
}
