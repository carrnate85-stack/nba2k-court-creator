#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
//! NBA 2K Court Creator (Rust/Tauri preview). Uses the installed WebView2 runtime; no .NET,
//! Python, Node or browser runtime is bundled or started.
mod artwork;
mod artwork_text;
mod commands;
mod smoke;
mod startup;
mod state;

use state::{Cache, Preferences, Session, Writer};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use tauri::{Emitter, Manager};

pub struct App {
    pub session: Arc<Mutex<Session>>,
    pub recovery: Arc<Writer>,
    pub preferences: Arc<Writer>,
    pub cancel: Arc<AtomicBool>,
    pub closing: Arc<AtomicBool>,
    pub preparing: AtomicBool,
    pub startup: Arc<Mutex<startup::StartupState>>,
    pub smoke: Option<PathBuf>,
    pub artwork: Arc<artwork::Host>,
}

impl App {
    pub fn save_preferences(&self) {
        if self.smoke.is_some() {
            return;
        }
        if let Ok(session) = self.session.lock() {
            self.preferences.queue(session.prefs.to_document());
        }
    }
}

/// Existing installations' asset roots (validated libraries are referenced read-only).
pub fn legacy_roots() -> Vec<PathBuf> {
    court_core::legacy::roots().into_iter().map(|root| root.join("assets")).collect()
}

pub async fn worker<T: Send + 'static>(job: impl FnOnce() -> Result<T, String> + Send + 'static) -> Result<T, String> {
    tauri::async_runtime::spawn_blocking(job).await.map_err(|e| e.to_string())?
}

#[tauri::command]
fn startup_state(app: tauri::State<'_, App>) -> serde_json::Value {
    let state = app.startup.lock().unwrap().clone();
    let data = app.session.lock().ok().and_then(|mut s| startup::studio_data(&mut s));
    serde_json::json!({"state": state, "data": data})
}

#[tauri::command]
async fn choose_game_folder(handle: tauri::AppHandle, window: tauri::WebviewWindow) -> Result<bool, String> {
    let folder = worker(move || Ok(rfd::FileDialog::new().set_parent(&window).set_title("Choose your installed NBA 2K27 folder (manifest and mod.exe)").pick_folder())).await?;
    let Some(folder) = folder else { return Ok(false) };
    court_core::game::find_root(Some(&folder)).map_err(|e| e.to_string())?;
    startup::start(handle, Some(folder));
    Ok(true)
}

#[tauri::command]
fn retry_startup(handle: tauri::AppHandle) {
    startup::start(handle, None);
}

/// Final flush before closing: recovery and preferences, unless `force` skips a failed flush.
#[tauri::command]
async fn app_exit(handle: tauri::AppHandle, app: tauri::State<'_, App>, force: bool) -> Result<(), String> {
    app.closing.store(true, Ordering::Release);
    app.cancel.store(true, Ordering::Release);
    let session = app.session.clone();
    let recovery = app.recovery.clone();
    let preferences = app.preferences.clone();
    let smoke = app.smoke.is_some();
    let flushed = worker(move || {
        let (project, prefs) = {
            let mut session = session.lock().map_err(|e| e.to_string())?;
            let prefs = session.prefs.to_document();
            let project = session.studio.as_mut().map(|s| {
                s.commit_gesture();
                s.create_project()
            });
            (project, prefs)
        };
        if smoke {
            return Ok(());
        }
        let mut errors = Vec::new();
        if let Some(project) = project {
            let revision = recovery.queue(project);
            if let Err(e) = recovery.flush(revision, std::time::Duration::from_secs(20)) {
                errors.push(format!("Recovery: {e}"));
            }
        }
        let revision = preferences.queue(prefs);
        if let Err(e) = preferences.flush(revision, std::time::Duration::from_secs(10)) {
            errors.push(format!("Preferences: {e}"));
        }
        if errors.is_empty() { Ok(()) } else { Err(errors.join("\n")) }
    })
    .await;
    if let Err(error) = flushed {
        if !force {
            app.closing.store(false, Ordering::Release);
            return Err(error);
        }
    }
    handle.exit(0);
    Ok(())
}

#[cfg(windows)]
fn webview2_available() -> bool {
    use webview2_com::Microsoft::Web::WebView2::Win32::GetAvailableCoreWebView2BrowserVersionString;
    let mut version = windows::core::PWSTR::null();
    let ok = unsafe { GetAvailableCoreWebView2BrowserVersionString(windows::core::PCWSTR::null(), &mut version) }.is_ok() && !version.is_null();
    if !version.is_null() {
        unsafe { windows::Win32::System::Com::CoTaskMemFree(Some(version.0 as *const _)) };
    }
    ok
}

#[cfg(windows)]
fn missing_webview2() {
    use windows::Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW};
    use windows::core::HSTRING;
    let text = "NBA 2K Court Creator needs the Microsoft Edge WebView2 Runtime, which is included with current Windows 10 and 11.\n\nInstall the Evergreen WebView2 Runtime from Microsoft (https://developer.microsoft.com/microsoft-edge/webview2/) and start Court Creator again.";
    unsafe { MessageBoxW(None, &HSTRING::from(text), &HSTRING::from("2K Court Creator"), MB_OK | MB_ICONERROR) };
}

fn main() {
    let args: Vec<_> = std::env::args_os().collect();
    let smoke = args.iter().position(|a| a == "--smoke-output").and_then(|i| args.get(i + 1)).map(PathBuf::from);
    let hidden = smoke.is_some();
    let dark = args.iter().any(|a| a == "--smoke-dark");
    let compact = args.iter().any(|a| a == "--smoke-compact");
    #[cfg(windows)]
    if !webview2_available() {
        missing_webview2();
        std::process::exit(2);
    }
    let (prefs, prefs_warning) = if hidden { (Preferences::default(), None) } else { Preferences::load() };
    let session = Session {
        studio: None,
        cache: Cache::default(),
        prefs,
        geometry_regions: Default::default(),
        importers: Default::default(),
        next_importer: 0,
        library_name: String::new(),
        library_complete: false,
        busy: None,
        import: None,
    };
    let app = App {
        session: Arc::new(Mutex::new(session)),
        recovery: Writer::start("court-recovery", |value| if std::env::var_os("COURT_CREATOR_NO_RECOVERY").is_some() { Ok(()) } else { state::write_recovery(value) }),
        preferences: Writer::start("court-preferences", state::write_preferences),
        cancel: Arc::new(AtomicBool::new(false)),
        closing: Arc::new(AtomicBool::new(false)),
        preparing: AtomicBool::new(false),
        startup: Arc::new(Mutex::new(startup::StartupState { phase: "loading".into(), message: prefs_warning.unwrap_or_else(|| "Starting studio...".into()), ..Default::default() })),
        smoke: smoke.clone(),
        artwork: Arc::default(),
    };
    tauri::Builder::default()
        .manage(app)
        .invoke_handler(tauri::generate_handler![
            startup_state, choose_game_folder, retry_startup, app_exit,
            commands::set_layer, commands::fill_region, commands::regions, commands::set_two_point_enabled, commands::select_floor,
            commands::hardwood_settings, commands::hardwood_preview, commands::logo_pixels, commands::floor_thumbnail,
            commands::logo_transform, commands::logo_update, commands::logo_opacity, commands::logo_delete, commands::logo_duplicate, commands::logo_reorder,
            commands::rename_project, commands::undo, commands::document,
            commands::importer_open, commands::importer_pixels, commands::importer_action, commands::importer_place, commands::importer_close,
            commands::court_text_preview, commands::text_defaults, commands::text_apply,
            commands::set_preferences, commands::add_custom_floor,
            commands::new_project, commands::open_project, commands::save_project,
            commands::export_court, commands::prepare_export_base, commands::send_to_canvas, commands::cancel_operation, commands::import_open, commands::import_preview, commands::import_export,
            artwork::artwork_open, artwork::artwork_apply, artwork::artwork_close,
            artwork::edit, artwork::document_info, artwork::history, artwork::cancel_selection, artwork::render, artwork::render_view,
            artwork::render_preview, artwork::render_operation_preview, artwork::selection_pixels, artwork::sample_pixel,
            artwork::layer_pixels, artwork::mask_pixels, artwork::snap_to_edges,
            artwork_text::text_fonts, artwork_text::text_begin, artwork_text::text_preview, artwork_text::text_draft_pixels,
            artwork_text::text_finish, artwork_text::text_cancel, artwork_text::text_add_layer, artwork_text::text_update_layer,
            artwork_text::text_settings, artwork_text::text_render,
            smoke::smoke_report, smoke::smoke_capture, smoke::smoke_open_fixture, smoke::smoke_export, smoke::smoke_save_reopen,
        ])
        .setup(move |app| {
            let mut url = String::from("index.html");
            if hidden {
                url = format!("index.html?smoke=1{}", if dark { "&dark=1" } else { "" });
            }
            tauri::WebviewWindowBuilder::new(app, "main", tauri::WebviewUrl::App(url.into()))
                .title("2K Court Creator")
                .inner_size(if compact { 1000.0 } else { 1440.0 }, if compact { 680.0 } else { 900.0 })
                .min_inner_size(1000.0, 680.0)
                .decorations(false)
                .visible(!hidden)
                .center()
                .build()?;
            if let Some(path) = smoke.clone() {
                smoke::watchdog(app.handle().clone(), path);
            }
            startup::start(app.handle().clone(), None);
            Ok(())
        })
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                let app = window.app_handle();
                if !app.state::<App>().closing.load(Ordering::Acquire) {
                    api.prevent_close();
                    let _ = window.emit("close-request", ());
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("Unable to start 2K Court Creator");
}
